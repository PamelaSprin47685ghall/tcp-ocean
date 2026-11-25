/// Serial (DisableParallelization): the trace test turns the process-wide TraceGate on, and
/// any test running beside it then records into the same arrays and can overwrite its entry.
[<Xunit.Collection("BridgeTests serial")>]
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

[<CollectionDefinition("BridgeTests serial", DisableParallelization = true)>]
type SerialBridge() = class end

/// A slow local reader: custody may leave the decoder window while the socket accepts
/// nothing, and the two trace kinds must say different things about that. This is the
/// distinction the earlier `Deliver` documentation got wrong — it claimed "delivered to the
/// local socket" while `RxDelivered` also counts the bytes parked in the reserve.
[<Fact>]
let ``backpressure advances custody but not socket writes, and the reserve drains later`` () =
    let tmp = IO.Path.GetTempFileName()
    let zero = Array.zeroCreate<byte> 64
    Trace.start tmp
    try
        // A reader that takes nothing until `open` flips, then takes everything.
        let mutable open_ = false
        let mutable accepted = 0
        let port =
            { new IFlowPort with
                member _.Open(_, _) = ()
                member _.Write(_, data) =
                    if open_ then
                        accepted <- accepted + data.Length
                        data.Length
                    else 0                      // EAGAIN: the local socket takes nothing
                member _.Finish _ = ()
                member _.Reset _ = ()
                member _.Close _ = ()
                member _.Credit _ = () }
        let store =
            { new ISourceStore with
                member _.Source(_, _) = ReadOnlySpan<byte> zero }
        let flows = FlowTable(port, store, Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
        let env (flags: StreamFlags) (offset: uint64) (len: int) : SourceEnvelope =
            { RealLength = len
              FlowSlot = 1uy
              Flags = flags
              FlowGeneration = 3u
              StreamOffset = offset }
        Assert.True(flows.OnSource(0UL, 0, env StreamFlags.Open 0UL 0))
        flows.Pump 1
        Assert.True(flows.OnSource(1UL, 0, env StreamFlags.None 0UL 16))
        flows.Pump 1
        let f = flows.[1]
        // The port took nothing, so custody must have advanced anyway (the reserve absorbed it)
        // while the port-accepted counter stayed at zero. `RxStaged` counts bytes the PORT took
        // responsibility for — for `FlowBridge` that means copying into its stage buffer, not a
        // kernel send — so if `Deliver` were truly "socket delivery", these two would have to
        // agree and this assertion would fail.
        Assert.True(f.RxDelivered >= 16UL, sprintf "custody %d did not advance" f.RxDelivered)
        Assert.Equal(0UL, f.RxStaged)
        Assert.True(f.Reserve.Count > 0, "the reserve did not absorb the unwritable bytes")
        Trace.flush ()
        let before = IO.File.ReadAllLines tmp |> Array.filter (fun l -> l.StartsWith "9 ")
        Assert.Equal(0, before.Length)          // no Stage record while the port refused
        // Reader recovers: drainReserve hands the reserve over and `Stage` must now appear.
        open_ <- true
        flows.Pump 1
        Assert.True(f.RxStaged > 0UL, "the port took nothing even after it reopened")
        Assert.Equal(0, f.Reserve.Count)
        Assert.Equal(uint64 accepted, f.RxStaged)
        Trace.flush ()
    finally
        TraceGate.On <- false
    let lines = IO.File.ReadAllLines tmp
    IO.File.Delete tmp
    let staged = lines |> Array.filter (fun l -> l.StartsWith "9 ")
    Assert.True(staged.Length >= 1, "no Stage record after the port reopened")
    let last = staged.[staged.Length - 1].Split(' ')
    Assert.Equal("9", last.[0])
    Assert.Equal("1", last.[2])                 // flow slot
    Assert.Equal("3", last.[3])                 // generation
    Assert.True(Int64.Parse last.[1] > 0L)      // cumulative port-accepted bytes
    // Explicitly uncovered here: `Trace.Sent` (kind 10) is written only by
    // `FlowBridge.flushSlot`, which needs a real socket; this fixture uses a fake IFlowPort,
    // so no `10 ` record can exist and none is asserted. The distinction it encodes
    // (staged vs handed to the kernel) is therefore tested nowhere yet.

/// A real loopback socket: staging is not kernel acceptance, and `Sent` marks the moment the
/// kernel took the bytes. Uses a genuine FlowBridge + listener rather than a fake port, because
/// the distinction being asserted (`IFlowPort.Write` copies into a 64 KiB stage; `socket.Send`
/// happens in `flushSlot`) exists only inside that implementation.
[<Fact>]
let ``a real loopback send is traced only when the kernel accepts the staged bytes`` () =
    let tmp = IO.Path.GetTempFileName()
    use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
    listener.Listen 1
    let port = (listener.LocalEndPoint :?> IPEndPoint).Port
    // The peer reads everything that arrives, so a send can complete.
    //
    // The peer's byte count is written by the reader thread and read by this one, so it is a
    // shared cell updated with Interlocked, never a bare `ref`: a plain read while another
    // thread writes is a data race, and the compiler/HW may reorder or tear it.
    let received = Array.zeroCreate<int> 1
    use readerDone = new ManualResetEventSlim(false)
    let reader =
        Thread(fun () ->
            try
                use peer = listener.Accept()
                peer.ReceiveTimeout <- 4000
                let buf = Array.zeroCreate<byte> 4096
                let mutable go = true
                while go do
                    match peer.Receive buf with
                    | n when n > 0 -> Interlocked.Add(&received.[0], n) |> ignore
                    | _ -> go <- false
            with _ -> ()
            readerDone.Set() |> ignore)
    reader.IsBackground <- true
    reader.Start()
    // Wait until the peer has actually delivered `expected` bytes, with a bound. Asserting
    // `received > 0` immediately after Flush assumed the reader thread had already been
    // scheduled and had returned from Accept+Receive; on a loaded CI box that is a flaky
    // failure, not a product bug. Polling keeps the check real (the bytes must arrive) while
    // removing the scheduling assumption.
    let waitFor (expected: int) (timeoutMs: int) =
        let sw = Diagnostics.Stopwatch.StartNew()
        while Volatile.Read(&received.[0]) < expected && sw.ElapsedMilliseconds < int64 timeoutMs do
            Thread.Sleep 5
        Volatile.Read(&received.[0])
    Trace.start tmp
    try
        let bridge = FlowBridge(ValueSome(IPEndPoint(IPAddress.Loopback, port)))
        // Production order, reproduced: the session exists before any slot opens, because
        // `rearm` asks it for `FlowSending`/`FlowRoom` — readability is a credit decision, not
        // a socket-only one. The `Pair` harness supplies a real `Session` without any sockets;
        // the bridge keeps its own loopback socket, so nothing here is mocked.
        let pair = Pair(SessionConfig.defaults, 1, 0.0, 1)
        bridge.Attach pair.Server
        let p = bridge :> IFlowPort
        p.Open(0, 1u)
        // A loopback connect normally completes immediately; if the kernel defers it, Write
        // reports 0 and this test would be measuring the connect, not the send.
        let payload = Array.init 32 (fun i -> byte (i + 1))
        let staged = p.Write(0, ReadOnlySpan<byte> payload)
        Assert.Equal(32, staged)
        Trace.flush ()
        let afterStage = IO.File.ReadAllLines tmp |> Array.filter (fun l -> l.StartsWith "10 ")
        Assert.Equal(0, afterStage.Length)          // nothing has reached the kernel yet
        Assert.Equal(0, Volatile.Read(&received.[0]))   // and the peer has read nothing
        bridge.Flush ()
        // Deterministic: flushSlot sends the whole 32-byte stage in one call and records
        // `markSent slot len`, so the cumulative kernel-accepted total is exactly the payload
        // length. Comparing it with the racy peer counter coupled the assertion to scheduler
        // timing; the peer count is checked separately, by the bounded wait below.
        Assert.Equal(32, waitFor 32 2000)
        Trace.flush ()
        let afterSent = IO.File.ReadAllLines tmp |> Array.filter (fun l -> l.StartsWith "10 ")
        Assert.True(afterSent.Length >= 1, "no Sent record after a real socket.Send")
        let last = afterSent.[afterSent.Length - 1].Split(' ')
        Assert.Equal("10", last.[0])
        Assert.Equal("32", last.[1])                    // cumulative == bytes the kernel took
        Assert.Equal("0", last.[2])                     // flow slot
        Assert.Equal("-1", last.[3])                    // generation not tracked at this layer
    finally
        TraceGate.On <- false
        readerDone.Wait 1000 |> ignore
        listener.Dispose()
    IO.File.Delete tmp

/// The step↔flow link. `Bind` names the step whose bytes are leaving custody, written
/// immediately before the `Deliver` that counts them, so a reader can attribute a byte range
/// to a step without another field, another clock, or a cross-machine comparison.
[<Fact>]
let ``a delivered source names the step that produced it`` () =
    let tmp = IO.Path.GetTempFileName()
    let zero = Array.zeroCreate<byte> 64
    Trace.start tmp
    try
        let mutable accepted = 0
        let port =
            { new IFlowPort with
                member _.Open(_, _) = ()
                member _.Write(_, data) =
                    accepted <- accepted + data.Length
                    data.Length
                member _.Finish _ = ()
                member _.Reset _ = ()
                member _.Close _ = ()
                member _.Credit _ = () }
        let store =
            { new ISourceStore with
                member _.Source(_, _) = ReadOnlySpan<byte> zero }
        let flows = FlowTable(port, store, Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
        let env (flags: StreamFlags) (offset: uint64) (len: int) : SourceEnvelope =
            { RealLength = len
              FlowSlot = 1uy
              Flags = flags
              FlowGeneration = 3u
              StreamOffset = offset }
        // OPEN carries no bytes, so it must bind nothing; then two REAL sources from two
        // different steps, so a constant step id in the record could not pass this test.
        Assert.True(flows.OnSource(0UL, 0, env StreamFlags.Open 0UL 0))
        flows.Pump 1
        Assert.True(flows.OnSource(7UL, 0, env StreamFlags.None 0UL 16))
        flows.Pump 1
        Assert.True(flows.OnSource(9UL, 0, env StreamFlags.None 16UL 16))
        flows.Pump 1
        Trace.flush ()
    finally
        TraceGate.On <- false
    let lines = IO.File.ReadAllLines tmp |> Array.map (fun l -> l.Split(' '))
    IO.File.Delete tmp
    let binds = lines |> Array.filter (fun p -> p.[0] = "11")
    let delivers = lines |> Array.filter (fun p -> p.[0] = "8")
    Assert.Equal(2, binds.Length)                 // one per delivered source, none for OPEN
    Assert.Equal("7", binds.[0].[1])              // step id, exact
    Assert.Equal("1", binds.[0].[2])              // flow slot
    Assert.Equal("3", binds.[0].[3])              // generation
    Assert.Equal("9", binds.[1].[1])              // the second source's step, not a repeat
    Assert.True(delivers.Length >= 2, sprintf "only %d Deliver records" delivers.Length)
    // Positional pairing is the whole contract: a Bind must sit BEFORE the Deliver whose byte
    // range it explains, otherwise every attribution would be one record late.
    let firstIndexOf kind = lines |> Array.findIndex (fun p -> p.[0] = kind)
    Assert.True(firstIndexOf "11" < firstIndexOf "8", "Bind must precede the Deliver it explains")
    Assert.True(Int64.Parse(lines.[firstIndexOf "11"].[4]) > 0L)   // monotonic timestamp present

/// A flow's ordered delivery frontier, which the session `Ack` record cannot express: a flow
/// releases bytes by stream offset, and one step carries bytes belonging to several flows.
/// Before this record existed, a probe delayed behind an earlier gap in its own flow was
/// indistinguishable from a session-wide stall.
[<Fact>]
let ``a flow's ordered delivery is traced with its slot, generation and stream offset`` () =
    let tmp = IO.Path.GetTempFileName()
    let zero = Array.zeroCreate<byte> 64
    let mutable expected = 0UL
    Trace.start tmp                                   // also turns TraceGate.On on
    try
        let mutable accepted = 0
        let port =
            { new IFlowPort with
                member _.Open(_, _) = ()
                member _.Write(_, data) =
                    accepted <- accepted + data.Length
                    data.Length
                member _.Finish _ = ()
                member _.Reset _ = ()
                member _.Close _ = ()
                member _.Credit _ = () }
        let store =
            { new ISourceStore with
                member _.Source(_, _) = ReadOnlySpan<byte> zero }
        let flows = FlowTable(port, store, Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
        let env (flags: StreamFlags) (offset: uint64) : SourceEnvelope =
            { RealLength = 8
              FlowSlot = 2uy
              Flags = flags
              FlowGeneration = 5u
              StreamOffset = offset }
        Assert.True(flows.OnSource(0UL, 0, env StreamFlags.Open 0UL))
        flows.Pump 2
        Assert.True(flows.OnSource(0UL, 1, env StreamFlags.None 8UL))
        flows.Pump 2
        expected <- flows.[2].RxDelivered
        Assert.True(expected > 0UL, "the flow delivered nothing, so the record would be vacuous")
        Trace.flush ()
    finally
        TraceGate.On <- false
    let lines = IO.File.ReadAllLines tmp
    IO.File.Delete tmp
    let delivered = lines |> Array.filter (fun l -> l.StartsWith "8 ")
    Assert.True(delivered.Length >= 1, "no Deliver record was written")
    let last = delivered.[delivered.Length - 1].Split(' ')
    Assert.Equal("8", last.[0])                        // kind
    Assert.Equal(string expected, last.[1])            // cumulative bytes == the flow's RxDelivered
    Assert.Equal("2", last.[2])                        // flow slot
    Assert.Equal("5", last.[3])                        // generation
    Assert.True(Int64.Parse last.[4] > 0L)             // monotonic timestamp

/// True when Tbt.Carrier was compiled with optimizations. A Debug build allocates in the
/// round loop (152 bytes a round: F# Debug codegen), so only the optimized build can hold the
/// zero-allocation claim; both must still be steady (the same every round, nothing growing).
let private optimized =
    typeof<Poller>.Assembly.GetCustomAttributes(typeof<Diagnostics.DebuggableAttribute>, false)
    |> Array.forall (fun a -> not (a :?> Diagnostics.DebuggableAttribute).IsJITOptimizerDisabled)

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
    // The round loop allocates nothing once warm (optimized build); every round the same.
    let perRound = Array.zeroCreate<int64> 100
    for r in 0 .. perRound.Length - 1 do
        let b0 = GC.GetAllocatedBytesForCurrentThread()
        carrier.Watch poller
        poller.Wait 0 |> ignore
        carrier.Service poller
        perRound.[r] <- GC.GetAllocatedBytesForCurrentThread() - b0
    Assert.All(perRound, fun b -> Assert.Equal(perRound.[0], b))
    if optimized then Assert.Equal(0L, perRound.[0])
    for a in apps do a.Dispose()
    bridge.CloseAll()

/// A path that takes every frame and counts them.
type private CountingPath(id: uint16, ?unsent: int) =
    let buf = Array.zeroCreate<byte> 2048
    let mutable closes = 0
    member val Frames = 0 with get, set
    member val Unsent = defaultArg unsent 0 with get, set
    /// How many times `Close` ran. A path posted while the carrier closes must land on exactly 1:
    /// 0 means the op was dropped without anyone closing it (socket leaked), 2 means two drainers
    /// both claimed it.
    member _.Closes = System.Threading.Volatile.Read(&closes)
    interface IPath with
        member val PathId = id with get, set
        member val FrameLength = 0 with get, set
        member _.Alive = true
        member _.Attach(_) = ()
        member _.Backlog with set (_: int) = ()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member p.UnsentBytes = p.Unsent
        member p.TryBeginFrame(frame: byref<Span<byte>>) =
            frame <- Span(buf, 0, (p :> IPath).FrameLength)
            true
        member p.CommitFrame() = p.Frames <- p.Frames + 1
        member _.Close() = System.Threading.Interlocked.Increment(&closes) |> ignore

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
    // The delivery-frontier record must be written while the gate is still on: this fact
    // turned the gate off in `finally` before asking for the Ack line, so the record was a
    // no-op and the lookup threw. Keep every record/flush inside the enabled window.
    TraceGate.On <- true
    Trace.record Trace.Ack 900UL 0us 7
    Trace.flush ()
    TraceGate.On <- false
    // One line per call that retired steps: `step` = the new frontier, `aux` = how many it
    // crossed. It separates "the probe's step never decoded" from "it decoded but stayed
    // behind an earlier hole".
    let ack = IO.File.ReadAllLines(file) |> Array.find (fun l -> l.StartsWith "7 900 0 7 ")
    Assert.True(Int64.Parse(ack.Split(' ').[4]) > 0L)
    IO.File.Delete file

[<Fact>]
let ``a real completing cell writes exactly one frontier record, and a non-completing one writes none`` () =
    // This fact belongs beside the other trace fact rather than next to the RxHalf harness:
    // TraceGate is process-wide state with one global ring, and this class is in the
    // "BridgeTests serial" collection. Any parallel fact would record into the same arrays
    // and could overwrite these entries, which would make the assertion flaky instead of
    // wrong — a worse failure, because it would pass sometimes.
    //
    // What it pins: RxHalf.traceFrontier must fire when a cell COMPLETES a step (a probe's
    // own DATA cell is exactly that case) and must not fire otherwise. The earlier version
    // snapshotted the frontier on entry to drainKnown, which runs after the decoder already
    // advanced it for that very cell, so the one advance that matters was never recorded.
    let symbol = 64
    let file = IO.Path.GetTempFileName()
    let acks () =
        IO.File.ReadAllLines file
        |> Array.filter (fun l -> l.Split(' ').[0] = "7")
    let dec = Tbt.Codec.Decoder(11UL, symbol, 64, 256)
    let opener =
        Tbt.Crypto.CellOpener(Tbt.Contracts.Direction.Up, symbol, Tbt.Crypto.KeyTable(), Tbt.Crypto.NoHeaderMask())
    let rx = RxHalf(symbol, CodeField.Gf16, dec, opener, 64, 4)
    let zero = Array.zeroCreate<byte> symbol
    // Sink-only flow table: these cells carry zero envelopes, and drainKnown needs a table.
    let port =
        { new IFlowPort with
            member _.Open(_, _) = ()
            member _.Write(_, data) = data.Length
            member _.Finish _ = ()
            member _.Reset _ = ()
            member _.Close _ = ()
            member _.Credit _ = () }
    let store =
        { new ISourceStore with
            member _.Source(_, _) = ReadOnlySpan<byte> zero }
    rx.Attach(FlowTable(port, store, Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults))
    let feed slot k t (now: int64) =
        let m : Tbt.Wire.Meta =
            { Type = CellType.Data
              Flags = MetaFlags.None
              Slot = byte slot
              StepUsed = byte k
              Aux = 0us
              RxWinGrant = 0us
              StepId = t
              AckStepLo = 0UL
              CarrierWm = 0UL
              KHist = 0u
              ErasureQ16 = 0us }
        rx.OnCoded(m, ReadOnlySpan<byte> zero, 0us, now)
    Trace.start file
    try
        // Three of four sources: the step cannot complete, so the frontier stays at 0.
        for slot in 0 .. 2 do
            feed slot 4 0UL (int64 slot * 1_000_000L)
        Trace.flush ()
        Assert.Empty(acks ())
        // The fourth source completes step 0: frontier 0 -> 1, one record, aux = 1 step.
        feed 3 4 0UL 3_000_000L
        Trace.flush ()
        let first = acks ()
        Assert.Equal(1, first.Length)
        let parts = first.[0].Split(' ')
        Assert.Equal("1", parts.[1])
        Assert.Equal("1", parts.[3])
        // A cell that completes nothing must not add a second record.
        feed 0 4 1UL 4_000_000L
        Trace.flush ()
        Assert.Equal(1, (acks ()).Length)
    finally
        TraceGate.On <- false
        IO.File.Delete file

[<Fact>]
let ``write gate: first pass admits up to 4 KiB, second up to 256 KiB, unknown only on the second`` () =
    Assert.True(WriteGate.admits 1 0)
    Assert.True(WriteGate.admits 1 1)
    Assert.True(WriteGate.admits 1 4096)
    Assert.False(WriteGate.admits 1 4097)
    Assert.False(WriteGate.admits 1 -1)
    Assert.True(WriteGate.admits 2 0)
    Assert.True(WriteGate.admits 2 1)
    Assert.True(WriteGate.admits 2 4097)
    Assert.True(WriteGate.admits 2 262144)
    Assert.False(WriteGate.admits 2 262145)
    Assert.True(WriteGate.admits 2 -1)

[<Fact>]
let ``write gate: a session pass-2 limit is honoured and pass 1 stays at 4 KiB`` () =
    Assert.True(WriteGate.admitsWith 32768 1 4096)
    Assert.False(WriteGate.admitsWith 32768 1 4097)
    Assert.True(WriteGate.admitsWith 32768 2 32768)
    Assert.False(WriteGate.admitsWith 32768 2 32769)
    Assert.True(WriteGate.admitsWith 32768 2 -1)
    Assert.True(WriteGate.admitsWith 262144 2 262144)

[<Fact>]
let ``send-side supply: notsent lowat and tx slots clamp to the product range`` () =
    // Defaults stay the product values; the switches only widen the experiment range.
    Assert.Equal(SocketTuning.NotSentLimit, SocketTuning.notsentLowatFor SocketTuning.NotSentLimit)
    Assert.Equal(4096, SocketTuning.notsentLowatFor 1)
    Assert.Equal(262144, SocketTuning.notsentLowatFor 999999)
    Assert.Equal(32768, SocketTuning.notsentLowatFor 32768)
    Assert.Equal(16, SocketTuning.txSlotsFor 1)
    Assert.Equal(32, SocketTuning.txSlotsFor 32)
    // The ring is the pre-send queue, so its size is the per-path in-flight ceiling:
    // 16 slots at 2 KiB is 32 KiB, i.e. 17 Mbit/s on a 341 ms path, which is what the live
    // 24 %-loss run delivered while the carrier was saturated. The range now reaches 256.
    Assert.Equal(256, SocketTuning.txSlotsFor 999)

[<Fact>]
let ``user timeout: zero disables and requests clamp to the supported range`` () =
    // Zero is the rollback switch: the option is never set, which is the old behaviour.
    Assert.Equal(0, SocketTuning.userTimeoutMsFor 0)
    Assert.Equal(0, SocketTuning.userTimeoutMsFor -1)
    Assert.Equal(1000, SocketTuning.userTimeoutMsFor 1)
    Assert.Equal(1000, SocketTuning.userTimeoutMsFor 999)
    Assert.Equal(SocketTuning.UserTimeoutMs, SocketTuning.userTimeoutMsFor SocketTuning.UserTimeoutMs)
    Assert.Equal(300000, SocketTuning.userTimeoutMsFor 300001)
    // The product default must stay the value the design names.
    Assert.Equal(30000, SocketTuning.UserTimeoutMs)

[<Fact>]
let ``write batch prefers empty paths and only then those with notsent at most 256 KiB`` () =
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
    let empty = CountingPath(0us, unsent = 0)
    let queued = CountingPath(1us, unsent = 8000)
    let full = CountingPath(2us, unsent = 300000)
    carrier.AddPath empty
    carrier.AddPath queued
    carrier.AddPath full
    let n = carrier.WriteBatch 8
    Assert.True(n > 0)
    Assert.True(empty.Frames > 0)
    // Pass 2 must run whenever pass 1 gated a path, not only when pass 1 made
    // no progress at all. With 256 paths one idle path set `progress` and the
    // other 255 were skipped every round: the live link sent about six frames
    // per Service iteration (3 Mbit/s) while 50 MB sat queued.
    Assert.True(queued.Frames > 0)
    Assert.Equal(0, full.Frames)
    // No empty path: the second pass may use a path with notsent ≤ 16 KiB, never one above it.
    let onlyQueued = SessionCarrier(session, bridge)
    let mid = CountingPath(0us, unsent = 8000)
    let over = CountingPath(1us, unsent = 300000)
    onlyQueued.AddPath mid
    onlyQueued.AddPath over
    let n2 = onlyQueued.WriteBatch 8
    Assert.True(n2 > 0)
    Assert.True(mid.Frames > 0)
    Assert.Equal(0, over.Frames)
    bridge.CloseAll()

[<Fact>]
let ``segmented byte queue preserves bytes across chunks and reuses after clear`` () =
    // The reserve of one flow can reach 8 MiB. It must never be one array that big: chunks of
    // 64 KiB stay under the LOH threshold, and a drained chunk goes back to the shared pool.
    let cap = 12 <<< 20
    let q = ByteQueue cap
    let total = 10 <<< 20
    let src = Array.init total (fun i -> byte ((i * 31 + 7) &&& 0xFF))
    let mutable written = 0
    let mutable checkedCount = 0
    let mutable seed = 12345
    while written < total do
        seed <- (seed * 1103515245 + 12345) &&& 0x7FFFFFFF
        let chunk = min (total - written) ((seed % 70000) + 1)
        Assert.True(chunk <= q.Free, "free must cover the write")
        q.Write(ReadOnlySpan<byte>(src, written, chunk))
        written <- written + chunk
        Assert.Equal(written - checkedCount, q.Count)
        // Drain sometimes, so the head moves and empty chunks are returned.
        if seed % 3 = 0 && q.Count > 0 then
            let seg = q.Peek()
            let n = min (min q.Count ((seed % 5000) + 1)) seg.Length
            Assert.True(seg.Slice(0, n).SequenceEqual(ReadOnlySpan<byte>(src, checkedCount, n)))
            q.Skip n
            checkedCount <- checkedCount + n
    while q.Count > 0 do
        let seg = q.Peek()
        Assert.True(seg.SequenceEqual(ReadOnlySpan<byte>(src, checkedCount, seg.Length)))
        q.Skip seg.Length
        checkedCount <- checkedCount + seg.Length
    Assert.Equal(total, checkedCount)
    Assert.Equal(0, q.Count)
    Assert.Equal(cap, q.Free)
    // A cleared queue writes again from its first chunk.
    q.Clear()
    Assert.Equal(0, q.Count)
    Assert.Equal(cap, q.Free)
    q.Write(ReadOnlySpan<byte>(src, 0, 200))
    Assert.Equal(200, q.Count)
    Assert.True(q.Peek().Slice(0, 200).SequenceEqual(ReadOnlySpan<byte>(src, 0, 200)))
    q.Clear()
    Assert.Equal(0, q.Count)

[<Fact>]
let ``a path posted from another thread wakes the session loop instead of waiting for a socket`` () =
    // The coordinator's only armed descriptor can be the receive-channel wake fd. A posted path
    // is not a frame, so without an explicit wake the session thread stays inside epoll_wait(-1)
    // and the queued JOIN waits for an unrelated socket event (or, with --stats-s, up to 1 s).
    // This pins the wake contract itself: post → the hook fires → the owner thread applies.
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    let struct (app, local) = loopPair listener
    use app = app
    Assert.True(bridge.TryAdd local)
    use channel = new RxChannel()
    let group = new AsyncPollerGroup(1)
    let carrier = SessionCarrier(session, bridge, rxChannel = channel, asyncIo = group)
    let mutable wakes = 0
    carrier.WakePathOps <- Some(fun () -> wakes <- wakes + 1)
    Assert.Equal(0, carrier.PathCount)
    carrier.AddPath(CountingPath(9us, unsent = 0))
    // Posted, not applied: only the thread that owns `paths` may touch them.
    Assert.Equal(0, carrier.PathCount)
    Assert.Equal(1, carrier.PathOpsPending)
    // The wake is the fix; without it this counter would stay 0 and the wait would block.
    Assert.Equal(1, wakes)
    // A bounded wait remains only as a safety net for a post that raced the wait itself.
    Assert.Equal(1000, carrier.WaitUs())
    use poller = new Poller()
    carrier.Watch poller
    Assert.Equal(1, carrier.PathCount)
    Assert.Equal(0, carrier.PathOpsPending)
    Assert.Equal(-1, carrier.WaitUs())
    bridge.CloseAll()

[<Fact>]
let ``a real poller wait on the wake fd returns when another thread posts a path`` () =
    // The discriminator for the wake contract: `poller.Wait(-1)` is an infinite epoll wait, so if
    // the post did not wake it the worker thread never returns and this test fails on the join —
    // it cannot pass by luck. The wake fd is level-triggered, so the assertion also holds when the
    // post happens to land just before the worker enters the wait.
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    use channel = new RxChannel()
    use poller = new Poller()
    let group = new AsyncPollerGroup(1)
    let carrier = SessionCarrier(session, bridge, rxChannel = channel, asyncIo = group)
    carrier.WakePathOps <- Some(fun () -> channel.Wake())
    Assert.Equal(-1, carrier.WaitUs()) // empty and no op pending: a genuine infinite wait
    poller.WatchFd channel.WakeFd
    use entered = new ManualResetEventSlim(false)
    use returned = new ManualResetEventSlim(false)
    let worker =
        Thread(fun () ->
            entered.Set()
            poller.Wait(-1) |> ignore
            channel.ClearWake()
            returned.Set())
    worker.IsBackground <- true
    worker.Start()
    Assert.True(entered.Wait 5000, "worker did not start")
    carrier.AddPath(CountingPath(7us))
    Assert.True(returned.Wait 5000, "AddPath did not wake a thread blocked in poller.Wait")
    Assert.True(worker.Join 5000, "worker stuck after the wake")
    carrier.Watch poller
    Assert.Equal(1, carrier.PathCount)
    bridge.CloseAll()

[<Fact>]
let ``a path posted while the carrier closes is still closed exactly once`` () =
    // The hazard: PostPathOp increments, then Close drains and exits, then the enqueue lands.
    // Nothing applies ops after Close, so without the post-enqueue claim the IPath is never
    // closed (socket and buffers leak) and HasPath keeps reporting it joined. The invariant must
    // hold for every interleaving, so this repeats the race rather than relying on timing.
    let mutable bad = 0
    for _ in 1 .. 10 do
        let struct (session, bridge) = clientOnBridge ()
        use channel = new RxChannel()
        let group = new AsyncPollerGroup(1)
        let carrier = SessionCarrier(session, bridge, rxChannel = channel, asyncIo = group)
        carrier.WakePathOps <- Some(fun () -> channel.Wake())
        use go = new ManualResetEventSlim(false)
        let paths = [| for i in 0 .. 7 -> CountingPath(uint16 i) |]
        let posters =
            [| for p in paths ->
                 let t = Thread(fun () -> go.Wait(); carrier.AddPath p)
                 t.IsBackground <- true
                 t.Start()
                 t |]
        go.Set()
        carrier.Close()
        for t in posters do Assert.True(t.Join 5000, "poster stuck")
        // And one strictly after Close, which must also be claimed by its own poster.
        let late = CountingPath 99us
        carrier.AddPath late
        for p in Array.append paths [| late |] do
            if p.Closes <> 1 then bad <- bad + 1
        Assert.Equal(0, carrier.PathCount)
    Assert.Equal(0, bad)

[<Fact>]
let ``reading the path set while another thread adds and removes never faults`` () =
    // The reader is the failure detector: it indexes and enumerates the set the way
    // `writeBatch`/`Watch`/`Describe` do, while a second thread adds and removes paths. With a
    // mutable List this is undefined behaviour (shifted indices, invalidated enumerators); with
    // the published snapshot every read sees one complete set, so the only acceptable outcome is
    // a clean finish. No assertion beyond "it completed" is needed: an index out of range, a
    // null slot, or an InvalidOperationException fails the test.
    let struct (session, bridge) = clientOnBridge ()
    use channel = new RxChannel()
    let group = new AsyncPollerGroup(1)
    let carrier = SessionCarrier(session, bridge, rxChannel = channel, asyncIo = group)
    carrier.WakePathOps <- Some(fun () -> channel.Wake())
    use stop = new ManualResetEventSlim(false)
    let reader =
        Thread(fun () ->
            while not stop.IsSet do
                let n = carrier.PathCount
                let mutable total = 0
                for p in carrier.Paths do
                    total <- total + int p.PathId
                Assert.Equal(n, carrier.Paths.Count)
                ignore total)
    reader.IsBackground <- true
    reader.Start()
    for round in 0 .. 300 do
        let p = CountingPath(uint16 (round % 64))
        carrier.AddPath p
        carrier.RemovePath(uint16 (round % 64))
    stop.Set()
    Assert.True(reader.Join 10000, "reader did not finish")
    carrier.Close()
    Assert.Equal(0, carrier.PathCount)
    bridge.CloseAll()

[<Fact>]
let ``a path-set snapshot carries the version of that same instant`` () =
    // The previous ranking code took the array first and read a version kept elsewhere
    // afterwards. A replacement landing in between made the snapshot pre-change and the version
    // post-change, so the batch's `rankVersion = Version` test passed while `rankSnap` still
    // pointed at the removed, already-closed path — frames went to a dead socket.
    //
    // The contract that rules that out: array and version come from ONE critical section, and any
    // mutation makes the version strictly larger.
    let set = PathSet()
    let p1 = CountingPath 1us
    let p2 = CountingPath 2us
    set.Add p1
    let arr0, v0 = set.SnapshotWithVersion()
    Assert.Equal(1, arr0.Length)
    Assert.True(obj.ReferenceEquals(arr0.[0], p1))
    Assert.Equal(set.Version, v0)

    // Same-count replacement: this is the case a length check cannot see. One leaves, one joins.
    match set.TakeIf(fun p -> obj.ReferenceEquals(p, p1)) with
    | Some v -> Assert.True(obj.ReferenceEquals(v, p1))
    | None -> failwith "TakeIf did not return the path it was asked for"
    set.Add p2
    Assert.Equal(1, set.Count)
    Assert.True(set.Version > v0, "a same-count replacement must still advance the version")
    // The older snapshot must not have changed under the reader.
    Assert.Equal(1, arr0.Length)
    Assert.True(obj.ReferenceEquals(arr0.[0], p1), "a published snapshot must stay immutable")

    // ... and a fresh snapshot is the pair for the current instant.
    let arr1, v1 = set.SnapshotWithVersion()
    Assert.Equal(set.Version, v1)
    Assert.True(v1 > v0)
    Assert.True(obj.ReferenceEquals(arr1.[0], p2))

[<Fact>]
let ``taking a path out of the set is atomic, so only one caller can own it`` () =
    // `FindIndex` then `.[i]` then `RemoveAt i` are three snapshots: a concurrent add/remove can
    // delist a *different* path than the one about to be closed, which leaks that path's socket
    // and closes a path that is still listed. `TakeIf` decides and removes under one lock.
    let set = PathSet()
    let p = CountingPath 7us
    let other = CountingPath 8us
    set.Add p
    set.Add other
    // `p` was added first, so it sits at index 0 and `other` at 1.
    Assert.Equal(0, set.FindIndex(fun x -> obj.ReferenceEquals(x, p)))
    match set.TakeIf(fun x -> obj.ReferenceEquals(x, p)) with
    | Some v -> Assert.True(obj.ReferenceEquals(v, p))
    | None -> failwith "TakeIf found nothing for a path that is in the set"
    match set.TakeIf(fun x -> obj.ReferenceEquals(x, p)) with
    | Some _ -> failwith "the same path was handed out twice — two owners would close it twice"
    | None -> ()
    Assert.Equal(1, set.Count)
    // The untouched neighbour is still there — the removal did not take a different entry.
    Assert.True(obj.ReferenceEquals(set.ToArray().[0], other))

[<Fact>]
let ``draining the set leaves every path to exactly one owner`` () =
    let set = PathSet()
    let paths = [| for i in 0 .. 9 -> CountingPath(uint16 i) |]
    for p in paths do set.Add p
    Assert.Equal(10, set.Count)
    let taken = set.DrainAll()
    Assert.Equal(10, taken.Length)
    Assert.Equal(0, set.Count)
    // Same members, no duplicates and none lost: a double take would close one socket twice, a
    // lost one would never be closed at all.
    for p in paths do
        Assert.Equal(1, taken |> Array.filter (fun t -> obj.ReferenceEquals(t, p)) |> Array.length)
    // Draining again yields nothing: ownership was a single event.
    Assert.Empty(set.DrainAll())

[<Fact>]
let ``a reader sees a whole set while another thread replaces paths`` () =
    // Every accepted snapshot must be internally consistent: the array a reader got never changes
    // length or slot contents, and no slot is ever null. A `List` shared across threads fails this
    // as shifted indices or an invalidated enumerator.
    let set = PathSet()
    let seed = [| for i in 0 .. 7 -> CountingPath(uint16 i) |]
    for p in seed do set.Add p
    use stop = new ManualResetEventSlim(false)
    let reader =
        Thread(fun () ->
            while not stop.IsSet do
                let arr, v = set.SnapshotWithVersion()
                let n = arr.Length
                for i in 0 .. n - 1 do
                    // A null slot would mean a slot existed before its value was published.
                    Assert.NotNull arr.[i]
                // The version of a later read is never older than this snapshot's, and the array
                // this reader holds never changes shape: both follow from publishing one
                // immutable array under the same lock that bumps the version.
                Assert.True(set.Version >= v, "the version went backwards")
                Assert.Equal(n, arr.Length))
    reader.IsBackground <- true
    reader.Start()
    for round in 0 .. 200 do
        match set.TakeIf(fun p -> p.PathId = uint16 (round % 8)) with
        | Some v -> v.Close()
        | None -> ()
        set.Add(CountingPath(uint16 (round % 8)))
    stop.Set()
    Assert.True(reader.Join 20000, "reader did not finish")

[<Fact>]
let ``a close from another thread is a request the owner performs exactly once`` () =
    // `AsyncSession.Stop` may run `carrier.Close()` on the Stop thread when its join times out,
    // while the coordinator is still inside a round holding a span into a path's slab. Returning
    // that slab to `ArrayPool` there lets another path rent the same bytes: a torn frame,
    // attributed to the wrong path. So the close belongs to the thread that owns `paths`.
    //
    // This drives the contract with a real second thread: the foreign close must only mark the
    // carrier, and the owner's next round must be the thing that closes the path — once.
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    use channel = new RxChannel()
    use poller = new Poller()
    let group = new AsyncPollerGroup(1)
    let carrier = SessionCarrier(session, bridge, rxChannel = channel, asyncIo = group)
    carrier.WakePathOps <- Some(fun () -> channel.Wake())
    let p = CountingPath 3us
    carrier.AddPath p
    carrier.Watch poller                        // this thread becomes the owner; applies the add
    Assert.Equal(1, carrier.PathCount)
    Assert.Equal(0, p.Closes)

    let foreign = Thread(fun () -> carrier.Close())
    foreign.IsBackground <- true
    foreign.Start()
    Assert.True(foreign.Join 5000, "foreign close did not return")
    // Marked, not performed: the path still owns its buffers.
    Assert.True carrier.CloseRequested
    Assert.Equal(1, carrier.PathCount)
    Assert.Equal(0, p.Closes)

    // The owner performs it at the top of its next round.
    carrier.Watch poller
    Assert.Equal(0, carrier.PathCount)
    Assert.Equal(1, p.Closes)

    // A third close, from either thread, must not close the path a second time.
    let again = Thread(fun () -> carrier.Close())
    again.IsBackground <- true
    again.Start()
    Assert.True(again.Join 5000)
    carrier.Watch poller
    Assert.Equal(1, p.Closes)
    bridge.CloseAll()

/// A path that reports a chosen TCP state, so the health tier is decided by the test.
/// `CountingPath` cannot do this: it is not an `IPathHealth`, so `healthOf` would see
/// `PathHealth.unknown` and every path would rank Tier 0.
type private StatePath(id: uint16, caState: byte) =
    let buf = Array.zeroCreate<byte> 2048
    let mutable closes = 0
    let mutable health = PathHealth.unknown
    do
        // Only the fields `healthTier` reads. `Retrans = 0` keeps a recovered socket out of Tier 1
        // so `caState` alone decides between 0 and 2.
        health <- { PathHealth.unknown with
                      SampledNs = 1L
                      CaState = caState
                      CwndBytes = 100_000u
                      UnackedBytes = 0u
                      Retrans = 0u
                      RetransRate = 0.0
                      MinRttUs = 40_000u
                      RttUs = 40_000u
                      Valid = true }
    member val Frames = 0 with get, set
    interface IPath with
        member val PathId = id with get, set
        member val FrameLength = 0 with get, set
        member _.Alive = true
        member _.Attach(_) = ()
        member _.Backlog with set (_: int) = ()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member _.UnsentBytes = 0
        member p.TryBeginFrame(frame: byref<Span<byte>>) =
            frame <- Span(buf, 0, (p :> IPath).FrameLength)
            true
        member p.CommitFrame() = p.Frames <- p.Frames + 1
        member _.Close() = System.Threading.Interlocked.Increment(&closes) |> ignore
    interface IPathHealth with
        member _.Health = health
        member _.RefreshHealth(_, _) = ()

[<Fact>]
let ``a stray TCP_CA_Loss verdict does not widen the active working set`` () =
    // The defect this pins: the write loop counted a tier-2 *skip* as proof that the working set
    // was saturated, so each stray verdict grew `activeTarget` by 50 %. The shrink clause needs
    // `Tx.Queued = 0`, which a session with pending data does not reach, so the set only ever
    // walked up: the growing set is what this test prevents, not a specific path count or link.
    // A tier-2 path is skipped, not evidence of saturation.
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    let struct (app, local) = loopPair listener
    use app = app
    Assert.True(bridge.TryAdd local)
    // `NextUp` is `int -> int` (first free slot from -1), not a property; call it explicitly.
    let slot = bridge.Opened.NextUp(-1)
    app.Send(Array.create 200_000 3uy) |> ignore
    for _ in 1 .. 400 do
        if session.Tx.Queued < 200 then
            bridge.Read slot
            Thread.Sleep 1
    // `Tx.Queued > 0` is the condition that actually matters: the shrink clause requires
    // `Tx.Queued = 0`, so any pending frame keeps the set from shrinking while tier-2 verdicts
    // arrive. Asking for a specific depth was the earlier mistake here — 200 000 bytes is about
    // 138 frames, so a `>= 200` precondition could never hold.
    Assert.True(session.Tx.Queued > 0, "the session must have data waiting, as in a loaded run")
    ignore (box session.Tx.Queued)
    let carrier = SessionCarrier(session, bridge)
    // 17 paths: enough to exceed the initial working set of 16. Nine report Loss, eight are
    // healthy, and the ranking puts the healthy ones first, so the set has healthy members and
    // must not be widened. The contract under test is unchanged — a stray Tier-2 verdict is not
    // evidence of saturation — only the working set's floor moved from 32 to 16.
    for i in 0 .. 8 do
        carrier.AddPath(StatePath(uint16 i, 4uy))
    for i in 9 .. 16 do
        carrier.AddPath(StatePath(uint16 i, 0uy))
    Assert.Equal(17, carrier.PathCount)
    let before = carrier.ActivePathTarget
    Assert.Equal(16, before)
    let sent = carrier.WriteBatch 256
    Assert.True(sent > 0, "healthy paths must carry the batch")
    Assert.Equal(before, carrier.ActivePathTarget)
    bridge.CloseAll()

[<Fact>]
let ``a working set with no healthy path still widens to every path`` () =
    // The positive control for the fix above: widening must still happen when it is needed —
    // here every path of the initial set reports Loss, so the scan widens to all n to find one
    // that is not. If the fix had disabled widening wholesale, this test would show it.
    use listener = listen ()
    let struct (session, bridge) = clientOnBridge ()
    let struct (app, local) = loopPair listener
    use app = app
    Assert.True(bridge.TryAdd local)
    // `NextUp` is `int -> int` (first free slot from -1), not a property; call it explicitly.
    let slot = bridge.Opened.NextUp(-1)
    app.Send(Array.create 200_000 3uy) |> ignore
    for _ in 1 .. 400 do
        if session.Tx.Queued < 200 then
            bridge.Read slot
            Thread.Sleep 1
    let carrier = SessionCarrier(session, bridge)
    for i in 0 .. 31 do
        carrier.AddPath(StatePath(uint16 i, 4uy))      // the initial working set is all Tier 2
    carrier.AddPath(StatePath(32us, 0uy))             // one healthy path outside the initial set
    Assert.Equal(33, carrier.PathCount)
    let sent = carrier.WriteBatch 256
    Assert.True(sent > 0, "the healthy path outside the initial set must be reachable")
    bridge.CloseAll()
