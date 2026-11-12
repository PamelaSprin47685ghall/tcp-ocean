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

    let move (src: Session) (dst: Session) =
        let mutable moved = 0
        for p in 0 .. paths - 1 do
            let mutable n = 0
            while n < 4 && src.TryProduce(uint16 p, Span frame) do
                n <- n + 1
                moved <- moved + 1
                if rnd.NextDouble() >= loss && not (blackout && obj.ReferenceEquals(src, client)) then
                    dst.OnFrame(uint16 p, Span frame) |> ignore
                else dropped <- dropped + 1
        moved

    member _.Client = client
    member _.Server = server
    member _.ClientPort = clientPort
    member _.ServerPort = serverPort
    member _.ClientParams = clientParams
    member _.ServerParams = serverParams
    member _.Dropped = dropped
    /// Drop every client cell while set.
    member _.Blackout with get () = blackout and set v = blackout <- v

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
