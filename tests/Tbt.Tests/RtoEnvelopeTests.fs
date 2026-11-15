module Tbt.Tests.RtoEnvelopeTests

open System
open Xunit
open Tbt.Carrier
open Tbt.Session
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests

let private sendFrame (f: Fixture) index =
    f.Send(f.Data0 + uint32 (index * L), ReadOnlySpan(f.Stream, index * L, L), [||])

let private lastAck (f: Fixture) = parse f.Out.Packets.[f.Out.Packets.Count - 1]

[<Fact>]
let ``later bytes do not restart the gap grace`` () =
    let f = Fixture 5
    f.Open()
    sendFrame f 0
    f.Path.FlushAck()
    let before = f.Out.Packets.Count
    let firstSeen = f.Clock.Now
    // Frame 1 is absent. Later frames extend the same interval.
    for index in 2 .. 4 do
        f.Clock.Now <- firstSeen + int64 (index - 2) * 5_000_000L
        sendFrame f index
        f.Path.Service f.Clock.Now
        f.Path.FlushAck()
        Assert.Equal(before, f.Out.Packets.Count)
    f.Clock.Now <- firstSeen + 14_999_999L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    Assert.Equal(0L, f.Path.Counters.VirtualAcks)
    f.Clock.Now <- firstSeen + 15_000_000L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    let ack = lastAck f
    Assert.Equal(f.Data0 + uint32 (5 * L), ack.AckNo)
    Assert.Equal(0, ack.OptionsLength)
    Assert.Equal(1L, f.Path.Counters.VirtualAcks)
    Assert.Equal(int64 L, f.Path.BytesSwallowed)
    Assert.Equal<int list>([1; 3; 4; 5], [for frame in f.Owner.Frames -> int frame.[0]])

[<Theory>]
[<InlineData(0)>]
[<InlineData(7)>]
let ``publishing a swallowed ACK needs its own flush budget`` (flushDelayMs: int) =
    let f = Fixture 3
    f.Open()
    sendFrame f 0
    f.Path.FlushAck()
    let before = f.Out.Packets.Count
    sendFrame f 2
    let firstSeen = f.Clock.Now
    f.Clock.Now <- firstSeen + 15_000_000L
    // Calling FlushAck alone does not run the grace timer.
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    f.Path.Service f.Clock.Now
    Assert.Equal(1L, f.Path.Counters.VirtualAcks)
    Assert.Equal(before, f.Out.Packets.Count)
    f.Clock.Now <- f.Clock.Now + int64 flushDelayMs * 1_000_000L
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(f.Data0 + uint32 (3 * L), (lastAck f).AckNo)
    Assert.Equal(0, (lastAck f).OptionsLength)

[<Fact>]
let ``an unseen tail is not virtually acknowledged and later evidence resumes progress`` () =
    let f = Fixture 3
    f.Open()
    sendFrame f 0
    f.Path.FlushAck()
    let before = f.Out.Packets.Count
    // Frame 1 was sent by the peer but never reached this path. The receiver
    // cannot infer that fact from elapsed time alone.
    for _ in 1 .. 300 do
        f.Clock.Now <- f.Clock.Now + 1_000_000L
        f.Path.Service f.Clock.Now
        f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    Assert.Equal(f.Data0 + uint32 L, (lastAck f).AckNo)
    Assert.Equal(0L, f.Path.Counters.VirtualAcks)
    Assert.Equal(0L, f.Path.BytesSwallowed)
    sendFrame f 2
    f.Clock.Now <- f.Clock.Now + 15_000_000L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(f.Data0 + uint32 (3 * L), (lastAck f).AckNo)
    Assert.Equal(int64 L, f.Path.BytesSwallowed)
    Assert.Equal<int list>([1; 3], [for frame in f.Owner.Frames -> int frame.[0]])

