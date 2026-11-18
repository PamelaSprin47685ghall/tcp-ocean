#!/bin/sh
# Restart the tbt4 mihomo when its vless inbound stops reading tbtun.
#
# The failure mode this guards against: mihomo stays alive and keeps listening
# on 127.0.0.1:18443, but it stops reading the bytes tbtun sends.  `Recv-Q`
# grows on the mihomo side and `Send-Q` grows on the tbtun side, the control
# plane may still answer 204, and bulk throughput collapses to zero.
# `Restart=always` does not help: the process never exits.
#
# Usage: mihomo-watchdog.sh <mihomo-unit>
# Env:   MIHOMO_WATCHDOG_BYTES   backlog threshold, default 262144 (256 KiB)
set -eu

unit="${1:-mihomo-hnd0tbt4}"
limit="${MIHOMO_WATCHDOG_BYTES:-262144}"
stamp="/run/mihomo-watchdog.${unit}.stamp"
now=$(date +%s)

# Do not restart more often than every 120 s, even if the backlog rebuilds.
if [ -f "$stamp" ]; then
    last=$(cat "$stamp" 2>/dev/null || echo 0)
    case "$last" in '' | *[!0-9]*) last=0 ;; esac
    if [ $((now - last)) -lt 120 ]; then
        exit 0
    fi
fi

# ss -tn columns: State Recv-Q Send-Q Local Peer.
# local 18443 / ESTAB  -> Recv-Q is what mihomo has not read.
# peer  18443 / ESTAB  -> Send-Q is what tbtun could not hand to the kernel.
recv=$(ss -tn 2>/dev/null | awk '$1 == "ESTAB" && $4 ~ /:18443$/ { s += $2 } END { print s + 0 }')
send=$(ss -tn 2>/dev/null | awk '$1 == "ESTAB" && $5 ~ /:18443$/ { s += $3 } END { print s + 0 }')

if [ "$recv" -lt "$limit" ] && [ "$send" -lt "$limit" ]; then
    exit 0
fi

logger -t mihomo-watchdog \
    "restarting ${unit}: 18443 backlog recv=${recv} send=${send} limit=${limit}"
echo "$now" >"$stamp"
systemctl restart "$unit"
