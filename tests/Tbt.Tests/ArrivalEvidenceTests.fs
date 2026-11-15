module Tbt.Tests.ArrivalEvidenceTests

open System
open System.Buffers.Binary
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
open Tbt.Wire
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Tests.SessionTests

let private enabled () = Pair(SessionConfig.defaults, 1, 0.0, 8, arrivalClient=true, arrivalServer=true)
let private bytes (e: CodedArrivalEvidence voption) = Assert.True(e.IsSome); Assert.False(e.Value.Overflow); e.Value.Bytes
let private source (p: Pair) (s: Session) slot (data: byte[]) = Assert.Equal(data.Length, p.Feed(s,slot,ReadOnlySpan data,false))
let private openFlow (s: Session) = let mutable slot = -1 in Assert.True(s.TryOpenFlow(&slot)); slot
let private deliver (s: Session) (d: Session) =
    let f = Array.zeroCreate<byte> s.FrameLength
    Assert.True(s.TryProduce(0us,Span f))
    d.OnFrame(0us,Span f) |> ignore
    f

[<Theory>]
[<InlineData(false,false)>]
[<InlineData(true,false)>]
[<InlineData(false,true)>]
[<InlineData(true,true)>]
let ``experimental arrival is bilateral and default caps unchanged`` client server =
    let p = Pair(SessionConfig.defaults,1,0.0,3,arrivalClient=client,arrivalServer=server)
    Assert.Equal(client && server,p.Client.ArrivalEvidenceEnabled)
    Assert.Equal(client && server,p.Server.ArrivalEvidenceEnabled)
    Assert.Equal(0us, SupportedCaps &&& Caps.ExperimentalArrivalEvidence)
    Assert.Equal(0us, AllowedCaps &&& Caps.ExperimentalArrivalEvidence)
    Assert.Equal(p.Client.Caps,p.Server.Caps)
    Assert.Equal((if client && server then Caps.ExperimentalArrivalEvidence else 0us),p.Client.Caps &&& Caps.ExperimentalArrivalEvidence)

[<Fact>]
let ``default off data frames remain byte-identical to explicitly disabled offer`` () =
    let a = Pair(SessionConfig.defaults,1,0.0,5)
    let b = Pair(SessionConfig.defaults,1,0.0,5,arrivalClient=false,arrivalServer=false)
    for p in [a;b] do source p p.Client (openFlow p.Client) [|13uy|]
    let x,y=Array.zeroCreate<byte> a.Client.FrameLength,Array.zeroCreate<byte> b.Client.FrameLength
    Assert.True(a.Client.TryProduce(0us,Span x)); Assert.True(b.Client.TryProduce(0us,Span y))
    Assert.Equal<byte[]>(x,y)
    Assert.True(a.Client.Tx.GeneratedCodedFrameEvidence.IsNone)
    Assert.True(a.Server.Rx.AcceptedCodedFrameEvidence.IsNone)

[<Fact>]
let ``legacy codec stays strict and experimental codec checks version flags reserved length`` () =
    let h=Unchecked.defaultof<WinAckHead>
    let f=Array.zeroCreate<byte> 1344
    let mutable parsed=Unchecked.defaultof<WinAckHead>
    let mutable evidence=Unchecked.defaultof<CodedArrivalEvidence>
    WinAck.writeHead h (Span f)
    Assert.True(WinAck.tryReadHead (ReadOnlySpan f) &parsed)
    Assert.False(WinAck.tryReadHeadWithArrival (ReadOnlySpan f) &parsed &evidence)
    WinAck.writeHeadWithArrival h {Bytes=0UL;Overflow=false} (Span f)
    Assert.False(WinAck.tryReadHead (ReadOnlySpan f) &parsed)
    Assert.True(WinAck.tryReadHeadWithArrival (ReadOnlySpan f) &parsed &evidence)
    Assert.Equal(0UL,evidence.Bytes);Assert.False(evidence.Overflow)
    for at,value in [80,2uy;81,2uy;82,1uy;87,1uy;34,1uy] do
        let bad=Array.copy f
        bad.[at]<-value
        Assert.False(WinAck.tryReadHeadWithArrival (ReadOnlySpan bad) &parsed &evidence)
    Assert.False(WinAck.tryReadHeadWithArrival (ReadOnlySpan(f,0,95)) &parsed &evidence)
    Assert.False(WinAck.tryReadHead (ReadOnlySpan(f,0,10)) &parsed)

