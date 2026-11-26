module Tbt.Tests.WindowCustodyTests

open System
open Xunit
open Tbt.Codec
open Tbt.Session

[<Fact>]
let ``parity snapshot exposes missing step and delivers recovered symbols once`` () =
    let tx = WindowTxStore(32, 1)
    let a = Array.init 32 byte
    let b = Array.init 32 (fun i -> byte (i + 32))
    let c = Array.init 32 (fun i -> byte (i + 64))
    Assert.True(tx.TryWrite(0UL, 0, ReadOnlySpan a))
    Assert.True(tx.TryWrite(0UL, 1, ReadOnlySpan b))
    Assert.True(tx.SealStep(0UL, 2))
    Assert.True(tx.TryWrite(1UL, 0, ReadOnlySpan c))
    Assert.True(tx.SealStep(1UL, 1))
    let rx = WindowCustody(32, 1, 10000L)
    rx.NextGrant() |> ignore
    Assert.True(rx.TryStepUsed(1UL).IsNone)
    Assert.True(rx.MissingKnown(1UL).IsNone)
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(0UL, 0, 2, a))
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(0UL, 1, 2, b))
    let parity = Array.zeroCreate<byte> 32
    let mutable index = 0
    while rx.MissingKnown(1UL) <> ValueSome 0us && index < 48 do
        let meta = tx.TryRenderParity(0UL, index, Span parity).Value
        rx.AddParity(meta, parity) |> ignore
        index <- index + 1
    Assert.Equal(ValueSome 1, rx.TryStepUsed(1UL))
    Assert.Equal(ValueSome 0us, rx.MissingKnown(1UL))
    let seen = ResizeArray<struct(uint64 * int)>()
    let deliver = Action<uint64, int>(fun step slot -> seen.Add(struct(step, slot)))
    Assert.Equal(3, rx.DeliverAvailable(0UL, deliver))
    Assert.Equal(0, rx.DeliverAvailable(0UL, deliver))
    Assert.Contains(struct(1UL, 0), seen)
    Assert.True((rx :> ISourceStore).Source(1UL, 0).SequenceEqual(ReadOnlySpan c))

[<Fact>]
let ``custody retains source while referenced and refuses old frames after release`` () =
    let rx = WindowCustody(32, 2, 1L <<< 20)
    Assert.Equal(8us, rx.NextGrant())
    for step in 0UL .. 3UL do
        let bytes = Array.create 32 (byte (step + 1UL))
        Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(step, 0, 1, bytes))
        Assert.True(rx.TakeKnown(step, 0))
        Assert.False(rx.TakeKnown(step, 0))
        rx.StepUndelivered(step, 1)
    rx.Advance()
    Assert.Equal(4UL, rx.DecodedFrontier)
    Assert.Equal(0UL, rx.CustodyFrontier)
    Assert.True((rx :> ISourceStore).Source(0UL, 0).SequenceEqual(ReadOnlySpan(Array.create 32 1uy)))
    for step in 0UL .. 3UL do rx.StepUndelivered(step, -1)
    rx.Advance()
    Assert.Equal(4UL, rx.CustodyFrontier)
    // Advancing custody alone must not authorize newly opened steps.
    Assert.Equal(8UL, rx.PublishedLimit)
    Assert.Equal(WindowCodec.AddOutcome.Invalid, rx.AddSource(8UL, 0, 1, Array.zeroCreate 32))
    Assert.Equal(8us, rx.NextGrant())
    Assert.Equal(12UL, rx.PublishedLimit)
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(8UL, 0, 1, Array.zeroCreate 32))
    Assert.True((rx :> ISourceStore).Source(0UL, 0).IsEmpty)
    Assert.Equal(WindowCodec.AddOutcome.Stale, rx.AddSource(0UL, 0, 1, Array.zeroCreate 32))

[<Fact>]
let ``custody accepts sliced spans with the same grant and conflict rules`` () =
    let rx = WindowCustody(32, 1, 10000L)
    let bytes = Array.init 40 byte
    let slice = ReadOnlySpan<byte>(bytes, 4, 32)
    Assert.Equal(WindowCodec.AddOutcome.Invalid, rx.AddSource(0UL, 0, 1, slice))
    rx.NextGrant() |> ignore
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(0UL, 0, 1, slice))
    let struct (meta, _) = WindowBridge.snapshotParity 0UL 0 [| 1 |]
    Assert.Equal(WindowCodec.AddOutcome.Duplicate, rx.AddParity(meta, slice))
    bytes.[4] <- bytes.[4] ^^^ 1uy
    Assert.Equal(WindowCodec.AddOutcome.Invalid, rx.AddParity(meta, slice))
    Assert.Equal(WindowCodec.AddOutcome.Invalid, rx.AddSource(0UL, 0, 1, Unchecked.defaultof<byte[]>))
    Assert.True((rx :> ISourceStore).Source(0UL, 0).SequenceEqual(ReadOnlySpan<byte>(Array.init 32 (fun i -> byte (i + 4)))))

