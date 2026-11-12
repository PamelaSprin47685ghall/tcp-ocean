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
