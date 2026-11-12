module Tbt.Tests.BridgeTests

open System
open System.Net
open System.Net.Sockets
open System.Threading
open Xunit
open Tbt.Contracts
open Tbt.Wire
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Tests.SessionTests

/// A client session on a FlowBridge, with no paths (nothing leaves, so credit only falls).
let private clientOnBridge () =
    let config = { SessionConfig.defaults with WindowSteps = 512 }
    let entropy = SeededEntropy 11
    let offer = ClientOffer(psk, Guid(Array.create 16 7uy), entropy, uint64 unixMs, 1448, 256)
    let accepted = (ServerOffer([| psk |], entropy, uint16 config.WindowSteps, 256)).TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    let struct (p, _) = offer.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    let bridge = FlowBridge ValueNone
    let session = Session(Role.Client, p, bridge, ManualClock(), config)
    bridge.Attach session
    struct (session, bridge)

/// A loopback pair: (app end, end the bridge owns).
let private loopPair (listener: Socket) =
    let app = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    app.Connect(nonNull listener.LocalEndPoint)
    let local = listener.Accept()
    struct (app, local)

let private listen () =
    let l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    l.Bind(IPEndPoint(IPAddress.Loopback, 0))
    l.Listen 256
    l

/// Read slot until the peer's credit is spent (the app sent more than the credit).
let private drainCredit (session: Session) (bridge: FlowBridge) (slot: int) =
    let mutable n = 0
    while session.FlowRoom slot > 0 && n < 2000 do
        bridge.Read slot
        if session.FlowRoom slot > 0 then Thread.Sleep 1
        n <- n + 1

[<Fact>]
let ``a flow without credit leaves the read set and comes back on the peer's FLOW record`` () =
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    let struct (app, local) = loopPair listener
    use app = app
    Assert.True(bridge.TryAdd local)
    let slot = bridge.Opened.NextUp -1
    Assert.True(bridge.Armed.Contains slot)
    let room0 = session.FlowRoom slot
    Assert.True(room0 > 0 && room0 < Int32.MaxValue)
    app.Send(Array.create (room0 + 4096) 1uy) |> ignore
    drainCredit session bridge slot
    Assert.Equal(0, session.FlowRoom slot)
    Assert.False(bridge.Armed.Contains slot)
    Assert.Equal(0, bridge.Armed.Count)
    // The peer took the bytes and grew the limit: the event, not a scan, re-arms the slot.
    let f = session.Flows.[slot]
    let r = { State = FlowLifecycle.Open; FlowSlot = slot; FlowGeneration = f.Gen; DeliveredOffset = uint64 room0; CreditBytes = 8192u }
    session.Flows.OnPeerCredit(f.TxCreditEpoch + 1u, 1u, r)
    Assert.True(session.FlowRoom slot > 0)
    Assert.True(bridge.Armed.Contains slot)
    bridge.Read slot
    Assert.True(session.FlowRoom slot < 8192)
    // An old record does not re-arm or grow anything.
    let room1 = session.FlowRoom slot
    session.Flows.OnPeerCredit(0u, 0u, r)
    Assert.Equal(room1, session.FlowRoom slot)
    bridge.CloseAll()

[<Fact>]
let ``watch and service visit only armed and writing flows, not all 256 slots`` () =
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    let apps = ResizeArray<Socket>()
    for _ in 1 .. 40 do
        let struct (app, local) = loopPair listener
        apps.Add app
        Assert.True(bridge.TryAdd local)
    Assert.Equal(40, bridge.Opened.Count)
    Assert.Equal(40, bridge.Armed.Count)
    // 39 apps half-close: their flows send FIN and stop reading; one stays armed.
    let keep = bridge.Opened.NextUp -1
    let mutable slot = bridge.Opened.NextUp keep
    while slot >= 0 do
        apps.[slot - keep].Shutdown SocketShutdown.Send
        let mutable n = 0
        while bridge.Armed.Contains slot && n < 2000 do
            bridge.Read slot
            if bridge.Armed.Contains slot then Thread.Sleep 1
            n <- n + 1
        slot <- bridge.Opened.NextUp slot
    Assert.Equal(40, bridge.Opened.Count)
    Assert.Equal(1, bridge.Armed.Count)
    Assert.True(bridge.Armed.Contains keep)
    Assert.Equal(0, bridge.Writers.Count)
    let carrier = SessionCarrier(session, bridge)
    use poller = new Poller()
    let v0 = carrier.SlotVisits
    for _ in 1 .. 10 do
        carrier.Watch poller
        poller.Wait 0 |> ignore
        carrier.Service poller
    // One watch visit and two read passes per round, over one armed flow.
    Assert.InRange(carrier.SlotVisits - v0, 1L, 30L)
    // The round loop allocates nothing once warm.
    let b0 = GC.GetAllocatedBytesForCurrentThread()
    for _ in 1 .. 100 do
        carrier.Watch poller
        poller.Wait 0 |> ignore
        carrier.Service poller
    Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - b0)
    for a in apps do a.Dispose()
    bridge.CloseAll()

/// A path that takes every frame and counts them.
type private CountingPath(id: uint16) =
    let buf = Array.zeroCreate<byte> 2048
    member val Frames = 0 with get, set
    interface IPath with
        member val PathId = id with get, set
        member val FrameLength = 0 with get, set
        member _.Alive = true
        member _.Attach(_) = ()
        member _.Backlog with set (_: int) = ()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member p.TryBeginFrame(frame: byref<Span<byte>>) =
            frame <- Span(buf, 0, (p :> IPath).FrameLength)
            true
        member p.CommitFrame() = p.Frames <- p.Frames + 1
        member _.Close() = ()

[<Fact>]
let ``bulk goes out in batches of at most 4 frames per path, 256 per round`` () =
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    let struct (app, local) = loopPair listener
    use app = app
    Assert.True(bridge.TryAdd local)
    let slot = bridge.Opened.NextUp -1
    app.Send(Array.create 60000 3uy) |> ignore
    for _ in 1 .. 200 do
        if session.Tx.Queued < 40 then
            bridge.Read slot
            Thread.Sleep 1
    Assert.True(session.Tx.Queued >= 40)
    let carrier = SessionCarrier(session, bridge)
    let a = CountingPath 0us
    let b = CountingPath 1us
    carrier.AddPath a
    carrier.AddPath b
    Assert.Equal(8, carrier.WriteBatch 256)
    Assert.Equal(4, a.Frames)
    Assert.Equal(4, b.Frames)
    Assert.Equal(3, carrier.WriteBatch 3)
    Assert.Equal(11, a.Frames + b.Frames)
    use poller = new Poller()
    let out0 = carrier.FramesOut
    carrier.Service poller
    let sent = carrier.FramesOut - out0
    Assert.InRange(sent, 9L, 256L)
    bridge.CloseAll()

[<Fact>]
let ``the trace is off by default, costs no allocation off, and writes its records on`` () =
    Assert.False(TraceGate.On)
    let b0 = GC.GetAllocatedBytesForCurrentThread()
    for i in 0 .. 9999 do Trace.record Trace.Seal (uint64 i) 0us 1
    Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - b0)
    let file = IO.Path.GetTempFileName()
    Trace.start file
    try
        Trace.record Trace.Send 42UL 3us 1
        Trace.flush ()
    finally
        TraceGate.On <- false
    let line = IO.File.ReadAllLines(file) |> Array.find (fun l -> l.StartsWith "2 42 3 1 ")
    Assert.True(Int64.Parse(line.Split(' ').[4]) > 0L)
    IO.File.Delete file
