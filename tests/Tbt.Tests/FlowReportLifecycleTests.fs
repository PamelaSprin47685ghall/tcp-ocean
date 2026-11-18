[<Xunit.Collection("BridgeTests serial")>]
module Tbt.Tests.FlowReportLifecycleTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Wire
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Tests.SessionTests
open Tbt.Tests.SessionTestsB

type private Lifecycle() =
    let clock = ManualClock()
    do clock.Now <- 1L
    let entropy = SeededEntropy 533
    let offer = ClientOffer(psk,Guid(Array.create 16 7uy),entropy,uint64 unixMs,1448,256)
    let accepted = ServerOffer([|psk|],entropy,4096us,256).TryAccept(ReadOnlySpan offer.Frame,unixMs,1448).Value
    let struct(cp,_) = offer.TryAccept(ReadOnlySpan accepted.Reply).Value
    let clientPort,serverPort = RecordingPort(),RecordingPort()
    let client = Session(Role.Client,cp,clientPort,clock,SessionConfig.defaults)
    let server = Session(Role.Server,accepted.Params,serverPort,clock,SessionConfig.defaults)
    let frame = Array.zeroCreate<byte> client.FrameLength
    let moveOne (src:Session) (dst:Session) =
        if not(src.TryProduce(0us,Span frame)) then None
        else
            Assert.NotEqual(UInt64.MaxValue,dst.OnFrame(0us,Span frame))
            let mutable meta = Unchecked.defaultof<Meta>
            Assert.True(Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &meta)
            if meta.Type = CellType.FlowState then
                let mutable head = Unchecked.defaultof<FlowStateHead>
                Assert.True(FlowState.tryReadHead (ReadOnlySpan(frame,Layout.SymbolOffset,server.SymbolSize)) &head)
                Some(meta.Type,head.RecordCount,head.Snapshot)
            else Some(meta.Type,-1,false)
    let drainClient () =
        let mutable n = 0
        while (moveOne client server).IsSome do
            n <- n+1
            Assert.True(n<100)
    member _.Client = client
    member _.ServerPort = serverPort
    member _.StageSnapshot() =
        clock.Now <- clock.Now + 1_000_000_000L
        server.Tick()
    member _.Echo(slot) =
        let mutable span = Span<byte>.Empty
        Assert.True(server.TryBeginSourceFor(slot,&span))
        span.[0] <- 91uy
        server.CommitSource(slot,1,false)
        server.Flush()
        let mutable n = 0
        while (moveOne server client).IsSome do
            n <- n+1
            Assert.True(n<100)
    member _.ClientFin(slot, bytes) =
        let mutable span = Span<byte>.Empty
        Assert.True(client.TryBeginSourceFor(slot,&span))
        span.Slice(0,bytes).Fill 19uy
        client.CommitSource(slot,bytes,true)
        client.Flush()
        drainClient()
    member _.StageServerFin(slot) =
        let mutable span = Span<byte>.Empty
        Assert.True(server.TryBeginSourceClose(&span))
        server.CommitSource(slot,0,true)
        server.Flush()
    member _.Server = server
    member _.Open() =
        let mutable slot = -1
        Assert.True(client.TryOpenFlow(&slot))
        let mutable span = Span<byte>.Empty
        Assert.True(client.TryBeginSourceFor(slot,&span))
        client.CommitSource(slot,0,false)
        client.Flush()
        drainClient()
        Assert.True(server.Flows.[slot].Active)
        slot
    member _.ResetAndAck(slot) =
        client.FailFlow slot
        client.Flush()
        drainClient()
        Assert.False(server.Flows.[slot].Active)
        // Deliver only the real priority WIN_ACK, leaving lower-priority reports
        // pending across slot reuse. No private state/counter mutation.
        clock.Now <- clock.Now + 20_000_000L
        server.Tick()
        Assert.Equal(Some(CellType.WinAck,-1,false),moveOne server client)
        Assert.False(client.Flows.[slot].Active)
    member _.ReportRound() =
        clock.Now <- clock.Now + 20_000_000L
        server.Tick()
        let pages = ResizeArray<struct(int*bool)>()
        let mutable go = true
        let mutable n = 0
        while go do
            match moveOne server client with
            | None -> go <- false
            | Some(kind,count,snapshot) ->
                if kind = CellType.FlowState then pages.Add(struct(count,snapshot))
                n <- n+1
                Assert.True(n<100)
        pages.ToArray()

