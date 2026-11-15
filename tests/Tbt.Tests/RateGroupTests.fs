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

    /// One sample interval. backlog: the session had a bulk queue during it (a loaded
    /// sender), as SessionCarrier reports once per write round through the paths.
    member _.StepWith(sent: int, delivered: int, inflight: int, backlog: bool) =
        clock.Now <- clock.Now + period
        if backlog then group.NoteBacklog()
        if sent > 0 then group.OnSent sent
        for i in 0 .. paths - 1 do
            let part value = value / paths + (if i < value % paths then 1 else 0)
            let arrived = part delivered
            members.[i].Delivered <- members.[i].Delivered + int64 arrived
            members.[i].Inflight <- part inflight
            if arrived > 0 then members.[i].LastDelivered <- clock.Now
        group.Service clock.Now

    member this.Step(sent: int, delivered: int, inflight: int) = this.StepWith(sent, delivered, inflight, false)

    /// A loaded round: a bulk sender with a queue keeps one BDP in flight.
    member this.Loaded() = this.StepWith(this.RoundBytes, this.RoundBytes, this.Bdp, true)

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
    rounds.StepWith(larger, larger, rounds.Bdp * 2, true)
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
    up.StepWith(up.RoundBytes * 2, up.RoundBytes * 2, up.Bdp * 2, true)
    near (up.Rate * 2.0) up.Group.Bandwidth
    near down.Rate down.Group.Bandwidth

[<Theory>]
[<InlineData(32.0, 400, 8)>]
[<InlineData(256.0, 400, 64)>]
[<InlineData(200.0, 40, 4)>]
let ``a handshake trickle with no backlog does not end startup`` pipeMbit rttMs paths =
    // A new session: JOINs and control frames give a tiny rate sample and keep a few kB
    // in flight, which is above one BDP of that tiny estimate. Nothing is queued.
    let trickle = Rounds(0.2, rttMs, paths)
    let group = trickle.Group
    for _ in 1 .. 10 do trickle.Step(trickle.RoundBytes, trickle.RoundBytes, trickle.Bdp * 2)
    Assert.True(group.InStartup, group.Describe())
    Assert.Equal(0, group.FullPipeRounds)
    // The first bulk then grows at the startup gain from that estimate.
    let mutable rate = trickle.Rate
    let period = float (max (int64 rttMs * 1_000_000L) 5_000_000L)
    let cap = pipeMbit / 8000.0
    while rate < cap do
        rate <- min cap (rate * 2.885)
        let bytes = int (rate * period)
        trickle.StepWith(bytes, bytes, int (rate * float rttMs * 1_000_000.0), true)
        Assert.True(group.InStartup, group.Describe())
    near cap group.Bandwidth

[<Theory>]
[<InlineData(16.0, 400, 8)>]
[<InlineData(36.0, 400, 8)>]
[<InlineData(64.0, 400, 64)>]
[<InlineData(20.0, 40, 4)>]
let ``a delivery stall in startup is one round, not three`` residualMbit rttMs paths =
    let rounds = Rounds(residualMbit, rttMs, paths)
    let group = rounds.Group
    rounds.Loaded()
    // A hole on one path holds delivery for several sample intervals while the backlog
    // keeps 2.885 BDP in flight. Time passes, but less than one BDP is delivered, so no
    // delivery round ends and none counts as full.
    let trickle = rounds.RoundBytes / 100
    for _ in 1 .. 6 do
        rounds.StepWith(trickle, trickle, int (2.885 * float rounds.Bdp), true)
        Assert.True(group.InStartup, group.Describe())
    Assert.Equal(0, group.FullPipeRounds)
    // Delivery resumes at a larger rate: still startup, and the estimate grows.
    let larger = rounds.RoundBytes * 2
    rounds.StepWith(larger, larger, rounds.Bdp * 2, true)
    Assert.True(group.InStartup, group.Describe())
    near (rounds.Rate * 2.0) group.Bandwidth
    Assert.Equal(0, group.FullPipeRounds)

[<Theory>]
[<InlineData(200.0, 40, 4)>]
[<InlineData(32.0, 400, 8)>]
[<InlineData(256.0, 400, 64)>]
let ``a backlogged pipe ends startup without a TryTake refusal`` rateMbit rttMs paths =
    // 20/200 5 %: the coding window or the peer window holds the sender, the pacer never
    // refuses, inflight sits between 1 and 2.885 BDP. That is a full pipe (2bc2968).
    let rounds = Rounds(rateMbit, rttMs, paths)
    let group = rounds.Group
    let inflight = int (1.6 * float rounds.Bdp)
    for _ in 1 .. 3 do
        rounds.StepWith(rounds.RoundBytes, rounds.RoundBytes, inflight, true)
        Assert.True(group.InStartup, group.Describe())
    rounds.StepWith(rounds.RoundBytes, rounds.RoundBytes, inflight, true)
    Assert.Equal("drain", group.Phase)
    near rounds.Rate group.Bandwidth

