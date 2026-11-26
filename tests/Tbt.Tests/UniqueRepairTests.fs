module Tbt.Tests.UniqueRepairTests

open System
open Xunit
open Tbt.Carrier
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Wire

let check condition message = if not condition then failwith message

type Clock() =
    member val Now = 0L with get,set
    interface IClock with
        member c.NowNs() = c.Now
        member c.FreshNs() = c.Now

type Entropy() =
    let random = Random 937
    interface IEntropy with member _.Fill(dst) = random.NextBytes dst

let port () =
    { new IFlowPort with
        member _.Open(_,_) = ()
        member _.Write(_,data) = data.Length
        member _.Finish(_) = ()
        member _.Reset(_) = ()
        member _.Close(_) = ()
        member _.Credit(_) = () }


let private runApi rounds ackBeforeRender repairFirst =
    let clock = Clock()
    let entropy = Entropy()
    let psk = Array.init 32 (fun i -> byte(i*3+1))
    let offer = ClientOffer(psk,Guid(Array.create 16 7uy),entropy,1_800_000_000_000UL,1448,256)
    let accepted = ServerOffer([|psk|],entropy,512us,256).TryAccept(ReadOnlySpan offer.Frame,1_800_000_000_000L,1448).Value
    let struct(cp,_) = offer.TryAccept(ReadOnlySpan accepted.Reply).Value
    let config = {SessionConfig.defaults with WindowSteps=512}
    let c = Session(Role.Client,cp,port(),clock,config)
    let s = Session(Role.Server,accepted.Params,port(),clock,config)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable slot = -1
    check (c.TryOpenFlow(&slot)) "flow allocation failed"
    let mutable app = Span<byte>.Empty
    check (c.TryBeginSource(&app)) "source unavailable"
    app.[0] <- 11uy
    c.CommitSource(slot,1,false)
    c.Flush()
    let all = ResizeArray<string*Meta>()
    let drain phase limit =
        let mutable n = 0
        while n < limit && c.Tx.TryRenderNext(Span frame,0us,0UL,4096us,0us) do
            check (s.OnFrame(0us,Span frame) <> UInt64.MaxValue) "frame authentication failed"
            let mutable meta = Unchecked.defaultof<Meta>
            check (Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &meta) "meta failed"
            all.Add(phase,meta)
            n <- n+1
        n
    let initial = if repairFirst then c.Tx.Queued else drain "initial" Int32.MaxValue
    for i in 1..rounds do
        clock.Now <- clock.Now + int64 (ceil c.Tx.RepairWaitNs) + 1L
        c.Tx.OnNack(0UL,1,1)
        if ackBeforeRender && i=rounds then c.Tx.OnPeerAck(1UL,4096us)
        drain ("round"+string i) (if repairFirst && i<65 then 4 else Int32.MaxValue) |> ignore
    if not ackBeforeRender then c.Tx.OnPeerAck(1UL,4096us)
    let cells = all |> Seq.map snd |> Seq.toArray
    let nonBooster = cells |> Array.filter(fun m -> m.Type<>CellType.Booster)
    let distinct = nonBooster |> Array.map(fun m -> struct(m.Type,m.StepId,m.Slot)) |> Array.distinct
    let boosters = cells |> Array.filter(fun m -> m.Type=CellType.Booster)
    check (distinct.Length=initial) "repair introduced new nonbooster identity"
    check (c.Tx.UniqueArrived=c.Tx.UniqueAcked) "post-ACK arrived/acked mismatch"

    initial,c.Tx.UniqueAcked,nonBooster.Length,boosters.Length

[<Theory>]
[<InlineData(0, false)>]
[<InlineData(64, false)>]
[<InlineData(65, false)>]
[<InlineData(65, true)>]
let ``first-copy counters exclude repeated DATA including repair-first`` rounds repairFirst =
    let initial,acked,_,_=runApi rounds false repairFirst
    Assert.Equal(int64 initial,acked)

