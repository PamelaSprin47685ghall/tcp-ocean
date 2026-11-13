#!/usr/bin/env bash
# Three network namespaces near <-> wan <-> far, joined by veth pairs. The wan namespace
# forwards every packet through wanem (user-space delay, rate and loss on a TUN device).
# far also holds tbt0 (10.200.3.1/24) for the server's user-space TCP at 10.200.3.2.
#   netns.sh up <workdir>      create namespaces, start wanem (config: <workdir>/wanem.conf)
#   netns.sh down [workdir]    stop wanem, delete only namespaces recorded for this owned run
# Needs root (run with sudo), iproute2 and gcc.
#
# Safety (from the v2.2 testbed):
#   * `up` never deletes existing namespaces, even if they have the fixed names
#     tbe_near / tbe_wan / tbe_far. If any of those names already exist, it refuses.
#   * Every namespace this run creates is recorded by (st_dev, st_ino) plus boot_id.
#     `down` deletes only those identities. A replaced name or a missing manifest
#     means nothing is touched. Cleanup does not trust a PID file alone.
#   * Concurrent runs serialize on /tmp/tbtun-netns.lock (flock, held for the call;
#     run.sh holds the same lock for a whole e2e run and sets TBTUN_NETNS_LOCK_HELD=1).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
cmd=${1:?up or down}
work=${2:-}
NEAR=tbe_near WAN=tbe_wan FAR=tbe_far
guard=(python3 "$here/tb_guard.py")

# Serialize with other workers. If the caller (run.sh) already holds the lock, skip.
acquire_lock() {
    if [[ ${TBTUN_NETNS_LOCK_HELD:-0} == 1 ]]; then
        return 0
    fi
    exec 9>/tmp/tbtun-netns.lock
    if ! flock -n 9; then
        echo "waiting for /tmp/tbtun-netns.lock" >&2
        flock 9
    fi
    export TBTUN_NETNS_LOCK_HELD=1
}

down() {
    "${guard[@]}" down
}

up() {
    [[ -n $work ]] || { echo "usage: netns.sh up <workdir>" >&2; exit 2; }
    mkdir -p "$work"
    gcc -O2 -Wall -Wextra -o "$work/wanem" "$here/wanem.c" -lm
    [[ -f $work/wanem.conf ]] || printf 'owd_ms=10\nrate_mbit=1000\nloss=0\n' > "$work/wanem.conf"
    "${guard[@]}" begin $NEAR $WAN $FAR
    "${guard[@]}" record-work "$work"
    rollback() {
        echo "setup failed; cleaning only this run's recorded resources" >&2
        "${guard[@]}" down || echo "cleanup incomplete; owned manifest retained" >&2
    }
    trap rollback ERR
    for n in $NEAR $WAN $FAR; do
        ip netns add "$n"
        "${guard[@]}" record-ns "$n"
    done
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
    # Start wanem as a direct child so the guard can record (pid, start-time, parent).
    # Close the lock fd in the child so it does not hold the flock after we exit.
    # exec in the subshell keeps $! equal to wanem (ip netns exec then execs wanem).
    (
        exec 9>&- 2>/dev/null || true
        exec ip netns exec $WAN "$work/wanem" tbe0 "$work/wanem.conf" "$work/wanem.stats" \
            </dev/null >/dev/null 2>"$work/wanem.log"
    ) &
    wanem_pid=$!
    "${guard[@]}" record-process wanem "$wanem_pid" "$$"
    sleep 0.5
    ip -n $WAN link set tbe0 txqueuelen 20000
    ip -n $WAN link set tbe0 up
    ip -n $WAN rule add iif tbwn lookup 100 pref 100
    ip -n $WAN rule add iif tbwf lookup 100 pref 101
    ip -n $WAN route add default dev tbe0 table 100
    for k in all default tbe0 tbwn tbwf; do ip netns exec $WAN sysctl -qw "net.ipv4.conf.$k.rp_filter=0"; done
    # Server TUN (spec 12.1): the server's user-space TCP owns 10.200.3.2 behind tbt0 in far.
    # The device belongs to the invoking user, so the server needs no root.
    ip -n $FAR tuntap add dev tbt0 mode tun user "${SUDO_USER:-root}"
    ip -n $FAR addr add 10.200.3.1/24 dev tbt0
    ip -n $FAR link set tbt0 up
    ip netns exec $FAR sysctl -qw net.ipv4.ip_forward=1
    for k in all default tbf tbt0; do ip netns exec $FAR sysctl -qw "net.ipv4.conf.$k.rp_filter=0"; done
    ip -n $WAN route add 10.200.3.0/24 via 10.200.2.2
    trap - ERR
}

acquire_lock
case $cmd in
    up) up ;;
    down) down ;;
    *) echo "usage: netns.sh up|down [workdir]" >&2; exit 2 ;;
esac
