namespace Tbt.Session

open System

/// Tracks authenticated sealed-step observations and a stalled receive frontier.
/// The caller owns loss estimation and computes the NACK deficit separately.
[<Sealed>]
type WindowRxGapTracker() =
    let mutable seenEnd = 0UL
    let mutable frontier = 0UL
    let mutable since = 0L
    let mutable lastNow = 0L
    let mutable timing = false

    member _.SeenEnd = seenEnd
    member _.Frontier = frontier

    /// Observe an authenticated inclusive sealed step, within the already
    /// advertised exclusive grant. UInt64.MaxValue has no representable successor.
    member _.ObserveSealed(step: uint64, authorizedEnd: uint64, nowNs: int64) =
        if nowNs < lastNow || step = UInt64.MaxValue || step >= authorizedEnd then false
        else
            lastNow <- nowNs
            let next = step + 1UL
            if next > seenEnd then seenEnd <- next
            if not timing && frontier < seenEnd then
                since <- nowNs
                timing <- true
            true

    /// Advance only after the decoder has established a contiguous frontier.
    member _.Advance(next: uint64, nowNs: int64) =
        if nowNs < lastNow || next < frontier || next > seenEnd then false
        else
            lastNow <- nowNs
            if next > frontier then
                frontier <- next
                since <- nowNs
            timing <- frontier < seenEnd
            true

    /// Return a candidate range, not an invented deficit for unknown steps.
    /// The caller schedules and rate-limits NACKs and provides a DATA fallback.
    member _.TryRange(nowNs: int64, span: byref<int>) =
        if nowNs < lastNow then false
        else
            lastNow <- nowNs
            if not timing || frontier >= seenEnd then false
            elif seenEnd - frontier < 66UL && nowNs - since < 20_000_000L then false
            else
                span <- int (min 4UL (seenEnd - frontier))
                true
