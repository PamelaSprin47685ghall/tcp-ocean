module Tbt.Tests.ArrivalRunnerTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Session
open Tbt.Wire
open Tbt.Carrier
open Tbt.ArrivalShadowRunner
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests

let private pair ()=Pair(SessionConfig.defaults,1,0.0,8,arrivalClient=true,arrivalServer=true)

type private TestPath() =
    let mutable owner=Unchecked.defaultof<IPathOwner>
    member _.Deliver frame=owner.OnFrame(0us,frame)
    member _.Fill frame=owner.ProduceFill(0us,frame)
    member _.Frontier=owner.Frontier
    member val Attachments=0 with get,set
    member val Commits=0 with get,set
    interface IPath with
        member val PathId=0us with get,set
        member val FrameLength=0 with get,set
        member val Alive=true with get
        member _.UnsentBytes=7
        member _.Backlog with set (_:int)=()
        member this.Attach o=owner<-o;this.Attachments<-this.Attachments+1
        member _.Watch(_,_) = ()
        member _.Receive _=()
        member _.TryBeginFrame(frame:byref<Span<byte>>)=frame<-Span<byte>.Empty;false
        member this.CommitFrame()=this.Commits<-this.Commits+1
        member _.Close()=()

let private owner (s:Session) =
    { new IPathOwner with
        member _.OnFrame(p,f)=s.OnFrame(p,f)
        member _.Frontier=s.Frontier
        member _.ProduceFill(p,f)=s.ProduceFill(p,f)
        member _.Fail reason=s.Fail reason }

let private data (p:Pair) =
    let mutable slot= -1
    Assert.True(p.Client.TryOpenFlow(&slot))
    Assert.Equal(1,p.Feed(p.Client,slot,ReadOnlySpan [|7uy|],false))
    let f=Array.zeroCreate<byte> p.Client.FrameLength
    Assert.True(p.Client.TryProduce(0us,Span f,false))
    f

[<Fact>]
let ``tap sees only authenticated meta and does not repeat Attach`` () =
    let p=pair()
    let raw=TestPath()
    let mutable seen=0
    let tap={new IAcceptedObserver with member _.Observe frame=Assert.Equal(byte CellType.Data,frame.[Layout.MetaOffset]);seen<-seen+1}
    let wrapped=ObservedPath(raw,p.Server,Some tap)
    let ip=wrapped :> IPath
    ip.Attach(owner p.Server)
    let frame=data p
    let corrupt=Array.copy frame
    corrupt.[Layout.tagOffset p.Client.SymbolSize]<-corrupt.[Layout.tagOffset p.Client.SymbolSize] ^^^ 1uy
    Assert.Equal(UInt64.MaxValue,raw.Deliver(Span corrupt))
    Assert.Equal(0,seen)
    let replay=Array.copy frame
    Assert.NotEqual(UInt64.MaxValue,raw.Deliver(Span frame))
    Assert.Equal(1,seen)
    Assert.Equal(UInt64.MaxValue,raw.Deliver(Span replay))
    Assert.Equal(1,seen)
    Assert.Equal(1L,p.ServerPort.Stream(0).Length)
    Assert.False(wrapped.DiagnosticFault)
    Assert.Equal(1,raw.Attachments)
    Assert.Throws<InvalidOperationException>(fun ()->ip.Attach(owner p.Server)) |> ignore
    Assert.Equal(1,raw.Attachments)

[<Fact>]
let ``collector exception cannot replace accepted protocol return or local delivery`` () =
    let p=pair()
    let raw=TestPath()
    let tap={new IAcceptedObserver with member _.Observe _=failwith "injected diagnostic failure"}
    let wrapped=ObservedPath(raw,p.Server,Some tap)
    (wrapped :> IPath).Attach(owner p.Server)
    Assert.NotEqual(UInt64.MaxValue,raw.Deliver(Span(data p)))
    Assert.True(wrapped.DiagnosticFault)
    Assert.Equal(1L,p.ServerPort.Stream(0).Length)
    Assert.False(p.Server.Faulted)

let private reportCount (p:Pair) timestamp bytes =
    let f=Array.zeroCreate<byte> p.Server.FrameLength
    FrameMath.writeChunkHeader p.Server.SymbolSize (Span f)
    f.[f.Length-2]<-13uy
    f.[f.Length-1]<-10uy
    Meta.write {Unchecked.defaultof<Meta> with Type=CellType.WinAck;RxWinGrant=100us} (Span(f,Layout.MetaOffset,Layout.MetaLength))
    WinAck.writeHeadWithArrival {Unchecked.defaultof<WinAckHead> with Span=1uy;CtrlAckHi=1u;RxTimeUs=timestamp} {Bytes=bytes;Overflow=false} (Span(f,Layout.SymbolOffset,p.Server.SymbolSize))
    p.Server.Tx.Seal(Span f,0us)
    f

let private report p timestamp = reportCount p timestamp 0UL

[<Fact>]
let ``collector full and exceptions explicitly invalidate capture without stopping protocol`` () =
    let p=pair()
    let raw=TestPath()
    let mutable now=10000000L
    let collector=Collector(p.Client,1,nowNs=(fun ()->now))
    let wrapped=ObservedPath(raw,p.Client,Some(collector :> IAcceptedObserver))
    (wrapped :> IPath).Attach(owner p.Client)
    for time in [10000UL;20000UL] do
        now<-int64 time*1000L
        Assert.NotEqual(UInt64.MaxValue,raw.Deliver(Span(report p time)))
    Assert.Equal(2L,collector.Reports)
    Assert.Equal(1,collector.Recorded)
    Assert.Equal(1L,collector.Dropped)
    Assert.False(collector.Complete)
    Assert.False(p.Client.Faulted)
    let bad=Collector(p.Client,1,nowNs=(fun ()->failwith "clock failure"))
    // Observe a known accepted plaintext copy; diagnostic exception is contained.
    let frame=report p 30000UL
    p.Client.OnFrame(0us,Span frame) |> ignore
    (bad :> IAcceptedObserver).Observe(Span frame)
    Assert.Equal(1L,bad.Errors)
    Assert.False(bad.Complete)
    Assert.False(p.Client.Faulted)

