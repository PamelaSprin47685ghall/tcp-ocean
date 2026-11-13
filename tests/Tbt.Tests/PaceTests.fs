module Tbt.Tests.PaceTests

open System
open Xunit
open Tbt.Carrier
open Tbt.Tests.SessionTests

let L = 1455

[<Fact>]
let ``PathPacer at zero rate never blocks`` () =
    let p = PathPacer(0.0)
    Assert.True(p.TryTake(L, 0L))
    p.OnSent L
    Assert.True(p.TryTake(L, 0L))

[<Fact>]
let ``PathPacer 4 Mbit starts with a 4-frame burst then waits for refill`` () =
    let p = PathPacer(4.0)
    let now = 1_000_000_000L
    let mutable n = 0
    while n < 8 && p.TryTake(L, now) do
        p.OnSent L
        n <- n + 1
    Assert.Equal(4, n)
    Assert.False(p.TryTake(L, now))
    // 1.25 × 4 Mbit × 20 ms (refill dt cap) ≈ 12.5 KiB: at least one more frame.
    Assert.True(p.TryTake(L, now + 20_000_000L))

[<Fact>]
let ``PathPacer depth follows 1.25 BDP and a late min-RTT does not grant a full BDP burst`` () =
    let p = PathPacer(4.0)
    let now = 1_000_000_000L
    p.SetMinRtt 400_000_000L
    let mutable sent = 0
    while sent < 300_000 && p.TryTake(L, now) do
        p.OnSent L
        sent <- sent + L
    // Starts at 4 frames, not 1.25 × 4 Mbit × 400 ms = 250 KiB.
    Assert.Equal(4 * L, sent)
    // Over a long wall-clock jump, refill is still capped at 20 ms (~12.5 KiB).
    Assert.True(p.TryTake(L, now + 400_000_000L))
    p.OnSent L
    sent <- sent + L
    let mutable extra = 0
    while extra < 20 && p.TryTake(L, now + 400_000_000L) do
        p.OnSent L
        extra <- extra + 1
    Assert.True(extra <= 8, sprintf "late min-RTT bursted %d extra frames" extra)

type Mem() =
    member val InflightV = 0 with get, set
    member val DeliveredV = 0L with get, set
    member val CopiesV = 0L with get, set
    member val MinRttV = 400_000_000L with get, set
    member val LastNsV = 0L with get, set
    interface IRateMember with
        member m.Inflight = m.InflightV
        member m.Delivered = m.DeliveredV
        member m.CopyEstimate = m.CopiesV
        member m.MinRttNs = m.MinRttV
        member m.LastDeliveredNs = m.LastNsV

[<Fact>]
let ``RateGroup before the first sample caps cwnd at 4 frames`` () =
    let clock = ManualClock()
    let g = RateGroup(clock)
    let m = Mem()
    g.Add m
    Assert.False(g.Ready)
    Assert.Equal(4 * L, g.Cwnd L)
    m.InflightV <- 4 * L
    Assert.False(g.TryTake(L, true))

[<Fact>]
let ``RateGroup repair bucket is charged separately and a dry reserve still allows new data`` () =
    let clock = ManualClock()
    let g = RateGroup(clock)
    let m = Mem()
    g.Add m
    // Prime a delivery sample: 100 frames in one 400 ms RTT.
    let bytes = int64 (100 * L)
    m.DeliveredV <- bytes
    m.LastNsV <- clock.Now
    g.OnSent(int bytes)
    m.InflightV <- 0
    clock.Now <- clock.Now + 400_000_000L
    g.Service clock.Now
    Assert.True(g.Ready)
    Assert.True(g.Bandwidth > 0.0)
    // Drain the repair reserve.
    let mutable repairs = 0
    while repairs < 10_000 && g.TryTakeRepair(L, L) do
        g.OnRepair L
        repairs <- repairs + 1
    Assert.True(repairs > 0, "repair reserve should allow at least one copy")
    Assert.False(g.TryTakeRepair(L, L))
    // New data still has its own tokens / cwnd (inflight is 0; tokens may be negative
    // from OnRepair, but the reserve lets them go to -reserve; after many repairs
    // tokens may also be dry). Advance a little so the data bucket refills at full
    // rate while repair is still half-rate dry.
    clock.Now <- clock.Now + 20_000_000L
    g.Service clock.Now
    Assert.True(g.TryTake(L, true), "new data must not starve when the repair reserve is dry")

[<Fact>]
let ``SignalLoss leaves startup and begins drain`` () =
    let clock = ManualClock()
    let g = RateGroup(clock)
    let m = Mem()
    g.Add m
    let primed = int64 (2000 * L)
    m.DeliveredV <- primed
    m.LastNsV <- clock.Now
    g.OnSent(int primed)
    clock.Now <- clock.Now + 400_000_000L
    g.Service clock.Now
    Assert.True(g.InStartup)
    g.SignalLoss()
    Assert.False(g.InStartup)

[<Fact>]
let ``client recovery samples do not evict the max filter; server samples still do`` () =
    let run protect =
        let clock = ManualClock()
        let g = RateGroup(clock)
        g.ProtectRecovery <- protect
        let m = Mem()
        g.Add m
        let primed = int64 (2000 * L)
        m.DeliveredV <- primed
        m.LastNsV <- clock.Now
        g.OnSent(int primed)
        m.InflightV <- 0
        clock.Now <- clock.Now + 400_000_000L
        g.Service clock.Now
        let peak = g.Bandwidth
        m.InflightV <- 4 * int (peak * 400_000_000.0)
        for _ in 1 .. 12 do
            m.DeliveredV <- m.DeliveredV + int64 (10 * L)
            m.LastNsV <- clock.Now + 400_000_000L
            g.OnRepair L
            g.TryTake(L, true) |> ignore
            clock.Now <- clock.Now + 400_000_000L
            g.Service clock.Now
        peak, g.Bandwidth
    let peakC, afterC = run true
    Assert.True(peakC * 8000.0 > 50.0, sprintf "primed bw was %f" (peakC * 8000.0))
    Assert.True(afterC >= peakC * 0.9, sprintf "client evicted %f to %f" (peakC * 8000.0) (afterC * 8000.0))
    let peakS, afterS = run false
    Assert.True(afterS < peakS * 0.5, sprintf "server should still take trickle samples (%f -> %f)" (peakS * 8000.0) (afterS * 8000.0))