[<Fact>]
let ``missing slots track registered sources without inventing unknown step lengths`` () =
    let rx = WindowCustody(32, 1, 10000L)
    rx.NextGrant() |> ignore
    Assert.Equal(ValueNone, rx.Missing 0UL)
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(0UL, 1, 3, Array.create 32 1uy))
    Assert.True(rx.TakeKnown(0UL, 1))
    Assert.Equal(ValueSome 5us, rx.Missing 0UL)
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(0UL, 0, 3, Array.create 32 2uy))
    Assert.True(rx.TakeKnown(0UL, 0))
    Assert.Equal(ValueSome 4us, rx.Missing 0UL)

[<Fact>]
let ``reentrant advance during delivery retains the borrowed window`` () =
    let rx = WindowCustody(32, 1, 10000L)
    rx.NextGrant() |> ignore
    for step in 0UL .. 2UL do
        Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(step, 0, 1, Array.create 32 (byte (step + 1UL))))
        Assert.True(rx.TakeKnown(step, 0))
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(3UL, 0, 1, Array.create 32 4uy))
    let deliver = Action<uint64, int>(fun step slot ->
        let borrowed = (rx :> ISourceStore).Source(step, slot)
        rx.Advance()
        Assert.Equal(3UL, rx.CustodyFrontier)
        Assert.True(borrowed.SequenceEqual(ReadOnlySpan(Array.create 32 4uy))))
    Assert.True(rx.DeliverKnown(3UL, 0, deliver))
    rx.Advance()
    Assert.Equal(4UL, rx.CustodyFrontier)
    Assert.True((rx :> ISourceStore).Source(3UL, 0).IsEmpty)

[<Fact>]
let ``batch delivery defers reentrant advance until every slot is registered`` () =
    let rx = WindowCustody(32, 1, 10000L)
    rx.NextGrant() |> ignore
    for step in 0UL .. 3UL do
        Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(step, 0, 1, Array.create 32 (byte (step + 1UL))))
    let seen = ResizeArray<uint64>()
    let deliver = Action<uint64, int>(fun step slot ->
        seen.Add step
        rx.Advance()
        Assert.False((rx :> ISourceStore).Source(step, slot).IsEmpty))
    Assert.Equal(4, rx.DeliverAvailable(0UL, deliver))
    Assert.Equal<uint64[]>([| 0UL; 1UL; 2UL; 3UL |], seen.ToArray())
    Assert.Equal(0, rx.DeliverAvailable(0UL, deliver))
    rx.Advance()
    Assert.Equal(4UL, rx.CustodyFrontier)
    Assert.True((rx :> ISourceStore).Source(0UL, 0).IsEmpty)

[<Fact>]
let ``callback failure retains source rather than silently releasing it`` () =
    let rx = WindowCustody(32, 1, 10000L)
    rx.NextGrant() |> ignore
    for step in 0UL .. 2UL do
        Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(step, 0, 1, Array.create 32 1uy))
        Assert.True(rx.TakeKnown(step, 0))
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(3UL, 0, 1, Array.create 32 2uy))
    let deliver = Action<uint64, int>(fun _ _ -> failwith "consumer failed")
    Assert.ThrowsAny<Exception>(fun () -> rx.DeliverKnown(3UL, 0, deliver) |> ignore) |> ignore
    rx.Advance()
    Assert.Equal(3UL, rx.CustodyFrontier)
    Assert.False((rx :> ISourceStore).Source(3UL, 0).IsEmpty)

[<Fact>]
let ``explicit matrix reserve is reused after complete custody release`` () =
    let perWindow = 64L * 8L + 65L * 32L
    let rx = WindowCustody(32, 1, perWindow, reserveCount = 1)
    Assert.Equal(perWindow, rx.ReservedBytes)
    Assert.Equal(1, rx.IdleReservedCount)
    rx.NextGrant() |> ignore
    for step in 0UL .. 3UL do
        Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(step, 0, 1, Array.create 32 1uy))
        Assert.True(rx.TakeKnown(step, 0))
    Assert.Equal(0, rx.IdleReservedCount)
    rx.Advance()
    Assert.Equal(1, rx.IdleReservedCount)
    rx.NextGrant() |> ignore
    Assert.Equal(WindowCodec.AddOutcome.Innovative, rx.AddSource(4UL, 0, 1, Array.create 32 2uy))
    Assert.Equal(0, rx.IdleReservedCount)
    Assert.Equal(perWindow, rx.ReservedBytes)

[<Fact>]
let ``matrix reserve rejects invalid counts and insufficient bytes`` () =
    let perWindow = 64L * 8L + 65L * 32L
    Assert.Throws<ArgumentException>(fun () -> WindowCustody(32, 1, perWindow, reserveCount = 2) |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> WindowReceive(32, 1, perWindow - 1L, reserveCount = 1) |> ignore) |> ignore
    let lazyRx = WindowCustody(32, 1, perWindow)
    Assert.Equal(0L, lazyRx.ReservedBytes)

[<Fact>]
let ``advertised window cannot exceed decoder byte budget`` () =
    Assert.Throws<ArgumentException>(fun () -> WindowCustody(1344, 2, 1000L) |> ignore) |> ignore
    let rx = WindowCustody(32, 1, 10000L)
    Assert.Equal(WindowCodec.AddOutcome.Invalid, rx.AddSource(0UL, 0, 1, Array.zeroCreate 32))
    Assert.Equal(4us, rx.NextGrant())
    Assert.Equal(WindowCodec.AddOutcome.Invalid, rx.AddSource(4UL, 0, 1, Array.zeroCreate 32))
