#!/usr/bin/env python3
"""End-to-end traffic through the tunnel. The first byte of a connection picks the mode:
'E' echo every byte back, 'S' sink (count bytes, answer the count), 'G' generate bytes
for N seconds (N=0 means until the client closes).
  echo.py serve PORT                       serve all modes
  echo.py check HOST PORT FLOWS BYTES      FLOWS parallel echo connections, each sends BYTES
                                           random bytes and must get exactly them back
  echo.py ping HOST PORT COUNT             one echo connection, COUNT 64-byte round trips
                                           20 ms apart; prints latency percentiles
  echo.py bulk HOST PORT up|down SECONDS   one connection, prints goodput
  echo.py pingbulk HOST PORT COUNT         ping while one bulk download runs until every
                                           probe has finished (success, timeout, or fail).
                                           verdict is the on-time success rate of ALL planned
                                           probes under proven load; P99 is secondary and uses
                                           only those successful in-load samples.
Exit status 0 only when every check matched, and for ping/pingbulk when every planned
probe succeeded on time under proven load (zero in-load samples is a failure)."""
import os, socket, struct, sys, threading, time

def serve(port, stop=None):
    """Accept until `stop` is set. `stop` is how a test host closes a server it started:
    without it the accept loop outlives the test, and every test that starts one leaks a
    socket and a thread for the life of the process."""
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("0.0.0.0", port))
    s.listen(256)
    block = os.urandom(65536)
    def one(c):
        with c:
            mode = c.recv(1)
            if mode == b"E":
                while True:
                    d = c.recv(65536)
                    if not d:
                        break
                    c.sendall(d)
            elif mode == b"S":
                n = 0
                while True:
                    d = c.recv(1 << 20)
                    if not d:
                        break
                    n += len(d)
                c.sendall(struct.pack("<Q", n))
            elif mode == b"G":
                seconds = struct.unpack("<I", recv_exact(c, 4))[0]
                ph = _bulk_phases("server")
                try:
                    run_generator(c.sendall, seconds, block, phases=ph)
                except OSError:
                    pass
                _append_phase(ph)
            try:
                c.shutdown(socket.SHUT_WR)
            except OSError:
                # The peer may already have closed its half (a probe that timed out and
                # went away). Nothing is left to signal, and a traceback here would be
                # printed by the thread runtime into the caller's output.
                pass
    s.settimeout(0.1)
    try:
        while stop is None or not stop.is_set():
            try:
                c, _ = s.accept()
            except socket.timeout:
                continue
            threading.Thread(target=one, args=(c,), daemon=True).start()
    finally:
        s.close()

def recv_exact(c, n):
    b = bytearray()
    while len(b) < n:
        d = c.recv(n - len(b))
        if not d:
            raise EOFError("short read")
        b.extend(d)
    return bytes(b)

def check(host, port, flows, size):
    results = [False] * flows
    def one(i):
        data = os.urandom(size)
        got = bytearray()
        with socket.create_connection((host, port), timeout=60) as c:
            def rd():
                while True:
                    d = c.recv(65536)
                    if not d:
                        break
                    got.extend(d)
            t = threading.Thread(target=rd)
            t.start()
            c.sendall(b"E" + data)
            c.shutdown(socket.SHUT_WR)
            t.join(120)
        results[i] = bytes(got) == data
    start = time.time()
    ts = [threading.Thread(target=one, args=(i,)) for i in range(flows)]
    for t in ts: t.start()
    for t in ts: t.join()
    dt = time.time() - start
    ok = sum(results)
    print(f"echo flows={flows} bytes={size} ok={ok}/{flows} seconds={dt:.2f} "
          f"goodput_mbit={2 * flows * size * 8 / dt / 1e6:.1f}")
    return 0 if ok == flows else 1

def _pct(sorted_vals, p):
    if not sorted_vals:
        return None
    return sorted_vals[min(len(sorted_vals) - 1, int(p * len(sorted_vals)))]

