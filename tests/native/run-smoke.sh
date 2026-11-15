#!/usr/bin/env bash
# Publish linux-x64 libtbt.so and run the socketpair C ABI smoke test.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${OUT:-$ROOT/artifacts/publish/Tbt.NativeLib/linux-x64}"
export DOTNET_ROOT="${DOTNET_ROOT:-/home/box/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

dotnet publish "$ROOT/src/Tbt.NativeLib/Tbt.NativeLib.csproj" \
    -c Release -r linux-x64 \
    -p:PublishAot=true -p:NativeLib=Shared \
    -o "$OUT"

# NativeAOT may emit tbt.so or libtbt.so depending on the pack.
if [[ -f "$OUT/libtbt.so" ]]; then
  SO="$OUT/libtbt.so"
elif [[ -f "$OUT/tbt.so" ]]; then
  ln -sfn tbt.so "$OUT/libtbt.so"
  SO="$OUT/libtbt.so"
else
  echo "no tbt shared library in $OUT" >&2
  ls -la "$OUT" >&2
  exit 1
fi

# Copy the header next to the test for a stable include.
cc -std=c11 -Wall -Wextra -O2 \
    -I"$ROOT/src/Tbt.NativeLib" \
    -o "$OUT/tbt_abi_smoke" \
    "$ROOT/tests/native/tbt_abi_smoke.c" \
    -L"$OUT" -ltbt -Wl,-rpath,"$OUT"

echo "=== readelf / nm exports ==="
(readelf -Ws "$SO" 2>/dev/null || llvm-readelf -Ws "$SO") | grep -E 'tbt_(start|stop|stats|set_protect)' || true
nm -D "$SO" | grep -E 'tbt_(start|stop|stats|set_protect)' || true

echo "=== run smoke ==="
"$OUT/tbt_abi_smoke"
