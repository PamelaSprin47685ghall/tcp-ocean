/// Experimental observation only. No rate-controller consumer is changed here.
namespace Tbt.Session

open System
open Tbt.Wire

module ArrivalAccounting =
    /// A complete-frame multiple representable by a future signed rate consumer.
    let limit (frameLength: int) =
        if frameLength <= 0 then invalidArg "frameLength" "must be positive"
        (uint64 Int64.MaxValue / uint64 frameLength) * uint64 frameLength

    /// Pure checked transition permits boundary tests without generating exabytes.
    let addFrame frameLength (e: CodedArrivalEvidence) =
        let cap = limit frameLength
        if e.Overflow then e
        elif e.Bytes > cap - uint64 frameLength then { e with Overflow = true }
        else { Bytes = e.Bytes + uint64 frameLength; Overflow = false }

    [<Sealed>]
    type Counter(frameLength: int) =
        let mutable evidence = { Bytes = 0UL; Overflow = false }
        member _.Evidence = evidence
        member _.AddFrame() = evidence <- addFrame frameLength evidence

    /// Cumulative observation of the opposite receiver, in THIS TX direction.
    /// No fallback to THi, retired originals or repair-send estimates.
    [<Sealed>]
    type PeerCounter(frameLength: int) =
        let cap = limit frameLength
        let mutable seen = false
        let mutable value = 0UL
        let mutable invalid = false
        member _.Evidence =
            if seen || invalid then ValueSome { Bytes = value; Overflow = invalid }
            else ValueNone
        member _.TryObserve(report: CodedArrivalEvidence, generated: CodedArrivalEvidence) =
            if report.Bytes % uint64 frameLength <> 0UL || report.Bytes > cap
               || report.Bytes > generated.Bytes then false
            else
                // A disabled observation never resumes or switches measurement bases.
                seen <- true
                if generated.Overflow || report.Overflow then invalid <- true
                elif not invalid && report.Bytes > value then value <- report.Bytes
                true
        member _.WithGeneratedBound(generated: CodedArrivalEvidence) =
            if generated.Overflow then invalid <- true
            if seen || invalid then ValueSome { Bytes = value; Overflow = invalid }
            else ValueNone
