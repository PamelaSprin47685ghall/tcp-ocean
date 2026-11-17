# Phase B proposal: shadow sampling before control

Status: B1 pure shadow helper implemented and independently reviewed; tests-project validation passed; B2 remains a proposal, unimplemented and not accepted. Base Phase A f5463ef atop 496d511. The Phase A experimental bit 0x0800 and WIN_ACK bytes80..95 are tentative, not globally reserved; unseen parallel work can conflict. Default behavior must remain unchanged. No change to ACK scheduling, write gates, ledger credit, gain defaults or old recovery assertions is part of this proposal.

## Objective and non-objective

Reject THi existence inference and repair-sent estimates as evidence of actual coded-frame acceptance. Preserve separate counters for inferred arrival, retired originals, actual accepted coded frames, and wire/repair expenditure. This does not measure application goodput, complete physical link capacity or physical storage release.

A newly sealed BOOSTER with a fresh cell counter can contribute to accepted coded-frame evidence. Ordinary physical copies of an already received TCP byte range are deduplicated below the session; retransmitting the same segment can consume link capacity without increasing this counter. TunPath ProduceFill may reseal a fresh cell into a reused TCP sequence range: if it becomes the first effective arrival for a still-missing range, it can contribute a new coded frame. TCP sequence equality alone is not sufficient to classify session receipt. Therefore removing CopyEstimate from capacity evidence does not eliminate actual retransmission costs, and cannot justify silently equating coded-arrival throughput with physical wire capacity. Server TunPath also charges complete stream frames, including noncoded control/feedback frames, whereas the new receipt counter intentionally excludes them. The client SessionCarrier coded-wire accounting has a different boundary. A future estimator must explicitly reconcile these units and test control-heavy traffic; it cannot silently interpret all charged bytes as covered by coded receipt evidence.

## Stage B1: observation-only estimator

Add a distinct session-direction observation and sample channel. Do not repurpose IRateMember.Delivered, alter existing goodput telemetry, add CopyEstimate to the new total, or change LastDeliveredNs/outage logic as a side effect. Phase A evidence currently exposes a cumulative count, not report freshness or a usable time interval.

Required state: session identity, negotiated status, cumulative accepted count, valid report arrival time, evidence coverage anchor, last accepted receiver timestamp if used, and explicit unknown/fresh/stale/invalid status. Invalid overflow remains sticky. No dynamic fallback to THi, retired originals or sent repairs.

The current RateGroup.Service advances sampleNs during no-delivery rounds. Reusing that anchor would divide a blackout's accumulated bytes by the final short round and invent a peak. The new coverage anchor must be independent of Service's idle rounds. First report establishes a baseline unless a fully justified initial coverage interval is available. Repeated or reordered reports cannot generate new rate samples or reset the growth interval.

Receiver RxTimeUs already exists in WIN_ACK, but using it requires a contract tying it to the same cumulative snapshot and handling timestamp regression, delayed equal-count reports, wrap and invalid values. Receiver time differences need no clock synchronization, but authentication does not prove honest timestamps. A cumulative generated-byte upper bound alone does not bound feedback-compressed rates. The exact estimator and conservative send-rate bound remain to be selected and tested, not inferred from this document.

## Aggregation and scope

Client uplink has one SessionRateMember and can report each session direction once. Default uplinkPace=false must remain off.

Server TunStack shares a RateGroup by source IP across paths and potentially sessions. A session's cumulative total must not be counted once per path. Never combine that total with the same traffic's legacy path copy estimates. A session/group ownership registry and stable identities are prerequisites for consumption.

Mixed negotiated and legacy sessions cannot silently blend incompatible evidence bases or split a shared bottleneck into independent budgets. Initially shadow status should report incomplete coverage; a future consumer experiment must explicitly reject unsupported mixed coverage or settle a separately reviewed policy. JOIN/rekey preserve session identity; replacement sessions do not inherit prior counters.

