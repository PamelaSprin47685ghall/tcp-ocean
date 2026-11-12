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

[<Sealed>]
type RxHalf(symbolSize: int, field: CodeField, decoder: Decoder, opener: CellOpener, capacitySteps: int, maxPaths: int) =
    let counters = RxCounters()
    let hold = ParityHold(symbolSize, 2 * capacitySteps)
    /// Per path: highest step of a DATA or PARITY cell seen, and when the path last gave one.
    let pathStep: uint64[] = Array.zeroCreate maxPaths
    let pathSeenNs: int64[] = Array.zeroCreate maxPaths
    /// A path counts for the loss evidence while it gave a cell this recently.
    let pathActiveNs = 50_000_000L
    /// The frontier may wait this long for late cells before held parities are used.
    let stallNs = 20_000_000L
    let mutable frontierSince = 0L
    let mutable lastFrontier = 0UL
    /// Sources per step that wait for delivery (decoder keeps the step while not zero).
    let undelivered: int[] = Array.zeroCreate capacitySteps
    /// Lowest step that may still hold undelivered sources.
    let mutable deliveredFloor = 0UL
    /// Cells expected (k + m from meta) and received per step, for the erasure estimate.
    let expected: byte[] = Array.zeroCreate capacitySteps
    let received: byte[] = Array.zeroCreate capacitySteps
    let mutable estimateFloor = 0UL
    let mutable erasure = 0.0
    let mutable flows: FlowTable = Unchecked.defaultof<_>
    let index (t: uint64) = int (t % uint64 capacitySteps)

    let noteHistory (t: uint64) (kHist: uint32) =
        for i in 0 .. 5 do
            let r = CodeGraph.Strides.[i]
            if t >= r then decoder.NoteStepUsed(t - r, Meta.kHistAt kHist i) |> ignore

    let count (m: Meta) =
        let i = index m.StepId
        if m.StepId >= estimateFloor && m.StepId < estimateFloor + uint64 capacitySteps then
            if expected.[i] = 0uy then expected.[i] <- byte (int m.StepUsed + int (min m.Aux 48us))
            if received.[i] < 255uy then received.[i] <- received.[i] + 1uy

    /// Fold steps below the frontier into the erasure estimate (EWMA over about 256 steps).
    let foldEstimate () =
        let f = decoder.Frontier
        while estimateFloor < f do
            let i = index estimateFloor
            if expected.[i] > 0uy then
                let e = 1.0 - min 1.0 (float received.[i] / float expected.[i])
                erasure <- erasure + (e - erasure) / 256.0
            expected.[i] <- 0uy
            received.[i] <- 0uy
            estimateFloor <- estimateFloor + 1UL

    let drainKnown () =
        let mutable t = 0UL
        let mutable s = 0
        let mutable recovered = false
        let mutable e = Unchecked.defaultof<SourceEnvelope>
        while decoder.TryTakeKnown(&t, &s, &recovered) do
            if recovered then counters.Recovered <- counters.Recovered + 1L
            if Trace.enabled then Trace.record Trace.Known t 0us (if recovered then 1 else 0)
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
        for p in 0 .. maxPaths - 1 do
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
        let mutable m = Unchecked.defaultof<Meta>
        let mutable symbol = ReadOnlySpan<byte>.Empty
        while hold.TryPeek(&m, &symbol) && m.StepId < f do
            counters.ParitiesUnused <- counters.ParitiesUnused + 1L
            hold.Drop()
        if hold.Count > 0 && f < decoder.SeenEnd && (force || now - frontierSince >= stallNs || lossEvident now) then
            while hold.TryPeek(&m, &symbol) do
                if m.StepId >= decoder.Frontier then offerParity m symbol
                else counters.ParitiesUnused <- counters.ParitiesUnused + 1L
                hold.Drop()

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

    member _.ErasureQ16 = uint16 (min 65535.0 (erasure * 65536.0))

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

    member _.HeldParities = hold.Count

    /// Hand a coded cell (DATA, PARITY, BOOSTER) of path to the decoder, then deliver what
    /// is known. A parity waits in the hold while the decoder has no work for it.
    member _.OnCoded(m: Meta, symbol: ReadOnlySpan<byte>, path: uint16, now: int64) =
        let p = int path
        if Trace.enabled && (m.Type = CellType.Booster || m.Flags.HasFlag MetaFlags.EndOfBurst) then
            Trace.record Trace.Arrive m.StepId path (if m.Type = CellType.Data then 0 elif m.Type = CellType.Parity then 1 else 2)
        if p < maxPaths && (m.Type = CellType.Data || m.Type = CellType.Parity) then
            if m.StepId > pathStep.[p] then pathStep.[p] <- m.StepId
            pathSeenNs.[p] <- now
        match m.Type with
        | CellType.Data ->
            count m
            noteHistory m.StepId m.KHist
            decoder.OfferSource(m.StepId, int m.Slot, int m.StepUsed, symbol) |> ignore
        | CellType.Parity ->
            count m
            if decoder.NoteStepUsed(m.StepId, int m.StepUsed) then
                noteHistory m.StepId m.KHist
                if m.StepId < decoder.Frontier then counters.ParitiesUnused <- counters.ParitiesUnused + 1L
                elif decoder.PendingRows > 0 then offerParity m symbol
                elif hold.IsFull then offerParity m symbol
                else hold.Add(m, symbol)
        | CellType.Booster -> decoder.OfferBooster(m.StepId, int m.StepUsed, int m.Slot, symbol) |> ignore
        | _ -> counters.Unsupported <- counters.Unsupported + 1L
        drainKnown ()
        serviceHold now false
        drainKnown ()

    /// Timer: held parities go to the decoder once the frontier waited too long.
    member _.Service(now: int64) =
        serviceHold now false
        drainKnown ()

    /// HEARTBEAT: the sender has sealed step t (spec 5.8 tail rule).
    member _.OnHeartbeat(t: uint64) = decoder.NoteSeen t |> ignore

    /// NACK state (spec 5.8): the frontier step lacks rank, and a step 65 or more later was
    /// seen or the lack lasted 20 ms. Held parities go to the decoder first. Return the
    /// span and rank deficit to report, or false when no NACK is due.
    member _.TryNack(now: int64, span: byref<int>, deficit: byref<int>) =
        let f = decoder.Frontier
        if f >= decoder.SeenEnd then false
        elif decoder.SeenEnd < f + 66UL && now - frontierSince < 20_000_000L then false
        else
            serviceHold now true
            drainKnown ()
            let f = decoder.Frontier
            if f >= decoder.SeenEnd then false
            else
                span <- int (min 4UL (decoder.SeenEnd - f))
                deficit <- decoder.Deficit(f, span)
                deficit > 0
