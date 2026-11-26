namespace Tbt.Session

open System
open System.Collections.Generic

/// Owner-thread-only bookkeeping for decoded steps and outstanding flow references.
/// This component does not authenticate frames or reserve decoder storage.
[<Sealed>]
type WindowRxProgress(maxSteps: int) =
    let steps = Dictionary<uint64, struct (int * uint16 * int)>()
    let mutable decoded = 0UL
    let mutable custody = 0UL
    do if maxSteps < 1 then invalidArg (nameof maxSteps) "must be positive"

    member _.DecodedFrontier = decoded
    member _.CustodyFrontier = custody
    member _.Count = steps.Count

    /// Set the authenticated length of a step. Contradictory lengths are rejected.
    member _.ObserveStep(step: uint64, used: int) =
        if used < 1 || used > 16 || step < custody || step - custody >= uint64 maxSteps then false
        else
            match steps.TryGetValue step with
            | true, struct (old, _, _) -> old = used
            | false, _ when steps.Count >= maxSteps -> false
            | false, _ -> steps.Add(step, struct (used, 0us, 0)); true

    /// Mark a newly retained source. False for invalid or already known slots;
    /// callers can use the result to avoid delivering the same source twice.
    member _.MarkKnown(step: uint64, slot: int) =
        match steps.TryGetValue step with
        | true, struct (used, known, refs) when slot >= 0 && slot < used ->
            let bit = 1us <<< slot
            if known &&& bit <> 0us then false
            else
                steps.[step] <- struct (used, known ||| bit, refs)
                true
        | _ -> false

    /// Return the missing source slots for an observed step.
    /// Unknown steps return ValueNone rather than an invented length.
    member _.Missing(step: uint64) =
        match steps.TryGetValue step with
        | true, struct (used, known, _) ->
            let expected = uint16 ((1u <<< used) - 1u)
            ValueSome (expected &&& ~~~known)
        | _ -> ValueNone

    /// FlowTable calls this for references into the source store.
    member _.StepUndelivered(step: uint64, delta: int) =
        match steps.TryGetValue step with
        | true, struct (used, known, refs) ->
            let next = int64 refs + int64 delta
            if next < 0L || next > int64 Int32.MaxValue then invalidArg (nameof delta) "invalid reference count"
            steps.[step] <- struct (used, known, int next)
        | _ -> invalidArg (nameof step) "unknown or released step"

    /// Invoke after the owner has delivered all newly decoded sources to FlowTable.
    /// Only contiguous complete steps advance; custody waits for references to clear.
    member _.Advance() =
        let mutable advancing = true
        while advancing && decoded <> UInt64.MaxValue do
            match steps.TryGetValue decoded with
            | true, struct (used, known, _) when uint32 known = (1u <<< used) - 1u -> decoded <- decoded + 1UL
            | _ -> advancing <- false
        advancing <- true
        while advancing && custody < decoded do
            match steps.TryGetValue custody with
            | true, struct (_, _, 0) ->
                steps.Remove custody |> ignore
                custody <- custody + 1UL
            | _ -> advancing <- false
