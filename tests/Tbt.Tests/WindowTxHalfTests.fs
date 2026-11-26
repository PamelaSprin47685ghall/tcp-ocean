module Tbt.Tests.WindowTxHalfTests

open System
open Xunit
open Tbt.Codec
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Session
open Tbt.Wire

type private Clock() =
    member val Time = 1_000_000_000L with get, set
    interface IClock with
        member x.NowNs() = x.Time
        member x.FreshNs() = x.Time

let private makeWithClock () =
    let key = Array.init 32 byte
    let send = KeyTable()
    let recv = KeyTable()
    send.Install(0u, ReadOnlySpan key, 0UL)
    recv.Install(0u, ReadOnlySpan key, 0UL)
    let sealer = CellSealer(Direction.Up, 1024, send, NoHeaderMask())
    let opener = CellOpener(Direction.Up, 1024, recv, NoHeaderMask())
    let clock = Clock()
    let enc = WindowStepEncoder(1024, 16)
    let tx = TxHalf(Direction.Up, 1024, CodeField.Gf2, enc :> ICodecEncoder, sealer, 0.5, false, 0.0, 1.0, 0.0, 1.0, 64, 4, 128, clock, useWindowCodec = true)
    tx.InitialGrant 16UL
    tx, opener, clock

let private make () =
    let tx, opener, _ = makeWithClock ()
    tx, opener

let private add (tx: TxHalf) (value: byte) =
    let mutable slot = Span<byte>.Empty
    Assert.True(tx.TryBeginSource(&slot))
    slot.[Layout.EnvelopeLength] <- value
    tx.CommitSource({ RealLength = 1; FlowSlot = 1uy; Flags = StreamFlags.None; FlowGeneration = 1u; StreamOffset = 0UL }, false) |> ignore

let private openFrame (opener: CellOpener) (frame: byte[]) =
    let mutable counter = 0UL
    Assert.True(opener.TryOpen(Span frame, 0us, &counter))
    let mutable meta = Unchecked.defaultof<Meta>
    Assert.True(Meta.tryRead (ReadOnlySpan(frame, Layout.MetaOffset, Layout.MetaLength)) &meta)
    meta, counter

[<Fact>]
let ``encrypted window frames recover a missing whole step`` () =
    let tx, opener = make ()
    let decoder = WindowSnapshotDecoder(1024, 4, 1L <<< 20)
    let frame = Array.zeroCreate<byte> (Layout.frameLength 1024)
    // Partial step followed by a full step and a partial tail in the same window.
    for i = 0 to 2 do add tx (byte (10 + i))
    tx.Flush()
    for i = 0 to 15 do add tx (byte (30 + i))
    for i = 0 to 1 do add tx (byte (80 + i))
    tx.Flush()
    let mutable dataDropped = 0
    while tx.HasCandidate do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        let meta, _ = openFrame opener frame
        let symbol = ReadOnlySpan<byte>(frame, Layout.SymbolOffset, 1024)
        match meta.Type with
        | CellType.Data when meta.StepId = 1UL -> dataDropped <- dataDropped + 1
        | CellType.Data -> decoder.OfferSource(meta.StepId, int meta.Slot, int meta.StepUsed, symbol) |> ignore
        | CellType.Parity -> decoder.OfferWindowParity(meta, symbol) |> ignore
        | _ -> failwith "unexpected window frame"
    Assert.Equal(16, dataDropped)
    // Exhausted or rank-deficient parity is repaired by original DATA.
    tx.OnNack(1UL, 1, 16)
    let mutable attempts = 0
    while tx.HasCandidate && attempts < 128 do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        let meta, _ = openFrame opener frame
        let symbol = ReadOnlySpan<byte>(frame, Layout.SymbolOffset, 1024)
        match meta.Type with
        | CellType.Data -> decoder.OfferSource(meta.StepId, int meta.Slot, int meta.StepUsed, symbol) |> ignore
        | CellType.Parity -> decoder.OfferWindowParity(meta, symbol) |> ignore
        | _ -> failwith "unexpected repair frame"
        attempts <- attempts + 1
    let mutable step = 0UL
    let mutable slot = 0
    let mutable recovered = false
    let mutable count = 0
    while decoder.TryTakeKnown(&step, &slot, &recovered) do
        let expected = if step = 0UL then 10 + slot elif step = 1UL then 30 + slot else 80 + slot
        let actual = decoder.Source(step, slot)
        Assert.Equal(byte expected, actual.[Layout.EnvelopeLength])
        decoder.FinishKnown(step)
        count <- count + 1
    Assert.Equal(21, count)

[<Fact>]
let ``window parity carries snapshot and authenticated counters advance`` () =
    let tx, opener = make ()
    add tx 42uy
    tx.Flush()
    let frame = Array.zeroCreate<byte> (Layout.frameLength 1024)
    let mutable previous = 0UL
    let mutable first = true
    let mutable parity = false
    let mutable count = 0
    while tx.HasCandidate && count < 100 do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        let meta, counter = openFrame opener frame
        if not first then Assert.True(counter > previous)
        first <- false
        previous <- counter
        Assert.NotEqual(CellType.Booster, meta.Type)
        if meta.Type = CellType.Parity then
            parity <- true
            Assert.True(WindowBridge.trySnapshotParityMask meta |> ValueOption.isSome)
        count <- count + 1
    Assert.True(parity)

