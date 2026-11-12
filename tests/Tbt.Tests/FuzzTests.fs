/// Peer input must never throw: random and malformed cells (authenticated, so they reach
/// the decoder and the session), random TCP segments on the TUN path, and random cell
/// straddling over segment sizes down to 1 byte. Deterministic seeds.
module Tbt.Tests.FuzzTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Wire
open Tbt.Session
open Tbt.Carrier
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests

[<Fact>]
let ``fill: a repeated DATA repair (BOOSTER seeds used up) does not crash the borrowed-shell fill`` () =
    // The A/B crash: TxHalf.RenderFill took every repair entry for a BOOSTER; a DATA entry
    // (span 0) made boosterTerms throw inside the TUN tail repair.
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 11)
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    pair.Feed(c, slot, ReadOnlySpan(Array.create 4000 7uy), false) |> ignore
    let tLo = c.Tx.Head - 1UL
    // Five NACK rounds of 64 BOOSTERs: the 256 seeds run out, DATA cells go again.
    for d in 60 .. 64 do c.Tx.OnNack(tLo, 1, d)
    let frame = Array.zeroCreate<byte> c.FrameLength
    for _ in 1 .. 400 do c.ProduceFill(0us, Span frame) |> ignore
    // The DATA repairs are still there for the normal path.
    let mutable rendered = 0
    while c.TryProduce(0us, Span frame) do rendered <- rendered + 1
    Assert.True(rendered >= 1)

let private randomMeta (rnd: Random) (head: uint64) =
    let types = [| 0uy; 1uy; 2uy; 3uy; 4uy; 5uy; 6uy; 7uy; 8uy; 9uy; 10uy; 255uy |]
    let step () =
        match rnd.Next 4 with
        | 0 -> uint64 (rnd.Next 8)
        | 1 -> head + uint64 (rnd.Next 600) - 300UL
        | 2 -> UInt64.MaxValue - uint64 (rnd.Next 4)
        | _ -> uint64 (rnd.NextInt64())
    { Type = LanguagePrimitives.EnumOfValue(types.[rnd.Next types.Length])
      Flags = LanguagePrimitives.EnumOfValue(byte (rnd.Next 256) &&& (if rnd.Next 2 = 0 then 0uy else 0xFFuy))
      Slot = byte (rnd.Next 256)
      StepUsed = byte (if rnd.Next 2 = 0 then rnd.Next 18 else rnd.Next 256)
      Aux = uint16 (rnd.Next 65536)
      RxWinGrant = uint16 (rnd.Next 65536)
      StepId = step ()
      AckStepLo = step ()
      CarrierWm = step ()
      KHist = uint32 (rnd.NextInt64(0L, 1L <<< 32))
      ErasureQ16 = uint16 (rnd.Next 65536) }

[<Theory>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
let ``fuzz: random authenticated cells reach the server without an exception`` (seed: int) =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, seed)
    let rnd = Random seed
    let c = pair.Client
    let s = pair.Server
    let frame = Array.zeroCreate<byte> c.FrameLength
    let sym = s.SymbolSize
    for i in 1 .. 3000 do
        Array.Clear frame
        let m = randomMeta rnd s.Tx.Head
        FrameMath.writeChunkHeader sym (Span frame)
        Meta.write m (Span(frame, Layout.MetaOffset, Layout.MetaLength))
        let symbol = Span(frame, Layout.SymbolOffset, sym)
        match rnd.Next 4 with
        | 0 -> rnd.NextBytes symbol
        | 1 -> ()
        | 2 ->
            // A WIN_ACK-like or FLOW_STATE-like head with random fields.
            rnd.NextBytes(symbol.Slice(0, 96))
        | _ ->
            // A source envelope-like symbol: small header values, random body.
            rnd.NextBytes symbol
            symbol.Slice(0, 16).Clear()
            symbol.[0] <- byte (rnd.Next 8)
            symbol.[1] <- byte (rnd.Next 256)
        frame.[frame.Length - 2] <- 13uy
        frame.[frame.Length - 1] <- 10uy
        if rnd.Next 10 = 0 then
            // Unauthenticated garbage.
            rnd.NextBytes frame
        else c.Tx.Seal(Span frame, uint16 (rnd.Next 3))
        s.OnFrame(uint16 (rnd.Next 3), Span frame) |> ignore
        if i % 50 = 0 then
            pair.Round() |> ignore
    for _ in 1 .. 200 do pair.Round() |> ignore
    // These cells carry the peer's own keys: they may break this session, but then it must
    // fail (and be torn down), never stall silently. Healthy means a real flow still works.
    if not s.Faulted then
        let mutable slot = -1
        Assert.True(c.TryOpenFlow(&slot))
        let data = Array.init 20000 (fun i -> byte (i * 13))
        let mutable sent = 0
        let mutable r = 0
        let got () = if pair.ServerPort.Data.ContainsKey slot then int pair.ServerPort.Data.[slot].Length else 0
        while r < 3000 && got () < data.Length && not s.Faulted do
            if sent < data.Length then sent <- sent + pair.Feed(c, slot, ReadOnlySpan(data, sent, data.Length - sent), false)
            pair.Round() |> ignore
            r <- r + 1
        Assert.True(s.Faulted || got () = data.Length, sprintf "server neither failed nor carried the flow (%d of %d bytes)" (got ()) data.Length)

