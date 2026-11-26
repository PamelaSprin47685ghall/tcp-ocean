module Tbt.Tests.WindowRepairDispatchTests

open Xunit
open Tbt.Session

[<Fact>]
let ``failed submission retains exactly the same repair candidate`` () =
    let d = WindowRepairDispatch(2)
    Assert.True(d.TryRequest(4UL, 0b1010us))
    let mutable step = 0UL
    let mutable slot = 0
    Assert.True(d.TryBegin(&step, &slot))
    Assert.Equal(4UL, step)
    Assert.Equal(1, slot)
    Assert.True(d.Complete false)
    Assert.True(d.TryBegin(&step, &slot))
    Assert.Equal(4UL, step)
    Assert.Equal(1, slot)
    Assert.True(d.Complete true)
    Assert.True(d.TryBegin(&step, &slot))
    Assert.Equal(3, slot)
    Assert.True(d.Complete true)
    Assert.False(d.TryBegin(&step, &slot))

[<Fact>]
let ``ack discards held obsolete repair and never moves backwards`` () =
    let d = WindowRepairDispatch(2)
    Assert.True(d.TryRequest(0UL, 1us))
    Assert.True(d.TryRequest(4UL, 2us))
    let mutable step = 0UL
    let mutable slot = 0
    Assert.True(d.TryBegin(&step, &slot))
    Assert.False(d.CanReleaseThrough 4UL)
    Assert.True(d.AdvanceAck 4UL)
    Assert.True(d.CanReleaseThrough 4UL)
    Assert.False(d.Complete true)
    Assert.True(d.TryBegin(&step, &slot))
    Assert.Equal(4UL, step)
    Assert.Equal(1, slot)
    Assert.False(d.AdvanceAck 3UL)

[<Fact>]
let ``repeated nack during a held submission keeps the repair pending`` () =
    let d = WindowRepairDispatch(2)
    Assert.True(d.TryRequest(4UL, 0b0010us))
    let mutable step = 0UL
    let mutable slot = 0
    Assert.True(d.TryBegin(&step, &slot))
    Assert.Equal(4UL, step)
    Assert.Equal(1, slot)
    // Same slot arrives again while this candidate is being submitted. A
    // successful submit must not consume the repeated request.
    Assert.True(d.TryRequest(4UL, 0b0010us))
    Assert.True(d.Complete true)
    Assert.True(d.TryBegin(&step, &slot))
    Assert.Equal(4UL, step)
    Assert.Equal(1, slot)
    Assert.True(d.Complete true)
    Assert.False(d.TryBegin(&step, &slot))

[<Fact>]
let ``ack clears a held repair with a repeated nack`` () =
    let d = WindowRepairDispatch(2)
    Assert.True(d.TryRequest(4UL, 1us))
    let mutable step = 0UL
    let mutable slot = 0
    Assert.True(d.TryBegin(&step, &slot))
    Assert.True(d.TryRequest(4UL, 1us))
    Assert.True(d.AdvanceAck 8UL)
    Assert.False(d.Complete true)
    Assert.False(d.TryBegin(&step, &slot))

[<Fact>]
let ``capacity rejection can be retried after confirmed send`` () =
    let d = WindowRepairDispatch(1)
    Assert.True(d.TryRequest(0UL, 1us))
    Assert.False(d.TryRequest(4UL, 1us))
    let mutable step = 0UL
    let mutable slot = 0
    Assert.True(d.TryBegin(&step, &slot))
    Assert.True(d.Complete true)
    Assert.True(d.TryRequest(4UL, 1us))
    Assert.True(d.TryBegin(&step, &slot))
    Assert.Equal(4UL, step)
