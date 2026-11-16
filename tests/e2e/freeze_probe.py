#!/usr/bin/env python3
"""Record short whole-box or network-path freezes during a testbed run.

The box (a VM) sometimes stops for ~100 ms. Everything stops with it, wanem included,
so every path goes silent at once; at 40 ms RTT that is longer than the tail timer and
costs about the freeze length in throughput. host_noise.py only sees fork/balloon
markers and stats_gap only gaps of seconds, so these freezes were invisible.

Two detectors, one JSON line per freeze in the output file:
  timer  a 1 ms sleep loop on CLOCK_MONOTONIC in this process; a wake-up later than
         --min-ms means this process did not run (the box or the CPU stopped).
  icmp   (instead of timer when --icmp-target is given) echo requests every --icmp-interval-ms to --icmp-target from this process (run
         it in the near namespace as root); a gap of at least --min-ms between two replies
         means the path through wanem stopped. Probes cross the bottleneck, so this one
         adds a little traffic (about 0.1 Mbit/s) and changes which packets wanem drops.
Each line: {"kind": "freeze", "source": ..., "start": unix_s, "end": unix_s, "ms": ...}.
A final {"kind": "freeze_probe_end", ...} line says the detector ran to the end; without
it the evidence for the run tail is unknown. This module never changes test verdicts.
"""
import argparse
import json
import os
import select
import signal
import socket
import struct
import sys
import time


def checksum(data):
    if len(data) % 2:
        data += b"\0"
    total = sum(struct.unpack("!%dH" % (len(data) // 2), data))
    total = (total >> 16) + (total & 0xFFFF)
    total += total >> 16
    return ~total & 0xFFFF


def steal_jiffies():
    # Total steal over all CPUs from the first /proc/stat line (USER_HZ ticks): time the
    # hypervisor ran something else while a vCPU wanted to run. A whole-VM stop of T ms
    # shows as about ncpu*T of steal; a stall of only this process (CPU contention,
    # interrupts) shows none.
    try:
        with open("/proc/stat", "rb") as stat:
            fields = stat.readline().split()
        return int(fields[8])
    except (OSError, IndexError, ValueError):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("out")
    parser.add_argument("--min-ms", type=float, default=50.0)
    parser.add_argument("--icmp-target")
    parser.add_argument("--icmp-interval-ms", type=float, default=5.0)
    args = parser.parse_args()
    out = open(args.out, "a", buffering=1)
    stop = []
    signal.signal(signal.SIGTERM, lambda *_: stop.append(1))
    signal.signal(signal.SIGINT, lambda *_: stop.append(1))

    hz = os.sysconf("SC_CLK_TCK")
    ncpu = os.cpu_count() or 1

    def emit(source, start_mono, end_mono, **extra):
        # Wall time from the monotonic offset at emission: a wall-clock step inside the
        # freeze must not invent or hide one.
        offset = time.time() - time.monotonic()
        row = {"kind": "freeze", "source": source, "start": round(start_mono + offset, 4),
               "end": round(end_mono + offset, 4), "ms": round((end_mono - start_mono) * 1000, 1), **extra}
        out.write(json.dumps(row) + "\n")

    sock = None
    if args.icmp_target:
        sock = socket.socket(socket.AF_INET, socket.SOCK_RAW, socket.IPPROTO_ICMP)
        sock.setblocking(False)
    ident = os.getpid() & 0xFFFF
    seq = 0
    sent = {}
    interval = args.icmp_interval_ms / 1000.0
    limit = args.min_ms / 1000.0
    start = time.monotonic()
    try:
        policy = {os.SCHED_OTHER: "other", os.SCHED_FIFO: "fifo", os.SCHED_RR: "rr"}.get(os.sched_getscheduler(0), "?")
    except OSError:
        policy = "unknown"
    out.write(json.dumps({"kind": "freeze_probe_start", "unix": round(time.time(), 4), "min_ms": args.min_ms,
                          "icmp_target": args.icmp_target, "pid": os.getpid(), "sched": policy,
                          "ncpu": ncpu}) + "\n")
    last_wake = start
    last_steal = steal_jiffies()
    next_probe = start
    last_reply = None
    replies = probes = 0
    while not stop:
        now = time.monotonic()
        steal = steal_jiffies() if sock is None else None
        if sock is None and now - last_wake >= limit:
            # steal_cpu_ms: steal during the gap divided by the CPU count; close to the gap
            # length means every vCPU was stopped (whole-VM freeze).
            extra = {}
            if steal is not None and last_steal is not None:
                extra = {"steal_cpu_ms": round((steal - last_steal) * 1000 / hz / ncpu, 1)}
            emit("timer", last_wake, now, **extra)
        last_wake = now
        last_steal = steal
        if sock is not None:
            if now >= next_probe:
                seq = (seq + 1) & 0xFFFF
                body = b"tbtun-freeze-probe"
                header = struct.pack("!BBHHH", 8, 0, 0, ident, seq)
                packet = struct.pack("!BBHHH", 8, 0, checksum(header + body), ident, seq) + body
                try:
                    sock.sendto(packet, (args.icmp_target, 0))
                    sent[seq] = now
                    probes += 1
                except OSError:
                    pass
                next_probe = max(next_probe + interval, now)
                if len(sent) > 4096:
                    sent.clear()
            ready, _, _ = select.select([sock], [], [], max(0.0, min(0.001, next_probe - time.monotonic())))
            while ready:
                try:
                    data, _ = sock.recvfrom(2048)
                except (BlockingIOError, InterruptedError):
                    break
                arrived = time.monotonic()
                head = (data[0] & 0x0F) * 4
                kind, _, _, rid, rseq = struct.unpack("!BBHHH", data[head:head + 8])
                if kind == 0 and rid == ident and rseq in sent:
                    sent_at = sent.pop(rseq)
                    replies += 1
                    if last_reply is not None and arrived - last_reply >= limit:
                        emit("icmp", last_reply, arrived, rtt_ms=round((arrived - sent_at) * 1000, 1))
                    last_reply = arrived
        else:
            time.sleep(0.001)
    out.write(json.dumps({"kind": "freeze_probe_end", "unix": round(time.time(), 4),
                          "seconds": round(time.monotonic() - start, 3), "icmp_probes": probes,
                          "icmp_replies": replies}) + "\n")
    out.close()


if __name__ == "__main__":
    sys.exit(main())
