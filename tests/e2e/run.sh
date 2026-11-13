#!/usr/bin/env bash
# End-to-end run on network namespaces (netns.sh) with real traffic:
#   1. one session: echo checks (every byte checked), echo latency, bulk up/down, latency
#      under bulk, back to back;
#   2. one session: iperf3 up, down, parallel up, parallel down, up again, back to back.
# The server runs as the NativeAOT binary, the client on the JIT runtime.
# Reports server peak RSS (VmHWM), server CPU seconds, goodput and echo latency.
#   run.sh [owd_ms] [rate_mbit] [loss] [paths] [seconds]
# Optional env: WINDOW (default 1024), WINDOW_CLAMP (default 600000; --window-clamp on both sides),
#   MAX_PATHS (default 64; server --max-paths), TBT_BIN=dir (reuse prebuilt aot/ and jit/ under dir),
#   SEED (default 7), CARRIER=tun|kernel, ALLOW_DIRTY=1, SKIP_ECHO=1, SKIP_IPERF=1,
#   SKIP_REF=1 (skip plain-TCP iperf/ping baselines), PING_COUNT (default 200),
#   HANDSHAKE_S (default 10; raise for large path counts at long RTT).
# Needs sudo, gcc, python3, iperf3, .NET 10 SDK. Logs go to a temporary directory.
#
# Concurrent workers serialize on /tmp/tbtun-netns.lock (flock, held for the whole run,
# including cleanup). Do not start a run while another worker already holds the lock;
# wait, do not delete its namespaces.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
profile=${PROFILE:-}
if [[ $profile == rtt400 ]]; then
    owd=${1:-200} rate=${2:-1000} loss=${3:-0} paths=${4:-4} seconds=${5:-10}
    if [[ -z ${WANEM_EXTRA:-} ]]; then
        WANEM_EXTRA="tuple_mbit=4\ntuple_bucket_bytes=32000"
    fi
else
    owd=${1:-20} rate=${2:-200} loss=${3:-0} paths=${4:-4} seconds=${5:-10}
fi
window=${WINDOW:-1024}
window_clamp=${WINDOW_CLAMP:-600000}
max_paths=${MAX_PATHS:-64}
seed=${SEED:-7}
ping_count=${PING_COUNT:-200}
handshake_s=${HANDSHAKE_S:-10}
# CARRIER=kernel: server paths on kernel TCP; CARRIER=tun: user-space TCP on tbt0 (default).
carrier=${CARRIER:-tun}
if [[ $carrier == tun ]]; then
    server_at=10.200.3.2:8443
    server_args=(--tun tbt0 --tun-listen "$server_at")
else
    server_at=10.200.2.2:8443
    server_args=(--listen "$server_at")
fi

# Hold the global lock before preflight or any namespace work, and until EXIT cleanup ends.
# If the caller already holds it (and inherited that fact via TBTUN_NETNS_LOCK_HELD), do not
# reopen fd 9: `exec 9>` would drop the inherited lock and then wait on the caller forever.
# The env flag is not enough: it can leak from a parent shell that does not actually hold
# the lock. Only skip acquire when fd 9 is already an exclusive flock on the lock file.
holds_netns_lock() {
    local lock target
    lock=$(readlink -f /tmp/tbtun-netns.lock 2>/dev/null) || return 1
    [[ -e /proc/self/fd/9 ]] || return 1
    target=$(readlink -f /proc/self/fd/9 2>/dev/null) || return 1
    [[ $target == "$lock" ]] || return 1
    flock -n 9
}
if ! holds_netns_lock; then
    if [[ ${TBTUN_NETNS_LOCK_HELD:-0} == 1 ]]; then
        echo "warning: TBTUN_NETNS_LOCK_HELD=1 but fd 9 is not a held lock on /tmp/tbtun-netns.lock; acquiring it ourselves" >&2
    fi
    exec 9>/tmp/tbtun-netns.lock
    if ! flock -n 9; then
        echo "waiting for /tmp/tbtun-netns.lock" >&2
        flock 9
    fi
    export TBTUN_NETNS_LOCK_HELD=1
fi