[<Fact>]
let ``counter boundary is complete-frame signed-safe sticky overflow`` () =
    let fl=1415
    let cap=ArrivalAccounting.limit fl
    Assert.True(cap<=uint64 Int64.MaxValue);Assert.Equal(0UL,cap%uint64 fl)
    let last=ArrivalAccounting.addFrame fl {Bytes=cap-uint64 fl;Overflow=false}
    Assert.Equal(cap,last.Bytes);Assert.False(last.Overflow)
    let stopped=ArrivalAccounting.addFrame fl last
    Assert.Equal(cap,stopped.Bytes);Assert.True(stopped.Overflow)
    Assert.Equal(stopped,ArrivalAccounting.addFrame fl stopped)

[<Fact>]
let ``peer cumulative evidence distinguishes unknown zero reorder invalid and overflow`` () =
    let fl=1415UL
    let peer=ArrivalAccounting.PeerCounter(int fl)
    let generated={Bytes=20UL*fl;Overflow=false}
    Assert.True(peer.Evidence.IsNone)
    Assert.True(peer.TryObserve({Bytes=0UL;Overflow=false},generated))
    Assert.Equal(0UL,bytes peer.Evidence)
    for n in [3UL;3UL;1UL;10UL] do Assert.True(peer.TryObserve({Bytes=n*fl;Overflow=false},generated))
    Assert.Equal(10UL*fl,bytes peer.Evidence)
    Assert.False(peer.TryObserve({Bytes=21UL*fl;Overflow=false},generated))
    Assert.False(peer.TryObserve({Bytes=fl+1UL;Overflow=false},generated))
    Assert.Equal(10UL*fl,bytes peer.Evidence)
    Assert.True(peer.TryObserve({Bytes=10UL*fl;Overflow=true},generated))
    Assert.True(peer.Evidence.Value.Overflow)
    Assert.True(peer.TryObserve({Bytes=20UL*fl;Overflow=false},generated))
    Assert.True(peer.Evidence.Value.Overflow)
    Assert.Equal(10UL*fl,peer.Evidence.Value.Bytes)
    let fresh=ArrivalAccounting.PeerCounter(int fl)
    Assert.True(fresh.WithGeneratedBound({Bytes=0UL;Overflow=true}).Value.Overflow)
    Assert.True(fresh.Evidence.Value.Overflow)

[<Fact>]
let ``authenticated coded frames count once per sequence not once per source`` () =
    let p=enabled()
    let c,s=p.Client,p.Server
    source p c (openFlow c) [|13uy|]
    let wire=Array.zeroCreate<byte> c.FrameLength
    Assert.True(c.TryProduce(0us,Span wire))
    let replay=Array.copy wire
    let bad=Array.copy wire
    bad.[Layout.tagOffset c.SymbolSize]<-bad.[Layout.tagOffset c.SymbolSize] ^^^ 1uy
    s.OnFrame(0us,Span bad) |> ignore
    Assert.Equal(0UL,bytes s.Rx.AcceptedCodedFrameEvidence)
    s.OnFrame(0us,Span wire) |> ignore
    Assert.Equal(uint64 c.FrameLength,bytes s.Rx.AcceptedCodedFrameEvidence)
    s.OnFrame(0us,Span replay) |> ignore
    Assert.Equal(uint64 c.FrameLength,bytes s.Rx.AcceptedCodedFrameEvidence)
    // wire now holds authenticated plaintext. Re-seal same source with a NEW sequence.
    c.Tx.Seal(Span wire,0us)
    s.OnFrame(0us,Span wire) |> ignore
    Assert.Equal(2UL*uint64 c.FrameLength,bytes s.Rx.AcceptedCodedFrameEvidence)
    Assert.Equal(2UL*uint64 c.FrameLength,bytes c.Tx.GeneratedCodedFrameEvidence)
    Assert.Equal(1L,p.ServerPort.Stream(0).Length)

