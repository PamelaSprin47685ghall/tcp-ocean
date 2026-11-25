/// Receiving half of one direction (spec 6.6, 6.9.1, 6.11.2): opens frames, feeds the
/// decoder, hands known sources to the flows, and computes ack_step_lo and the grant.
namespace Tbt.Session

open System
open Tbt.Contracts
open Tbt.Codec
open Tbt.Crypto
open Tbt.Wire

/// Receive counters (spec 6.6 failure targets).
[<Sealed>]
type RxCounters() =
    member val Cells = 0L with get, set
    member val FramingErrors = 0L with get, set
    member val ProtoErrors = 0L with get, set
    member val EnvelopeErrors = 0L with get, set
    member val Recovered = 0L with get, set
    member val Unsupported = 0L with get, set
    /// Parities given to the decoder, and parities dropped unused (spec 5.9 note in ParityHold).
    member val ParitiesUsed = 0L with get, set
    member val ParitiesUnused = 0L with get, set
    /// Cumulative erasure evidence for this receive direction, split by what proved it.
    ///
    /// `ErasureQ16` says how much was erased; these say why, which is the difference between
    /// a link that loses cells and a link that only delivers them out of order. A source the
    /// decoder had to rebuild is certain proof the cell did not arrive; a source marked late
    /// by the step clock alone is weaker, because normal multi-path queueing also puts a cell
    /// after its step's deadline. Splitting the two turns "reported loss is 0.24 on a lossless
    /// link" from an unattributable number into a measurement.
    ///
    /// All three are session-cumulative. The rate the controller reads is the 10 ms window in
    /// `ErasureQ16`/`ErasureP95Q16`, not a ratio of these; compare their deltas between two
    /// stats lines to see what the recent erasures were made of.
    member val TimelyDue = 0L with get, set
    member val ErasureRecovered = 0L with get, set
    member val ErasureLateClock = 0L with get, set
    /// Rebuilds withdrawn because the source's own DATA cell arrived inside the deadline.
    /// A rebuild means the decoder solved the slot by elimination before the cell showed up;
    /// the spec's erasure is "not delivered and authenticated before the deadline", so an
    /// arrival inside the deadline makes that rebuild a latency artefact, not an erasure.
    member val ErasureCancelled = 0L with get, set
    /// Sources booked into the 10 ms windows by `settlePendingSlot` — i.e. the deadline
    /// settlements of PENDING REBUILDS only.
    ///
    /// This is deliberately NOT the whole window. A source that arrives directly is booked by
    /// `observeTimely` (one accounted source, erased or not) and never passes through
    /// `settlePendingSlot`, so `Missing/Due` here is a strict subset of what `winDue`/
    /// `winErased` contain. Do not present it as the window's own total, and do not divide it
    /// and call the result the reported erasure rate: `ErasureQ16`/`ErasureP95Q16` are the
    /// window's answer, this pair only says how the rebuild part settled.
    ///
    /// `due` counts every accounted source of the settled step (missing + vindicated), `missing`
    /// only those whose cell never arrived by the deadline. `ErasureRecovered` above counts
    /// reports, not outcomes: a rebuild is reported before its deadline decides, and
    /// `ErasureCancelled` subtracts the ones a timely arrival vindicated.
    member val RebuildSettledDue = 0L with get, set
    member val RebuildSettledMissing = 0L with get, set

