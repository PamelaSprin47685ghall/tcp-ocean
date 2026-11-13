module Tbt.Tests.SessionTests

open System
open System.Collections.Generic
open System.IO
open Xunit
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake

[<Sealed>]
type ManualClock() =
    member val Now = 1_000_000_000L with get, set
    interface IClock with
        member c.NowNs() = c.Now

[<Sealed>]
type SeededEntropy(seed: int) =
    let rnd = Random seed
    interface IEntropy with
        member _.Fill(d) = rnd.NextBytes d

/// Local end that records delivered bytes; WriteLimit bounds one write (0 = blocked).
[<Sealed>]
type RecordingPort() =
    member val Data = Dictionary<int, MemoryStream>()
    member val Opened = List<struct (int * uint32)>()
    member val Finished = HashSet<int>()
    member val Closed = List<int>()
    member val Resets = List<int>()
    member val Credits = 0 with get, set
    member val WriteLimit = Int32.MaxValue with get, set
    /// Slots whose local reader is stalled (writes take nothing).
    member val Blocked = HashSet<int>()
    member p.Stream(slot: int) =
        match p.Data.TryGetValue slot with
        | true, s -> s
        | _ ->
            let s = new MemoryStream()
            p.Data.[slot] <- s
            s
    interface IFlowPort with
        member p.Open(slot, gen) = p.Opened.Add(struct (slot, gen))
        member p.Write(slot, data) =
            let n = if p.Blocked.Contains slot then 0 else min data.Length p.WriteLimit
            p.Stream(slot).Write(data.Slice(0, n))
            n
        member p.Finish(slot) = p.Finished.Add slot |> ignore
        member p.Reset(slot) = p.Resets.Add slot
        member p.Close(slot) = p.Closed.Add slot
        member p.Credit(slot) = p.Credits <- p.Credits + 1

let psk = Array.init 32 (fun i -> byte (i * 3 + 1))
let unixMs = 1_800_000_000_000L

/// Both ends of a session joined by in-memory paths.
type Pair(config: SessionConfig, paths: int, loss: float, seed: int) =
    let clock = ManualClock()
    let entropy = SeededEntropy seed
    let offer = ClientOffer(psk, Guid(Array.create 16 7uy), entropy, uint64 unixMs, 1448, 256)
    let accepted = (ServerOffer([| psk |], entropy, uint16 config.WindowSteps, 256)).TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    let serverParams = accepted.Value.Params
    let struct (clientParams, _) = offer.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    let clientPort = RecordingPort()
    let serverPort = RecordingPort()
    let client = Session(Role.Client, clientParams, clientPort, clock, config)
    let server = Session(Role.Server, serverParams, serverPort, clock, config)
    let rnd = Random seed
    let frame = Array.zeroCreate<byte> client.FrameLength
    let mutable dropped = 0

    let mutable blackout = false
    // One-way delay in ns (0: frames arrive in the same round).
    let mutable delay = 0L
    let inFlight = Queue<struct (int64 * Session * uint16 * byte[])>()

    let move (src: Session) (dst: Session) =
        let mutable moved = 0
        for p in 0 .. paths - 1 do
            let mutable n = 0
            while n < 4 && src.TryProduce(uint16 p, Span frame) do
                n <- n + 1
                moved <- moved + 1
                if rnd.NextDouble() >= loss && not (blackout && obj.ReferenceEquals(src, client)) then
                    if delay > 0 then inFlight.Enqueue(struct (clock.Now + delay, dst, uint16 p, Array.copy frame))
                    else dst.OnFrame(uint16 p, Span frame) |> ignore
                else dropped <- dropped + 1
        moved

    let deliver () =
        let mutable go = true
        while go && inFlight.Count > 0 do
            let struct (due, dst, p, f) = inFlight.Peek()
            if due <= clock.Now then
                inFlight.Dequeue() |> ignore
                dst.OnFrame(p, Span f) |> ignore
            else go <- false

    member _.Clock = clock
    member _.Client = client
    member _.Server = server
    member _.ClientPort = clientPort
    member _.ServerPort = serverPort
    member _.ClientParams = clientParams
    member _.ServerParams = serverParams
    member _.Dropped = dropped
    /// Drop every client cell while set.
    member _.Blackout with get () = blackout and set v = blackout <- v
    /// One-way delay of every frame, in ns.
    member _.Delay with get () = delay and set v = delay <- v

    /// Write bytes of slot into session s as sources; return bytes taken.
    member _.Feed(s: Session, slot: int, data: ReadOnlySpan<byte>, fin: bool) =
        let mutable at = 0
        let mutable app = Span<byte>.Empty
        while at < data.Length && s.FlowRoom slot > 0 && s.TryBeginSource(&app) do
            let n = min (min app.Length (data.Length - at)) (s.FlowRoom slot)
            data.Slice(at, n).CopyTo app
            at <- at + n
            s.CommitSource(slot, n, fin && at = data.Length)
        if fin && data.IsEmpty && s.TryBeginSource(&app) then s.CommitSource(slot, 0, true)
        s.Flush()
        at

    /// One round: 1 ms passes, both ends send up to 4 cells per path.
    member _.Round() =
        clock.Now <- clock.Now + 1_000_000L
        deliver ()
        client.Tick()
        server.Tick()
        move client server + move server client

[<Fact>]
let ``both ends derive the same session from the handshake`` () =
    let pair = Pair(SessionConfig.defaults, 4, 0.0, 1)
    Assert.Equal(1344, pair.ClientParams.Symbol)
    Assert.Equal(pair.ServerParams.Caps, pair.ClientParams.Caps)
    Assert.Equal<byte[]>(pair.ServerParams.Keys.UpKey 0u, pair.ClientParams.Keys.UpKey 0u)

