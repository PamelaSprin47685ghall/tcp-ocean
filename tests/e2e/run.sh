#!/usr/bin/env bash
# End-to-end run on network namespaces (netns.sh) with real traffic:
#   1. one session: echo checks (every byte checked), echo latency, bulk up/down, latency
#      under bulk, back to back;
#   2. one session: iperf3 up, down, parallel up, parallel down, up again, back to back.
# The server runs as the NativeAOT binary, the client on the JIT runtime.
# Reports server peak RSS (VmHWM), server CPU seconds, goodput and echo latency.
#   run.sh [owd_ms] [rate_mbit] [loss] [paths] [seconds]
# Needs sudo, gcc, python3, iperf3, .NET 10 SDK. Logs go to a temporary directory.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
owd=${1:-20} rate=${2:-200} loss=${3:-0} paths=${4:-4} seconds=${5:-10}
# CARRIER=kernel: server paths on kernel TCP; CARRIER=tun: user-space TCP on tbt0 (default).
carrier=${CARRIER:-tun}
if [[ $carrier == tun ]]; then
    server_at=10.200.3.2:8443
    server_args=(--tun tbt0 --tun-listen "$server_at")
else
    server_at=10.200.2.2:8443
    server_args=(--listen "$server_at")
fi
# Poka-yoke: a clean machine and a known commit, or no run.
preflight() {
    local bad=0
    if ip netns list | grep -q '^tbe_'; then echo "refuse: tbe_* network namespaces exist: $(ip netns list | grep '^tbe_' | tr '\n' ' ')"; bad=1; fi
    # Executables named tbtun/wanem/iperf3, the .NET tbtun.dll, or echo.py servers.
    local procs
    procs=$(ps -eo pid=,comm=,args= | awk '$2 == "tbtun" || $2 == "wanem" || $2 == "iperf3" || /tbtun\.dll/ || /echo\.py/' | grep -v -e awk -e "^ *$$ " || true)
    if [[ -n $procs ]]; then echo "refuse: test processes still run:"; echo "$procs"; bad=1; fi
    if [[ -n $(git -C "$repo" status --porcelain) && ${ALLOW_DIRTY:-0} != 1 ]]; then
        echo "refuse: the work tree has changes (ALLOW_DIRTY=1 runs anyway)"; bad=1
    fi
    (( bad == 0 )) || exit 2
}
preflight
commit=$(git -C "$repo" rev-parse --short HEAD)
[[ -n $(git -C "$repo" status --porcelain) ]] && commit="$commit-dirty"
echo "commit: $commit"
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
echo "link: owd ${owd} ms each way, ${rate} Mbit/s, segment loss ${loss}; paths ${paths}; server carrier ${carrier}"

in_ns far python3 "$work/echo.py" serve 7000 &
in_ns far iperf3 -s -p 5201 >/dev/null 2>&1 &
sleep 0.5

# One tunnel = one session; each session runs several cases back to back.
tunnel() {
    local name=$1 target=$2
    in_ns far "$work/aot/tbtun" server "${server_args[@]}" --target "$target" --psk-file "$work/psk" --stats-s 1 \
        2>"$work/server-$name.log" &
    sleep 0.5
    in_ns near "$work/jit/tbtun" client --server "$server_at" --listen 127.0.0.1:1080 --psk-file "$work/psk" --stats-s 1 \
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
echo "reference, plain TCP without tunnel: $(in_ns near python3 "$work/echo.py" ping 10.200.2.2 7000 100)"

# Session 1: every echo/latency case back to back on one session, so that a flow left
# stalled by an earlier case shows up as a failure of the later ones.
if [[ ${SKIP_ECHO:-0} != 1 ]]; then
tunnel mux 10.200.2.2:7000
run_case() { local rc=0; "$@" || rc=$?; if (( rc )); then echo "FAILED (exit $rc$( (( rc == 124 )) && echo ", timed out")): ${*:6}"; status=1; fi; }
run_case in_ns near timeout 120 python3 -u "$work/echo.py" check 127.0.0.1 1080 8 1000000
run_case in_ns near timeout 120 python3 -u "$work/echo.py" check 127.0.0.1 1080 64 20000
run_case in_ns near timeout 120 python3 -u "$work/echo.py" ping 127.0.0.1 1080 200
run_case in_ns near timeout 120 python3 -u "$work/echo.py" bulk 127.0.0.1 1080 up "$seconds"
run_case in_ns near timeout 120 python3 -u "$work/echo.py" bulk 127.0.0.1 1080 down "$seconds"
run_case in_ns near timeout 120 python3 -u "$work/echo.py" pingbulk 127.0.0.1 1080 200 "$seconds"
run_case in_ns near timeout 120 python3 -u "$work/echo.py" check 127.0.0.1 1080 8 1000000
report mux; stop
fi

# Session 2: iperf3 cases back to back on one session (control flow idles while data runs).
tunnel bulk 10.200.2.2:5201
for args in "" "-R" "-P $paths" "-R -P $paths" ""; do
    # shellcheck disable=SC2086
    sleep 2  # iperf3 -s takes one test at a time: let the last one finish on the far side
    if json=$(timeout $((seconds + 30)) sudo ip netns exec tbe_near iperf3 -c 127.0.0.1 -p 1080 -t "$seconds" $args -J) \
        && out=$(goodput <<<"$json"); then
        echo "iperf3 ${args:-up}: $out Mbit/s"
    else
        echo "FAILED: iperf3 ${args:-up}: $(python3 -c 'import json,sys; print(json.load(sys.stdin).get("error","no result"))' <<<"$json" 2>/dev/null)"
        status=1
    fi
done
report bulk; stop

echo "wanem: $(tr '\n' ' ' < "$work/wanem.stats")"
echo "commit: $commit"
echo "logs: $work"
exit $status
