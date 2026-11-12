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
      Index: byte }

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
            rho: float, maxPaths: int) =
    let capacity = 1 <<< 16
    let stepParities: byte[] = Array.zeroCreate capacity
    let stepFlags: MetaFlags[] = Array.zeroCreate capacity
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

    let index (t: uint64) = int (t &&& uint64 (capacity - 1))

    let seal (k: int) (small: bool) =
        let t = encoder.Seal k
        let m =
            if small then max (int (ceil (float k * rho))) smallParities.[k]
            else
                acc <- acc + float k * rho
                let m = min 48 (int (floor acc))
                acc <- acc - float m
                m
        stepParities.[index t] <- byte m
        stepFlags.[index t] <- (if small then MetaFlags.EndOfBurst else MetaFlags.None)
        for s in 0 .. k - 1 do queue.Enqueue { Kind = CellType.Data; Step = t; Index = byte s }
        for p in 0 .. m - 1 do queue.Enqueue { Kind = CellType.Parity; Step = t; Index = byte p }
        filled <- 0
        t

    member _.Direction = dir
    member _.SymbolSize = symbolSize
    member _.PeerAck = peerAck
    member _.GrantLimit = grantLimit
    member _.Head = encoder.Head
    member _.Queued = queue.Count
    member _.CellsSent = cellsSent
    member _.Erasure = erasure

    /// True when a new source can go into the current step now (window and peer grant).
    member _.CanFill = filled > 0 || (encoder.Head < grantLimit && encoder.HeldSteps < encoder.Capacity)

    /// The symbol of the next source slot. The caller writes user bytes from offset 16 on.
    member _.TryBeginSource(symbol: byref<Span<byte>>) =
        if filled = 0 && encoder.Head >= grantLimit then false
        else encoder.TryGetFillSlot(filled, &symbol)

    /// Commit the source begun last: write its envelope and zero the tail. Return the step
    /// the source belongs to.
    member _.CommitSource(e: SourceEnvelope) =
        let mutable symbol = Span<byte>.Empty
        if not (encoder.TryGetFillSlot(filled, &symbol)) then invalidOp "no source slot is open"
        SourceEnvelope.write e symbol
        symbol.Slice(Layout.EnvelopeLength + e.RealLength).Clear()
        let t = encoder.Head
        filled <- filled + 1
        if filled = CodeGraph.SlotsPerStep then seal filled false |> ignore
        t

    /// Seal a partly filled step (input paused: 0-wait flush).
    member _.Flush() =
        if filled > 0 then seal filled true |> ignore

    /// Authenticated meta from the peer: its custody frontier and grant (spec 6.9.2, 6.9.3).
    member _.OnPeerAck(ack: uint64, grant: uint16) =
        if ack <= encoder.Head then
            if ack > peerAck then
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

    /// Drop queued cells of steps the peer already holds; true when a cell waits.
    member _.HasCandidate =
        let mutable c = Unchecked.defaultof<Candidate>
        while queue.TryPeek(&c) && c.Step < peerAck do
            queue.Dequeue() |> ignore
        queue.Count > 0

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
            c <- queue.Dequeue()
            let t = c.Step
            let k = encoder.StepUsed t
            let symbol = frame.Slice(Layout.SymbolOffset, symbolSize)
            let rendered =
                if k = 0 then false
                elif c.Kind = CellType.Data then
                    encoder.Source(t, int c.Index).CopyTo symbol
                    true
                else encoder.TryRenderParity(t, int c.Index, field, false, symbol)
            if rendered then
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

    /// carrier_wm of path (largest DATA/PARITY step sent on it).
    member _.CarrierWm(path: uint16) = carrierWm.[int path]
