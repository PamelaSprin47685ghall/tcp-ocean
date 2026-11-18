module Tbt.Tests.TunTestsB

open System
open System.Buffers.Binary
open System.Collections.Generic
open Xunit
open Tbt.Carrier
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests

type private FakeMember() =
    member val Inflight = 0 with get, set
    member val Delivered = 0L with get, set
    interface IRateMember with
        member m.Inflight = m.Inflight
        member m.Delivered = m.Delivered
        member _.CopyEstimate = 0L
        member _.MinRttNs = 40_000_000L
        member _.LastDeliveredNs = 0L

[<Fact>]
let ``rate group: after startup the in-flight cap is 1.25 BDP, not 2`` () =
    let clock = ManualClock()
    let g = RateGroup(clock)
    let a = FakeMember()
    let b = FakeMember()
    g.Add a
    g.Add b
    // The bottleneck delivers 25 000 bytes per ms (200 Mbit/s) whatever is offered.
    for _ in 1 .. 4000 do
        clock.Now <- clock.Now + 1_000_000L
        g.OnSent 25_000
        a.Delivered <- a.Delivered + 12_500L
        b.Delivered <- b.Delivered + 12_500L
        // A backlogged sender: above the cap (the group refuses: not app-limited), except
        // in drain, where the queue empties below one BDP (1 MB).
        let each = if g.Describe().Contains "drain" then 400_000 else 2_000_000
        a.Inflight <- each
        b.Inflight <- each
        g.TryTake(L, true) |> ignore
        g.Service clock.Now
    Assert.False(g.InStartup, g.Describe())
    let bdp = g.Bandwidth * 40_000_000.0
    Assert.InRange(g.Bandwidth, 0.024, 0.026)
    Assert.InRange(float (g.Cwnd L), 1.25 * bdp, 1.25 * bdp + 4.0 * float L + 1.0)
    Assert.True(float (g.Cwnd L) < 1.5 * bdp)
    // In flight at the cap: no new frame.
    a.Inflight <- g.Cwnd L / 2
    b.Inflight <- g.Cwnd L / 2
    Assert.False(g.TryTake(L, false))

[<Fact>]
let ``rate group: after a 1 MB repair burst the next new frame waits until the pacer has paid it back`` () =
    // Facts: the bottleneck delivers 25 000 B per ms; at t the paths put 1 000 000 B of repair
    // copies on the wire. The next new frame is admitted only once that much time has passed.
    let clock = ManualClock()
    let g = RateGroup(clock)
    let a = FakeMember()
    g.Add a
    for _ in 1 .. 2000 do
        clock.Now <- clock.Now + 1_000_000L
        g.OnSent 25_000
        a.Delivered <- a.Delivered + 25_000L
        a.Inflight <- 0
        g.Service clock.Now
    Assert.True(g.Ready)
    clock.Now <- clock.Now + 10_000_000L
    Assert.True(g.TryTake(L, false))
    let wire0 = g.WireSentBytes
    g.OnRepair 1_000_000
    Assert.Equal(1_000_000L, g.RepairBytes)
    Assert.Equal(wire0 + 1_000_000L, g.WireSentBytes)
    // Pacing rate = gain x bw (the phase does not move: no Service call); the debt is 1 MB
    // less the bucket that was full (2 ms at the rate).
    let rate = g.Bandwidth * (if g.InStartup then 2.885 elif g.Cycle = 0 then 1.25 elif g.Cycle = 1 then 0.75 else 1.05)
    let firstOk =
        let mutable t = 0
        while t < 200 && not (g.TryTake(L, false)) do
            clock.Now <- clock.Now + 1_000_000L
            t <- t + 1
        t
    let expected = (1_000_000.0 - rate * 2_000_000.0) / rate / 1e6
    Assert.True(firstOk >= int (expected * 0.75) && firstOk <= int (expected * 1.6) + 2,
                sprintf "first new frame after %d ms, expected about %.1f ms (startup %b, phase %d)" firstOk expected g.InStartup g.Cycle)

