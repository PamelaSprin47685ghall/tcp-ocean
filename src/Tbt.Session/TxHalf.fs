/// Sending half of one direction (spec 5.7, 6.9, 6.11.1): fills steps from flow bytes,
/// keeps the step queue, and renders and seals cells when a path asks for one (late
/// binding: path_id is part of nonce and AAD, so a cell is sealAt for the path that sends it).
namespace Tbt.Session

open System
open System.Collections.Generic
open Tbt.Contracts
open Tbt.Codec
open Tbt.Crypto
open Tbt.Wire

/// One cell that waits for a path.
[<Struct>]
type Candidate =
    { Kind: CellType
      Step: uint64
      /// DATA: slot; PARITY: p; BOOSTER: booster_seq.
      Index: byte
      /// BOOSTER: span and the round total n; else 0.
      Span: byte
      Round: byte }

/// Smallest m such that P(Bin(k + m, 1 - eps) < need(k)) <= 1e-3 (spec 5.7 small steps).
module SmallStep =
    let private binomialBelow (n: int) (p: float) (need: int) =
        // P(X < need), X ~ Bin(n, p), by the recurrence on the probability mass.
        let mutable term = Math.Pow(1.0 - p, float n)
        let mutable sum = 0.0
        for x in 0 .. need - 1 do
            sum <- sum + term
            term <- term * float (n - x) / float (x + 1) * p / (1.0 - p)
        sum

    let parities (k: int) (eps: float) =
        let need = if k >= 4 then k + 1 else k
        let p = 1.0 - (max 1e-6 (min 0.95 eps))
        let mutable m = 0
        while m < 48 && binomialBelow (k + m) p need > 1e-3 do
            m <- m + 1
        m