## Stage B2: consumer experiment, separately gated

Only after B1 passes should an independently opt-in consumer use the new samples. This is a control-policy change, not merely renaming counters. Keep existing inflight/custody semantics, wire charges, repair charges and recovery discovery unchanged unless a separately justified change is authorized.

No feedback is not zero delivery. Holding a prior rate forever can sustain excess traffic; decaying it can starve repairs. A bootstrap/recovery allowance would itself be a policy change. Existing ProtectRecovery and startup fallback, and the f2dd308 candidate that has not passed full acceptance and still has completion-error evidence, must not be assumed safe without new evidence. The unresolved acceptance requirement is simultaneous bounded expenditure and eventual recovery under no-SACK and a finite reverse-feedback blackout followed by restored connectivity and valid feedback, without manufacturing delivered bytes. An expenditure bound must explicitly say whether it limits instantaneous rate, outstanding window, or total work over a finite interval. Permanent loss of all reverse feedback cannot promise eventual recovery and finite total repair expenditure simultaneously.

## Falsifiable validation

1. All tail coded frames lost; HEARTBEAT increases THi but not new receipt evidence; unchanged NACK/BOOSTER restores delivery.
2. Arbitrarily many sent but unreceived repairs cannot increase the measured arrival sample.
3. Same-counter/TCP replay contributes nothing; fresh authenticated reseal contributes exactly one accepted frame; no CopyEstimate double count.
4. Fixed receiver arrival history with delayed, batched, reordered and repeated feedback cannot create an arbitrarily larger sample solely through feedback timing.
5. Extremely late first report and prolonged blackout retain honest unknown/stale states, no invented zero samples, no switch to legacy estimates.
6. no-SACK, constrained reverse link, dropped feedback and asymmetric directions preserve both recovery completion and charged expenditure bounds.
7. Uplink pacing on/off, actual TunPath TCP state-machine integration, multiple paths/JOIN/rekey, and multiple sessions prove direction/scope isolation. Packet fixtures are not Linux kernel/TUN end-to-end evidence.
8. Invalid schema or overflow reports cannot contaminate filters or revive disabled evidence. Normally reordered lower cumulative reports are ignored, not classified as permanent invalidity; timestamp regressions require a separately specified sampling rule.
9. All unchanged recovery, 256-budget, later-open-flow, credit and scheduling tests remain passing; no exception tailored to old assertions.
10. Controlled A/B on the same valid QEMU workloads follows only after these mechanisms pass. Simulation rates do not establish production capacity or solve the historical 64-path variance.

Three separate gates are required: truthful feedback accounting; sampling robust to feedback timing; safe consumer recovery. Passing one does not imply the others.

## B1 implementation contract (experimental shadow only)

`ArrivalShadow.Sampler` is explicitly constructed by tests/experiments. No production Session, Carrier, TunPath, RateGroup or timer instantiates/calls it. Its source is compiled but there is no default hot-path state, polling or allocation. B2 remains unimplemented. Phase A accounting and existing control consumers remain unchanged.

One sampler belongs to one stable SessionId + Direction. New Session identity requires a new sampler; JOIN/rekey do not create new observation epochs. Input must be a negotiated, authenticated, schema-valid Phase A report plus the matching **trusted local Phase A GeneratedCodedFrameEvidence counter**. The supplied generated bound is also checked for complete-frame multiples, signed-safe cap and monotonicity using a separate generated high-water mark; violations latch shadow-only `InvalidGeneratedBound`. This catches identity/input misuse but does not prove the input came from an authentic local Counter. It rechecks report frame multiples, signed-safe cap and generated upper bound; schema-invalid reports must not be supplied. Authentication does not imply an honest peer or truthful clock/count.

