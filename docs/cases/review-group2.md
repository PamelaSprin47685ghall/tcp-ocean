# Review group 2: repair pacing, metering split, D-SACK from receive history

## (d) Repairs go through the shared pacer
Before: RACK, the tail timer and RTO go-back-N sent copies on the spot, outside the
group's token bucket; a demoted path re-sent on every timer without being charged.

Now RACK, tail and RTO only *request* repairs (`repairQ`, bounded 1024; a segment is queued
once, `Due`). `drainRepairs` sends them when `RateGroup.TryTakeRepair` allows:
- a repair bucket refilled at 0.5 x rate, depth = recovery reserve = max(16 frames, rate x 2 ms);
- the main bucket may go down to -reserve for repairs, never further; every repair is
  charged to both buckets (`OnRepair`), so the reserve is borrowed, not free;
- new frames wait while repairs are queued (`TryBeginFrame`).
Demoted paths probe one segment per timer, back-off x2 up to x64, charged and counted
(`DemotedRepairs`); no go-back-N while demoted.

Found on the way (root causes, not masks):
- one path's repair storm took all tokens and starved the others: fixed by the half-rate
  repair bucket;
- a round with zero delivery (all paths blacked out) was taken as a bandwidth sample of 0
  and emptied the max filter: such rounds are outages now, not samples; an outage needs
  inflight on at least min(2, members) paths;
- demotion right after a group outage punished a path for the shared blackout: demotion
  now also needs `now - LastOutageNs > after`.
Counters: RepairRequests, RepairsDeferred, DemotedRepairs (Describe: rq/req/defer).

## (e) Metering split
- delivery / bw samples: unique acked bytes, from ACK/SACK evidence only (`UniqueDelivered`);
- wire cost: every byte sent, copies included (`WireSentBytes`);
- copy overhead: `CopyEstimate`, labeled as an estimate (copy_est_kib in Describe). It is
  still added to the group sample, so the pacer's behavior is unchanged: copy cost is not
  dropped, only no longer mixed with what was delivered.
Tests now assert sent/acked facts (a repaired segment is delivered once; the extra copy shows
up in the estimate and in wire bytes; after a 1 MB repair burst a new frame waits until the
pacer has paid it back).

## (f) D-SACK from real receive history
- Swallowed (virtually acked) ranges are kept apart from received bytes (up to 64 ranges;
  older ones are forgotten and counted, `SwallowedForgotten`).
- The first late arrival of a swallowed seq is new data, not a duplicate (`LateFirstBytes`);
  only a second copy is reported as D-SACK (RFC 2883 reports duplicates actually received).
- A duplicate above the cumulative ACK uses the RFC 2883 two-block form: D-SACK block first,
  then the SACK block that contains it, then the others. During a hole's grace it is held
  (the containing block would show the hole); once the gap is swallowed it is below the ACK
  and goes as a lone D-SACK.
- Loss hiding of RTO tail loss (policy A) comes later, as a separate, counted policy.

## netns A/B (main cd010d8 vs rb-g2 eee3556, interleaved, stamped, no clock jumps)
Medians, B = group 2, A = main. 20 ms OWD, 200 Mbit, 4 paths, 10 s.

| case | pairs | bulk up | bulk down | up SRTT | ping-under-bulk p99 | cpu s/Gbit |
|---|---|---|---|---|---|---|
| 0 % | 4 vs 5 | 144.9 vs 144.7 | 143.4 vs 143.6 | 119 vs 119 ms | 56.0 vs 54.9 | 2.49 vs 2.38 |
| 5 % | 4 vs 5 | 152.2 vs 152.2 | 136.0 vs 136.2 | 108 vs 108 ms | 132.4 vs 119.3 (A spread 111-145) | 3.29 vs 3.23 |
| 5 % + 300 ms outage / 3 s | 5 vs 6 | 110.8 vs 114.3 (both 88-129) | 110.2 vs 109.3 | 83 vs 104 ms | 568 vs 637 | 3.95 vs 3.92 |

No regression beyond run-to-run spread; under outages ping p99 and uplink SRTT improve. The
client kernel still takes RTOs during the 300 ms outages (TCPTimeouts 30-45 per run on both
sides): that leak is policy A's job, not group 2's.
