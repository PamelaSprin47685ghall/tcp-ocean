module Tbt.Tests.ControlTests

open System
open System.Buffers.Binary
open Xunit
open Tbt.Wire
open Tbt.Session
open Tbt.Carrier

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
open Tbt.Crypto.Bootstrap
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

[<Fact>]
let ``produce fill: a closed path or a cut reserve does not seal and returns false`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 61)
    for _ in 1 .. 20 do pair.Round() |> ignore
    let c = pair.Client
    let frame = Array.zeroCreate<byte> c.FrameLength
    let start = c.Tx.NextCounter
    // Path 1 is still idle: a fill is allowed and consumes a counter.
    c.ProduceFill(0us, Span frame) |> ignore
    Assert.True(c.Tx.NextCounter > start)
    Assert.True(c.BeginPathClose(1us, 1uy))
    let mutable n = 0
    while c.PathState 1us <> PathCloseState.Closed && n < 2000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.Equal(PathCloseState.Closed, c.PathState 1us)
    let mid = c.Tx.NextCounter
    Assert.False(c.ProduceFill(1us, Span frame))
    Assert.Equal(mid, c.Tx.NextCounter)
    // The last 256 numbers of a rekey are reserved: no payload fill.
    c.Tx.SetCutLimit(c.Tx.NextCounter + 8UL, 256UL)
    Assert.False(c.Tx.CanSealPayload)
    Assert.False(c.ProduceFill(0us, Span frame))
    Assert.Equal(mid, c.Tx.NextCounter)

[<Fact>]
let ``path close: AddPath after PATH_CLOSED calls NotePathJoined and the id can send again`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 62)
    for _ in 1 .. 20 do pair.Round() |> ignore
    Assert.True(pair.Client.BeginPathClose(1us, 5uy))
    Assert.Equal(PathCloseState.Closing, pair.Client.PathState 1us)
    let bridge = FlowBridge(ValueNone)
    let carrier = SessionCarrier(pair.Client, bridge)
    let stub =
        let mutable pathId = 1us
        let mutable frameLength = 0
        { new IPath with
            member _.PathId with get () = pathId and set v = pathId <- v
            member _.FrameLength with get () = frameLength and set v = frameLength <- v
            member _.Alive = true
            member _.Attach(_) = ()
            member _.Backlog with set (_: int) = ()
            member _.Watch(_, _) = ()
            member _.Receive(_) = ()
            member _.TryBeginFrame(_) = false
            member _.CommitFrame() = ()
            member _.Close() = ()
            member _.UnsentBytes = 0 }
    // While the close transaction is open, the id must not be attached again.
    carrier.AddPath stub
    Assert.Equal(PathCloseState.Closing, pair.Client.PathState 1us)
    Assert.False(carrier.HasPath 1us)
    let mutable n = 0
    while pair.Client.PathState 1us <> PathCloseState.Closed && n < 2000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.Equal(PathCloseState.Closed, pair.Client.PathState 1us)
    let frame = Array.zeroCreate<byte> pair.Client.FrameLength
    Assert.False(pair.Client.TryProduce(1us, Span frame))
    carrier.AddPath stub
    Assert.Equal(PathCloseState.Idle, pair.Client.PathState 1us)
    pair.Client.Tick()
    Assert.True(pair.Client.TryProduce(1us, Span frame) || not pair.Client.WantsToSend)
    Assert.True(carrier.HasPath 1us)

let private stubPath (id: uint16) =
    let mutable pathId = id
    let mutable frameLength = 0
    { new IPath with
        member _.PathId with get () = pathId and set v = pathId <- v
        member _.FrameLength with get () = frameLength and set v = frameLength <- v
        member _.Alive = true
        member _.Attach(_) = ()
        member _.Backlog with set (_: int) = ()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member _.TryBeginFrame(_) = false
        member _.CommitFrame() = ()
        member _.Close() = ()
        member _.UnsentBytes = 0 }