[<Fact>]
let ``successful coded fill counts but PAD and refused fill do not`` () =
    let p=enabled()
    let c=p.Client
    source p c (openFlow c) [|13uy|]
    let f=Array.zeroCreate<byte> c.FrameLength
    let before=bytes c.Tx.GeneratedCodedFrameEvidence
    Assert.True(c.ProduceFill(0us,Span f))
    Assert.Equal(before+uint64 c.FrameLength,bytes c.Tx.GeneratedCodedFrameEvidence)
    p.Server.OnFrame(0us,Span f) |> ignore
    Assert.Equal(uint64 c.FrameLength,bytes p.Server.Rx.AcceptedCodedFrameEvidence)
    let previous=bytes c.Tx.GeneratedCodedFrameEvidence
    c.Tx.SetCutLimit(c.Tx.NextCounter,0UL)
    Assert.False(c.ProduceFill(0us,Span f))
    Assert.Equal(previous,bytes c.Tx.GeneratedCodedFrameEvidence)
    let empty=enabled()
    Assert.True(empty.Client.ProduceFill(0us,Span f))
    Assert.Equal(0UL,bytes empty.Client.Tx.GeneratedCodedFrameEvidence)
    empty.Server.OnFrame(0us,Span f) |> ignore
    Assert.Equal(0UL,bytes empty.Server.Rx.AcceptedCodedFrameEvidence)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``heartbeat preserves tail existence without manufacturing accepted arrival`` down =
    let p=enabled()
    let slot=openFlow p.Client
    source p p.Client slot [|1uy|]
    for _ in 1..50 do p.Round() |> ignore
    if down then
        source p p.Server slot [|1uy|]
        for _ in 1..50 do p.Round() |> ignore
    let sender,receiver,port = if down then p.Server,p.Client,p.ClientPort else p.Client,p.Server,p.ServerPort
    let actual=bytes receiver.Rx.AcceptedCodedFrameEvidence
    let inferred=sender.Tx.UniqueArrived
    let retired=sender.Tx.UniqueAcked
    let step=sender.Tx.Head
    source p sender slot (Array.create 3000 7uy)
    let f=Array.zeroCreate<byte> sender.FrameLength
    while sender.Tx.Queued>0 do Assert.True(sender.TryProduce(0us,Span f,false))
    p.Clock.Now<-p.Clock.Now+60_000_000L
    Assert.True(sender.Tx.HeartbeatDue)
    Assert.True(sender.TryProduce(0us,Span f,false))
    receiver.OnFrame(0us,Span f) |> ignore
    Assert.Equal(actual,bytes receiver.Rx.AcceptedCodedFrameEvidence)
    Assert.Equal(sender.Tx.Head,receiver.Rx.Decoder.SeenEnd)
    Assert.Equal(step,receiver.Rx.Decoder.Frontier)
    Assert.Equal(1L,port.Stream(slot).Length)
    receiver.Tick()
    deliver receiver sender |> ignore
    Assert.Equal(actual,bytes sender.PeerAcceptedCodedFrameEvidence)
    Assert.True(sender.Tx.UniqueArrived>inferred) // Legacy recovery/consumer untouched.
    Assert.Equal(retired,sender.Tx.UniqueAcked)
    for _ in 1..100 do p.Round() |> ignore
    Assert.Equal(3001L,port.Stream(slot).Length)
    Assert.True(bytes receiver.Rx.AcceptedCodedFrameEvidence>actual)

[<Fact>]
let ``accepted arrival decoded frontier and local writes remain distinct`` () =
    let p=enabled()
    let slot=openFlow p.Client
    p.ServerPort.WriteLimit<-0
    source p p.Client slot (Array.create 100 7uy)
    for _ in 1..50 do p.Round() |> ignore
    Assert.True(bytes p.Server.Rx.AcceptedCodedFrameEvidence>0UL)
    Assert.True(p.Server.Rx.Decoder.Frontier>0UL)
    Assert.True(p.Client.Tx.UniqueAcked>0L)
    Assert.Equal(0L,p.ServerPort.Stream(slot).Length)

[<Fact>]
let ``asymmetric receiver reports remain in matching send direction`` () =
    let p=enabled()
    let slot=openFlow p.Client
    source p p.Client slot (Array.create 8000 7uy)
    for _ in 1..100 do p.Round() |> ignore
    source p p.Server slot [|13uy|]
    for _ in 1..100 do p.Round() |> ignore
    Assert.Equal(bytes p.Server.Rx.AcceptedCodedFrameEvidence,bytes p.Client.PeerAcceptedCodedFrameEvidence)
    Assert.Equal(bytes p.Client.Rx.AcceptedCodedFrameEvidence,bytes p.Server.PeerAcceptedCodedFrameEvidence)
    Assert.True(bytes p.Client.PeerAcceptedCodedFrameEvidence>bytes p.Server.PeerAcceptedCodedFrameEvidence)
    let fresh=enabled()
    Assert.Equal(0UL,bytes fresh.Client.Tx.GeneratedCodedFrameEvidence)
    Assert.Equal(0UL,bytes fresh.Server.Rx.AcceptedCodedFrameEvidence)
    Assert.True(fresh.Client.PeerAcceptedCodedFrameEvidence.IsNone)