[<Fact>]
let ``custody before queued fallback does not charge discarded candidates`` () =
    let initial,acked,nonBooster,boosters=runApi 65 true false
    Assert.Equal(int64 initial,acked)
    Assert.Equal(initial,nonBooster)
    Assert.Equal(256,boosters)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``natural missing-tail WIN_ACK fallback preserves first-copy counts`` deliverOriginalsLate =
    let clock=Clock()
    clock.Now <- 1L
    let entropy=Entropy()
    let psk=Array.init 32 (fun i -> byte(i*3+1))
    let offer=ClientOffer(psk,Guid(Array.create 16 7uy),entropy,1_800_000_000_000UL,1448,256)
    let accepted=ServerOffer([|psk|],entropy,512us,256).TryAccept(ReadOnlySpan offer.Frame,1_800_000_000_000L,1448).Value
    let struct(cp,_)=offer.TryAccept(ReadOnlySpan accepted.Reply).Value
    let config={SessionConfig.defaults with WindowSteps=512}
    let c=Session(Role.Client,cp,port(),clock,config)
    let received=ResizeArray<byte>()
    let serverPort={new IFlowPort with
        member _.Open(_,_) = ()
        member _.Write(_,data) =
            for b in data do received.Add b
            data.Length
        member _.Finish(_) = ()
        member _.Reset(_) = ()
        member _.Close(_) = ()
        member _.Credit(_) = ()}
    let s=Session(Role.Server,accepted.Params,serverPort,clock,config)
    let inspector=Session(Role.Server,accepted.Params,port(),clock,config)
    let frame=Array.zeroCreate<byte> c.FrameLength
    let mutable slot = -1
    check (c.TryOpenFlow(&slot)) "flow failed"
    let addSource () =
        let mutable app=Span<byte>.Empty
        check (c.TryBeginSource(&app)) "source failed"
        app.[0] <- 11uy
        c.CommitSource(slot,1,false)
        c.Flush()
    addSource()
    let delayed=ResizeArray<byte[]>()
    let original=ResizeArray<Meta>()
    let coded=ResizeArray<Meta>()
    let mutable fallback=false
    let mutable boosters=0
    let mutable nackCount=0
    let clientDrain initial =
        let mutable count=0
        while c.TryProduce(0us,Span frame) do
            count <- count+1
            check (count<1024) "unbounded client drain"
            let copy=Array.copy frame
            check (inspector.OnFrame(0us,Span copy)<>UInt64.MaxValue) "inspect auth"
            let mutable m=Unchecked.defaultof<Meta>
            check (Meta.tryRead (ReadOnlySpan(copy,Layout.MetaOffset,Layout.MetaLength)) &m) "meta"
            let isCoded=m.Type=CellType.Data || m.Type=CellType.Parity || m.Type=CellType.Booster
            if isCoded then coded.Add m
            if initial && isCoded then
                original.Add m
                delayed.Add(Array.copy frame)
            if m.Type=CellType.Booster then boosters <- boosters+1
            let deliver=not isCoded || (not initial && m.Type=CellType.Data)
            if deliver then
                check (s.OnFrame(0us,Span frame)<>UInt64.MaxValue) "server auth"
                if m.Type=CellType.Data then
                    fallback <- true
                    if deliverOriginalsLate then
                        for i in delayed.Count-1 .. -1 .. 0 do
                            check (s.OnFrame(0us,Span delayed.[i])<>UInt64.MaxValue) "late original auth"
                        delayed.Clear()
    let serverDrain () =
        let mutable count=0
        while s.TryProduce(0us,Span frame) do
            count <- count+1
            check (count<1024) "unbounded server drain"
            check (c.OnFrame(0us,Span frame)<>UInt64.MaxValue) "client auth"
            let mutable m=Unchecked.defaultof<Meta>
            check (Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &m) "feedback meta"
            let mutable span=""
            let mutable deficit=""
            if m.Type=CellType.WinAck then
                let mutable h=Unchecked.defaultof<WinAckHead>
                check (WinAck.tryReadHead (ReadOnlySpan(frame,Layout.SymbolOffset,c.SymbolSize)) &h) "ack head"
                span <- string h.Span
                deficit <- string h.Deficit
                if h.NackFlags &&& 1uy<>0uy then nackCount <- nackCount+1
    clientDrain true
    let mutable rounds=0
    while c.Tx.PeerAck<1UL && rounds<200 do
        clock.Now <- clock.Now + max 50_000_001L (int64(ceil c.Tx.RepairWaitNs)+1L)
        c.Tick();s.Tick()
        serverDrain()
        clientDrain false
        rounds <- rounds+1
    check (c.Tx.PeerAck=1UL && fallback) "fallback/ACK did not complete"
    check (received.ToArray()=[|11uy|]) "application byte mismatch"
    check (not c.Faulted && not s.Faulted && not inspector.Faulted) "session fault"
    let nonBooster=coded |> Seq.filter(fun m -> m.Type<>CellType.Booster) |> Seq.toArray
    let distinct=nonBooster |> Array.map(fun m -> struct(m.Type,m.StepId,m.Slot)) |> Array.distinct
    check (original.Count=3 && distinct.Length=3 && nonBooster.Length=4) "coded identity mismatch"
    Assert.Equal(int64 distinct.Length,c.Tx.UniqueAcked)
    Assert.Equal(int64 distinct.Length,c.Tx.UniqueArrived)