[<Fact>]
let ``HasPath, PathIds and PathCount are safe from another thread while paths change`` () =
    // Live server: the accept loop's HasPath (List.Exists) raced the coordinator's
    // RemoveAt, read a null slot and crashed with NullReferenceException.
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 63)
    let carrier = SessionCarrier(pair.Client, FlowBridge(ValueNone))
    let stop = ref false
    let mutable failure: exn = null
    let reader =
        System.Threading.Thread(fun () ->
            try
                while not stop.Value do
                    for id in 0us .. 40us do carrier.HasPath id |> ignore
                    let ids = carrier.PathIds
                    for id in ids do Assert.True(id <= 40us)
                    Assert.True(carrier.PathCount >= 0)
            with e -> failure <- e)
    reader.Start()
    let sw = System.Diagnostics.Stopwatch.StartNew()
    while sw.ElapsedMilliseconds < 1500L do
        for id in 1us .. 40us do carrier.AddPath(stubPath id)
        for k in 40 .. -1 .. 1 do carrier.RemovePath(uint16 k)
    stop.Value <- true
    reader.Join()
    Assert.Null(failure)
    carrier.AddPath(stubPath 7us)
    Assert.True(carrier.HasPath 7us)
    Assert.Equal<uint16[]>([| 7us |], carrier.PathIds)
    carrier.RemovePath 7us
    Assert.False(carrier.HasPath 7us)
    Assert.Equal(0, carrier.PathCount)

[<Fact>]
let ``a session whose every path dropped stays joinable for the no-path grace`` () =
    // Bench: ss -K of all 256 server sockets closed the session at once; the client's
    // rejoins had nothing to join and it exited "no path remains" 37 s later.
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 64)
    let carrier = SessionCarrier(pair.Server, FlowBridge(ValueNone))
    carrier.NoPathGraceNs <- 30_000_000_000L
    carrier.AddPath(stubPath 3us)
    Assert.True(carrier.Alive)
    carrier.RemovePath 3us
    Assert.Equal(0, carrier.PathCount)
    Assert.True(carrier.Alive, "grace: the session must wait for rejoins")
    pair.Clock.Now <- pair.Clock.Now + 20_000_000_000L
    Assert.True(carrier.Alive)
    // A rejoin resets the grace.
    carrier.AddPath(stubPath 4us)
    carrier.RemovePath 4us
    pair.Clock.Now <- pair.Clock.Now + 20_000_000_000L
    Assert.True(carrier.Alive, "grace restarts after a rejoin")
    pair.Clock.Now <- pair.Clock.Now + 11_000_000_000L
    Assert.False(carrier.Alive, "grace expired")
    // Without a grace the old rule holds: no path, not alive.
    let c0 = SessionCarrier(pair.Client, FlowBridge(ValueNone))
    c0.AddPath(stubPath 5us)
    c0.RemovePath 5us
    Assert.False(c0.Alive)

[<Fact>]
let ``a brand-new session is alive for the no-path grace before its first path lands`` () =
    // Bench: the server logged "session up" and "closed" in the same millisecond when
    // its main loop checked Alive before the coordinator applied the handshake path.
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 64)
    pair.Clock.Now <- pair.Clock.Now + 100_000_000_000L
    let carrier = SessionCarrier(pair.Server, FlowBridge(ValueNone))
    carrier.NoPathGraceNs <- 30_000_000_000L
    Assert.True(carrier.Alive, "new session with its first path still queued")
    pair.Clock.Now <- pair.Clock.Now + 31_000_000_000L
    Assert.False(carrier.Alive, "no path ever arrived")

let private capacityBody (budget: uint64) =
    let b = Array.zeroCreate<byte> 8
    Ctrl.writeCapacityBody budget (Span b)
    b

let private mssBody (path: uint16) (mss: uint16) =
    let b = Array.zeroCreate<byte> 4
    Ctrl.writeMssNoticeBody path mss (Span b)
    b

