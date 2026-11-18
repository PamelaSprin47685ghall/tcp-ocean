module Tbt.Tests.SessionTestsB

open System
open System.Collections.Generic
open System.IO
open Xunit
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Crypto.Bootstrap
open Tbt.Tests.SessionTests

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

[<Fact>]
let ``a local RST is sent while the bulk queue is full`` () =
    // Spec 6.10b.4: FIN/RST uses the close reserve; a full data queue must not block the end.
    // This is the pingbulk leftover: the client aborts a flow while new echo data already
    // fills TxQueueCells, and the RST must still reach the server in the next round.
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 17)
    let c = pair.Client
    let mutable bulk = -1
    let mutable victim = -1
    Assert.True(c.TryOpenFlow(&bulk))
    Assert.True(c.TryOpenFlow(&victim))
    Assert.Equal(3, pair.Feed(c, victim, ReadOnlySpan [| 1uy; 2uy; 3uy |], false))
    pair.Round() |> ignore
    Assert.Equal(3L, pair.ServerPort.Stream(victim).Length)
    let payload = Array.init (1000 * 1328) byte
    let sent = pair.Feed(c, bulk, ReadOnlySpan payload, false)
    Assert.True(sent < payload.Length)
    Assert.False(c.CanFill)
    Assert.True(c.CanFillClose)
    Assert.True(c.Tx.Queued >= SessionConfig.defaults.TxQueueCells)
    c.FailFlow victim
    Assert.True(c.Flows.ResetPending victim)
    c.Flush()
    Assert.False(c.Flows.ResetPending victim)
    Assert.True(c.Flows.[victim].TxEndSent)
    Assert.False(c.CanFill)
    pair.Round() |> ignore
    Assert.Contains(victim, pair.ServerPort.Resets)

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
let pumpDown (pair: Pair) (slot: int) (rounds: int) =
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