[<Fact>]
let ``downlink: the two repair copies are charged to the rate group`` () =
    let clock = ManualClock()
    let group = RateGroup(clock)
    let f = Fixture(1, clock, group, 40000us)
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let starts = List<uint32>()
    for _ in 1 .. 3 do
        clock.Now <- clock.Now + 20_000_000L
        let mutable frame = Span<byte>.Empty
        Assert.True(ip.TryBeginFrame(&frame))
        frame.Fill 0x11uy
        ip.CommitFrame()
        starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
    let n = f.Out.Packets.Count
    let o = Array.zeroCreate<byte> 12
    o.[0] <- 1uy
    o.[1] <- 1uy
    o.[2] <- 5uy
    o.[3] <- 10uy
    BinaryPrimitives.WriteUInt32BigEndian(Span(o, 4, 4), starts.[2])
    BinaryPrimitives.WriteUInt32BigEndian(Span(o, 8, 4), starts.[2] + uint32 L)
    clock.Now <- clock.Now + 40_000_000L
    let struct (s, p) = clientSegment (f.Data0) (starts.[1]) Packet.Ack o ReadOnlySpan.Empty
    f.Path.OnSegment(s, Span p, clock.Now)
    let copies = f.Out.Packets.Count - n
    Assert.Equal(2, copies)
    Assert.Equal(int64 (copies * L), group.RepairBytes)

[<Fact>]
let ``downlink: a repaired segment is delivered once; its extra copy is a labeled estimate and wire cost`` () =
    // Facts: frames 0..2 at 20 ms steps; the client SACKs frame 2 with the cumulative ACK at
    // frame 1 (frame 1 missing); the server writes frame 1 twice at that moment; 40 ms later
    // the cumulative ACK covers frame 2.
    let clock = ManualClock()
    let group = RateGroup(clock)
    let f = Fixture(1, clock, group, 40000us)
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let starts = List<uint32>()
    for _ in 1 .. 3 do
        clock.Now <- clock.Now + 20_000_000L
        let mutable frame = Span<byte>.Empty
        Assert.True(ip.TryBeginFrame(&frame))
        frame.Fill 0x11uy
        ip.CommitFrame()
        starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
    let wire0 = f.Path.WireSentBytes
    let unique0 = f.Path.UniqueDelivered
    Assert.Equal(0L, f.Path.CopyEstimate)
    clock.Now <- clock.Now + 40_000_000L
    let n0 = f.Out.Packets.Count
    let struct (s1, p1) =
        clientSegment f.Data0 starts.[1] Packet.Ack (sackOption [ (starts.[2], starts.[2] + uint32 L) ]) ReadOnlySpan.Empty
    f.Path.OnSegment(s1, Span p1, clock.Now)
    // Sent: exactly two packets, both frame 1, both now.
    let sent = [ for i in n0 .. f.Out.Packets.Count - 1 -> parse f.Out.Packets.[i] ]
    Assert.Equal(2, sent.Length)
    for q in sent do
        Assert.Equal(starts.[1], q.Seq)
        Assert.Equal(L, q.PayloadLength)
    Assert.Equal(wire0 + 2L * int64 L, f.Path.WireSentBytes)
    // Received evidence so far: frame 0 (cumulative ACK) and frame 2 (SACK), once each.
    Assert.Equal(unique0 + 2L * int64 L, f.Path.UniqueDelivered)
    clock.Now <- clock.Now + 40_000_000L
    let struct (s2, p2) = clientSegment f.Data0 (starts.[2] + uint32 L) Packet.Ack [||] ReadOnlySpan.Empty
    f.Path.OnSegment(s2, Span p2, clock.Now)
    // Frame 1 is acked: unique delivery grows by one frame, not two.
    Assert.Equal(unique0 + 3L * int64 L, f.Path.UniqueDelivered)
    let m = f.Path :> IRateMember
    Assert.Equal(f.Path.UniqueDelivered, m.Delivered)
    // The second copy is not evidence: it is counted apart, as an estimate.
    Assert.Equal(int64 L, f.Path.CopyEstimate)
    Assert.Equal(int64 L, m.CopyEstimate)