The first report establishes a growth anchor `(bytes, receiverNs, localNs)` with no sample. A later count increase produces `ObservedCodedBytesPerSecond = deltaBytes * 1e9 / max(receiverNs - anchor.receiverNs, localNs - anchor.localNs)`, then moves that anchor. Equal-count reports with a newer receiver timestamp update feedback liveness but not the growth anchor or last sample. Lower-count reordered reports and older equal-count reports are ignored, not used to renew liveness. Same-timestamp growth is deferred because timestamps have microsecond precision; a later timestamp with the **same highest pending count** can release the sample. Timer/snapshot reads never shorten growth coverage or create zero-delivery samples.

This statistic assumes the receiver's declared microsecond clock is monotonic **and has a trustworthy stable rate**. Merely monotonic clocks can run arbitrarily slowly; neither this formula nor authentication provides a physical rate bound or resistance to malicious timestamp manipulation. The maximum of the two intervals is a conservative shadow coverage choice, not a proven capacity estimate, a safe sending limit or an always-lower bound on instantaneous true arrival throughput.

`FreshFeedback` means a locally recent arrival of a strictly newer usable remote timestamp, not known recent receiver-side delivery. A delayed queue of reports can appear locally fresh; absolute remote report age is explicitly Unknown. Feedback age and sample age are distinct. `Unknown`, `StaleFeedback`, and `Invalid(reason)` never fall back to THi, retired originals or sent-copy estimates. Outputs are `ShadowOnly`; the sampler supplies no control-eligibility/capacity output.

Receiver timestamps must be <= `Int64.MaxValue / 1000` before converting microseconds to nanoseconds. Timestamp regression on increasing cumulative evidence compares the most recent valid receiver timestamp, including idle reports, rather than only the growth anchor. It latches a shadow-only `ReceiverTimestampRegression`; out-of-range timestamps and overflow likewise latch explicit invalid reasons. Only a new sampler for a new session resets this conservative experiment limitation. It does not disable live Phase A counters, protocol repair or recovery.

Local receive timestamps must be nonnegative and monotonic. A negative or regressing local input is rejected **without any state mutation**, not latched into remote timing invalidity. Otherwise a call advances the local input-order guard even if its evidence is rejected (and a valid local generated bound updates its separate high-water mark), but rejected evidence does not refresh feedback or move the growth anchor. `Snapshot(nowNs)` is read-only, validates its local time, and does not advance that call-order guard. All elapsed calculations stay within the nonnegative signed Int64 time domain.

`ArrivalShadow.coverage` takes a complete authoritative active-membership snapshot plus direction observations. It is not wired to TunStack. It deduplicates identical session-direction observations across paths, flags conflicting duplicate observations, unknown identities, missing/stale/invalid observations, legacy/mixed membership, overlapping path incarnations and a session-direction assigned to more than one group. The caller unloads an exact old incarnation rather than deleting a replacement binding with the same path ID. It cannot discover a path omitted by the caller. It never sums different session windows, apportions cumulative totals across paths, or splits a bottleneck group. `ControlEligible` is always false, including complete fresh coverage; fresh baseline-only evidence is not a rate/capacity measurement.

### B1 validation snapshot

Release tests-project build: 0 warnings/errors. Full tests assembly: **387 passed, 0 failed, 0 skipped**, 29.284 s, comprising prior 367 plus 20 shadow cases. This is the historical B1 snapshot; see [consolidated results and limitations](arrival-evidence-results.md) for subsequent validation. Tests include late baseline, compressed feedback, idle/blackout coverage, reorder/repeat, microsecond pending growth, sticky timestamp/overflow/generated-bound reasons, invalid local-time non-mutation, generated-but-unreceived repair upper bounds, disabled mode, scope dedup/mixed/conflicts/incarnation replacement, and real authenticated Session feedback decoded into an independent shadow observer. No controller integration, OS-level network test or performance claim follows from this result. That historical `496d511` NativeLib full-solution blocker was fixed on main by `701e228`, now integrated here. This historical tests-project pass is not an updated-head full-solution build pass.