[<Fact>]
let ``a replayed or foreign CLIENT_INIT gets no reply`` () =
    let e = SeededEntropy 2
    let offer = ClientOffer(psk, Guid.NewGuid(), e, uint64 unixMs, 1448, 256)
    let server = ServerOffer([| psk |], e, 4096us, 256)
    Assert.True(server.TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448).IsSome)
    Assert.True(server.TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448).IsNone)
    let foreign = ClientOffer(Array.zeroCreate 32, Guid.NewGuid(), e, uint64 unixMs, 1448, 256)
    Assert.True(server.TryAccept(ReadOnlySpan foreign.Frame, unixMs, 1448).IsNone)
    let late = ClientOffer(psk, Guid.NewGuid(), e, uint64 (unixMs - 121_000L), 1448, 256)
    Assert.True(server.TryAccept(ReadOnlySpan late.Frame, unixMs, 1448).IsNone)

[<Fact>]
let ``a path join is accepted only with matching fields`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 3)
    let p = pair.ClientParams
    let kPath = p.Keys.PathKey
    let join = JoinOffer(kPath, SeededEntropy 4, p.Keys.SessionId, p.Symbol, p.Caps, 256, 9us, 1u, 1448)
    let r = (tryJoin kPath (ReadOnlySpan join.Frame)).Value
    Assert.Equal(9us, r.PathId)
    let ack = joinAck kPath (SeededEntropy 5) p.Keys.SessionId p.Symbol p.Caps 256 (ReadOnlySpan join.Frame) r Tbt.Crypto.Join.JoinStatus.Accept 1448
    Assert.True(join.TryAccept(ReadOnlySpan ack).IsSome)
    let wrongS = joinAck kPath (SeededEntropy 5) p.Keys.SessionId 1280 p.Caps 256 (ReadOnlySpan join.Frame) r Tbt.Crypto.Join.JoinStatus.Accept 1448
    Assert.True(join.TryAccept(ReadOnlySpan wrongS).IsNone)
    Assert.True((tryJoin (Array.zeroCreate 32) (ReadOnlySpan join.Frame)).IsNone)

/// Echo: the server returns every byte it receives on the same flow, then FIN.
let private echo (pair: Pair) (payload: byte[]) (rounds: int) =
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let mutable sent = 0
    let mutable echoed = 0
    let mutable serverFin = false
    let mutable r = 0
    while r < rounds && not (pair.ClientPort.Closed.Contains slot && pair.ServerPort.Closed.Contains slot) do
        if sent < payload.Length || (sent = payload.Length && c.FlowSending slot) then
            sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, payload.Length - sent), true)
        let got = if pair.ServerPort.Data.ContainsKey slot then int pair.ServerPort.Data.[slot].Length else 0
        if got > echoed then
            let buf = pair.ServerPort.Data.[slot].GetBuffer()
            echoed <- echoed + pair.Feed(pair.Server, slot, ReadOnlySpan(buf, echoed, got - echoed), false)
        if not serverFin && echoed = payload.Length && pair.ServerPort.Finished.Contains slot then
            pair.Feed(pair.Server, slot, ReadOnlySpan<byte>.Empty, true) |> ignore
            serverFin <- not (pair.Server.FlowSending slot)
        pair.Round() |> ignore
        r <- r + 1
    slot

[<Theory>]
[<InlineData(0.0, 4, 6)>]
[<InlineData(0.03, 16, 6)>]
[<InlineData(0.1, 8, 1)>]
[<InlineData(0.1, 8, 2)>]
[<InlineData(0.2, 4, 3)>]
[<InlineData(0.3, 4, 4)>]
let ``echo returns every byte in order and closes both ends`` (loss: float, paths: int, seed: int) =
    let pair = Pair(SessionConfig.defaults, paths, loss, seed)
    let payload = Array.init 300_000 (fun i -> byte (i * 7 + i / 251))
    let slot = echo pair payload 20_000
    Assert.Equal<byte[]>(payload, pair.ServerPort.Data.[slot].ToArray())
    Assert.Equal<byte[]>(payload, pair.ClientPort.Data.[slot].ToArray())
    Assert.Contains(slot, pair.ClientPort.Finished)
    Assert.Contains(slot, pair.ClientPort.Closed)
    Assert.Contains(slot, pair.ServerPort.Closed)
    Assert.Equal<struct (int * uint32)>([ struct (slot, 1u) ], pair.ServerPort.Opened)
    if loss > 0.0 then
        Assert.True(pair.Dropped > 0)
        Assert.True(pair.Server.Rx.Counters.Recovered > 0L)
        Assert.True(pair.Client.Rx.Counters.Recovered > 0L)

[<Fact>]
let ``a blocked local reader stops its flow through the flow credit, then resumes`` () =
    let config = { SessionConfig.defaults with WindowSteps = 512 }
    let pair = Pair(config, 4, 0.0, 7)
    pair.ServerPort.WriteLimit <- 0
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init (12000 * 1328) byte
    let mutable sent = 0
    for _ in 1 .. 1000 do
        sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
    // The receiver promised one flow window past the bytes its reader took (none).
    Assert.Equal(config.Credit.FlowWindowMax, sent)
    Assert.Equal(0, c.FlowRoom slot)
    Assert.Equal(sent, pair.Server.Flows.[slot].Reserve.Count)
    Assert.True(pair.Server.FlowStatesSent > 0L)
    pair.ServerPort.WriteLimit <- Int32.MaxValue
    for _ in 1 .. 6000 do
        pair.Server.PumpFlow slot
        sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
    Assert.Equal(payload.Length, sent)
    Assert.Equal<byte[]>(payload, pair.ServerPort.Data.[slot].ToArray())

[<Theory>]
[<InlineData(0.0)>]
[<InlineData(0.05)>]
let ``a stalled reader on one flow does not stop another flow`` (loss: float) =
    // Flow A sends more than the whole decode window while its reader takes nothing.
    // Before per-flow credit, A's undelivered steps filled the window and B stopped too.
    let config = { SessionConfig.defaults with WindowSteps = 512 }
    let pair = Pair(config, 4, loss, 11)
    let c = pair.Client
    let mutable a = -1
    let mutable b = -1
    Assert.True(c.TryOpenFlow(&a))
    Assert.True(c.TryOpenFlow(&b))
    pair.ServerPort.Blocked.Add a |> ignore
    let bulkA = Array.init (12000 * 1328) (fun i -> byte (i * 13))
    let dataB = Array.init 3_000_000 (fun i -> byte (i * 5 + 1))
    let mutable sentA = 0
    let mutable sentB = 0
    let mutable r = 0
    let doneB () = pair.ServerPort.Finished.Contains b
    while r < 20_000 && not (doneB ()) do
        sentA <- sentA + pair.Feed(c, a, ReadOnlySpan(bulkA, sentA, bulkA.Length - sentA), false)
        if sentB < dataB.Length || c.FlowSending b then
            sentB <- sentB + pair.Feed(c, b, ReadOnlySpan(dataB, sentB, dataB.Length - sentB), true)
        pair.Round() |> ignore
        r <- r + 1
    Assert.True(doneB (), $"flow B stalled: {pair.ServerPort.Stream(b).Length} of {dataB.Length} bytes")
    Assert.Equal<byte[]>(dataB, pair.ServerPort.Stream(b).ToArray())
    Assert.Equal(0L, pair.ServerPort.Stream(a).Length)
    Assert.True(sentA <= config.Credit.FlowWindowMax + 1328)
    // Now the reader of A wakes up: A finishes too, in order.
    pair.ServerPort.Blocked.Remove a |> ignore
    r <- 0
    while r < 40_000 && pair.ServerPort.Stream(a).Length < int64 bulkA.Length do
        pair.Server.PumpFlow a
        sentA <- sentA + pair.Feed(c, a, ReadOnlySpan(bulkA, sentA, bulkA.Length - sentA), false)
        pair.Round() |> ignore
        r <- r + 1
    Assert.Equal<byte[]>(bulkA, pair.ServerPort.Stream(a).ToArray())

[<Fact>]
let ``FLOW_STATE needs both caps bits`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 12)
    let both = Tbt.Crypto.Bootstrap.Caps.FlowState ||| Tbt.Crypto.Bootstrap.Caps.FlowState2
    Assert.Equal(both, pair.ServerParams.Caps &&& both)