[<Sealed>]
type TxHalf(dir: Direction, symbolSize: int, field: CodeField, encoder: Encoder, sealer: CellSealer,
            rho: float, maxPaths: int, queueLimit: int, clock: IClock) =
    let capacity = 1 <<< 16
    let stepParities: byte[] = Array.zeroCreate capacity
    let stepFlags: MetaFlags[] = Array.zeroCreate capacity
    /// Next parity index for a hole fill of the step (starts at m_t, spec 6.7.2 priority 2).
    let fillParity: byte[] = Array.zeroCreate capacity
    let mutable fillsSent = 0L
    let queue = Queue<Candidate>(4096)
    let carrierWm: uint64[] = Array.zeroCreate maxPaths
    let kHist: int[] = Array.zeroCreate 6
    let mutable filled = 0
    let mutable acc = 0.0
    let mutable nextCounter = 0UL
    let mutable peerAck = 0UL
    let mutable grantLimit = 0UL
    let mutable erasure = 0.05
    let smallParities = Array.init 17 (fun k -> if k = 0 then 0 else SmallStep.parities k 0.05)
    let mutable cellsSent = 0L
    /// First-copy DATA/PARITY cells per step (not BOOSTERs), still in the window.
    let uniqueCells: int[] = Array.zeroCreate capacity
    /// Cumulative first-copy cells the peer has in custody (sum of uniqueCells over acked steps).
    let mutable uniqueAcked = 0L
    /// Repair (spec 5.8): BOOSTER cells go before all other cells.
    let repairs = Queue<Candidate>(256)
    /// Interactive steps (spec 16.3 CR-1): one small session-level reserve, sent before the
    /// bulk queue. Bulk leaves reserveSteps of the peer's step grant to it (CR-4: no grant
    /// already given to bulk is taken back).
    let urgent = Queue<Candidate>(64)
    static let urgentLimit = 64
    static let reserveSteps = 16UL
    let mutable fillingUrgent = false
    let mutable urgentFull = 0L
    let mutable urgentSteps = 0L
    let mutable urgentSources = 0L
    let mutable boostersSent = 0L
    /// Seal time per step, for the round-trip estimate (seal to ack) and per-step
    /// expected arrival (seal + RepairWait).
    let sealNs: int64[] = Array.zeroCreate encoder.Capacity
    /// Last BOOSTER (or DATA-repair) send time for the step; 0 if never repaired.
    let lastRepairNs: int64[] = Array.zeroCreate encoder.Capacity
    /// BOOSTER seeds already used for this generation of the step (0..256).
    let repairCount: int[] = Array.zeroCreate encoder.Capacity
    /// Deficit remembered from a NACK until a due repair is sent.
    let pendingDeficit: int[] = Array.zeroCreate encoder.Capacity
    /// Deadline last inserted into dueQ for this step (stale heap entries are ignored).
    let nextDeadlineNs: int64[] = Array.zeroCreate encoder.Capacity
    /// Min-heap of (step, deadline): O(eligible), not O(in-flight).
    let dueQ = PriorityQueue<uint64, int64>()
    /// Steps sealAt as interactive (for the opt-in latency trace).
    let traced: bool[] = Array.zeroCreate capacity
    let mutable srttNs = 100_000_000.0
    /// Jacobson/Karels EWMA of |sample − SRTT|; used with SRTT for the repair deadline.
    let mutable rttvarNs = 0.0
    /// Windowed minimum of clean RTT samples: the min of this and the last 10 s bucket.
    let mutable minCurNs = Int64.MaxValue
    let mutable minPrevNs = Int64.MaxValue
    let mutable minSinceNs = 0L
    let mutable rttSkipped = 0L
    let mutable repairRepeats = 0L
    /// BOOSTERs sent for a step whose first copy later arrived (ack sooner than minRTT after the repair).
    let mutable spuriousRepairs = 0L
    /// When a BOOSTER for this step was sealed (0: none in this generation).
    let boostSentNs: int64[] = Array.zeroCreate encoder.Capacity
    let mutable lastSourceNs = 0L
    let mutable lastHeartbeatNs = 0L

    let index (t: uint64) = int (t &&& uint64 (capacity - 1))

    let seal (k: int) (small: bool) =
        let target = if fillingUrgent then urgent else queue
        let wasUrgent = fillingUrgent
        if fillingUrgent then
            urgentSteps <- urgentSteps + 1L
            urgentSources <- urgentSources + int64 k
        fillingUrgent <- false
        let t = encoder.Seal k
        traced.[index t] <- wasUrgent
        if wasUrgent then Trace.record Trace.Seal t 0us k
        let si = int (t % uint64 sealNs.Length)
        sealNs.[si] <- clock.NowNs()
        lastRepairNs.[si] <- 0L
        repairCount.[si] <- 0
        pendingDeficit.[si] <- 0
        nextDeadlineNs.[si] <- 0L
        let m =
            if small then max (int (ceil (float k * rho))) smallParities.[k]
            else
                acc <- acc + float k * rho
                let m = min 48 (int (floor acc))
                acc <- acc - float m
                m
        stepParities.[index t] <- byte m
        fillParity.[index t] <- byte m
        stepFlags.[index t] <- (if small then MetaFlags.EndOfBurst else MetaFlags.None)
        for s in 0 .. k - 1 do target.Enqueue { Kind = CellType.Data; Step = t; Index = byte s; Span = 0uy; Round = 0uy }
        for p in 0 .. m - 1 do target.Enqueue { Kind = CellType.Parity; Step = t; Index = byte p; Span = 0uy; Round = 0uy }
        filled <- 0
        t

    member _.Direction = dir
    member _.SymbolSize = symbolSize
    member _.PeerAck = peerAck
    member _.GrantLimit = grantLimit
    member _.Head = encoder.Head
    member _.Queued = queue.Count + urgent.Count
    member _.UrgentQueued = urgent.Count
    /// Interactive sources that took the bulk path because the interactive reserve was full.
    member _.UrgentFull = urgentFull
    member _.NoteUrgentFull() = urgentFull <- urgentFull + 1L
    member _.UrgentSteps = urgentSteps
    member _.UrgentSources = urgentSources
    member _.CellsSent = cellsSent
    /// First-copy DATA/PARITY cells the peer has in custody.
    member _.UniqueAcked = uniqueAcked
    member _.Erasure = erasure

    /// True when a new bulk source can go into the current step now (window and peer grant).
    /// A new step also waits while queueLimit cells wait for a path: local input then stays in
    /// the local sockets (back-pressure to the application) instead of in this queue.
    member _.CanFill =
        filled > 0
        || (encoder.Head + min reserveSteps ((grantLimit - min grantLimit peerAck) / 8UL) < grantLimit
            && encoder.HeldSteps < encoder.Capacity && queue.Count < queueLimit)

    /// True when a new interactive source can go out now: real step grant and the reserve.
    member _.CanFillUrgent =
        filled > 0 || (encoder.Head < grantLimit && encoder.HeldSteps < encoder.Capacity && urgent.Count < urgentLimit)

    /// The symbol of the next source slot. The caller writes user bytes from offset 16 on.
    /// An interactive source puts its step into the interactive queue.
    member this.TryBeginSource(symbol: byref<Span<byte>>, interactive: bool) =
        // Bulk sources do not ride in an interactive step: close it first.
        if not interactive && fillingUrgent && filled > 0 then seal filled true |> ignore
        if not (if interactive then this.CanFillUrgent else this.CanFill) then false
        else encoder.TryGetFillSlot(filled, &symbol)

    member this.TryBeginSource(symbol: byref<Span<byte>>) = this.TryBeginSource(&symbol, false)

    /// Commit the source begun last: write its envelope and zero the tail. Return the step
    /// the source belongs to.
    member _.CommitSource(e: SourceEnvelope, interactive: bool) =
        let mutable symbol = Span<byte>.Empty
        if not (encoder.TryGetFillSlot(filled, &symbol)) then invalidOp "no source slot is open"
        SourceEnvelope.write e symbol
        symbol.Slice(Layout.EnvelopeLength + e.RealLength).Clear()
        let t = encoder.Head
        if filled = 0 then lastSourceNs <- clock.NowNs()
        filled <- filled + 1
        // An interactive source marks its step urgent; the step seals at the end of the
        // input round (Flush, 0-wait) or when full, so one round's sources share a step.
        if interactive then fillingUrgent <- true
        if filled = CodeGraph.SlotsPerStep then seal filled false |> ignore
        t

    member this.CommitSource(e: SourceEnvelope) = this.CommitSource(e, false)

    /// Seal a partly filled step (input paused: 0-wait flush).
    member _.Flush() =
        if filled > 0 then seal filled true |> ignore

    /// Authenticated meta from the peer: its custody frontier and grant (spec 6.9.2, 6.9.3).
    /// ack beyond our head is invalid and changes nothing.
    member _.OnPeerAck(ack: uint64, grant: uint16) =
        if ack <= encoder.Head then
            if ack > peerAck then
                // Round-trip estimate from seal to custody of the newest acked step.
                // Karn: only a never-repaired step measures the path. A sample
                // taken behind a repair of *that* step measures the wait, not
                // the path, and would stretch later deadlines. A repair of a
                // neighbour must not silence sampling globally (8-path first
                // echo: one early BOOSTER made rtt_skipped 191, SRTT stuck at
                // 100 ms, minRTT unset until the burst ended). Of a jump, use
                // the newest never-repaired step so older gaps do not enter
                // SRTT. Credit unique cells before Release may drop StepUsed.
                let mutable tAck = peerAck
                while tAck < ack do
                    uniqueAcked <- uniqueAcked + int64 uniqueCells.[index tAck]
                    uniqueCells.[index tAck] <- 0
                    tAck <- tAck + 1UL
                let now = clock.NowNs()
                // A BOOSTER cannot complete a minRTT after it was sent: an earlier
                // ack is the original first copy arriving — the repair was spurious.
                let minRtt = min minCurNs minPrevNs
                let mutable tSp = peerAck
                while tSp < ack do
                    let bi = int (tSp % uint64 boostSentNs.Length)
                    let sentAt = boostSentNs.[bi]
                    if sentAt > 0L && minRtt <> Int64.MaxValue && now - sentAt < minRtt then
                        spuriousRepairs <- spuriousRepairs + 1L
                    boostSentNs.[bi] <- 0L
                    tSp <- tSp + 1UL
                let mutable tSamp = ack
                let mutable foundClean = false
                let mutable sealAt = 0L
                while tSamp > peerAck && not foundClean do
                    tSamp <- tSamp - 1UL
                    let si = int (tSamp % uint64 lastRepairNs.Length)
                    if lastRepairNs.[si] = 0L then
                        foundClean <- true
                        sealAt <- sealNs.[int (tSamp % uint64 sealNs.Length)]
                if not foundClean then rttSkipped <- rttSkipped + 1L
                elif now - sealAt > 0L then
                    let sample = now - sealAt
                    // Clamp to k × minRTT so one long custody delay cannot
                    // drag the session SRTT (minRTT itself stays raw).
                    let m = min minCurNs minPrevNs
                    let used =
                        if m = Int64.MaxValue then sample
                        else min sample (3L * m)
                    // RFC 6298: RTTVAR first from the pre-update SRTT, then SRTT.
                    let err = float used - srttNs
                    if rttvarNs = 0.0 then rttvarNs <- abs err / 2.0
                    else rttvarNs <- rttvarNs + (abs err - rttvarNs) / 4.0
                    srttNs <- srttNs + (float used - srttNs) / 8.0
                    if now - minSinceNs >= 10_000_000_000L then
                        minPrevNs <- minCurNs
                        minCurNs <- Int64.MaxValue
                        minSinceNs <- now
                    if sample < minCurNs then minCurNs <- sample
                peerAck <- ack
                if ack > CodeGraph.HistorySpan then encoder.Release(ack - CodeGraph.HistorySpan)
            // The grant is relative to THIS report's own ack: a late (reordered) report with an
            // older ack must not be applied to a newer peerAck (that would amplify the grant).
            let limit = ack + uint64 grant
            if limit > grantLimit then grantLimit <- limit

    /// Set the first grant (SERVER_INIT rx_win_grant0 or the peer's first cell).
    member _.InitialGrant(limit: uint64) = if limit > grantLimit then grantLimit <- limit

    /// The peer's report of its erasure rate of our cells.
    member _.OnPeerErasure(q16: uint16) =
        let e = float q16 / 65536.0
        if abs (e - erasure) > 0.01 then
            erasure <- max 0.01 e
            for k in 1 .. 16 do smallParities.[k] <- SmallStep.parities k erasure

    member _.SrttNs = srttNs
    /// Windowed minimum RTT sample (Int64.MaxValue before the first clean sample).
    member _.MinRttNs = min minCurNs minPrevNs
    /// RTT samples skipped by the Karn rule.
    member _.RttSkipped = rttSkipped
    /// Times a step was repaired again after its last-repair wait ran out.
    member _.RepairRepeats = repairRepeats
    /// Repair deadline: minRTT + measured queueing and variance
    /// (clamped SRTT + 4·RTTVAR), floored at minRTT and capped at 4×minRTT.
    /// 1.05×minRTT alone fires on any standing queue above 5% of minRTT
    /// (894194e), which turns paced bulk into a spurious BOOSTER storm.
    /// Before a min sample the value is 1.05 × session SRTT, but OnNack does
    /// not repair (expected arrival is unknown).
    member _.RepairWaitNs =
        let m = min minCurNs minPrevNs
        if m = Int64.MaxValue then 1.05 * srttNs
        else
            let wait = max srttNs (float m) + 4.0 * rttvarNs
            min (max wait (float m)) (4.0 * float m)
    member _.BoostersSent = boostersSent
    member _.SpuriousRepairs = spuriousRepairs
    member _.RttVarNs = rttvarNs

    /// Per-step eligibility: now > seal + RepairWait and, if the step was
    /// already repaired, now > lastRepair + RepairWait. No minRTT sample
    /// means no expected arrival, so nothing is due. Frontier motion does
    /// not create eligibility.
    member private this.StepDue(t: uint64, now: int64, wait: int64) =
        let cap = sealNs.Length
        let si = int (t % uint64 cap)
        let sealAt = sealNs.[si]
        let last = lastRepairNs.[si]
        let minRtt = min minCurNs minPrevNs
        minRtt <> Int64.MaxValue && t >= peerAck && t < encoder.Head && encoder.StepUsed t > 0
        && sealAt > 0L && now - sealAt >= wait && (last = 0L || now - last >= wait)

    /// Record a NACK against step t and insert it into the deadline heap.
    member private this.ArmStep(t: uint64, deficit: int, now: int64, wait: int64) =
        if t < peerAck || t >= encoder.Head || encoder.StepUsed t = 0 || deficit < 1 then ()
        else
            let si = int (t % uint64 sealNs.Length)
            let sealAt = sealNs.[si]
            if sealAt <= 0L then ()
            else
                pendingDeficit.[si] <- max pendingDeficit.[si] deficit
                let last = lastRepairNs.[si]
                let dueAt =
                    let first = sealAt + wait
                    if last = 0L then first else max first (last + wait)
                if nextDeadlineNs.[si] <> dueAt then
                    nextDeadlineNs.[si] <- dueAt
                    dueQ.Enqueue(t, dueAt)

    /// Send a due step's own deficit as BOOSTERs (or DATA once 256 seeds are used).
    member private this.SendStepRepair(t: uint64, now: int64) =
        let si = int (t % uint64 sealNs.Length)
        let deficit = pendingDeficit.[si]
        if deficit < 1 then ()
        else
            let room = max 0 (256 - repairs.Count)
            let n = min room (min 64 (int (ceil (float (deficit + 2) / (1.0 - min 0.9 erasure)))))
            if room = 0 || n < 1 then ()
            else
                if lastRepairNs.[si] > 0L then repairRepeats <- repairRepeats + 1L
                lastRepairNs.[si] <- now
                pendingDeficit.[si] <- 0
                nextDeadlineNs.[si] <- 0L
                if repairCount.[si] + n <= 256 then
                    for i in 0 .. n - 1 do
                        repairs.Enqueue
                            { Kind = CellType.Booster; Step = t; Index = byte (repairCount.[si] + i)
                              Span = 1uy; Round = byte n }
                    repairCount.[si] <- repairCount.[si] + n
                else
                    for s in 0 .. encoder.StepUsed t - 1 do
                        if repairs.Count < 256 then
                            repairs.Enqueue { Kind = CellType.Data; Step = t; Index = byte s; Span = 0uy; Round = 0uy }

    /// Pop every step whose deadline has passed; O(due this call).
    member private this.DrainDueRepairs() =
        let minRtt = min minCurNs minPrevNs
        if minRtt = Int64.MaxValue then ()
        else
            let now = clock.NowNs()
            let wait = int64 this.RepairWaitNs
            let mutable step = 0UL
            let mutable deadline = 0L
            while repairs.Count < 256 && dueQ.TryPeek(&step, &deadline) && deadline <= now do
                dueQ.Dequeue() |> ignore
                let si = int (step % uint64 nextDeadlineNs.Length)
                if nextDeadlineNs.[si] <> deadline then ()
                elif step < peerAck || pendingDeficit.[si] < 1 then
                    nextDeadlineNs.[si] <- 0L
                elif this.StepDue(step, now, wait) then
                    this.SendStepRepair(step, now)
                else
                    this.ArmStep(step, pendingDeficit.[si], now, wait)

    /// A NACK from the peer's WIN_ACK (spec 5.8): steps [tLo, tLo + span) lack deficit ranks.
    /// Each named step is armed with its share of the deficit and is sent only when
    /// that step itself is due (seal + RepairWait, and lastRepair + RepairWait).
    /// A moving frontier never makes a fresh step eligible. When the 256 seeds of
    /// a step are used, its DATA cells go again.
    member this.OnNack(tLo: uint64, span: int, deficit: int) =
        let now = clock.NowNs()
        let span = int (min (uint64 (min span CodeGraph.MaxBoosterSpan)) (encoder.Head - min tLo encoder.Head))
        let minRtt = min minCurNs minPrevNs
        if minRtt = Int64.MaxValue || tLo < peerAck || span < 1 || deficit < 1 then ()
        else
            let wait = int64 this.RepairWaitNs
            // Each named step gets its own share: a range deficit is not a
            // licence to dump the whole batch on the frontier, and a fresh
            // tail of the span is only armed, not sent, until it is due.
            let share = max 1 (int (ceil (float deficit / float span)))
            for j in 0 .. span - 1 do
                this.ArmStep(tLo + uint64 j, share, now, wait)
            this.DrainDueRepairs()

    /// Tail rule (spec 5.8): input paused for 50 ms and the peer lacks steps: a HEARTBEAT
    /// every 50 ms carries tx_head_step.
    member _.HeartbeatDue =
        let now = clock.NowNs()
        peerAck < encoder.Head && encoder.Head > 0UL && queue.Count = 0 && urgent.Count = 0 && repairs.Count = 0
        && now - lastSourceNs >= 50_000_000L && now - lastHeartbeatNs >= 50_000_000L

    /// Render and seal a HEARTBEAT for path.
    member this.RenderHeartbeat(frame: Span<byte>, path: uint16, ackLo: uint64, grant: uint16, erasureQ16: uint16) =
        lastHeartbeatNs <- clock.NowNs()
        frame.Slice(Layout.SymbolOffset, symbolSize).Clear()
        let meta =
            { Type = CellType.Heartbeat; Flags = MetaFlags.None; Slot = 0uy; StepUsed = 0uy; Aux = 0us
              RxWinGrant = grant; StepId = encoder.Head - 1UL; AckStepLo = ackLo
              CarrierWm = carrierWm.[int path]; KHist = 0u; ErasureQ16 = erasureQ16 }
        FrameMath.writeChunkHeader symbolSize frame
        Meta.write meta (frame.Slice(Layout.MetaOffset, Layout.MetaLength))
        frame.[frame.Length - 2] <- 13uy
        frame.[frame.Length - 1] <- 10uy
        this.Seal(frame, path)

    /// True when a BOOSTER (or other repair cell) is at the head of the send queues.
    member this.RepairWaiting =
        this.HasCandidate |> ignore
        repairs.Count > 0

    /// Drop queued cells of steps the peer already holds; true when a cell waits.
    member this.HasCandidate =
        this.DrainDueRepairs()
        let mutable c = Unchecked.defaultof<Candidate>
        while repairs.TryPeek(&c) && c.Step + uint64 (max 1uy c.Span) <= peerAck do
            repairs.Dequeue() |> ignore
        while urgent.TryPeek(&c) && c.Step < peerAck do
            urgent.Dequeue() |> ignore
        while queue.TryPeek(&c) && c.Step < peerAck do
            queue.Dequeue() |> ignore
        repairs.Count > 0 || urgent.Count > 0 || queue.Count > 0

    /// Seal a rendered frame with the next counter of this direction (one counter seals
    /// one plaintext only).
    member _.Seal(frame: Span<byte>, path: uint16) =
        sealer.Seal(frame, nextCounter, path)
        nextCounter <- nextCounter + 1UL
        cellsSent <- cellsSent + 1L

    /// Render the next queued DATA or PARITY cell for path into frame and seal it.
    /// ackLo, grant and erasureQ16 are this end's receive state. False when nothing waits.
    member this.TryRenderNext(frame: Span<byte>, path: uint16, ackLo: uint64, grant: uint16, erasureQ16: uint16, ?allowRepair: bool) =
        let mutable c = Unchecked.defaultof<Candidate>
        let mutable done' = false
        let allowRepair = defaultArg allowRepair true
        let mutable ok = false
        while not done' && this.HasCandidate do
            if not allowRepair && repairs.Count > 0 && urgent.Count = 0 && queue.Count = 0 then
                done' <- true
            else
                c <- if allowRepair && repairs.Count > 0 then repairs.Dequeue() elif urgent.Count > 0 then urgent.Dequeue() else queue.Dequeue()
                let t = c.Step
                let k = encoder.StepUsed t
                let symbol = frame.Slice(Layout.SymbolOffset, symbolSize)
                let rendered =
                    if k = 0 then false
                    elif c.Kind = CellType.Data then
                        encoder.Source(t, int c.Index).CopyTo symbol
                        true
                    elif c.Kind = CellType.Booster then encoder.TryRenderBooster(t, int c.Span, int c.Index, symbol)
                    else encoder.TryRenderParity(t, int c.Index, field, false, symbol)
                if rendered && TraceGate.On && traced.[index t] then
                    Trace.record Trace.Send t path (if c.Kind = CellType.Data then 0 elif c.Kind = CellType.Parity then 1 else 2)
                if rendered && c.Kind = CellType.Booster then
                    let meta =
                        { Type = CellType.Booster; Flags = MetaFlags.None; Slot = c.Index; StepUsed = c.Span
                          Aux = uint16 c.Round; RxWinGrant = grant; StepId = t; AckStepLo = ackLo
                          CarrierWm = carrierWm.[int path]; KHist = 0u; ErasureQ16 = erasureQ16 }
                    FrameMath.writeChunkHeader symbolSize frame
                    Meta.write meta (frame.Slice(Layout.MetaOffset, Layout.MetaLength))
                    frame.[frame.Length - 2] <- 13uy
                    frame.[frame.Length - 1] <- 10uy
                    this.Seal(frame, path)
                    boostersSent <- boostersSent + 1L
                    let nowB = clock.NowNs()
                    for j in 0 .. int c.Span - 1 do
                        boostSentNs.[int ((t + uint64 j) % uint64 boostSentNs.Length)] <- nowB
                    ok <- true
                    done' <- true
                elif rendered then
                    uniqueCells.[index t] <- uniqueCells.[index t] + 1
                    for i in 0 .. 5 do
                        let r = CodeGraph.Strides.[i]
                        kHist.[i] <- if t < r then 0 else (let h = encoder.StepUsed(t - r) in if h = 0 then 16 else h)
                    let meta =
                        { Type = c.Kind; Flags = stepFlags.[index t]; Slot = c.Index; StepUsed = byte k
                          Aux = uint16 stepParities.[index t]; RxWinGrant = grant; StepId = t; AckStepLo = ackLo
                          CarrierWm = carrierWm.[int path]; KHist = Meta.packKHist (ReadOnlySpan kHist); ErasureQ16 = erasureQ16 }
                    FrameMath.writeChunkHeader symbolSize frame
                    Meta.write meta (frame.Slice(Layout.MetaOffset, Layout.MetaLength))
                    frame.[frame.Length - 2] <- 13uy
                    frame.[frame.Length - 1] <- 10uy
                    if t > carrierWm.[int path] then carrierWm.[int path] <- t
                    this.Seal(frame, path)
                    ok <- true
                    done' <- true
        ok

    /// Fill for a whole-frame hole of path (borrowed shell, spec 6.7.2): a BOOSTER when one
    /// waits, else the next unsent parity of the oldest unacknowledged step, else PAD. It
    /// never carries a first-sent DATA cell. Always renders; meta.flags has FILL.
    member this.RenderFill(frame: Span<byte>, path: uint16, ackLo: uint64, grant: uint16, erasureQ16: uint16) =
        let symbol = frame.Slice(Layout.SymbolOffset, symbolSize)
        let mutable meta = Unchecked.defaultof<Meta>
        let mutable c = Unchecked.defaultof<Candidate>
        let mutable found = false
        // Only BOOSTERs: a repeated DATA cell (the seeds of a range ran out) stays queued for
        // TryRenderNext, which renders it with its own step meta.
        while not found && (this.HasCandidate |> ignore; repairs.TryPeek(&c) && c.Kind = CellType.Booster) do
            repairs.Dequeue() |> ignore
            if encoder.StepUsed c.Step > 0 && encoder.TryRenderBooster(c.Step, int c.Span, int c.Index, symbol) then
                meta <- { Type = CellType.Booster; Flags = MetaFlags.Fill; Slot = c.Index; StepUsed = c.Span
                          Aux = uint16 c.Round; RxWinGrant = grant; StepId = c.Step; AckStepLo = ackLo
                          CarrierWm = carrierWm.[int path]; KHist = 0u; ErasureQ16 = erasureQ16 }
                boostersSent <- boostersSent + 1L
                let nowB = clock.NowNs()
                for j in 0 .. int c.Span - 1 do
                    boostSentNs.[int ((c.Step + uint64 j) % uint64 boostSentNs.Length)] <- nowB
                found <- true
        // Parity: the oldest steps first; a few steps, then PAD.
        let mutable t = max peerAck encoder.Tail
        let last = min encoder.Head (t + 4UL)
        while not found && t < last do
            let k = encoder.StepUsed t
            let p = int fillParity.[index t]
            if k > 0 && p < 48 && encoder.TryRenderParity(t, p, field, false, symbol) then
                fillParity.[index t] <- byte (p + 1)
                for i in 0 .. 5 do
                    let r = CodeGraph.Strides.[i]
                    kHist.[i] <- if t < r then 0 else (let h = encoder.StepUsed(t - r) in if h = 0 then 16 else h)
                meta <- { Type = CellType.Parity; Flags = stepFlags.[index t] ||| MetaFlags.Fill; Slot = byte p
                          StepUsed = byte k; Aux = uint16 stepParities.[index t]; RxWinGrant = grant; StepId = t
                          AckStepLo = ackLo; CarrierWm = carrierWm.[int path]
                          KHist = Meta.packKHist (ReadOnlySpan kHist); ErasureQ16 = erasureQ16 }
                if t > carrierWm.[int path] then carrierWm.[int path] <- t
                found <- true
            t <- t + 1UL
        if not found then
            symbol.Clear()
            meta <- { Type = CellType.Pad; Flags = MetaFlags.Fill; Slot = 0uy; StepUsed = 0uy; Aux = 0us
                      RxWinGrant = grant; StepId = 0UL; AckStepLo = ackLo; CarrierWm = carrierWm.[int path]
                      KHist = 0u; ErasureQ16 = erasureQ16 }
        FrameMath.writeChunkHeader symbolSize frame
        Meta.write meta (frame.Slice(Layout.MetaOffset, Layout.MetaLength))
        frame.[frame.Length - 2] <- 13uy
        frame.[frame.Length - 1] <- 10uy
        this.Seal(frame, path)
        fillsSent <- fillsSent + 1L
        meta.Type

    member _.FillsSent = fillsSent

    /// carrier_wm of path (largest DATA/PARITY step sent on it).
    member _.CarrierWm(path: uint16) = carrierWm.[int path]