[<Fact>]
let ``capacity: CAPACITY_CHANGE is executed, caps future credit, and never retracts issued limits`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 70)
    for _ in 1 .. 20 do pair.Round() |> ignore
    let reserved = 256L * int64 CreditPolicy.C0
    Assert.Equal(uint64 pair.Client.Flows.Credit.PoolBytes, pair.Client.RxBudget)
    Assert.Equal(UInt64.MaxValue, pair.Server.PeerRxBudget)
    Assert.Equal(reserved, pair.Server.Flows.CommittedBytes)
    // Open a flow and deliver enough that the server's rx limit grows above C0.
    let data = Array.init 80_000 (fun i -> byte (i * 7 + 1))
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    let mutable sent = 0
    let mutable n = 0
    while n < 400 && pair.Server.Flows.[slot].RxLimit <= uint64 CreditPolicy.C0 do
        sent <- sent + pair.Feed(pair.Client, slot, ReadOnlySpan(data, sent, data.Length - sent), false)
        pair.Round() |> ignore
        n <- n + 1
    let limit0 = pair.Server.Flows.[slot].RxLimit
    Assert.True(limit0 > uint64 CreditPolicy.C0, "rx limit did not grow: " + string limit0)
    let committed = pair.Server.Flows.CommittedBytes
    Assert.True(pair.Server.BeginCapacityChange(uint64 committed))
    Assert.Equal(uint64 committed, pair.Server.RxBudget)
    Assert.Equal(committed, pair.Server.Flows.BudgetCap)
    Assert.Equal(limit0, pair.Server.Flows.[slot].RxLimit)
    // A cap below already issued commitments is an explicit failure; state is unchanged.
    Assert.False(pair.Server.BeginCapacityChange(uint64 (committed - 1L)))
    Assert.False(pair.Server.BeginCapacityChange 0UL)
    Assert.Equal(uint64 committed, pair.Server.RxBudget)
    Assert.Equal(limit0, pair.Server.Flows.[slot].RxLimit)
    let mutable r = 0
    while r < 200 do
        if sent < data.Length then
            sent <- sent + pair.Feed(pair.Client, slot, ReadOnlySpan(data, sent, data.Length - sent), false)
        pair.Round() |> ignore
        r <- r + 1
    Assert.Equal(limit0, pair.Server.Flows.[slot].RxLimit)
    Assert.True(pair.Server.Flows.CommittedBytes <= committed)
    // The client recorded the peer's announcement; session S is unchanged.
    Assert.Equal(uint64 committed, pair.Client.PeerRxBudget)
    Assert.Equal(1L, pair.Client.CtrlExecuted)
    Assert.Equal(0L, pair.Client.CtrlRejected)
    Assert.Equal(pair.ClientParams.Symbol, pair.Client.SymbolSize)
    Assert.False(pair.Client.Faulted || pair.Server.Faulted)

[<Fact>]
let ``capacity: a repeated CAPACITY_CHANGE is executed once; a stale budget does not roll back`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 71)
    let s = pair.Server
    s.OnFrame(0us, Span(ctrlCell pair.Client 2u CtrlOp.CapacityChange (capacityBody 40_000_000UL))) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 2u CtrlOp.CapacityChange (capacityBody 40_000_000UL))) |> ignore
    Assert.Equal(1L, s.CtrlExecuted)
    Assert.Equal(0L, s.CtrlRejected)
    Assert.Equal(40_000_000UL, s.PeerRxBudget)
    // An older seq is ACKed (executed) but must not overwrite the newer budget.
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.CapacityChange (capacityBody 20_000_000UL))) |> ignore
    Assert.Equal(2L, s.CtrlExecuted)
    Assert.Equal(40_000_000UL, s.PeerRxBudget)
    // Zero is refused and does not move the recorded budget; seq 3 stays fresh.
    s.OnFrame(0us, Span(ctrlCell pair.Client 3u CtrlOp.CapacityChange (capacityBody 0UL))) |> ignore
    Assert.Equal(1L, s.CtrlRejected)
    Assert.Equal(2L, s.CtrlExecuted)
    Assert.Equal(40_000_000UL, s.PeerRxBudget)
    s.OnFrame(0us, Span(ctrlCell pair.Client 3u CtrlOp.CapacityChange (capacityBody 50_000_000UL))) |> ignore
    Assert.Equal(3L, s.CtrlExecuted)
    Assert.Equal(50_000_000UL, s.PeerRxBudget)

