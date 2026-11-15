module Tbt.Tests.ControlTests

open System
open System.Buffers.Binary
open Xunit
open Tbt.Wire
open Tbt.Session

let private render (log: ControlLog) now srtt =
    let cell = Array.zeroCreate<byte> 64
    if log.TryRenderDue(now, srtt, Span cell) then
        let mutable h = Unchecked.defaultof<CtrlHead>
        Assert.True(Ctrl.tryReadHead (ReadOnlySpan cell) &h)
        Some(h, cell.[Ctrl.HeadLength .. Ctrl.HeadLength + h.BodyLength - 1])
    else None

[<Fact>]
let ``ctrl: an operation is sent, sent again after 1.05 SRTT with the same seq and body, and leaves on ACK`` () =
    let log = ControlLog()
    let seq = log.Start(CtrlOp.SessionClose, ReadOnlySpan [| 3uy |])
    Assert.Equal(1u, seq)
    let srtt = 40_000_000L
    match render log 0L srtt with
    | Some(h, body) ->
        Assert.Equal(1u, h.ControlSeq)
        Assert.Equal(CtrlOp.SessionClose, h.Op)
        Assert.Equal<byte[]>([| 3uy |], body)
    | None -> failwith "not sent"
    Assert.True((render log 41_000_000L srtt).IsNone)
    match render log 42_000_000L srtt with
    | Some(h, body) ->
        Assert.Equal(1u, h.ControlSeq)
        Assert.Equal<byte[]>([| 3uy |], body)
    | None -> failwith "not sent again"
    // WIN_ACK: ctrl_ack_hi 2 means seq 1 executed.
    log.OnAck(2u, 0UL)
    Assert.Equal(0, log.Pending)
    Assert.True((render log 200_000_000L srtt).IsNone)

[<Fact>]
let ``ctrl: the receiver executes each seq once and its ACK covers out-of-order arrivals`` () =
    let rx = ControlLog()
    Assert.Equal(ControlArrival.Fresh, rx.Arrive 2u)
    rx.Executed 2u
    Assert.Equal(1u, rx.AckHi)
    Assert.Equal(0b10UL, rx.AckBitmap)
    Assert.Equal(ControlArrival.Duplicate, rx.Arrive 2u)
    Assert.Equal(ControlArrival.Fresh, rx.Arrive 1u)
    rx.Executed 1u
    Assert.Equal(3u, rx.AckHi)
    Assert.Equal(0UL, rx.AckBitmap)
    Assert.Equal(ControlArrival.Duplicate, rx.Arrive 1u)
    Assert.Equal(ControlArrival.OutOfWindow, rx.Arrive 67u)
    Assert.Equal(ControlArrival.Fresh, rx.Arrive 66u)

[<Fact>]
let ``ctrl: a bitmap ACK removes only the entries it names`` () =
    let log = ControlLog()
    for _ in 1 .. 3 do log.Start(CtrlOp.MssNotice, ReadOnlySpan(Array.zeroCreate<byte> 4)) |> ignore
    // hi 1, bit 1: seq 2 executed, 1 and 3 not.
    log.OnAck(1u, 0b10UL)
    Assert.Equal(2, log.Pending)
    let sent = [ for t in 0 .. 1 -> (render log (int64 t) 1_000_000_000L).Value |> fst |> fun h -> h.ControlSeq ]
    Assert.Equal<uint32 list>([ 1u; 3u ], sent)

[<Fact>]
let ``ctrl: 48 own operations at most, the last 16 entries are kept for replies`` () =
    let log = ControlLog()
    let body = ReadOnlySpan(Array.zeroCreate<byte> 4)
    for k in 1 .. 48 do Assert.Equal(uint32 k, log.Start(CtrlOp.MssNotice, body))
    Assert.Equal(0u, log.Start(CtrlOp.MssNotice, body))
    for _ in 1 .. 16 do
        Assert.True(log.CanReply)
        Assert.NotEqual(0u, log.Reply(CtrlOp.SessionClosed, ReadOnlySpan.Empty))
    Assert.False(log.CanReply)
    Assert.Equal(0u, log.Reply(CtrlOp.SessionClosed, ReadOnlySpan.Empty))

