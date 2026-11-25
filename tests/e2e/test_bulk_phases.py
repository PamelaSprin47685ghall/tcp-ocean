#!/usr/bin/env python3
"""Local tests for the bulk phase record (BULK_PHASE_TRACE). No netns, no external host.

What is pinned here, and why each assertion can fail:

  * the generation loop keeps its original control law. With the record on, the deadline is
    still `now` (the caller's wall clock) and the record merely *adds* a monotonic reading
    beside it. Swapping the control clock for a monotonic one would have measured a different
    loop than production runs, so a test drives the two clocks apart and requires the loop to
    follow the wall clock;
  * both clocks are recorded separately (`wall_*`, `mono_*`) and their span difference
    (`clock_skew_ns`) is reported. A step is recorded, never corrected or attributed;
  * a send that finishes strictly after the deadline is counted exactly
    (`straddle_sends`), with no percentage tolerance; a send finishing exactly on it is not;
  * with the record off, the loop issues exactly the clock reads it issued before;
  * the client distinguishes a real EOF from its caller stopping it;
  * a failed or short append is visible on stderr and never costs bytes;
  * clock-domain identity needs boot id AND time-namespace id.
"""
import importlib.util
import io
import os
import socket
import tempfile
import threading
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('echo', HERE / 'echo.py')
echo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(echo)


class WallClock:
    """The clock that decides: seconds, as `time.time` reports them."""

    def __init__(self, start=0.0):
        self.t = start


class MonoClock:
    """The clock that cannot step: nanoseconds, as `time.monotonic_ns` reports them."""

    def __init__(self, start_ns=0):
        self.ns = start_ns


def two_clocks(wall_start=0.0, mono_start_ns=0):
    w = WallClock(wall_start)
    m = MonoClock(mono_start_ns)

    def now():
        return w.t

    def ns():
        return m.ns

    return w, m, now, ns


