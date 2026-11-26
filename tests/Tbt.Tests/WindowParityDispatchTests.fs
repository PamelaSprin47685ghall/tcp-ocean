module Tbt.Tests.WindowParityDispatchTests

open System
open Xunit
open Tbt.Session
open Tbt.Wire

[<Fact>]
let ``pending parity survives failed submissions without reusing its index`` () =
    let store = WindowTxStore(32, 1)
    let source = Array.init 32 byte
    Assert.True(store.TryWrite(0UL, 0, ReadOnlySpan source))
    Assert.True(store.SealStep(0UL, 1))
    let d = WindowParityDispatch(store, 32)
    Assert.True(d.TryBegin(0UL, 0))
    Assert.False(d.TryBegin(0UL, 1))
    let first = Array.zeroCreate<byte> 32
    let second = Array.zeroCreate<byte> 32
    let mutable m1 = Unchecked.defaultof<Meta>
    let mutable m2 = Unchecked.defaultof<Meta>
    Assert.True(d.TryCopy(Span first, &m1))
    Assert.True(d.TryCopy(Span second, &m2))
    Assert.Equal<byte[]>(first, second)
    Assert.Equal(m1, m2)
    Assert.Equal<byte[]>(source, first)
    Assert.True(d.Finish())
    Assert.False(d.TryBegin(0UL, 0))
    Assert.True(d.TryBegin(0UL, 1))
    Assert.True(d.Finish())
    Assert.False(d.Finish())
