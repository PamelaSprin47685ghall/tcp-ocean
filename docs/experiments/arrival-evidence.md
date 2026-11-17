# Experimental coded-arrival evidence (default off)

Base: 496d511a45053253970f78509803ddad5877c955. Branch experiment/arrival-evidence. Default off, no merge, no RateGroup consumer change in phase A. Existing f2dd308 candidate is neither included nor assumed correct.

## Scope and collision check

Pinned v2.2 spec §3.16 reserves caps bits11–15; §6.10 reserves WIN_ACK bytes80–95 as zero. Source Caps.Reserved=0xF800. Therefore 0x0800 is NOT an existing assigned capability: using it is an explicit experimental deviation that needs its own opt-in contract, not a claim of unchanged v2.2 conformance. No other use found in pinned source/docs. The only locally visible remote branch not merged into this base, origin/diag/uplink-segment-evidence (d17d7f2), edits CI/offline evidence docs/scripts/tests, not handshake/wire/runtime files. This check does not exclude unpublished parallel work. Capability and field allocation require maintainer coordination before deployment or merge; no global reservation is claimed.

## Phase A: feedback transport and separate observation only

Optional ClientOffer/ServerOffer constructor argument offerArrivalEvidence defaults false. SupportedCaps/default advertised bits and baseline reserved-byte parser behavior stay unchanged. Each side conditionally permits experimental bit0x0800; server selects intersection of client offer and its own explicit opt-in; client refuses an unoffered selected experimental bit. Both-off and either-side-off use existing zero-reserved WIN_ACK. Prior server masks unknown offer bits in this pinned implementation; do not promise all historical implementations accept experimental offers.

No CLI flag in phase A: tests/API opt-in only, avoiding overlap with recent CLI work. A later explicit CLI experiment flag can be reviewed separately.

### Arrival unit and trust

Counter covers one session RECEIVE direction, aggregated over all paths. Increment by fixed Session.FrameLength only after existing framing, CellOpener authentication/replay filtering and Meta.tryRead succeed, and only for DATA/PARITY/BOOSTER. This is accepted authenticated coded-cell frame bytes, not decoded user bytes, socket-consumed bytes, TCP/IP wire cost or physical duplicated TCP packet receipt.

- Same wire cell/replay does not count twice.
- A resealed new cell_seq carrying the same source counts as a newly accepted coded-cell frame; decoder/source dedup remains separate.
- HEARTBEAT, WIN_ACK, CTRL and FLOW_STATE do not increment it.
- Authentication failure, framing failure or invalid meta does not increment it. Replay-window behavior stays unchanged.
- Authenticated coded cells count even if not innovative/decodable or if their source content is later irrelevant; the measure is frame acceptance, not successful source decode.
- Session creation resets counter/validity. JOIN and key rotation do not reset it because they retain the same RxHalf/CellOpener direction state.
- The supported maximum is the largest complete FrameLength multiple no greater than Int64.MaxValue (stored as uint64). When the next frame would exceed this maximum, latch InvalidOverflow and freeze last valid count. Continue normal protocol processing and tail repair. Never wrap or silently saturate into valid evidence.

### Experimental WIN_ACK extension layout

Tentative experimental bit 0x0800 and bytes 80–95 are not globally reserved; unpublished parallel work may conflict. Only after bilateral negotiation of this experiment:

| Offset | Size | Meaning |
|---|---:|---|
|80|1|Version = 1|
|81|1|Flags: bit0 overflow-disabled; all other bits zero. Zero flags means valid.|
|82|6|Must remain zero|
|88|8|AcceptedCodedFrameBytes cumulative uint64, little-endian|

The count always describes the WIN_ACK sender's RECEIVE direction, i.e. the peer's matching SEND direction. No copying between opposite-direction controllers. All fields are authenticated as part of the existing cell.

Keep WinAck.tryReadHead/writeHead legacy behavior strict zero for80..95. Add explicit experiment-specific read/write entry points sharing ordinary field validation (including34..35, span/path bounds), so absent opt-in never relaxes legacy parsing. The legacy short-input parser defensively returns false instead of throwing; valid legacy bytes and reserved-byte rejection remain unchanged. Reject short symbols, unknown version/flag bits and nonzero extension reserved bytes. No frame length, ACK frequency, ACK priority, THi, deficit or decoded-bitmap behavior change. Malformed extension is not observed evidence. Preserve existing treatment of authenticated meta AckStepLo before WIN_ACK body parsing; do not quietly broaden this task into ACK validation changes.

