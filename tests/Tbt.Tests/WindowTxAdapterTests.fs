module Tbt.Tests.WindowTxAdapterTests

open System
open Xunit
open Tbt.Session

[<Fact>]
let ``sealed source waits for accepted submit and repair survives failed submit`` () =
    let tx = WindowTxCandidates(32, 1, 1)
    let mutable dst = Span<byte>.Empty
    Assert.True(tx.TryBeginSource(&dst))
    dst.Fill 42uy
    Assert.True(tx.CommitSource())
    Assert.True(tx.SealStep())
    let mutable step = 99UL
    let mutable slot = -1
    let mutable repair = true
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.Equal(0UL, step)
    Assert.Equal(0, slot)
    Assert.False(repair)
    Assert.False(tx.OnAck 1UL)
    Assert.True(tx.Complete false)
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.False(repair)
    Assert.True(tx.Complete true)
    Assert.True(tx.TryNack 0UL)
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.True(repair)
    Assert.Equal(42uy, tx.Source(step, slot).[0])
    Assert.True(tx.Complete false)
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.True(tx.Complete true)
    Assert.True(tx.OnAck 1UL)
    Assert.False(tx.TryNack 0UL)

[<Fact>]
let ``duplicate nack during a held repair requires another send`` () =
    let tx = WindowTxCandidates(32, 1, 2)
    let mutable dst = Span<byte>.Empty
    Assert.True(tx.TryBeginSource(&dst))
    dst.Fill 7uy
    Assert.True(tx.CommitSource())
    Assert.True(tx.SealStep())
    let mutable step = 0UL
    let mutable slot = 0
    let mutable repair = false
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.True(tx.Complete true)
    Assert.True(tx.TryNack 0UL)
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.True(tx.TryNack 0UL)
    Assert.True(tx.Complete true)
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.True(repair)
    Assert.True(tx.Complete true)
    Assert.False(tx.TryPrepare(&step, &slot, &repair))

[<Fact>]
let ``capacity rejection keeps earlier repair and caller can retry`` () =
    let tx = WindowTxCandidates(32, 1, 1)
    let mutable dst = Span<byte>.Empty
    for _ in 0..1 do
        Assert.True(tx.TryBeginSource(&dst))
        dst.Fill 5uy
        Assert.True(tx.CommitSource())
        Assert.True(tx.SealStep())
    let mutable step = 0UL
    let mutable slot = 0
    let mutable repair = false
    for _ in 0..1 do
        Assert.True(tx.TryPrepare(&step, &slot, &repair))
        Assert.True(tx.Complete true)
    Assert.True(tx.TryNack 0UL)
    Assert.False(tx.TryNack 1UL)
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.Equal(0UL, step)
    Assert.True(tx.Complete true)
    Assert.True(tx.TryNack 1UL)
    Assert.True(tx.TryPrepare(&step, &slot, &repair))
    Assert.Equal(1UL, step)