class BulkWatch:
    """Records whether the companion bulk transfer is still receiving/sending.

    One clock read, two representations. Each boundary is read once with
    `perf_counter_ns` and the float seconds are derived from that same reading, so
    `t_start` and `t_start_ns` name one instant instead of two. A timestamp is `None`
    until its event happened, and `state` names the case, so "never started", "still
    running", "finished" and "raised" never have to be inferred from a number.
    """
    STATE_NONE = "none"        # no byte has flowed yet
    STATE_RUNNING = "running"  # first byte seen, window not closed
    STATE_DONE = "done"        # window closed normally
    STATE_ERROR = "error"      # the transfer raised

    def __init__(self):
        self._lock = threading.Lock()
        self.active = False
        self.t_start = None
        self.t_stop = None
        self.t_start_ns = None
        self.t_stop_ns = None
        self.bytes = 0
        self.error = None

    def set_active(self, on):
        with self._lock:
            if on and self.t_stop is not None:
                # A closed window stays closed. `active` is what `inside_load` is read from, so
                # reopening it would relabel a probe that ran after the load as having run under
                # it — the one direction of error that inflates an under-load result. The bytes
                # may keep arriving; the window does not reopen.
                self.active = False
                return
            now_ns = time.perf_counter_ns()
            now = now_ns / 1e9
            self.active = on
            if on and self.t_start is None:
                self.t_start = now
                self.t_start_ns = now_ns
            if not on and self.t_start is not None and self.t_stop is None:
                self.t_stop = now
                self.t_stop_ns = now_ns

    def snapshot(self):
        with self._lock:
            return self.active

    def mark_error(self, exc):
        """Record that the transfer raised. The window still closes in `finally`, so a probe's
        bytes stay attributable, but `state` stays `error` — a failed transfer must not read
        as a completed one."""
        with self._lock:
            if self.error is None:
                self.error = type(exc).__name__

    def _state_locked(self):
        if self.error is not None:
            return self.STATE_ERROR
        if self.t_start is None:
            return self.STATE_NONE
        return self.STATE_RUNNING if self.t_stop is None else self.STATE_DONE

    def state(self):
        with self._lock:
            return self._state_locked()

    def snapshot_full(self):
        """One consistent read: (active, bytes, t_start_ns, t_stop_ns, state, error).

        A single lock acquisition is what makes the numbers comparable. Reading `bytes` and
        then a timestamp separately lets a probe pair a byte count from after the window
        closed with a start time from before it — exactly the mismatch this exists to rule
        out. `None` means the event did not happen (not zero).
        """
        with self._lock:
            return (self.active, self.bytes, self.t_start_ns, self.t_stop_ns,
                    self._state_locked(), self.error)

    def add_bytes(self, n):
        """Count transferred bytes. The first n>0 is what proves load is actually flowing.

        Bytes keep counting after the window closes (a late read may still drain), but a
        closed window must not be reopened: `active` is what `inside_load` is read from, so
        letting a post-stop byte flip it back to True would label a probe that ran after the
        load ended as having run under it.
        """
        with self._lock:
            if n > 0:
                self.bytes += n
                if not self.active and self.t_stop is None:
                    now_ns = time.perf_counter_ns()
                    self.active = True
                    if self.t_start is None:
                        self.t_start = now_ns / 1e9
                        self.t_start_ns = now_ns

def _unset(v):
    """Trace sentinel for "this event did not happen": `-1`.

    Not `0`: a `perf_counter_ns` reading of 0 is legal, so an absent timestamp written as 0
    would be indistinguishable from a real one at the start of the clock.
    """
    return -1 if v is None else v


