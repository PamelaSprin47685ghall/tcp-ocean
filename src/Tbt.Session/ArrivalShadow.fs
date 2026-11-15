/// Explicitly constructed experiment only. Nothing in Session/Carrier instantiates this.
/// This channel never supplies controller delivery, charges, recovery or ACK decisions.
namespace Tbt.Session

open System
open Tbt.Contracts
open Tbt.Wire

module ArrivalShadow =
    [<Struct>]
    type Identity = { SessionId: Guid; Direction: Direction }

    type InvalidReason = Overflow | InvalidGeneratedBound | ReceiverTimestampRegression | ReceiverTimestampOutOfRange
    type FeedbackStatus = Unknown | FreshFeedback | StaleFeedback | Invalid of InvalidReason

    [<Struct>]
    type Anchor = { Bytes: uint64; ReceiverNs: int64; LocalNs: int64 }

    [<Struct>]
    type Sample =
        { From: Anchor; Until: Anchor; DeltaBytes: uint64; CoverageNs: int64
          ObservedCodedBytesPerSecond: float }
        member _.ShadowOnly = true

    type Outcome =
        | Disabled | RejectedEvidence | RejectedLocalTime
        | IgnoredReordered | IgnoredRepeated | Baseline | FeedbackOnly | Deferred
        | Sampled of Sample | Invalidated of InvalidReason

    type Snapshot =
        { Identity: Identity; Negotiated: bool; Status: FeedbackStatus
          HighestObservedBytes: uint64; GrowthAnchor: Anchor option
          LastFeedbackLocalNs: int64 option; LastSample: Sample option
          FeedbackAgeNs: int64 option; SampleAgeNs: int64 option }
        member _.ShadowOnly = true
        /// No synchronized remote clock or bounded feedback delay exists.
        member _.AbsoluteRemoteAgeNs: int64 option = None

    [<Sealed>]
    type Sampler(identity: Identity, negotiated: bool, frameLength: int, staleAfterNs: int64) =
        let cap = ArrivalAccounting.limit frameLength
        do
            if identity.SessionId = Guid.Empty then invalidArg "identity" "session identity required"
            if identity.Direction <> Direction.Up && identity.Direction <> Direction.Down then invalidArg "identity" "direction required"
            if staleAfterNs <= 0L then invalidArg "staleAfterNs" "must be positive"
        let mutable anchor: Anchor option = None
        let mutable highest = 0UL
        let mutable generatedHigh = 0UL
        let mutable remote: int64 option = None
        let mutable feedbackAt: int64 option = None
        let mutable lastLocal = -1L
        let mutable sample: Sample option = None
        let mutable invalid: InvalidReason option = None
        let disable reason = invalid <- Some reason; Invalidated reason

        /// Caller supplies an authenticated, schema-valid negotiated report. Bounds are
        /// rechecked here; this helper has no authority to modify Phase A accounting.
        member _.Observe(evidence: CodedArrivalEvidence, generated: CodedArrivalEvidence,
                         receiverTimeUs: uint64, localNs: int64) =
            if not negotiated then Disabled
            elif localNs < 0L || localNs < lastLocal then RejectedLocalTime
            else
                lastLocal <- localNs
                match invalid with
                | Some reason -> Invalidated reason
                | None when generated.Overflow -> disable Overflow
                | None when generated.Bytes > cap || generated.Bytes % uint64 frameLength <> 0UL
                            || generated.Bytes < generatedHigh -> disable InvalidGeneratedBound
                | None ->
                    generatedHigh <- generated.Bytes
                    if evidence.Bytes > cap || evidence.Bytes % uint64 frameLength <> 0UL
                                || evidence.Bytes > generated.Bytes then RejectedEvidence
                    elif evidence.Overflow then disable Overflow
                    elif anchor.IsSome && evidence.Bytes < highest then IgnoredReordered
                    elif receiverTimeUs > uint64 Int64.MaxValue / 1000UL then disable ReceiverTimestampOutOfRange
                    else
                        let receiverNs = int64 receiverTimeUs * 1000L
                        match remote with
                        // Equal-count old idle reports can also reorder. They do not refresh
                        // feedback freshness and are not evidence of a clock epoch change.
                        | Some previous when receiverNs < previous && evidence.Bytes = highest -> IgnoredReordered
                        | Some previous when receiverNs < previous -> disable ReceiverTimestampRegression
                        | _ ->
                            let fresh = remote.IsNone || receiverNs > remote.Value
                            if fresh then
                                remote <- Some receiverNs
                                feedbackAt <- Some localNs
                            highest <- max highest evidence.Bytes
                            let point = { Bytes=evidence.Bytes; ReceiverNs=receiverNs; LocalNs=localNs }
                            match anchor with
                            | None -> anchor <- Some point; Baseline
                            | Some start when evidence.Bytes = start.Bytes ->
                                if fresh then FeedbackOnly else IgnoredRepeated
                            | Some start when receiverNs = start.ReceiverNs -> Deferred
                            | Some start ->
                                // No idle/timer callback moves start; accumulated growth keeps
                                // its full coverage even after no-delivery feedback or blackout.
                                let elapsed = max (receiverNs-start.ReceiverNs) (localNs-start.LocalNs)
                                let delta = evidence.Bytes-start.Bytes
                                let value =
                                    { From=start; Until=point; DeltaBytes=delta; CoverageNs=elapsed
                                      ObservedCodedBytesPerSecond=float delta * 1e9 / float elapsed }
                                anchor <- Some point
                                sample <- Some value
                                Sampled value
        member _.Snapshot(nowNs: int64) =
            if nowNs < 0L || nowNs < lastLocal then invalidArg "nowNs" "monotonic local time required"
            let age = feedbackAt |> Option.map (fun t -> nowNs-t)
            let status =
                match invalid, age with
                | Some reason, _ -> Invalid reason
                | None, None -> Unknown
                | None, Some elapsed when elapsed > staleAfterNs -> StaleFeedback
                | _ -> FreshFeedback
            { Identity=identity; Negotiated=negotiated; Status=status
              HighestObservedBytes=highest; GrowthAnchor=anchor; LastFeedbackLocalNs=feedbackAt
              LastSample=sample; FeedbackAgeNs=age
              SampleAgeNs=sample |> Option.map (fun s -> nowNs-s.Until.LocalNs) }

    /// Explicit snapshot supplied by an experiment, not a live TunStack registry.
    type Membership = { GroupId: uint32; Identity: Identity; PathId: uint16; Incarnation: uint32; Negotiated: bool }
    type CoverageReason =
        | EmptyGroup | UnknownIdentity | ConflictingMembership | LegacyMember
        | MissingObservation | ConflictingObservation | UnknownFeedback | StaleFeedbackEvidence | InvalidEvidence
    type Coverage =
        { GroupId: uint32; PathCount: int; SessionDirectionCount: int
          Observations: Snapshot array; IncompleteCoverage: CoverageReason array }
        member _.ControlEligible = false
        member _.ShadowOnly = true

    /// No total/rate sum: different session windows cannot be silently combined. Caller
    /// must supply the complete active membership snapshot; this cannot discover omissions.
    let coverage groupId (members: Membership array) (observations: Snapshot array) =
        let reasons = Collections.Generic.HashSet<CoverageReason>()
        let add r = reasons.Add r |> ignore
        let selected = members |> Array.filter (fun m -> m.GroupId=groupId) |> Array.distinct
        if selected.Length=0 then add EmptyGroup
        let validId id = id.SessionId<>Guid.Empty && (id.Direction=Direction.Up || id.Direction=Direction.Down)
        let groups = selected |> Array.groupBy (fun m -> m.Identity)
        let accepted = ResizeArray<Snapshot>()
        for identity, paths in groups do
            if not (validId identity) then add UnknownIdentity
            if paths |> Array.exists (fun p -> not p.Negotiated) then add LegacyMember
            // Same session-direction cannot belong to two bottleneck groups; no split.
            if members |> Array.exists (fun m -> m.Identity=identity && m.GroupId<>groupId) then add ConflictingMembership
            if paths |> Array.groupBy (fun p -> p.PathId) |> Array.exists (fun (_,p) -> p.Length>1) then add ConflictingMembership
            let matches = observations |> Array.filter (fun o -> o.Identity=identity) |> Array.distinct
            if matches.Length=0 then add MissingObservation
            elif matches.Length>1 then add ConflictingObservation
            else
                let observation=matches.[0]
                accepted.Add observation
                if not observation.Negotiated then add LegacyMember
                match observation.Status with
                | Unknown -> add UnknownFeedback
                | StaleFeedback -> add StaleFeedbackEvidence
                | Invalid _ -> add InvalidEvidence
                | FreshFeedback -> ()
        { GroupId=groupId; PathCount=selected.Length; SessionDirectionCount=groups.Length
          Observations=accepted.ToArray(); IncompleteCoverage=reasons |> Seq.sort |> Seq.toArray }
