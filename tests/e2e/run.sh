#!/usr/bin/env bash
# End-to-end run on network namespaces (netns.sh) with real traffic:
#   1. echo: FLOWS parallel connections through the tunnel, every byte checked;
#   2. bulk: iperf3 upload and download through the tunnel.
# The server runs as the NativeAOT binary, the client on the JIT runtime.
# Reports server peak RSS (VmHWM), server CPU seconds and iperf3 goodput.
#   run.sh [owd_ms] [rate_mbit] [loss] [paths] [seconds]
# Needs sudo, gcc, python3, iperf3, .NET 10 SDK. Logs go to a temporary directory.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
owd=${1:-20} rate=${2:-200} loss=${3:-0} paths=${4:-4} seconds=${5:-10}
work=$(mktemp -d /tmp/tbtun-e2e.XXXXXX)
user=$(id -un)
dotnet_root=${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}
in_ns() { local ns=$1; shift; sudo ip netns exec "tbe_$ns" sudo -u "$user" env DOTNET_ROOT="$dotnet_root" "$@"; }

# Processes whose executable is under the work directory (or iperf3/python3 started here).
pids_of() { for p in /proc/[0-9]*; do [[ $(sudo readlink "$p/exe" 2>/dev/null) == "$1" ]] && echo "${p#/proc/}"; done; true; }
cleanup() {
    for exe in "$work/aot/tbtun" "$work/jit/tbtun"; do pids_of "$exe" | xargs -r sudo kill 2>/dev/null || true; done
    sudo ip netns pids tbe_far 2>/dev/null | xargs -r sudo kill 2>/dev/null || true
    sudo ip netns pids tbe_near 2>/dev/null | xargs -r sudo kill 2>/dev/null || true
    sudo "$here/netns.sh" down "$work"
}
trap cleanup EXIT

dotnet publish "$repo/src/Tbt.Cli" -c Release -r linux-x64 -o "$work/aot" >"$work/publish.log"
dotnet build "$repo/src/Tbt.Cli" -c Release -o "$work/jit" >"$work/build.log"
cp "$here/echo.py" "$work/echo.py"
python3 -c 'import os; print(os.urandom(32).hex())' > "$work/psk"
printf 'owd_ms=%s\nrate_mbit=%s\nloss=%s\nseed=7\n' "$owd" "$rate" "$loss" > "$work/wanem.conf"
sudo "$here/netns.sh" up "$work"
echo "link: owd ${owd} ms each way, ${rate} Mbit/s, segment loss ${loss}; paths ${paths}"

in_ns far python3 "$work/echo.py" serve 7000 &
in_ns far iperf3 -s -p 5201 >/dev/null 2>&1 &
sleep 0.5

# One tunnel per test, so that each measurement starts from a fresh session.
tunnel() {
    local name=$1 target=$2
    in_ns far "$work/aot/tbtun" server --listen 10.200.2.2:8443 --target "$target" --psk-file "$work/psk" --stats-s 1 \
        2>"$work/server-$name.log" &
    sleep 0.5
    in_ns near "$work/jit/tbtun" client --server 10.200.2.2:8443 --listen 127.0.0.1:1080 --psk-file "$work/psk" --stats-s 1 \
        --paths "$paths" --stats-s 1 2>"$work/client-$name.log" &
    sleep 2
    grep -q "session up" "$work/client-$name.log" || { echo "handshake failed"; cat "$work/client-$name.log"; exit 1; }
}
server_pid() { pids_of "$work/aot/tbtun" | head -1; }
report() {
    local pid; pid=$(server_pid)
    local hwm rss cpu
    hwm=$(sudo awk '/VmHWM/ {print $2}' "/proc/$pid/status")
    rss=$(sudo awk '/VmRSS/ {print $2}' "/proc/$pid/status")
    cpu=$(sudo awk -v hz="$(getconf CLK_TCK)" '{printf "%.2f", ($14 + $15) / hz}' "/proc/$pid/stat")
    echo "server($1): rss_kib=$rss peak_rss_kib=$hwm cpu_s=$cpu"
}
stop() { for exe in "$work/jit/tbtun" "$work/aot/tbtun"; do pids_of "$exe" | xargs -r sudo kill; done; sleep 0.5; }
goodput() { python3 -c 'import json,sys; d=json.load(sys.stdin); print("%.1f" % (d["end"]["sum_received"]["bits_per_second"]/1e6))'; }

status=0
echo "reference, plain TCP without tunnel: 1 flow $(in_ns near iperf3 -c 10.200.2.2 -p 5201 -t "$seconds" -J | goodput) Mbit/s, $paths flows $(in_ns near iperf3 -c 10.200.2.2 -p 5201 -t "$seconds" -P "$paths" -J | goodput) Mbit/s"
if [[ ${SKIP_ECHO:-0} != 1 ]]; then
tunnel echo 10.200.2.2:7000
in_ns near python3 "$work/echo.py" check 127.0.0.1 1080 8 1000000 || status=1
in_ns near python3 "$work/echo.py" check 127.0.0.1 1080 64 20000 || status=1
report echo; stop
fi

tunnel up 10.200.2.2:5201
echo "bulk upload: $(in_ns near iperf3 -c 127.0.0.1 -p 1080 -t "$seconds" -J | goodput) Mbit/s"
report upload; stop

tunnel down 10.200.2.2:5201
echo "bulk download: $(in_ns near iperf3 -c 127.0.0.1 -p 1080 -t "$seconds" -R -J | goodput) Mbit/s"
report download; stop

echo "wanem: $(tr '\n' ' ' < "$work/wanem.stats")"
echo "logs: $work"
exit $status