def _boot_id():
    """This boot's identity. Necessary but NOT sufficient for clock comparability.

    `/proc/sys/kernel/random/boot_id` is global to the machine, but CLOCK_MONOTONIC can be
    offset per Linux *time namespace* (`CLONE_NEWTIME`, Linux 5.6+): two processes with the
    same boot id in different time namespaces read different monotonic values. So a reader
    must compare `timens` as well — see `_time_ns_id` and `same_clock_domain`.
    """
    try:
        with open("/proc/sys/kernel/random/boot_id", encoding="ascii") as f:
            return f.read().strip() or "unknown"
    except OSError:
        return "unknown"


def _time_ns_id():
    """Identity of this process's time namespace — the domain CLOCK_MONOTONIC lives in.

    Complements `_boot_id`: equal boot ids across two different time namespaces still name
    different monotonic scales, so `boot` alone cannot certify a subtraction. `unknown` means
    the kernel or runtime does not expose it, and then comparability cannot be certified.
    """
    try:
        return os.readlink("/proc/self/ns/time") or "unknown"
    except OSError:
        return "unknown"


def same_clock_domain(a, b):
    """Whether two phase records *claim* the same monotonic clock domain.

    True only when both records name the same boot id *and* the same time namespace, and
    neither field is `unknown`. That is a necessary condition, not a proof:

      · a time-namespace id is a filesystem inode number, and inode numbers are reused — two
        distinct namespaces alive at different times can present the same string;
      · boot ids are random per boot, but this checks that two *observed* strings agree; it
        cannot observe a clock being offset by anything that leaves both strings unchanged
        (a container runtime adjusting the namespace offset, for example);
      · readings taken on two different machines are only ever comparable if both strings
        agree, which is why the check exists at all — but agreement is evidence, not a
        guarantee, so a subtraction across two hosts stays an inference.

    Use it to refuse the obviously incomparable case, and treat a `True` as "the identities
    look the same", not as "the scales are certainly identical". Same host name, same pid
    namespace or equal wall-clock time are all irrelevant here.
    """
    kb = ("boot", "timens")
    if any(a.get(k) in (None, "unknown") or b.get(k) in (None, "unknown") for k in kb):
        return False
    return all(a.get(k) == b.get(k) for k in kb)


def record_intact(line):
    """Whether a phase-record line satisfies its own trailing ` nbytes=<body length>`.

    The tail is what makes a torn or interleaved write detectable. `False` is the answer for
    a line that has no such tail at all (a fragment cut before it was written), for one whose
    tail is not a number, and for one whose body length disagrees with the tail — including
    the case where a later record was appended onto a fragment and the two now share a line.
    A reader that parses fields without this check reads truncated numbers as facts.
    """
    head, sep, nbytes = line.rpartition(" nbytes=")
    if not sep or not nbytes.isdigit():
        return False
    return len(head) == int(nbytes)


class BulkPhases:
    """Opt-in, same-clock-domain record of the phases of one bulk transfer.

    It exists to separate two facts that the existing `seconds=` metric conflates: the
    generator running longer than it was asked to, and bytes that were already generated
    still draining through socket and tunnel buffers. `seconds=` spans the `G` request, the
    whole generation and the final drain, and `G` mode carries no in-band "generation
    finished" signal, so the split cannot be read out of it.

    Only `time.monotonic_ns()` readings are recorded. `time.time()` — which the transfer
    metrics use — is CLOCK_REALTIME and can step under NTP; over a transfer of tens of
    seconds that is enough to make a subtraction wrong. `boot` and `timens` name the clock
    domain the readings were taken in, so a reader can refuse the obviously incomparable case
    — see `same_clock_domain`, which is a necessary check and not a proof.
    """
    def __init__(self, role, boot=None, ns_now=None, tens=None):
        self.role = role
        self.boot = boot if boot is not None else _boot_id()
        self.timens = tens if tens is not None else _time_ns_id()
        self._ns_now = ns_now if ns_now is not None else time.monotonic_ns
        self.fields = {}

    def note(self, name):
        self.fields[name] = self._ns_now()

    def add(self, name, value):
        self.fields[name] = value

    def identity(self):
        """The two fields `same_clock_domain` compares."""
        return {"boot": self.boot, "timens": self.timens}

    def line(self):
        parts = [f"role={self.role}", f"boot={self.boot}", f"timens={self.timens}"]
        for k in sorted(self.fields):
            parts.append(f"{k}={self.fields[k]}")
        return " ".join(parts)