### Local generated upper bound and validation

Track GeneratedCodedFrameBytes at the common TxHalf.Seal entry point: inspect plaintext type before sealing and add FrameLength only AFTER successful Seal. This covers DATA/PARITY/BOOSTER in normal TryProduce and ProduceFill, including newly sealed repair cells; failed ProduceFill and PAD/control/heartbeat do not count. Rendered-but-not-written frames remain a safe generated upper bound, never observed delivery. The helper is enabled only for negotiated experiments and uses checked accumulation with sticky overflow invalidity.

A received evidence count must be an exact FrameLength multiple and no greater than this TX direction's valid generated coded-frame total. Reject impossible values, never clamp them; an invalid local generated bound disables this direction's evidence instead of falling back to legacy. Schema-invalid extensions reject the body. For a schema-valid extension, bound-invalid or non-frame-multiple evidence is rejected only as new accounting evidence; the original CTRL ACK, THi and NACK processing still proceeds. Earlier authenticated-meta processing remains untouched. No wire cost is inferred from this upper bound.

### Sender observation state

Expose separate PeerAcceptedCodedFrameBytes plus validity/observed flag, scoped to this Session TX direction. Initial report zero is meaningful; distinguish no report from observed zero. Valid reports update a monotonic max; repeated/reordered lower cumulative reports do not subtract or double count, and lost feedback is covered by a later cumulative value. No report-sequence/clock assumptions are needed to accumulate bytes. A lower report is ignored (cannot distinguish reordered old report from a buggy peer reset); never pretend that detects all peer implementation violations.

Once an authenticated overflow-disabled report is received, latch observation invalid for the remainder of the Session; later delayed valid reports cannot reactivate it. Malformed/unknown extension reports are rejected, not used to raise the counter. A new Session is the only reset. Fresh JOIN/path incarnation and rekey must preserve these counters. A cumulative report is not evidence of freshness or a current delivery rate. For future rate sampling, byte differences alone are not an interval estimator: delayed/batched feedback and sender/receiver time-base/rate bounds require a separately reviewed design.

## Naming/separation without a false completion claim

Add precise aliases/documentation for existing SeenThroughStepInferredCells (current UniqueArrived) and OriginalCellsRetiredByFrontier (current UniqueAcked); preserve old API/tests for compatibility. Do not relabel either as measured arrival. Existing decoder complete frontier, flow accepted/reserve state, and history storage release are distinct. Do not invent one ambiguous CustodyReleased byte total or alter ledger transitions.

Phase A minimum is new arrival/generated/peer-observed accounting alongside truthful aliases for the existing inferred-arrival and retired-original counters. Decoder complete frontier and physical history/reserve release stay separately documented; no new ambiguous release metric is introduced. A decoded-payload byte counter is deferred from this minimum unless separately approved. Unreported physical storage release remains Unknown to the sender, not inferred from AckStepLo.

Copy accounting remains visibly separate: client repair-sent bytes and server historical extra-copy arrival estimate have different semantics. Phase A does not change either RateGroup rate numerator or ProtectRecovery. Therefore phase A is NOT a capacity-controller fix. Phase B must explicitly isolate estimates from a justified rate consumer, preserve no-SACK/blackout recovery and compare both directions/asymmetry. No f2dd308 recovery heuristic, gain, ACK/gate or credit changes are imported.

## Tests and evidence required

1. Capability default bits byte-for-byte unchanged; both-on negotiated; client-on/server-off, client-off/server-on and both-off use legacy format. Unoffered selected bit rejected. Fixed old-format vectors still pass strict reserved-byte tests.
2. New parser version/flags/reserved/length validation; legacy parser rejects nonzero extension. Bad/malformed, non-frame-multiple or above-generated-bound feedback cannot increase observed counter. Count successful ProduceFill coded seals in the bound; rejected fills do not count.
3. Pure heartbeat after all tail originals lost raises THi as before but new receipt counter stays unchanged. Old tail discovery and repair test unmodified and passes.
4. DATA/PARITY/BOOSTER accepted counts correct; duplicate ciphertext/replay and auth failure count zero; resealed new sequence counts another accepted frame but duplicate source does not create another decoded payload.
5. Receiver actual frame arrival, decoded source and local socket delivery can differ under loss/parity repair/blocked local reader; avoid treating frontiers as byte counters.
6. Repeated/reordered/lost feedback produces monotonic/idempotent observation; no feedback distinguishes from zero. Overflow helper reaches boundary without sending Int64.MaxValue bytes; overflow disable sticky on both sides despite delayed older valid reports.
7. New Session reset; JOIN and real rekey preserve counters. Aggregate across paths without summing a session total once per member. Directionally asymmetric traffic must leave opposite direction counter independent.
8. Full unchanged suite, no-SACK/blackout/RepairWait/frontier and256-budget/later-open-flow credit assertions. Phase A opt-in tests can run with pacing on/off but must not claim consumers were repaired or production throughput improved.

