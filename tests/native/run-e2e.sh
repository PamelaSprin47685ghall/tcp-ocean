#!/usr/bin/env bash
# Host-side end-to-end: linux-x64 libtbt.so + local tbtun server (kernel TCP, no TUN)
# + loopback echo. No netns or CAP_NET_ADMIN required.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${OUT:-$ROOT/artifacts/publish/Tbt.NativeLib/linux-x64}"
CLI_OUT="${CLI_OUT:-$ROOT/artifacts/publish/Tbt.Cli/linux-x64}"
export DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
WORKDIR=$(mktemp -d /tmp/tbt-e2e.XXXXXX)
cleanup() {
  if [[ -n "${SERVER_PID:-}" ]]; then kill "$SERVER_PID" 2>/dev/null || true; fi
  if [[ -n "${ECHO_PID:-}" ]]; then kill "$ECHO_PID" 2>/dev/null || true; fi
  rm -rf "$WORKDIR"
}
trap cleanup EXIT

dotnet publish "$ROOT/src/Tbt.NativeLib/Tbt.NativeLib.csproj" \
    -c Release -r linux-x64 \
    -p:PublishAot=true -p:NativeLib=Shared \
    -o "$OUT"
dotnet publish "$ROOT/src/Tbt.Cli/Tbt.Cli.fsproj" \
    -c Release -r linux-x64 \
    -o "$CLI_OUT"

if [[ -f "$OUT/tbt.so" && ! -f "$OUT/libtbt.so" ]]; then
  ln -sfn tbt.so "$OUT/libtbt.so"
fi
# PrefixLibTbt may already have copied it.

cc -std=c11 -Wall -Wextra -O2 -o "$WORKDIR/tcp_echo" "$ROOT/tests/native/tcp_echo.c"
cc -std=c11 -Wall -Wextra -O2 \
    -I"$ROOT/src/Tbt.NativeLib" -I"$ROOT/tests/native" \
    -o "$OUT/tbt_e2e" "$ROOT/tests/native/tbt_e2e.c" \
    -L"$OUT" -ltbt -Wl,-rpath,"$OUT"

"$WORKDIR/tcp_echo" 0 >"$WORKDIR/echo.port" &
ECHO_PID=$!
for i in $(seq 1 50); do
  [[ -s "$WORKDIR/echo.port" ]] && break
  sleep 0.05
done
ECHO_PORT=$(tr -d '[:space:]' < "$WORKDIR/echo.port")
# Pick a free server port
SERVER_PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
PSK=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
printf '%s\n' "$PSK" >"$WORKDIR/psk.txt"

TBTUN="$CLI_OUT/tbtun"
if [[ ! -x "$TBTUN" ]]; then
  echo "missing $TBTUN" >&2
  ls -la "$CLI_OUT" >&2
  exit 1
fi
"$TBTUN" server --listen "127.0.0.1:${SERVER_PORT}" --target "127.0.0.1:${ECHO_PORT}" \
    --psk-file "$WORKDIR/psk.txt" --max-paths 4 --window 1024 >"$WORKDIR/server.log" 2>&1 &
SERVER_PID=$!
# Wait for the listener
for i in $(seq 1 100); do
  if grep -q "server on" "$WORKDIR/server.log" 2>/dev/null; then
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    echo "server died:" >&2
    cat "$WORKDIR/server.log" >&2
    exit 1
  fi
  sleep 0.05
done

echo "echo=$ECHO_PORT server=$SERVER_PORT"
set +e
"$OUT/tbt_e2e" 127.0.0.1 "$SERVER_PORT" "$PSK"
RC=$?
set -e
if [[ $RC -ne 0 ]]; then
  echo "===== server log =====" >&2
  cat "$WORKDIR/server.log" >&2 || true
fi
exit $RC