/// API fixture: no feedback fabrication hidden inside transport helpers.
type private ApiFixture(newRepair: bool) =
    let pair = SessionTests.Pair({SessionConfig.defaults with WindowSteps=512; NewRepair=newRepair},1,0.0,937)
    let c,s = pair.Client,pair.Server
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable slot = -1
    do check (c.TryOpenFlow(&slot)) "flow allocation"
    member _.Client = c
    member _.Server = s
    member _.Clock = pair.Clock
    member _.Add() =
        let mutable app=Span<byte>.Empty
        check (c.TryBeginSource(&app)) "source allocation"
        app.[0] <- 11uy
        c.CommitSource(slot,1,false)
        c.Flush()
    member _.AddFull() =
        for _ in 1..16 do
            let mutable app=Span<byte>.Empty
            check (c.TryBeginSource(&app)) "full source allocation"
            app.[0] <- 11uy
            c.CommitSource(slot,1,false)
        c.Flush()
    member _.Drain() =
        let metas=ResizeArray<Meta>()
        while c.Tx.TryRenderNext(Span frame,0us,0UL,4096us,0us) do
            check (s.OnFrame(0us,Span frame)<>UInt64.MaxValue) "auth"
            let mutable m=Unchecked.defaultof<Meta>
            check (Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &m) "meta"
            metas.Add m
        metas.ToArray()
    member _.Expire() = pair.Clock.Now <- pair.Clock.Now + int64(ceil c.Tx.RepairWaitNs)+1L

[<Fact>]
let ``partial NACK queue keeps unused repair seeds available`` () =
    let f=ApiFixture(false)
    for _ in 1..8 do f.Add()
    f.Drain() |> ignore
    let tx=f.Client.Tx
    // Two ranges of four steps, 31 rounds: 31 × 2 × 4 × 4 = 992.
    // Each step has used 124 seeds. An extra five on step 4 leaves 27 slots.
    for _ in 1..31 do
        f.Expire()
        tx.OnNack(0UL,4,4)
        tx.OnNack(4UL,4,4)
    f.Expire()
    tx.OnNack(4UL,1,2)
    f.Expire()
    // Eight per step: the last step gets only three of the 27 free slots.
    tx.OnNack(0UL,4,20)
    let partial=f.Drain() |> Array.filter(fun m -> m.Type=CellType.Booster && m.StepId=3UL)
    Assert.Equal(127,partial.Length)
    Assert.Equal(126uy,partial.[126].Slot)
    Assert.Equal(3us,partial.[126].Aux)
    f.Expire()
    tx.OnNack(3UL,1,1)
    let next=f.Drain() |> Array.filter(fun m -> m.Type=CellType.Booster)
    Assert.Equal<byte>([|127uy;128uy;129uy;130uy|],next |> Array.map(fun m -> m.Slot))