[<Fact>]
let ``ctrl: no ACK within the retry limit is a hard failure`` () =
    let log = ControlLog(1_000_000_000L)
    log.Start(CtrlOp.SessionClose, ReadOnlySpan [| 0uy |]) |> ignore
    let srtt = 100_000_000L
    let mutable t = 0L
    while t <= 1_200_000_000L do
        render log t srtt |> ignore
        t <- t + 110_000_000L
    Assert.True(log.Failed)

[<Fact>]
let ``ctrl: a body that does not match the operation is refused`` () =
    let log = ControlLog()
    Assert.Throws<ArgumentException>(fun () -> log.Start(CtrlOp.SessionClose, ReadOnlySpan.Empty) |> ignore) |> ignore

// ---- wired into the session ---------------------------------------------------------

open Tbt.Contracts
open Tbt.Tests.SessionTests

/// A CTRL cell from c, sealed as c would send it.
let private ctrlCell (c: Session) (seq: uint32) (op: CtrlOp) (body: byte[]) =
    let frame = Array.zeroCreate<byte> c.FrameLength
    let symbol = Span(frame, Layout.SymbolOffset, c.SymbolSize)
    BinaryPrimitives.WriteUInt32LittleEndian(symbol, seq)
    BinaryPrimitives.WriteUInt16LittleEndian(symbol.Slice 4, uint16 op)
    BinaryPrimitives.WriteUInt16LittleEndian(symbol.Slice 6, uint16 body.Length)
    body.CopyTo(symbol.Slice 8)
    let m = { Type = CellType.Ctrl; Flags = MetaFlags.None; Slot = 0uy; StepUsed = 0uy; Aux = 0us
              RxWinGrant = 1024us; StepId = 0UL; AckStepLo = 0UL; CarrierWm = 0UL; KHist = 0u; ErasureQ16 = 0us }
    FrameMath.writeChunkHeader c.SymbolSize (Span frame)
    Meta.write m (Span(frame, Layout.MetaOffset, Layout.MetaLength))
    frame.[frame.Length - 2] <- 13uy
    frame.[frame.Length - 1] <- 10uy
    c.Tx.Seal(Span frame, 0us)
    frame

[<Fact>]
let ``session close: SESSION_CLOSE and SESSION_CLOSED complete under 30% loss`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.3, 31)
    for _ in 1 .. 20 do pair.Round() |> ignore
    Assert.True(pair.Client.Close 7uy)
    Assert.False(pair.Client.Close 7uy)
    let mutable n = 0
    while not (pair.Client.Closed && pair.Server.Closed) && n < 20000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.True(pair.Client.Closed, "client still " + string pair.Client.CloseState)
    Assert.True(pair.Server.Closed, "server still " + string pair.Server.CloseState)
    Assert.Equal(7, pair.Server.PeerCloseReason)
    Assert.Equal(-1, pair.Client.PeerCloseReason)
    Assert.Equal(1L, pair.Server.CtrlExecuted)
    Assert.Equal(1L, pair.Client.CtrlExecuted)
    Assert.False(pair.Client.Faulted || pair.Server.Faulted)
    Assert.True(pair.Dropped > 0)

[<Fact>]
let ``session close: a repeated SESSION_CLOSE is executed once, its acknowledgement is sent again`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 32)
    let s = pair.Server
    let cell = ctrlCell pair.Client 1u CtrlOp.SessionClose [| 3uy |]
    s.OnFrame(0us, Span(Array.copy cell)) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.SessionClose [| 3uy |])) |> ignore
    Assert.Equal(1L, s.CtrlExecuted)
    Assert.Equal(0L, s.CtrlRejected)
    Assert.Equal(CloseState.Draining, s.CloseState)
    Assert.True(s.WantsToSend)