/// Compare product TunPath data-phase entry and packet forwarding with or without
/// decorator. Each raw TunPath is attached exactly once, never re-attached as a tap.
[<Fact>]
let ``forward decorator preserves TunPath first attach and bidirectional control behavior`` () =
    let run wrap =
        let p=pair()
        let group=RateGroup(p.Clock)
        let f=Fixture(0,p.Clock,group,40000us)
        group.Add f.Path
        f.Send(5001u,ReadOnlySpan(Array.append f.Head f.Boot),[||])
        Assert.Equal(1,f.Path.BootState)
        let bridge=FlowBridge ValueNone
        bridge.Attach p.Server
        let carrier=SessionCarrier(p.Server,bridge)
        let wrapped=ObservedPath(f.Path,p.Server,None)
        let link=if wrap then wrapped :> IPath else f.Path :> IPath
        carrier.AddPath link
        let frame=data p
        f.Send(f.Data0,ReadOnlySpan frame,[||])
        p.Clock.Now<-p.Clock.Now+20000000L
        p.Server.Tick()
        f.Path.Service p.Clock.Now
        group.Service p.Clock.Now
        carrier.WriteBatch 256 |> ignore
        Assert.Equal(1L,p.ServerPort.Stream(0).Length)
        Assert.Equal(0L,f.Path.Counters.Faults)
        if wrap then Assert.Equal(1,wrapped.AttachCount)
        p.Server.Rx.AcceptedCodedFrameEvidence,f.Path.Counters.CellsReceived,f.Path.Counters.SegmentsSent,f.Out.Packets |> Seq.map Convert.ToHexString |> Seq.toArray
    let a,b,c,packets=run false
    let x,y,z,other=run true
    Assert.Equal(a,x)
    Assert.Equal(b,y)
    Assert.Equal(c,z)
    Assert.Equal<string>(packets,other)

[<Fact>]
let ``decorator hides PathLink Describe type but retains original socket for diagnostics`` () =
    let describe wrap =
        let p=pair()
        let bridge=FlowBridge ValueNone
        bridge.Attach p.Client
        let carrier=SessionCarrier(p.Client,bridge)
        use socket=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)
        let raw=PathLink(socket,SocketTuning.probe())
        let decorated=ObservedPath(raw,p.Client,None)
        carrier.AddPath(if wrap then decorated :> IPath else raw :> IPath)
        Assert.Same(socket,raw.Socket)
        Assert.Same(raw,decorated.Inner)
        carrier.Describe()
    Assert.Contains("send_control=kernel",describe false)
    // This is a diagnostic classification artifact, NOT a carrier behavior change.
    Assert.Contains("send_control=tun",describe true)

[<Fact>]
let ``complete capture of invalid shadow timing is not usable evidence`` () =
    let p=pair()
    let raw=TestPath()
    let collector=Collector(p.Client,4,nowNs=(fun ()->10000000L))
    let wrapped=ObservedPath(raw,p.Client,Some(collector :> IAcceptedObserver))
    (wrapped :> IPath).Attach(owner p.Client)
    Assert.NotEqual(UInt64.MaxValue,raw.Deliver(Span(report p UInt64.MaxValue)))
    Assert.True(collector.Complete)
    Assert.Equal(1L,collector.InvalidReports)
    Assert.Equal(6,collector.StatusCode 10000000L)
    Assert.False(collector.ShadowUsable 10000000L)
    Assert.False(p.Client.Faulted)


[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``rejected report after valid sample invalidates quality not capture or protocol`` badLocalTime =
    let p=pair()
    let raw=TestPath()
    let mutable now=10000000L
    let collector=Collector(p.Client,8,nowNs=(fun ()->now))
    let wrapped=ObservedPath(raw,p.Client,Some(collector :> IAcceptedObserver))
    (wrapped :> IPath).Attach(owner p.Client)
    raw.Deliver(Span(reportCount p 10000UL 0UL)) |> ignore
    data p |> ignore // Creates a real local generated upper bound.
    let bytes=uint64 p.Client.FrameLength
    now<-20000000L
    raw.Deliver(Span(reportCount p 20000UL bytes)) |> ignore
    Assert.Equal(1L,collector.Samples)
    Assert.True(collector.ShadowUsable now)
    now<-21000000L
    raw.Deliver(Span(reportCount p 20000UL bytes)) |> ignore // repeated: normal
    now<-22000000L
    raw.Deliver(Span(reportCount p 19000UL bytes)) |> ignore // reordered: normal
    Assert.Equal(0L,collector.RejectedReports)
    Assert.True(collector.ShadowUsable now)
    if badLocalTime then now<-21999999L else now<-23000000L
    let reportBytes=if badLocalTime then bytes else bytes*2UL
    Assert.NotEqual(UInt64.MaxValue,raw.Deliver(Span(reportCount p 30000UL reportBytes)))
    Assert.Equal(1L,collector.RejectedReports)
    Assert.Equal(0L,collector.InvalidReports)
    Assert.Equal(0L,collector.Errors)
    Assert.True(collector.Complete)
    Assert.False(collector.ShadowUsable 24000000L)
    Assert.Equal(bytes,p.Client.PeerAcceptedCodedFrameEvidence.Value.Bytes)
    Assert.False(p.Client.Faulted)