[<Fact>]
let ``rate group: an ACK burst does not raise the estimate above the send rate`` () =
    let clock = ManualClock()
    let g = RateGroup(clock)
    let a = FakeMember()
    g.Add a
    // Steady: 25 000 B/ms sent and delivered (200 Mbit/s).
    let tick (delivered: int64) =
        clock.Now <- clock.Now + 1_000_000L
        g.OnSent 25_000
        a.Delivered <- a.Delivered + delivered
        a.Inflight <- 2_000_000
        g.TryTake(L, true) |> ignore
        g.Service clock.Now
    for _ in 1 .. 2000 do tick 25_000L
    let steady = g.Bandwidth
    Assert.InRange(steady, 0.0245, 0.0255)
    // A hole holds 40 ms of ACKs, then one cumulative ACK delivers them at once.
    for _ in 1 .. 40 do tick 0L
    tick (41L * 25_000L)
    for _ in 1 .. 20 do tick 25_000L
    Assert.InRange(g.Bandwidth, 0.0, steady * 1.05)

[<Fact>]
let ``rate group: the 0.75 phase ends once in flight is down to one BDP`` () =
    let clock = ManualClock()
    let g = RateGroup(clock)
    let a = FakeMember()
    g.Add a
    let tick (inflight: int) =
        clock.Now <- clock.Now + 1_000_000L
        g.OnSent 25_000
        a.Delivered <- a.Delivered + 25_000L
        a.Inflight <- inflight
        g.TryTake(L, true) |> ignore
        g.Service clock.Now
    let mutable n = 0
    while (g.InStartup || g.Describe().Contains "drain") && n < 4000 do
        tick (if g.Describe().Contains "drain" then 500_000 else 3_000_000)
        n <- n + 1
    // Run to the start of the 0.75 phase with in flight above one BDP (1 MB).
    n <- 0
    while g.Cycle <> 1 && n < 4000 do
        tick 1_200_000
        n <- n + 1
    Assert.Equal(1, g.Cycle)
    tick 1_200_000
    Assert.Equal(1, g.Cycle)
    // In flight falls to one BDP: the phase ends at once, not after a round (40 ms).
    tick 990_000
    Assert.Equal(2, g.Cycle)

[<Fact>]
let ``rate group: startup full-pipe check ignores app-limited idle and sub-BDP rounds`` () =
    // Dedicated line: after a short burst leaves fullCount nearly full, a long
    // app-limited idle (echo -> ping/uplink) must not let the next bulk exit
    // startup on pacer-limited rounds with almost nothing in flight. Otherwise
    // drain ends at once (inflight still below 1 BDP) and down1 crawls at
    // 1.25 x the leftover bw (64-path 96 vs 133 Mbit in 10 s).
    let clock = ManualClock()
    let g = RateGroup(clock)
    let a = FakeMember()
    g.Add a
    let tick (inflight: int) (bulk: bool) (sent: int) (burn: int) =
        clock.Now <- clock.Now + 1_000_000L
        g.OnSent sent
        a.Delivered <- a.Delivered + int64 sent
        a.Inflight <- inflight
        if burn > 0 then g.OnSent burn
        g.TryTake(L, bulk) |> ignore
        g.Service clock.Now
    // Learn 200 Mbit/s in startup (40 ms minRTT, 1 BDP = 1 MB). Three loaded
    // no-growth samples set leftover fullCount = 2 without exiting (4th would).
    for _ in 1 .. 120 do
        tick 3_000_000 true 25_000 0
    Assert.True(g.InStartup, g.Describe())
    Assert.InRange(g.Bandwidth, 0.024, 0.026)
    // App-limited idle (nothing in flight, low send).
    for _ in 1 .. 400 do
        tick 0 false 100 0
    Assert.True(g.InStartup, "idle must keep startup: " + g.Describe())
    Assert.InRange(g.Bandwidth, 0.024, 0.026)
    // Bulk resumes: pacer/cwnd limited but inflight well below 1 BDP (400 kB).
    for _ in 1 .. 200 do
        tick 400_000 true 25_000 1_000_000
    Assert.True(g.InStartup, "sub-BDP limited rounds must not end startup: " + g.Describe())
    // A loaded pipe (3 MB > 1 BDP) with no 25 % growth: three rounds end startup.
    let mutable n = 0
    while g.InStartup && n < 200 do
        tick 3_000_000 true 25_000 0
        n <- n + 1
    Assert.False(g.InStartup, "full pipe still ends startup: " + g.Describe())
    Assert.True(n >= 3 * 40 && n <= 5 * 40, sprintf "exited after %d ms, expected about 3 rounds (120 ms)" n)