def _bulk_phases(role):
    """A phase record when `BULK_PHASE_TRACE` names a file, otherwise None.

    None is the normal case. With the switch off this costs one `os.environ.get` per
    connection and no record is ever built, so the data path is byte-for-byte unchanged.
    """
    return BulkPhases(role) if os.environ.get("BULK_PHASE_TRACE") else None


def _append_phase(ph):
    """Append one record line, atomically and visibly.

    Atomic: `os.open(O_APPEND)` + a single `os.write` — one append syscall, so a record from
    the server thread and one from the client process cannot interleave inside a line. A
    buffered `open("a")` + `close` is *usually* one write but is not guaranteed to be.

    Visible: a diagnostics switch that silently drops records is worse than no switch — a
    reader would take "no overrun recorded" for "no overrun happened". The failure goes to
    stderr and the transfer continues; diagnostics must never cost bytes, but they must not
    lie by omission either.
    """
    path = os.environ.get("BULK_PHASE_TRACE")
    if ph is None or not path:
        return
    body = ph.line()
    # The record carries its own body length, so a reader can reject a line that was split or
    # cut. Without it a short write leaves a fragment that still parses as `role=…` with
    # truncated numbers: a wrong record that looks right, which is worse than no record.
    data = f"{body} nbytes={len(body)}\n".encode()
    try:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
        try:
            # One append syscall, never a retry. O_APPEND makes a single write atomic at EOF;
            # a second write would append at the *then* current EOF — after any record another
            # writer added meanwhile — and split this one around it. A short write is reported
            # and left to the reader to reject via `nbytes`, not repaired into a corrupt file.
            n = os.write(fd, data)
            if n != len(data):
                sys.stderr.write(
                    f"echo.py: BULK_PHASE_TRACE={path} record torn ({n} of {len(data)} bytes "
                    f"written, not retried; readers must reject it via nbytes)\n")
                sys.stderr.flush()
        finally:
            os.close(fd)
    except OSError as exc:
        sys.stderr.write(f"echo.py: BULK_PHASE_TRACE={path} write failed: {exc}\n")
        sys.stderr.flush()