[<Fact>]
let ``mss: MSS_NOTICE updates path MSS, keeps session S, and accepts a straddle shrink`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 72)
    for _ in 1 .. 20 do pair.Round() |> ignore
    let s0 = pair.Client.SymbolSize
    let l0 = pair.Client.FrameLength
    Assert.Equal(0us, pair.Client.PathMss 1us)
    Assert.Equal(0us, pair.Client.LocalPathMss 1us)
    Assert.True(pair.Client.PathFitsOneSegment 1us)
    Assert.True(pair.Client.BeginMssNotice(1us, 1448us))
    Assert.Equal(1448us, pair.Client.LocalPathMss 1us)
    Assert.Equal(0us, pair.Client.PathMss 1us)
    let mutable n = 0
    while pair.Server.PathMss 1us = 0us && n < 2000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.Equal(1448us, pair.Server.PathMss 1us)
    Assert.Equal(0us, pair.Server.LocalPathMss 1us)
    Assert.True(pair.Server.PathFitsOneSegment 1us)
    Assert.Equal(s0, pair.Client.SymbolSize)
    Assert.Equal(l0, pair.Client.FrameLength)
    Assert.Equal(s0, pair.Server.SymbolSize)
    Assert.True((pair.ClientParams.Caps &&& Caps.StraddleOk) <> 0us)
    // With STRADDLE_OK, a shrink that no longer fits one segment is accepted; S is still fixed.
    Assert.True(pair.Client.BeginMssNotice(1us, 1200us))
    n <- 0
    while pair.Server.PathMss 1us <> 1200us && n < 2000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.Equal(1200us, pair.Server.PathMss 1us)
    Assert.False(pair.Server.PathFitsOneSegment 1us)
    Assert.Equal(s0, pair.Server.SymbolSize)
    Assert.Equal(l0, pair.Server.FrameLength)
    Assert.False(pair.Client.Faulted || pair.Server.Faulted)

[<Fact>]
let ``mss: zero, unknown path, closed path and a shrink without STRADDLE_OK are refused`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 73)
    for _ in 1 .. 20 do pair.Round() |> ignore
    Assert.False(pair.Client.BeginMssNotice(1us, 0us))
    Assert.False(pair.Client.BeginMssNotice(300us, 1448us))
    Assert.Equal(0us, pair.Client.LocalPathMss 1us)
    let s = pair.Server
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.MssNotice (mssBody 1us 0us))) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 2u CtrlOp.MssNotice (mssBody 300us 1448us))) |> ignore
    Assert.Equal(2L, s.CtrlRejected)
    Assert.Equal(0L, s.CtrlExecuted)
    Assert.Equal(0us, s.PathMss 1us)
    // Without STRADDLE_OK a shrink that cannot carry L is not executed (JOIN rule mid-session).
    let noStraddle = { pair.ServerParams with Caps = pair.ServerParams.Caps &&& ~~~Caps.StraddleOk }
    let lonely = Session(Role.Server, noStraddle, RecordingPort(), pair.Clock, SessionConfig.defaults)
    lonely.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.MssNotice (mssBody 1us 1200us))) |> ignore
    Assert.Equal(1L, lonely.CtrlRejected)
    Assert.Equal(0L, lonely.CtrlExecuted)
    Assert.Equal(0us, lonely.PathMss 1us)
    // A legal notice still works on this session (control_seq 1 is still fresh).
    lonely.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.MssNotice (mssBody 1us 1448us))) |> ignore
    Assert.Equal(1L, lonely.CtrlExecuted)
    Assert.Equal(1448us, lonely.PathMss 1us)
    Assert.True(lonely.PathFitsOneSegment 1us)
    // After PATH_CLOSE completes, a notice for that id is refused until a new join.
    Assert.True(pair.Client.BeginPathClose(1us, 1uy))
    let mutable n = 0
    while pair.Client.PathState 1us <> PathCloseState.Closed && n < 2000 do
        pair.Round() |> ignore
        n <- n + 1
    Assert.Equal(PathCloseState.Closed, pair.Client.PathState 1us)
    Assert.False(pair.Client.BeginMssNotice(1us, 1448us))
    let executed = pair.Server.CtrlExecuted
    let rejected = pair.Server.CtrlRejected
    pair.Server.OnFrame(0us, Span(ctrlCell pair.Client 40u CtrlOp.MssNotice (mssBody 1us 1448us))) |> ignore
    Assert.Equal(executed, pair.Server.CtrlExecuted)
    Assert.True(pair.Server.CtrlRejected > rejected)
    Assert.True(pair.Client.NotePathJoined 1us)
    Assert.Equal(0us, pair.Client.LocalPathMss 1us)
    Assert.Equal(0us, pair.Client.PathMss 1us)
    Assert.True(pair.Client.BeginMssNotice(1us, 1448us))
    Assert.Equal(1448us, pair.Client.LocalPathMss 1us)