/// Seal an authenticated report using the same existing Tx API; no forged peer permission.
let private reportHead (sender:Session) evidence (h: WinAckHead) =
    let f=Array.zeroCreate<byte> sender.FrameLength
    FrameMath.writeChunkHeader sender.SymbolSize (Span f)
    WinAck.writeHeadWithArrival h evidence (Span(f,Layout.SymbolOffset,sender.SymbolSize))
    let meta={ Type=CellType.WinAck;Flags=MetaFlags.None;Slot=0uy;StepUsed=0uy;Aux=0us
               RxWinGrant=100us;StepId=0UL;AckStepLo=0UL;CarrierWm=0UL;KHist=0u;ErasureQ16=0us }
    Meta.write meta (Span(f,Layout.MetaOffset,Layout.MetaLength))
    f.[f.Length-2]<-13uy;f.[f.Length-1]<-10uy
    sender.Tx.Seal(Span f,0us)
    f

let private report sender evidence thi ctrlAck =
    reportHead sender evidence { Unchecked.defaultof<WinAckHead> with THi=thi;Span=1uy;CtrlAckHi=ctrlAck }

[<Fact>]
let ``invalid evidence cannot suppress a valid base highwater`` () =
    let p=enabled()
    let c=p.Client
    let slot=openFlow c
    for _ in 1..3 do source p c slot [|1uy|]
    let f=Array.zeroCreate<byte> c.FrameLength
    while c.Tx.Queued>0 do Assert.True(c.TryProduce(0us,Span f,false))
    let high=c.Tx.Head-1UL
    let impossible=bytes c.Tx.GeneratedCodedFrameEvidence+uint64 c.FrameLength
    let wire=report p.Server {Bytes=impossible;Overflow=false} high 1u
    c.OnFrame(0us,Span wire) |> ignore
    Assert.Equal(high,c.Tx.PeerSeenHi)
    Assert.True(c.PeerAcceptedCodedFrameEvidence.IsNone)
    let nonMultiple=report p.Server {Bytes=1UL;Overflow=false} high 1u
    c.OnFrame(0us,Span nonMultiple) |> ignore
    Assert.True(c.PeerAcceptedCodedFrameEvidence.IsNone)
    let zero=report p.Server {Bytes=0UL;Overflow=false} high 1u
    c.OnFrame(0us,Span zero) |> ignore
    Assert.Equal(0UL,bytes c.PeerAcceptedCodedFrameEvidence)

