# First-copy accounting for DATA repair fallback

## Scope and counter contract

A DATA source may be rendered from either the original queue or a repair fallback after the available BOOSTER seed batch no longer fits. Its first successful render and Seal counts once, regardless of queue origin. Later renders of the same absolute step/source-slot identity remain copies. Initial queued PARITY identities count once; BOOSTER and extra RenderFill PARITY/PAD retain their existing classification.

`TxHalf.UniqueRendered` is a monotonic count of those successfully rendered/sealed first identities. It is **not** confirmed socket submission, physical transmission, arrival or application delivery. `UniqueAcked` remains custody-based. `UniqueArrived` still includes the existing THi/high-water estimate: this change does not turn that estimate into exact arrival evidence or implement the separate experimental feedback protocol.

## Defect and change

Before this change, TryRenderNext incremented the per-step first-copy counter for every non-BOOSTER render. A repeated DATA fallback therefore increased UniqueAcked after custody, and UniqueArrived followed it. A real authenticated Session loss fixture uses sender HEARTBEAT and receiver-generated authenticated WIN_ACK feedback, exhausts the next fitting BOOSTER batch, delivers DATA fallback and completes the application byte. Three distinct initial DATA/PARITY identities became a count of four. The fixture uses deliberate loss and manual deadline advancement; it is not evidence of how often this happens in deployed traffic.

Each encoder ring slot now has a 16-bit rendered-DATA mask. Sealing a new absolute step clears its reused slot; custody ACK does not clear it. After a successful Seal, the corresponding source bit determines whether to add a first copy. All 16 DATA positions, including slot 15, are covered. Ordinary initial PARITY follows its existing once-queued behavior. Failed Seal does not claim a successful render; existing exception/dequeue behavior is not rolled back.

The optional client uplink pacer's SessionCarrier records the per-TryProduce UniqueRendered delta after CommitFrame returns, for either existing charge class. It no longer assumes every ordinary charged frame is unique or every repair charged frame is a copy. Existing wireOut frame charges, OnRepair/OnSent calls, path charges, admission and queue-selection rules are unchanged. A throwing CommitFrame does not book carrier submission counters; Tx generation already occurred and is not rolled back. A nonthrowing CommitFrame is not proof of a physical send.

Uplink pacing remains **off by default**. Server TunPath pacing is independent and unchanged. This is not a default 64-path throughput fix, RateGroup policy change, ACK/flush change, or a repair scheduler change. NewRepair remains off by default; both existing scheduler branches use the common render identity accounting. The separate per-range repair-history change is not included.

## Validation and boundaries

On clean 701, the standard full suite with the first expanded tests gave 340 total: 334 passed and 6 expected accounting failures; original tests passed unchanged. The same 340 passed after the minimal change. Added review-boundary tests cover:

- No-repair and BOOSTER-only controls; fallback after original DATA and fallback before original DATA.
- Actual receiver-generated authenticated WIN_ACK feedback after deliberate coded loss, with and without late reversed original-frame delivery.
- ACK before queued fallback, which exercises HasCandidate queue pruning, not encoder storage release.
- More than 512 absolute steps through a 512-slot encoder ring, and fallback under opt-in NewRepair.
- Paced in-memory path commits and exact public pace_inflight before custody, where its clamp cannot hide a discrepancy; committed frame count and authenticated metadata.
- K16 source masks, ordinary versus extra fill PARITY, PAD, failed Seal and throwing CommitFrame.

The carrier tests do not directly read private wireOut or CopyEstimate. Their unchanged charge formulas/branches are reviewed in source; the public tests verify committed frames and inflight. They are not kernel TCP/TUN tests or physical wire accounting. No new diagnostic API or private-state reflection is introduced. Final full-suite: 343/343 passed, including 14 new cases; full solution and final test rebuild both had 0 warnings/errors. Offline guard 11/11 and echo 5/5 passed; echo retained an existing background shutdown OSError 107. No official performance matrix or throughput acceptance is claimed here.

## Memory and cost

The fixed DATA mask contains 2×encoder.Capacity bytes, plus array header/alignment; the monotonic scalar adds one int64 field. Both scheduler modes allocate it. Clearing one mask slot is part of sealing a new step; DATA render performs constant-time bit accounting after successful Seal. These facts are not a measured timing guarantee. On the measured x64 runtime, independent constructor allocation comparison (three processes, nine samples each) gave:

| Encoder capacity | Clean701 TxHalf bytes | Candidate TxHalf bytes | Delta |
|---:|---:|---:|---:|
|512|718,520|719,584|1,064|
|4096|861,880|870,112|8,232|

The array costs 2C+24 bytes and extra object fields/alignment add 16, giving a measured net 2C+40 bytes per TxHalf. Each Session has one TxHalf: at default C4096 this is about 8.04 KiB per Session, allocated eagerly in both scheduler modes. Multiplying 1,000 instances gives about 7.85 MiB additional managed allocation; this is arithmetic extrapolation, not a thousand-instance run or RSS measurement. No time, throughput or zero-cost conclusion follows.

## Pre-publication revalidation caveat

A later full candidate run passed 342/343: the existing `JoinTests` refusal test failed when `JoinOffer.TryOpen` returned None. Its rejection stage and cause were not captured. Clean 701 then passed 329/329; the candidate's isolated JOIN tests passed 4/4, and one full candidate repeat passed 343/343. An independently reviewed diagnostic copy retained the original parser decisions and assertions, adding only scoped failure-stage reporting; all three bounded full runs passed 343/343 with no diagnostic output. The sample cap was reached and testing stopped. These passes do not explain or erase the first failure, establish that it is harmless, or prove that it is unrelated to the candidate. No JOIN product fix or diagnostic instrumentation is included here. Publication is not a claim of an identified JOIN root cause, official throughput acceptance, or local NativeAOT validation; CI must be reported separately.
