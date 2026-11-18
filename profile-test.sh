#!/bin/bash
export DOTNET_PerfMapEnabled=1
export DOTNET_EnableEventLog=1
export COMPlus_PerfMapEnabled=1
/usr/local/bin/tbtun server --listen 127.0.0.1:39444 --target 127.0.0.1:18443 --psk-file /tmp/psk --max-paths 8 &
PID=$!
sleep 2
dotnet trace collect -p $PID --duration 00:00:30 --format speedscope -o /tmp/tbtun-profile.speedscope.json 2>&1 | head -20 &
TRACE_PID=$!
sleep 5
# Simulate load via local client
/usr/local/bin/tbtun client --server 127.0.0.1:39444 --listen 127.0.0.1:19443 --psk-file /tmp/psk --paths 8 &
CLIENT=$!
sleep 5
curl -x socks5h://127.0.0.1:19443 https://www.google.com -o /dev/null -s -m 20 || true
sleep 5
kill $CLIENT 2>/dev/null || true
wait $TRACE_PID
kill $PID 2>/dev/null || true
ls -lh /tmp/tbtun-profile.speedscope.json 2>/dev/null || echo "trace failed"
