namespace Tbt.Session

open System
open System.Collections.Generic
open Tbt.Codec
open Tbt.Wire

/// Owner-thread-only bounded source custody for experimental window coding.
[<Sealed>]
type private TxWindow(symbolSize: int) =
    let bytes = Array.zeroCreate<byte> (64 * symbolSize)
    let lengths = Array.zeroCreate<int> 4
    let mutable present = 0UL
    let mutable sealedCount = 0
    let mutable nextParity = 0
    member _.Bytes = bytes
    member _.Lengths = lengths
    member _.Present with get() = present and set value = present <- value
    member _.SealedCount with get() = sealedCount and set value = sealedCount <- value
    member _.NextParity with get() = nextParity and set value = nextParity <- value

/// Each accepted source remains stable until its whole window is acknowledged and
/// the caller has explicitly confirmed that no outstanding references remain.
[<Sealed>]
type WindowTxStore(symbolSize: int, maxWindows: int) =
    let windows = Dictionary<uint64, TxWindow>()
    let mutable releasedBefore = 0UL
    // One owner-thread reservation at a time. The returned span is valid only
    // until CommitWrite or CancelWrite; it must not escape that interval.
    let mutable reservedStep = 0UL
    let mutable reservedSlot = -1
    do
        if symbolSize <= 0 || symbolSize > Int32.MaxValue / 64 then invalidArg (nameof symbolSize) "invalid symbol size"
        if maxWindows <= 0 then invalidArg (nameof maxWindows) "must be positive"

    member _.Count = windows.Count

    /// Reserves one writable slot; it is invisible to readers until committed.
    member _.TryBeginWrite(step: uint64, slot: int, target: byref<Span<byte>>) =
        target <- Span<byte>.Empty
        let id = WindowBridge.windowId step
        match WindowBridge.tryIndex (step, slot) with
        | ValueNone -> false
        | ValueSome _ when reservedSlot >= 0 || id < releasedBefore -> false
        | ValueSome index ->
            let mutable w = Unchecked.defaultof<TxWindow>
            let found = windows.TryGetValue(id, &w)
            if not found && windows.Count >= maxWindows then false
            else
                if not found then
                    w <- TxWindow(symbolSize)
                    windows.Add(id, w)
                let bit = 1UL <<< index
                if int (step % 4UL) < w.SealedCount || w.Present &&& bit <> 0UL then false
                else
                    reservedStep <- step
                    reservedSlot <- slot
                    target <- Span(w.Bytes, index * symbolSize, symbolSize)
                    true

    member _.CommitWrite(step: uint64, slot: int) =
        if reservedSlot <> slot || reservedSlot < 0 || reservedStep <> step then false
        else
            let w = windows.[WindowBridge.windowId step]
            let index = int (step % 4UL) * 16 + slot
            w.Present <- w.Present ||| (1UL <<< index)
            reservedSlot <- -1
            true

    member _.CancelWrite() =
        if reservedSlot >= 0 then
            let id = WindowBridge.windowId reservedStep
            let w = windows.[id]
            let index = int (reservedStep % 4UL) * 16 + reservedSlot
            Span(w.Bytes, index * symbolSize, symbolSize).Clear()
            reservedSlot <- -1
            if w.Present = 0UL && w.SealedCount = 0 then windows.Remove id |> ignore

    member _.TryWrite(step: uint64, slot: int, data: ReadOnlySpan<byte>) =
        let id = WindowBridge.windowId step
        match WindowBridge.tryIndex (step, slot) with
        | ValueNone -> false
        | ValueSome _ when reservedSlot >= 0 || id < releasedBefore || data.Length <> symbolSize -> false
        | ValueSome index ->
            let mutable w = Unchecked.defaultof<TxWindow>
            let found = windows.TryGetValue(id, &w)
            if not found && windows.Count >= maxWindows then false
            else
                if not found then
                    w <- TxWindow(symbolSize)
                    windows.Add(id, w)
                let bit = 1UL <<< index
                if int (step % 4UL) < w.SealedCount || w.Present &&& bit <> 0UL then false
                else
                    data.CopyTo(Span(w.Bytes, index * symbolSize, symbolSize))
                    w.Present <- w.Present ||| bit
                    true

    /// Sealing requires a contiguous prefix of steps and exactly stepUsed sources.
    member _.SealStep(step: uint64, stepUsed: int) =
        let id = WindowBridge.windowId step
        match windows.TryGetValue id with
        | false, _ -> false
        | true, w ->
            let offset = int (step % 4UL)
            if reservedSlot >= 0 && reservedStep = step then false
            elif stepUsed < 1 || stepUsed > 16 || offset <> w.SealedCount then false
            else
                let actual = (w.Present >>> (offset * 16)) &&& 0xFFFFUL
                let expected = (1UL <<< stepUsed) - 1UL
                if actual <> expected then false
                else
                    w.Lengths.[offset] <- stepUsed
                    w.SealedCount <- offset + 1
                    true

    /// Returns metadata only after rendering succeeds. Indices never repeat within a window.
    member _.TryRenderParity(windowId: uint64, parityIdx: int, dst: Span<byte>) : Meta voption =
        match windows.TryGetValue windowId with
        | false, _ -> ValueNone
        | true, w when windowId > UInt64.MaxValue / 4UL || dst.Length <> symbolSize || parityIdx <> w.NextParity || parityIdx >= 48 || w.SealedCount = 0 -> ValueNone
        | true, w ->
            let mutable history = 0u
            for i = 0 to w.SealedCount - 1 do
                history <- history ||| (uint32 (w.Lengths.[i] - 1) <<< (i * 4))
            let tail = (w.SealedCount - 1) * 16 + w.Lengths.[w.SealedCount - 1]
            let meta = WindowBridge.parityMeta { WindowId = windowId; FirstStep = WindowBridge.firstStep windowId; ParityIdx = parityIdx; Tail = tail }
            let meta = { meta with KHist = history }
            let mask = (WindowBridge.trySnapshotParityMask meta).Value
            dst.Clear()
            for i = 0 to 63 do
                if mask &&& (1UL <<< i) <> 0UL then
                    WindowCodec.xorInto (ReadOnlySpan(w.Bytes, i * symbolSize, symbolSize)) dst
            w.NextParity <- parityIdx + 1
            ValueSome meta

    /// Caller must establish absence of outstanding source references before release.
    member _.ReleaseThrough(peerAck: uint64, noPendingReferences: bool) =
        if noPendingReferences && reservedSlot < 0 then
            let before = peerAck / 4UL
            let remove = ResizeArray<uint64>()
            for KeyValue(id, _) in windows do
                if id < before then remove.Add id
            for id in remove do windows.Remove id |> ignore
            releasedBefore <- max releasedBefore before

    interface ISourceStore with
        member _.Source(step, slot) =
            match WindowBridge.tryIndex (step, slot), windows.TryGetValue(WindowBridge.windowId step) with
            | ValueSome index, (true, w) when w.Present &&& (1UL <<< index) <> 0UL ->
                ReadOnlySpan<byte>(w.Bytes, index * symbolSize, symbolSize)
            | _ -> ReadOnlySpan<byte>.Empty