[<Fact>]
let ``a fully lost tail is found by HEARTBEAT and repaired by BOOSTER`` () =
    let pair = Pair(SessionConfig.defaults, 4, 0.0, 8)
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let head = Array.init 40_000 (fun i -> byte i)
    let tail = Array.init 3_000 (fun i -> byte (i * 3))
    pair.Feed(c, slot, ReadOnlySpan head, false) |> ignore
    for _ in 1 .. 50 do pair.Round() |> ignore
    pair.Blackout <- true
    pair.Feed(c, slot, ReadOnlySpan tail, false) |> ignore
    for _ in 1 .. 5 do pair.Round() |> ignore
    pair.Blackout <- false
    let mutable r = 0
    while r < 2000 && pair.ServerPort.Stream(slot).Length < int64 (head.Length + tail.Length) do
        pair.Round() |> ignore
        r <- r + 1
    Assert.Equal<byte[]>(Array.append head tail, pair.ServerPort.Stream(slot).ToArray())
    Assert.True(c.Tx.BoostersSent > 0L)

[<Fact>]
let ``short local writes go through the reserve and keep every byte in order`` () =
    let config = { SessionConfig.defaults with WindowSteps = 512 }
    let pair = Pair(config, 4, 0.02, 13)
    let c = pair.Client
    let rnd = Random 5
    let flows = 6
    let slots = Array.zeroCreate<int> flows
    for i in 0 .. flows - 1 do
        let mutable s = -1
        Assert.True(c.TryOpenFlow(&s))
        slots.[i] <- s
    let data = Array.init flows (fun i -> Array.init (400_000 + i * 50_000) (fun j -> byte (j * (i + 3) + j / 977)))
    let sent = Array.zeroCreate<int> flows
    let mutable r = 0
    while r < 30_000 && slots |> Array.exists (fun s -> not (pair.ServerPort.Finished.Contains s)) do
        for i in 0 .. flows - 1 do
            if sent.[i] < data.[i].Length || c.FlowSending slots.[i] then
                sent.[i] <- sent.[i] + pair.Feed(c, slots.[i], ReadOnlySpan(data.[i], sent.[i], data.[i].Length - sent.[i]), true)
        pair.ServerPort.WriteLimit <- (if rnd.Next 3 = 0 then 0 else rnd.Next(1, 3000))
        for s in slots do pair.Server.PumpFlow s
        pair.Round() |> ignore
        r <- r + 1
    for i in 0 .. flows - 1 do
        Assert.Equal<byte[]>(data.[i], pair.ServerPort.Stream(slots.[i]).ToArray())

[<Sealed>]
type ZeroStore() =
    let source = Array.zeroCreate<byte> 2048
    interface ISourceStore with
        member _.Source(_, _) = ReadOnlySpan source