## Remaining review and validation boundaries

- Confirm the tentative bit0x0800 and extension-field allocation with maintainers; no global reservation is claimed.
- Phase A layout/version/state semantics, signed-safe maximum, max-on-reorder and sticky overflow invalidation are implemented; independent review and final validation remain required.
- Phase A remains arrival feedback, generated-bound validation and truthful aliases, with RateGroup numerator/LastDeliveredNs entirely untouched and decoded-payload counter deferred.
- In-memory Carrier tests cover client uplinkPace on/off with asymmetric traffic in both directions, not the real server TunPath pacer or TCP. A separate product TunPath/RateGroup packet-peer fixture now complements them; real Linux kernel TCP/TUN evidence is reported separately in the [results summary](arrival-evidence-results.md), with its own workload and completion limitations.
- Historical full-solution validation on pinned `496d511` was blocked by a NativeLib ClientOffer constructor mismatch. Main fixed it in `701e228`, now integrated here; tests-project success alone is still not full-solution success.
- B1 shadow sampling is implemented separately. B2 controller-consumer policy remains unresolved and requires explicit review; this feedback work does not enable a rate-controller fix.

## Phase A validation snapshot

Tests-project Release build: 0 warnings, 0 errors. Full tests assembly: 367 passed, 0 failed, 0 skipped in 28.673 s (baseline 329 plus 38 experimental cases). This is the historical Phase A snapshot; see [consolidated results and limitations](arrival-evidence-results.md) for subsequent validation. This historical snapshot is not a full-solution green claim: clean pinned `496d511` reproduced CS7036 at ClientHost.cs:188 for the missing offerChaCha20 constructor argument. Main subsequently fixed the call in `701e228`, now integrated here; do not treat this historical failure as an unresolved current-main blocker.

Coverage includes bilateral opt-in/off negotiation, byte-level defaults, strict codec separation, bounded/sticky counters, authentication/replay/resealed-source accounting, successful/refused fill, both-direction lost-tail heartbeat and recovery, blocked local delivery, asymmetric counters, schema-valid impossible evidence preserving highwater/CTRL/NACK, cumulative report reorder/repeat/loss, new-session reset, real rekey, and client Carrier pacing on/off with two-direction traffic. JOIN coverage now includes actual coded traffic in both directions on new path 1, same-path replay, and authenticated same-cell-sequence cross-path replay (with a fresh opener proving the path-0 authentication is valid). Four authenticated invalid-Meta cases and two framing cases test the receive acceptance boundary. Eight known types test Tx/Rx coded/noncoded classification at Seal/Open, not semantic body dispatch for every type. Product TunPath TCP/RateGroup and SessionCarrier run together with a deterministic TCP packet peer, client pacing on/off, both-direction asymmetric traffic and cumulative TCP ACKs. This fixture models TCP duplicate-range discard and reports discarded duplicate bytes; it is not a real Linux kernel TCP or TUN device. Phase B consumer isolation remains outside this phase. The final packet-peer run observed 6 discarded duplicate segments (8490 bytes) in each client pacing mode, 12735 unique TCP stream bytes and 4245 accepted coded bytes. The test records duplicates but does not assert that a future run must naturally generate duplicates; this observation is not an independent physical-wire measurement. No throughput improvement is claimed.


### Remaining OS-level integration requirements

Actual kernel TCP ↔ TUN validation uses the separate [API experiment runner](../../experiments/ArrivalShadowRunner/README.md), with explicit constructor opt-in (no production CLI/default change), a Linux guest or host with `/dev/net/tun` and CAP_NET_ADMIN for namespaces/routes, and the existing official testbed isolation/cleanup controls. A QEMU minimal-Linux guest can supply these privileges without changing host networking. The packet fixture does not substitute for that run, does not validate kernel buffer/segmentation or real scheduling, and does not establish throughput or rate-estimator correctness.
