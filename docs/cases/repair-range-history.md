# Preserve default repair-range history across reordered feedback

Base: `701e228`. Scope: the default (`NewRepair=false`) sender's repair bookkeeping. No ACK-generation, write-gate, credit, pacing, source-retention or protocol changes. This is separate from the FLOW_STATE lifecycle fix and arrival-feedback experiment.

## Controlled failure

A real receiver generates distinct authenticated WIN_ACK frames A1 and A2 for `(tLo=0, span=1)`, then B for `(0,2)` after seeing the next step. Delivering A1, B, then delayed A2 is legal within the replay window. A2 has the same deficit as A1, does not lower the acknowledged frontier or seen high-water mark, and is not a replayed ciphertext.

The baseline stores only the latest range. B overwrites A's wait and sequence state. Returning to A is mistaken for a new range: at zero manual-clock elapsed time, below the current 105 ms RepairWait, it generates 18 more BOOSTERs with the same range, seed and encoded payload. Without B, the same delayed A2 does not trigger another round. This is a deterministic legal scheduling counterexample, not a measurement of deployment frequency or throughput.

Current receiver span does not naturally shrink at a fixed frontier because SeenEnd is monotonic. New-counter range alternation in direct API tests is additional robustness coverage, not a claim that the current receiver naturally generates that pattern. Repeated equations are not cryptographic nonce reuse, and distinct ranges do not share one global 256-seed budget.

## Bounded correction

Keep each effective `(absolute tLo, normalized span)` entry in a fixed value-type table with four entries per encoder-window slot. Each remembers the last accepted enqueue time, deficit and next sequence. A valid absolute step tag distinguishes subsequent uses of the ring index. Capacity multiplication is checked; accepted spans are bounded before indexing. Nonpositive spans passed directly to the default integer API are rejected; legitimate wire parsing already constrains active NACK spans to 1 through 4.

The existing source-window and custody checks precede access. Two simultaneously held encoder steps cannot alias the same modulo-capacity slot. Recovered entries can be overwritten on a different absolute tag; no LRU can evict a still-held range. Old records may remain physically in the fixed table but cannot reapply after the custody frontier or to another absolute step. There is no per-seal clearing, growing dictionary or periodic scan. This bounds history storage, not every pre-existing repair queue.

A first valid range still responds immediately. A revisit uses its own previous state, even if another range intervened. The existing deficit-growth exception and RepairWait formula remain. The most recent accepted repairLo/repairNs remain separate for the existing Karn calculation. RepairRepeats now counts a due repeat of a remembered range even when another range intervened; it is telemetry, not a new policy input.

If the next round cannot fit the remaining sequence budget, existing DATA fallback remains. That fallback does not consume the unused seeds: a later smaller round can still use them. No source obligation is silently abandoned, and the NewRepair=true scheduling branch is unchanged. The checked table length is computed during construction, but the table itself is allocated only on the first valid default OnNack, before changing Karn, repeat counters or repair queues. A positive stored deficit marks an initialized entry; both absolute Lo zero and time zero remain valid. Deficit and sequence keep their full integer types. A session that only uses NewRepair never allocates this table. Once created, it is retained across mode changes so returning to the default scheduler does not forget its history. This is a single-owner API, not a concurrent initialization scheme. Allocation/zeroing and its GC or OOM risk move from construction to first default repair; they do not disappear. A failed allocation does not half-accept that repair round.

## Limits and validation

The unit tests distinguish enqueue time from actual transmission. This patch does not prove a strict wire-time inter-round bound or resolve the existing deficit-growth/minRTT differences from the specification's literal timing wording. It does not reject a whole older WIN_ACK and lose independent custody or CTRL acknowledgments.

Regression coverage includes the real reordered-feedback case and no-interleaving control, due-time continuation, seed continuity and exhaustion/fallback, remaining-seed use after a too-large round, distinct valid lows/spans, normalized identity, old feedback after custody, and actual encoder ring reuse. Original blackout/tail/RepairWait assertions remain intact.

## Measured allocation scope

The initial eager candidate used 32-byte managed entries and allocated 65,560 / 524,312 bytes per table at capacity 512 / 4096. Its earlier results are retained separately. The final lazy candidate measures a 24-byte managed stride with Unsafe.SizeOf on this 64-bit host. Activated table allocations, including the array header, are 49,176 / 393,240 bytes respectively. These are actual managed array allocations, not RSS or native-layout guarantees.

Warm Session constructors were measured three times for each capacity, Client/Server role and scheduler mode against clean 701e228. All matched: baseline 25,151,480 / 189,463,544 bytes; candidate 25,151,472 / 189,463,536 bytes, a net constructor delta of -8 bytes. This net object-layout result is distinct from the deferred table allocation and does not imply zero overall initialization cost. The shared empty-array initialization was warmed before these measurements.

With identical source/RTT setup and preallocated repair queues, the first valid default OnNack allocated 49,176 / 393,240 bytes in the candidate and zero in the baseline; the next measured default call allocated zero in both. NewRepair's first call allocated 88 bytes in both versions, the next zero, and the candidate history remained empty. These are total call allocations in this narrow fixture, not a universal bound on repair queue or scheduler allocations. The script's `no_repair` interval contains only KeepAlive: it is an empty measurement-envelope check, not a no-feedback workload allocation claim. Absence of the table before valid default repair is independently inspected in tests.

For an activated table the measured formula is 96*C+24 bytes per TxHalf, with one TxHalf per Session. At capacity 4096, 100 / 1000 such tables imply about 37.5023 / 375.0229 MiB; at 512, about 4.6898 / 46.8979 MiB. These are arithmetic extrapolations, not measured large fleets. Two endpoint Sessions per connection double this table-only amount only if both have activated their history. Reconnect overlap may temporarily retain old and new objects, and first-repair allocation creates a transient allocation/GC burden. Neither lazy allocation nor constant-time lookup establishes negligible latency or throughput cost.

Final regression coverage also checks no allocation for invalid default inputs or NewRepair-only use, Lo=0/time=0, table identity reuse, and default→NewRepair→default preservation of wait, deficit-growth and sequence/fallback history. The mode-switch test does not establish a minRTT for the alternate scheduler, so it proves preservation of default history, not cross-scheduler equation uniqueness after active alternate repair. Full solution build and all 340 tests pass; the 11 guard and 5 echo tests pass. Official valid-sample performance comparisons and NativeAOT validation of this patch remain separate gates.

Local linux-musl-x64 CLI and a separate public-API smoke runner built with zero warnings, but this was managed preparation, not NativeAOT publication. The isolated guest console could not accept input before login, so no guest publish or native smoke execution occurred. No other branch's native result is substituted. NativeAOT publish/start validation is therefore pending this draft PR's CI; even a successful startup would not be an official network or throughput acceptance result.
