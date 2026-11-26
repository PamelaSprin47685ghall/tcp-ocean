namespace Tbt.Session

open System

/// Owner-thread repair dispatch. A candidate is removed only after the transport
/// accepts it; a failed send leaves the exact same source pending for retry.
/// Authentication and ACK validation are the caller's responsibility.
[<Sealed>]
type WindowRepairDispatch(maxSteps: int) =
    let ledger = WindowRepairLedger(maxSteps)
    let mutable active = false
    let mutable activeStep = 0UL
    let mutable activeSlot = 0
    let mutable repeated = false

    member _.Count = ledger.Count
    member _.AckFloor = ledger.AckFloor

    /// False requires the caller to retain and retry the request.
    member _.TryRequest(step: uint64, missing: uint16) =
        let accepted = ledger.TryRequest(step, missing)
        if accepted && active && step = activeStep && missing &&& (1us <<< activeSlot) <> 0us then
            repeated <- true
        accepted

    /// Repeated calls before completion return the same candidate.
    member _.TryBegin(step: byref<uint64>, slot: byref<int>) =
        if not active then
            let mutable s = 0UL
            let mutable i = 0
            if ledger.TryPeek(&s, &i) then
                activeStep <- s
                activeSlot <- i
                active <- true
        if active then
            step <- activeStep
            slot <- activeSlot
        active

    /// A failed submission does not consume the candidate. The caller must
    /// rebuild and reseal any retried frame with a fresh transport counter.
    member _.Complete(accepted: bool) =
        if not active then false
        elif not accepted then true
        else
            let removed =
                if repeated then
                    repeated <- false
                    true
                else ledger.Sent(activeStep, activeSlot)
            active <- false
            removed

    /// ACK is exclusive, authenticated and bounded by the sender's frontier.
    /// An ACK invalidates a held candidate if the peer already has that step.
    member _.AdvanceAck(exclusiveStep: uint64) =
        if not (ledger.AdvanceAck exclusiveStep) then false
        else
            if active && activeStep < exclusiveStep then
                active <- false
                repeated <- false
            true

    member _.CanReleaseThrough(exclusiveStep: uint64) =
        ledger.CanReleaseThrough exclusiveStep
        && (not active || activeStep >= exclusiveStep / 4UL * 4UL)
