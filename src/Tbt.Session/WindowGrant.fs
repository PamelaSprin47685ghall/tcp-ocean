namespace Tbt.Session

open System

/// Tracks an irrevocable exclusive step limit. One owner must reserve the
/// corresponding window capacity before publishing a grant or advancing custody.
[<Sealed>]
type WindowGrantTracker(maxWindows: int) =
    do
        if maxWindows < 1 || maxWindows > 160 then
            invalidArg (nameof maxWindows) "window capacity must be in 1..160"

    let span = min 512UL (uint64 maxWindows * 4UL)
    let mutable floor = 0UL
    let mutable lastAck = 0UL
    let mutable published = 0UL

    let safeLimit start =
        if UInt64.MaxValue - start < span then UInt64.MaxValue else start + span

    member _.FloorWindowStart = floor
    member _.PublishedLimit = published
    member _.SafeExclusive = safeLimit floor

    /// Acknowledge only previously granted steps, in order. Call first with zero
    /// to publish the initial grant before sending the handshake.
    member _.NextGrant(ackStep: uint64) =
        if ackStep < lastAck || ackStep > published then
            invalidArg (nameof ackStep) "acknowledgement is stale or exceeds published credit"
        let limit = safeLimit floor
        if ackStep > limit || published > limit then
            invalidOp "published credit exceeds reserved capacity"
        let grant = min (uint64 UInt16.MaxValue) (limit - ackStep)
        let next = ackStep + grant
        lastAck <- ackStep
        published <- max published next
        uint16 grant

    /// Advance only to a fully released, four-step-aligned custody frontier.
    /// The caller must establish that no pending source refers to those windows.
    member _.AdvanceCustody(exclusiveStep: uint64) =
        if exclusiveStep % 4UL <> 0UL || exclusiveStep < floor || exclusiveStep > lastAck then
            invalidArg (nameof exclusiveStep) "custody frontier must be aligned, monotonic and acknowledged"
        floor <- exclusiveStep