[<Fact>]
let ``real JOIN negotiation retains session counters and experimental caps`` () =
    let p=enabled()
    let slot=openFlow p.Client
    source p p.Client slot [|1uy|]
    for _ in 1..30 do p.Round() |> ignore
    let before=bytes p.Server.Rx.AcceptedCodedFrameEvidence
    let parameters=p.ClientParams
    let admission=JoinAdmission(parameters.Keys.PathKey,SeededEntropy 5,p.Clock,parameters.Keys.SessionId,parameters.Symbol,parameters.Caps,4,1448,288)
    let offer=JoinOffer(parameters.Keys.PathKey,SeededEntropy 6,parameters.Keys.SessionId,parameters.Symbol,parameters.Caps,4,1us,1u,1448)
    let request=(tryJoin parameters.Keys.PathKey (ReadOnlySpan offer.Frame)).Value
    let decision=admission.Decide(ReadOnlySpan offer.Frame,request,1,false,false)
    Assert.Equal(JoinVerdict.Accept,decision.Verdict)
    Assert.True((offer.TryAccept(ReadOnlySpan decision.Ack)).IsSome)
    p.Client.NotePathJoined 1us |> ignore;p.Server.NotePathJoined 1us |> ignore
    Assert.Equal(before,bytes p.Server.Rx.AcceptedCodedFrameEvidence)
    source p p.Client slot [|2uy|]
    let frame=Array.zeroCreate<byte> p.Client.FrameLength
    let sequence=p.Client.Tx.NextCounter
    Assert.True(p.Client.TryProduce(1us,Span frame,false))
    let ciphertext=Array.copy frame
    p.Server.OnFrame(1us,Span frame) |> ignore
    let after=bytes p.Server.Rx.AcceptedCodedFrameEvidence
    Assert.Equal(before+uint64 frame.Length,after)
    Assert.Equal(2L,p.ServerPort.Stream(slot).Length)
    p.Server.OnFrame(1us,Span(Array.copy ciphertext)) |> ignore
    Assert.Equal(after,bytes p.Server.Rx.AcceptedCodedFrameEvidence)
    // Re-authenticate the identical cell_seq for path 0, so rejection is global replay,
    // not merely the path AAD mismatch of copying path-1 ciphertext unchanged.
    let table=KeyTable(parameters.Keys.Suite)
    table.Install(0u,ReadOnlySpan(parameters.Keys.UpKey 0u),0UL)
    let mask () : IHeaderMask =
        if parameters.Caps &&& Caps.HpMask=0us then NoHeaderMask()
        else HeaderMasks.Create(parameters.Keys.Suite,ReadOnlySpan parameters.Keys.HpUp)
    let resealer=CellSealer(Direction.Up,parameters.Symbol,table,mask())
    resealer.Seal(Span frame,sequence,0us)
    let freshTable=KeyTable(parameters.Keys.Suite)
    freshTable.Install(0u,ReadOnlySpan(parameters.Keys.UpKey 0u),0UL)
    let freshOpener=CellOpener(Direction.Up,parameters.Symbol,freshTable,mask())
    let mutable counter=0UL
    Assert.True(freshOpener.TryOpen(Span(Array.copy frame),0us,&counter))
    Assert.Equal(sequence,counter)
    p.Server.OnFrame(0us,Span frame) |> ignore
    Assert.Equal(after,bytes p.Server.Rx.AcceptedCodedFrameEvidence)
    Assert.Equal(2L,p.ServerPort.Stream(slot).Length)
    // Drain outstanding parity/control before testing newly joined reverse traffic.
    for _ in 1..30 do p.Round() |> ignore
    let downBefore=bytes p.Client.Rx.AcceptedCodedFrameEvidence
    source p p.Server slot [|3uy|]
    Assert.True(p.Server.TryProduce(1us,Span frame,false))
    p.Client.OnFrame(1us,Span frame) |> ignore
    Assert.Equal(downBefore+uint64 frame.Length,bytes p.Client.Rx.AcceptedCodedFrameEvidence)
    Assert.Equal(1L,p.ClientPort.Stream(slot).Length)

[<Fact>]
let ``real rekey preserves cumulative arrival evidence`` () =
    let p=enabled()
    let c,s=p.Client,p.Server
    let slot=openFlow c
    source p c slot [|1uy|]
    for _ in 1..30 do p.Round() |> ignore
    let before=bytes s.Rx.AcceptedCodedFrameEvidence
    Assert.True(c.BeginRekey())
    let chunk=Array.create 32768 7uy
    let mutable n=0
    while n<20000 && not(c.TxGeneration=1u && c.TxKeyCount=1 && s.RxGeneration=1u && s.RxKeyCount=1) do
        p.Feed(c,slot,ReadOnlySpan chunk,false) |> ignore
        p.Round() |> ignore
        Assert.True(bytes s.Rx.AcceptedCodedFrameEvidence>=before)
        n<-n+1
    Assert.True(n<20000)
    Assert.True(bytes s.Rx.AcceptedCodedFrameEvidence>before)
    for _ in 1..50 do p.Round() |> ignore
    Assert.Equal(bytes s.Rx.AcceptedCodedFrameEvidence,bytes c.PeerAcceptedCodedFrameEvidence)

[<Fact>]
let ``client rejects authenticated server selection of unoffered experimental capability`` () =
    let entropy=SeededEntropy 13
    let offer=ClientOffer(psk,Guid(Array.create 16 7uy),entropy,uint64 unixMs,1448,256)
    let server=ServerOffer([|psk|],entropy,4096us,256,offerArrivalEvidence=true)
    let accepted=server.TryAccept(ReadOnlySpan offer.Frame,unixMs,1448).Value
    let cn=ClientInit.clientNonce (Bootstrap.content (ReadOnlySpan offer.Frame))
    let content=Bootstrap.content (ReadOnlySpan accepted.Reply)
    let h,pub=ServerInit.tryOpen (ReadOnlySpan psk) cn content |> ValueOption.get
    let changed={h with Caps=h.Caps ||| Caps.ExperimentalArrivalEvidence}
    let rebuilt=ServerInit.build (ReadOnlySpan psk) cn (content.Slice(0,32)) changed (ReadOnlySpan pub) (ReadOnlySpan(Array.zeroCreate<byte>(ServerInit.BodyLength-84)))
    Assert.True(offer.TryAccept(ReadOnlySpan(Bootstrap.frame (ReadOnlySpan rebuilt))).IsNone)

