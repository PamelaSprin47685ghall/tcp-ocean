/// Offline post-authentication component decomposition. No production calls/hooks.
module ArrivalOfflineBench.Program

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Session.ArrivalShadow
open Tbt.Wire
open Tbt.Carrier
open Tbt.ArrivalShadowRunner

[<Struct>]
type Input =
    { LocalNs:int64; ReceiverUs:uint64; Bytes:uint64; Generated:uint64
      Outcome:int; Status:int; Delta:uint64; Coverage:int64; Rate:float }

let invariant=CultureInfo.InvariantCulture
let readCsv path =
    let lines=File.ReadAllLines path
    if lines.[0]<>"local_ns,receiver_us,coded_bytes,generated_bytes,outcome,status,delta_bytes,coverage_ns,observed_coded_bytes_per_second" then failwith "unexpected input schema"
    lines |> Array.skip 1 |> Array.filter(fun l->l.Length>0) |> Array.map(fun line ->
        let c=line.Split(',')
        if c.Length<>9 then failwith "unexpected row width"
        {LocalNs=Int64.Parse(c.[0],invariant);ReceiverUs=UInt64.Parse(c.[1],invariant)
         Bytes=UInt64.Parse(c.[2],invariant);Generated=UInt64.Parse(c.[3],invariant)
         Outcome=Int32.Parse c.[4];Status=Int32.Parse c.[5];Delta=UInt64.Parse(c.[6],invariant)
         Coverage=Int64.Parse(c.[7],invariant);Rate=Double.Parse(c.[8],invariant)})

// Cheap deterministic semantic checksum, not a cryptographic integrity claim. Input
// files and canonical plaintext buffers separately receive SHA256 outside timing.
let mix h v = (h ^^^ v) * 1099511628211UL
let seed=14695981039346656037UL
let statusCode = function Unknown->0 | FreshFeedback->1 | StaleFeedback->2 | Invalid Overflow->3 | Invalid InvalidGeneratedBound->4 | Invalid ReceiverTimestampRegression->5 | Invalid ReceiverTimestampOutOfRange->6
let outcomeCode = function Disabled->0 | RejectedEvidence->1 | RejectedLocalTime->2 | IgnoredReordered->3 | IgnoredRepeated->4 | Baseline->5 | FeedbackOnly->6 | Deferred->7 | Sampled _->8 | Invalidated _->9
let parseHash h (head:WinAckHead) (e:CodedArrivalEvidence) = mix (mix (mix h head.RxTimeUs) e.Bytes) (if e.Overflow then 1UL else 0UL)
let sampleHash h outcome status delta coverage rate =
    mix (mix (mix (mix (mix h (uint64 outcome)) (uint64 status)) delta) (uint64 coverage)) (BitConverter.DoubleToUInt64Bits rate)
let capturedHash (rows:Input[]) =
    rows |> Array.fold(fun h r->sampleHash h r.Outcome r.Status r.Delta r.Coverage r.Rate) seed

[<Sealed>]
type FixedClock() =
    interface IClock with member _.NowNs()=1000000000L; member _.FreshNs()=1000000000L
[<Sealed>]
type SeedEntropy() =
    let random=Random 741
    interface IEntropy with member _.Fill dst=random.NextBytes dst
let identity={SessionId=Guid.Parse "11111111-2222-3333-4444-555555555555";Direction=Direction.Up}
let setupSession () =
    let key=Array.init 32 (fun i->byte(i+1)) // public synthetic fixture key, no account.
    let entropy=SeedEntropy()
    let unix=1700000000000L
    let client=ClientOffer(key,identity.SessionId,entropy,uint64 unix,1448,1,offerArrivalEvidence=true)
    let server=ServerOffer([|key|],entropy,1024us,1,offerArrivalEvidence=true)
    let accepted=server.TryAccept(ReadOnlySpan client.Frame,unix,1448).Value
    let struct(parameters,_)=client.TryAccept(ReadOnlySpan accepted.Reply).Value
    let port=
        {new IFlowPort with
            member _.Open(_,_) = ()
            member _.Write(_,data)=data.Length
            member _.Finish _=()
            member _.Reset _=()
            member _.Close _=()
            member _.Credit _=()}
    Session(Role.Client,parameters,port,FixedClock(),{SessionConfig.defaults with MaxPaths=1}),parameters

