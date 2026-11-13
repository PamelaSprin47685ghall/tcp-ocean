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