[<Fact>]
let ``rate group: uplink and downlink keep separate estimates after idle`` () =
    // Dedicated lines are asymmetric and come in more than one bandwidth:
    // the client uplink RateGroup and the server downlink RateGroup each
    // learn their own pipe. An idle / direction switch must not copy one
    // estimate onto the other, and must not depend on a hard-coded rate.
    let clock = ManualClock()
    let up = RateGroup(clock)
    let down = RateGroup(clock)
    let um = FakeMember()
    let dm = FakeMember()
    up.Add um
    down.Add dm
    let feed (g: RateGroup) (m: FakeMember) inflight bulk sent burn =
        g.OnSent sent
        m.Delivered <- m.Delivered + int64 sent
        m.Inflight <- inflight
        if burn > 0 then g.OnSent burn
        g.TryTake(L, bulk) |> ignore
        g.Service clock.Now
    let step () = clock.Now <- clock.Now + 1_000_000L
    // 40 ms minRTT. Up 20 Mbit/s = 2500 B/ms (1 BDP = 100 kB).
    // Down 200 Mbit/s = 25 000 B/ms (1 BDP = 1 MB).
    for _ in 1 .. 120 do
        step ()
        feed up um 300_000 true 2_500 0
    Assert.True(up.InStartup, up.Describe())
    Assert.InRange(up.Bandwidth, 0.0024, 0.0026)
    for _ in 1 .. 120 do
        step ()
        feed down dm 3_000_000 true 25_000 0
    Assert.True(down.InStartup, down.Describe())
    Assert.InRange(down.Bandwidth, 0.024, 0.026)
    // App-limited idle on both (the opposite direction has the bulk).
    for _ in 1 .. 400 do
        step ()
        feed up um 0 false 20 0
        feed down dm 0 false 100 0
    Assert.True(up.InStartup, "up stays in startup: " + up.Describe())
    Assert.True(down.InStartup, "down stays in startup: " + down.Describe())
    Assert.InRange(up.Bandwidth, 0.0024, 0.0026)
    Assert.InRange(down.Bandwidth, 0.024, 0.026)
    Assert.True(down.Bandwidth > up.Bandwidth * 5.0, "estimates must stay an order of magnitude apart")
    // Downlink bulk resumes below 1 BDP: must not exit, must not fall to the 20 Mbit estimate.
    for _ in 1 .. 200 do
        step ()
        feed down dm 400_000 true 25_000 1_000_000
        feed up um 0 false 20 0
    Assert.True(down.InStartup, "down sub-BDP must not end startup: " + down.Describe())
    Assert.InRange(down.Bandwidth, 0.024, 0.026)
    Assert.InRange(up.Bandwidth, 0.0024, 0.0026)
    // A loaded downlink pipe still ends startup; the uplink estimate is untouched.
    let mutable n = 0
    while down.InStartup && n < 200 do
        step ()
        feed down dm 3_000_000 true 25_000 0
        feed up um 0 false 20 0
        n <- n + 1
    Assert.False(down.InStartup, "down full pipe still ends startup: " + down.Describe())
    Assert.True(up.InStartup, "up must not follow the downlink exit: " + up.Describe())
    Assert.InRange(up.Bandwidth, 0.0024, 0.0026)
    Assert.InRange(down.Bandwidth, 0.024, 0.026)