# Poka-yoke: a clean machine and a known commit, or no run.
preflight() {
    local bad=0
    # Recover a previous run that died after creating an owned testbed. down verifies
    # identity; it will refuse (and we then refuse) if the namespaces are not ours.
    if [[ $(sudo python3 "$here/tb_guard.py" status) == owned-run ]]; then
        echo "recovering leftover owned testbed"
        sudo TBTUN_NETNS_LOCK_HELD=1 "$here/netns.sh" down || { echo "refuse: leftover owned testbed could not be cleaned"; bad=1; }
    fi
    if ip netns list | grep -q '^tbe_'; then echo "refuse: tbe_* network namespaces exist: $(ip netns list | grep '^tbe_' | tr '\n' ' ')"; bad=1; fi
    # Only our own resources: the box is shared with other agents, so other processes
    # (even an echo server) are not ours to judge. Ours are the tbe_* namespaces (checked
    # above) and processes started from an e2e work dir or the /tmp/dbg debug tree.
    local procs
    procs=$(ps -eo pid=,args= | awk '/\/tmp\/tbtun-e2e\./ || /\/tmp\/dbg\/.*tbtun/' | grep -v -e awk -e "^ *$$ " || true)
    if [[ -n $procs ]]; then echo "refuse: test processes still run:"; echo "$procs"; bad=1; fi
    if [[ -n $(git -C "$repo" status --porcelain) && ${ALLOW_DIRTY:-0} != 1 ]]; then
        echo "refuse: the work tree has changes (ALLOW_DIRTY=1 runs anyway)"; bad=1
    fi
    (( bad == 0 )) || exit 2
}
preflight
commit=$(git -C "$repo" rev-parse --short HEAD)
[[ -n $(git -C "$repo" status --porcelain) ]] && commit="$commit-dirty"
work=$(mktemp -d /tmp/tbtun-e2e.XXXXXX)
stamp() { echo "$*" | tee -a "$work/stamp.txt"; }
stamp "stamp: commit=$commit owd_ms=$owd rate_mbit=$rate loss=$loss paths=$paths window=$window window_clamp=$window_clamp seed=$seed server_max_paths=$max_paths seconds=$seconds carrier=$carrier profile=${profile:-none}"
if (( paths > max_paths )); then
    echo "warning: requested paths=$paths exceeds server --max-paths=$max_paths (default 64); handshake will negotiate at most $max_paths, and the run will fail the negotiated==requested check. Set MAX_PATHS."
fi
user=$(id -un)
dotnet_root=${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}
in_ns() { local ns=$1; shift; sudo ip netns exec "tbe_$ns" sudo -u "$user" env DOTNET_ROOT="$dotnet_root" TBT_TRACE="${TBT_TRACE:-}" PING_TRACE="${PING_TRACE:-}" ECHO_PROBE_LOG="${ECHO_PROBE_LOG:-}" "$@"; }

# Processes whose executable is under the work directory (or iperf3/python3 started here).
pids_of() {
    local want want_real exe
    want=$1
    want_real=$(readlink -f "$want" 2>/dev/null || echo "$want")
    for p in /proc/[0-9]*; do
        exe=$(sudo readlink "$p/exe" 2>/dev/null) || continue
        if [[ $exe == "$want" || $exe == "$want_real" ]]; then echo "${p#/proc/}"; fi
    done
    true
}
cleanup() {
    for exe in "$work/aot/tbtun" "$work/jit/tbtun"; do pids_of "$exe" | xargs -r sudo kill 2>/dev/null || true; done
    # Do not kill by the fixed namespace names. netns.sh down verifies ownership, then
    # signals leftover processes in the recorded namespaces only, then deletes them.
    sudo TBTUN_NETNS_LOCK_HELD=1 "$here/netns.sh" down "$work"
}
trap cleanup EXIT

if [[ -n ${TBT_BIN:-} ]]; then
    ln -s "$(cd "$TBT_BIN/aot" && pwd)" "$work/aot"
    ln -s "$(cd "$TBT_BIN/jit" && pwd)" "$work/jit"
else
    dotnet publish "$repo/src/Tbt.Cli" -c Release -r linux-x64 -o "$work/aot" >"$work/publish.log"
    dotnet build "$repo/src/Tbt.Cli" -c Release -o "$work/jit" >"$work/build.log"
fi
cp "$here/echo.py" "$work/echo.py"
python3 -c 'import os; print(os.urandom(32).hex())' > "$work/psk"
printf 'owd_ms=%s\nrate_mbit=%s\nloss=%s\nseed=%s\n' "$owd" "$rate" "$loss" "$seed" > "$work/wanem.conf"
if [ -n "${WANEM_EXTRA:-}" ]; then printf "%b\n" "$WANEM_EXTRA" >> "$work/wanem.conf"; fi   # e.g. WANEM_EXTRA="outage_ms=300\noutage_every_s=3"
sudo TBTUN_NETNS_LOCK_HELD=1 "$here/netns.sh" up "$work"
echo "link: owd ${owd} ms each way, ${rate} Mbit/s, segment loss ${loss}; paths ${paths}; window ${window}; window_clamp ${window_clamp}; seed ${seed}; server_max_paths ${max_paths}; server carrier ${carrier}"

in_ns far python3 "$work/echo.py" serve 7000 &
in_ns far iperf3 -s -p 5201 >/dev/null 2>&1 &
sleep 0.5

