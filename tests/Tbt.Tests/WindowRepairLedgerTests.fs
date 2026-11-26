module Tbt.Tests.WindowRepairLedgerTests

open Xunit
open Tbt.Session

[<Fact>]
let ``full ledger requires retry and preserves earlier repairs`` () =
    let q = WindowRepairLedger(1)
    Assert.True(q.TryRequest(4UL, 3us))
    Assert.False(q.TryRequest(5UL, 1us))
    let mutable step = 0UL
    let mutable slot = -1
    Assert.True(q.TryPeek(&step, &slot))
    Assert.Equal(4UL, step)
    Assert.Equal(0, slot)
    Assert.True(q.TryPeek(&step, &slot)) // blocked sends do not consume
    Assert.True(q.Sent(4UL, 0))
    Assert.True(q.TryPeek(&step, &slot))
    Assert.Equal(1, slot)
    Assert.True(q.Sent(4UL, 1))
    Assert.True(q.TryRequest(5UL, 1us))

[<Fact>]
let ``ack discards obsolete candidates and cannot move backwards`` () =
    let q = WindowRepairLedger(3)
    Assert.True(q.TryRequest(1UL, 1us))
    Assert.True(q.TryRequest(4UL, 2us))
    Assert.True(q.AdvanceAck(4UL))
    Assert.False(q.AdvanceAck(3UL))
    Assert.False(q.TryRequest(1UL, 1us))
    Assert.True(q.CanReleaseThrough(4UL))
    Assert.False(q.CanReleaseThrough(5UL))
    let mutable step = 0UL
    let mutable slot = -1
    Assert.True(q.TryPeek(&step, &slot))
    Assert.Equal(4UL, step)
    Assert.Equal(1, slot)

[<Fact>]
let ``full ledger still merges an existing step and retries after acknowledgement`` () =
    let q = WindowRepairLedger(1)
    Assert.True(q.TryRequest(7UL, 1us))
    Assert.True(q.TryRequest(7UL, 0x8000us))
    Assert.False(q.TryRequest(8UL, 1us))
    Assert.True(q.AdvanceAck(8UL))
    Assert.Equal(0, q.Count)
    Assert.False(q.TryRequest(7UL, 1us))
    Assert.True(q.TryRequest(8UL, 1us))
    let mutable step = 0UL
    let mutable slot = -1
    Assert.True(q.TryPeek(&step, &slot))
    Assert.Equal(8UL, step)
    Assert.Equal(0, slot)

[<Fact>]
let ``maximum step acknowledgement retains its exclusive boundary without overflow`` () =
    let q = WindowRepairLedger(2)
    Assert.True(q.TryRequest(System.UInt64.MaxValue - 1UL, 1us))
    Assert.True(q.TryRequest(System.UInt64.MaxValue, 0x8000us))
    Assert.True(q.AdvanceAck(System.UInt64.MaxValue))
    Assert.False(q.TryRequest(System.UInt64.MaxValue - 1UL, 1us))
    let mutable step = 0UL
    let mutable slot = -1
    Assert.True(q.TryPeek(&step, &slot))
    Assert.Equal(System.UInt64.MaxValue, step)
    Assert.Equal(15, slot)
    Assert.True(q.Sent(step, slot))
    Assert.Equal(0, q.Count)

[<Fact>]
let ``duplicate requests merge without duplicate sends`` () =
    let q = WindowRepairLedger(1)
    Assert.True(q.TryRequest(0UL, 1us))
    Assert.True(q.TryRequest(0UL, 3us))
    Assert.True(q.Sent(0UL, 0))
    Assert.False(q.Sent(0UL, 0))
    Assert.True(q.Sent(0UL, 1))
    Assert.Equal(0, q.Count)
