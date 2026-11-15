module Tbt.Tests.RateGroupTests

open Xunit
open Tbt.Carrier
open Tbt.Contracts

type private Clock() =
    member val Now = 1_000_000_000L with get, set
    interface IClock with
        member clock.NowNs() = clock.Now
        member clock.FreshNs() = clock.Now

type private Member(rtt: int64) =
    member val Inflight = 0 with get, set
    member val Delivered = 0L with get, set
    member val Copies = 0L with get, set
    member val LastDelivered = 0L with get, set
    interface IRateMember with
        member m.Inflight = m.Inflight
        member m.Delivered = m.Delivered
        member m.CopyEstimate = m.Copies
        member _.MinRttNs = rtt
        member m.LastDeliveredNs = m.LastDelivered

/// Supply measured rounds to the real controller; this is not a link simulation.
type private Rounds(rateMbit: float, rttMs: int, paths: int) =
    let clock = Clock()
    let rtt = int64 rttMs * 1_000_000L
    let period = max rtt 5_000_000L
    let group = RateGroup(clock)
    let members = Array.init paths (fun _ -> Member(rtt))
    let rate = rateMbit / 8000.0
    do for m in members do group.Add m

    member _.Group = group
    member _.Rate = rate
    member _.Bdp = int (rate * float rtt)
    member _.RoundBytes = int (rate * float period)

    member _.Step(sent: int, delivered: int, inflight: int) =
        clock.Now <- clock.Now + period
        if sent > 0 then group.OnSent sent
        for i in 0 .. paths - 1 do
            let part value = value / paths + (if i < value % paths then 1 else 0)
            let arrived = part delivered
            members.[i].Delivered <- members.[i].Delivered + int64 arrived
            members.[i].Inflight <- part inflight
            if arrived > 0 then members.[i].LastDelivered <- clock.Now
        group.Service clock.Now

    member this.Loaded() = this.Step(this.RoundBytes, this.RoundBytes, this.Bdp)

let private near expected actual =
    Assert.InRange(actual, expected * 0.999, expected * 1.001)

[<Theory>]
[<InlineData(20.0, 40, 1)>]
[<InlineData(200.0, 40, 4)>]
[<InlineData(32.0, 400, 8)>]
[<InlineData(256.0, 400, 64)>]
let ``zero traffic idle breaks the previous full-pipe count`` rateMbit rttMs paths =
    let rounds = Rounds(rateMbit, rttMs, paths)
    let group = rounds.Group
    // First sample establishes the rate. Two more loaded samples do not grow it.
    for _ in 1 .. 3 do rounds.Loaded()
    Assert.True(group.InStartup)
    Assert.Equal(2, group.FullPipeRounds)
    // Unlike a low-rate echo, this interval has no sends, arrivals, or in-flight bytes.
    for _ in 1 .. 4 do rounds.Step(0, 0, 0)
    near rounds.Rate group.Bandwidth
    Assert.Equal(0, group.FullPipeRounds)
    Assert.Equal(0.0, group.Goodput)
    Assert.Equal(0.0, group.CopyRate)
    Assert.Equal(0.0, group.LastSampleRate)
    Assert.Equal(0.0, group.LastSendRate)
    // Three new loaded samples are required. No TryTake refusal is needed.
    for _ in 1 .. 2 do
        rounds.Loaded()
        Assert.True(group.InStartup, group.Describe())
    rounds.Loaded()
    Assert.False(group.InStartup, group.Describe())
    Assert.Equal("drain", group.Phase)
    near (rounds.Rate / 2.885) group.PacingRate

[<Theory>]
[<InlineData(20.0, 40, 1)>]
[<InlineData(200.0, 40, 4)>]
[<InlineData(256.0, 400, 64)>]
let ``a delivery outage retains bandwidth and does not reset loaded rounds`` rateMbit rttMs paths =
    let rounds = Rounds(rateMbit, rttMs, paths)
    let group = rounds.Group
    for _ in 1 .. 3 do rounds.Loaded()
    for _ in 1 .. 12 do rounds.Step(0, 0, rounds.Bdp)
    // An outstanding pipe with no ACK is not an idle application. Loss must not
    // reset the count on each missed round and keep startup running forever.
    near rounds.Rate group.Bandwidth
    Assert.Equal(2, group.FullPipeRounds)
    rounds.Loaded()
    Assert.Equal("drain", group.Phase)

[<Theory>]
[<InlineData(16.0, 8)>]
[<InlineData(36.0, 8)>]
[<InlineData(64.0, 64)>]
[<InlineData(99.0, 64)>]
let ``idle keeps the full startup gain and can sample a larger pipe`` residualMbit paths =
    let rounds = Rounds(residualMbit, 400, paths)
    let group = rounds.Group
    for _ in 1 .. 3 do rounds.Loaded()
    let cwndBeforeIdle = group.Cwnd 1415
    let pacingBeforeIdle = group.PacingRate
    for _ in 1 .. 4 do rounds.Step(0, 0, 0)
    // Both slow and fast echo residuals keep the existing gain immediately.
    // There is no extra round at a gain of one before probing a larger pipe.
    near (rounds.Rate * 2.885) group.PacingRate
    Assert.Equal(cwndBeforeIdle, group.Cwnd 1415)
    Assert.Equal(pacingBeforeIdle, group.PacingRate)
    let larger = rounds.RoundBytes * 2
    rounds.Step(larger, larger, rounds.Bdp * 2)
    near (rounds.Rate * 2.0) group.Bandwidth
    Assert.True(group.InStartup, group.Describe())
    Assert.Equal(0, group.FullPipeRounds)

[<Theory>]
[<InlineData(20.0, 200.0)>]
[<InlineData(80.0, 8.0)>]
let ``a true idle and restart keep asymmetric direction estimates separate`` upMbit downMbit =
    let up = Rounds(upMbit, 40, 4)
    let down = Rounds(downMbit, 40, 4)
    for _ in 1 .. 3 do
        up.Loaded()
        down.Loaded()
    for _ in 1 .. 4 do up.Step(0, 0, 0)
    // Only the uplink has a fully idle round; downlink evidence stays intact.
    Assert.Equal(0, up.Group.FullPipeRounds)
    Assert.Equal(2, down.Group.FullPipeRounds)
    down.Loaded()
    Assert.Equal("drain", down.Group.Phase)
    up.Loaded()
    Assert.Equal("startup", up.Group.Phase)
    near up.Rate up.Group.Bandwidth
    near down.Rate down.Group.Bandwidth
    // A capacity increase in the uplink must not change the downlink estimate.
    up.Step(up.RoundBytes * 2, up.RoundBytes * 2, up.Bdp * 2)
    near (up.Rate * 2.0) up.Group.Bandwidth
    near down.Rate down.Group.Bandwidth