[<Fact>]
let ``nack retransmits data with a fresh counter and fill is a pad`` () =
    let tx, opener = make ()
    add tx 99uy
    tx.Flush()
    let frame = Array.zeroCreate<byte> (Layout.frameLength 1024)
    let mutable firstCounter = 0UL
    while tx.HasCandidate do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        let _, counter = openFrame opener frame
        firstCounter <- counter
    tx.OnNack(0UL, 1, 1)
    Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
    let meta, counter = openFrame opener frame
    Assert.Equal(CellType.Data, meta.Type)
    Assert.True(counter > firstCounter)
    Assert.Equal(99uy, frame.[Layout.SymbolOffset + Layout.EnvelopeLength])
    while tx.HasCandidate do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        openFrame opener frame |> ignore
    // The step is still unacknowledged, so an unused window parity is the useful
    // fill; PAD is only correct once no unacknowledged step can still be repaired.
    let unackedFill = tx.RenderFill(Span frame, 0us, 0UL, 16us, 0us)
    let unacked, _ = openFrame opener frame
    Assert.Equal(unackedFill, unacked.Type)
    if unackedFill = CellType.Parity then
        Assert.True(WindowBridge.trySnapshotParityMask unacked |> ValueOption.isSome)
    else
        Assert.Equal(CellType.Pad, unackedFill)
    tx.OnPeerAck(1UL, 16us)
    while tx.HasCandidate do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        openFrame opener frame |> ignore
    Assert.Equal(CellType.Pad, tx.RenderFill(Span frame, 0us, 0UL, 16us, 0us))
    let fill, _ = openFrame opener frame
    Assert.Equal(CellType.Pad, fill.Type)
    Assert.Equal(0u, fill.KHist)

[<Fact>]
let ``window tail timeout repairs without an RTT sample and stops after acknowledgement`` () =
    let tx, opener, clock = makeWithClock ()
    add tx 61uy
    tx.Flush()
    let frame = Array.zeroCreate<byte> (Layout.frameLength 1024)
    while tx.HasCandidate do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        openFrame opener frame |> ignore
    Assert.Equal(Int64.MaxValue, tx.MinRttNs)
    // The earlier heartbeat is a separate deadline; after sending it the
    // scheduler must expose the tail-repair deadline without an RTT sample.
    clock.Time <- 1_050_000_000L
    Assert.True(tx.HeartbeatDue)
    tx.RenderHeartbeat(Span frame, 0us, 0UL, 16us, 0us)
    openFrame opener frame |> ignore
    clock.Time <- 1_199_999_999L
    tx.Prepare()
    Assert.False(tx.RepairWaiting)
    Assert.True(tx.NextHeartbeatNs <= 1_200_000_000L)
    clock.Time <- 1_200_000_000L
    tx.Prepare()
    Assert.True(tx.RepairWaiting)
    Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
    let repaired, _ = openFrame opener frame
    Assert.Equal(CellType.Data, repaired.Type)
    Assert.Equal(0UL, repaired.StepId)
    Assert.Equal(61uy, frame.[Layout.SymbolOffset + Layout.EnvelopeLength])
    clock.Time <- 1_399_999_999L
    tx.Prepare()
    Assert.False(tx.RepairWaiting)
    tx.OnPeerAck(1UL, 16us)
    clock.Time <- 2_000_000_000L
    tx.Prepare()
    Assert.False(tx.RepairWaiting)
    Assert.Equal(Int64.MaxValue, tx.NextHeartbeatNs)

[<Fact>]
let ``exhausted window parity falls back to authenticated data repair`` () =
    let tx, opener = make ()
    add tx 73uy
    tx.Flush()
    let frame = Array.zeroCreate<byte> (Layout.frameLength 1024)
    let mutable previous = 0UL
    let mutable started = false
    let observe () =
        let meta, counter = openFrame opener frame
        if started then Assert.True(counter > previous, "counter must advance for every sealed frame")
        started <- true
        previous <- counter
        Assert.NotEqual(CellType.Booster, meta.Type)
        meta
    while tx.HasCandidate do
        Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
        observe () |> ignore
    let mutable exhausted = false
    let mutable fills = 0
    while not exhausted && fills < 49 do
        let kind = tx.RenderFill(Span frame, 0us, 0UL, 16us, 0us)
        let meta = observe ()
        Assert.Equal(kind, meta.Type)
        if kind = CellType.Pad then exhausted <- true
        else
            Assert.Equal(CellType.Parity, kind)
            Assert.True(WindowBridge.trySnapshotParityMask meta |> ValueOption.isSome)
        fills <- fills + 1
    Assert.True(exhausted, "48 parity indices must eventually be exhausted")
    tx.OnNack(0UL, 1, 1)
    Assert.True(tx.TryRenderNext(Span frame, 0us, 0UL, 16us, 0us))
    let repaired = observe ()
    Assert.Equal(CellType.Data, repaired.Type)
    Assert.Equal(0UL, repaired.StepId)
    Assert.Equal(0uy, repaired.Slot)
    Assert.Equal(73uy, frame.[Layout.SymbolOffset + Layout.EnvelopeLength])