[<Fact>]
let ``session control: unsupported, unknown and out-of-window operations are not executed`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 33)
    let s = pair.Server
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.KeyRetire [| 1uy; 0uy; 0uy; 0uy |])) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u (LanguagePrimitives.EnumOfValue<uint16, CtrlOp> 99us) [||])) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 65u CtrlOp.SessionClose [| 1uy |])) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.SessionClose [| 1uy; 1uy |])) |> ignore
    Assert.Equal(4L, s.CtrlRejected)
    Assert.Equal(0L, s.CtrlExecuted)
    Assert.Equal(CloseState.Open, s.CloseState)
    // The window did not move: control_seq 1 is still fresh.
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.SessionClose [| 1uy |])) |> ignore
    Assert.Equal(1L, s.CtrlExecuted)

[<Fact>]
let ``session close: with no answer the close ends at the retry limit, an open session fails`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 34)
    for _ in 1 .. 20 do pair.Round() |> ignore
    pair.Blackout <- true
    Assert.True(pair.Client.Close 0uy)
    let mutable n = 0
    while not pair.Client.Closed && n < 40000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.True(pair.Client.Closed)
    Assert.True(n > 29000, "closed after " + string n + " ms, before the 30 s retry limit")
    Assert.False(pair.Client.Faulted)
    Assert.Equal(CloseState.Open, pair.Server.CloseState)

let private pump (pair: Pair) (rounds: int) (done': unit -> bool) =
    let data = Array.init 40_000 (fun i -> byte (i * 13))
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    let mutable n = 0
    while n < rounds && not (done'()) do
        pair.Feed(pair.Client, slot, ReadOnlySpan data, false) |> ignore
        pair.Round() |> ignore
        n <- n + 1
    n, slot

let private prepareBody (gen: uint32) (cut: uint64) =
    let b = Array.zeroCreate<byte> 12
    BinaryPrimitives.WriteUInt32LittleEndian(Span(b, 0, 4), gen)
    BinaryPrimitives.WriteUInt64LittleEndian(Span(b, 4, 8), cut)
    b

[<Fact>]
let ``rekey: KEY_PREPARE, KEY_READY and KEY_RETIRE install and retire a generation at CutCounter`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 40)
    for _ in 1 .. 20 do pair.Round() |> ignore
    let start = pair.Client.Tx.NextCounter
    Assert.True(pair.Client.BeginRekey())
    Assert.Equal(start + 4096UL, pair.Client.RekeyCut)
    Assert.True(pair.Client.RekeyPending)
    Assert.Equal(1, pair.Client.TxKeyCount)
    let n, slot = pump pair 20_000 (fun () ->
        pair.Client.TxKeyCount = 1 && pair.Client.TxGeneration = 1u
        && pair.Server.RxKeyCount = 1 && pair.Server.RxGeneration = 1u)
    Assert.True(n < 20_000, "rekey did not finish in " + string n + " rounds")
    Assert.Equal(1, pair.Client.TxKeyCount)
    Assert.Equal(1u, pair.Client.TxGeneration)
    Assert.Equal(1, pair.Server.RxKeyCount)
    Assert.Equal(1u, pair.Server.RxGeneration)
    // The downlink was not part of this transaction.
    Assert.Equal(0u, pair.Server.TxGeneration)
    Assert.Equal(0u, pair.Client.RxGeneration)
    Assert.True(pair.Client.Tx.NextCounter >= pair.Client.RekeyCut)
    Assert.False(pair.Client.Faulted || pair.Server.Faulted)
    // Traffic after retire still authenticates with the new generation.
    let more = Array.init 8_000 (fun i -> byte (i * 3 + 7))
    let mutable sent = 0
    let before = if pair.ServerPort.Data.ContainsKey slot then int pair.ServerPort.Data.[slot].Length else 0
    for _ in 1 .. 400 do
        sent <- sent + pair.Feed(pair.Client, slot, ReadOnlySpan(more, sent, more.Length - sent), false)
        pair.Round() |> ignore
    let after = if pair.ServerPort.Data.ContainsKey slot then int pair.ServerPort.Data.[slot].Length else 0
    Assert.True(after - before >= more.Length / 2, "post-rekey delivery stalled")
    Assert.Equal(0L, pair.Server.Opener.AuthFails)

[<Fact>]
let ``rekey: a bad KEY_PREPARE is not executed`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 41)
    for _ in 1 .. 20 do pair.Round() |> ignore
    let s = pair.Server
    // Wrong next_gen (2, not 1) and a CutCounter that is not after the seen max.
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.KeyPrepare (prepareBody 2u 1UL))) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 2u CtrlOp.KeyPrepare (prepareBody 1u 0UL))) |> ignore
    Assert.Equal(2L, s.CtrlRejected)
    Assert.Equal(0L, s.CtrlExecuted)
    Assert.Equal(1, s.RxKeyCount)
    Assert.Equal(0u, s.RxGeneration)
    // control_seq 1 is still fresh: a valid close is executed.
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.SessionClose [| 1uy |])) |> ignore
    Assert.Equal(1L, s.CtrlExecuted)

