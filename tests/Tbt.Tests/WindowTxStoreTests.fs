module Tbt.Tests.WindowTxStoreTests

open System
open Xunit
open Tbt.Session

let private symbol n = Array.init 32 (fun i -> byte ((i + n * 17) % 256))

[<Fact>]
let ``step length is visible only after sealing and disappears on release`` () =
    let tx = WindowTxStore(32, 1)
    Assert.True(tx.TryStepUsed(0UL).IsNone)
    Assert.True(tx.TryWrite(0UL, 0, ReadOnlySpan(symbol 1)))
    Assert.True(tx.TryStepUsed(0UL).IsNone)
    Assert.True(tx.SealStep(0UL, 1))
    Assert.Equal(ValueSome 1, tx.TryStepUsed(0UL))
    Assert.True(tx.TryStepUsed(1UL).IsNone)
    Assert.True(tx.TryWrite(1UL, 0, ReadOnlySpan(symbol 2)))
    Assert.True(tx.TryWrite(1UL, 1, ReadOnlySpan(symbol 3)))
    Assert.True(tx.SealStep(1UL, 2))
    Assert.Equal(ValueSome 2, tx.TryStepUsed(1UL))
    tx.ReleaseThrough(4UL, true)
    Assert.True(tx.TryStepUsed(0UL).IsNone)
    Assert.True(tx.TryStepUsed(1UL).IsNone)

[<Fact>]
let ``reserved source is invisible until commit and cancellation frees capacity`` () =
    let tx = WindowTxStore(32, 1)
    let mutable slot = Span<byte>.Empty
    Assert.True(tx.TryBeginWrite(0UL, 0, &slot))
    slot.Fill(42uy)
    Assert.True((tx :> ISourceStore).Source(0UL, 0).IsEmpty)
    Assert.False(tx.TryWrite(0UL, 1, ReadOnlySpan(symbol 1)))
    Assert.False(tx.CommitWrite(0UL, 1))
    tx.ReleaseThrough(4UL, true)
    Assert.Equal(1, tx.Count)
    Assert.True(tx.CommitWrite(0UL, 0))
    Assert.True((tx :> ISourceStore).Source(0UL, 0).SequenceEqual(ReadOnlySpan(Array.create 32 42uy)))
    Assert.True(tx.SealStep(0UL, 1))
    tx.ReleaseThrough(4UL, true)
    Assert.True(tx.TryBeginWrite(4UL, 0, &slot))
    tx.CancelWrite()
    Assert.Equal(0, tx.Count)
    Assert.False(tx.CommitWrite(4UL, 0))

[<Fact>]
let ``a step cannot seal while a source in that step is reserved`` () =
    let tx = WindowTxStore(32, 1)
    Assert.True(tx.TryWrite(0UL, 0, ReadOnlySpan(symbol 1)))
    let mutable slot = Span<byte>.Empty
    Assert.True(tx.TryBeginWrite(0UL, 1, &slot))
    Assert.False(tx.SealStep(0UL, 1))
    slot.Fill(7uy)
    Assert.True(tx.CommitWrite(0UL, 1))
    Assert.True(tx.SealStep(0UL, 2))

[<Fact>]
let ``sealed partial steps render parity that recovers an entirely lost step`` () =
    let tx = WindowTxStore(32, 2)
    let a, b, c = symbol 1, symbol 2, symbol 3
    Assert.True(tx.TryWrite(0UL, 0, ReadOnlySpan a))
    Assert.True(tx.TryWrite(0UL, 1, ReadOnlySpan b))
    Assert.True(tx.SealStep(0UL, 2))
    Assert.True(tx.TryWrite(1UL, 0, ReadOnlySpan c))
    Assert.True(tx.SealStep(1UL, 1))
    let rx = WindowReceive(32, 2, 1L <<< 20)
    Assert.True(rx.AddSource(0UL, 0, 2, a))
    Assert.True(rx.AddSource(0UL, 1, 2, b))
    let parity = Array.zeroCreate<byte> 32
    let mutable recovered = false
    let mutable i = 0
    while not recovered && i < 48 do
        match tx.TryRenderParity(0UL, i, Span parity) with
        | ValueNone -> failwith "parity unavailable"
        | ValueSome meta ->
            rx.AddParity(meta, parity) |> ignore
            let output = Array.zeroCreate<byte> 32
            if rx.TryCopyKnown(1UL, 0, Span output) then
                Assert.Equal<byte[]>(c, output)
                recovered <- true
        i <- i + 1
    Assert.True(recovered)
    Assert.True(tx.TryRenderParity(0UL, i - 1, Span parity).IsNone)

[<Fact>]
let ``out of order sources and sparse repairs recover ten missing symbols`` () =
    let tx = WindowTxStore(32, 1)
    let src = Array.init 64 symbol
    for step = 0 to 3 do
        for slot = 0 to 15 do
            Assert.True(tx.TryWrite(uint64 step, slot, ReadOnlySpan src.[step * 16 + slot]))
        Assert.True(tx.SealStep(uint64 step, 16))
    let rx = WindowReceive(32, 1, 1L <<< 20)
    for i = 63 downto 0 do
        if i % 7 <> 0 then
            Assert.True(rx.AddSource(uint64 (i / 16), i % 16, 16, src.[i]))
    let parity = Array.zeroCreate<byte> 32
    for i = 0 to 47 do
        let meta = tx.TryRenderParity(0UL, i, Span parity).Value
        rx.AddParity(meta, parity) |> ignore
    let result = Array.zeroCreate<byte> 32
    for i = 0 to 63 do
        Assert.True(rx.TryCopyKnown(uint64 (i / 16), i % 16, Span result), sprintf "missing %d" i)
        Assert.Equal<byte[]>(src.[i], result)

[<Fact>]
let ``source custody is bounded and release requires explicit reference clearance`` () =
    let tx = WindowTxStore(32, 1)
    let a = symbol 5
    Assert.True(tx.TryWrite(0UL, 0, ReadOnlySpan a))
    Assert.False(tx.TryWrite(0UL, 0, ReadOnlySpan a))
    Assert.True(tx.SealStep(0UL, 1))
    Assert.False(tx.TryWrite(4UL, 0, ReadOnlySpan a))
    tx.ReleaseThrough(4UL, false)
    Assert.Equal(1, tx.Count)
    Assert.True(((tx :> ISourceStore).Source(0UL, 0)).SequenceEqual(ReadOnlySpan a))
    tx.ReleaseThrough(4UL, true)
    Assert.Equal(0, tx.Count)
    Assert.True(tx.TryWrite(4UL, 0, ReadOnlySpan a))
    Assert.False(tx.TryWrite(0UL, 0, ReadOnlySpan a))

[<Fact>]
let ``parity indices exhaust without overwriting source custody`` () =
    let tx = WindowTxStore(32, 1)
    let a = symbol 1
    Assert.True(tx.TryWrite(0UL, 0, ReadOnlySpan a))
    Assert.True(tx.SealStep(0UL, 1))
    let dst = Array.zeroCreate<byte> 32
    for i = 0 to 47 do
        Assert.True(tx.TryRenderParity(0UL, i, Span dst).IsSome)
        Assert.Equal<byte[]>(a, dst)
    Assert.True(tx.TryRenderParity(0UL, 48, Span dst).IsNone)
    Assert.True(((tx :> ISourceStore).Source(0UL, 0)).SequenceEqual(ReadOnlySpan a))