def run_generator(send, seconds, block, now=time.time, phases=None):
    """Server side of `G` mode: send `block` until `seconds` elapse (0 = until `send` raises).

    The control law is the one this function always had, and nothing here changes it: the
    deadline comes from `now` (the caller's wall clock), it is re-read once per iteration
    between sends, and `now() < end` is the only thing that stops a timed transfer. An
    earlier revision switched the deadline to CLOCK_MONOTONIC whenever diagnostics were on,
    which made the observed loop a different loop from the one production runs — a diagnostic
    must not change what is being measured.

    So both clocks are recorded side by side instead: `wall_*` for the one that decides, and
    `mono_*` for the one that cannot step. `clock_skew_ns` is their span difference. That
    number is where a wall-clock step shows up; it is *recorded*, never corrected, and it
    cannot be attributed to a cause (NTP step, suspend/resume, a wrong clock in a namespace)
    from these two readings alone.

    `now` is consulted only *between* `send` calls, so one `send` that blocks past the
    deadline stretches the real window without bound: the loop cannot interrupt a syscall in
    progress. `straddle_sends` counts sends that finished strictly after the deadline — the
    only way this loop can miss it. A send that finishes exactly on the deadline finished on
    time and is not counted. With `phases` off, the loop issues exactly the clock reads it
    issued before (one per iteration) and builds no record.
    """
    wall_start = now()
    if phases is not None:
        phases.add("req_s", seconds)
        mono = phases._ns_now
        mono_start = mono()
        phases.add("wall_start_ns", int(wall_start * 1_000_000_000))
        phases.add("mono_start_ns", mono_start)
    # `seconds=0` means "until the peer closes": no deadline exists, so none is computed.
    end = (wall_start + seconds) if seconds else float("inf")
    straddles = 0
    sent = 0
    prev = wall_start
    try:
        while True:
            t = now()
            # Exactly one clock read per iteration — the same read the untraced loop makes,
            # so switching the record on does not perturb the loop it is measuring. The
            # interval (prev, t] contains the previous send, so a deadline crossing inside it
            # is that send finishing late: counted exactly, with no tolerance and no extra
            # read. `t` is also this iteration's loop condition.
            if phases is not None and prev < end < t:
                straddles += 1
            if t >= end:
                break
            send(block)
            sent += len(block)
            prev = t
    finally:
        if phases is not None:
            exc = sys.exc_info()[0]
            if exc is not None:
                # A transfer that raised is not a completed window. Without this marker a
                # peer that vanished mid-way would leave a record indistinguishable from a
                # clean short transfer — the failure would be reported as a success.
                phases.add("error", exc.__name__)
            wall_end = now()
            mono_end = mono()
            wall_span = int(round((wall_end - wall_start) * 1_000_000_000))
            mono_span = mono_end - mono_start
            phases.add("wall_end_ns", int(wall_end * 1_000_000_000))
            phases.add("mono_end_ns", mono_end)
            phases.add("wall_span_ns", wall_span)
            phases.add("mono_span_ns", mono_span)
            phases.add("clock_skew_ns", wall_span - mono_span)
            phases.add("excess_ns", (wall_span - seconds * 1_000_000_000) if seconds else 0)
            phases.add("straddle_sends", straddles)
            phases.add("bytes", sent)
    return sent


def _write_ping_trace(path, records, watch, joined=True):
    """Optional, self-contained diagnostics: the load window plus per-probe byte counts.

    Format is append-only by design. A record keeps its five positional fields and its
    `send=`/`recv=` flags with exactly their old meaning, and every new fact is a trailing
    `key=value`; a reader that slices the first six tokens, or that looks up `send=`, keeps
    working. The header line starts with `#` so a positional reader can skip it — and without
    it a probe could only be called "overlapping" by comparing it against the *process*
    span, which is strictly wider than the interval in which bytes actually flowed.
    """
    snap = watch.snapshot_full() if watch is not None else None
    # `joined=0` says the bulk thread had not stopped when its join timed out. The window
    # then legitimately has no end, and a reader must not treat "still running" as "finished
    # with no bytes": the load may still be flowing while the process exits.
    joined_key = "joined=1" if joined else "joined=0"
    with open(path, "w") as f:
        if snap is None:
            f.write(f"# load state={BulkWatch.STATE_NONE} start_ns={_unset(None)} "
                    f"stop_ns={_unset(None)} bytes=0 error=- {joined_key}\n")
        else:
            f.write(f"# load state={snap[4]} start_ns={_unset(snap[2])} "
                    f"stop_ns={_unset(snap[3])} bytes={snap[1]} "
                    f"error={snap[5] or '-'} {joined_key}\n")
        for r in records:
            f.write(f"{r['i']} {_unset(r['send_ns'])} {r['rtt_ms']:.3f} {r['status']} "
                    f"send={int(r['bulk_at_send'])} recv={int(r['bulk_at_recv'])} "
                    f"send_bytes={_unset(r.get('bytes_at_send'))} "
                    f"recv_bytes={_unset(r.get('bytes_at_recv'))}\n")