[<Fact>]
let ``the next generation opens when the old one closes on the peer's ACK`` () =
    // Server side: the client reuses a slot as soon as it has the server's FIN, so the next
    // OPEN can arrive while the server still waits for the ACK of its own FIN.
    let port = RecordingPort()
    let flows = FlowTable(port, ZeroStore(), Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
    let source (step: uint64) gen flags =
        let e: Tbt.Wire.SourceEnvelope = { RealLength = 10; FlowSlot = 3uy; Flags = flags; FlowGeneration = gen; StreamOffset = 0UL }
        Assert.True(flows.OnSource(step, 0, e))
        flows.Pump 3
    source 0UL 1u (Tbt.Wire.StreamFlags.Open ||| Tbt.Wire.StreamFlags.Fin)
    Assert.Equal(1, port.Opened.Count)
    Assert.Contains(3, port.Finished)
    // The server answers and ends its side in step 5.
    flows.NextEnvelope(3, 10, true) |> ignore
    flows.NoteEndStep(3, 5UL)
    // Generation 2 arrives before the ACK of step 5: it waits.
    source 1UL 2u Tbt.Wire.StreamFlags.Open
    Assert.Equal(1, port.Opened.Count)
    flows.OnPeerAck 6UL
    Assert.Equal<int list>([ 3 ], List.ofSeq port.Closed)
    Assert.Equal(2, port.Opened.Count)
    Assert.Equal(struct (3, 2u), port.Opened.[1])

[<Fact>]
let ``an interactive flow goes before a full bulk queue`` () =
    // No flow credit here, so the bulk flow stops on the cell queue only.
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 11)
    let c = pair.Client
    let mutable bulk = -1
    let mutable chat = -1
    Assert.True(c.TryOpenFlow(&bulk))
    Assert.True(c.TryOpenFlow(&chat))
    let payload = Array.init (1000 * 1328) byte
    let sent = pair.Feed(c, bulk, ReadOnlySpan payload, false)
    Assert.True(sent < payload.Length)
    Assert.False(c.CanFill)
    Assert.True(c.Tx.Queued >= SessionConfig.defaults.TxQueueCells)
    // The bulk flow is a heavy hitter; the new flow is not.
    Assert.False(c.IsInteractive bulk)
    Assert.True(c.IsInteractive chat)
    let message = Array.init 64 (fun i -> byte (200 + i))
    let mutable app = Span<byte>.Empty
    Assert.True(c.TryBeginSourceFor(chat, &app))
    message.CopyTo app
    c.CommitSource(chat, message.Length, false)
    c.Flush()
    Assert.Equal(1L, c.InteractiveSources)
    // One round moves 4 cells: the interactive step is among them, the bulk queue is not empty.
    pair.Round() |> ignore
    Assert.Equal<byte[]>(message, pair.ServerPort.Data.[chat].ToArray())
    Assert.True(c.Tx.Queued > 200)

/// Send src bytes of slot as interactive-or-bulk sources (as the carrier does); bytes taken.
let feedFor (s: Session) (slot: int) (data: ReadOnlySpan<byte>) (fin: bool) =
    let mutable at = 0
    let mutable app = Span<byte>.Empty
    while (at < data.Length || (fin && s.FlowSending slot)) && s.FlowRoom slot > 0 && s.TryBeginSourceFor(slot, &app) do
        let n = min (min app.Length (data.Length - at)) (s.FlowRoom slot)
        data.Slice(at, n).CopyTo app
        at <- at + n
        s.CommitSource(slot, n, fin && at = data.Length)
    s.Flush()
    at

[<Theory>]
[<InlineData(0.0)>]
[<InlineData(0.05)>]
let ``bulk download with echo on one session, twice in a row, completes and closes`` (loss: float) =
    let pair = Pair(SessionConfig.defaults, 4, loss, 21)
    let c, s = pair.Client, pair.Server
    let bulk = Array.init 1_500_000 (fun i -> byte (i * 13 + i / 997))
    for case in 1 .. 2 do
        let mutable b = -1
        let mutable e = -1
        Assert.True(c.TryOpenFlow(&b))
        Assert.True(c.TryOpenFlow(&e))
        for slot in [ b; e ] do
            pair.ServerPort.Finished.Remove slot |> ignore
            pair.ClientPort.Finished.Remove slot |> ignore
        let clientB0 = pair.ClientPort.Stream(b).Length
        let clientE0 = pair.ClientPort.Stream(e).Length
        let serverE0 = pair.ServerPort.Stream(e).Length
        let opened0 = pair.ServerPort.Opened.Count
        let cClosed0 = pair.ClientPort.Closed.FindAll(fun x -> x = b || x = e).Count
        let sClosed0 = pair.ServerPort.Closed.FindAll(fun x -> x = b || x = e).Count
        // OPEN for both flows (zero-length sources).
        let mutable app = Span<byte>.Empty
        for slot in [ b; e ] do
            Assert.True(c.TryBeginSourceFor(slot, &app))
            c.CommitSource(slot, 0, false)
        c.Flush()
        let mutable r = 0
        while pair.ServerPort.Opened.Count < opened0 + 2 && r < 2000 do
            pair.Round() |> ignore
            r <- r + 1
        Assert.Equal(opened0 + 2, pair.ServerPort.Opened.Count)
        let mutable sent = 0
        let mutable pings = 0
        let mutable echoed = 0L
        let mutable pending = false
        let ping = Array.init 64 byte
        r <- 0
        let closed () = pair.ClientPort.Closed.FindAll(fun x -> x = b || x = e).Count >= cClosed0 + 2
                        && pair.ServerPort.Closed.FindAll(fun x -> x = b || x = e).Count >= sClosed0 + 2
        while not (closed ()) && r < 60_000 do
            if sent < bulk.Length || s.FlowSending b then
                sent <- sent + feedFor s b (ReadOnlySpan(bulk, sent, bulk.Length - sent)) true
            // Echo: one 64-byte ping at a time; the server returns what it got.
            let got = pair.ServerPort.Stream(e).Length - serverE0
            if got > echoed then
                let back = pair.ServerPort.Stream(e).ToArray()
                let n = feedFor s e (ReadOnlySpan(back, int (serverE0 + echoed), int (got - echoed))) false
                echoed <- echoed + int64 n
            if pending && pair.ClientPort.Stream(e).Length - clientE0 = int64 (64 * pings) then pending <- false
            if not pending && pings < 20 && r % 50 = 0 then
                if feedFor c e (ReadOnlySpan ping) false = 64 then
                    pings <- pings + 1
                    pending <- true
            if pings = 20 && not pending && c.FlowSending e then
                feedFor c e ReadOnlySpan.Empty true |> ignore
                feedFor c b ReadOnlySpan.Empty true |> ignore
            if s.FlowSending e && pair.ServerPort.Finished.Contains e && echoed = int64 (64 * pings) then
                feedFor s e ReadOnlySpan.Empty true |> ignore
            pair.Round() |> ignore
            r <- r + 1
        Assert.Equal(20, pings)
        Assert.True(closed (), sprintf "case %d did not close in %d rounds" case r)
        Assert.Equal(int64 bulk.Length, pair.ClientPort.Stream(b).Length - clientB0)
        let down = pair.ClientPort.Stream(b).ToArray()
        Assert.True(ReadOnlySpan(down, int clientB0, bulk.Length).SequenceEqual(ReadOnlySpan bulk))

