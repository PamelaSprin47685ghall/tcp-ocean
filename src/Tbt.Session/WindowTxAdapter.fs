namespace Tbt.Session

open System

/// Experimental owner-thread source scheduler. It does not seal frames, provide
/// timers or authenticate acknowledgements, and is not connected to a session.
[<Sealed>]
type WindowTxCandidates(symbolSize: int, maxWindows: int, maxRepairSteps: int) =
    let store = WindowTxStore(symbolSize, maxWindows)
    let repairs = WindowRepairDispatch(maxRepairSteps)
    let mutable head = 0UL
    let mutable used = 0
    let mutable reserved = false
    let mutable freshStep = 0UL
    let mutable freshSlot = 0
    let mutable ack = 0UL
    let mutable active = false
    let mutable activeRepair = false
    let mutable activeStep = 0UL
    let mutable activeSlot = 0

    member _.Head = head
    member _.PeerAck = ack
    member _.WindowCount = store.Count

    /// Reserve the next source; caller must commit or cancel before sealing.
    member _.TryBeginSource(dst: byref<Span<byte>>) =
        if head = UInt64.MaxValue || reserved then
            dst <- Span<byte>.Empty
            false
        else
            let ok = store.TryBeginWrite(head, used, &dst)
            reserved <- ok
            ok

    member _.CommitSource() =
        if not reserved then false
        elif store.CommitWrite(head, used) then
            reserved <- false
            used <- used + 1
            true
        else false

    member _.CancelSource() =
        if reserved then
            store.CancelWrite()
            reserved <- false

    /// Seal an incomplete step explicitly, or seal a full 16-symbol step.
    member _.SealStep() =
        if reserved || used = 0 || head = UInt64.MaxValue then false
        elif store.SealStep(head, used) then
            head <- head + 1UL
            used <- 0
            true
        else false

    /// Requests all symbols in the sealed step. False means caller must retain
    /// the NACK and retry; no missing repair is silently discarded.
    member _.TryNack(step: uint64) =
        match store.TryStepUsed step with
        | ValueSome n when step >= ack ->
            let mask = uint16 ((1u <<< n) - 1u)
            repairs.TryRequest(step, mask)
        | _ -> false

    /// Candidate remains unchanged until Complete(true), or until acknowledged.
    member _.TryPrepare(step: byref<uint64>, slot: byref<int>, isRepair: byref<bool>) =
        if not active then
            let mutable s = 0UL
            let mutable i = 0
            if repairs.TryBegin(&s, &i) then
                active <- true
                activeRepair <- true
                activeStep <- s
                activeSlot <- i
            elif freshStep < head then
                active <- true
                activeRepair <- false
                activeStep <- freshStep
                activeSlot <- freshSlot
        if active then
            step <- activeStep
            slot <- activeSlot
            isRepair <- activeRepair
        active

    member _.Source(step: uint64, slot: int) = (store :> ISourceStore).Source(step, slot)
    member _.StepUsed(step: uint64) = store.TryStepUsed step

    /// Acceptance means the transport owns a copied/sealed frame, not an ACK.
    member _.Complete(accepted: bool) =
        if not active then false
        elif not accepted then true
        elif activeRepair then
            let ok = repairs.Complete true
            if ok then active <- false
            ok
        else
            match store.TryStepUsed activeStep with
            | ValueNone -> false
            | ValueSome length ->
                freshSlot <- freshSlot + 1
                if freshSlot = length then
                    freshStep <- freshStep + 1UL
                    freshSlot <- 0
                active <- false
                true

    /// Caller authenticates ACK. Acknowledging unsent or unsealed steps is invalid.
    member _.OnAck(exclusiveStep: uint64) =
        if exclusiveStep < ack || exclusiveStep > freshStep then false
        elif not (repairs.AdvanceAck exclusiveStep) then false
        else
            ack <- exclusiveStep
            if active && activeStep < ack then active <- false
            if repairs.CanReleaseThrough ack then store.ReleaseThrough(ack, true)
            true