[<Fact>]
let ``rate group: leftover high bw still leaves startup once one BDP is in flight`` () =
    // 20/200 5 %: leftover group bw is already at/above the pipe. Startup must
    // end after 3 no-growth limited rounds with inflight >= 1 BDP, otherwise
    // 2.885 x pacing overshoots for the whole 10 s down1 window.
    let clock = ManualClock()
    let g = RateGroup(clock)
    let a = FakeMember()
    g.Add a
    let tick inflight bulk sent burn =
        clock.Now <- clock.Now + 1_000_000L
        g.OnSent sent
        a.Delivered <- a.Delivered + int64 sent
        a.Inflight <- inflight
        if burn > 0 then g.OnSent burn
        g.TryTake(L, bulk) |> ignore
        g.Service clock.Now
    for _ in 1 .. 120 do
        tick 3_000_000 true 25_000 0
    Assert.True(g.InStartup, g.Describe())
    Assert.InRange(g.Bandwidth, 0.024, 0.026)
    for _ in 1 .. 200 do
        tick 0 false 100 0
    Assert.True(g.InStartup, g.Describe())
    // ~1.2 BDP (1.2 MB) is loaded but below startup cwnd; burn the pacer so
    // the round is limited (as a backlogged sender hitting the 2 ms bucket).
    let mutable n = 0
    while g.InStartup && n < 200 do
        tick 1_200_000 true 25_000 1_000_000
        n <- n + 1
    Assert.False(g.InStartup, "1.2 BDP no-growth must end startup: " + g.Describe())
    Assert.True(n >= 40 && n <= 200, sprintf "exited after %d ms" n)
    Assert.InRange(g.Bandwidth, 0.024, 0.026)

