namespace Tbt.Session

open System
open System.Collections.Generic
open Tbt.Codec
open Tbt.Wire

/// Window parity needs authenticated metadata unavailable to the legacy codec API.
type IWindowEncoderMeta =
    abstract TryRenderWindowParity: uint64 * int * Span<byte> * byref<Meta> -> bool

/// Experimental adapter. Legacy parity and booster methods cannot represent window metadata.
[<Sealed>]
type WindowStepEncoder(symbolSize: int, capacitySteps: int) =
    do
        if symbolSize <= 0 || symbolSize > Int32.MaxValue / 16 then invalidArg (nameof symbolSize) "invalid symbol size"
        if capacitySteps < 16 || capacitySteps > 640 || capacitySteps % 4 <> 0 then
            invalidArg (nameof capacitySteps) "must be a multiple of four in 16..640"
    let maxWindows = capacitySteps / 4
    let store = WindowTxStore(symbolSize, maxWindows)
    let staging = Array.zeroCreate<byte> (16 * symbolSize)
    let mutable head = 0UL
    let mutable tail = 0UL
    let mutable opened = false
    let mutable filled = 0us
    member _.SymbolSize = symbolSize
    member _.Capacity = capacitySteps
    member _.Head = head
    member _.Tail = tail
    member _.HeldSteps = int (head - tail) + (if opened then 1 else 0)
    member _.TryGetFillSlot(slot: int, target: byref<Span<byte>>) =
        if slot < 0 || slot >= 16 then invalidArg (nameof slot) "slot out of range"
        // A full store can still accept the remaining steps in its newest window.
        if not opened && (head - tail >= uint64 capacitySteps || (head % 4UL = 0UL && store.Count >= maxWindows)) then
            target <- Span<byte>.Empty
            false
        else
            opened <- true
            filled <- filled ||| (1us <<< slot)
            target <- Span(staging, slot * symbolSize, symbolSize)
            true
    member _.Seal(k: int) =
        if not opened then invalidOp "no step is being filled"
        if k < 1 || k > 16 then invalidArg (nameof k) "step_used out of range"
        if head = UInt64.MaxValue then invalidOp "step number exhausted"
        if filled <> uint16 ((1u <<< k) - 1u) then invalidOp "sources must be a contiguous prefix"
        for slot = 0 to k - 1 do
            if not (store.TryWrite(head, slot, ReadOnlySpan(staging, slot * symbolSize, symbolSize))) then
                invalidOp "window source storage cannot accept sealed step"
        if not (store.SealStep(head, k)) then invalidOp "window step could not be sealed"
        let t = head
        head <- head + 1UL
        opened <- false
        filled <- 0us
        Array.Clear staging
        t
    member _.StepUsed(t: uint64) =
        if t < tail || t >= head then 0
        else match store.TryStepUsed t with ValueSome k -> k | ValueNone -> 0
    member _.Source(t: uint64, slot: int) : ReadOnlySpan<byte> =
        if t < tail || t >= head then ReadOnlySpan<byte>.Empty
        else (store :> ISourceStore).Source(t, slot)
    member _.TryRenderWindowParity(t: uint64, p: int, dst: Span<byte>, meta: byref<Meta>) =
        if t < tail || t >= head then false
        else
            match store.TryRenderParity(WindowBridge.windowId t, p, dst) with
            | ValueSome result -> meta <- result; true
            | ValueNone -> false
    member _.Release(below: uint64) =
        let complete = (min below head / 4UL) * 4UL
        if complete > tail then
            store.ReleaseThrough(complete, true)
            tail <- complete
    interface IWindowEncoderMeta with
        member x.TryRenderWindowParity(t, p, dst, meta) = x.TryRenderWindowParity(t, p, dst, &meta)
    interface ICodecEncoder with
        member x.SymbolSize = x.SymbolSize
        member x.Capacity = x.Capacity
        member x.Head = x.Head
        member x.Tail = x.Tail
        member x.HeldSteps = x.HeldSteps
        member x.TryGetFillSlot(slot, dst) = x.TryGetFillSlot(slot, &dst)
        member x.Seal(k) = x.Seal(k)
        member x.StepUsed(t) = x.StepUsed(t)
        member x.Source(t, slot) = x.Source(t, slot)
        member _.TryRenderParity(_, _, _, _, _) = false
        member _.TryRenderBooster(_, _, _, _) = false
        member x.Release(below) = x.Release(below)