[<Fact>]
let ``interactive sources of one input round share a step, and a full reserve falls back to bulk`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 12)
    let c = pair.Client
    let slots = Array.init 80 (fun _ -> let mutable s = -1 in Assert.True(c.TryOpenFlow(&s)); s)
    let mutable app = Span<byte>.Empty
    // Round 1: three small writes on three flows, one Flush: one interactive step.
    for i in 0 .. 2 do
        Assert.True(c.TryBeginSourceFor(slots.[i], &app))
        c.CommitSource(slots.[i], 10, false)
    c.Flush()
    Assert.Equal(1L, c.Tx.UrgentSteps)
    Assert.Equal(3L, c.Tx.UrgentSources)
    // Many small writes, one per round, with no path: the reserve fills, then the
    // sources still go (bulk path) and are counted.
    for i in 3 .. 79 do
        Assert.True(c.TryBeginSourceFor(slots.[i], &app))
        c.CommitSource(slots.[i], 10, false)
        c.Flush()
    Assert.True(c.Tx.UrgentFull > 0L)
    Assert.True(c.Tx.UrgentQueued <= 64 + 16)
    Assert.Equal(80L, c.InteractiveSources + c.Tx.UrgentFull)
    Assert.Equal(c.InteractiveSources, c.Tx.UrgentSources)

[<Fact>]
let ``a first data source decoded before the zero-length OPEN still opens the flow`` () =
    // The client sends OPEN as a zero-length source, then data from offset 0 without the
    // OPEN flag. An interactive step can overtake the OPEN's step.
    let port = RecordingPort()
    let flows = FlowTable(port, ZeroStore(), Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
    let env len flags : Tbt.Wire.SourceEnvelope =
        { RealLength = len; FlowSlot = 5uy; Flags = flags; FlowGeneration = 1u; StreamOffset = 0UL }
    Assert.True(flows.OnSource(9UL, 0, env 20 Tbt.Wire.StreamFlags.None))
    flows.Pump 5
    Assert.Empty(port.Opened)
    Assert.True(flows.OnSource(8UL, 0, env 0 Tbt.Wire.StreamFlags.Open))
    flows.Pump 5
    Assert.Equal<struct (int * uint32) list>([ struct (5, 1u) ], List.ofSeq port.Opened)
    Assert.Equal(20L, port.Stream(5).Length)

/// Bulk from server to client on one flow; returns bytes the client got in rounds.
let private pumpDown (pair: Pair) (slot: int) (rounds: int) =
    let chunk = Array.create 65536 7uy
    let start = pair.ClientPort.Stream(slot).Length
    for _ in 1 .. rounds do
        feedFor pair.Server slot (ReadOnlySpan chunk) false |> ignore
        pair.Round() |> ignore
    pair.ClientPort.Stream(slot).Length - start

[<Fact>]
let ``a stall of the peer's acknowledgements does not leave the repair round trip inflated`` () =
    // Guard for the suspected loop (seal-to-custody samples across a repair wait feed the
    // 1.05 x SRTT repair rounds): with 20 ms one-way delay and 5% loss it does not lock in.
    let pair = Pair(SessionConfig.defaults, 4, 0.05, 41)
    pair.Delay <- 20_000_000L
    let c, s = pair.Client, pair.Server
    let mutable b = -1
    Assert.True(c.TryOpenFlow(&b))
    let mutable app = Span<byte>.Empty
    Assert.True(c.TryBeginSourceFor(b, &app))
    c.CommitSource(b, 0, false)
    c.Flush()
    let mutable r = 0
    while pair.ServerPort.Opened.Count < 1 && r < 2000 do
        pair.Round() |> ignore
        r <- r + 1
    pumpDown pair b 2000 |> ignore
    let before = pumpDown pair b 3000
    let srttBefore = s.Tx.SrttNs
    pair.Blackout <- true
    pumpDown pair b 1500 |> ignore
    pair.Blackout <- false
    pumpDown pair b 3000 |> ignore
    let after = pumpDown pair b 3000
    let msg = sprintf "before %d B, after %d B, srtt %.1f ms -> %.1f ms" before after (srttBefore / 1e6) (s.Tx.SrttNs / 1e6)
    Assert.True(before > 0L, msg)
    Assert.True(after * 10L >= before * 8L, msg)
    Assert.True(s.Tx.SrttNs < 4.0 * srttBefore + 5e6, msg)

let karnCase (loss: float) (blackMs: int) =
    // RTT 40 ms, 5 % loss. A 300 ms blackout strands many steps; their repair waits must not
    // enter SRTT, else repair rounds (1.05 x SRTT apart) slow down, SRTT grows, and the
    // tail crawls for seconds (seen in netns: srtt 286 -> 1428 ms, 4-8 steps/s).
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, loss, 31)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init ((500 + blackMs) * 8_000) (fun i -> byte (i * 7))
    let mutable sent = 0
    let feed () = sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, min 8_000 (payload.Length - sent)), false)
    for _ in 1 .. 500 do
        feed ()
        pair.Round() |> ignore
    let srtt0 = c.Tx.SrttNs
    pair.Blackout <- true
    for _ in 1 .. blackMs do
        feed ()
        pair.Round() |> ignore
    pair.Blackout <- false
    let mutable maxSrtt = 0.0
    let mutable r = 0
    while r < 20_000 && pair.ServerPort.Stream(slot).Length < int64 payload.Length do
        feed ()
        pair.Round() |> ignore
        maxSrtt <- max maxSrtt c.Tx.SrttNs
        r <- r + 1
    let msg = sprintf "srtt before %.0f ms, max after %.0f ms, end %.0f ms; %d ms to finish; boosters %d" (srtt0 / 1e6) (maxSrtt / 1e6) (c.Tx.SrttNs / 1e6) r c.Tx.BoostersSent
    Assert.True(pair.ServerPort.Stream(slot).Length = int64 payload.Length, msg)
    Assert.Equal<byte[]>(payload, pair.ServerPort.Stream(slot).ToArray())
    // SRTT stays near the path RTT (40 ms + queue), not the stall.
    Assert.True(maxSrtt < 4.0 * max srtt0 40e6, msg)
    Assert.True(r < 6_000, msg)
    msg

[<Theory>]
[<InlineData(0.02, 300)>]
[<InlineData(0.05, 300)>]
[<InlineData(0.10, 300)>]
let ``a repair stall does not inflate SRTT and slow every later repair (Karn)`` (loss: float, blackMs: int) =
    // Before the fix: 2 %: SRTT 807 ms; 5 % and 10 %: SRTT 5.9 / 6.3 s, tail unfinished after 20 s.
    karnCase loss blackMs |> ignore

[<Fact>]
let ``Karn skips samples held behind a repair but keeps clean ones`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, 0.05, 7)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let data = Array.zeroCreate<byte> 8_000
    for r in 1 .. 1000 do
        pair.Blackout <- r > 500 && r <= 600
        pair.Feed(c, slot, ReadOnlySpan data, false) |> ignore
        pair.Round() |> ignore
    Assert.True(c.Tx.RttSkipped > 0L)
    let m = c.Tx.MinRttNs
    Assert.True(m >= 40_000_000L && m < 60_000_000L, sprintf "min rtt %d" m)
    Assert.True(c.Tx.RepairWaitNs <= 4.0 * float m)

