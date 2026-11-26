module Tbt.Tests.WindowRxBackendTests

open System
open Xunit
open Tbt.Session

[<Fact>]
let ``decoded frontier advances in order and custody waits for references`` () =
    let p = WindowRxProgress(8)
    Assert.True(p.ObserveStep(1UL, 1))
    Assert.True(p.MarkKnown(1UL, 0))
    p.Advance()
    Assert.Equal(0UL, p.DecodedFrontier)
    Assert.True(p.ObserveStep(0UL, 2))
    Assert.True(p.MarkKnown(0UL, 1))
    Assert.True(p.MarkKnown(0UL, 0))
    Assert.False(p.MarkKnown(0UL, 0))
    Assert.False(p.MarkKnown(0UL, -1))
    p.StepUndelivered(0UL, 1)
    p.Advance()
    Assert.Equal(2UL, p.DecodedFrontier)
    Assert.Equal(0UL, p.CustodyFrontier)
    p.StepUndelivered(0UL, -1)
    p.Advance()
    Assert.Equal(2UL, p.CustodyFrontier)
    Assert.Equal(0, p.Count)
    Assert.False(p.ObserveStep(0UL, 2))

[<Fact>]
let ``conflicting lengths and exhausted capacity leave progress intact`` () =
    let p = WindowRxProgress(1)
    Assert.True(p.ObserveStep(0UL, 1))
    Assert.False(p.ObserveStep(0UL, 2))
    Assert.False(p.ObserveStep(1UL, 1))
    Assert.False(p.ObserveStep(UInt64.MaxValue, 1))
    Assert.False(p.MarkKnown(0UL, 1))
    Assert.Throws<ArgumentException>(fun () -> p.StepUndelivered(0UL, -1)) |> ignore
    Assert.True(p.MarkKnown(0UL, 0))
    p.Advance()
    Assert.True(p.ObserveStep(1UL, 1))
