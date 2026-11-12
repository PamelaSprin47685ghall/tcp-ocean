/// Sending half of one direction (spec 5.7, 6.9, 6.11.1): fills steps from flow bytes,
/// keeps the step queue, and renders and seals cells when a path asks for one (late
/// binding: path_id is part of nonce and AAD, so a cell is sealed for the path that sends it).
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
    let mutable repairLo = UInt64.MaxValue
    let mutable repairSpan = 0
    let mutable repairSeq = 0
    let mutable repairDeficit = 0
    let mutable repairNs = 0L
    let mutable boostersSent = 0L
    /// Seal time per step, for the round-trip estimate (seal to ack).
    let sealNs: int64[] = Array.zeroCreate encoder.Capacity
    let mutable srttNs = 100_000_000.0
    let mutable lastSourceNs = 0L
    let mutable lastHeartbeatNs = 0L

    let index (t: uint64) = int (t &&& uint64 (capacity - 1))

    let seal (k: int) (small: bool) =
        let target = if fillingUrgent then urgent else queue
        if fillingUrgent then
            urgentSteps <- urgentSteps + 1L
            urgentSources <- urgentSources + int64 k
        fillingUrgent <- false
        let t = encoder.Seal k
        sealNs.[int (t % uint64 sealNs.Length)] <- clock.NowNs()
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
    member _.OnPeerAck(ack: uint64, grant: uint16) =
        if ack <= encoder.Head then
            if ack > peerAck then
                // Round-trip estimate from seal to custody of the newest acked step.
                let sample = float (clock.NowNs() - sealNs.[int ((ack - 1UL) % uint64 sealNs.Length)])
                if sample > 0.0 then srttNs <- srttNs + (sample - srttNs) / 8.0
                peerAck <- ack
                if ack > CodeGraph.HistorySpan then encoder.Release(ack - CodeGraph.HistorySpan)
            let limit = peerAck + uint64 grant
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
    member _.BoostersSent = boostersSent

    /// A NACK from the peer's WIN_ACK (spec 5.8): steps [tLo, tLo + span) lack deficit ranks.
    /// One round sends n = ceil((deficit + 2) / (1 - eps)) BOOSTERs; rounds for the same
    /// range wait 1.05 SRTT unless the deficit grew. When the 256 seeds of a range are used,
    /// the range's DATA cells go again (unit rows fill the missing columns).
    member _.OnNack(tLo: uint64, span: int, deficit: int) =
        let now = clock.NowNs()
        let span = int (min (uint64 (min span CodeGraph.MaxBoosterSpan)) (encoder.Head - min tLo encoder.Head))
        if tLo >= peerAck && span >= 1 && deficit >= 1 && encoder.StepUsed tLo > 0 then
            let same = tLo = repairLo && span = repairSpan
            if not same || deficit > repairDeficit || float (now - repairNs) >= 1.05 * srttNs then
                if not same then
                    repairLo <- tLo
                    repairSpan <- span
                    repairSeq <- 0
                repairDeficit <- deficit
                repairNs <- now
                let n = min 64 (int (ceil (float (deficit + 2) / (1.0 - min 0.9 erasure))))
                if repairSeq + n <= 256 then
                    for i in 0 .. n - 1 do
                        repairs.Enqueue { Kind = CellType.Booster; Step = tLo; Index = byte (repairSeq + i)
                                          Span = byte span; Round = byte n }
                    repairSeq <- repairSeq + n
                else
                    for j in 0 .. span - 1 do
                        let t = tLo + uint64 j
                        for s in 0 .. encoder.StepUsed t - 1 do
                            repairs.Enqueue { Kind = CellType.Data; Step = t; Index = byte s; Span = 0uy; Round = 0uy }

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

    /// Drop queued cells of steps the peer already holds; true when a cell waits.
    member _.HasCandidate =
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
    member this.TryRenderNext(frame: Span<byte>, path: uint16, ackLo: uint64, grant: uint16, erasureQ16: uint16) =
        let mutable c = Unchecked.defaultof<Candidate>
        let mutable done' = false
        let mutable ok = false
        while not done' && this.HasCandidate do
            c <- if repairs.Count > 0 then repairs.Dequeue() elif urgent.Count > 0 then urgent.Dequeue() else queue.Dequeue()
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
                ok <- true
                done' <- true
            elif rendered then
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
        while not found && (this.HasCandidate |> ignore; repairs.TryDequeue(&c)) do
            if encoder.StepUsed c.Step > 0 && encoder.TryRenderBooster(c.Step, int c.Span, int c.Index, symbol) then
                meta <- { Type = CellType.Booster; Flags = MetaFlags.Fill; Slot = c.Index; StepUsed = c.Span
                          Aux = uint16 c.Round; RxWinGrant = grant; StepId = c.Step; AckStepLo = ackLo
                          CarrierWm = carrierWm.[int path]; KHist = 0u; ErasureQ16 = erasureQ16 }
                boostersSent <- boostersSent + 1L
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