let canonical (session:Session) (parameters:SessionParams) (rows:Input[]) =
    let senderKeys=KeyTable(parameters.Keys.Suite)
    let receiverKeys=KeyTable(parameters.Keys.Suite)
    senderKeys.Install(0u,ReadOnlySpan(parameters.Keys.DownKey 0u),0UL)
    receiverKeys.Install(0u,ReadOnlySpan(parameters.Keys.DownKey 0u),0UL)
    let mask () : IHeaderMask =
        if parameters.Caps &&& Caps.HpMask=0us then NoHeaderMask()
        else HeaderMasks.Create(parameters.Keys.Suite,ReadOnlySpan parameters.Keys.HpDown)
    let sealer=CellSealer(Direction.Down,parameters.Symbol,senderKeys,mask())
    let opener=CellOpener(Direction.Down,parameters.Symbol,receiverKeys,mask())
    rows |> Array.mapi(fun i row ->
        if row.Bytes % uint64 session.FrameLength<>0UL || row.Generated % uint64 session.FrameLength<>0UL then failwith "CSV/frame unit mismatch"
        let frame=Array.zeroCreate<byte> session.FrameLength
        FrameMath.writeChunkHeader session.SymbolSize (Span frame)
        frame.[frame.Length-2]<-13uy;frame.[frame.Length-1]<-10uy
        Meta.write {Unchecked.defaultof<Meta> with Type=CellType.WinAck;RxWinGrant=100us} (Span(frame,Layout.MetaOffset,Layout.MetaLength))
        WinAck.writeHeadWithArrival {Unchecked.defaultof<WinAckHead> with Span=1uy;CtrlAckHi=1u;RxTimeUs=row.ReceiverUs} {Bytes=row.Bytes;Overflow=false} (Span(frame,Layout.SymbolOffset,session.SymbolSize))
        sealer.Seal(Span frame,uint64(i+1),0us)
        let mutable counter=0UL
        if not (opener.TryOpen(Span frame,0us,&counter)) || counter<>uint64(i+1) then failwith "synthetic authentication failed"
        let mutable meta=Unchecked.defaultof<Meta>
        if not (Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &meta) then failwith "synthetic meta failed"
        frame) // Already authenticated plaintext only. Crypto is OUTSIDE measurement.

[<Sealed>]
type FeedPath() =
    let mutable owner=Unchecked.defaultof<IPathOwner>
    member _.Deliver(frame:byte[])=owner.OnFrame(0us,Span frame)
    interface IPath with
        member val PathId=0us with get,set
        member val FrameLength=0 with get,set
        member _.Alive=true
        member _.UnsentBytes=0
        member _.Backlog with set (_:int)=()
        member _.Attach o=owner<-o
        member _.Watch(_,_) = ()
        member _.Receive _=()
        member _.TryBeginFrame(frame:byref<Span<byte>>)=frame<-Span<byte>.Empty;false
        member _.CommitFrame()=()
        member _.Close()=()

[<Sealed>]
type ParseOwner(symbol:int) =
    let mutable checksum=seed
    member _.Reset()=checksum<-seed
    member _.Checksum=checksum
    member _.Parse(frame:Span<byte>) =
        let mutable head=Unchecked.defaultof<WinAckHead>
        let mutable evidence=Unchecked.defaultof<CodedArrivalEvidence>
        if not (WinAck.tryReadHeadWithArrival (Span<byte>.op_Implicit(frame.Slice(Layout.SymbolOffset,symbol))) &head &evidence) then failwith "canonical parser rejection"
        checksum<-parseHash checksum head evidence
        0UL
    interface IPathOwner with
        member this.OnFrame(_,frame)=this.Parse frame
        member _.Frontier=0UL
        member _.ProduceFill(_,_) = false
        member _.Fail _=failwith "unexpected path failure"