[<Fact>]
let ``rekey: a second KEY_PREPARE is refused while two generations are live`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 42)
    for _ in 1 .. 20 do pair.Round() |> ignore
    Assert.True(pair.Client.BeginRekey())
    let mutable n = 0
    while pair.Server.RxKeyCount < 2 && n < 200 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.Equal(2, pair.Server.RxKeyCount)
    let executed = pair.Server.CtrlExecuted
    let cut = pair.Client.RekeyCut + 4096UL
    // Next control_seq in the receive window after the first PREPARE (seq 1).
    pair.Server.OnFrame(0us, Span(ctrlCell pair.Client 2u CtrlOp.KeyPrepare (prepareBody 2u cut))) |> ignore
    Assert.Equal(executed, pair.Server.CtrlExecuted)
    Assert.True(pair.Server.CtrlRejected > 0L)
    Assert.Equal(2, pair.Server.RxKeyCount)
    Assert.Equal(1u, pair.Server.RxGeneration)

[<Fact>]
let ``rekey: CutCounter is not issued before KEY_READY and the last 256 numbers are reserved`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 43)
    for _ in 1 .. 20 do pair.Round() |> ignore
    let c = pair.Client
    let start = c.Tx.NextCounter
    // Same limiter startRekey uses: a short cut so the whole remainder is the CTRL reserve.
    c.Tx.SetCutLimit(start + 32UL, 256UL)
    Assert.False(c.Tx.CanSealPayload)
    Assert.True(c.Tx.CanSeal)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.CanSeal && n < 64 do
        c.Tick()
        c.TryProduce(0us, Span frame) |> ignore
        n <- n + 1
    Assert.True(c.Tx.NextCounter < start + 32UL || not c.Tx.CanSeal)
    Assert.True(c.Tx.NextCounter <= start + 32UL)
    Assert.False(c.Tx.CanSealPayload)
    // Production rekey: R_cut = 4096, reserve 256, payload still allowed at the start.
    let pair2 = Pair(SessionConfig.defaults, 1, 0.0, 44)
    let start2 = pair2.Client.Tx.NextCounter
    Assert.True(pair2.Client.BeginRekey())
    Assert.Equal(start2 + 4096UL, pair2.Client.RekeyCut)
    Assert.True(pair2.Client.Tx.CanSealPayload)
    Assert.True(pair2.Client.RekeyPending)

