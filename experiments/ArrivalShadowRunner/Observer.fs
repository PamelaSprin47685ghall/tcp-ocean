/// Diagnostic adapter only, never included in the production CLI.
namespace Tbt.ArrivalShadowRunner

open System
open System.Diagnostics
open System.Globalization
open System.IO
open Tbt.Contracts
open Tbt.Wire
open Tbt.Session
open Tbt.Session.ArrivalShadow
open Tbt.Carrier

module Monotonic =
    let nowNs () = int64 (float (Stopwatch.GetTimestamp()) * (1e9 / float Stopwatch.Frequency))

/// Called only after the original owner accepted/authenticated the frame.
type IAcceptedObserver =
    abstract Observe: frame: Span<byte> -> unit

/// Decorates exactly the FIRST Attach. A second Attach is not a safe tap installation
/// mechanism for TunPath and is never used by this runner.
[<Sealed>]
type ObservedPath(inner: IPath, session: Session, observer: IAcceptedObserver option) =
    let mutable diagnosticFault = false
    let mutable attachCount = 0
    member _.Inner = inner
    member _.DiagnosticFault = diagnosticFault
    member _.AttachCount = attachCount
    interface IPath with
        member _.PathId with get()=inner.PathId and set v=inner.PathId<-v
        member _.FrameLength with get()=inner.FrameLength and set v=inner.FrameLength<-v
        member _.Alive=inner.Alive
        member _.UnsentBytes=inner.UnsentBytes
        member _.Backlog with set v=inner.Backlog<-v
        member _.Watch(p,s)=inner.Watch(p,s)
        member _.Receive p=inner.Receive p
        member _.TryBeginFrame(frame)=inner.TryBeginFrame(&frame)
        member _.CommitFrame()=inner.CommitFrame()
        member _.Close()=inner.Close()
        member _.Attach(owner)=
            attachCount<-attachCount+1
            if attachCount<>1 then invalidOp "diagnostic path must be attached once"
            inner.Attach
                { new IPathOwner with
                    member _.OnFrame(path,frame)=
                        match observer with
                        | None -> owner.OnFrame(path,frame)
                        | Some tap ->
                            let cells=session.Rx.Counters.Cells
                            let result=owner.OnFrame(path,frame) // Product exceptions retain original behavior.
                            if not diagnosticFault && result<>UInt64.MaxValue && session.Rx.Counters.Cells>cells then
                                try tap.Observe frame
                                with _ -> diagnosticFault<-true // Never change the protocol return value.
                            result
                    member _.Frontier=owner.Frontier
                    member _.ProduceFill(p,f)=owner.ProduceFill(p,f)
                    member _.Fail reason=owner.Fail reason }

[<Struct>]
type private Row =
    { LocalNs:int64; ReceiverUs:uint64; Bytes:uint64; Generated:uint64
      Outcome:int; Status:int; Delta:uint64; CoverageNs:int64; Rate:float }

