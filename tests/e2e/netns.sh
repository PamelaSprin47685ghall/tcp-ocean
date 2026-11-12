#!/usr/bin/env bash
# Three network namespaces near <-> wan <-> far, joined by veth pairs. The wan namespace
# forwards every packet through wanem (user-space delay, rate and loss on a TUN device).
#   netns.sh up <workdir>      create namespaces, start wanem (config: <workdir>/wanem.conf)
#   netns.sh down <workdir>    stop wanem, delete the namespaces
# Needs root (run with sudo), iproute2 and gcc.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
cmd=${1:?up or down}
work=${2:?work directory}
NEAR=tbe_near WAN=tbe_wan FAR=tbe_far

down() {
    if [[ -f $work/wanem.pid ]]; then kill "$(cat "$work/wanem.pid")" 2>/dev/null || true; rm -f "$work/wanem.pid"; fi
    for n in $NEAR $WAN $FAR; do ip netns del "$n" 2>/dev/null || true; done
}

up() {
    mkdir -p "$work"
    gcc -O2 -Wall -Wextra -o "$work/wanem" "$here/wanem.c" -lm
    [[ -f $work/wanem.conf ]] || printf 'owd_ms=10\nrate_mbit=1000\nloss=0\n' > "$work/wanem.conf"
    for n in $NEAR $WAN $FAR; do ip netns add "$n"; done
    ip -n $NEAR link add tbn type veth peer name tbwn netns $WAN
    ip -n $FAR link add tbf type veth peer name tbwf netns $WAN
    ip -n $NEAR addr add 10.200.1.2/24 dev tbn
    ip -n $WAN addr add 10.200.1.1/24 dev tbwn
    ip -n $WAN addr add 10.200.2.1/24 dev tbwf
    ip -n $FAR addr add 10.200.2.2/24 dev tbf
    for pair in "$NEAR tbn" "$WAN tbwn" "$WAN tbwf" "$FAR tbf" "$NEAR lo" "$WAN lo" "$FAR lo"; do
        set -- $pair; ip -n "$1" link set "$2" up
    done
    ip -n $NEAR route add default via 10.200.1.1
    ip -n $FAR route add default via 10.200.2.1
    ip netns exec $WAN sysctl -qw net.ipv4.ip_forward=1
    # One packet = one segment, so wanem loss is segment loss.
    for d in "$NEAR tbn" "$WAN tbwn" "$WAN tbwf" "$FAR tbf"; do
        set -- $d; ip netns exec "$1" ethtool -K "$2" tso off gso off gro off >/dev/null 2>&1 || true
    done
    ip netns exec $WAN "$work/wanem" tbe0 "$work/wanem.conf" "$work/wanem.stats" </dev/null >/dev/null 2>"$work/wanem.log" &
    echo $! > "$work/wanem.pid"
    sleep 0.5
    ip -n $WAN link set tbe0 txqueuelen 20000
    ip -n $WAN link set tbe0 up
    ip -n $WAN rule add iif tbwn lookup 100 pref 100
    ip -n $WAN rule add iif tbwf lookup 100 pref 101
    ip -n $WAN route add default dev tbe0 table 100
    for k in all default tbe0 tbwn tbwf; do ip netns exec $WAN sysctl -qw "net.ipv4.conf.$k.rp_filter=0"; done
}

case $cmd in
    up) down; up ;;
    down) down ;;
    *) echo "usage: netns.sh up|down <workdir>" >&2; exit 2 ;;
esac