[<Fact>]
let ``a jump in peer ack samples only the newest sealed step, not the oldest`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            let took = pair.Feed(c, slot, ReadOnlySpan chunk, false)
            c.Tx.Flush()
            guard <- guard - 1
            if took = 0 && c.Tx.Head < n then failwith (sprintf "feed stuck at head %d" c.Tx.Head)
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 2UL
    Assert.Equal(0UL, c.Tx.PeerAck)
    // The first steps age 8 s, then two more seal, then an ack covers all after 400 ms.
    clock.Now <- clock.Now + 8_000_000_000L
    let afterGap = c.Tx.Head
    sealUntil (afterGap + 2UL)
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    // Newest step: 400 ms → SRTT 100 + (400-100)/8 = 137.5 ms. The 8 s gap would
    // have pushed SRTT above a second if the oldest step in the jump were used.
    Assert.InRange(c.Tx.SrttNs, 130e6, 150e6)

[<Fact>]
let ``an oversized seal-to-custody sample is clamped to three times minRTT`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            let took = pair.Feed(c, slot, ReadOnlySpan chunk, false)
            c.Tx.Flush()
            guard <- guard - 1
            if took = 0 && c.Tx.Head < n then failwith (sprintf "feed stuck at head %d" c.Tx.Head)
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 1UL
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    let head0 = c.Tx.Head
    sealUntil (head0 + 1UL)
    clock.Now <- clock.Now + 8_000_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    // minRTT stays 400 ms; the 8 s sample is used as 1.2 s. SRTT starts at
    // 137.5 ms after the first sample, then 137.5 + (1200-137.5)/8 = 270.3 ms.
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    Assert.InRange(c.Tx.SrttNs, 250e6, 300e6)
    // Deadline is clamped SRTT + 4·RTTVAR, never below minRTT or above 4×minRTT.
    // It must not follow the 8 s sample, and must exceed 1.05×minRTT once queueing/var is known.
    Assert.True(c.Tx.RepairWaitNs >= float c.Tx.MinRttNs, sprintf "wait %g min %d" c.Tx.RepairWaitNs c.Tx.MinRttNs)
    Assert.True(c.Tx.RepairWaitNs <= 4.0 * float c.Tx.MinRttNs, sprintf "wait %g" c.Tx.RepairWaitNs)
    Assert.True(c.Tx.RepairWaitNs < 2_000_000_000.0, sprintf "wait followed the 8 s sample: %g" c.Tx.RepairWaitNs)


[<Fact>]
let ``repair wait is minRTT plus measured queueing and variance, not 1.05 times minRTT alone`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            let took = pair.Feed(c, slot, ReadOnlySpan chunk, false)
            c.Tx.Flush()
            guard <- guard - 1
            if took = 0 && c.Tx.Head < n then failwith (sprintf "feed stuck at head %d" c.Tx.Head)
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 1UL
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    // A standing queue of 80 ms (20 % of minRTT) on later samples.
    let head0 = c.Tx.Head
    sealUntil (head0 + 3UL)
    clock.Now <- clock.Now + 480_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    // 1.05 × minRTT is 420 ms and would fire inside the queueing delay.
    Assert.True(c.Tx.RepairWaitNs > 1.05 * 400e6 + 1.0, sprintf "wait %g still 1.05×minRTT" c.Tx.RepairWaitNs)
    Assert.True(c.Tx.RepairWaitNs <= 4.0 * 400e6, sprintf "wait %g" c.Tx.RepairWaitNs)

[<Fact>]
let ``a NACK of the outstanding tail waits the long tail timer, not the short reorder window`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    let tLo = 1UL
    Assert.True(c.Tx.Head > tLo)
    // No later step has been seen: this is the tail. minRTT + reo_wnd is not enough.
    let evidenceAt = sealedAt + c.Tx.MinRttNs + c.Tx.ReoWndNs
    clock.Now <- evidenceAt
    c.Tx.OnNack(tLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "no later-step evidence: the short reo window must not fire")
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "tail timer passed; a repair round is due")

