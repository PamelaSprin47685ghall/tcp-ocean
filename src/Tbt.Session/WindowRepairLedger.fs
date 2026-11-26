namespace Tbt.Session

open System
open System.Collections.Generic

/// Owner-thread-only repair ledger. Each tracked sealed step owns its source
/// references until the caller confirms an accepted send or a valid custody ACK.
/// This type schedules candidates; framing, retransmission timers and ACK
/// authentication belong to the caller.
[<Sealed>]
type WindowRepairLedger(maxSteps: int) =
    let pending = SortedDictionary<uint64, uint16>()
    let mutable ack = 0UL
    do if maxSteps <= 0 then invalidArg (nameof maxSteps) "must be positive"

    member _.Count = pending.Count
    member _.AckFloor = ack

    /// Merge missing slots. False means capacity was unavailable; the caller must
    /// retain the NACK and retry it, rather than silently dropping the request.
    member _.TryRequest(step: uint64, missing: uint16) =
        if step < ack || missing = 0us then false
        else
            match pending.TryGetValue step with
            | true, current -> pending.[step] <- current ||| missing; true
            | false, _ when pending.Count >= maxSteps -> false
            | false, _ -> pending.Add(step, missing); true

    /// Peek without consuming. A failed or blocked send leaves the candidate live.
    member _.TryPeek(step: byref<uint64>, slot: byref<int>) =
        let mutable e = pending.GetEnumerator()
        if not (e.MoveNext()) then false
        else
            let entry = e.Current
            step <- entry.Key
            slot <- System.Numerics.BitOperations.TrailingZeroCount(uint32 entry.Value)
            true

    /// Only call after the transport accepted this candidate for transmission.
    member _.Sent(step: uint64, slot: int) =
        if slot < 0 || slot >= 16 then false
        else
            match pending.TryGetValue step with
            | false, _ -> false
            | true, bits ->
                let bit = 1us <<< slot
                if bits &&& bit = 0us then false
                else
                    let rest = bits &&& ~~~bit
                    if rest = 0us then pending.Remove step |> ignore
                    else pending.[step] <- rest
                    true

    /// ACK is an exclusive custody frontier and must already be authenticated
    /// and checked against the last sent step by the caller. Never moves back.
    member _.AdvanceAck(exclusiveStep: uint64) =
        if exclusiveStep < ack then false
        else
            ack <- exclusiveStep
            let obsolete = ResizeArray<uint64>()
            for KeyValue(step, _) in pending do
                if step < ack then obsolete.Add step
            for step in obsolete do pending.Remove step |> ignore
            true

    /// No pending reference exists to any complete window below this boundary.
    member _.CanReleaseThrough(exclusiveStep: uint64) =
        if exclusiveStep > ack then false
        else
            let complete = exclusiveStep / 4UL * 4UL
            let mutable safe = true
            let mutable e = pending.GetEnumerator()
            if e.MoveNext() && e.Current.Key < complete then safe <- false
            safe