[<Theory>]
[<InlineData(20.0, 200.0)>]
[<InlineData(80.0, 8.0)>]
let ``a trickle on one direction does not end startup or move the other direction`` upMbit downMbit =
    let up = Rounds(upMbit, 400, 8)
    let down = Rounds(downMbit, 400, 8)
    // Uplink only carries ACK-sized traffic with no backlog; downlink is loaded.
    up.Loaded()
    for _ in 1 .. 4 do
        up.Step(up.RoundBytes / 50, up.RoundBytes / 50, up.Bdp)
        down.Loaded()
    Assert.True(up.Group.InStartup, up.Group.Describe())
    Assert.Equal("drain", down.Group.Phase)
    near up.Rate up.Group.Bandwidth
    near down.Rate down.Group.Bandwidth

/// A one-bottleneck link fed by the real controller: frames leave at the group's pace and
/// cwnd, queue at a bottleneck of capacity bytes/ns, and are ACKed one RTT after they leave
/// it. Before the bulk, `trickle` frames per RTT (ACK-only feedback) seed the estimate.
/// perAck: the paths report each delivered frame to the group (TunPath does). Returns the
/// time from bulk start until 100 ms of delivery reach 90 % of capacity, in ms.
let private rampMs (capacityMbit: float) (rttMs: int) (trickle: int) (perAck: bool) =
    let clock = Clock()
    let rtt = int64 rttMs * 1_000_000L
    let g = RateGroup(clock)
    let m = Member(rtt)
    g.Add m
    let cap = capacityMbit / 8000.0
    let L = 1344
    let flight = System.Collections.Generic.Queue<struct (int64 * int64 * int64 * int64 * int64)>()
    let mutable linkFree = 0L
    let send () =
        if g.Inflight = 0 && flight.Count = 0 then g.OnSendStart clock.Now
        let depart = max clock.Now linkFree + int64 (float L / cap)
        linkFree <- depart
        flight.Enqueue(struct (depart + rtt, g.DeliveredBytes, g.DeliveredNs, g.FirstSentNs, clock.Now))
        g.OnSent L
        m.Inflight <- m.Inflight + L
    let deliver () =
        let mutable n = 0
        while flight.Count > 0 && (let struct (at, _, _, _, _) = flight.Peek() in at <= clock.Now) do
            let struct (_, d, dn, fs, sn) = flight.Dequeue()
            m.Delivered <- m.Delivered + int64 L
            m.Inflight <- m.Inflight - L
            m.LastDelivered <- clock.Now
            if perAck then g.OnDelivered(L, d, dn, fs, sn, clock.Now)
            n <- n + L
        n
    // Feedback-only phase: a few frames per RTT for 4 s.
    for t in 1 .. 4000 do
        clock.Now <- clock.Now + 1_000_000L
        deliver () |> ignore
        if t % rttMs < trickle then send ()
        g.Service clock.Now
    let window = Array.zeroCreate<int> 100
    let mutable sum = 0
    let mutable result = -1
    let mutable t = 0
    while result < 0 && t < 20_000 do
        t <- t + 1
        clock.Now <- clock.Now + 1_000_000L
        let n = deliver ()
        sum <- sum + n - window.[t % 100]
        window.[t % 100] <- n
        g.NoteBacklog()
        while g.TryTake(L, true) do send ()
        g.Service clock.Now
        if t >= 100 && float sum >= 0.9 * cap * 100_000_000.0 then result <- t
        if System.Environment.GetEnvironmentVariable "RAMP_TRACE" = "1" && t % 100 = 0 then
            printfn "  t=%d perAck=%b win=%.1f Mbit %s" t perAck (float sum * 8.0 / 100_000.0) (g.Describe())
    result

[<Theory>]
[<InlineData(145.0, 400, 112)>]
[<InlineData(145.0, 400, 60)>]
[<InlineData(145.0, 400, 8)>]
[<InlineData(32.0, 400, 60)>]
[<InlineData(200.0, 40, 1)>]
let ``after an ACK-only direction, per-ACK samples fill the pipe sooner than interval samples`` capMbit rttMs trickle =
    let fast = rampMs capMbit rttMs trickle true
    let slow = rampMs capMbit rttMs trickle false
    printfn "ramp %g Mbit %d ms trickle %d: per-ACK %d ms, interval only %d ms" capMbit rttMs trickle fast slow
    Assert.True(fast > 0 && slow > 0)
    // The interval sampler applies a rate one sample late; per-ACK samples (BBR) at once.
    Assert.True(float fast <= 0.9 * float slow, sprintf "per-ACK %d ms vs interval %d ms" fast slow)