[<Fact>]
let ``a step is presumed lost only after a later-sealed step is seen and the reorder window passes`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 5UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    // The server has seen step 4 (WIN_ACK t_hi) while the frontier still sits at 1.
    c.Tx.OnPeerSeen 4UL
    Assert.Equal(4UL, c.Tx.PeerSeenHi)
    c.Tx.OnNack(1UL, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "later step seen, but its arrival plus reo_wnd has not passed")
    clock.Now <- sealedAt + c.Tx.MinRttNs + c.Tx.ReoWndNs
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "later-sealed step seen and reorder window passed")
    Assert.True(clock.Now < sealedAt + int64 c.Tx.RepairWaitNs, "evidence must not wait the tail timer")

[<Fact>]
let ``a NACK does not enqueue repairs before a minRTT sample exists`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    Assert.True(c.Tx.MinRttNs = Int64.MaxValue)
    let tLo = 1UL
    Assert.True(c.Tx.Head > tLo)
    clock.Now <- sealedAt + 200_000_000L
    c.Tx.OnNack(tLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "no expected arrival until a minRTT sample")
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "tail timer passed after a minRTT sample")

[<Fact>]
let ``a NACK repairs only the steps of the range whose originals are due`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
            c.Tx.Flush()
            guard <- guard - 1
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 2UL
    let earlySealed = clock.Now
    clock.Now <- earlySealed + 400_000_000L
    let tLo = c.Tx.Head - 1UL
    c.Tx.OnPeerAck(tLo, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    let wait = c.Tx.RepairWaitNs
    clock.Now <- earlySealed + int64 wait - 1_000_000L
    sealUntil (c.Tx.Head + 4UL)
    let span = int (c.Tx.Head - tLo)
    Assert.True(span >= 4, sprintf "span %d" span)
    clock.Now <- earlySealed + int64 wait
    c.Tx.OnNack(tLo, span, 30)
    Assert.True(c.Tx.RepairWaiting, "due prefix should repair")
    let frame = Array.zeroCreate<byte> c.FrameLength
    let before = c.Tx.BoostersSent
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    let sent = c.Tx.BoostersSent - before
    Assert.True(sent >= 1L && sent <= 16L, sprintf "boosters %d span %d wait %g" sent span wait)
    Assert.False(c.Tx.RepairWaiting)

[<Fact>]
let ``a growing NACK deficit does not skip the last-repair wait for that step`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    let tLo = 1UL
    let wait = c.Tx.RepairWaitNs
    clock.Now <- sealedAt + int64 wait
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    let afterFirst = c.Tx.BoostersSent
    Assert.True(afterFirst >= 1L)
    clock.Now <- clock.Now + 10_000_000L
    c.Tx.OnNack(tLo, 1, 30)
    Assert.False(c.Tx.RepairWaiting, "deficit growth must not skip this step's wait")
    Assert.Equal(afterFirst, c.Tx.BoostersSent)
    clock.Now <- clock.Now + int64 wait
    c.Tx.OnNack(tLo, 1, 30)
    Assert.True(c.Tx.RepairWaiting, "a later round is due once this step's wait has passed")

[<Fact>]
let ``seeing a later step does not make a not-yet-due hole eligible`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let earlySealed = clock.Now
    clock.Now <- earlySealed + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    let wait = c.Tx.RepairWaitNs
    // A later step, sealed just now, is seen at once: evidence exists, but reo_wnd
    // after that later seal has not passed, and the later step itself is not due.
    clock.Now <- earlySealed + int64 wait - 1_000_000L
    let lateLo = c.Tx.Head
    guard <- 64
    while c.Tx.Head < lateLo + 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    Assert.True(c.Tx.Head > lateLo, sprintf "head %d lateLo %d" c.Tx.Head lateLo)
    let lateSealed = clock.Now
    c.Tx.OnPeerSeen lateLo
    c.Tx.OnNack(lateLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "t_hi of a just-sealed step must not create eligibility")
    clock.Now <- lateSealed + 1_000_000L
    c.Tx.OnNack(lateLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "reorder window after the later seal has not passed")

[<Fact>]
let ``a never-repaired tail step is eligible on its own timer after another step was repaired`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 5UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.Head >= 5UL, sprintf "head %d" c.Tx.Head)
    let wait = c.Tx.RepairWaitNs
    clock.Now <- sealedAt + int64 wait
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    let afterFirst = c.Tx.BoostersSent
    clock.Now <- clock.Now + 10_000_000L
    c.Tx.OnNack(4UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "a due never-repaired tail step has its own deadline")
    Assert.True(c.Tx.BoostersSent = afterFirst, "boosters are queued, not yet sent")
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
    Assert.True(c.Tx.BoostersSent > afterFirst)

[<Fact>]
let ``Karn still samples a never-repaired step after a neighbour was repaired`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 5UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.Equal(0L, c.Tx.RttSkipped)
    let wait = c.Tx.RepairWaitNs
    clock.Now <- sealedAt + int64 wait
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(2UL, 100us)
    Assert.True(c.Tx.RttSkipped >= 1L, sprintf "skipped %d" c.Tx.RttSkipped)
    let skipped = c.Tx.RttSkipped
    let srtt = c.Tx.SrttNs
    c.Tx.OnPeerAck(4UL, 100us)
    Assert.Equal(skipped, c.Tx.RttSkipped)
    Assert.True(c.Tx.SrttNs <> srtt, sprintf "srtt stayed %g" srtt)

[<Fact>]
let ``a spurious repair widens the reorder window`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 5UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    let reo0 = c.Tx.ReoWndNs
    Assert.Equal(c.Tx.MinRttNs / 4L, reo0)
    c.Tx.OnPeerSeen 4UL
    clock.Now <- sealedAt + c.Tx.MinRttNs + reo0
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, allowRepair=true))
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.True(c.Tx.SpuriousRepairs >= 1L, sprintf "spurious %d" c.Tx.SpuriousRepairs)
    Assert.True(c.Tx.ReoWndNs > reo0, sprintf "reo grew from %d to %d" reo0 c.Tx.ReoWndNs)
    Assert.True(c.Tx.ReoWndNs <= c.Tx.MinRttNs)