def ping(host, port, count, label="ping", watch=None, probe_timeout=2.0, records_out=None):
    """COUNT probes, 20 ms apart. If watch is set, each probe records whether bulk
    was active at send and at recv. Timeouts and mismatches are kept in the denominator."""
    records = []
    # The probe protocol is a strict 64-byte request/response frame on ONE stream. If a
    # probe dies mid-frame (timeout, EOF, reset), the bytes it already consumed are gone and
    # the peer's remaining bytes stay queued, so every later `recv_exact` reads a shifted
    # frame. Proven with a deterministic local server that echoed every request correctly: it
    # slept past the probe timeout on the first request, and the third probe was then reported
    # as `mismatch` — pure frame desynchronisation, not payload corruption. A torn frame
    # therefore ends the connection: the remaining planned probes are reported as `unrun`
    # (kind `desync`) so the denominator stays `count` and no further probe is judged on a
    # stream whose boundaries can no longer be trusted.
    torn = None
    with socket.create_connection((host, port), timeout=30) as c:
        c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        c.sendall(b"E")
        for i in range(count):
            msg = struct.pack("<I", i) + os.urandom(60)
            t0 = time.perf_counter()
            snap_send = watch.snapshot_full() if watch is not None else None
            bulk_send = snap_send[0] if snap_send is not None else True
            bytes_at_send = snap_send[1] if snap_send is not None else None
            status = "ok"
            kind = ""
            try:
                c.settimeout(probe_timeout)
                if os.environ.get("PING_TRACE") and label == "ping-under-bulk":
                    send_ns = time.perf_counter_ns()
                else:
                    send_ns = None
                c.sendall(msg)
                got = recv_exact(c, 64)
                t1 = time.perf_counter()
                if got != msg:
                    status = "mismatch"
                    kind = "mismatch"
            except (socket.timeout, TimeoutError, EOFError, OSError) as e:
                t1 = time.perf_counter()
                status = "timeout" if isinstance(e, (socket.timeout, TimeoutError)) else "fail"
                # Category only. "fail" currently lumps a peer close, a reset and a broken
                # pipe together, and those have different owners (tunnel reset vs echo
                # server vs local socket). The class name plus errno separates them
                # without recording messages, addresses or payload.
                kind = type(e).__name__
                errno_ = getattr(e, "errno", None)
                if status != "timeout" and errno_ is not None:
                    kind = f"{kind}:{errno_}"
                send_ns = locals().get("send_ns")
            snap_recv = watch.snapshot_full() if watch is not None else None
            bulk_recv = snap_recv[0] if snap_recv is not None else True
            bytes_at_recv = snap_recv[1] if snap_recv is not None else None
            records.append({
                "i": i,
                "t_send": t0,
                "t_recv": t1,
                "rtt_ms": (t1 - t0) * 1000,
                "bulk_at_send": bulk_send,
                "bulk_at_recv": bulk_recv,
                "bytes_at_send": bytes_at_send,
                "bytes_at_recv": bytes_at_recv,
                "inside_load": bool(bulk_send and bulk_recv),
                "status": status,
                "kind": kind,
                "send_ns": send_ns,
            })
            if status != "ok":
                # Frame boundary lost: stop probing this connection (see `torn` above).
                torn = (status, kind)
                break
            time.sleep(0.02)
    if torn is not None:
        # Remain planned, never invented: `unrun` is reported separately and keeps `count` as
        # the denominator, so `ontime_rate` cannot improve just because the stream was lost.
        active = watch.snapshot() if watch is not None else True
        bytes_now = watch.snapshot_full()[1] if watch is not None else None
        for j in range(len(records), count):
            records.append({
                "i": j,
                "t_send": time.perf_counter(),
                "t_recv": time.perf_counter(),
                "rtt_ms": 0.0,
                "bulk_at_send": active,
                "bulk_at_recv": active,
                "bytes_at_send": bytes_now,
                "bytes_at_recv": bytes_now,
                "inside_load": bool(active),
                "status": "unrun",
                "kind": "desync",
                "send_ns": None,
            })
    if records_out is not None:
        # Hand the records to the caller instead of writing the trace here: `ping` returns
        # before the bulk thread is stopped, so a header written at this point could never
        # carry the load's end. The trace is written by `pingbulk` after `join`.
        records_out.extend(records)
    return _report_ping(label, count, records)

