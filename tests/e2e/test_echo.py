#!/usr/bin/env python3
"""Local tests for ping / pingbulk reporting. No netns."""
import importlib.util
import io
import socket
import sys
import tempfile
import threading
import time
import unittest
from contextlib import redirect_stdout
from pathlib import Path

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('echo', HERE / 'echo.py')
echo = importlib.util.module_from_spec(spec)
spec.loader.exec_module(echo)


def _free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class EchoChecks(unittest.TestCase):
    def setUp(self):
        self.port = _free_port()
        # A stop event, so tearDown can actually close the server it started.
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

    def test_idle_ping_all_ok(self):
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = echo.ping("127.0.0.1", self.port, 20)
        line = buf.getvalue().strip()
        self.assertEqual(rc, 0)
        self.assertIn("ok=20", line)
        self.assertIn("timeout=0", line)
        self.assertIn("fail=0", line)
        self.assertIn("denom=20", line)
        self.assertIn("under_load=20", line)
        self.assertIn("p99_ms=", line)
        self.assertNotIn("p99_ms=na", line)

    def test_pingbulk_runs_until_probes_finish_and_p99_is_under_load(self):
        buf = io.StringIO()
        t0 = time.time()
        with redirect_stdout(buf):
            rc = echo.pingbulk("127.0.0.1", self.port, 30)
        dt = time.time() - t0
        out = buf.getvalue()
        self.assertEqual(rc, 0)
        # 30 probes * 20 ms spacing is ~0.6 s plus RTT; must not be capped by a short bulk.
        self.assertGreater(dt, 0.5)
        self.assertIn("ping-under-bulk", out)
        self.assertIn("ok=30", out)
        self.assertIn("denom=30", out)
        self.assertIn("under_load=30", out)
        self.assertIn("(p99 secondary, over successful in-load samples)", out)
        self.assertIn("ontime_in_load=30", out)
        self.assertIn("ontime_rate=1.000", out)
        self.assertIn("bulk down", out)

    def test_timeout_counted_in_denominator(self):
        # A probe timeout is recorded; p99 uses only successful in-load samples.
        records = [
            {"status": "ok", "inside_load": True, "rtt_ms": 10.0},
            {"status": "ok", "inside_load": True, "rtt_ms": 12.0},
            {"status": "timeout", "inside_load": True, "rtt_ms": 2000.0},
            {"status": "ok", "inside_load": False, "rtt_ms": 8.0},
            {"status": "fail", "inside_load": True, "rtt_ms": 1.0},
        ]
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = echo._report_ping("ping-under-bulk", 5, records)
        line = buf.getvalue().strip()
        self.assertEqual(rc, 1)
        self.assertIn("ok=3", line)
        self.assertIn("timeout=1", line)
        self.assertIn("fail=1", line)
        self.assertIn("denom=5", line)
        self.assertIn("under_load=2", line)
        self.assertIn("ontime_in_load=2", line)
        self.assertIn("planned=5", line)
        self.assertIn("p99_ms=12.0", line)
        self.assertNotIn("2000", line)

    def test_a_timed_out_probe_ends_the_connection_instead_of_faking_mismatch(self):
        # The probe protocol is a strict 64-byte request/response frame on one stream. When a
        # probe dies mid-frame, `recv_exact` has already consumed part of the response and
        # throws it away, so every later read is a shifted frame. PROVEN here with a server
        # that echoes EVERY request byte-for-byte and merely answers the first one after the
        # probe timeout: before the fix, the third probe was reported as `mismatch` — a
        # payload-corruption verdict for a stream whose payload was never wrong.
        # The contract now: the torn probe keeps its real status, the connection ends, and the
        # remaining planned probes are `unrun`/`desync` so the denominator stays `count`.
        delay = 0.4
        srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        srv.bind(("127.0.0.1", 0))
        srv.listen(1)
        port = srv.getsockname()[1]

        def serve():
            try:
                c, _ = srv.accept()
            except OSError:
                return
            with c:
                if not c.recv(1):
                    return
                first = True
                while True:
                    req = b""
                    while len(req) < 64:
                        d = c.recv(64 - len(req))
                        if not d:
                            return
                        req += d
                    if first:
                        time.sleep(delay)   # late, but echoed correctly
                        first = False
                    c.sendall(req)

        t = threading.Thread(target=serve, daemon=True)
        t.start()
        try:
            buf = io.StringIO()
            with redirect_stdout(buf):
                rc = echo.ping("127.0.0.1", port, 3, label="proof", probe_timeout=0.15)
            line = buf.getvalue()
        finally:
            srv.close()
        self.assertEqual(rc, 1)                       # nothing on time, so not a pass
        self.assertIn("timeout=1", line)
        self.assertIn("unrun=2", line)
        self.assertIn("fail=0", line)                 # no fabricated mismatch/reset
        self.assertIn("denom=3", line)                # planned count preserved
        self.assertIn("ontime_in_load=0", line)

    def test_mostly_failed_probes_make_latency_non_representative(self):
        # Observed on the real IPv6 path: 3 ok, 1 timeout, 11 fail, p50 409 ms. The percentile
        # is computed over the 3 survivors, so a small p50 there must never be read as "the
        # probes were fast": 12 of 15 never produced a timely answer. This pins the report so
        # the survivor latency and the probe outcome stay separable.
        records = [{"status": "ok", "inside_load": True, "rtt_ms": 409.0} for _ in range(3)]
        records += [{"status": "timeout", "inside_load": True, "rtt_ms": 2000.0}]
        records += [{"status": "fail", "inside_load": True, "rtt_ms": 1.0} for _ in range(11)]
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = echo._report_ping("ping-under-bulk", 15, records)
        line = buf.getvalue().strip()
        self.assertEqual(rc, 1)
        self.assertIn("ok=3", line)
        self.assertIn("timeout=1", line)
        self.assertIn("fail=11", line)
        self.assertIn("planned=15", line)
        # Every planned probe must appear in the denominator, and only 3 are on time.
        self.assertIn("denom=15", line)
        self.assertIn("ontime_in_load=3", line)
        self.assertIn("ontime_rate=0.200", line)
        # The survivor percentile is still reported, and it is explicitly secondary.
        self.assertIn("p50_ms=409.0", line)
        self.assertIn("(p99 secondary, over successful in-load samples)", line)

    def test_probe_log_keeps_failure_category_without_payload(self):
        # `fail` alone cannot say whether the tunnel reset the flow, the echo server closed it,
        # or the local socket broke — and those have different owners. ECHO_PROBE_LOG now
        # carries the exception class (plus errno) and "mismatch", never a message, address or
        # payload byte. Deterministic: the records are supplied, no sockets involved.
        import os
        import tempfile
        records = [
            {"i": 0, "status": "ok", "kind": "", "rtt_ms": 12.0,
             "bulk_at_send": True, "bulk_at_recv": True, "inside_load": True},
            {"i": 1, "status": "timeout", "kind": "TimeoutError", "rtt_ms": 2000.0,
             "bulk_at_send": True, "bulk_at_recv": True, "inside_load": True},
            {"i": 2, "status": "fail", "kind": "ConnectionResetError:104", "rtt_ms": 0.4,
             "bulk_at_send": True, "bulk_at_recv": True, "inside_load": True},
            {"i": 3, "status": "fail", "kind": "EOFError", "rtt_ms": 0.3,
             "bulk_at_send": True, "bulk_at_recv": True, "inside_load": True},
            {"i": 4, "status": "mismatch", "kind": "mismatch", "rtt_ms": 13.0,
             "bulk_at_send": True, "bulk_at_recv": True, "inside_load": True},
        ]
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "probe.log"
            old = os.environ.get("ECHO_PROBE_LOG")
            os.environ["ECHO_PROBE_LOG"] = str(path)
            try:
                buf = io.StringIO()
                with redirect_stdout(buf):
                    rc = echo._report_ping("ping-under-bulk", 5, records)
            finally:
                if old is None:
                    os.environ.pop("ECHO_PROBE_LOG", None)
                else:
                    os.environ["ECHO_PROBE_LOG"] = old
            text = path.read_text()
        self.assertEqual(rc, 1)          # only one on-time success of five planned
        lines = text.strip().splitlines()
        self.assertEqual(lines[0].split(), ["i", "status", "kind", "rtt_ms",
                                            "bulk_at_send", "bulk_at_recv", "inside_load"])
        by_i = {ln.split()[0]: ln.split() for ln in lines[1:]}
        self.assertEqual(by_i["1"][1:3], ["timeout", "TimeoutError"])
        self.assertEqual(by_i["2"][1:3], ["fail", "ConnectionResetError:104"])
        self.assertEqual(by_i["3"][1:3], ["fail", "EOFError"])
        self.assertEqual(by_i["4"][1:3], ["mismatch", "mismatch"])
        # The categories must be distinguishable, and the file must carry no payload or message.
        self.assertEqual(len({by_i[k][2] for k in ("1", "2", "3", "4")}), 4)
        self.assertNotIn("127.0.0.1", text)

    def test_probe_log_ok_rows_keep_seven_columns(self):
        # An `ok` probe has no failure category. Writing an empty field there produced two
        # spaces, so any whitespace-splitting consumer (awk, pandas, a shell `cut`) saw six
        # columns and silently shifted every value after `status`. The placeholder keeps the
        # arity fixed; this test pins it so the log stays machine-readable.
        import os
        import tempfile
        records = [
            {"i": 0, "status": "ok", "kind": "", "rtt_ms": 11.5,
             "bulk_at_send": True, "bulk_at_recv": True, "inside_load": True},
            {"i": 1, "status": "fail", "kind": "EOFError", "rtt_ms": 0.2,
             "bulk_at_send": True, "bulk_at_recv": True, "inside_load": True},
        ]
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "probe.log"
            old = os.environ.get("ECHO_PROBE_LOG")
            os.environ["ECHO_PROBE_LOG"] = str(path)
            try:
                buf = io.StringIO()
                with redirect_stdout(buf):
                    echo._report_ping("ping", 2, records)
            finally:
                if old is None:
                    os.environ.pop("ECHO_PROBE_LOG", None)
                else:
                    os.environ["ECHO_PROBE_LOG"] = old
            lines = path.read_text().strip().splitlines()
        rows = [ln.split() for ln in lines[1:]]
        self.assertEqual([len(r) for r in rows], [7, 7])
        self.assertEqual(rows[0][2], "-")
        self.assertEqual(rows[0][3], "11.500")
        self.assertEqual(rows[0][4:], ["1", "1", "1"])
        self.assertEqual(rows[1][2], "EOFError")

    def test_p99_na_when_no_in_load_sample(self):
        # All probes can succeed off-load and still fail: no proven-load sample.
        records = [{"status": "ok", "inside_load": False, "rtt_ms": 10.0}]
        buf = io.StringIO()
        with redirect_stdout(buf):
            rc = echo._report_ping("ping-under-bulk", 1, records)
        self.assertEqual(rc, 1)
        line = buf.getvalue()
        self.assertIn("p99_ms=na", line)
        self.assertIn("ontime_in_load=0", line)
        self.assertIn("ontime_rate=0.000", line)

    def test_bulk_watch_active_only_after_bytes(self):
        w = echo.BulkWatch()
        self.assertFalse(w.active)
        w.add_bytes(0)
        self.assertFalse(w.active)
        self.assertEqual(w.bytes, 0)
        w.add_bytes(16)
        self.assertTrue(w.active)
        self.assertEqual(w.bytes, 16)
        w.set_active(False)
        self.assertFalse(w.active)
        self.assertIsNotNone(w.t_start)
        self.assertIsNotNone(w.t_stop)

    # --- Load-window diagnostics (optional PING_TRACE) -----------------------------------
    # These pin the three states a reader can otherwise only guess: never started, still
    # running, and finished. No sleeps: every transition is driven by an explicit call, so
    # the assertions are about the invariant, not about timing luck.

    def test_bulk_watch_window_states_and_ns_clock(self):
        w = echo.BulkWatch()
        active, nbytes, start_ns, stop_ns, state, error = w.snapshot_full()
        self.assertEqual((active, nbytes, state, error), (False, 0, "none", None))
        self.assertIsNone(start_ns)
        self.assertIsNone(stop_ns)

        w.add_bytes(1000)
        _, nbytes, start_ns, stop_ns, state, _ = w.snapshot_full()
        self.assertEqual((nbytes, state), (1000, "running"))
        self.assertIsNotNone(start_ns)
        self.assertIsNone(stop_ns)

        w.set_active(False)
        _, nbytes, start_ns2, stop_ns, state, _ = w.snapshot_full()
        self.assertEqual((nbytes, state), (1000, "done"))
        self.assertEqual(start_ns2, start_ns)  # the start instant never moves
        self.assertGreaterEqual(stop_ns, start_ns)
        self.assertGreaterEqual(w.t_stop_ns - w.t_start_ns, 0)

    def test_bulk_watch_zero_bytes_never_starts_window(self):
        # A bulk that connects and dies before a byte must not claim a load window: the
        # window is what licenses saying a probe was "under load".
        w = echo.BulkWatch()
        w.add_bytes(0)
        w.set_active(False)
        _, nbytes, start_ns, stop_ns, state, _ = w.snapshot_full()
        self.assertEqual((nbytes, start_ns, stop_ns, state), (0, None, None, "none"))

    def test_bulk_watch_closed_window_stays_closed(self):
        w = echo.BulkWatch()
        w.add_bytes(64)
        w.set_active(False)
        stop_before = w.t_stop_ns
        # A late add_bytes can still count bytes, but it must not reopen the window or move
        # its end: otherwise the recorded interval would grow after the load stopped.
        w.add_bytes(64)
        _, nbytes, _, stop_after, state, _ = w.snapshot_full()
        self.assertEqual(nbytes, 128)
        self.assertEqual(stop_after, stop_before)
        self.assertEqual(state, "done")

    def test_bulk_watch_error_state_is_not_success(self):
        w = echo.BulkWatch()
        w.add_bytes(32)
        w.mark_error(ConnectionResetError())
        w.set_active(False)
        *_, state, error = w.snapshot_full()
        self.assertEqual(state, "error")
        self.assertEqual(error, "ConnectionResetError")

    def test_trace_header_and_sentinels_for_never_started(self):
        import tempfile
        w = echo.BulkWatch()
        records = [{"i": 0, "send_ns": None, "rtt_ms": 0.0, "status": "unrun",
                    "bulk_at_send": False, "bulk_at_recv": False,
                    "bytes_at_send": None, "bytes_at_recv": None}]
        path = Path(tempfile.mkdtemp()) / "ping.trace"
        echo._write_ping_trace(str(path), records, w)
        lines = path.read_text().splitlines()
        self.assertTrue(lines[0].startswith("# load "), lines[0])
        self.assertIn("state=none", lines[0])
        self.assertIn("start_ns=-1", lines[0])
        self.assertIn("stop_ns=-1", lines[0])
        # Positional fields keep their old positions; new facts are trailing key=value.
        fields = lines[1].split()
        self.assertEqual(fields[0], "0")
        self.assertEqual(fields[1], "-1")
        self.assertEqual(fields[3], "unrun")
        self.assertEqual(fields[4:6], ["send=0", "recv=0"])
        self.assertEqual(fields[6:], ["send_bytes=-1", "recv_bytes=-1"])

    def test_trace_overlap_is_decidable_from_header_alone(self):
        # The point of the header: a probe overlaps the load only if its [send, recv]
        # interval intersects [start_ns, stop_ns] AND bytes moved across it. Without the
        # header the only available span is the whole pingbulk process, which is wider.
        import tempfile
        w = echo.BulkWatch()
        w.add_bytes(4096)
        w.t_start_ns = 1_000
        w.set_active(False)
        w.t_stop_ns = 2_000
        records = [
            {"i": 0, "send_ns": 1_200, "rtt_ms": 5.0, "status": "ok",
             "bulk_at_send": True, "bulk_at_recv": True,
             "bytes_at_send": 1024, "bytes_at_recv": 3072},
            {"i": 1, "send_ns": 2_500, "rtt_ms": 5.0, "status": "ok",
             "bulk_at_send": False, "bulk_at_recv": False,
             "bytes_at_send": 4096, "bytes_at_recv": 4096},
        ]
        path = Path(tempfile.mkdtemp()) / "ping.trace"
        echo._write_ping_trace(str(path), records, w)
        head = path.read_text().splitlines()[0].split()
        kv = dict(tok.split("=", 1) for tok in head[2:])
        start, stop = int(kv["start_ns"]), int(kv["stop_ns"])
        self.assertEqual((start, stop, kv["state"]), (1_000, 2_000, "done"))
        probes = [l.split() for l in path.read_text().splitlines()[1:]]
        inside = [int(p[1]) <= stop and int(p[1]) >= start for p in probes]
        self.assertEqual(inside, [True, False])
        self.assertGreater(int(probes[0][7].split("=")[1]) - int(probes[0][6].split("=")[1]), 0)

    def test_closed_window_is_not_reopened_by_late_bytes(self):
        # `active` is what `inside_load` is read from. Bytes may still arrive while a socket
        # drains after the stop, but the window is closed: if those late bytes flipped
        # `active` back to True, a probe that ran after the load had ended would be reported
        # as having run under it — an over-count in the direction that matters.
        w = echo.BulkWatch()
        w.add_bytes(512)
        w.set_active(False)
        self.assertFalse(w.snapshot())
        w.add_bytes(512)                      # late drain after the stop
        self.assertEqual(w.bytes, 1024)
        self.assertFalse(w.snapshot(), "a closed window must stay closed")
        active, _, _, stop_ns, state, _ = w.snapshot_full()
        self.assertEqual((active, state), (False, "done"))
        self.assertIsNotNone(stop_ns)

    def test_closed_window_is_not_reopened_by_set_active_true(self):
        # The third way in, after late bytes and repeated `set_active(False)`: an explicit
        # restart. `set_active(True)` used to set `active = True` unconditionally, which left
        # a window reading `active=True` while `state=done` — and `inside_load` reads `active`,
        # so probes taken after the load would have counted as under it. The start instant
        # must not move either: the recorded interval would then begin later than it did.
        w = echo.BulkWatch()
        w.add_bytes(512)
        w.set_active(False)
        start_ns, stop_ns = w.t_start_ns, w.t_stop_ns
        w.set_active(True)
        active, nbytes, start_ns2, stop_ns2, state, _ = w.snapshot_full()
        self.assertFalse(active, "a closed window must not reopen")
        self.assertEqual(state, "done")
        self.assertEqual(nbytes, 512)
        self.assertEqual((start_ns2, stop_ns2), (start_ns, stop_ns),
                         "a refused restart must not move either boundary")

    def test_pingbulk_trace_is_written_after_the_load_window_closes(self):
        # The defect this pins: the trace used to be written inside `ping`, which returns
        # before `stop.set()` and `join`. Every trace therefore said `state=running` with
        # `stop_ns=-1`, i.e. the header's whole purpose (the load's end) was never recorded.
        # This drives the real `pingbulk` against the fixture's real echo server.
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "ping.trace"
            import os as _os
            old = _os.environ.get("PING_TRACE")
            _os.environ["PING_TRACE"] = str(path)
            try:
                with redirect_stdout(io.StringIO()):
                    rc = echo.pingbulk("127.0.0.1", self.port, 2)
            finally:
                if old is None:
                    _os.environ.pop("PING_TRACE", None)
                else:
                    _os.environ["PING_TRACE"] = old
            self.assertEqual(rc, 0, "a load that stops in time keeps its normal verdict")
            lines = path.read_text().splitlines()
            self.assertTrue(lines[0].startswith("# load "), lines[0])
            kv = dict(tok.split("=", 1) for tok in lines[0].split()[2:])
            self.assertEqual(kv["joined"], "1", "the bulk thread must have stopped")
            self.assertNotEqual(kv["state"], "running",
                                "the trace must be written after join, not mid-load")
            self.assertNotEqual(kv["stop_ns"], "-1", "the load's end must be recorded")
            self.assertGreaterEqual(int(kv["stop_ns"]), int(kv["start_ns"]))
            probe = lines[1].split()
            self.assertEqual(probe[0], "0")
            self.assertEqual(probe[4:6], ["send=1", "recv=1"], "probe ran with bulk flowing")
            self.assertTrue(probe[6].startswith("send_bytes=") and probe[7].startswith("recv_bytes="),
                            "per-probe byte counts are trailing key=value")
            self.assertEqual(len(lines) - 1, 2, "every planned probe gets a line")

    def test_stuck_bulk_is_reported_incomplete_not_as_a_finished_load(self):
        # `join` returning is not proof the thread died: it may have timed out. A load that
        # never stops is still flowing, so the run must be withheld (rc 1), the trace must
        # say joined=0, and the window must keep its end unset instead of inventing one.
        release = threading.Event()

        def stuck(host, port, direction, seconds, out=None, stop=None, watch=None):
            watch.add_bytes(4096)            # load is real and flowing...
            release.wait(10)                 # ...and never stops on `stop`

        real_bulk = echo.bulk
        echo.bulk = stuck
        try:
            with tempfile.TemporaryDirectory() as td:
                path = Path(td) / "ping.trace"
                import os as _os
                old = _os.environ.get("PING_TRACE")
                _os.environ["PING_TRACE"] = str(path)
                try:
                    with redirect_stdout(io.StringIO()) as buf:
                        rc = echo.pingbulk("127.0.0.1", self.port, 1, join_s=0.2)
                finally:
                    if old is None:
                        _os.environ.pop("PING_TRACE", None)
                    else:
                        _os.environ["PING_TRACE"] = old
                self.assertEqual(rc, 1, "an incomplete load must not pass")
                self.assertIn("incomplete", buf.getvalue())
                head = dict(tok.split("=", 1) for tok in path.read_text().splitlines()[0].split()[2:])
                self.assertEqual(head["joined"], "0")
                self.assertEqual(head["state"], "running", "the window is still open")
                self.assertEqual(head["stop_ns"], "-1", "no end instant may be invented")
        finally:
            echo.bulk = real_bulk
            release.set()


if __name__ == "__main__":
    unittest.main(verbosity=2)