let blackoutRunWith (sack: bool) (paths: int) (lossP: float) (seed: int) (lost: int64 -> bool) (ms: int) =
    // netns (5 % loss + 300 ms outage every 3 s): all paths stalled together, all were demoted,
    // no segment was sent again and the downlink stayed dead for minutes.
    // Two paths in one group; a model receiver per path (20 ms one way, cumulative ACKs).
    // Every packet in [500 ms, 1800 ms) is lost.
    let clock = ManualClock()
    let group = RateGroup(clock)
    let fs = [| for k in 0 .. paths - 1 -> Fixture(1, clock, group, uint16 (40000 + k)) |]
    let rnd = Random(seed)
    clock.Now <- clock.Now + 40_000_000L
    let t0 = clock.Now
    for f in fs do
        f.Open()
        group.Add(f.Path :> IRateMember)
        (f.Path :> IPath).Backlog <- 100
    let seen = [| for _ in fs -> fs.[0].Out.Packets.Count |]
    for i in 0 .. fs.Length - 1 do seen.[i] <- fs.[i].Out.Packets.Count
    let got = [| for _ in fs -> Dictionary<uint32, int>() |]
    let cum = Array.zeroCreate<uint32> paths
    let acks = [| for _ in fs -> Queue<struct (int64 * uint32 * byte[])>() |]
    let be (v: uint32) = [| byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]
    // SACK blocks as Linux sends them: up to 3 received ranges above the cumulative ACK.
    let sackOf (k: int) =
        let ranges = List<struct (uint32 * uint32)>()
        for kv in got.[k] |> Seq.filter (fun kv -> kv.Key > cum.[k]) |> Seq.sortBy (fun kv -> kv.Key) do
            if ranges.Count > 0 && (let struct (_, r) = ranges.[ranges.Count - 1] in r = kv.Key) then
                let struct (l, _) = ranges.[ranges.Count - 1]
                ranges.[ranges.Count - 1] <- struct (l, kv.Key + uint32 kv.Value)
            else ranges.Add(struct (kv.Key, kv.Key + uint32 kv.Value))
        let top = ranges |> Seq.rev |> Seq.truncate 3 |> Seq.toArray
        if top.Length = 0 then [||]
        else Array.concat [ yield [| 1uy; 1uy; 5uy; byte (2 + 8 * top.Length) |]
                            for struct (l, r) in top do yield be l; yield be r ]
    let lost (t: int64) = lost (t - t0)
    let sentIn = Array.zeroCreate<int> paths
    let resentIn = Array.zeroCreate<int> paths
    let sentAfter = Array.zeroCreate<int> paths
    let cumAtBlack = Array.zeroCreate<uint32> paths
    let starts = [| for _ in fs -> HashSet<uint32>() |]
    let lastProgress = Array.zeroCreate<int64> paths
    for _ in 1 .. ms do
        clock.Now <- clock.Now + 1_000_000L
        let now = clock.Now
        for k in 0 .. paths - 1 do
            let f = fs.[k]
            // Packets the server wrote since the last round reach the client 20 ms later.
            for i in seen.[k] .. f.Out.Packets.Count - 1 do
                let q = parse f.Out.Packets.[i]
                if q.PayloadLength > 0 then
                    if cum.[k] = 0u then cum.[k] <- q.Seq
                    let fresh = starts.[k].Add q.Seq
                    if lost now then (if fresh then sentIn.[k] <- sentIn.[k] + 1 else resentIn.[k] <- resentIn.[k] + 1)
                    elif now - t0 >= 1_800_000_000L then sentAfter.[k] <- sentAfter.[k] + 1
                    if not (lost now) && rnd.NextDouble() >= lossP then
                        got.[k].[q.Seq] <- q.PayloadLength
                        let mutable l = 0
                        while got.[k].TryGetValue(cum.[k], &l) do
                            cum.[k] <- cum.[k] + uint32 l
                            lastProgress.[k] <- now - t0
                        acks.[k].Enqueue(struct (now + 40_000_000L, cum.[k], (if sack then sackOf k else [||])))
            seen.[k] <- f.Out.Packets.Count
            if now - t0 = 500_000_000L then cumAtBlack.[k] <- cum.[k]
            // ACKs come back 20 ms later (40 ms after the send); those in the blackout are lost.
            let mutable a = Unchecked.defaultof<struct (int64 * uint32 * byte[])>
            while acks.[k].TryPeek(&a) && (let struct (due, _, _) = a in due <= now) do
                let struct (due, c, sk) = acks.[k].Dequeue()
                if not (lost (due - 20_000_000L)) && rnd.NextDouble() >= lossP then
                    let struct (s, p) = clientSegment f.Data0 c Packet.Ack sk ReadOnlySpan.Empty
                    f.Path.OnSegment(s, Span p, now)
            let ip = f.Path :> IPath
            let mutable frame = Span<byte>.Empty
            let mutable n = 0
            while n < 4 && ip.TryBeginFrame(&frame) do
                frame.Fill 0x66uy
                ip.CommitFrame()
                n <- n + 1
            f.Path.Service now
        group.Service now
    [ for k in 0 .. paths - 1 ->
        sprintf "path %d: re-sent in blackout %d, sent after %d, last progress %d ms, cum %d; %s" k resentIn.[k] sentAfter.[k] (lastProgress.[k] / 1_000_000L) (int (cum.[k] - cumAtBlack.[k])) (fs.[k].Path.Describe()), lastProgress.[k], fs.[k].Path.Counters.Demotions ]

let blackoutRun = blackoutRunWith true