[<Sealed>]
type Collector(session: Session, capacity: int, ?nowNs: unit -> int64) =
    let nowNs=defaultArg nowNs Monotonic.nowNs
    let identity={SessionId=session.Keys.SessionId;Direction=if session.Role=Role.Client then Direction.Up else Direction.Down}
    let sampler=Sampler(identity,session.ArrivalEvidenceEnabled,session.FrameLength,1000000000L)
    do if capacity<1 || capacity>16384 then invalidArg "capacity" "range 1..16384"
    let rows=Array.zeroCreate<Row> capacity
    let mutable count=0
    let mutable dropped=0L
    let mutable errors=0L
    let mutable reports=0L
    let mutable samples=0L
    let mutable invalidReports=0L
    let mutable rejectedReports=0L
    let mutable lastStatus=Unknown
    let mutable processingTicks=0L
    let mutable maxProcessingTicks=0L
    let statusCode = function Unknown->0 | FreshFeedback->1 | StaleFeedback->2 | Invalid Overflow->3 | Invalid InvalidGeneratedBound->4 | Invalid ReceiverTimestampRegression->5 | Invalid ReceiverTimestampOutOfRange->6
    let outcomeCode = function Disabled->0 | RejectedEvidence->1 | RejectedLocalTime->2 | IgnoredReordered->3 | IgnoredRepeated->4 | Baseline->5 | FeedbackOnly->6 | Deferred->7 | Sampled _->8 | Invalidated _->9
    member _.Reports=reports
    member _.Samples=samples
    member _.InvalidReports=invalidReports
    member _.RejectedReports=rejectedReports
    member _.Recorded=count
    member _.Dropped=dropped
    member _.Errors=errors
    member _.Complete=dropped=0L && errors=0L
    member _.ProcessingTicks=processingTicks
    member _.MaxProcessingTicks=maxProcessingTicks
    member _.Snapshot now=sampler.Snapshot now
    member _.StatusCode now=statusCode (sampler.Snapshot(now).Status)
    member _.ShadowUsable now=
        let state=sampler.Snapshot(now).Status
        samples>0L && invalidReports=0L && rejectedReports=0L && (match state with Invalid _->false | _->true)
    member _.WriteCsv(path: string)=
        use writer=new StreamWriter(path,false)
        writer.WriteLine("local_ns,receiver_us,coded_bytes,generated_bytes,outcome,status,delta_bytes,coverage_ns,observed_coded_bytes_per_second")
        for i in 0..count-1 do
            let r=rows.[i]
            let fields=[|string r.LocalNs;string r.ReceiverUs;string r.Bytes;string r.Generated;string r.Outcome;string r.Status;string r.Delta;string r.CoverageNs;r.Rate.ToString("R",CultureInfo.InvariantCulture)|]
            writer.WriteLine(String.Join(",",fields))
    interface IAcceptedObserver with
        member _.Observe(frame)=
            // Meta is already authenticated and range-checked by the original owner.
            if frame.[Layout.MetaOffset]=byte CellType.WinAck then
                let start=Stopwatch.GetTimestamp()
                try
                    let local=nowNs()
                    let mutable head=Unchecked.defaultof<WinAckHead>
                    let mutable evidence=Unchecked.defaultof<CodedArrivalEvidence>
                    if WinAck.tryReadHeadWithArrival (Span<byte>.op_Implicit(frame.Slice(Layout.SymbolOffset,session.SymbolSize))) &head &evidence then
                        reports<-reports+1L
                        let generated=session.Tx.GeneratedCodedFrameEvidence.Value
                        let outcome=sampler.Observe(evidence,generated,head.RxTimeUs,local)
                        match outcome with
                        | RejectedEvidence | RejectedLocalTime -> rejectedReports<-rejectedReports+1L
                        | Sampled _ -> samples<-samples+1L
                        | _ -> ()
                        // A rejected local clock cannot be passed to Snapshot. Preserve
                        // its explicit rejected row and last known status, not an exception.
                        match outcome with
                        | RejectedLocalTime -> ()
                        | _ -> lastStatus<-(sampler.Snapshot local).Status
                        match lastStatus with Invalid _->invalidReports<-invalidReports+1L | _->()
                        let delta,coverage,rate =
                            match outcome with Sampled s -> s.DeltaBytes,s.CoverageNs,s.ObservedCodedBytesPerSecond | _ -> 0UL,0L,0.0
                        if count<rows.Length then
                            rows.[count]<-{LocalNs=local;ReceiverUs=head.RxTimeUs;Bytes=evidence.Bytes;Generated=generated.Bytes;Outcome=outcomeCode outcome;Status=statusCode lastStatus;Delta=delta;CoverageNs=coverage;Rate=rate}
                            count<-count+1
                        else dropped<-dropped+1L
                    else errors<-errors+1L
                with _ -> errors<-errors+1L
                let elapsed=Stopwatch.GetTimestamp()-start
                processingTicks<-processingTicks+elapsed
                maxProcessingTicks<-max maxProcessingTicks elapsed