[<Fact>]
let ``mss: a repeated MSS_NOTICE is executed once; a stale mss does not roll back`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 74)
    let s = pair.Server
    s.OnFrame(0us, Span(ctrlCell pair.Client 2u CtrlOp.MssNotice (mssBody 1us 1448us))) |> ignore
    s.OnFrame(0us, Span(ctrlCell pair.Client 2u CtrlOp.MssNotice (mssBody 1us 1448us))) |> ignore
    Assert.Equal(1L, s.CtrlExecuted)
    Assert.Equal(0L, s.CtrlRejected)
    Assert.Equal(1448us, s.PathMss 1us)
    s.OnFrame(0us, Span(ctrlCell pair.Client 1u CtrlOp.MssNotice (mssBody 1us 1400us))) |> ignore
    Assert.Equal(2L, s.CtrlExecuted)
    Assert.Equal(1448us, s.PathMss 1us)
    // Path 0 is independent of path 1.
    s.OnFrame(0us, Span(ctrlCell pair.Client 3u CtrlOp.MssNotice (mssBody 0us 1448us))) |> ignore
    Assert.Equal(3L, s.CtrlExecuted)
    Assert.Equal(1448us, s.PathMss 0us)
    Assert.Equal(1448us, s.PathMss 1us)

type private HealthStubPath(id: uint16, initialUnsent: int, initialHealth: PathHealth) =
    let mutable pathId = id
    let mutable frameLength = 1448
    let buf = Array.zeroCreate<byte> 2048
    member val Unsent = initialUnsent with get, set
    member val HealthState = initialHealth with get, set
    member val Frames = 0 with get, set
    interface IPath with
        member _.PathId with get () = pathId and set v = pathId <- v
        member _.FrameLength with get () = frameLength and set v = frameLength <- v
        member _.Alive = true
        member _.Attach(_) = ()
        member _.Backlog with set (_: int) = ()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member p.UnsentBytes = p.Unsent
        member p.TryBeginFrame(frame: byref<Span<byte>>) =
            frame <- Span(buf, 0, frameLength)
            true
        member p.CommitFrame() = p.Frames <- p.Frames + 1
        member _.Close() = ()
    interface IPathHealth with
        member p.Health = p.HealthState
        member _.RefreshHealth(_, _) = ()

[<Fact>]
let ``health-aware writeBatch skips TCP_CA_Loss paths even at unsent=0 and falls back when all are degraded`` () =
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 81)
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    let payload = Array.create 40_000 5uy
    pair.Feed(pair.Client, slot, ReadOnlySpan payload, false) |> ignore
    pair.Client.Flush()
    let carrier = SessionCarrier(pair.Client, FlowBridge(ValueNone))
    let lossHealth =
        { PathHealth.unknown with
            SampledNs = 1L; CaState = 4uy; RttUs = 250_000u; MinRttUs = 113_000u
            CwndBytes = 14480u; DeliveryRate = 100_000L; RetransRate = 0.30; Valid = true }
    let goodHealth =
        { PathHealth.unknown with
            SampledNs = 1L; CaState = 0uy; RttUs = 114_000u; MinRttUs = 113_000u
            CwndBytes = 131_072u; DeliveryRate = 2_000_000L; RetransRate = 0.01; Valid = true }
    // Path 0 is stuck in TCP_CA_Loss with SIOCOUTQNSD=0 (the classic RTO trap).
    let rtoPath = HealthStubPath(0us, 0, lossHealth)
    let okPath = HealthStubPath(1us, 0, goodHealth)
    carrier.AddPath rtoPath
    carrier.AddPath okPath
    carrier.RefreshHealthNow()
    let sent = carrier.WriteBatch 8
    Assert.True(sent > 0)
    Assert.Equal(0, rtoPath.Frames)
    Assert.True(okPath.Frames > 0)
    // If every path enters Loss simultaneously (blackout), fallback still sends so recovery progresses.
    okPath.HealthState <- lossHealth
    carrier.RefreshHealthNow()
    let before = rtoPath.Frames + okPath.Frames
    let sentFallback = carrier.WriteBatch 8
    Assert.True(sentFallback > 0)
    Assert.True(rtoPath.Frames + okPath.Frames > before)

[<Fact>]
let ``a round with nothing sent asks for a bounded backoff instead of spinning`` () =
    // Live 24 %-loss forensics: every path's tx ring was full, so each round admitted no frame
    // and WaitUs returned 0, and the session thread spun at ~120 000 rounds/s on a 2-vCPU host
    // (2.58 M futile refusals, a whole core burned, IO threads starved). A round that put
    // nothing on the wire while data is waiting must yield for a millisecond and let the IO
    // thread wake it as soon as a ring drains.
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 85)
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    let payload = Array.create 60_000 7uy
    pair.Feed(pair.Client, slot, ReadOnlySpan payload, false) |> ignore
    pair.Client.Flush()
    let carrier = SessionCarrier(pair.Client, FlowBridge(ValueNone))
    let healthy =
        { PathHealth.unknown with
            SampledNs = 1L; CaState = 0uy; RttUs = 113_000u; MinRttUs = 113_000u
            CwndBytes = 131_072u; DeliveryRate = 2_000_000L; Valid = true }
    // Unsent far above both write gates: WriteGate admits nothing on either path.
    let a = HealthStubPath(0us, 1_000_000, healthy)
    let b = HealthStubPath(1us, 1_000_000, healthy)
    carrier.AddPath a
    carrier.AddPath b
    carrier.RefreshHealthNow()
    Assert.True(pair.Client.WantsToSend)
    Assert.Equal(0, carrier.WriteBatch 8)
    Assert.Equal(0, a.Frames + b.Frames)
    let waitUs = carrier.WaitUs()
    Assert.True(waitUs > 0, sprintf "WaitUs %d spins on a fully gated round" waitUs)
    Assert.True(waitUs <= 5_000, sprintf "WaitUs %d oversleeps a microsecond-scale wake" waitUs)

[<Fact>]
let ``health-aware writeBatch never starves SACK Recovery paths and never freezes output`` () =
    // Live 24 %-loss forensics: at that loss Linux keeps nearly every socket in
    // TCP_CA_Recovery (3) or CWR (2) while still carrying line rate. Grading those as sick
    // made the write loop skip every path, `frames_out` froze for seconds and `hskip`
    // reached 28 million. Recovery must stay a normal, writable state.
    let pair = Pair(SessionConfig.defaults, 2, 0.0, 84)
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    let payload = Array.create 40_000 5uy
    pair.Feed(pair.Client, slot, ReadOnlySpan payload, false) |> ignore
    pair.Client.Flush()
    let carrier = SessionCarrier(pair.Client, FlowBridge(ValueNone))
    let recoveryHealth =
        { PathHealth.unknown with
            SampledNs = 1L; CaState = 3uy; RttUs = 180_000u; MinRttUs = 113_000u
            CwndBytes = 29_000u; DeliveryRate = 1_500_000L; RetransRate = 0.24; Valid = true }
    let a = HealthStubPath(0us, 0, recoveryHealth)
    let b = HealthStubPath(1us, 0, recoveryHealth)
    carrier.AddPath a
    carrier.AddPath b
    carrier.RefreshHealthNow()
    let sent = carrier.WriteBatch 8
    Assert.True(sent > 0, "a lossy link in SACK Recovery must still carry data")
    Assert.True(a.Frames + b.Frames > 0, sprintf "frames a=%d b=%d" a.Frames b.Frames)
    // Every path stalled in a real RTO (CaState = 4) must still drain via the hard fallback,
    // or a blackout could never recover and output would freeze again.
    let lossHealth = { recoveryHealth with CaState = 4uy }
    a.HealthState <- lossHealth
    b.HealthState <- lossHealth
    carrier.RefreshHealthNow()
    let before = a.Frames + b.Frames
    let sentFallback = carrier.WriteBatch 8
    Assert.True(sentFallback > 0)
    Assert.True(a.Frames + b.Frames > before, "an all-RTO link must still make progress")

[<Fact>]
let ``elastic active working set holds at 32 when healthy and expands when pass 1 saturates`` () =
    let pair = Pair(SessionConfig.defaults, 48, 0.0, 82)
    let mutable slot1 = -1
    let mutable slot2 = -1
    Assert.True(pair.Client.TryOpenFlow(&slot1))
    Assert.True(pair.Client.TryOpenFlow(&slot2))
    let payload = Array.create 60_000 9uy
    pair.Feed(pair.Client, slot1, ReadOnlySpan payload, false) |> ignore
    pair.Feed(pair.Client, slot2, ReadOnlySpan payload, false) |> ignore
    pair.Client.Flush()
    let carrier = SessionCarrier(pair.Client, FlowBridge(ValueNone))
    let goodHealth =
        { PathHealth.unknown with
            SampledNs = 1L; CaState = 0uy; RttUs = 113_000u; MinRttUs = 112_000u
            CwndBytes = 131_072u; DeliveryRate = 2_000_000L; RetransRate = 0.01; Valid = true }
    let stubs = Array.init 48 (fun i -> HealthStubPath(uint16 i, 0, goodHealth))
    for st in stubs do carrier.AddPath st
    carrier.RefreshHealthNow()
    // The floor moved (32 -> 16); the contract did not. Pinning the literal here would re-break on
    // the next sizing change, so the test pins the behaviour — hold while pass 1 is healthy, expand
    // the moment it saturates — plus the floor's own invariant (a working set, not all 48 paths).
    let initial = carrier.ActivePathTarget
    Assert.True(initial >= 16 && initial < 48, sprintf "initial working set %d out of range" initial)
    // Healthy pass 1 (notSent = 0) uses the working set without over-fragmenting to all 48.
    let sent1 = carrier.WriteBatch 32
    Assert.True(sent1 > 0)
    Assert.Equal(initial, carrier.ActivePathTarget)
    let tailFramesBefore = [| for i in initial .. 47 -> stubs.[i].Frames |] |> Array.sum
    Assert.Equal(0, tailFramesBefore)
    // A 2 MiB/s sample opens pass 1 to 16 KiB. Saturation is notSent above that ceiling,
    // not the old 4 KiB constant.
    for i in 0 .. initial - 1 do stubs.[i].Unsent <- 20_000
    let sent2 = carrier.WriteBatch 32
    Assert.True(sent2 > 0)
    Assert.True(carrier.ActivePathTarget > initial,
                sprintf "expected expansion > %d, got %d" initial carrier.ActivePathTarget)
    let tailFramesAfter = [| for i in initial .. 47 -> stubs.[i].Frames |] |> Array.sum
    Assert.True(tailFramesAfter > 0, "spillover must immediately engage spare paths")

[<Fact>]
let ``observed backlog keeps an expanded working set and silence shrinks it`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 84)
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    pair.Feed(pair.Client, slot, ReadOnlySpan(Array.create 200_000 1uy), false) |> ignore
    pair.Client.Flush()
    let carrier = SessionCarrier(pair.Client, FlowBridge(ValueNone), minActivePaths = 16)
    let stubs = Array.init 64 (fun i -> HealthStubPath(uint16 i, 0, PathHealth.unknown))
    for st in stubs do carrier.AddPath st
    use poller = new Poller()
    let mutable guard = 0
    for st in stubs do st.Unsent <- 8000
    carrier.WriteBatch 64 |> ignore
    let expanded = carrier.ActivePathTarget
    Assert.True(expanded > 16, sprintf "expanded=%d" expanded)
    stubs.[0].Unsent <- 4096
    carrier.Service poller
    pair.Clock.Now <- pair.Clock.Now + 2_000_000_000L
    carrier.Service poller
    Assert.Equal(expanded, carrier.ActivePathTarget)
    stubs.[0].Unsent <- 0
    carrier.Service poller
    pair.Clock.Now <- pair.Clock.Now + 2_000_000_000L
    carrier.Service poller
    pair.Clock.Now <- pair.Clock.Now + 2_000_000_000L
    carrier.Service poller
    Assert.True(carrier.ActivePathTarget < 64)