[<Theory>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
[<InlineData(4)>]
let ``fuzz: random TCP segments on a TUN path never throw`` (seed: int) =
    let rnd = Random seed
    let f = Fixture 20
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 50
    let sizes = [| 0; 1; 2; 3; 40; 536; 1000; 1415; 1460; 1461; 4000 |]
    for i in 1 .. 4000 do
        f.Clock.Now <- f.Clock.Now + int64 (rnd.Next 3_000_000)
        let mutable frame = Span<byte>.Empty
        if rnd.Next 3 = 0 && ip.TryBeginFrame(&frame) then
            frame.Fill(byte i)
            ip.CommitFrame()
        let n = sizes.[rnd.Next sizes.Length]
        let payload = Array.zeroCreate<byte> n
        rnd.NextBytes payload
        let seq = f.Data0 + uint32 (rnd.Next 60000) - 1000u
        let ack = if rnd.Next 2 = 0 then 1001u + uint32 (rnd.Next 200000) else uint32 (rnd.NextInt64(0L, 1L <<< 32))
        let flags = if rnd.Next 50 = 0 then byte (rnd.Next 256) else Packet.Ack ||| (if rnd.Next 2 = 0 then Packet.Psh else 0uy)
        let options =
            match rnd.Next 5 with
            | 0 -> [||]
            | 1 -> sackOption [ for _ in 0 .. rnd.Next 4 -> (uint32 (rnd.NextInt64(0L, 1L <<< 32)), uint32 (rnd.NextInt64(0L, 1L <<< 32))) ]
            | 2 -> Array.init (4 * (1 + rnd.Next 9)) (fun _ -> byte (rnd.Next 256))
            | 3 -> [| 5uy; 255uy; 0uy; 0uy |]
            | _ -> [| 1uy; 1uy; 5uy; 2uy |]
        let struct (s, p) = clientSegment seq ack flags options (ReadOnlySpan payload)
        f.Path.OnSegment(s, Span p, f.Clock.Now)
        f.Path.Service f.Clock.Now
        f.Path.FlushAck()
        Assert.True(f.Path.Inflight >= 0)

[<Theory>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
let ``fuzz: cells straddling random segments (1 byte to 1460, 536 MSS) all arrive intact`` (seed: int) =
    let rnd = Random seed
    let frames = 40
    let f = Fixture frames
    f.Open()
    // Cut the stream at random points, some segments reordered and repeated; no loss and
    // no time passes, so nothing is swallowed: every frame must come out once, intact.
    let cuts = Collections.Generic.List<int * int>()
    let mutable at = 0
    let sizes = [| 1; 2; 7; 100; 536; 536; 1000; 1414; 1415; 1416; 1460 |]
    while at < f.Stream.Length do
        let n = min sizes.[rnd.Next sizes.Length] (f.Stream.Length - at)
        cuts.Add((at, n))
        at <- at + n
    let order = cuts.ToArray()
    for i in 0 .. order.Length - 2 do
        if rnd.Next 4 = 0 then
            let t = order.[i]
            order.[i] <- order.[i + 1]
            order.[i + 1] <- t
    for (a, n) in order do
        f.Send(f.Data0 + uint32 a, ReadOnlySpan(f.Stream, a, n), [||])
        if rnd.Next 5 = 0 then f.Send(f.Data0 + uint32 a, ReadOnlySpan(f.Stream, a, n), [||])
    let got = [ for fr in f.Owner.Frames -> fr ] 
    Assert.Equal(frames, got.Length)
    let firsts = got |> List.map (fun fr -> int fr.[0]) |> List.sort
    Assert.Equal<int list>([ 1 .. frames ], firsts)
    for fr in got do Assert.True(fr |> Array.forall (fun b -> b = fr.[0]), "a frame came out mixed")

let private sealCell (c: Session) (m: Meta) =
    let frame = Array.zeroCreate<byte> c.FrameLength
    FrameMath.writeChunkHeader c.SymbolSize (Span frame)
    Meta.write m (Span(frame, Layout.MetaOffset, Layout.MetaLength))
    frame.[frame.Length - 2] <- 13uy
    frame.[frame.Length - 1] <- 10uy
    c.Tx.Seal(Span frame, 0us)
    frame

[<Fact>]
let ``session: two k for one step from the peer fail the session at once`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 21)
    let s = pair.Server
    let t = s.Frontier + 2UL
    let cell k = { Type = CellType.Data; Flags = MetaFlags.None; Slot = 0uy; StepUsed = byte k; Aux = 0us
                   RxWinGrant = 1024us; StepId = t; AckStepLo = 0UL; CarrierWm = 0UL; KHist = 0u; ErasureQ16 = 0us }
    s.OnFrame(0us, Span(sealCell pair.Client (cell 3))) |> ignore
    Assert.False(s.Faulted)
    s.OnFrame(0us, Span(sealCell pair.Client (cell 4))) |> ignore
    Assert.True(s.Faulted)
    Assert.Equal("peer step_used conflict", s.Fault)

[<Fact>]
let ``tun: an exception from the owner fails that owner and resets the connection, nothing escapes`` () =
    let f = Fixture 2
    f.Open()
    f.Owner.Throw <- true
    f.Send(f.Data0, ReadOnlySpan(f.Stream, 0, L), [||])
    Assert.Equal(1L, f.Path.Counters.Faults)
    Assert.Equal(1, f.Owner.Failures.Count)
    Assert.True(f.Path.Closed)
    let last = parse f.Out.Packets.[f.Out.Packets.Count - 1]
    Assert.True(last.Flags &&& Packet.Rst <> 0uy)
