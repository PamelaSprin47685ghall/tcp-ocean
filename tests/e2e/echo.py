#!/usr/bin/env python3
"""End-to-end echo traffic.
  echo.py serve PORT                       echo every byte back, then half-close
  echo.py check HOST PORT FLOWS BYTES      FLOWS parallel connections, each sends BYTES
                                           random bytes and must get exactly them back
Exit status 0 only when every flow matched."""
import os, socket, sys, threading, time

def serve(port):
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("0.0.0.0", port))
    s.listen(256)
    def one(c):
        with c:
            while True:
                d = c.recv(65536)
                if not d:
                    break
                c.sendall(d)
            c.shutdown(socket.SHUT_WR)
    while True:
        c, _ = s.accept()
        threading.Thread(target=one, args=(c,), daemon=True).start()

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
            c.sendall(data)
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

if __name__ == "__main__":
    if sys.argv[1] == "serve":
        serve(int(sys.argv[2]))
    else:
        sys.exit(check(sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])))
