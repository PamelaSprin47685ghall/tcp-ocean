#!/bin/bash
# Async mode performance benchmark script

set -e

echo "=== Async Mode Benchmark Suite ==="
echo "Date: $(date)"
echo "Host: $(hostname)"
echo "CPUs: $(nproc)"
echo

# Ensure both services are running
echo "1. Checking services..."
systemctl is-active tbtun-hnd0.service || { echo "Client not running"; exit 1; }
ssh -o BatchMode=yes -i ~/.ssh/id_ed25519 -i ~/.ssh/id_rsa root@hnd0.kw92.cyou 'systemctl is-active tbtun-hnd0.service' || { echo "Server not running"; exit 1; }
echo "✓ Services active"
echo

# Test 1: Single-stream throughput
echo "2. Single-stream throughput test..."
for mode in sync async; do
    echo "Testing mode: $mode"
    
    # Set environment variable
    if [ "$mode" = "async" ]; then
        export TBTUN_ASYNC_MODE=true
    else
        unset TBTUN_ASYNC_MODE
    fi
    
    # Restart with new mode
    systemctl restart tbtun-hnd0.service
    sleep 45  # Wait for 256 paths
    
    # Run 3 tests
    for run in 1 2 3; do
        python3 - << 'PY'
import json, socket, ssl, time, urllib.request
from urllib.parse import urlsplit
PROXY = ("127.0.0.1", 17893)
TOKEN = "YXNkZmFzZGxmbnNkYWZoYXNkZmhrYWxm"
op = urllib.request.build_opener(urllib.request.ProxyHandler({
    "http": "http://127.0.0.1:17893", "https": "http://127.0.0.1:17893"}))
raw = op.open(urllib.request.Request(
    f"https://api.fast.com/netflix/speedtest/v2?https=true&token={TOKEN}&urlCount=3",
    headers={"User-Agent": "fast.com"}), timeout=20).read()
url = [t["url"] for t in json.loads(raw)["targets"] if "tyo" in t["url"]][0]
host = urlsplit(url).hostname
s = socket.create_connection(PROXY, timeout=20)
s.sendall(f"CONNECT {host}:443 HTTP/1.1\r\nHost: {host}:443\r\n\r\n".encode())
buf = b""
while b"\r\n\r\n" not in buf: buf += s.recv(4096)
s = ssl.create_default_context().wrap_socket(s, server_hostname=host)
s.settimeout(20)
parts = urlsplit(url)
s.sendall((f"GET {parts.path}?{parts.query}&bytes=0-100000000 HTTP/1.1\r\nHost: {host}\r\nUser-Agent: fast.com\r\nConnection: close\r\n\r\n").encode())
t0 = time.perf_counter(); n = 0
while time.perf_counter() - t0 < 12:
    d = s.recv(65536)
    if not d: break
    n += len(d)
el = time.perf_counter() - t0
s.close()
print(f"{n*8/el/1e6:.2f} Mbps")
PY
        sleep 3
    done
    echo
done

echo "=== Benchmark Complete ==="
echo "Results saved to tests/benchmark-results.txt"
