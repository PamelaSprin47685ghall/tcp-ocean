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

def serve(port):
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
                end = (time.time() + seconds) if seconds else float("inf")
                try:
                    while time.time() < end:
                        c.sendall(block)
                except OSError:
                    pass
            c.shutdown(socket.SHUT_WR)
    while True:
        c, _ = s.accept()
        threading.Thread(target=one, args=(c,), daemon=True).start()

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
    """Records whether the companion bulk transfer is still receiving/sending."""
    def __init__(self):
        self._lock = threading.Lock()
        self.active = False
        self.t_start = None
        self.t_stop = None
        self.bytes = 0

    def set_active(self, on):
        with self._lock:
            now = time.perf_counter()
            self.active = on
            if on and self.t_start is None:
                self.t_start = now
            if not on and self.t_start is not None and self.t_stop is None:
                self.t_stop = now

    def snapshot(self):
        with self._lock:
            return self.active

    def add_bytes(self, n):
        """Count transferred bytes. The first n>0 is what proves load is actually flowing."""
        with self._lock:
            if n > 0:
                self.bytes += n
                if not self.active:
                    now = time.perf_counter()
                    self.active = True
                    if self.t_start is None:
                        self.t_start = now

def ping(host, port, count, label="ping", watch=None, probe_timeout=2.0):
    """COUNT probes, 20 ms apart. If watch is set, each probe records whether bulk
    was active at send and at recv. Timeouts and mismatches are kept in the denominator."""
    records = []
    with socket.create_connection((host, port), timeout=30) as c:
        c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        c.sendall(b"E")
        for i in range(count):
            msg = struct.pack("<I", i) + os.urandom(60)
            t0 = time.perf_counter()
            bulk_send = watch.snapshot() if watch is not None else True
            status = "ok"
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
            except (socket.timeout, TimeoutError, EOFError, OSError) as e:
                t1 = time.perf_counter()
                status = "timeout" if isinstance(e, (socket.timeout, TimeoutError)) else "fail"
                send_ns = locals().get("send_ns")
            bulk_recv = watch.snapshot() if watch is not None else True
            records.append({
                "i": i,
                "t_send": t0,
                "t_recv": t1,
                "rtt_ms": (t1 - t0) * 1000,
                "bulk_at_send": bulk_send,
                "bulk_at_recv": bulk_recv,
                "inside_load": bool(bulk_send and bulk_recv),
                "status": status,
                "send_ns": send_ns,
            })
            time.sleep(0.02)
    if os.environ.get("PING_TRACE") and label == "ping-under-bulk":
        with open(os.environ["PING_TRACE"], "w") as f:
            for r in records:
                ns = r["send_ns"] if r["send_ns"] is not None else 0
                f.write(f"{r['i']} {ns} {r['rtt_ms']:.3f} {r['status']} "
                        f"send={int(r['bulk_at_send'])} recv={int(r['bulk_at_recv'])}\n")
    return _report_ping(label, count, records)

def _report_ping(label, count, records):
    ok = sum(1 for r in records if r["status"] == "ok")
    timeouts = sum(1 for r in records if r["status"] == "timeout")
    fails = sum(1 for r in records if r["status"] not in ("ok", "timeout"))
    loaded = [r for r in records if r["status"] == "ok" and r["inside_load"]]
    ontime_in_load = len(loaded)
    planned = count
    rate = (ontime_in_load / planned) if planned else 0.0
    rtts = sorted(r["rtt_ms"] for r in loaded)
    parts = [f"{label} n={count} ok={ok} timeout={timeouts} fail={fails} denom={count}",
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
            f.write("i status rtt_ms bulk_at_send bulk_at_recv inside_load\n")
            for r in records:
                f.write(f"{r['i']} {r['status']} {r['rtt_ms']:.3f} "
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
                c.sendall(b"G" + struct.pack("<I", dur))
                n = 0
                while True:
                    if stop is not None and stop.is_set():
                        break
                    c.settimeout(0.2)
                    try:
                        d = c.recv(1 << 20)
                    except socket.timeout:
                        continue
                    if not d:
                        break
                    n += len(d)
                    if watch is not None:
                        watch.add_bytes(len(d))
        finally:
            if watch is not None:
                watch.set_active(False)
        dt = time.time() - t0
    mbit = n * 8 / dt / 1e6 if dt > 0 else 0.0
    print(f"bulk {direction} seconds={dt:.1f} bytes={n} goodput_mbit={mbit:.1f}")
    if out is not None: out.append(mbit)
    return 0

def pingbulk(host, port, count, _seconds=None):
    """Run a downlink bulk transfer until every probe has finished. `_seconds` is
    accepted for old call sites but no longer caps the transfer: probes decide when
    bulk stops. Load is proven only after real bulk bytes flow, not at connect.
    The verdict is the on-time success rate of every planned probe under that load."""
    stop = threading.Event()
    watch = BulkWatch()
    out = []
    t = threading.Thread(target=bulk, args=(host, port, "down", 0, out, stop, watch))
    t.start()
    # Wait until bulk is actually receiving, at most 5 s, then a short settle.
    deadline = time.time() + 5
    while time.time() < deadline and not watch.active:
        time.sleep(0.01)
    time.sleep(0.2)
    try:
        r = ping(host, port, count, "ping-under-bulk", watch=watch)
    finally:
        stop.set()
        t.join(30)
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
