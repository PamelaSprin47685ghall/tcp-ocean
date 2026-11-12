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