[<Fact>]
let ``DATA identity ring reuse keeps new absolute steps distinct`` () =
    let f=ApiFixture(false)
    let mutable expected=0L
    for i in 0..513 do
        f.Add()
        if i>=512 then
            // Public API robustness: obsolete absolute range cannot affect the reused slot.
            f.Client.Tx.OnNack(0UL,1,16)
        let cells=f.Drain()
        Assert.Equal(3,cells.Length)
        Assert.All(cells, fun m -> Assert.Equal(uint64 i,m.StepId))
        expected <- expected+3L
        f.Clock.Now <- f.Clock.Now+1_000_000L
        f.Client.Tx.OnPeerAck(uint64(i+1),4096us)
        Assert.Equal(expected,f.Client.Tx.UniqueAcked)

[<Fact>]
let ``opt-in scheduler DATA fallback excludes duplicate first copies`` () =
    let f=ApiFixture(true)
    f.Add()
    Assert.Equal(3,f.Drain().Length)
    f.Clock.Now <- f.Clock.Now+100_000_000L
    f.Client.Tx.OnPeerAck(1UL,4096us) // establish legitimate public API RTT sample
    f.Add()
    Assert.Equal(3,f.Drain().Length)
    let mutable fallback=0
    let mutable boosters=0
    for _ in 1..65 do
        f.Expire()
        f.Client.Tx.OnNack(1UL,1,1)
        for m in f.Drain() do
            if m.Type=CellType.Data then fallback <- fallback+1
            elif m.Type=CellType.Booster then boosters <- boosters+1
    Assert.Equal(256,boosters)
    Assert.Equal(1,fallback)
    f.Client.Tx.OnPeerAck(2UL,4096us)
    Assert.Equal(6L,f.Client.Tx.UniqueAcked)
    Assert.Equal(6L,f.Client.Tx.UniqueArrived)

/// In-memory committed frames, not OS TCP or physical wire evidence.
type private CommitSpy(peer:Session, ?failCommit:bool) =
    let buf=Array.zeroCreate<byte> 2048
    let cells=ResizeArray<Meta>()
    member _.Cells=cells
    interface IPath with
        member val PathId=0us with get,set
        member val FrameLength=0 with get,set
        member _.Alive=true
        member _.Attach(_) = ()
        member _.Backlog with set (_:int) = ()
        member _.Watch(_,_) = ()
        member _.Receive(_) = ()
        member _.UnsentBytes=0
        member p.TryBeginFrame(frame:byref<Span<byte>>) =
            frame <- Span(buf,0,(p:>IPath).FrameLength)
            true
        member p.CommitFrame() =
            if defaultArg failCommit false then invalidOp "fixture commit failure"
            let frame=Span(buf,0,(p:>IPath).FrameLength)
            check (peer.OnFrame(0us,frame)<>UInt64.MaxValue) "commit authentication"
            let mutable m=Unchecked.defaultof<Meta>
            check (Meta.tryRead (ReadOnlySpan(buf,Layout.MetaOffset,Layout.MetaLength)) &m) "commit meta"
            cells.Add m
        member _.Close() = ()

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``paced committed inflight follows first actual DATA not queue origin`` repairFirst =
    let f=ApiFixture(false)
    let bridge=FlowBridge ValueNone
    bridge.Attach f.Client
    let carrier=SessionCarrier(f.Client,bridge,uplinkPace=true)
    let path=CommitSpy(f.Server)
    carrier.AddPath path
    f.Add()
    let unique=Collections.Generic.HashSet<struct(CellType*uint64*byte)>()
    let inflight () =
        carrier.Describe().Split(' ')
        |> Array.find(fun s -> s.StartsWith("pace_inflight=",StringComparison.Ordinal))
        |> fun s -> Int64.Parse(s.Substring("pace_inflight=".Length))
    let send count =
        for _ in 1..count do
            Assert.Equal(1,carrier.WriteBatch 1)
            let m=path.Cells.[path.Cells.Count-1]
            if m.Type=CellType.Data || m.Type=CellType.Parity then unique.Add(struct(m.Type,m.StepId,m.Slot)) |> ignore
            // PeerAck is zero: no clamp ambiguity in this exact public observation.
            Assert.Equal(0UL,f.Client.Tx.PeerAck)
            Assert.Equal(int64 unique.Count*int64 f.Client.FrameLength,inflight())
            Assert.Equal(int64 path.Cells.Count,carrier.FramesOut)
    if not repairFirst then send 3
    for _ in 1..64 do
        f.Expire()
        f.Client.Tx.OnNack(0UL,1,1)
        send 4
    f.Expire()
    f.Client.Tx.OnNack(0UL,1,1)
    send 1
    if repairFirst then send 3
    Assert.Equal(260L,carrier.FramesOut)
    Assert.Equal(3,unique.Count)
    f.Client.Tx.OnPeerAck(1UL,4096us)
    Assert.Equal(0L,inflight())
    Assert.Equal(0,carrier.WriteBatch 1)
    carrier.Close()