type Harness(rows:Input[]) =
    let session,parameters=setupSession()
    let frames=canonical session parameters rows
    let parser=ParseOwner(session.SymbolSize)
    let direct: IPathOwner=parser
    let feed=FeedPath()
    let wrapper=ObservedPath(feed,session,None)
    do (wrapper :> IPath).Attach direct
    member _.Session=session
    member _.Frames=frames
    member _.InputHash=Convert.ToHexString(SHA256.HashData(Array.concat frames)).ToLowerInvariant()
    member _.Pass(stage:string)=
        parser.Reset()
        match stage with
        | "parse-direct" ->
            for frame in frames do direct.OnFrame(0us,Span frame) |> ignore
            parser.Checksum
        | "parse-forward" ->
            for frame in frames do feed.Deliver frame |> ignore
            parser.Checksum
        | "parse-sample" ->
            let sampler=Sampler(identity,true,session.FrameLength,1000000000L)
            let mutable hash=seed
            for i in 0..rows.Length-1 do
                let mutable head=Unchecked.defaultof<WinAckHead>
                let mutable evidence=Unchecked.defaultof<CodedArrivalEvidence>
                if not (WinAck.tryReadHeadWithArrival (ReadOnlySpan(frames.[i],Layout.SymbolOffset,session.SymbolSize)) &head &evidence) then failwith "canonical parser rejection"
                let result=sampler.Observe(evidence,{Bytes=rows.[i].Generated;Overflow=false},head.RxTimeUs,rows.[i].LocalNs)
                let status=statusCode (sampler.Snapshot(rows.[i].LocalNs).Status)
                let delta,coverage,rate=match result with Sampled s->s.DeltaBytes,s.CoverageNs,s.ObservedCodedBytesPerSecond | _->0UL,0L,0.0
                hash<-sampleHash hash (outcomeCode result) status delta coverage rate
            hash
        | _ -> invalidArg "stage" "parse-direct, parse-forward, or parse-sample"

/// Real Collector validation, NOT timed: creating its exact local generated counters
/// requires synthetic DATA seals, not reconstruction of original transport state.
let validateCollector (rows:Input[]) (frames:byte[][]) output =
    let session,_=setupSession()
    let mutable local=rows.[0].LocalNs
    let collector=Collector(session,max 1 rows.Length,nowNs=(fun ()->local))
    let blank=Array.zeroCreate<byte> session.FrameLength
    FrameMath.writeChunkHeader session.SymbolSize (Span blank)
    blank.[blank.Length-2]<-13uy;blank.[blank.Length-1]<-10uy
    Meta.write {Unchecked.defaultof<Meta> with Type=CellType.Data;StepUsed=1uy} (Span(blank,Layout.MetaOffset,Layout.MetaLength))
    let mutable seals=0L
    for i in 0..rows.Length-1 do
        while session.Tx.GeneratedCodedFrameEvidence.Value.Bytes<rows.[i].Generated do
            let frame=Array.copy blank
            session.Tx.Seal(Span frame,0us)
            seals<-seals+1L
        if session.Tx.GeneratedCodedFrameEvidence.Value.Bytes<>rows.[i].Generated then failwith "capture generated bound regressed"
        local<-rows.[i].LocalNs
        (collector :> IAcceptedObserver).Observe(Span frames.[i])
    collector.WriteCsv output
    let replay=readCsv output
    if replay<>rows then failwith "Collector replay differs from captured rows"
    if not collector.Complete || collector.RejectedReports<>0L || collector.InvalidReports<>0L then failwith "Collector quality failure"
    seals

[<CLIMutable>]
type Measurement =
    { Stage:string; Phase:string; Trial:int; Passes:int; RowsPerPass:int
      WallMs:float; ProcessCpuMs:float; ThreadAllocatedBytes:int64
      Gen0:int; Gen1:int; Gen2:int; SemanticChecksum:string }