[<Fact>]
let ``an ack that arrives sooner than minRTT after a BOOSTER counts as a spurious repair`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    let tLo = 1UL
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
    Assert.True(c.Tx.BoostersSent >= 1L)
    // Custody of the original arrives immediately: the BOOSTER has not had a minRTT on the path.
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.True(c.Tx.SpuriousRepairs >= 1L, sprintf "spurious %d boosters %d" c.Tx.SpuriousRepairs c.Tx.BoostersSent)

[<Fact>]
let ``the grant limit is each report's own ack plus its grant: late, duplicate and reordered reports never amplify it`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 5)
    let c = pair.Client
    let mutable b = -1
    Assert.True(c.TryOpenFlow(&b))
    let chunk = Array.create 65536 7uy
    feedFor c b (ReadOnlySpan chunk) false |> ignore
    let tx = c.Tx
    let head = tx.Head
    Assert.True(head >= 4UL, sprintf "head %d" head)
    let g0 = tx.GrantLimit
    // A late report of an older ack: its window counts from its own ack.
    tx.OnPeerAck(3UL, uint16 (g0 - 3UL + 100UL))
    Assert.Equal(g0 + 100UL, tx.GrantLimit)
    tx.OnPeerAck(1UL, uint16 (g0 - 1UL + 150UL))
    Assert.Equal(g0 + 150UL, tx.GrantLimit)
    Assert.Equal(3UL, tx.PeerAck)
    // A duplicate changes nothing; a smaller grant never takes the limit back.
    tx.OnPeerAck(3UL, uint16 (g0 - 3UL + 100UL))
    tx.OnPeerAck(head, 1us)
    Assert.Equal(g0 + 150UL, tx.GrantLimit)
    // An ack beyond our head is invalid and changes nothing.
    tx.OnPeerAck(head + 1UL, 65535us)
    Assert.Equal(g0 + 150UL, tx.GrantLimit)
    // Random interleaving of a report history: the limit is the max of ack + grant.
    let rnd = Random 3
    let reports = Array.init 200 (fun i -> struct (uint64 (rnd.Next(0, int head + 1)), uint16 (rnd.Next(0, 65536))))
    let mutable model = tx.GrantLimit
    for _ in 1 .. 2000 do
        let struct (a, g) = reports.[rnd.Next reports.Length]
        tx.OnPeerAck(a, g)
        model <- max model (a + uint64 g)
        Assert.Equal(model, tx.GrantLimit)

[<Fact>]
let ``256 flows opened one after another with readers that take nothing stay within the 32 MiB pool`` () =
    let config = { SessionConfig.defaults with WindowSteps = 1024 }
    let pair = Pair(config, 4, 0.0, 9)
    pair.ServerPort.WriteLimit <- 0
    let c = pair.Client
    let s = pair.Server
    let pool = int64 config.Credit.PoolBytes
    let c0 = int64 config.Credit.Initial
    let limits = Array.zeroCreate<uint64> 256
    let check () =
        Assert.True(s.Flows.CommittedBytes <= pool, sprintf "committed %d > pool %d" s.Flows.CommittedBytes pool)
        for i in 0 .. 255 do
            let l = s.Flows.[i].RxLimit
            if s.Flows.[i].Active then
                Assert.True(l >= limits.[i], sprintf "slot %d limit %d fell below %d" i l limits.[i])
                limits.[i] <- l
    // The first flow fills its whole promise: the bytes sit in its reserve (held, not consumed).
    let payload = Array.create (9 <<< 20) 5uy
    let mutable first = -1
    Assert.True(c.TryOpenFlow(&first))
    let mutable sent = 0
    for _ in 1 .. 1500 do
        sent <- sent + pair.Feed(c, first, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
        check ()
    Assert.Equal(config.Credit.FlowWindowMax, sent)
    let mutable app = Span<byte>.Empty
    let mutable opened = 1
    for _ in 1 .. 255 do
        let mutable b = -1
        Assert.True(c.TryOpenFlow(&b))
        Assert.True(c.TryBeginSourceFor(b, &app))
        c.CommitSource(b, 0, false)
        c.Flush()
        opened <- opened + 1
        for _ in 1 .. 20 do
            pair.Round() |> ignore
            check ()
    for _ in 1 .. 200 do
        pair.Round() |> ignore
        check ()
    Assert.Equal(256, s.Flows.RxActive)
    // Every flow keeps at least C0; the sum of promises would be ~161 MiB with a per-flow split.
    let total = [ for i in 0 .. 255 -> int64 s.Flows.[i].RxLimit ] |> List.sum
    Assert.True(total <= pool, sprintf "sum of limits %d" total)
    for i in 0 .. 255 do Assert.True(int64 s.Flows.[i].RxLimit >= c0)
    // The reader of the first flow takes its bytes: the pool frees and the others can grow.
    let usedBefore = s.Flows.PoolUsed
    pair.ServerPort.WriteLimit <- Int32.MaxValue
    s.PumpFlow first
    for _ in 1 .. 50 do
        pair.Round() |> ignore
        check ()
    Assert.True(s.Flows.PoolUsed <= usedBefore, sprintf "%d -> %d" usedBefore s.Flows.PoolUsed)