class GeneratorControlLaw(unittest.TestCase):
    """The loop stops on the wall clock; the monotonic reading is only recorded."""

    def test_the_deadline_follows_the_wall_clock_not_the_monotonic_one(self):
        # The behavioural regression this pins: an earlier revision made the deadline
        # monotonic whenever diagnostics were on, so a run with BULK_PHASE_TRACE set executed
        # a different loop from the same run without it. Here monotonic is frozen and the wall
        # clock advances past the deadline: the loop must still stop at the wall deadline.
        w, m, now, ns = two_clocks()
        ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=ns)
        sends = []

        def send(block):
            sends.append(len(block))
            w.t += 0.4          # wall advances; monotonic stays at 0

        echo.run_generator(send, 1, b"x" * 64, now=now, phases=ph)
        self.assertEqual(len(sends), 3, "stops on the wall deadline (0.0, 0.4, 0.8)")
        self.assertEqual(ph.fields["req_s"], 1)
        self.assertEqual(ph.fields["mono_span_ns"], 0, "the frozen clock did not move")
        self.assertEqual(ph.fields["wall_span_ns"], 1_200_000_000)
        self.assertEqual(ph.fields["clock_skew_ns"], 1_200_000_000)

    def test_a_wall_clock_step_is_recorded_and_not_corrected(self):
        # A step shows up as a difference between the two spans. The record must not "fix" it:
        # these two readings cannot say whether the wall clock jumped, the monotonic clock was
        # in another namespace, or the process was suspended — so nothing is inferred.
        w, m, now, ns = two_clocks()
        ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=ns)

        def send(block):
            w.t += 0.25
            m.ns += 250_000_000        # both advance together
            if w.t >= 1.0:
                w.t = 1.0

        echo.run_generator(send, 1, b"y" * 8, now=now, phases=ph)
        self.assertEqual(ph.fields["clock_skew_ns"], 0,
                         "clocks that advance together have no skew")

        w2, m2, now2, ns2 = two_clocks()
        ph2 = echo.BulkPhases("server", boot="b", tens="t", ns_now=ns2)

        def send_jump(block):
            w2.t += 10.0               # wall jumps 10 s ...
            m2.ns += 250_000_000       # ... monotonic advances 0.25 s

        echo.run_generator(send_jump, 1, b"z" * 8, now=now2, phases=ph2)
        self.assertEqual(ph2.fields["clock_skew_ns"], 10_000_000_000 - 250_000_000,
                         "the step is reported as a skew, not hidden")
        self.assertEqual(ph2.fields["mono_span_ns"], 250_000_000)
        self.assertEqual(ph2.fields["wall_span_ns"], 10_000_000_000)

    def test_a_send_finishing_after_the_deadline_is_counted_exactly(self):
        # 9 ms past a 1 s deadline: well under any percentage gate, and still a real straddle.
        w, m, now, ns = two_clocks()
        ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=ns)
        sends = []

        def send(block):
            sends.append(len(block))
            w.t = 1.009
            m.ns = 1_009_000_000

        sent = echo.run_generator(send, 1, b"x" * 64, now=now, phases=ph)
        self.assertEqual(sent, 64)
        self.assertEqual(len(sends), 1)
        self.assertEqual(ph.fields["straddle_sends"], 1)
        self.assertEqual(ph.fields["excess_ns"], 9_000_000)
        self.assertEqual(ph.fields["clock_skew_ns"], 0)

    def test_a_send_finishing_exactly_on_the_deadline_is_not_a_straddle(self):
        # Negative control for the count: finishing on the deadline is finishing on time.
        w, m, now, ns = two_clocks()
        ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=ns)

        def send(block):
            w.t += 0.5
            m.ns += 500_000_000

        echo.run_generator(send, 1, b"y" * 8, now=now, phases=ph)
        self.assertEqual(ph.fields["straddle_sends"], 0)
        self.assertEqual(ph.fields["excess_ns"], 0)
        self.assertEqual(ph.fields["wall_span_ns"], 1_000_000_000)

    def test_until_close_has_no_deadline_so_nothing_can_straddle(self):
        # `seconds=0` asks for "until the peer closes". There is no deadline to cross.
        w, m, now, ns = two_clocks()
        ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=ns)

        class Closed(Exception):
            pass

        def send(block):
            w.t += 5.0
            m.ns += 5_000_000_000
            if w.t >= 15.0:
                raise Closed()          # the only way this loop ends when seconds=0

        with self.assertRaises(Closed):
            echo.run_generator(send, 0, b"z" * 8, now=now, phases=ph)
        self.assertEqual(ph.fields["req_s"], 0)
        # Two complete sends; the third raises inside `send`, so its bytes were never counted.
        self.assertEqual(ph.fields["bytes"], 16)
        self.assertEqual(ph.fields["straddle_sends"], 0)
        self.assertEqual(ph.fields["excess_ns"], 0, "no deadline means no excess")

    def test_the_span_is_recorded_even_when_the_peer_vanishes(self):
        # A dead peer raises out of `send`. The record must still close, or a failed transfer
        # would look like a zero-byte one with no window.
        class Gone(Exception):
            pass

        w, m, now, ns = two_clocks()
        ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=ns)

        def send(block):
            w.t = 0.007
            m.ns = 7_000_000
            raise Gone()

        with self.assertRaises(Gone):
            echo.run_generator(send, 5, b"w" * 16, now=now, phases=ph)
        self.assertEqual(ph.fields["bytes"], 0)
        self.assertEqual(ph.fields["wall_end_ns"], 7_000_000)
        self.assertEqual(ph.fields["straddle_sends"], 0)

    def test_with_the_record_off_the_loop_reads_the_clock_as_before(self):
        # Production path: the original loop read the clock once per iteration; a diagnostic
        # must not add a read to a run that is not recording. `send` advances monotonically, so
        # this test cannot loop — a fixed clock value here was an infinite loop.
        clock = WallClock()
        calls = []

        def now():
            calls.append(clock.t)
            return clock.t

        def send(block):
            clock.t += 0.25

        sent = echo.run_generator(send, 1, b"o" * 8, now=now, phases=None)
        self.assertEqual(sent, 32, "four sends inside 1 s, as the original loop")
        # one read for the deadline, then one per iteration: 0.00, 0.25, 0.50, 0.75 (sends)
        # and 1.00 (the read that stops the loop).
        self.assertEqual(len(calls), 6, f"one deadline read plus one per iteration: {calls}")
        self.assertIsNone(echo._bulk_phases("server"))


