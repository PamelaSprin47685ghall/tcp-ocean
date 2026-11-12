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
    for a in apps do a.Dispose()
    bridge.CloseAll()