[<Sealed>]
type RxHalf(symbolSize: int, field: CodeField, decoder: Decoder, opener: CellOpener, capacitySteps: int, maxPaths: int) =
    let counters = RxCounters()
    let hold = ParityHold(symbolSize, 2 * capacitySteps)
    /// Per path: highest step of a DATA or PARITY cell seen, and when the path last gave one.
    let pathStep: uint64[] = Array.zeroCreate maxPaths
    let pathSeenNs: int64[] = Array.zeroCreate maxPaths
    /// Paths that ever carried a cell, compacted. The per-cell estimators used to walk
    /// 0..maxPaths-1 each time; on a four-path testbed that was 72 times the work needed.
    let pathLive: int[] = Array.zeroCreate maxPaths
    let mutable pathLiveCount = 0
    /// Bumped whenever a path's highest step advances, so the fold can skip rescans.
    let mutable pathStamp = 0L
    let mutable foldStamp = -1L
    let mutable foldedFrontier = UInt64.MaxValue
    /// A path counts for the loss evidence while it gave a cell this recently.
    let pathActiveNs = 50_000_000L
    /// Last time any path gave a cell; approximates "now" inside foldEstimate, which has
    /// no clock argument of its own.
    let mutable lastCellNs = 0L
    /// Time, advanced by the session timer as well as by cells.
    ///
    /// The window statistics are horizon-limited: a sample counts only while it is less than
    /// `winSlots` windows old. With `lastCellNs` alone, a link that goes quiet froze that
    /// horizon, so the last burst of erasures stayed "recent" for ever and every WIN_ACK kept
    /// reporting it — the peer's `rho` stayed at the level that burst justified, and the next
    /// burst carried parity for loss that had already stopped. `Service` is called every round
    /// even when nothing arrives, so letting it advance this clock makes the horizon follow
    /// real time. It only ever moves forward, and `windowAdd` still refuses to write backwards,
    /// so no expired window can be revived.
    let mutable clockNs = 0L
    /// The frontier may wait this long for late cells before held parities are used.
    /// The value is 20 ms. A 2026-11-20 experiment tried 2 ms: it raised the step rate
    /// 1.36x but moved neither the delivered bytes nor the stall rate, so it was rolled
    /// back. Keep this comment equal to the code.
    let stallNs = 20_000_000L
    let mutable frontierSince = 0L
    let mutable lastFrontier = 0UL
    let mutable lastFrontierScanNs = 0L
    let mutable lastScannedFrontier = UInt64.MaxValue
    /// Largest history stride a parity reaches back over.
    let maxHistoryStride = Seq.reduce max CodeGraph.Strides
    /// Sources per step that wait for delivery (decoder keeps the step while not zero).
    let undelivered: int[] = Array.zeroCreate capacitySteps
    /// Lowest step that may still hold undelivered sources.
    let mutable deliveredFloor = 0UL
    /// Cells expected (k + m from meta) and received per step, for the erasure estimate.
    let expected: byte[] = Array.zeroCreate capacitySteps
    let received: byte[] = Array.zeroCreate capacitySteps
    /// Which step the two arrays above currently describe. The ring reuses a slot long
    /// before the estimate folds it; without this tag a late cell of step t overwrote the
    /// counters of whatever step shared its slot, and a clean link reported one third lost.
    let expectedStep: uint64[] = Array.create capacitySteps UInt64.MaxValue
    let mutable estimateFloor = 0UL
    let mutable erasure = 0.0
    /// Last folded sample, exposed through Describe while the erasure estimate is suspect.
    let mutable lastFoldedStep = 0UL
    let mutable lastFoldedExpected = 0
    let mutable lastFoldedReceived = 0
    let mutable folds = 0L
    /// How far the fold may lag the decoder frontier before a path stops pinning it.
    /// A path whose `pathStep` sits more than this behind the frontier is slow or dead:
    /// letting it pin the fold was harmless with four paths, but on the live 256-path
    /// link one lagging path froze `estimateFloor` while the ring kept reusing slots, so
    /// the estimator folded almost nothing and reported 22 % erasure on a clean tunnel.
    let reorderSteps = uint64 (max 64 (capacitySteps / 2))
    let mutable flows: FlowTable = Unchecked.defaultof<_>
    let index (t: uint64) = int (t % uint64 capacitySteps)

    // --- Timely (deadline) erasure accounting ---------------------------------------
    // Spec 6.9.4/6.10: the FEC controller's only input is the cell erasure rate of this
    // direction, and a cell that arrives after its deadline counts as erased. The
    // permanent-loss fold below cannot see that: on a reliable TCP carrier the kernel
    // retransmits a lost segment, so the cell arrives 200 ms - 1 s late and the fold
    // records it as received. On the live 24 %-loss IPv6 link that made the estimator
    // report 0.0001 and pinned rho at its floor while 98.8 % of 16-cell steps waited for
    // a retransmission. This ledger classifies every source of a completed step exactly
    // once: recovered by FEC (certainly erased) or late by the step's own clock.
    let timelyStep: uint64[] = Array.create capacitySteps UInt64.MaxValue
    let timelyTarget: byte[] = Array.zeroCreate capacitySteps
    let timelyAccounted: int[] = Array.zeroCreate capacitySteps
    let timelyErased: int[] = Array.zeroCreate capacitySteps
    let timelyFirstNs: int64[] = Array.zeroCreate capacitySteps
    let timelyLastNs: int64[] = Array.zeroCreate capacitySteps
    /// Arrival of the median cell of the step: the step's own clock. Measuring the deadline
    /// from the FIRST arrival let one unqueued path that landed 60-100 ms early mark every
    /// healthy cell of the step as erased (the live link then reported cell_loss = 1.0000).
    let timelyMidNs: int64[] = Array.zeroCreate capacitySteps
    /// How many cells of this step have arrived, so `timelyMidNs` can be set at the median.
    let timelyArrivals: int[] = Array.zeroCreate capacitySteps
    /// DATA cells of this step that landed more than one deadline after the median arrival.
    /// Counted once each, at arrival, so a batch of steps that solve together is not judged
    /// against one shared "now".
    let timelyLateMark: int[] = Array.zeroCreate capacitySteps
    let timelyDone: bool[] = Array.zeroCreate capacitySteps
    /// This step's own cells spanned more than a second: the sender was starved, not the
    /// network. Such a step carries no timing evidence about loss (see noteTimelyArrival).
    let timelySlow: bool[] = Array.zeroCreate capacitySteps
    /// Observed intra-step arrival spread of clean steps. Cells of one step ride distinct
    /// paths and are handed to the carrier together, so on a clean link they arrive within
    /// a few ms of each other, while a cell the carrier had to retransmit arrives a whole
    /// RTT later (113 ms on the live link). The deadline sits between those two scales.
    let mutable spreadNs = 0L
    /// Tail threshold above the step's own median arrival. Normal multi-path jitter is a few
    /// ms; a cell the TCP carrier had to retransmit arrives a whole RTT late (112 ms live), so
    /// the threshold sits between the two and never below 90 ms. The 260 ms cap (was 200) covers a
    /// 112 ms-RTT multi-path link whose clean intra-step spread is tens of ms: at 200 ms a step
    /// whose last cell simply took a slow path was recorded as an erasure, and that reading fed
    /// rho. It stays a cap on *spread-derived* room, not a way to excuse a queued path: a cell
    /// delayed by a standing queue is still late and must still count.
    let deadlineNs () = min 260_000_000L (max 90_000_000L (45_000_000L + 4L * spreadNs))
    /// 10 ms windows of due and erased source cells. Spec 6.10 asks for the mean over the
    /// recent cells and the 95th percentile of the window series: a mean alone hides the
    /// bursts that FEC actually has to survive, one step at a time.
    let winNs = 10_000_000L
    let winSlots = 64
    let winOrdinal: int64[] = Array.create winSlots -1L
    let winDue: int[] = Array.zeroCreate winSlots
    let winErased: int[] = Array.zeroCreate winSlots
    let winScratch: float[] = Array.zeroCreate winSlots
    /// Carried through the SAME insertion sort as `winScratch`, so the diagnostics can name the
    /// window that actually landed at the p95 rank. Reporting the maximum-rate window instead
    /// was wrong: `p95` is a nearest-rank order statistic, and on 64 samples the rank is 60, so
    /// the extreme window often sits above it and a maximum says nothing about which sample the
    /// percentile reported.
    let winDueAt: int[] = Array.zeroCreate winSlots
    let winErasedAt: int[] = Array.zeroCreate winSlots
    /// Evidence behind the last `ErasureP95Q16` evaluation, for the 1 s stats line only.
    ///
    /// A percentile says nothing about how many samples produced it. `er_now=p95=65535` is
    /// ambiguous: it can mean "many windows were wholly erased" or "one 10 ms window held a
    /// single late cell and that one cell now sits at the 95th rank". The two imply opposite
    /// controller actions, so the count and the shape of the decisive window are reported
    /// alongside the value. Recorded inside the same loop that builds the percentile — no
    /// extra pass, no allocation, and it never influences the computed value.
    let mutable p95Candidates = 0
    let mutable p95Small = 0
    let mutable p95PickDue = 0
    let mutable p95PickErased = 0

    /// Returns true when the sample actually landed in a window slot.
    ///
    /// The write can be refused (see below), and a caller that keeps a "what the windows
    /// received" counter must not count a refused sample as booked: otherwise the counter
    /// claims evidence the controller never saw, which is worse than no counter at all.
    let windowAdd (now: int64) (due: int) (erased: int) =
        if due <= 0 then false
        else
            let o = now / winNs
            let i = int (o % int64 winSlots)
            // A slot only ever moves forward.
            //
            // Two callers pass a past instant: `settlePendingSlot` books at the step's deadline
            // (that is when the cell was absent) and a recycled ring slot settles the previous
            // step at the new step's arrival. Either can be up to a deadline — or a whole ring
            // of steps — behind the sweep. The old form reset the slot whenever the ordinal
            // differed, so such a write *erased* the newer window that already occupied the slot
            // and moved that slot's history backwards; `windowTotals` filters on `w <= o`, so the
            // successor window simply vanished from the mean and the p95. Late evidence for a
            // window that has already been superseded is dropped instead of overwriting its
            // successor: losing a stale sample is bounded and visible, whereas silently deleting
            // the newest sample feeds the controller a rate that never happened.
            if winOrdinal.[i] <= o then
                if winOrdinal.[i] < o then
                    winOrdinal.[i] <- o
                    winDue.[i] <- 0
                    winErased.[i] <- 0
                winDue.[i] <- winDue.[i] + due
                if erased > 0 then winErased.[i] <- winErased.[i] + erased
                true
            else false

    /// Rebuilds already reported but not yet settled against the step deadline.
    let pendingErased: int[] = Array.zeroCreate capacitySteps
    /// Rebuilds of the same slot that its own DATA cell vindicated inside the deadline.
    /// They are not erasures, but they WERE accounted sources of that step, so they stay in
    /// the window denominator: otherwise the mean would be erased/(due − vindicated) and a
    /// single reordered path could push it above the truth.
    let pendingCancelled: int[] = Array.zeroCreate capacitySteps
    let pendingDeadline: int64[] = Array.zeroCreate capacitySteps
    let pendingStep: uint64[] = Array.create capacitySteps UInt64.MaxValue
    let mutable pendingScan = 0

    /// Settle a bounded slice of pending rebuilds.
    ///
    /// A rebuild proves the decoder solved a slot by elimination before the cell arrived. The
    /// spec's erasure is narrower: a cell counts as erased only when it was *not* delivered and
    /// authenticated before the step's deadline. Losing on a 0 %-loss shaper was measured at
    /// 18.4 % because parity frequently wins the race across four reordered paths, and every
    /// such rebuild was booked as an erasure immediately, which lifted `rho` and spent wire.
    /// Each rebuild is now booked exactly once: cancelled if its own DATA cell arrives inside
    /// the deadline, or counted when the deadline passes without it.
    /// Settle one slot now: `due` counts every accounted source of that step, `erased` only
    /// the ones whose cell never arrived. Charging `due = erased` (the first form) made the
    /// settlement window look 100 % erased, which fed `ErasureP95Q16` and therefore `rho`
    /// even though those cells were a reordering artefact.
    let settlePendingSlot (i: int) (now: int64) =
        let missing = pendingErased.[i]
        let total = missing + pendingCancelled.[i]
        if total > 0 then
            // Book at the step's deadline, not at the moment the sweep reached it. A cell
            // counts as erased because it was absent at the deadline, so that is the instant
            // its evidence belongs to. Charging `now` moved a step's erasures into whatever
            // window happened to be current — and the sweep visits one slot per round, while
            // `noteTimelyArrival` settles a recycled slot at the NEW step's arrival, i.e. up
            // to `capacitySteps` steps after the loss. On a live session that turns old loss
            // into a periodic spike in the window the controller reads (ErasureP95Q16 drives
            // rho), which is a fabricated congestion signal. `now` is still passed so the
            // ordinary path keeps meaning "settled as of this instant".
            let at = if pendingDeadline.[i] > 0L && pendingDeadline.[i] <= now then pendingDeadline.[i] else now
            // Only a write the windows accepted is "booked". `windowAdd` refuses a sample for a
            // window that has already been superseded, and those are precisely the late
            // settlements this ledger produces (a recycled slot settles the previous step at the
            // new step's arrival, up to capacitySteps later). Counting a refused write here
            // would report evidence the controller never received — the exact ambiguity the
            // counter was added to remove.
            if windowAdd at total missing then
                counters.RebuildSettledDue <- counters.RebuildSettledDue + int64 total
                if missing > 0 then counters.RebuildSettledMissing <- counters.RebuildSettledMissing + int64 missing
            pendingErased.[i] <- 0
            pendingCancelled.[i] <- 0
            pendingStep.[i] <- UInt64.MaxValue

    /// Settle a bounded slice of pending rebuilds.
    ///
    /// A rebuild proves the decoder solved a slot by elimination before the cell arrived. The
    /// spec's erasure is narrower: a cell counts as erased only when it was *not* delivered and
    /// authenticated before the step's deadline. Losing on a 0 %-loss shaper was measured at
    /// 18.4 % because parity frequently wins the race across four reordered paths, and every
    /// such rebuild was booked as an erasure immediately, which lifted `rho` and spent wire.
    /// Each rebuild is now booked exactly once: cancelled if its own DATA cell arrives inside
    /// the deadline, or counted when the deadline passes without it.
    let settlePending (now: int64) =
        let mutable n = 0
        while n < 64 do
            let i = pendingScan
            pendingScan <- (pendingScan + 1) % capacitySteps
            if pendingErased.[i] + pendingCancelled.[i] > 0 && now > pendingDeadline.[i] then
                settlePendingSlot i now
            n <- n + 1

    /// Classify one newly known source of step t. Run exactly once per (step, slot).
    let observeTimely (now: int64) (t: uint64) (recovered: bool) =
        let i = index t
        if timelyStep.[i] = t && not timelyDone.[i] then
            let mutable target = int timelyTarget.[i]
            if target = 0 then
                let k = decoder.StepUsed t
                if k > 0 && k <= CodeGraph.SlotsPerStep then
                    target <- k
                    timelyTarget.[i] <- byte k
            timelyAccounted.[i] <- timelyAccounted.[i] + 1
            // Judge from evidence gathered at arrival: a cell FEC rebuilt, or a DATA cell that
            // landed more than one deadline after this step's median. Using the decoder's
            // report time instead counted a whole batch of steps that solved together as late.
            let erased =
                if recovered then true
                // A step of fewer than four sources is a trickle, not a burst that can lose a
                // tail: its sources are spread by the application, so only FEC evidence speaks.
                elif target < 4 || timelySlow.[i] then false
                elif timelyLateMark.[i] > 0 then
                    // Still past the adaptive deadline. Arrival alone does not prove the cell
                    // was only queued; the 75k late count was not shown to be all false loss.
                    timelyLateMark.[i] <- timelyLateMark.[i] - 1
                    true
                else false
            if erased then timelyErased.[i] <- timelyErased.[i] + 1
            counters.TimelyDue <- counters.TimelyDue + 1L
            if recovered then counters.ErasureRecovered <- counters.ErasureRecovered + 1L
            elif erased then counters.ErasureLateClock <- counters.ErasureLateClock + 1L
            if recovered then
                // Not an erasure yet: only the deadline decides (see settlePending).
                let anchor = if timelyMidNs.[i] > 0L then timelyMidNs.[i] else timelyFirstNs.[i]
                // The deadline belongs to the pending entries only. Vindicated rebuilds
                // (`pendingCancelled`) are already decided and carry no deadline, so letting
                // them suppress the refresh kept a STALE, already-expired deadline once a
                // step had one in-deadline arrival followed by another rebuild: the new
                // pending entry then settled on the first sweep as if its cell had been
                // absent, counting a loss that never happened.
                if pendingErased.[i] = 0 then
                    pendingDeadline.[i] <- anchor + deadlineNs ()
                    pendingStep.[i] <- t
                pendingErased.[i] <- pendingErased.[i] + 1
            else
                // A refused write here means a direct source's evidence missed its window
                // (bounded and visible through `er_settled`, which also excludes it).
                windowAdd now 1 (if erased then 1 else 0) |> ignore
            // Learn the healthy arrival spread from every on-time cell, not only from steps
            // with zero erasures. On the live link almost every step had at least one late
            // cell, so the old `timelyErased = 0` guard froze `spreadNs` at 0 for ever, the
            // deadline stayed at its floor, and ordinary multi-path queueing kept being
            // counted as loss: the reported rate reached 0.92 on a 24 %-loss path, which
            // then fed rho and the BOOSTER burst. On-time cells are the honest sample.
            if not erased then
                let anchor = if timelyMidNs.[i] > 0L then timelyMidNs.[i] else timelyFirstNs.[i]
                let sample = max 0L (min 30_000_000L (now - anchor))
                spreadNs <- spreadNs + (sample - spreadNs) / 32L
            if target > 0 && timelyAccounted.[i] >= target then
                timelyDone.[i] <- true
                if timelyErased.[i] = 0 then
                    // A clean step is the strongest spread sample: trust it faster.
                    let anchor = if timelyMidNs.[i] > 0L then timelyMidNs.[i] else timelyFirstNs.[i]
                    let sample = max 0L (min 30_000_000L (timelyLastNs.[i] - anchor))
                    spreadNs <- spreadNs + (sample - spreadNs) / 8L

    /// Note that a cell of step t arrived: this starts the step's ledger and its clock.
    let noteTimelyArrival (now: int64) (m: Meta) =
        let i = index m.StepId
        if timelyStep.[i] <> m.StepId then
            timelyStep.[i] <- m.StepId
            timelyTarget.[i] <-
                if (m.Type = CellType.Data || m.Type = CellType.Parity) && m.StepUsed > 0uy then m.StepUsed
                else 0uy
            timelyAccounted.[i] <- 0
            timelyErased.[i] <- 0
            timelyFirstNs.[i] <- now
            timelyMidNs.[i] <- 0L
            timelyArrivals.[i] <- 0
            timelyLateMark.[i] <- 0
            timelyDone.[i] <- false
            timelySlow.[i] <- false
            // The ring slot now describes a different step. Settle whatever the previous one
            // still owed: reusing slot i with `pendingErased` non-zero left `pendingStep`
            // pointing at the old step (so the new step's rebuilds could never be withdrawn)
            // and `pendingDeadline` stale (so they settled against the wrong deadline, i.e.
            // cross-step contamination).
            if pendingErased.[i] + pendingCancelled.[i] > 0 then settlePendingSlot i now
        timelyLastNs.[i] <- now
        timelyArrivals.[i] <- timelyArrivals.[i] + 1
        // The median arrival is this step's clock: cells of one step ride distinct paths, so
        // the first one to land can be a whole queueing delay early and must not define the
        // deadline for the rest.
        if timelyMidNs.[i] = 0L && timelyArrivals.[i] >= max 2 ((int timelyTarget.[i] + 1) / 2) then
            timelyMidNs.[i] <- now
        // A DATA cell that lands more than one deadline after the median was not delivered in
        // time, whether or not FEC later rebuilt the step; spec 6.10 counts a late cell as
        // erased. A rebuilt step is reported through `recovered` instead, so this never
        // double-counts a cell the decoder solved from parity.
        //
        // Steps of fewer than four sources are never clock-judged: a trickle step (an
        // interactive byte, a flush tail, a control-only step) is spread over time by the
        // application, not by the network, and the server's downlink run reported 0.7663
        // erasure on a 24 %-loss path because those steps were counted as loss.
        // A step whose own arrivals span a whole second was not delivered late by the network:
        // the sender emitted it over a second (credit starvation, a stalled application, a
        // paused session). Counting that as erasure pushed the reported rate to 0.52-0.77 on a
        // 24 %-loss link, which drove rho to its cap and added parity work to a decoder that
        // was already behind. FEC-rebuilt sources stay gold-standard evidence either way.
        if now - timelyFirstNs.[i] > 1_000_000_000L then timelySlow.[i] <- true
        if m.Type = CellType.Data && int timelyTarget.[i] >= 4 && not timelySlow.[i]
           && timelyMidNs.[i] > 0L && now - timelyMidNs.[i] > deadlineNs () then
            timelyLateMark.[i] <- timelyLateMark.[i] + 1

    /// Due and erased source cells over the live 10 ms windows.
    let windowTotals () =
        // The timer counts as time: see `clockNs`. Reading only the last arrival froze the
        // horizon on a quiet link and kept an old burst's rate "current" for ever.
        let now = max lastCellNs clockNs
        let mutable due = 0
        let mutable erased = 0
        if now > 0L then
            let o = now / winNs
            for i in 0 .. winSlots - 1 do
                let w = winOrdinal.[i]
                if w >= 0L && w <= o && o - w < int64 winSlots then
                    due <- due + winDue.[i]
                    erased <- erased + winErased.[i]
        struct (due, erased)

    let noteHistory (t: uint64) (kHist: uint32) =
        for i in 0 .. 5 do
            let r = CodeGraph.Strides.[i]
            if t >= r then decoder.NoteStepUsed(t - r, Meta.kHistAt kHist i) |> ignore

    /// Erasure is the source-cell loss rate: rho is parity per source, so the
    /// estimate must not mix in parity cells. `Aux` also means different things
    /// on DATA and PARITY frames, so adding it made every step whose parity had
    /// not arrived look one third lost.
    let count (m: Meta) =
        let i = index m.StepId
        if expectedStep.[i] <> m.StepId then
            expectedStep.[i] <- m.StepId
            expected.[i] <- m.StepUsed
            received.[i] <- 1uy
        elif received.[i] < 255uy then
            received.[i] <- received.[i] + 1uy

    /// Fold steps whose fate is settled into the erasure estimate (EWMA over about 256 steps).
    /// Cells of one step ride different paths, and TCP keeps order only inside one path.
    /// A path that already showed a later step cannot still deliver an earlier one, so the
    /// smallest `pathStep` over live paths is a safe folding line. A fixed reorder margin is
    /// not: at 165 Mbit/s and 40 ms RTT one path holds about 600 steps, and treating those
    /// cells as lost made a clean link report 33 % erasure and inflate rho to its ceiling.
    let foldEstimate () =
        let f = decoder.Frontier
        // The fold can only move when the frontier or some path's highest step moved:
        // rescanning every path on every cell was a full O(paths) walk per cell.
        let mutable stable = estimateFloor
        if f <> foldedFrontier || foldStamp <> pathStamp then
            foldedFrontier <- f
            foldStamp <- pathStamp
            stable <- f
            for k in 0 .. pathLiveCount - 1 do
                let p = pathLive.[k]
                // Do not gate on `pathSeenNs`: at 310 ms RTT a healthy path can go
                // longer than the 50 ms activity window without a cell, was treated
                // as dead, and the fallback then folded steps whose cells were still
                // in flight. That reported 22 % erasure on a clean tunnel. A path
                // still pins the fold through its real `pathStep`; only one that
                // lags the frontier by the whole reorder budget is overridden.
                let floorP =
                    if pathStep.[p] + reorderSteps < f then f - reorderSteps
                    else pathStep.[p]
                if floorP < stable then stable <- floorP
        while estimateFloor < stable do
            let i = index estimateFloor
            if expectedStep.[i] = estimateFloor && expected.[i] > 0uy then
                let e = 1.0 - min 1.0 (float received.[i] / float expected.[i])
                erasure <- erasure + (e - erasure) / 256.0
                lastFoldedStep <- estimateFloor
                lastFoldedExpected <- int expected.[i]
                lastFoldedReceived <- int received.[i]
                folds <- folds + 1L
            expectedStep.[i] <- UInt64.MaxValue
            expected.[i] <- 0uy
            received.[i] <- 0uy
            estimateFloor <- estimateFloor + 1UL

    /// Record that the delivery frontier moved, given the frontier as it was *before* the
    /// call that may have moved it.
    ///
    /// The snapshot has to be taken by the caller that owns the mutation. `drainKnown` used
    /// to snapshot at its own entry and compare at its exit, which cannot see an advance that
    /// happened *before* it was entered: `OnCoded` offers a cell to the decoder first (and
    /// `OfferSource`/`OfferParity`/`OfferBooster` all advance the frontier when a step
    /// completes), so the very advance caused by that cell was already in the past when
    /// `drainKnown` started. Those advances — the only ones that matter for a probe, since
    /// the probe's own cell is what completes its step — were silently missing from the trace.
    /// Every public entry point that can move the frontier now takes the snapshot itself:
    /// `OnCoded`, `Service` and `TryNack`. One record per call, `aux` = steps crossed.
    let traceFrontier (before: uint64) =
        if TraceGate.On then
            let after = decoder.Frontier
            if after > before then
                Trace.record Trace.Ack after 0us (int (min 2147483647UL (after - before)))

    let drainKnown (now: int64) =
        let mutable t = 0UL
        let mutable s = 0
        let mutable recovered = false
        let mutable e = Unchecked.defaultof<SourceEnvelope>
        while decoder.TryTakeKnown(&t, &s, &recovered) do
            if recovered then counters.Recovered <- counters.Recovered + 1L
            if TraceGate.On then Trace.record Trace.Known t 0us (if recovered then 1 else 0)
            // Timeliness of this source: a recovered slot was certainly erased, a direct
            // slot is erased when it arrived past the step's deadline (spec 6.10).
            observeTimely now t recovered
            let symbol = decoder.Source(t, s)
            if not (SourceEnvelope.tryRead symbol &e) then counters.EnvelopeErrors <- counters.EnvelopeErrors + 1L
            elif e.Flags.HasFlag StreamFlags.Pad then ()
            elif flows.OnSource(t, s, e) then flows.Pump(int e.FlowSlot)

    let offerParity (m: Meta) (symbol: ReadOnlySpan<byte>) =
        counters.ParitiesUsed <- counters.ParitiesUsed + 1L
        decoder.OfferParity(m.StepId, int m.Slot, field, m.Flags.HasFlag MetaFlags.Urgent, int m.StepUsed, symbol) |> ignore

    /// Evidence that the frontier step lost cells: every active path already gave a cell of
    /// a later step. TCP keeps the order on a path, so nothing sent earlier is still on the way.
    let lossEvident (now: int64) =
        let f = decoder.Frontier
        let mutable any = false
        let mutable all = true
        for k in 0 .. pathLiveCount - 1 do
            let p = pathLive.[k]
            if pathSeenNs.[p] <> 0L && now - pathSeenNs.[p] <= pathActiveNs then
                any <- true
                if pathStep.[p] <= f then all <- false
        any && all

    /// Give held parities to the decoder when it needs them; drop those it cannot use.
    let serviceHold (now: int64) (force: bool) =
        let f = decoder.Frontier
        if f <> lastFrontier then
            lastFrontier <- f
            frontierSince <- now
        // Frontier security first. A parity may sit behind parities of later steps in the
        // hold, and a head check then left the one parity that closes the frontier stuck:
        // the whole session waits on that step, so it must not queue behind anything.
        // A parity of step t also carries coefficients for the history steps t - stride,
        // so a few steps above the frontier can close its hole; further up cannot.
        if hold.Count > 0 && (now - lastFrontierScanNs >= 1_000_000L || f <> lastScannedFrontier) then
            lastFrontierScanNs <- now
            lastScannedFrontier <- f
            let mutable round = 0
            let mutable advanced = true
            while advanced && round < 4 do
                round <- round + 1
                advanced <- false
                let curF = decoder.Frontier
                if curF < decoder.SeenEnd && decoder.Deficit(curF, 1) > 0 then
                    let mutable i = 0
                    while not advanced && i < hold.Occupied do
                        let mutable hm = Unchecked.defaultof<Meta>
                        let mutable hns = 0L
                        let mutable hs = ReadOnlySpan<byte>.Empty
                        if hold.TryPeekAt(i, &hm, &hns, &hs) then
                            if hm.StepId >= curF && hm.StepId - curF <= maxHistoryStride then
                                offerParity hm hs
                                hold.Kill i
                                advanced <- true
                        i <- i + 1
        let mutable m = Unchecked.defaultof<Meta>
        let mutable addedNs = 0L
        let mutable symbol = ReadOnlySpan<byte>.Empty
        let mutable go = true
        while go && hold.TryPeek(&m, &addedNs, &symbol) do
            let curF = decoder.Frontier
            // The frontier's own parities were already taken above, wherever they sat, so
            // this fifo branch only has to serve the tail cases. Draining the whole hold as
            // soon as the frontier waits a few ms was tried on 2026-11-20 (2 ms) and rolled
            // back: it raised the step rate 1.36x but moved neither the delivered bytes nor
            // the stall rate, because it spent the decoder's row budget on equations whose
            // rank the frontier did not need. Keep the evidence-gated 20 ms window.
            if m.StepId < curF then
                counters.ParitiesUnused <- counters.ParitiesUnused + 1L
                hold.Drop()
            elif force
                 || now - addedNs >= stallNs
                 || (m.StepId = curF && now - frontierSince >= stallNs)
                 || (m.StepId = curF && lossEvident now) then
                offerParity m symbol
                hold.Drop()
            else
                go <- false

    member _.Counters = counters
    member _.Decoder = decoder

    /// The flow table must be attached before the first frame.
    member _.Attach(table: FlowTable) = flows <- table

    /// ack_step_lo: every step below it is decoded, checked and handed over (spec 6.9.1).
    member _.AckStepLo = decoder.Frontier

    /// rx_win_grant: the peer may send steps below decoder base + window (never decreases).
    member _.Grant =
        let limit = decoder.BaseStep + uint64 capacitySteps
        let ack = decoder.Frontier
        if limit <= ack then 0us else uint16 (min 65535UL (limit - ack))

    /// Cell erasure rate of this direction (spec 6.10): the mean over the recent due
    /// cells, counting a cell the carrier delivered only after its deadline. This is the
    /// only input the peer's FEC controller gets, so reporting permanent loss instead
    /// understated a 24 %-loss link as 0.0001 and left rho at its floor.
    member _.ErasureQ16 =
        let struct (due, erased) = windowTotals ()
        if due >= 16 then uint16 (min 65535L (int64 erased * 65536L / int64 due))
        elif due > 0 then
            // Few samples: blend with the permanent-loss fold so one busy window cannot
            // swing the ratio and a quiet link still reports something.
            let w = float due / 16.0
            uint16 (min 65535.0 ((w * float erased / float due + (1.0 - w) * erasure) * 65536.0))
        else uint16 (min 65535.0 (erasure * 65536.0))

    /// 95th percentile of the per-window erasure ratio (spec 6.10). The mean hides
    /// bursts, and each step is coded on its own, so a burst that empties one step costs
    /// a full repair round trip however good the average looked.
    member _.ErasureP95Q16 =
        let now = max lastCellNs clockNs
        let o = if now > 0L then now / winNs else -1L
        let mutable n = 0
        let mutable small = 0
        if o >= 0L then
            for i in 0 .. winSlots - 1 do
                let w = winOrdinal.[i]
                // Spec 6.10 defines this as the 95th percentile of the 10 ms WINDOW SERIES —
                // every window with at least one due cell is a sample, with no minimum sample
                // count. A window holding a single late cell is a real 10 ms observation of a
                // 100 % erased window, and it is exactly the burst evidence the mean cannot
                // carry (L1304/L1316 base the controller on a percentile for that reason).
                //
                // A `winDue >= 16` floor was tried here and is rejected: when a lossy link
                // thins the due population, most windows fall below the floor, `n` reaches 0
                // and the code fell back to the window MEAN — i.e. the percentile silently
                // became the mean exactly when burstiness mattered most, and genuine sparse
                // bursts (4 of 4 cells late) were discarded as "not a rate". The floor only
                // ever hid loss, never revealed it.
                //
                // What the floor was really trying to fix belongs to the consumer: a single
                // one-cell window at 1.00 could dominate `adaptRho`'s `0.65*p95` blend. That is
                // a weighting decision in the controller, not a licence to redefine the
                // statistic. `w < o` keeps the still-accumulating current window out, so a
                // partial window is never read as a finished sample.
                if w >= 0L && w < o && o - w < int64 winSlots && winDue.[i] > 0 then
                    let rate = float winErased.[i] / float winDue.[i]
                    winScratch.[n] <- rate
                    winDueAt.[n] <- winDue.[i]
                    winErasedAt.[n] <- winErased.[i]
                    // Diagnostics: how many candidates are one-or-few-cell windows. A sparse
                    // one-cell window reading 1.00 and a wholly-erased wide window give the same
                    // ratio but demand opposite reactions (do not let one cell weigh as one
                    // window, vs add parity), so their counts are reported separately.
                    if winDue.[i] < 16 then small <- small + 1
                    n <- n + 1
        p95Candidates <- n
        p95Small <- small
        // Cleared here, not only in the `else` branch below: with no closed window there is no
        // named window either, and leaving the previous evaluation's pair in place would make
        // the stats line describe a sample that is no longer in the horizon.
        p95PickDue <- 0
        p95PickErased <- 0
        if n = 0 then
            // No window has closed with due cells yet (a fresh or long-idle session): there is
            // no burst evidence to report, and inventing one from the permanent-loss fold would
            // be exactly the "mean standing in for a percentile" substitution rejected above.
            // Zero is the honest value here, and it is also the value §5.1 requires on a healthy
            // link, so the controller simply starts without extra parity until real windows exist.
            0us
        else
            // Nearest rank p95 over at most 64 samples, insertion sort in place. The (due,
            // erased) pair rides the same swaps so `P95Pick*` names the window that actually
            // occupies the p95 rank — the maximum-rate window is usually a different one.
            for a in 1 .. n - 1 do
                let v = winScratch.[a]
                let vd = winDueAt.[a]
                let ve = winErasedAt.[a]
                let mutable b = a - 1
                while b >= 0 && winScratch.[b] > v do
                    winScratch.[b + 1] <- winScratch.[b]
                    winDueAt.[b + 1] <- winDueAt.[b]
                    winErasedAt.[b + 1] <- winErasedAt.[b]
                    b <- b - 1
                winScratch.[b + 1] <- v
                winDueAt.[b + 1] <- vd
                winErasedAt.[b + 1] <- ve
            let rank = int (Math.Ceiling(0.95 * float n)) - 1
            let at = max 0 (min (n - 1) rank)
            p95PickDue <- winDueAt.[at]
            p95PickErased <- winErasedAt.[at]
            uint16 (min 65535.0 (winScratch.[at] * 65536.0))

    /// How many closed 10 ms windows the last `ErasureP95Q16` evaluation had, how many of
    /// them held fewer than 16 due cells, and the (due, erased) pair of the window that sits at
    /// the p95 RANK — the sample the percentile actually reported.
    ///
    /// Diagnostics only: these are read by the 1 s stats line, never by the control law, and
    /// they ride the percentile's own sort so they cost no extra pass. `er_p95` alone cannot
    /// tell a wholly-erased wide window from a one-cell window reading 1.00, and those two
    /// demand opposite reactions (more parity vs do not let one cell weigh as one window). The
    /// extreme-rate window is NOT reported: with 20 samples the nearest rank is 18, so the
    /// maximum often sits above the reported value and naming it would misdescribe the number.
    member _.P95Candidates = p95Candidates
    member _.P95SmallWindows = p95Small
    member _.P95PickDue = p95PickDue
    member _.P95PickErased = p95PickErased

    /// Selective ack (spec 6.10): bit j of the four words is step t_lo + 1 + j decoded on
    /// its own. The sender needs it to stop repairing steps that already arrived, and to
    /// tell a proven hole from a merely reordered cell.
    member _.FillDecodedBitmap() =
        let lo = decoder.Frontier
        let hi = decoder.SeenEnd
        let mutable b0 = 0UL
        let mutable b1 = 0UL
        let mutable b2 = 0UL
        let mutable b3 = 0UL
        let mutable j = 0
        while j < 256 && lo + 1UL + uint64 j < hi do
            if decoder.IsStepComplete(lo + 1UL + uint64 j) then
                let bit = 1UL <<< (j % 64)
                if j < 64 then b0 <- b0 ||| bit
                elif j < 128 then b1 <- b1 ||| bit
                elif j < 192 then b2 <- b2 ||| bit
                else b3 <- b3 ||| bit
            j <- j + 1
        struct (b0, b1, b2, b3)

    member _.EstimateFloor = estimateFloor
    member _.EstimateFolds = folds
    member _.LastFoldedStep = lastFoldedStep
    member _.LastFoldedExpected = lastFoldedExpected
    member _.LastFoldedReceived = lastFoldedReceived

    /// Delivery accounting from the flow table.
    member _.StepUndelivered(t: uint64, delta: int) =
        let i = index t
        undelivered.[i] <- undelivered.[i] + delta

    /// Free decoder storage that no equation and no flow needs any more.
    member _.Release() =
        let f = decoder.Frontier
        while deliveredFloor < f && undelivered.[index deliveredFloor] = 0 do
            deliveredFloor <- deliveredFloor + 1UL
        let history = CodeGraph.HistorySpan + 64UL
        let floor = if f > history then f - history else 0UL
        decoder.Release(min floor deliveredFloor)
        foldEstimate ()

    /// Receive pipeline steps 2..7 for one whole frame of path. Return the authenticated
    /// meta, or ValueNone when the frame was dropped (counted).
    member _.Open(frame: Span<byte>, path: uint16) =
        if not (FrameMath.checkFraming symbolSize (Span<byte>.op_Implicit frame)) then
            counters.FramingErrors <- counters.FramingErrors + 1L
            ValueNone
        else
            let mutable c = 0UL
            if not (opener.TryOpen(frame, path, &c)) then ValueNone
            else
                let mutable m = Unchecked.defaultof<Meta>
                if not (Meta.tryRead (Span<byte>.op_Implicit (frame.Slice(Layout.MetaOffset, Layout.MetaLength))) &m) then
                    counters.ProtoErrors <- counters.ProtoErrors + 1L
                    ValueNone
                else
                    counters.Cells <- counters.Cells + 1L
                    ValueSome m

    /// Open for a frame an IO thread already authenticated and decrypted (counter c):
    /// framing, the replay window and the meta parse stay here on the session thread.
    member _.OpenPre(frame: Span<byte>, c: uint64) =
        if not (FrameMath.checkFraming symbolSize (Span<byte>.op_Implicit frame)) then
            counters.FramingErrors <- counters.FramingErrors + 1L
            ValueNone
        elif not (opener.AcceptOpened c) then ValueNone
        else
            let mutable m = Unchecked.defaultof<Meta>
            if not (Meta.tryRead (Span<byte>.op_Implicit (frame.Slice(Layout.MetaOffset, Layout.MetaLength))) &m) then
                counters.ProtoErrors <- counters.ProtoErrors + 1L
                ValueNone
            else
                counters.Cells <- counters.Cells + 1L
                ValueSome m

    member _.HeldParities = hold.Count

    /// Hand a coded cell (DATA, PARITY, BOOSTER) of path to the decoder, then deliver what
    /// is known. A parity waits in the hold while the decoder has no work for it.
    member _.OnCoded(m: Meta, symbol: ReadOnlySpan<byte>, path: uint16, now: int64) =
        // Snapshot before the decoder sees this cell: OfferSource/OfferParity/OfferBooster
        // advance the frontier when the step completes, so a snapshot taken later (inside
        // drainKnown) would already include the advance caused by this very cell.
        let frontierAtEntry = decoder.Frontier
        let p = int path
        lastCellNs <- now
        if now > clockNs then clockNs <- now
        settlePending now
        // A cell of a step whose ring slot has already been reused must not take the slot back.
        //
        // `index t` is `t % capacitySteps`, so a cell of step `t - capacitySteps` maps onto the
        // slot of the current step `t`. `noteTimelyArrival` reacts to that mismatch by resetting
        // the slot to the late step: accounted/erased/target/mid/done/slow are zeroed (the
        // current step's evidence is lost, and its `done` flag is cleared, so it can complete a
        // second time), and the pending rebuild debt of the current step is settled at the late
        // frame's arrival instead of at its own deadline, which makes an in-deadline arrival
        // impossible to vindicate. The late cell still counts as an erasure — but that was
        // decided when its own step's slot was retired, and the decoder itself rejects the step
        // as out-of-window (`inWindow`), so nothing is lost by not re-opening the ledger here.
        let si = index m.StepId
        let stale =
            // Subtraction, not addition: `m.StepId + capacitySteps` can wrap near UInt64.MaxValue.
            timelyStep.[si] <> UInt64.MaxValue
            && timelyStep.[si] > m.StepId
            && timelyStep.[si] - m.StepId >= uint64 capacitySteps
        if not stale then noteTimelyArrival now m
        // Every cell of a traced step, not only a booster or the end-of-burst marker. The
        // probe's own DATA cell is the one whose arrival time decides whether the delay is
        // in the carrier or in the decoder, and it used to be the one record missing.
        if TraceGate.On then
            Trace.record Trace.Arrive m.StepId path (if m.Type = CellType.Data then 0 elif m.Type = CellType.Parity then 1 else 2)
        if p < maxPaths && (m.Type = CellType.Data || m.Type = CellType.Parity) then
            if pathSeenNs.[p] = 0L && pathLiveCount < maxPaths then
                // Register the path once; the fold and the loss evidence walk this list
                // instead of scanning all 288 ids for every cell.
                pathLive.[pathLiveCount] <- p
                pathLiveCount <- pathLiveCount + 1
            if m.StepId > pathStep.[p] then
                pathStep.[p] <- m.StepId
                pathStamp <- pathStamp + 1L
            pathSeenNs.[p] <- now
        match m.Type with
        | CellType.Data ->
            // A cell whose step's ring slot is already retired must not touch the fold
            // accounting. `count` keys on `index m.StepId`, so a stale cell writes the OLD
            // step id into `expectedStep` of the slot the CURRENT step owns, and resets that
            // slot's `expected`/`received` to the stale frame's values. The current step is
            // then never folded (foldEstimate requires `expectedStep[i] = estimateFloor`), so
            // its arrivals vanish from the permanent-loss estimate, and `noteHistory` would
            // additionally record the stale frame's k_hist against steps that is already
            // past. Same ring-slot aliasing that the timely ledger had to guard against; the
            // decoder rejects the step as out of window anyway, so nothing is lost by
            // skipping it here.
            if not stale then
                count m
                noteHistory m.StepId m.KHist
            // The step's own cell arrived, already authenticated by the carrier path. If a
            // rebuild had covered this slot, that rebuild was a race across reordered paths,
            // not a missing cell: withdraw it while the step's deadline still stands. Once the
            // deadline has passed the pending entry has been settled into the window by
            // `settlePending`, so an arrival after it stays an erasure (the spec says so).
            let si = index m.StepId
            if pendingStep.[si] = m.StepId && pendingErased.[si] > 0 && now <= pendingDeadline.[si]
               && decoder.WasRecovered(m.StepId, int m.Slot) then
                pendingErased.[si] <- pendingErased.[si] - 1
                pendingCancelled.[si] <- pendingCancelled.[si] + 1
                // The source was not erased, so the step's own tally must say so: that tally
                // is what marks a step "clean" and lets `spreadNs` learn the real intra-step
                // arrival spread the deadline is built from. Leaving it incremented meant no
                // step that ever had a vindicated rebuild could teach spread, so the deadline
                // stayed at its floor and ordinary jitter kept being read as loss.
                timelyErased.[si] <- max 0 (timelyErased.[si] - 1)
                // Idempotent per slot: a repair copy or a TCP retransmission delivers the same
                // DATA cell twice, and the second copy used to withdraw a second pending
                // erasure while `WasRecovered` was still true.
                decoder.AcknowledgeRecovered(m.StepId, int m.Slot)
                counters.ErasureCancelled <- counters.ErasureCancelled + 1L
            decoder.OfferSource(m.StepId, int m.Slot, int m.StepUsed, symbol) |> ignore
        | CellType.Parity ->
            if decoder.NoteStepUsed(m.StepId, int m.StepUsed) then
                noteHistory m.StepId m.KHist
                if m.StepId < decoder.Frontier then counters.ParitiesUnused <- counters.ParitiesUnused + 1L
                else
                    // Under real loss a held parity costs a frontier stall: the decoder
                    // cannot use it until the hold releases it, and the hold is a FIFO
                    // whose head may be a later step. Feed at once when the link is
                    // actually lossy, or when this parity is for the step that blocks
                    // the frontier. The hold then only serves clean links, where a
                    // parity no one needs would cost elimination work for nothing.
                    let struct (due, erased) = windowTotals ()
                    let lossy = due >= 16 && erased * 20 >= due
                    if lossy || m.StepId <= decoder.Frontier + 1UL then offerParity m symbol
                    else
                        if hold.IsFull then
                            let mutable hm = Unchecked.defaultof<Meta>
                            let mutable hns = 0L
                            let mutable hs = ReadOnlySpan<byte>.Empty
                            if hold.TryPeek(&hm, &hns, &hs) then
                                if hm.StepId < decoder.Frontier then counters.ParitiesUnused <- counters.ParitiesUnused + 1L
                                else offerParity hm hs
                                hold.Drop()
                        hold.Add(m, now, symbol)
        | CellType.Booster -> decoder.OfferBooster(m.StepId, int m.StepUsed, int m.Slot, symbol) |> ignore
        | _ -> counters.Unsupported <- counters.Unsupported + 1L
        drainKnown now
        serviceHold now false
        drainKnown now
        // Release() is the only other caller and runs on its own slow cadence; folding here
        // keeps the estimate advancing even when Release is skipped or rare.
        foldEstimate ()
        traceFrontier frontierAtEntry

    /// Timer: held parities go to the decoder once the frontier waited too long.
    member _.Service(now: int64) =
        let frontierAtEntry = decoder.Frontier
        if now > clockNs then clockNs <- now
        // Settlement is time-driven, not frame-driven. On a quiet link no cell arrives, yet a
        // rebuild whose deadline has passed is a real erasure and must still be booked;
        // frame-only settlement silently dropped those to zero, so a direction that had
        // genuinely lost cells reported less and less erasure the longer it stayed idle.
        settlePending now
        serviceHold now false
        drainKnown now
        traceFrontier frontierAtEntry

    /// HEARTBEAT: the sender has sealed step t (spec 5.8 tail rule).
    member _.OnHeartbeat(t: uint64) = decoder.NoteSeen t |> ignore

    /// NACK state (spec 5.8): the frontier step lacks rank, and a step 65 or more later was
    /// seen or the lack lasted 20 ms. Held parities go to the decoder first. Return the
    /// span and rank deficit to report, or false when no NACK is due.
    member _.TryNack(now: int64, span: byref<int>, deficit: byref<int>) =
        let frontierAtEntry = decoder.Frontier
        let f = decoder.Frontier
        if f >= decoder.SeenEnd then false
        elif decoder.SeenEnd < f + 66UL && now - frontierSince < 20_000_000L then false
        else
            serviceHold now true
            drainKnown now
            // The only place in TryNack where the frontier can move.
            traceFrontier frontierAtEntry
            let f = decoder.Frontier
            if f >= decoder.SeenEnd then false
            else
                span <- int (min 4UL (decoder.SeenEnd - f))
                deficit <- decoder.Deficit(f, span)
                deficit > 0