def _report_ping(label, count, records):
    ok = sum(1 for r in records if r["status"] == "ok")
    timeouts = sum(1 for r in records if r["status"] == "timeout")
    unrun = sum(1 for r in records if r["status"] == "unrun")
    fails = sum(1 for r in records if r["status"] not in ("ok", "timeout", "unrun"))
    loaded = [r for r in records if r["status"] == "ok" and r["inside_load"]]
    ontime_in_load = len(loaded)
    planned = count
    rate = (ontime_in_load / planned) if planned else 0.0
    rtts = sorted(r["rtt_ms"] for r in loaded)
    parts = [f"{label} n={count} ok={ok} timeout={timeouts} fail={fails} unrun={unrun} denom={count}",
             f"under_load={ontime_in_load}",
             f"ontime_in_load={ontime_in_load}",
             f"planned={planned}",
             f"ontime_rate={rate:.3f}"]
    if rtts:
        parts.append(f"p50_ms={_pct(rtts, 0.5):.1f}")
        parts.append(f"p95_ms={_pct(rtts, 0.95):.1f}")
        parts.append(f"p99_ms={_pct(rtts, 0.99):.1f}")
        parts.append(f"max_ms={rtts[-1]:.1f}")
        parts.append("(p99 secondary, over successful in-load samples)")
    else:
        parts.append("p99_ms=na (no successful in-load sample)")
    print(" ".join(parts))
    log_path = os.environ.get("ECHO_PROBE_LOG")
    if log_path:
        with open(log_path, "w") as f:
            f.write("i status kind rtt_ms bulk_at_send bulk_at_recv inside_load\n")
            for r in records:
                # `-` rather than an empty string: an `ok` probe has no category, and writing
                # nothing there produced two spaces in a row. Any reader that splits on
                # whitespace then sees six fields instead of seven and silently shifts
                # every column after it, which is worse than a missing value.
                kind = r.get("kind") or "-"
                f.write(f"{r['i']} {r['status']} {kind} {r['rtt_ms']:.3f} "
                        f"{int(r['bulk_at_send'])} {int(r['bulk_at_recv'])} {int(r['inside_load'])}\n")
    # Primary verdict: on-time success of every planned probe under proven load.
    # Zero in-load samples is a failure even if every idle probe succeeded.
    return 0 if planned and ontime_in_load == planned else 1

def bulk(host, port, direction, seconds, out=None, stop=None, watch=None):
    """Transfer for `seconds`. seconds=0, or a stop event, runs until stop is set
    (or the peer closes). watch, if given, is a BulkWatch updated around the transfer."""
    with socket.create_connection((host, port), timeout=60) as c:
        t0 = time.time()
        try:
            if direction == "up":
                c.sendall(b"S")
                block = os.urandom(1 << 16)
                while True:
                    if stop is not None and stop.is_set():
                        break
                    if seconds and time.time() - t0 >= seconds:
                        break
                    c.settimeout(0.2)
                    try:
                        c.sendall(block)
                    except socket.timeout:
                        continue
                    if watch is not None:
                        watch.add_bytes(len(block))
                c.shutdown(socket.SHUT_WR)
                c.settimeout(60)
                n = struct.unpack("<Q", recv_exact(c, 8))[0]
            else:
                # seconds=0: generate until this socket is closed (server G mode).
                dur = 0 if (stop is not None or seconds == 0) else seconds
                ph = _bulk_phases("client")
                if ph is not None:
                    ph.add("req_s", dur)
                    ph.note("req_ns")
                c.sendall(b"G" + struct.pack("<I", dur))
                n = 0
                saw_eof = False
                while True:
                    if stop is not None and stop.is_set():
                        break
                    c.settimeout(0.2)
                    try:
                        d = c.recv(1 << 20)
                    except socket.timeout:
                        continue
                    if not d:
                        saw_eof = True
                        break
                    if ph is not None and n == 0:
                        # The first byte is the only client-side evidence that generation
                        # actually started; without it a stalled request and a slow drain
                        # look the same in `seconds=`.
                        ph.note("first_ns")
                    n += len(d)
                    if watch is not None:
                        watch.add_bytes(len(d))
                if ph is not None:
                    # EOF and "the caller stopped us" are different facts and must not share a
                    # name: a stop means the peer never finished generating, so the client's
                    # last reading is not the transfer's end and nothing may be inferred from
                    # it as though it were.
                    ph.add("stopped", 0 if saw_eof else 1)
                    ph.note("eof_ns" if saw_eof else "stop_ns")
                    ph.add("bytes", n)
                    _append_phase(ph)
        except BaseException as exc:
            # A transfer that raises must not read as one that finished: `state` stays
            # `error` while `finally` still closes the window so byte counts remain usable.
            if watch is not None:
                watch.mark_error(exc)
            raise
        finally:
            if watch is not None:
                watch.set_active(False)
        dt = time.time() - t0
    mbit = n * 8 / dt / 1e6 if dt > 0 else 0.0
    print(f"bulk {direction} seconds={dt:.1f} bytes={n} goodput_mbit={mbit:.1f}")
    if out is not None: out.append(mbit)
    return 0