class PhaseRecords(unittest.TestCase):
    def test_a_failed_append_is_visible_and_costs_no_bytes(self):
        # A switch that silently drops records is worse than none: "no overrun recorded"
        # would then be indistinguishable from "the trace never worked".
        old = os.environ.get("BULK_PHASE_TRACE")
        os.environ["BULK_PHASE_TRACE"] = "/nonexistent-dir/never/x.log"
        err = io.StringIO()
        try:
            with redirect_stderr(err):
                echo._append_phase(echo.BulkPhases("client", boot="b", tens="t",
                                                   ns_now=lambda: 0))
        finally:
            if old is None:
                os.environ.pop("BULK_PHASE_TRACE", None)
            else:
                os.environ["BULK_PHASE_TRACE"] = old
        self.assertIn("BULK_PHASE_TRACE", err.getvalue())
        self.assertIn("write failed", err.getvalue())

    def test_a_short_write_is_reported_and_left_detectable(self):
        # `os.write` may accept fewer bytes than it was given. Dropping the rest would truncate
        # a record silently, and a truncated line parses as a *different* line; looping makes it
        # whole, and one append syscall per record is what keeps it un-interleaved. When the
        # kernel needs two calls, the record can interleave, so that fact goes to stderr.
        real_write = os.write
        calls = {"n": 0}

        def short_write(fd, data):
            calls["n"] += 1
            if calls["n"] == 1:                 # first call accepts half
                return real_write(fd, data[: max(1, len(data) // 2)])
            return real_write(fd, data)

        with tempfile.TemporaryDirectory() as td:
            path = os.path.join(td, "phases.log")
            old = os.environ.get("BULK_PHASE_TRACE")
            os.environ["BULK_PHASE_TRACE"] = path
            err = io.StringIO()
            try:
                ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=lambda: 0)
                ph.add("bytes", 11)
                os.write = short_write
                with redirect_stderr(err):
                    echo._append_phase(ph)
            finally:
                os.write = real_write
                if old is None:
                    os.environ.pop("BULK_PHASE_TRACE", None)
                else:
                    os.environ["BULK_PHASE_TRACE"] = old
            text = Path(path).read_text()
        # The fix, not the old defect: a short write is NOT completed by a second append
        # (that would land after any record another writer added meanwhile and split this one
        # around it). The record is reported as torn, and it must then fail its own length
        # check — including the case where the fragment has no ` nbytes=` tail at all, which
        # is why the check is `record_intact` and not `int(line.rpartition(...)[2])`.
        self.assertIn("record torn", err.getvalue())
        self.assertNotIn("atomicity not guaranteed", err.getvalue())
        for line in text.splitlines():
            self.assertFalse(echo.record_intact(line),
                             f"a torn record must not satisfy its own length check: {line!r}")

    def test_a_complete_record_satisfies_its_length_check(self):
        # The negative control for the assertion above: `record_intact` must accept a record
        # that was written whole, or the torn-record check would pass for the wrong reason.
        with tempfile.TemporaryDirectory() as td:
            path = os.path.join(td, "phases.log")
            old = os.environ.get("BULK_PHASE_TRACE")
            os.environ["BULK_PHASE_TRACE"] = path
            try:
                ph = echo.BulkPhases("server", boot="b", tens="t", ns_now=lambda: 0)
                ph.add("bytes", 11)
                echo._append_phase(ph)
            finally:
                if old is None:
                    os.environ.pop("BULK_PHASE_TRACE", None)
                else:
                    os.environ["BULK_PHASE_TRACE"] = old
            lines = Path(path).read_text().splitlines()
        self.assertEqual(len(lines), 1)
        self.assertTrue(echo.record_intact(lines[0]), lines[0])

    def test_a_fragment_that_swallowed_the_next_record_is_rejected(self):
        # O_APPEND cannot interleave two whole writes, but a torn write leaves no trailing
        # newline, so the *next* record lands on the same line. That line contains a complete
        # record's bytes and its ` nbytes=` tail; `record_intact` must still reject it, because
        # the body before the tail is the fragment plus that record, not the record alone.
        complete = "role=server boot=b tens=t bytes=5 nbytes=33"
        self.assertTrue(echo.record_intact(complete), "the control: a whole record passes")
        self.assertFalse(echo.record_intact("role=client boot=b " + complete))
        self.assertFalse(echo.record_intact("role=client boot=b bytes=1"))
        self.assertFalse(echo.record_intact("role=client boot=b nbytes=99"))
        self.assertFalse(echo.record_intact("role=client boot=b nbytes=x"))

    def test_two_appends_produce_two_whole_lines(self):
        with tempfile.TemporaryDirectory() as td:
            path = os.path.join(td, "phases.log")
            old = os.environ.get("BULK_PHASE_TRACE")
            os.environ["BULK_PHASE_TRACE"] = path
            try:
                a = echo.BulkPhases("server", boot="b", tens="t", ns_now=lambda: 0)
                a.add("bytes", 7)
                b = echo.BulkPhases("client", boot="b", tens="t", ns_now=lambda: 0)
                b.add("bytes", 9)
                echo._append_phase(a)
                echo._append_phase(b)
            finally:
                if old is None:
                    os.environ.pop("BULK_PHASE_TRACE", None)
                else:
                    os.environ["BULK_PHASE_TRACE"] = old
            lines = Path(path).read_text().splitlines()
            self.assertEqual(len(lines), 2)
            self.assertEqual({l.split()[0] for l in lines}, {"role=server", "role=client"})
            # Each line ends with its own body length and satisfies it: that is what makes a
            # split or truncated record detectable instead of plausible.
            for line in lines:
                self.assertTrue(echo.record_intact(line), line)
            self.assertTrue(any("bytes=7" in l for l in lines))
            self.assertTrue(any("bytes=9" in l for l in lines))

    def test_the_switch_off_builds_no_record(self):
        old = os.environ.pop("BULK_PHASE_TRACE", None)
        try:
            self.assertIsNone(echo._bulk_phases("server"))
        finally:
            if old is not None:
                os.environ["BULK_PHASE_TRACE"] = old


class ClockDomain(unittest.TestCase):
    def test_same_boot_and_time_namespace_may_be_subtracted(self):
        a = {"boot": "b1", "timens": "time:[1]"}
        b = {"boot": "b1", "timens": "time:[1]"}
        self.assertTrue(echo.same_clock_domain(a, b))

    def test_same_boot_in_different_time_namespaces_may_not(self):
        # Boot id alone is global, but CLOCK_MONOTONIC can be offset per time namespace, so
        # equal boot ids name different scales; subtracting across them yields a wrong number
        # rather than an error.
        a = {"boot": "b1", "timens": "time:[1]"}
        b = {"boot": "b1", "timens": "time:[2]"}
        self.assertFalse(echo.same_clock_domain(a, b))

    def test_an_unknown_identity_certifies_nothing(self):
        self.assertFalse(echo.same_clock_domain({"boot": "unknown", "timens": "time:[1]"},
                                                {"boot": "unknown", "timens": "time:[1]"}))
        self.assertFalse(echo.same_clock_domain({"boot": "b1", "timens": None},
                                                {"boot": "b1", "timens": None}))


class AcrossARealTransfer(unittest.TestCase):
    """Both ends of one real local transfer: same host, so the records may be subtracted."""

    def setUp(self):
        self.port = _free_port()
        self.server_stop = threading.Event()
        self.server = threading.Thread(
            target=echo.serve, args=(self.port, self.server_stop), daemon=True)
        self.server.start()
        deadline = time.time() + 2
        while time.time() < deadline:
            try:
                with socket.create_connection(("127.0.0.1", self.port), timeout=0.1):
                    return
            except OSError:
                time.sleep(0.02)
        self.fail("echo server did not start")

    def tearDown(self):
        self.server_stop.set()
        self.server.join(2)

    def _run(self, seconds, stop_after=None):
        old = os.environ.get("BULK_PHASE_TRACE")
        td = tempfile.TemporaryDirectory()
        path = os.path.join(td.name, "phases.log")
        os.environ["BULK_PHASE_TRACE"] = path
        stop = None
        timer = None
        try:
            if stop_after is not None:
                stop = threading.Event()
                timer = threading.Timer(stop_after, stop.set)
                timer.daemon = True
                timer.start()
            else:
                timer = None
            with redirect_stdout(io.StringIO()):
                rc = echo.bulk("127.0.0.1", self.port, "down", seconds, stop=stop)
        finally:
            if timer is not None:
                timer.cancel()      # a fired-or-pending timer thread must not outlive the test
            if old is None:
                os.environ.pop("BULK_PHASE_TRACE", None)
            else:
                os.environ["BULK_PHASE_TRACE"] = old
        try:
            lines = [l for l in Path(path).read_text().splitlines() if l.strip()]
        finally:
            td.cleanup()
        recs = {}
        for line in lines:
            kv = dict(tok.split("=", 1) for tok in line.split() if "=" in tok)
            recs[kv["role"]] = kv
        return rc, recs

    def test_both_ends_record_their_own_phases_and_are_comparable(self):
        rc, recs = self._run(1)
        self.assertEqual(rc, 0)
        self.assertEqual(set(recs), {"client", "server"})
        self.assertTrue(echo.same_clock_domain(recs["client"], recs["server"]),
                        "one host, one boot, one time namespace")
        client, server = recs["client"], recs["server"]
        for key in ("req_ns", "first_ns", "eof_ns", "stopped", "bytes"):
            self.assertIn(key, client)
        for key in ("wall_start_ns", "wall_end_ns", "wall_span_ns",
                    "mono_start_ns", "mono_end_ns", "mono_span_ns", "clock_skew_ns",
                    "excess_ns", "straddle_sends", "bytes"):
            self.assertIn(key, server)
        self.assertEqual(client["stopped"], "0", "the server finished, so EOF is real")
        self.assertLessEqual(int(client["req_ns"]), int(client["first_ns"]))
        self.assertLessEqual(int(client["first_ns"]), int(client["eof_ns"]))
        self.assertLessEqual(int(server["wall_start_ns"]), int(server["wall_end_ns"]))
        # Wall and monotonic are separate readings of one span on an unstepped host.
        self.assertLess(abs(int(server["clock_skew_ns"])), 100_000_000,
                        "no step on a local transfer")
        # The gap this record exists to expose: the client's EOF cannot precede the moment the
        # generator stopped. Both sides of this comparison are monotonic readings — `eof_ns` is
        # CLOCK_MONOTONIC on the client and `mono_end_ns` is CLOCK_MONOTONIC on the server. Using
        # the server's `wall_end_ns` here would be exactly the cross-clock subtraction this
        # record forbids (and it reads as a year-2026 wall timestamp against a monotonic one).
        self.assertGreaterEqual(int(client["eof_ns"]), int(server["mono_end_ns"]))
        self.assertEqual(int(client["bytes"]), int(server["bytes"]),
                         "a clean transfer delivers exactly what the generator wrote")

    def test_a_caller_stop_is_not_recorded_as_eof(self):
        # `stop` set while the server is still generating: the client's last reading is not
        # the transfer's end. Recording it as `eof_ns` would let a later reader compute a
        # "drain" that is really just truncation.
        rc, recs = self._run(0, stop_after=0.5)
        self.assertEqual(rc, 0)
        client = recs["client"]
        self.assertEqual(client["stopped"], "1")
        self.assertIn("stop_ns", client)
        self.assertNotIn("eof_ns", client)


def _free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


if __name__ == "__main__":
    unittest.main()