let measure (h:Harness) stage phase trial passes rowCount =
    use proc=Process.GetCurrentProcess()
    let beforeCpu=proc.TotalProcessorTime
    let alloc=GC.GetAllocatedBytesForCurrentThread()
    let g0,g1,g2=GC.CollectionCount 0,GC.CollectionCount 1,GC.CollectionCount 2
    let start=Stopwatch.GetTimestamp()
    let mutable hash=0UL
    for _ in 1..passes do hash<-mix hash (h.Pass stage)
    let stop=Stopwatch.GetTimestamp()
    let allocated=GC.GetAllocatedBytesForCurrentThread()-alloc
    let cpu=(proc.TotalProcessorTime-beforeCpu).TotalMilliseconds
    { Stage=stage;Phase=phase;Trial=trial;Passes=passes;RowsPerPass=rowCount
      WallMs=float(stop-start)*1000.0/float Stopwatch.Frequency;ProcessCpuMs=cpu
      ThreadAllocatedBytes=allocated;Gen0=GC.CollectionCount(0)-g0;Gen1=GC.CollectionCount(1)-g1;Gen2=GC.CollectionCount(2)-g2
      SemanticChecksum=hash.ToString("x16") }

[<EntryPoint>]
let main args =
    try
        if args.Length<4 then failwith "usage: input.csv stage output.json passes [warm-trials]"
        let path,stage,output=args.[0],args.[1],args.[2]
        let passes=Int32.Parse args.[3]
        let trials=if args.Length>4 then Int32.Parse args.[4] else 5
        if passes<1 || passes>100000 || trials<1 || trials>20 then failwith "invalid iteration budget"
        let rows=readCsv path
        if rows.Length<2 || rows.Length>16384 then failwith "invalid capture size"
        let h=Harness rows
        // First-call means this stage's first measured invocation after setup, NOT
        // process startup, cold caches, cold crypto or a clean operating system.
        let results=ResizeArray<Measurement>()
        results.Add(measure h stage "first-pass-after-setup" 0 1 rows.Length)
        for _ in 1..20 do h.Pass stage |> ignore
        for trial in 1..trials do results.Add(measure h stage "warm-batch" trial passes rows.Length)
        // Validation intentionally happens AFTER all timings (avoids warming first-pass).
        let direct=h.Pass "parse-direct"
        let forwarded=h.Pass "parse-forward"
        if direct<>forwarded then failwith "P/F semantic checksum mismatch"
        let sample=h.Pass "parse-sample"
        if sample<>capturedHash rows then failwith "S captured semantic checksum mismatch"
        let collectorOutput=output+".collector.csv"
        let seals=validateCollector rows h.Frames collectorOutput
        let expected=if stage="parse-sample" then sample else direct
        for r in results do
            let mutable expectedBatch=0UL
            for _ in 1..r.Passes do expectedBatch<-mix expectedBatch expected
            if r.SemanticChecksum<>expectedBatch.ToString("x16") then failwith "timed checksum mismatch"
        let metadata=
            {|Input=Path.GetFullPath path;InputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes path)).ToLowerInvariant()
              CanonicalPlaintextSha256=h.InputHash;Rows=rows.Length;FrameLength=h.Session.FrameLength
              SyntheticAuthenticationOutsideTiming=true;OriginalCiphertextAvailable=false
              DirectChecksum=direct.ToString("x16");ForwardChecksum=forwarded.ToString("x16")
              SampleChecksum=sample.ToString("x16");CollectorExactRows=true;CollectorSyntheticSealsOutsideTiming=seals
              Runtime=Environment.Version.ToString();CpuTiming="process CPU boundary reads; includes runtime/background CPU, resolution may quantize short passes"
              FirstPassMeaning="first stage invocation after synthetic authentication/setup, not process or OS cold start"
              SampleScope="actual parser + actual sampler + snapshot/checksum, NOT complete Collector or crypto/controller overhead"
              Measurements=results.ToArray()|}
        File.WriteAllText(output,JsonSerializer.Serialize(metadata,JsonSerializerOptions(WriteIndented=true)))
        Console.WriteLine("equal_input_verified rows="+string rows.Length+" stage="+stage)
        0
    with ex ->
        Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message)
        2