[<Fact>]
let ``all sixteen DATA slot identities survive fallback and fill parity is separate`` () =
    let f=ApiFixture(false)
    f.AddFull()
    let tx=f.Client.Tx
    let frame=Array.zeroCreate<byte> f.Client.FrameLength
    Assert.Equal(CellType.Parity,tx.RenderFill(Span frame,0us,0UL,4096us,0us))
    Assert.Equal(0L,tx.UniqueRendered)
    let originals=f.Drain()
    Assert.Equal(20,originals.Length)
    Assert.Equal<int>([|0..15|], originals |> Array.filter(fun m -> m.Type=CellType.Data) |> Array.map(fun m -> int m.Slot))
    Assert.Equal(20L,tx.UniqueRendered)
    let mutable fallback=[||]
    for _ in 1..14 do
        f.Expire()
        tx.OnNack(0UL,1,16)
        let cells=f.Drain()
        if cells.Length>0 && cells.[0].Type=CellType.Data then fallback <- cells
    Assert.Equal<int>([|0..15|],fallback |> Array.map(fun m -> int m.Slot))
    Assert.Equal(20L,tx.UniqueRendered)
    Assert.Equal(284L,tx.CellsSent) // 1 fill + 20 originals + 247 BOOSTER + 16 repeated DATA
    tx.OnPeerAck(1UL,4096us)
    Assert.Equal(20L,tx.UniqueAcked)
    Assert.Equal(CellType.Pad,tx.RenderFill(Span frame,0us,0UL,4096us,0us))
    Assert.Equal(20L,tx.UniqueRendered)
    Assert.Equal(285L,tx.CellsSent)

[<Fact>]
let ``failed Seal does not record DATA as successfully rendered`` () =
    let f=ApiFixture(false)
    f.Add()
    let tooLong=Array.zeroCreate<byte> (f.Client.FrameLength+1)
    Assert.Throws<ArgumentException>(fun () -> f.Client.Tx.TryRenderNext(Span tooLong,0us,0UL,4096us,0us) |> ignore) |> ignore
    Assert.Equal(0L,f.Client.Tx.UniqueRendered)
    Assert.Equal(0L,f.Client.Tx.CellsSent)
    // The existing dequeue-before-Seal exception behavior is not rolled back.
    Assert.Equal(2,f.Drain().Length)
    Assert.Equal(2L,f.Client.Tx.UniqueRendered)
    f.Client.Tx.OnPeerAck(1UL,4096us)
    Assert.Equal(2L,f.Client.Tx.UniqueAcked)

[<Fact>]
let ``throwing CommitFrame does not book carrier submission counters`` () =
    let f=ApiFixture(false)
    let bridge=FlowBridge ValueNone
    bridge.Attach f.Client
    let carrier=SessionCarrier(f.Client,bridge,uplinkPace=true)
    let path=CommitSpy(f.Server,failCommit=true)
    carrier.AddPath path
    f.Add()
    Assert.Throws<InvalidOperationException>(fun () -> carrier.WriteBatch 1 |> ignore) |> ignore
    Assert.Equal(1L,f.Client.Tx.UniqueRendered) // generated, not rolled back or called delivered
    Assert.Equal(0L,carrier.FramesOut)
    Assert.Empty(path.Cells)
    Assert.Contains(" pace_inflight=0 ",carrier.Describe())
    carrier.Close()
