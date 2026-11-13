#!/usr/bin/env bash
# Short calibration of the rtt400 profile. No tbtun, no campaign.
# Checks: ping RTT ≈ 400 ms; one plain TCP flow ≈ 4 Mbit/s; two flows ≈ 8 Mbit/s.
# Holds /tmp/tbtun-netns.lock for the whole check.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
exec 9>/tmp/tbtun-netns.lock
if ! flock -n 9; then
    echo "waiting for /tmp/tbtun-netns.lock" >&2
    flock 9
fi
export TBTUN_NETNS_LOCK_HELD=1
if [[ $(sudo python3 "$here/tb_guard.py" status) == owned-run ]]; then
    echo "recovering leftover owned testbed"
    sudo TBTUN_NETNS_LOCK_HELD=1 "$here/netns.sh" down
fi
if ip netns list | grep -q '^tbe_'; then
    echo "refuse: tbe_* namespaces exist" >&2
    exit 2
fi
work=$(mktemp -d /tmp/tbtun-e2e.cal.XXXXXX)
cleanup() {
    if [[ -f $work/iperf.pid ]]; then sudo kill "$(cat "$work/iperf.pid")" 2>/dev/null || true; fi
    sudo TBTUN_NETNS_LOCK_HELD=1 "$here/netns.sh" down "$work" || true
}
trap cleanup EXIT
cp "$here/profiles/rtt400.conf" "$work/wanem.conf"
sudo TBTUN_NETNS_LOCK_HELD=1 "$here/netns.sh" up "$work"
echo "wanem: $(tr '\n' ' ' < "$work/wanem.log" | tail -c 400)"
echo "--- ping (20 packets) ---"
ping_out=$(sudo ip netns exec tbe_near ping -c 20 -i 0.2 10.200.2.2)
echo "$ping_out" | tail -2
rtt=$(echo "$ping_out" | awk -F'/' '/^rtt/ {print $5}')
echo "rtt_avg_ms=$rtt (target 400 ± 10)"
# One long-lived iperf3 server in far (not -1: we run two tests).
sudo ip netns exec tbe_far bash -c 'iperf3 -s -p 5201 </dev/null >/dev/null 2>&1 & echo $! > "'"$work"'/iperf.pid"'
sleep 0.3
# -O 4 drops the 400 ms slow-start from the average. Each test is 4+8 s.
echo "--- one flow, 8 s (omit 4 s) ---"
one=$(sudo ip netns exec tbe_near iperf3 -c 10.200.2.2 -p 5201 -O 4 -t 8 -J) \
    || { echo "iperf3 one-flow failed"; echo "$one"; exit 1; }
printf '%s' "$one" > "$work/one.json"
one_m=$(python3 -c 'import json,sys; print("%.3f" % (json.load(sys.stdin)["end"]["sum_received"]["bits_per_second"]/1e6))' <"$work/one.json")
echo "one_flow_mbit=$one_m (target about 4.0, accept 3.5-4.3)"
echo "--- two flows, 8 s (omit 4 s) ---"
two=$(sudo ip netns exec tbe_near iperf3 -c 10.200.2.2 -p 5201 -O 4 -t 8 -P 2 -J) \
    || { echo "iperf3 two-flow failed"; echo "$two"; exit 1; }
printf '%s' "$two" > "$work/two.json"
python3 - "$work" "$rtt" << 'PY'
import json, sys
work, rtt_s = sys.argv[1], sys.argv[2]
rtt = float(rtt_s)
one = json.load(open(work + "/one.json"))["end"]["sum_received"]["bits_per_second"] / 1e6
two = json.load(open(work + "/two.json"))
streams = []
for s in two["end"]["streams"]:
    rec = s.get("receiver") or s
    streams.append(rec["bits_per_second"] / 1e6)
total = two["end"]["sum_received"]["bits_per_second"] / 1e6
print("two_flow_mbit=" + " ".join("%.3f" % x for x in streams) + " sum=%.3f (target about 4+4=8, accept 7.0-8.6)" % total)
ok = True
if abs(rtt - 400) > 10:
    print("FAIL: RTT %.1f ms not in 390-410" % rtt); ok = False
else:
    print("PASS: RTT %.1f ms" % rtt)
if not (3.5 <= one <= 4.3):
    print("FAIL: one flow %.3f Mbit/s not in 3.5-4.3" % one); ok = False
else:
    print("PASS: one flow %.3f Mbit/s" % one)
if not (7.0 <= total <= 8.6):
    print("FAIL: two-flow sum %.3f Mbit/s not in 7.0-8.6" % total); ok = False
else:
    print("PASS: two-flow sum %.3f Mbit/s" % total)
if streams and (min(streams) < 2.8 or max(streams) > 5.0):
    print("FAIL: per-flow split looks like a shared pipe, not per-tuple: %s" % streams); ok = False
sys.exit(0 if ok else 1)
PY
echo "logs: $work"