[<Fact>]
let ``real session applies lost repeated reordered cumulative reports idempotently`` () =
    let p=enabled()
    let c=p.Client
    source p c (openFlow c) (Array.create 10000 7uy)
    let frame=Array.zeroCreate<byte> c.FrameLength
    while c.Tx.Queued>0 do Assert.True(c.TryProduce(0us,Span frame,false))
    let fl=uint64 c.FrameLength
    Assert.True(bytes c.Tx.GeneratedCodedFrameEvidence>=4UL*fl)
    report p.Server {Bytes=fl;Overflow=false} 0UL 1u |> ignore // lost report
    let older=report p.Server {Bytes=2UL*fl;Overflow=false} 0UL 1u
    let newer=report p.Server {Bytes=4UL*fl;Overflow=false} 0UL 1u
    c.OnFrame(0us,Span newer) |> ignore
    c.OnFrame(0us,Span older) |> ignore
    Assert.Equal(4UL*fl,bytes c.PeerAcceptedCodedFrameEvidence)
    let repeated=report p.Server {Bytes=4UL*fl;Overflow=false} 0UL 1u
    c.OnFrame(0us,Span repeated) |> ignore
    Assert.Equal(4UL*fl,bytes c.PeerAcceptedCodedFrameEvidence)


[<Fact>]
let ``accounting bound rejection does not suppress control ACK or NACK repair`` () =
    let p=enabled()
    let c=p.Client
    source p c (openFlow c) (Array.create 3000 7uy)
    let frame=Array.zeroCreate<byte> c.FrameLength
    while c.Tx.Queued>0 do Assert.True(c.TryProduce(0us,Span frame,false))
    Assert.True(c.BeginMssNotice(0us,1448us))
    Assert.True(c.TryProduce(0us,Span frame))
    let ctrl=c.CtrlSent
    Assert.Equal(1L,ctrl)
    let h={Unchecked.defaultof<WinAckHead> with Span=1uy;Deficit=1us;NackFlags=1uy;CtrlAckHi=2u}
    let report=reportHead p.Server {Bytes=bytes c.Tx.GeneratedCodedFrameEvidence+uint64 c.FrameLength;Overflow=false} h
    c.OnFrame(0us,Span report) |> ignore
    Assert.True(c.PeerAcceptedCodedFrameEvidence.IsNone)
    Assert.True(c.RepairWaiting)
    p.Clock.Now<-p.Clock.Now+1_000_000_000L
    c.Tick()
    for _ in 1..16 do c.TryProduce(0us,Span frame) |> ignore
    Assert.True(c.Tx.BoostersSent>0L)
    Assert.Equal(ctrl,c.CtrlSent) // No retransmission: CTRL ack was applied despite invalid evidence.

