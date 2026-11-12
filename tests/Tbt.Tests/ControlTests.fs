module Tbt.Tests.ControlTests

open System
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
