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
type WindowCustody(symbolSize: int, maxWindows: int, maxBytes: int64) =
    let receiver = WindowReceive(symbolSize, maxWindows, maxBytes)
    let progress = WindowRxProgress(maxWindows * 4)
    let grant = WindowGrantTracker(maxWindows)
    do
        let perWindow = 64L * 8L + 65L * int64 symbolSize
        if int64 maxWindows * perWindow > maxBytes then
            invalidArg (nameof maxBytes) "insufficient decoder capacity for advertised windows"
    let mutable limit = 0UL

    member _.AllocatedBytes = receiver.AllocatedBytes
    member _.DecodedFrontier = progress.DecodedFrontier
    member _.CustodyFrontier = progress.CustodyFrontier
    member _.PublishedLimit = limit

    /// Publish only after the caller has reserved all resources promised by the grant.
    member _.NextGrant() =
        let ack = progress.DecodedFrontier
        let g = grant.NextGrant ack
        limit <- grant.PublishedLimit
        g

    /// Metadata and payload must have passed AEAD authentication before this call.
    member _.AddSource(step: uint64, slot: int, used: int, data: byte[]) =
        if step >= limit then WindowCodec.AddOutcome.Invalid
        else receiver.TryAddSource(step, slot, used, data)

    /// A parity snapshot must describe only steps already covered by the grant.
    member _.AddParity(meta: Meta, data: byte[]) =
        match WindowBridge.trySnapshotParityMask meta with
        | ValueNone -> WindowCodec.AddOutcome.Invalid
        | ValueSome _ ->
            let steps = (uint64 meta.Aux - 1UL) / 16UL + 1UL
            if meta.StepId > UInt64.MaxValue - steps || meta.StepId + steps > limit then
                WindowCodec.AddOutcome.Invalid
            else receiver.TryAddParity(meta, data)

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
        progress.Advance()
        let ack = progress.DecodedFrontier
        // Advance the tracker's acknowledged frontier, but do not grant credit
        // against the new custody floor until NextGrant is explicitly called.
        // The old floor is still in force here, so this cannot enlarge limit.
        grant.NextGrant ack |> ignore
        let floor = progress.CustodyFrontier - progress.CustodyFrontier % 4UL
        grant.AdvanceCustody floor
        receiver.ReleaseThrough floor

    interface ISourceStore with
        member _.Source(step, slot) = receiver.Source(step, slot)