def pingbulk(host, port, count, _seconds=None, join_s=30.0):
    """Run a downlink bulk transfer until every probe has finished. `_seconds` is
    accepted for old call sites but no longer caps the transfer: probes decide when
    bulk stops. Load is proven only after real bulk bytes flow, not at connect.
    The verdict is the on-time success rate of every planned probe under that load.

    `join_s` bounds the wait for the bulk thread. If it does not stop in time the run is
    reported as incomplete (rc 1), never as a finished load: the trace then carries
    `joined=0` and the window's end stays unset. The thread is a daemon so a transfer stuck
    in a syscall cannot keep the interpreter alive after that verdict is printed.
    """
    stop = threading.Event()
    watch = BulkWatch()
    out = []
    t = threading.Thread(target=bulk, args=(host, port, "down", 0, out, stop, watch), daemon=True)
    t.start()
    # Wait until bulk is actually receiving, at most 5 s, then a short settle.
    deadline = time.time() + 5
    while time.time() < deadline and not watch.active:
        time.sleep(0.01)
    time.sleep(0.2)
    records = []
    r = 1
    joined = True
    try:
        r = ping(host, port, count, "ping-under-bulk", watch=watch, records_out=records)
    finally:
        stop.set()
        t.join(join_s)
        # `join` can time out; only `is_alive` tells whether the thread is really gone. A
        # timed-out load is still flowing, so the window must stay open and the verdict must
        # not claim a finished under-load result.
        joined = not t.is_alive()
        # After the join the window is final: `state` is none/done/error and `stop_ns` is
        # real (or -1 only if no byte ever flowed). Writing it inside `ping` would have
        # recorded `state=running, stop_ns=-1` every single run.
        path = os.environ.get("PING_TRACE")
        if path:
            _write_ping_trace(path, records, watch, joined)
    if not joined:
        print(f"pingbulk incomplete: bulk did not stop within {join_s}s; "
              f"load window left open, verdict withheld")
        return 1
    return r

if __name__ == "__main__":
    a = sys.argv
    if a[1] == "serve": serve(int(a[2]))
    elif a[1] == "check": sys.exit(check(a[2], int(a[3]), int(a[4]), int(a[5])))
    elif a[1] == "ping": sys.exit(ping(a[2], int(a[3]), int(a[4])))
    elif a[1] == "bulk": sys.exit(bulk(a[2], int(a[3]), a[4], int(a[5])))
    elif a[1] == "pingbulk":
        # COUNT is required; a trailing SECONDS is ignored (kept so old run.sh still parses).
        sys.exit(pingbulk(a[2], int(a[3]), int(a[4]), int(a[5]) if len(a) > 5 else None))