[<Fact>]
let ``another path cannot provide a missing tail boundary`` () =
    let clock = ManualClock()
    let group = RateGroup(clock)
    let a = Fixture(3, clock, group, 40000us)
    let b = Fixture(3, clock, group, 40001us)
    for f in [a; b] do
        f.Open()
        group.Add(f.Path :> IRateMember)
        sendFrame f 0
        f.Path.FlushAck()
    let beforeA = a.Out.Packets.Count
    sendFrame b 2
    clock.Now <- clock.Now + 15_000_000L
    for f in [a; b] do
        f.Path.Service clock.Now
        f.Path.FlushAck()
    Assert.Equal(b.Data0 + uint32 (3 * L), (lastAck b).AckNo)
    Assert.Equal(1L, b.Path.Counters.VirtualAcks)
    Assert.Equal(beforeA, a.Out.Packets.Count)
    Assert.Equal(a.Data0 + uint32 L, (lastAck a).AckNo)
    Assert.Equal(0L, a.Path.Counters.VirtualAcks)

[<Theory>]
[<InlineData(300, 7)>]
[<InlineData(300, 9)>]
[<InlineData(1300, 7)>]
[<InlineData(1300, 9)>]
let ``a long forward outage preserves the tail and permits the next write`` (blackMs: int, seed: int) =
    // This uses the real session and codec, not a kernel TCP model. Recovery
    // has a 20 ms one-way delay, 1 ms service ticks and no further loss.
    let config = { SessionConfig.defaults with WindowSteps = 1024 }
    let pair = Pair(config, 4, 0.0, seed)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    Assert.False(c.Tx.NewRepair)
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let head = Array.init 40_000 (fun i -> byte (i * 7))
    let tail = Array.init 3_000 (fun i -> byte (i * 11 + 3))
    let next = Array.init 4_096 (fun i -> byte (i * 13 + 5))
    Assert.Equal(head.Length, pair.Feed(c, slot, ReadOnlySpan head, false))
    for _ in 1 .. 500 do pair.Round() |> ignore
    Assert.Equal<byte[]>(head, pair.ServerPort.Stream(slot).ToArray())
    pair.Blackout <- true
    Assert.Equal(tail.Length, pair.Feed(c, slot, ReadOnlySpan tail, false))
    for _ in 1 .. blackMs do pair.Round() |> ignore
    Assert.Equal<byte[]>(head, pair.ServerPort.Stream(slot).ToArray())
    let repairsBefore = c.Tx.BoostersSent
    pair.Blackout <- false
    let expected = Array.append head tail
    let mutable recoveryMs = 0
    while recoveryMs < 2_000 && (pair.ServerPort.Stream(slot).Length < int64 expected.Length || c.Tx.PeerAck < c.Tx.Head) do
        pair.Round() |> ignore
        recoveryMs <- recoveryMs + 1
    let message = sprintf "outage %d ms, seed %d, recovery %d ms, repairs %d" blackMs seed recoveryMs (c.Tx.BoostersSent - repairsBefore)
    Assert.True(recoveryMs < 2_000, message)
    Assert.Equal<byte[]>(expected, pair.ServerPort.Stream(slot).ToArray())
    Assert.Equal(c.Tx.Head, c.Tx.PeerAck)
    Assert.True(c.Tx.BoostersSent > repairsBefore, message)
    Assert.False(c.Tx.RepairWaiting)
    // Recovery must leave the session usable for new data.
    Assert.Equal(next.Length, pair.Feed(c, slot, ReadOnlySpan next, false))
    let complete = Array.append expected next
    let mutable nextMs = 0
    while nextMs < 2_000 && (pair.ServerPort.Stream(slot).Length < int64 complete.Length || c.Tx.PeerAck < c.Tx.Head) do
        pair.Round() |> ignore
        nextMs <- nextMs + 1
    Assert.True(nextMs < 2_000, sprintf "next write stalled after %s" message)
    Assert.Equal<byte[]>(complete, pair.ServerPort.Stream(slot).ToArray())
    Assert.Equal(c.Tx.Head, c.Tx.PeerAck)
    Assert.False(c.Tx.RepairWaiting)
