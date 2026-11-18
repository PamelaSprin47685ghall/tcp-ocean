#!/usr/bin/env bash
# P1 acceptance matrix: single-flow stability and multi-flow scaling.
#
# Reuses one prebuilt AOT/JIT tree through TBT_BIN (see run.sh) so every round
# skips the publish step. Run as root, like run.sh.
#
#   TBT_BIN=/tmp/tbt-bin tests/e2e/p1-matrix.sh [rounds] [seconds]
#
# Output:
#   $LOGDIR/goodput.txt   one "<case>\tiperf3 <args>: <mbit> Mbit/s" line per case
#   $LOGDIR/summary.txt   "<case> rc=<rc>"
#   stdout                single-flow mean/sd and the 3-flow total
#
# P1-5 (single-flow stability) reads the up-direction samples only: each round
# runs the same tunnel with paths=1, so "-P 1" and the plain case are the same
# measurement and are counted once.
set -u
repo=$(cd "$(dirname "$0")/../.." && pwd)
here="$repo/tests/e2e"
rounds=${1:-5}
seconds=${2:-15}
logdir=${LOGDIR:-/tmp/tbt-p1}
mkdir -p "$logdir"
: > "$logdir/goodput.txt"
: > "$logdir/summary.txt"

if [[ -z ${TBT_BIN:-} ]]; then
    echo "TBT_BIN must point at a directory holding aot/ and jit/ from an earlier run" >&2
    exit 2
fi

run_case() {
    local name=$1 paths=$2
    TBTUN_ASYNC_MODE=${TBTUN_ASYNC_MODE:-true} ALLOW_DIRTY=1 HOST_QUIET_BYPASS=1 \
        timeout $((seconds * 8 + 300)) "$here/run.sh" 20 200 0 "$paths" "$seconds" \
        > "$logdir/$name.log" 2>&1
    local rc=$?
    printf '%s rc=%s\n' "$name" "$rc" >> "$logdir/summary.txt"
    grep -E '^iperf3 ' "$logdir/$name.log" | sed "s/^/$name\t/" >> "$logdir/goodput.txt"
    grep -E 'session_up\(mux\)|negotiated paths|iperf3 |FAILED' "$logdir/$name.log" \
        | sed "s/^/$name: /" | tail -8
}

for i in $(seq 1 "$rounds"); do run_case "single_$i" 1; done
run_case "three" 3

python3 - "$logdir" <<'PY'
import pathlib
import re
import statistics
import sys

root = pathlib.Path(sys.argv[1])
up, down, three = [], [], []
for line in (root / "goodput.txt").read_text().splitlines():
    m = re.match(r"(single_\d+|three)\tiperf3 (.*?): ([0-9.]+) Mbit/s", line)
    if not m:
        continue
    case, args, value = m.group(1), m.group(2).strip(), float(m.group(3))
    if case == "three":
        three.append((args, value))
    elif args == "up":
        up.append(value)
    elif args == "-R":
        down.append(value)

def report(label, values):
    if not values:
        print(f"{label}: no samples")
        return None
    mean = statistics.fmean(values)
    sd = statistics.pstdev(values)
    print(f"{label}: n={len(values)} mean={mean:.2f} Mbit/s sd={sd:.2f} "
          f"({100.0 * sd / mean:.1f}%) min={min(values):.2f} max={max(values):.2f}")
    return mean

up_mean = report("single-flow up", up)
report("single-flow down", down)
parallel = [v for a, v in three if a == "-P 3"]
three_total = statistics.fmean(parallel) if parallel else None
if three_total is not None:
    print(f"3-flow up: {three_total:.2f} Mbit/s")
if up_mean and three_total is not None:
    print(f"3-flow / single-flow = {three_total / up_mean:.2f}x (P1-6 target >= 1.5x)")
if up and statistics.pstdev(up) / statistics.fmean(up) < 0.20:
    print("P1-5 target: standard deviation below 20%")
else:
    print("P1-5 target: standard deviation at or above 20%")
PY
