#!/usr/bin/env python3
"""End-to-end traffic through the tunnel. The first byte of a connection picks the mode:
'E' echo every byte back, 'S' sink (count bytes, answer the count), 'G' generate bytes
for N seconds.
  echo.py serve PORT                       serve all modes
  echo.py check HOST PORT FLOWS BYTES      FLOWS parallel echo connections, each sends BYTES
                                           random bytes and must get exactly them back
  echo.py ping HOST PORT COUNT             one echo connection, COUNT 64-byte round trips
                                           20 ms apart; prints latency percentiles
  echo.py bulk HOST PORT up|down SECONDS   one connection, prints goodput
  echo.py pingbulk HOST PORT COUNT SECONDS ping while one bulk download runs
Exit status 0 only when every check matched."""
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
                end = time.time() + seconds
                while time.time() < end:
                    c.sendall(block)
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

def ping(host, port, count, label="ping"):
    rtts = []
    with socket.create_connection((host, port), timeout=30) as c:
        c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        c.sendall(b"E")
        for i in range(count):
            msg = struct.pack("<I", i) + os.urandom(60)
            t0 = time.perf_counter()
            c.sendall(msg)
            if recv_exact(c, 64) != msg:
                print(f"{label}: wrong echo"); return 1
            rtts.append((time.perf_counter() - t0) * 1000)
            time.sleep(0.02)
    rtts.sort()
    q = lambda p: rtts[min(len(rtts) - 1, int(p * len(rtts)))]
    print(f"{label} n={count} p50_ms={q(0.5):.1f} p95_ms={q(0.95):.1f} p99_ms={q(0.99):.1f} max_ms={rtts[-1]:.1f}")
    return 0

def bulk(host, port, direction, seconds, out=None):
    with socket.create_connection((host, port), timeout=60) as c:
        t0 = time.time()
        if direction == "up":
            c.sendall(b"S")
            block = os.urandom(1 << 16)
            while time.time() - t0 < seconds:
                c.sendall(block)
            c.shutdown(socket.SHUT_WR)
            n = struct.unpack("<Q", recv_exact(c, 8))[0]
        else:
            c.sendall(b"G" + struct.pack("<I", seconds))
            n = 0
            while True:
                d = c.recv(1 << 20)
                if not d:
                    break
                n += len(d)
        dt = time.time() - t0
    mbit = n * 8 / dt / 1e6
    print(f"bulk {direction} seconds={dt:.1f} bytes={n} goodput_mbit={mbit:.1f}")
    if out is not None: out.append(mbit)
    return 0

def pingbulk(host, port, count, seconds):
    t = threading.Thread(target=bulk, args=(host, port, "down", seconds))
    t.start()
    time.sleep(1)
    r = ping(host, port, count, "ping-under-bulk")
    t.join()
    return r

if __name__ == "__main__":
    a = sys.argv
    if a[1] == "serve": serve(int(a[2]))
    elif a[1] == "check": sys.exit(check(a[2], int(a[3]), int(a[4]), int(a[5])))
    elif a[1] == "ping": sys.exit(ping(a[2], int(a[3]), int(a[4])))
    elif a[1] == "bulk": sys.exit(bulk(a[2], int(a[3]), a[4], int(a[5])))
    elif a[1] == "pingbulk": sys.exit(pingbulk(a[2], int(a[3]), int(a[4]), int(a[5])))
