module Tbt.Tests.WindowRxHalfTests

open System
open Xunit
open Tbt.Session

[<Fact>]
let ``authenticated later step exposes wholly missing frontier after deadline`` () =
    let tracker = WindowRxGapTracker()
    let mutable span = 0
    Assert.True(tracker.ObserveSealed(3UL, 8UL, 100L))
    Assert.False(tracker.TryRange(20_000_099L, &span))
    Assert.True(tracker.TryRange(20_000_100L, &span))
    Assert.Equal(4, span)
    Assert.True(tracker.Advance(2UL, 20_000_100L))
    Assert.False(tracker.TryRange(40_000_099L, &span))
    Assert.True(tracker.TryRange(40_000_100L, &span))
    Assert.Equal(2, span)
    Assert.True(tracker.Advance(4UL, 40_000_100L))
    Assert.False(tracker.TryRange(80_000_000L, &span))

[<Fact>]
let ``unauthorized and overflowing observations do not move the frontier`` () =
    let tracker = WindowRxGapTracker()
    Assert.False(tracker.ObserveSealed(8UL, 8UL, 1L))
    Assert.False(tracker.ObserveSealed(UInt64.MaxValue, UInt64.MaxValue, 1L))
    Assert.Equal(0UL, tracker.SeenEnd)
    Assert.False(tracker.Advance(1UL, 1L))
    Assert.True(tracker.ObserveSealed(65UL, 100UL, 1L))
    let mutable span = 0
    Assert.True(tracker.TryRange(1L, &span))
    Assert.Equal(4, span)
    Assert.False(tracker.Advance(1UL, 0L))
    Assert.Equal(0UL, tracker.Frontier)
