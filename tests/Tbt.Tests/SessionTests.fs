module Tbt.Tests.SessionTests

open System
open System.Collections.Generic
open System.IO
open Xunit
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Crypto.Bootstrap

[<Sealed>]
type ManualClock() =
    member val Now = 1_000_000_000L with get, set
    interface IClock with
        member c.NowNs() = c.Now
        member c.FreshNs() = c.Now

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
type Pair(config: SessionConfig, paths: int, loss: float, seed: int, ?wrapClock: ManualClock -> IClock) =
    let inner = ManualClock()
    let clock: IClock =
        match wrapClock with
        | Some f -> f inner
        | None -> inner :> IClock
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
                    if delay > 0 then inFlight.Enqueue(struct (inner.Now + delay, dst, uint16 p, Array.copy frame))
                    else dst.OnFrame(uint16 p, Span frame) |> ignore
                else dropped <- dropped + 1
        moved

    let deliver () =
        let mutable go = true
        while go && inFlight.Count > 0 do
            let struct (due, dst, p, f) = inFlight.Peek()
            if due <= inner.Now then
                inFlight.Dequeue() |> ignore
                dst.OnFrame(p, Span f) |> ignore
            else go <- false

    member _.Clock = inner
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
        inner.Now <- inner.Now + 1_000_000L
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
    let ack = joinAck kPath (SeededEntropy 5) p.Keys.SessionId p.Symbol p.Caps 256 (ReadOnlySpan join.Frame) r Tbt.Crypto.Join.JoinStatus.Accept 1448 Suite.ChaCha20Poly1305
    Assert.True(join.TryAccept(ReadOnlySpan ack).IsSome)
    let wrongS = joinAck kPath (SeededEntropy 5) p.Keys.SessionId 1280 p.Caps 256 (ReadOnlySpan join.Frame) r Tbt.Crypto.Join.JoinStatus.Accept 1448 Suite.ChaCha20Poly1305
    Assert.True(join.TryAccept(ReadOnlySpan wrongS).IsNone)
    Assert.True((tryJoin (Array.zeroCreate 32) (ReadOnlySpan join.Frame)).IsNone)

/// Echo: the server returns every byte it receives on the same flow, then FIN.
let echo (pair: Pair) (payload: byte[]) (rounds: int) =
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
    // The step window must be larger than one 24 MiB credit window, or WIN_ACK
    // stops the sender before credit does and the assertion measures the wrong gate.
    let config = { SessionConfig.defaults with WindowSteps = 2048 }
    let pair = Pair(config, 4, 0.0, 7)
    pair.ServerPort.WriteLimit <- 0
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init (config.Credit.FlowWindowMax + (1 <<< 20)) byte
    let mutable sent = 0
    for _ in 1 .. 2500 do
        sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
    // The receiver promised one flow window past the bytes its reader took (none).
    Assert.Equal(config.Credit.FlowWindowMax, sent)
    Assert.Equal(0, c.FlowRoom slot)
    Assert.Equal(sent, pair.Server.Flows.[slot].Reserve.Count)
    Assert.True(pair.Server.FlowStatesSent > 0L)
    pair.ServerPort.WriteLimit <- Int32.MaxValue
    for _ in 1 .. 8000 do
        pair.Server.PumpFlow slot
        sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
    Assert.Equal(payload.Length, sent)
    Assert.Equal<byte[]>(payload, pair.ServerPort.Data.[slot].ToArray())