# One tunnel = one session; each session runs several cases back to back.
tunnel() {
    local name=$1 target=$2
    in_ns far "$work/aot/tbtun" server "${server_args[@]}" --target "$target" --psk-file "$work/psk" --stats-s 1 \
        --window "$window" --window-clamp "$window_clamp" --max-paths "$max_paths" \
        2>"$work/server-$name.log" &
    sleep 0.5
    in_ns near "$work/jit/tbtun" client --server "$server_at" --listen 127.0.0.1:1080 --psk-file "$work/psk" --stats-s 1 \
        --paths "$paths" --window "$window" --window-clamp "$window_clamp" --stats-s 1 2>"$work/client-$name.log" &
    # Under loss a lost handshake segment costs a TCP retransmit timeout (1 s and more).
    # Sequential JOINs at long RTT can exceed the default 10 s; raise HANDSHAKE_S.
    local i t0 t1
    t0=$(date +%s%N)
    for i in $(seq $((handshake_s * 5))); do grep -q "session up" "$work/client-$name.log" && break; sleep 0.2; done
    t1=$(date +%s%N)
    stamp "handshake($name): $(awk -v t0="$t0" -v t1="$t1" 'BEGIN{printf "%.2f", (t1-t0)/1e9}')s (limit ${handshake_s}s)"
    grep -q "session up" "$work/client-$name.log" || { echo "handshake failed in ${handshake_s} s"; cat "$work/client-$name.log"; exit 1; }
    # Client logs "session up: S=... paths=N". Window is a local --window on both sides
    # (not a distinct handshake field besides SERVER_INIT rx_win_grant0 = server WindowSteps).
    local up_line neg_paths
    up_line=$(grep "session up" "$work/client-$name.log" | tail -1)
    neg_paths=${up_line##*paths=}
    neg_paths=${neg_paths%%[^0-9]*}
    stamp "negotiated($name): paths=$neg_paths window=$window window_clamp=$window_clamp (requested paths=$paths window=$window window_clamp=$window_clamp; server_max_paths=$max_paths)"
    if [[ "$neg_paths" != "$paths" ]]; then
        echo "FAILED: negotiated paths=$neg_paths != requested $paths (server --max-paths default 64; set MAX_PATHS if you asked for more than the server allows)"
        status=1
        return 1
    fi
}
server_pid() { pids_of "$work/aot/tbtun" | head -1; }
report() {
    local pid; pid=$(server_pid)
    if [[ -z $pid ]]; then echo "server($1): pid not found"; return 0; fi
    local hwm rss cpu
    hwm=$(sudo awk '/VmHWM/ {print $2}' "/proc/$pid/status")
    rss=$(sudo awk '/VmRSS/ {print $2}' "/proc/$pid/status")
    cpu=$(sudo awk -v hz="$(getconf CLK_TCK)" '{printf "%.2f", ($14 + $15) / hz}' "/proc/$pid/stat")
    echo "server($1): rss_kib=$rss peak_rss_kib=$hwm cpu_s=$cpu"
}
# stop <name>: SIGTERM the client, which closes the session (SESSION_CLOSE, spec 6.10a);
# the server must log the close by the peer before it is stopped.
stop() {
    pids_of "$work/jit/tbtun" | xargs -r sudo kill
    for _ in $(seq 30); do [[ -z $(pids_of "$work/jit/tbtun") ]] && break; sleep 0.1; done
    sleep 0.3
    if grep -q "closed by peer" "$work/server-$1.log"; then echo "session close($1): ok"
    else echo "FAILED: session close($1): the server did not log a close by the peer"; status=1; fi
    pids_of "$work/aot/tbtun" | xargs -r sudo kill; sleep 0.5
}
goodput() { python3 -c 'import json,sys; d=json.load(sys.stdin); print("%.1f" % (d["end"]["sum_received"]["bits_per_second"]/1e6))'; }

status=0
if [[ ${SKIP_REF:-0} != 1 ]]; then
echo "reference, plain TCP without tunnel: 1 flow $(in_ns near iperf3 -c 10.200.2.2 -p 5201 -t "$seconds" -J | goodput) Mbit/s, $paths flows $(in_ns near iperf3 -c 10.200.2.2 -p 5201 -t "$seconds" -P "$paths" -J | goodput) Mbit/s"
echo "reference, plain TCP without tunnel: $(in_ns near python3 "$work/echo.py" ping 10.200.2.2 7000 100)"
fi

# Session 1: every echo/latency case back to back on one session, so that a flow left
# stalled by an earlier case shows up as a failure of the later ones.
if [[ ${SKIP_ECHO:-0} != 1 ]]; then
tunnel mux 10.200.2.2:7000
run_case() { local rc=0; "$@" || rc=$?; if (( rc )); then echo "FAILED (exit $rc$( (( rc == 124 )) && echo ", timed out")): ${*:6}"; status=1; fi; }
run_case in_ns near timeout 120 python3 -u "$work/echo.py" check 127.0.0.1 1080 8 1000000
run_case in_ns near timeout 120 python3 -u "$work/echo.py" check 127.0.0.1 1080 64 20000
run_case in_ns near timeout $(( 90 + (2 * owd + 20) * ping_count / 1000 )) python3 -u "$work/echo.py" ping 127.0.0.1 1080 "$ping_count"
run_case in_ns near timeout $(( seconds + 90 )) python3 -u "$work/echo.py" bulk 127.0.0.1 1080 up "$seconds"
run_case in_ns near timeout $(( seconds + 90 )) python3 -u "$work/echo.py" bulk 127.0.0.1 1080 down "$seconds"
# pingbulk keeps bulk running until every probe finishes; allow RTT + 20 ms spacing * count, plus margin.
run_case in_ns near timeout $(( 90 + (2 * owd + 20) * ping_count / 1000 )) python3 -u "$work/echo.py" pingbulk 127.0.0.1 1080 "$ping_count" "$seconds"
run_case in_ns near timeout 120 python3 -u "$work/echo.py" check 127.0.0.1 1080 8 1000000
report mux; stop mux
fi

if [[ ${SKIP_IPERF:-0} != 1 ]]; then
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
report bulk; stop bulk
fi

echo "wanem: $(tr '\n' ' ' < "$work/wanem.stats")"
# Kernel TCP counters from both namespaces, while they still exist.
if ip netns list | grep -q '^tbe_near'; then
    sudo ip netns exec tbe_near nstat -az > "$work/nstat-near.txt" 2>/dev/null || true
    sudo ip netns exec tbe_far  nstat -az > "$work/nstat-far.txt" 2>/dev/null || true
    sudo ip netns exec tbe_near ss -tiemn > "$work/ss-near.txt" 2>/dev/null || true
    sudo ip netns exec tbe_far  ss -tiemn > "$work/ss-far.txt" 2>/dev/null || true
    python3 - "$work" <<'PY'
import re
import sys
from pathlib import Path
work = Path(sys.argv[1])
want = [
    "TcpInSegs", "TcpOutSegs", "TcpRetransSegs", "TcpInErrs",
    "TcpExtTCPLostRetransmit", "TcpExtTCPRetransFail", "TcpExtTCPTimeouts",
    "TcpExtTCPSynRetrans", "TcpExtTCPSackRecovery", "TcpExtTCPFastRetrans",
    "TcpExtTCPSlowStartRetrans", "TcpExtTCPLossProbes", "TcpExtTCPLossProbeRecovery",
    "TcpExtTCPDSACKRecv", "TcpExtTCPDSACKOfoRecv", "TcpExtTCPAbortOnTimeout",
    "TcpExtTCPSpuriousRTOs", "TcpExtListenOverflows", "TcpExtListenDrops",
    "TcpExtTCPRcvCoalesce", "TcpExtTCPBacklogDrop",
]
def load(path):
    vals = {}
    if not path.exists():
        return vals
    for line in path.read_text(errors="replace").splitlines():
        parts = line.split()
        if len(parts) >= 2 and parts[0] in want:
            vals[parts[0]] = parts[1]
    return vals
for label in ("near", "far"):
    vals = load(work / f"nstat-{label}.txt")
    bits = " ".join(f"{k}={vals[k]}" for k in want if k in vals)
    print(f"kernel_{label}: {bits or 'no-nstat'}")
def ss_retrans(path, label):
    text = path.read_text(errors="replace") if path.exists() else ""
    sockets = 0
    retr = 0
    unacked = 0
    lost_n = 0
    for m in re.finditer(r"retrans:(\d+)/(\d+)", text):
        sockets += 1
        retr += int(m.group(2))
    for m in re.finditer(r"unacked:(\d+)", text):
        unacked += int(m.group(1))
    for m in re.finditer(r" lost:(\d+)", text):
        lost_n += int(m.group(1))
    print(f"ss_{label}: sockets_with_retrans_field={sockets} retrans_sum={retr} unacked_sum={unacked} lost_sum={lost_n}")
ss_retrans(work / "ss-near.txt", "near")
ss_retrans(work / "ss-far.txt", "far")
PY
fi
stamp "stamp: commit=$commit owd_ms=$owd rate_mbit=$rate loss=$loss paths=$paths window=$window window_clamp=$window_clamp seed=$seed server_max_paths=$max_paths seconds=$seconds carrier=$carrier profile=${profile:-none} ping_count=$ping_count handshake_s=$handshake_s status=$status"
echo "logs: $work"
exit $status
