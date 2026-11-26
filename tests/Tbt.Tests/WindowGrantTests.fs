module Tbt.Tests.WindowGrantTests

open System
open Xunit
open Tbt.Session

[<Fact>]
let ``slow custody bounds irrevocable credit until a complete window is released`` () =
    let tracker = WindowGrantTracker(2)
    Assert.Equal(8us, tracker.NextGrant 0UL)
    Assert.Equal(8UL, tracker.PublishedLimit)
    Assert.Equal(4us, tracker.NextGrant 4UL)
    Assert.Equal(8UL, tracker.PublishedLimit)
    Assert.Equal(0us, tracker.NextGrant 8UL)
    Assert.Equal(8UL, tracker.SafeExclusive)
    tracker.AdvanceCustody 4UL
    Assert.Equal(12UL, tracker.SafeExclusive)
    Assert.Equal(4us, tracker.NextGrant 8UL)
    Assert.Equal(12UL, tracker.PublishedLimit)

[<Fact>]
let ``invalid acknowledgements and custody movements leave credit unchanged`` () =
    let tracker = WindowGrantTracker(1)
    Assert.Throws<ArgumentException>(fun () -> tracker.NextGrant UInt64.MaxValue |> ignore) |> ignore
    Assert.Equal(4us, tracker.NextGrant 0UL)
    Assert.Throws<ArgumentException>(fun () -> tracker.NextGrant 5UL |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> tracker.AdvanceCustody 4UL) |> ignore
    Assert.Equal(4us, tracker.NextGrant 0UL)
    Assert.Equal(2us, tracker.NextGrant 2UL)
    Assert.Throws<ArgumentException>(fun () -> tracker.NextGrant 1UL |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> tracker.AdvanceCustody 2UL) |> ignore
    Assert.Equal(4UL, tracker.PublishedLimit)
    Assert.Equal(0UL, tracker.FloorWindowStart)

[<Fact>]
let ``window capacity is bounded and capped at 512 steps`` () =
    Assert.Throws<ArgumentException>(fun () -> WindowGrantTracker(0) |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> WindowGrantTracker(161) |> ignore) |> ignore
    let tracker = WindowGrantTracker(160)
    Assert.Equal(512us, tracker.NextGrant 0UL)
    Assert.Equal(512UL, tracker.PublishedLimit)