[<Fact>]
let ``live flow sends two changed reports then becomes quiet`` () =
    let p = Lifecycle()
    let slot = p.Open()
    Assert.Equal(2,p.Server.Flows.[slot].ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(1,p.Server.Flows.[slot].ReportsDue)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(0,p.Server.Flows.ReportsDue)
    Assert.Empty(p.ReportRound())

[<Fact>]
let ``closed slot reporting is cleaned without disturbing another live slot`` () =
    let p = Lifecycle()
    let closed = p.Open()
    let live = p.Open()
    p.ResetAndAck closed
    Assert.False(p.Server.Flows.Reportable closed)
    Assert.True(p.Server.Flows.Reportable live)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(0,p.Server.Flows.[closed].ReportsDue)
    Assert.Equal(1,p.Server.Flows.[live].ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(0,p.Server.Flows.ReportsDue)

[<Fact>]
let ``slot generation reuse cannot leave phantom reports and persistent empty FLOW_STATE`` () =
    let p = Lifecycle()
    for expected in 0..255 do
        let slot = p.Open()
        Assert.Equal(expected,slot)
        p.ResetAndAck slot
    let reused = p.Open()
    Assert.Equal(0,reused)
    Assert.Equal(2u,p.Server.Flows.[reused].Gen)
    // Drain the two legitimate reports of the new generation. Closed slots are
    // not reportable in current main; this test does not add terminal tombstones.
    // Crossing one second may legitimately produce a snapshot, which does not
    // consume the two incremental report marks. Inspect actual header flags.
    let initial = Array.init 5 (fun _ -> p.ReportRound()) |> Array.concat
    let nonemptyDeltas = initial |> Array.filter(fun struct(n,snapshot) -> n>0 && not snapshot)
    Assert.Equal<struct(int*bool)>([|struct(1,false);struct(1,false)|],nonemptyDeltas)
    Assert.Equal(0,p.Server.Flows.[reused].ReportsDue)
    let residual = p.Server.Flows.ReportsDue
    // Only another80ms: no new1s snapshot should occur in this interval.
    let later = Array.init 4 (fun _ -> p.ReportRound())
    Assert.False(later |> Array.concat |> Array.exists(fun struct(_,snapshot) -> snapshot))
    Assert.True(residual = 0 && (later |> Array.forall Array.isEmpty),
                sprintf "phantom global ReportsDue=%d; subsequent FLOW_STATE (record count,snapshot)=%A" residual later)


[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
let ``actual close retires one marked flow regardless of remaining repeat count`` remaining =
    let p = Lifecycle()
    let slot = p.Open()
    for _ in 1..(2-remaining) do p.ReportRound() |> ignore
    Assert.Equal(remaining,p.Server.Flows.[slot].ReportsDue)
    p.ResetAndAck slot
    Assert.Equal(0,p.Server.Flows.[slot].ReportsDue)
    Assert.Equal(0,p.Server.Flows.ReportsDue)
    for _ in 1..4 do
        p.Server.PumpFlow slot
        Assert.False(p.Server.Flows.TakeReport slot)
    Assert.Equal(0,p.Server.Flows.ReportsDue)

[<Fact>]
let ``local allocation reuse also starts with no prior generation report mark`` () =
    let p = Lifecycle()
    let first = p.Open()
    p.Echo first
    Assert.Equal(2,p.Client.Flows.[first].ReportsDue)
    p.ResetAndAck first
    Assert.Equal(0,p.Client.Flows.ReportsDue)
    for expected in 1..255 do
        let slot = p.Open()
        Assert.Equal(expected,slot)
        p.ResetAndAck slot
    let reused = p.Open()
    Assert.Equal(first,reused)
    Assert.Equal(2u,p.Client.Flows.[reused].Gen)
    Assert.Equal(0,p.Client.Flows.ReportsDue)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``one sided FIN and unacknowledged local FIN do not retire live reports`` blocked =
    let p = Lifecycle()
    let slot = p.Open()
    if blocked then p.ServerPort.WriteLimit <- 0
    p.ClientFin(slot, if blocked then 1 else 0)
    let flow = p.Server.Flows.[slot]
    Assert.True(flow.Active)
    Assert.True(flow.RxDone)
    Assert.False(flow.TxDone)
    if blocked then
        Assert.True(flow.Reserve.Count > 0)
        Assert.True(flow.RxFinPending)
    Assert.Equal(2,flow.ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)
    p.StageServerFin slot
    Assert.True(flow.TxEndSent)
    Assert.False(flow.TxDone) // no authenticated custody ACK yet
    Assert.True(flow.Active)
    Assert.Equal(2,flow.ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)

[<Fact>]
let ``closing retires marks without cancelling an already frozen snapshot`` () =
    let p = Lifecycle()
    let slot = p.Open()
    p.StageSnapshot()
    p.ResetAndAck slot
    Assert.Equal(0,p.Server.Flows.ReportsDue)
    let pages = p.ReportRound()
    Assert.Equal<struct(int*bool)>([|struct(1,true)|],pages)
    Assert.Empty(p.ReportRound())

[<Fact>]
let ``custody close replaces old report mark with exactly one pending next generation mark`` () =
    let port = RecordingPort()
    let flows = FlowTable(port,ZeroStore(),Action<uint64,int>(fun _ _ -> ()),CreditPolicy.defaults)
    let source step gen flags offset length =
        let envelope = { RealLength=length; FlowSlot=3uy; Flags=flags; FlowGeneration=gen; StreamOffset=offset }
        Assert.True(flows.OnSource(step,0,envelope))
        flows.Pump 3
    // OPEN and FIN must be distinct: OPEN establishes the old generation mark.
    source 0UL 1u StreamFlags.Open 0UL 10
    Assert.Equal(2,flows.[3].ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    source 1UL 1u StreamFlags.Fin 10UL 0
    flows.NextEnvelope(3,10,true) |> ignore
    flows.NoteEndStep(3,5UL)
    source 2UL 2u StreamFlags.Open 0UL 10
    Assert.Equal(1u,flows.[3].Gen)
    Assert.Equal(1,port.Opened.Count)
    Assert.Equal(2,flows.[3].ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    // Insufficient custody does not prematurely close or retire a mark.
    flows.OnPeerAck 5UL
    Assert.Equal(1u,flows.[3].Gen)
    Assert.Equal(1,flows.ReportsDue)
    flows.OnPeerAck 6UL
    Assert.Equal<int list>([3],List.ofSeq port.Closed)
    Assert.Equal(struct(3,2u),port.Opened.[1])
    Assert.Equal(2,flows.[3].ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    Assert.True(flows.TakeReport 3)
    Assert.Equal(1,flows.ReportsDue)
    Assert.True(flows.TakeReport 3)
    Assert.Equal(0,flows.ReportsDue)
    for _ in 1..3 do
        flows.OnPeerAck 6UL
        flows.Pump 3
        Assert.False(flows.TakeReport 3)
    Assert.Equal(0,flows.ReportsDue)
    Assert.Single(port.Closed) |> ignore

[<Fact>]
let ``custody ACK with pending FIN reserve retains mark until actual drain closes flow`` () =
    let port = RecordingPort()
    port.WriteLimit <- 0
    let flows = FlowTable(port,ZeroStore(),Action<uint64,int>(fun _ _ -> ()),CreditPolicy.defaults)
    let source step flags offset length =
        let e = { RealLength=length; FlowSlot=3uy; Flags=flags; FlowGeneration=1u; StreamOffset=offset }
        Assert.True(flows.OnSource(step,0,e))
        flows.Pump 3
    source 0UL StreamFlags.Open 0UL 10
    source 1UL StreamFlags.Fin 10UL 0
    flows.NextEnvelope(3,0,true) |> ignore
    flows.NoteEndStep(3,5UL)
    flows.OnPeerAck 6UL
    let f = flows.[3]
    Assert.True(f.Active && f.TxDone && f.RxDone && f.RxFinPending)
    Assert.True(f.Reserve.Count > 0)
    Assert.Equal(2,f.ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    Assert.Empty(port.Closed)
    port.WriteLimit <- Int32.MaxValue
    flows.Pump 3
    Assert.False(f.Active)
    Assert.Equal(0,f.ReportsDue)
    Assert.Equal(0,flows.ReportsDue)
    Assert.Single(port.Closed) |> ignore
