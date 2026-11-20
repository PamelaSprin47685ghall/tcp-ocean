#!/bin/sh
# Restart the tbt4 proxy unit when its vless inbound on 127.0.0.1:18443 stops
# moving bytes.
#
# The failure mode this guards against: the proxy stays alive and keeps listening
# on 127.0.0.1:18443, but it stops reading (or writing) the bytes on that hop.
# Queues on the 18443 sockets grow and bulk throughput collapses to zero, while
# the process never exits, so `Restart=always` does not help.
#
# A big queue alone is NOT a stall.  On the client host the local side of 18443
# is tbtun itself: during a healthy upload tbtun holds back on flow credit and
# its 8 MB flow receive buffer fills, so Recv-Q sits at 6-11 MB while bytes move
# at full speed.  The old size-only check restarted the proxy in the middle of
# every large upload.  So the rule is: backlog over the limit AND no progress on
# any backlogged socket across a sampling interval.
#
# Usage: mihomo-watchdog.sh <unit>
# Env:   MIHOMO_WATCHDOG_BYTES    backlog threshold, default 4194304 (4 MiB)
#        MIHOMO_WATCHDOG_SAMPLE_S progress sampling interval, default 5
set -eu

unit="${1:-mihomo-hnd0tbt4}"
limit="${MIHOMO_WATCHDOG_BYTES:-4194304}"
gap="${MIHOMO_WATCHDOG_SAMPLE_S:-5}"
port="${MIHOMO_WATCHDOG_PORT:-18443}"
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

# One line per ESTAB socket touching :port:
#   key recvq sendq consumed acked
# consumed = bytes_received - Recv-Q (what the local reader has taken)
# acked    = bytes_acked              (what the peer has taken from us)
sample() {
    ss -tni state established "( sport = :$port or dport = :$port )" 2>/dev/null | awk '
        /^[0-9]/ { if (key != "") print key, rq, sq, rcv - rq, ack
                   rq = $1; sq = $2; key = $3 "-" $4; rcv = 0; ack = 0; next }
        { for (i = 1; i <= NF; i++) {
              if ($i ~ /^bytes_received:/) { split($i, a, ":"); rcv = a[2] }
              if ($i ~ /^bytes_acked:/)    { split($i, a, ":"); ack = a[2] } } }
        END { if (key != "") print key, rq, sq, rcv - rq, ack }'
}

backlog() { awk '{ r += $2; s += $3 } END { print (r > s ? r : s) + 0 }'; }

first=$(sample)
b1=$(printf '%s\n' "$first" | backlog)
if [ "$b1" -lt "$limit" ]; then
    exit 0
fi
sleep "$gap"
second=$(sample)
b2=$(printf '%s\n' "$second" | backlog)
if [ "$b2" -lt "$limit" ]; then
    exit 0
fi

# Progress on any socket that carries a backlog (present in both samples).
moved=$(printf '%s\n--\n%s\n' "$first" "$second" | awk '
    $1 == "--" { phase = 2; next }
    phase != 2 { c[$1] = $4; a[$1] = $5; next }
    ($1 in c) && ($2 > 0 || $3 > 0) { m += ($4 - c[$1]) + ($5 - a[$1]) }
    END { print m + 0 }')
if [ "$moved" -gt 0 ]; then
    exit 0
fi

logger -t mihomo-watchdog \
    "restarting ${unit}: ${port} backlog ${b1}->${b2} with no progress in ${gap}s (limit=${limit})"
echo "$now" >"$stamp"
systemctl restart "$unit"
