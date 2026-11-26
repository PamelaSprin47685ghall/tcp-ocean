namespace Tbt.Session

open System
open Tbt.Codec
open Tbt.Wire

/// Experimental owner-thread receive custody. The caller authenticates all frames,
/// reserves capacity before advertising grants, and registers flow references before
/// calling Advance. Source spans are borrowed: callers must prevent reentrant Advance
/// or release until each consumer has finished using its span. Budget checks cover
/// decoder row payload only, not allocations, flow buffers, or operating-system memory.
/// This type is not connected to Session or to the wire handshake.
[<Sealed>]
type WindowCustody(symbolSize: int, maxWindows: int, maxBytes: int64, ?reserveCount: int) =
    let receiver = WindowReceive(symbolSize, maxWindows, maxBytes, ?reserveCount = reserveCount)
    let progress = WindowRxProgress(maxWindows * 4)
    let grant = WindowGrantTracker(maxWindows)
    do
        let perWindow = 64L * 8L + 65L * int64 symbolSize
        if int64 maxWindows * perWindow > maxBytes then
            invalidArg (nameof maxBytes) "insufficient decoder capacity for advertised windows"
    let mutable limit = 0UL
    let mutable deliveringWindow = false

    member _.AllocatedBytes = receiver.AllocatedBytes
    member _.ActiveWindows = receiver.Count
    /// Reserved matrix payload; excludes object overhead and other session buffers.
    member _.ReservedBytes = receiver.Pool.ReservedBytes
    member _.IdleReservedCount = receiver.Pool.IdleReservedCount
    member _.DecodedFrontier = progress.DecodedFrontier
    member _.CustodyFrontier = progress.CustodyFrontier
    member _.PublishedLimit = limit

    /// Authenticated step length, or unknown before DATA/snapshot or after release.
    member _.TryStepUsed(step: uint64) = receiver.TryStepUsed step

    /// Missing decoded slots, not merely slots awaiting flow-table delivery.
    /// An entirely unseen step has no authenticated length and returns ValueNone.
    member _.MissingKnown(step: uint64) =
        match receiver.TryStepUsed step with
        | ValueNone -> ValueNone
        | ValueSome used ->
            let mutable missing = 0us
            for slot = 0 to used - 1 do
                if receiver.Source(step, slot).IsEmpty then
                    missing <- missing ||| (1us <<< slot)
            ValueSome missing

    /// Publish only after the caller has reserved all resources promised by the grant.
    member _.NextGrant() =
        let ack = progress.DecodedFrontier
        let g = grant.NextGrant ack
        limit <- grant.PublishedLimit
        g

    /// Metadata and payload must have passed AEAD authentication before this call.
    member _.AddSource(step: uint64, slot: int, used: int, data: ReadOnlySpan<byte>) =
        if step >= limit then WindowCodec.AddOutcome.Invalid
        else receiver.TryAddSource(step, slot, used, data)

    member this.AddSource(step: uint64, slot: int, used: int, data: byte[]) =
        if isNull (box data) then WindowCodec.AddOutcome.Invalid
        else this.AddSource(step, slot, used, ReadOnlySpan data)

    /// A parity snapshot must describe only steps already covered by the grant.
    member _.AddParity(meta: Meta, data: ReadOnlySpan<byte>) =
        match WindowBridge.trySnapshotParityMask meta with
        | ValueNone -> WindowCodec.AddOutcome.Invalid
        | ValueSome _ ->
            let steps = (uint64 meta.Aux - 1UL) / 16UL + 1UL
            if meta.StepId > UInt64.MaxValue - steps || meta.StepId + steps > limit then
                WindowCodec.AddOutcome.Invalid
            else receiver.TryAddParity(meta, data)

    member this.AddParity(meta: Meta, data: byte[]) =
        if isNull (box data) then WindowCodec.AddOutcome.Invalid
        else this.AddParity(meta, ReadOnlySpan data)

    /// Deliver a source while holding a temporary custody reference. The callback
    /// registers any durable flow references via StepUndelivered. If it throws,
    /// keep the temporary reference: its side effects are unknown, so releasing
    /// the source or delivering it again could corrupt the stream.
    member _.DeliverKnown(step: uint64, slot: int, deliver: Action<uint64, int>) =
        if isNull (box deliver) then nullArg (nameof deliver)
        match receiver.TryStepUsed step with
        | ValueNone -> false
        | ValueSome used ->
            if not (progress.ObserveStep(step, used)) || receiver.Source(step, slot).IsEmpty then false
            elif not (progress.MarkKnown(step, slot)) then false
            else
                progress.StepUndelivered(step, 1)
                deliver.Invoke(step, slot)
                progress.StepUndelivered(step, -1)
                true

    /// Deliver every known, authenticated source in this window at most once.
    /// Defer reentrant Advance until the batch has registered all references.
    member this.DeliverAvailable(windowId: uint64, deliver: Action<uint64, int>) =
        if isNull (box deliver) then nullArg (nameof deliver)
        if windowId > (UInt64.MaxValue - 3UL) / 4UL then 0
        elif deliveringWindow then invalidOp "reentrant window delivery"
        else
            deliveringWindow <- true
            try
                let first = windowId * 4UL
                let mutable delivered = 0
                for offset = 0 to 3 do
                    let step = first + uint64 offset
                    match receiver.TryStepUsed step with
                    | ValueNone -> ()
                    | ValueSome used ->
                        for slot = 0 to used - 1 do
                            if this.DeliverKnown(step, slot, deliver) then delivered <- delivered + 1
                delivered
            finally
                deliveringWindow <- false

    /// Returns a newly discovered source once. The caller must immediately register
    /// all resulting FlowTable references before Advance or any reentrant release.
    member _.TakeKnown(step: uint64, slot: int) =
        match receiver.TryStepUsed step with
        | ValueNone -> false
        | ValueSome used ->
            if not (progress.ObserveStep(step, used)) then false
            elif receiver.Source(step, slot).IsEmpty then false
            else progress.MarkKnown(step, slot)

    /// Return the missing source slots for a known step after TakeKnown has
    /// registered its authenticated length. Unknown steps remain unknown.
    member _.Missing(step: uint64) = progress.Missing step

    member _.StepUndelivered(step: uint64, delta: int) = progress.StepUndelivered(step, delta)

    /// Call after all discovered symbols have been delivered to the local flow table.
    /// This is a local custody boundary, not proof the remote application has read.
    member _.Advance() =
        if not deliveringWindow then
            progress.Advance()
            let ack = progress.DecodedFrontier
            // Keep the old floor in force when acknowledging the new frontier.
            grant.NextGrant ack |> ignore
            let floor = progress.CustodyFrontier - progress.CustodyFrontier % 4UL
            grant.AdvanceCustody floor
            receiver.ReleaseThrough floor

    interface ISourceStore with
        member _.Source(step, slot) = receiver.Source(step, slot)
