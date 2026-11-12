# Session SRTT runaway (Karn)

## Symptom
netns 5 % run /tmp/tbtun-e2e.0RMHfd: bulk up stalled at the tail. Client tx_head 9927 while
peer_ack crawled 4-8 steps/s, and the client session srtt rose 286 -> 1428 ms.

## Cause
TxHalf took one SRTT sample per ack advance, seal -> custody of step ack-1. While the
frontier waits for a BOOSTER round, every step behind it is held, so the sample measures
the repair wait. SRTT rises, the next round for the same range waits 1.05 x SRTT, steps are
held longer, and SRTT rises again. This is a positive feedback loop. Client and server share
TxHalf, so both had it. TunPath (the server's user-space TCP) already applies Karn
(`Retx = 0`) and keeps a 10 s windowed min RTT, so it did not have the problem.

## Fix (TxHalf)
- Karn: skip the sample when the last repair round covered steps below ack (repairLo < ack)
  and started after step ack-1 was sealed. Counted as `rtt_skipped`.
- Windowed min RTT over clean samples (two 10 s buckets, zero allocation): `minrtt_ms`.
- Repair-round wait = 1.05 SRTT clamped to [1.05, 4] x min RTT. A stale SRTT cannot space
  repair rounds out by seconds.

## Deterministic reproduction (SessionTests, Pair: 4 paths, 20 ms one-way delay, credit off,
8 KB fed per ms, blackout during bulk, then drain)

| loss / blackout | before: max srtt, drain | after: max srtt, drain |
|---|---|---|
| 2 % / 300 ms | 807 ms, 1.9 s | ~55 ms, passes (< 6 s) |
| 5 % / 300 ms | 5.9 s, unfinished at 20 s | ~50 ms, passes |
| 10 % / 300 ms | 6.3 s, unfinished at 20 s | ~45 ms, passes |
| 2 % / 1000 ms | 5.8 s, unfinished at 20 s | 55 ms, 7.25 s |

The 1000 ms case after the fix is bounded by the spec, not by SRTT. A NACK names only the
frontier range (spec 5.8, at most 4 steps), so recovering from a long blackout costs about
one RTT per 4 steps. This is left for the long-tail item.

## netns A/B (20 ms OWD, 200 Mbit, 5 % loss, 4 paths, tun carrier; interleaved, clock_jumps=0)

Steady 5 % loss, 3 vs 3 (medians, karn vs main): bulk up 151.4 / 143.9, bulk down
135.7 / 135.6, under-bulk p99 126 / 113 ms (ranges 110-130 on both sides), CPU 2.52 / 2.47 s/Gbit.

Stuck-tail scenario (same link plus `WANEM_EXTRA='outage_ms=300\noutage_every_s=3'`),
4 vs 4: bulk up 115.9 / 114.0, down 111.8 / 112.6, under-bulk p99 500 / 502 ms (the outage
dominates it), RSS 63.2 / 63.1 MiB, CPU 2.71 / 2.62 s/Gbit (overlapping ranges). Max session
srtt: karn 250-287 ms on client and server, main 439-551 ms on the client and up to 1306 ms on the server.
`repair_repeats` (rounds repeated for the same range by timeout) was 9 per side in a full
run, so the 4 x min RTT ceiling does not cause wasted repeat rounds.

Still open: under bulk up, clean samples reach ~400 ms against a 40 ms min RTT. Sealed steps
wait roughly 350 ms before custody (client send-side queueing). That is real queueing, not a
Karn problem; it is a matrix and long-tail item.