[<Fact>]
let ``rekey: 24 hours on a generation starts KEY_PREPARE`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 45)
    for _ in 1 .. 20 do pair.Round() |> ignore
    Assert.False(pair.Client.RekeyPending)
    pair.Clock.Now <- pair.Clock.Now + 24L * 3600L * 1_000_000_000L
    pair.Client.Tick()
    Assert.True(pair.Client.RekeyPending)
    Assert.True(pair.Client.RekeyCut > 0UL)
    Assert.Equal(pair.Client.Tx.NextCounter + 4096UL, pair.Client.RekeyCut)
    Assert.False(pair.Client.Faulted)

[<Fact>]
let ``path close: PATH_CLOSE and PATH_CLOSED complete, the path_id is not reused until then`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 46)
    for _ in 1 .. 20 do pair.Round() |> ignore
    Assert.True(pair.Client.BeginPathClose(1us, 5uy))
    Assert.Equal(PathCloseState.Closing, pair.Client.PathState 1us)
    Assert.False(pair.Client.NotePathJoined 1us)
    let mutable n = 0
    while (pair.Client.PathState 1us <> PathCloseState.Closed
           || pair.Server.PathState 1us <> PathCloseState.Closed) && n < 2000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.Equal(PathCloseState.Closed, pair.Client.PathState 1us)
    Assert.Equal(PathCloseState.Closed, pair.Server.PathState 1us)
    let frame = Array.zeroCreate<byte> pair.Client.FrameLength
    Assert.False(pair.Client.TryProduce(1us, Span frame))
    Assert.Equal(PathCloseState.Idle, pair.Client.PathState 0us)
    Assert.False(pair.Client.Faulted || pair.Server.Faulted)
    Assert.True(pair.Client.NotePathJoined 1us)
    Assert.Equal(PathCloseState.Idle, pair.Client.PathState 1us)
    // After a new join of the same id the path may send again.
    pair.Client.Tick()
    Assert.True(pair.Client.TryProduce(1us, Span frame) || not pair.Client.WantsToSend)

[<Fact>]
let ``path close: PATH_CLOSED for a path we did not close is not executed`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 47)
    let s = pair.Server
    let body = Array.zeroCreate<byte> 2
    BinaryPrimitives.WriteUInt16LittleEndian(Span body, 1us)
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.PathClosed body)) |> ignore
    Assert.Equal(1L, s.CtrlRejected)
    Assert.Equal(0L, s.CtrlExecuted)
    Assert.Equal(PathCloseState.Idle, s.PathState 1us)
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.SessionClose [| 1uy |])) |> ignore
    Assert.Equal(1L, s.CtrlExecuted)

[<Fact>]
let ``rekey: a lowered cell trigger completes PREPARE, READY and RETIRE`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 48)
    for _ in 1 .. 20 do pair.Round() |> ignore
    // Do not call BeginRekey: the automatic Tick path must start the transaction.
    pair.Client.SetRekeyTrigger(32UL, System.Int64.MaxValue)
    let n, slot = pump pair 20_000 (fun () ->
        pair.Client.TxKeyCount = 1 && pair.Client.TxGeneration = 1u
        && pair.Server.RxKeyCount = 1 && pair.Server.RxGeneration = 1u)
    Assert.True(n < 20_000, "auto-rekey did not finish in " + string n + " rounds")
    Assert.True(pair.Client.Tx.NextCounter >= pair.Client.RekeyCut)
    Assert.Equal(0u, pair.Server.TxGeneration)
    Assert.False(pair.Client.Faulted || pair.Server.Faulted)
    let more = Array.init 4_000 (fun i -> byte (i + 3))
    let mutable sent = 0
    let before = if pair.ServerPort.Data.ContainsKey slot then int pair.ServerPort.Data.[slot].Length else 0
    for _ in 1 .. 200 do
        sent <- sent + pair.Feed(pair.Client, slot, ReadOnlySpan(more, sent, more.Length - sent), false)
        pair.Round() |> ignore
    let after = if pair.ServerPort.Data.ContainsKey slot then int pair.ServerPort.Data.[slot].Length else 0
    Assert.True(after > before, "post-auto-rekey delivery stalled")
    Assert.Equal(0L, pair.Server.Opener.AuthFails)