/// Real SessionCarrier writes, with an in-memory already-connected path.
type private ArrivalPath(peer:Session) =
    let buffer=Array.zeroCreate<byte> 2048
    interface IPath with
        member val PathId=0us with get,set
        member val FrameLength=0 with get,set
        member _.Alive=true
        member _.UnsentBytes=0
        member _.Attach _=()
        member _.Backlog with set (_:int)=()
        member _.Watch(_,_) = ()
        member _.Receive _=()
        member p.TryBeginFrame(frame:byref<Span<byte>>) =
            frame<-Span(buffer,0,(p:>IPath).FrameLength)
            true
        member p.CommitFrame() = peer.OnFrame(0us,Span(buffer,0,(p:>IPath).FrameLength)) |> ignore
        member _.Close()=()

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``actual evidence coexists with unchanged client pacing in both asymmetric directions`` pacing =
    let p=enabled()
    let upBridge,downBridge=FlowBridge ValueNone,FlowBridge ValueNone
    upBridge.Attach p.Client
    downBridge.Attach p.Server
    let up=SessionCarrier(p.Client,upBridge,uplinkPace=pacing)
    let down=SessionCarrier(p.Server,downBridge)
    up.AddPath(ArrivalPath p.Server)
    down.AddPath(ArrivalPath p.Client)
    let tick () =
        p.Clock.Now<-p.Clock.Now+1_000_000L
        p.Client.Tick()
        p.Server.Tick()
        up.WriteBatch 256 |> ignore
        down.WriteBatch 256 |> ignore
    let slot=openFlow p.Client
    source p p.Client slot (Array.create 8000 7uy)
    for _ in 1..200 do tick()
    Assert.Equal(8000L,p.ServerPort.Stream(slot).Length)
    source p p.Server slot [|13uy|]
    for _ in 1..200 do tick()
    Assert.Equal(1L,p.ClientPort.Stream(slot).Length)
    Assert.Equal(bytes p.Server.Rx.AcceptedCodedFrameEvidence,bytes p.Client.PeerAcceptedCodedFrameEvidence)
    Assert.Equal(bytes p.Client.Rx.AcceptedCodedFrameEvidence,bytes p.Server.PeerAcceptedCodedFrameEvidence)
    Assert.True(bytes p.Client.PeerAcceptedCodedFrameEvidence>bytes p.Server.PeerAcceptedCodedFrameEvidence)


/// Raw authenticated frames isolate the receive acceptance boundary from dispatch/decoder.
let private plainFrame (session:Session) kind =
    let f=Array.zeroCreate<byte> session.FrameLength
    FrameMath.writeChunkHeader session.SymbolSize (Span f)
    let m={ Unchecked.defaultof<Meta> with Type=kind;StepUsed=1uy }
    Meta.write m (Span(f,Layout.MetaOffset,Layout.MetaLength))
    f.[f.Length-2]<-13uy
    f.[f.Length-1]<-10uy
    f

[<Theory>]
[<InlineData(1,true)>]
[<InlineData(2,true)>]
[<InlineData(3,true)>]
[<InlineData(4,false)>]
[<InlineData(5,false)>]
[<InlineData(6,false)>]
[<InlineData(8,false)>]
[<InlineData(9,false)>]
let ``all known cell types classify only accepted coded frame bytes`` kind coded =
    let p=enabled()
    let frame=plainFrame p.Client (LanguagePrimitives.EnumOfValue<byte,CellType>(byte kind))
    p.Client.Tx.Seal(Span frame,0us)
    Assert.True(p.Server.Rx.Open(Span frame,0us).IsSome)
    let expected=if coded then uint64 frame.Length else 0UL
    Assert.Equal(expected,bytes p.Client.Tx.GeneratedCodedFrameEvidence)
    Assert.Equal(expected,bytes p.Server.Rx.AcceptedCodedFrameEvidence)

[<Theory>]
[<InlineData(0,7)>]
[<InlineData(1,8)>]
[<InlineData(3,0)>]
[<InlineData(37,1)>]
let ``authenticated invalid meta does not create accepted arrival`` offset value =
    let p=enabled()
    let frame=plainFrame p.Client CellType.Data
    frame.[Layout.MetaOffset+offset]<-byte value
    p.Client.Tx.Seal(Span frame,0us)
    Assert.True(p.Server.Rx.Open(Span frame,0us).IsNone)
    Assert.Equal(1L,p.Server.Rx.Counters.ProtoErrors) // Authentication succeeded before meta rejection.
    Assert.Equal(0L,p.Server.Rx.Counters.Cells)
    Assert.Equal(0UL,bytes p.Server.Rx.AcceptedCodedFrameEvidence)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
let ``bad framing does not count or consume authenticated sequence`` shortFrame =
    let p=enabled()
    let frame=plainFrame p.Client CellType.Data
    p.Client.Tx.Seal(Span frame,0us)
    let bad=Array.copy frame
    if shortFrame then Assert.True(p.Server.Rx.Open(Span(bad,0,bad.Length-1),0us).IsNone)
    else
        bad.[bad.Length-1]<-0uy
        Assert.True(p.Server.Rx.Open(Span bad,0us).IsNone)
    Assert.Equal(1L,p.Server.Rx.Counters.FramingErrors)
    Assert.Equal(0UL,bytes p.Server.Rx.AcceptedCodedFrameEvidence)
    Assert.True(p.Server.Rx.Open(Span frame,0us).IsSome)
    Assert.Equal(uint64 frame.Length,bytes p.Server.Rx.AcceptedCodedFrameEvidence)