/// Experimental authenticated snapshot receiver. The caller must finish each
/// borrowed known source after registering any durable flow references.
[<Sealed>]
type WindowSnapshotDecoder(symbolSize: int, maxWindows: int, maxBytes: int64) =
    let custody = WindowCustody(symbolSize, maxWindows, maxBytes, reserveCount = maxWindows)
    let gap = WindowRxGapTracker()
    let known = Queue<struct (uint64 * int * bool)>()
    let recovered = HashSet<struct (uint64 * int)>()
    let borrowed = Dictionary<uint64, int>()
    let mutable inParity = false
    let mutable conflicts = 0L
    let deliver = Action<uint64, int>(fun step slot ->
        if inParity then recovered.Add(struct (step, slot)) |> ignore
        custody.StepUndelivered(step, 1)
        known.Enqueue(struct (step, slot, inParity)))
    do custody.NextGrant() |> ignore
    member _.Frontier = custody.DecodedFrontier
    member _.CustodyFrontier = custody.CustodyFrontier
    member _.PublishedLimit = custody.PublishedLimit
    member _.Conflicts = conflicts
    member _.AllocatedBytes = custody.AllocatedBytes
    member _.ReservedBytes = custody.ReservedBytes
    member _.MaxWindows = maxWindows
    member _.SeenEnd = gap.SeenEnd
    member _.NextGrant() = custody.NextGrant()
    member _.StepUsed(step: uint64) = custody.TryStepUsed step
    member _.Missing(step: uint64) = custody.MissingKnown step
    /// Only authenticated, fully decoded steps are complete. Unknown lengths
    /// must not be mistaken for empty or completed steps.
    member _.IsStepComplete(step: uint64) =
        match custody.MissingKnown step with
        | ValueSome mask -> mask = 0us
        | ValueNone -> false
    member _.StepKnownCount(step: uint64) =
        match custody.TryStepUsed step, custody.MissingKnown step with
        | ValueSome used, ValueSome missing ->
            used - System.Numerics.BitOperations.PopCount(uint32 missing)
        | _ -> 0
    /// Bit j represents step fromStep+j. No wrap past the step number limit.
    member this.BuildDecodedBitmap(fromStep: uint64) =
        let mutable a = 0UL
        let mutable b = 0UL
        let mutable c = 0UL
        let mutable d = 0UL
        for j = 0 to 255 do
            if uint64 j <= UInt64.MaxValue - fromStep && this.IsStepComplete(fromStep + uint64 j) then
                let bit = 1UL <<< (j % 64)
                match j / 64 with
                | 0 -> a <- a ||| bit
                | 1 -> b <- b ||| bit
                | 2 -> c <- c ||| bit
                | _ -> d <- d ||| bit
        struct (a, b, c, d)
    /// Unknown step lengths are conservatively treated as up to 16 missing slots.
    member _.Deficit(tLo: uint64, span: int) =
        if span < 0 || span > 256 then invalidArg (nameof span) "must be in 0..256"
        let mutable total = 0
        for i = 0 to span - 1 do
            if uint64 i <= UInt64.MaxValue - tLo then
                match custody.MissingKnown(tLo + uint64 i) with
                | ValueSome missing -> total <- total + System.Numerics.BitOperations.PopCount(uint32 missing)
                | ValueNone -> total <- total + 16
        total
    member _.ObserveEnd(endExclusive: uint64, nowNs: int64) =
        if endExclusive = 0UL then true
        else gap.ObserveSealed(endExclusive - 1UL, custody.PublishedLimit, nowNs)
    member _.OfferSource(step: uint64, slot: int, used: int, symbol: ReadOnlySpan<byte>) =
        let result = custody.AddSource(step, slot, used, symbol)
        match result with
        | WindowCodec.AddOutcome.Innovative
        | WindowCodec.AddOutcome.Duplicate -> custody.DeliverAvailable(step / 4UL, deliver) |> ignore
        | WindowCodec.AddOutcome.Invalid -> conflicts <- conflicts + 1L
        | _ -> ()
        result
    member _.OfferWindowParity(meta: Meta, symbol: ReadOnlySpan<byte>) =
        let result = custody.AddParity(meta, symbol)
        match result with
        | WindowCodec.AddOutcome.Innovative
        | WindowCodec.AddOutcome.Duplicate ->
            inParity <- true
            try custody.DeliverAvailable(meta.StepId / 4UL, deliver) |> ignore
            finally inParity <- false
        | WindowCodec.AddOutcome.Invalid -> conflicts <- conflicts + 1L
        | _ -> ()
        result
    member _.TryTakeKnown(step: byref<uint64>, slot: byref<int>, isRecovered: byref<bool>) =
        if known.Count = 0 then false
        else
            let struct (s, i, r) = known.Dequeue()
            step <- s
            slot <- i
            isRecovered <- r
            let mutable count = 0
            borrowed.TryGetValue(s, &count) |> ignore
            borrowed.[s] <- count + 1
            true
    member _.FinishKnown(step: uint64) =
        match borrowed.TryGetValue step with
        | true, count when count > 0 ->
            if count = 1 then borrowed.Remove step |> ignore
            else borrowed.[step] <- count - 1
            custody.StepUndelivered(step, -1)
        | _ -> invalidOp "no borrowed source for this step"
    member _.StepUndelivered(step: uint64, delta: int) = custody.StepUndelivered(step, delta)
    member _.WasRecovered(step: uint64, slot: int) = recovered.Contains(struct (step, slot))
    member _.AcknowledgeRecovered(step: uint64, slot: int) = recovered.Remove(struct (step, slot)) |> ignore
    member _.Source(step: uint64, slot: int) = (custody :> ISourceStore).Source(step, slot)
    member _.Advance(nowNs: int64) =
        custody.Advance()
        gap.Advance(custody.DecodedFrontier, nowNs) |> ignore
        let floor = custody.CustodyFrontier - custody.CustodyFrontier % 4UL
        recovered.RemoveWhere(Predicate<_>(fun struct (s, _) -> s < floor)) |> ignore
    member _.TryNackRange(nowNs: int64, span: byref<int>) = gap.TryRange(nowNs, &span)
    interface ISourceStore with
        member _.Source(step, slot) = (custody :> ISourceStore).Source(step, slot)
