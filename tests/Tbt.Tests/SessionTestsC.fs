module Tbt.Tests.SessionTestsC

open System
open System.Collections.Generic
open System.IO
open Xunit
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Crypto.Bootstrap
open Tbt.Tests.SessionTests
open Tbt.Tests.SessionTestsB

let karnCase (loss: float) (blackMs: int) =
    // RTT 40 ms, 5 % loss. A 300 ms blackout strands many steps; their repair waits must not
    // enter SRTT, else repair rounds (1.05 x SRTT apart) slow down, SRTT grows, and the
    // tail crawls for seconds (seen in netns: srtt 286 -> 1428 ms, 4-8 steps/s).
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, loss, 31)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init ((500 + blackMs) * 8_000) (fun i -> byte (i * 7))
    let mutable sent = 0
    let feed () = sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, min 8_000 (payload.Length - sent)), false)
    for _ in 1 .. 500 do
        feed ()
        pair.Round() |> ignore
    let srtt0 = c.Tx.SrttNs
    pair.Blackout <- true
    for _ in 1 .. blackMs do
        feed ()
        pair.Round() |> ignore
    pair.Blackout <- false
    let mutable maxSrtt = 0.0
    let mutable r = 0
    while r < 20_000 && pair.ServerPort.Stream(slot).Length < int64 payload.Length do
        feed ()
        pair.Round() |> ignore
        maxSrtt <- max maxSrtt c.Tx.SrttNs
        r <- r + 1
    let msg =
        sprintf
            "srtt before %.0f ms, max after %.0f ms, end %.0f ms; %d ms to finish; boosters %d; minrtt %.0f; wait %.0f; skipped %d; repeats %d; head %d; peerAck %d; recovered %d"
            (srtt0 / 1e6) (maxSrtt / 1e6) (c.Tx.SrttNs / 1e6) r c.Tx.BoostersSent
            (float c.Tx.MinRttNs / 1e6) (c.Tx.RepairWaitNs / 1e6) c.Tx.RttSkipped c.Tx.RepairRepeats
            c.Tx.Head c.Tx.PeerAck c.Rx.Counters.Recovered
    Assert.True(pair.ServerPort.Stream(slot).Length = int64 payload.Length, msg)
    Assert.Equal<byte[]>(payload, pair.ServerPort.Stream(slot).ToArray())
    // SRTT stays near the path RTT (40 ms + queue), not the stall.
    Assert.True(maxSrtt < 4.0 * max srtt0 40e6, msg)
    Assert.True(r < 6_000, msg)
    msg

[<Theory>]
[<InlineData(0.02, 300)>]
[<InlineData(0.05, 300)>]
[<InlineData(0.10, 300)>]
let ``a repair stall does not inflate SRTT and slow every later repair (Karn)`` (loss: float, blackMs: int) =
    // Before the fix: 2 %: SRTT 807 ms; 5 % and 10 %: SRTT 5.9 / 6.3 s, tail unfinished after 20 s.
    karnCase loss blackMs |> ignore

[<Fact>]
let ``Karn skips samples held behind a repair but keeps clean ones`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, 0.05, 7)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let data = Array.zeroCreate<byte> 8_000
    for r in 1 .. 1000 do
        pair.Blackout <- r > 500 && r <= 600
        pair.Feed(c, slot, ReadOnlySpan data, false) |> ignore
        pair.Round() |> ignore
    Assert.True(
        c.Tx.RttSkipped > 0L,
        sprintf "skipped=%d boosters=%d repeats=%d head=%d peerAck=%d dropped=%d"
            c.Tx.RttSkipped c.Tx.BoostersSent c.Tx.RepairRepeats c.Tx.Head c.Tx.PeerAck pair.Dropped)
    let m = c.Tx.MinRttNs
    Assert.True(m >= 40_000_000L && m < 60_000_000L, sprintf "min rtt %d" m)
    Assert.True(c.Tx.RepairWaitNs <= 4.0 * float m)

[<Fact>]
let ``a jump in peer ack samples only the newest sealed step, not the oldest`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            let took = pair.Feed(c, slot, ReadOnlySpan chunk, false)
            c.Tx.Flush()
            guard <- guard - 1
            if took = 0 && c.Tx.Head < n then failwith (sprintf "feed stuck at head %d" c.Tx.Head)
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 2UL
    Assert.Equal(0UL, c.Tx.PeerAck)
    // The first steps age 8 s, then two more seal, then an ack covers all after 400 ms.
    clock.Now <- clock.Now + 8_000_000_000L
    let afterGap = c.Tx.Head
    sealUntil (afterGap + 2UL)
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    // Newest step: 400 ms → SRTT 100 + (400-100)/8 = 137.5 ms. The 8 s gap would
    // have pushed SRTT above a second if the oldest step in the jump were used.
    Assert.InRange(c.Tx.SrttNs, 130e6, 150e6)

[<Fact>]
let ``an oversized seal-to-custody sample is clamped to three times minRTT`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            let took = pair.Feed(c, slot, ReadOnlySpan chunk, false)
            c.Tx.Flush()
            guard <- guard - 1
            if took = 0 && c.Tx.Head < n then failwith (sprintf "feed stuck at head %d" c.Tx.Head)
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 1UL
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    let head0 = c.Tx.Head
    sealUntil (head0 + 1UL)
    clock.Now <- clock.Now + 8_000_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    // minRTT stays 400 ms; the 8 s sample is used as 1.2 s. SRTT starts at
    // 137.5 ms after the first sample, then 137.5 + (1200-137.5)/8 = 270.3 ms.
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    Assert.InRange(c.Tx.SrttNs, 250e6, 300e6)
    // Deadline is clamped SRTT + 4·RTTVAR, never below minRTT or above 4×minRTT.
    // It must not follow the 8 s sample, and must exceed 1.05×minRTT once queueing/var is known.
    Assert.True(c.Tx.RepairWaitNs >= float c.Tx.MinRttNs, sprintf "wait %g min %d" c.Tx.RepairWaitNs c.Tx.MinRttNs)
    Assert.True(c.Tx.RepairWaitNs <= 4.0 * float c.Tx.MinRttNs, sprintf "wait %g" c.Tx.RepairWaitNs)
    Assert.True(c.Tx.RepairWaitNs < 2_000_000_000.0, sprintf "wait followed the 8 s sample: %g" c.Tx.RepairWaitNs)


[<Fact>]
let ``default repair is main's: a new range NACK enqueues BOOSTERs immediately`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    Assert.False(c.Tx.NewRepair)
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    // Main: first NACK of a new range does not wait. The new scheduler would
    // arm the step and wait the tail timer (~1.25×minRTT).
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "main OnNack must enqueue the first round now")
    Assert.True(c.Tx.RepairWaitNs = 1.05 * float c.Tx.MinRttNs, sprintf "wait %g" c.Tx.RepairWaitNs)

[<Fact>]
let ``a stale cached now cannot make a just-sealed step look RepairWait-expired`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let mutable cached = Unchecked.defaultof<CachedClock>
    let pair = Pair(config, 1, 0.0, 4, fun inner ->
        // No auto-refresh: a long iteration leaves NowNs stale, as the old
        // per-loop Tick cache did. Seal and OnNack must still go through FreshNs.
        cached <- CachedClock(inner, staleMs=1_000_000L, refreshEvery=Int32.MaxValue)
        cached :> IClock)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    cached.Tick() |> ignore
    clock.Now <- clock.Now + 400_000_000L
    cached.Tick() |> ignore
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    let wait = int64 c.Tx.RepairWaitNs
    let t0 = clock.Now
    cached.Tick() |> ignore
    // Inner time jumps many RepairWaits; the cached NowNs stays at t0.
    clock.Now <- t0 + 5L * wait
    let head0 = c.Tx.Head
    guard <- 64
    while c.Tx.Head <= head0 && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    Assert.True(c.Tx.Head > head0, "a new step must have been sealed while the cache was stale")
    let tLo = c.Tx.Head - 1UL
    c.Tx.OnNack(tLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting,
        "FreshNs must stamp the seal at the advanced time; a stale cache would already be past seal+RepairWait")

[<Fact>]
let ``repair wait is minRTT plus measured queueing and variance, not 1.05 times minRTT alone`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            let took = pair.Feed(c, slot, ReadOnlySpan chunk, false)
            c.Tx.Flush()
            guard <- guard - 1
            if took = 0 && c.Tx.Head < n then failwith (sprintf "feed stuck at head %d" c.Tx.Head)
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 1UL
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    // A standing queue of 80 ms (20 % of minRTT) on later samples.
    let head0 = c.Tx.Head
    sealUntil (head0 + 3UL)
    clock.Now <- clock.Now + 480_000_000L
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    // 1.05 × minRTT is 420 ms and would fire inside the queueing delay.
    Assert.True(c.Tx.RepairWaitNs > 1.05 * 400e6 + 1.0, sprintf "wait %g still 1.05×minRTT" c.Tx.RepairWaitNs)
    Assert.True(c.Tx.RepairWaitNs <= 4.0 * 400e6, sprintf "wait %g" c.Tx.RepairWaitNs)

[<Fact>]
let ``a NACK of the outstanding tail waits the long tail timer, not the short reorder window`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    let tLo = 1UL
    Assert.True(c.Tx.Head > tLo)
    // No later step has been seen: this is the tail. minRTT + reo_wnd is not enough.
    let evidenceAt = sealedAt + c.Tx.MinRttNs + c.Tx.ReoWndNs
    clock.Now <- evidenceAt
    c.Tx.OnNack(tLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "no later-step evidence: the short reo window must not fire")
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "tail timer passed; a repair round is due")

[<Fact>]
let ``a step is presumed lost only after a later-sealed step is seen and the reorder window passes`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    c.Tx.EvidenceRepair <- true
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 5UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    // The server has seen step 4 (WIN_ACK t_hi) while the frontier still sits at 1.
    c.Tx.OnPeerSeen 4UL
    Assert.Equal(4UL, c.Tx.PeerSeenHi)
    c.Tx.OnNack(1UL, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "later step seen, but its arrival plus reo_wnd has not passed")
    clock.Now <- sealedAt + c.Tx.MinRttNs + c.Tx.ReoWndNs
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "later-sealed step seen and reorder window passed")
    Assert.True(clock.Now < sealedAt + int64 c.Tx.RepairWaitNs, "evidence must not wait the tail timer")

[<Fact>]
let ``a NACK does not enqueue repairs before a minRTT sample exists`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let sealedAt = clock.Now
    Assert.True(c.Tx.MinRttNs = Int64.MaxValue)
    let tLo = 1UL
    Assert.True(c.Tx.Head > tLo)
    clock.Now <- sealedAt + 200_000_000L
    c.Tx.OnNack(tLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "no expected arrival until a minRTT sample")
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "tail timer passed after a minRTT sample")

[<Fact>]
let ``a NACK repairs only the steps of the range whose originals are due`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let sealUntil (n: uint64) =
        let mutable guard = 64
        while c.Tx.Head < n && guard > 0 do
            pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
            c.Tx.Flush()
            guard <- guard - 1
        Assert.True(c.Tx.Head >= n, sprintf "head %d want %d" c.Tx.Head n)
    sealUntil 2UL
    let earlySealed = clock.Now
    clock.Now <- earlySealed + 400_000_000L
    let tLo = c.Tx.Head - 1UL
    c.Tx.OnPeerAck(tLo, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    let wait = c.Tx.RepairWaitNs
    clock.Now <- earlySealed + int64 wait - 1_000_000L
    sealUntil (c.Tx.Head + 4UL)
    let span = int (c.Tx.Head - tLo)
    Assert.True(span >= 4, sprintf "span %d" span)
    clock.Now <- earlySealed + int64 wait
    c.Tx.OnNack(tLo, span, 30)
    Assert.True(c.Tx.RepairWaiting, "due prefix should repair")
    let frame = Array.zeroCreate<byte> c.FrameLength
    let before = c.Tx.BoostersSent
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    let sent = c.Tx.BoostersSent - before
    Assert.True(sent >= 1L && sent <= 16L, sprintf "boosters %d span %d wait %g" sent span wait)
    Assert.False(c.Tx.RepairWaiting)

[<Fact>]
let ``adaptive FEC lowers rho on a clean path`` () =
    let pair = Pair({ SessionConfig.defaults with FecAdaptive = true }, 1, 0.0, 71)
    let payload = Array.init 2_000_000 (fun i -> byte (i * 13))
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    let mutable sent = 0
    for _ in 1 .. 1500 do
        if sent < payload.Length then
            sent <- sent + pair.Feed(pair.Client, slot, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
    Assert.Equal(0, pair.Dropped)
    Assert.True(pair.Client.Tx.Rho < 0.25, sprintf "clean rho %f" pair.Client.Tx.Rho)
    Assert.True(pair.Client.Tx.Rho >= 0.01, sprintf "clean rho floor %f" pair.Client.Tx.Rho)

[<Fact>]
let ``adaptive FEC raises rho when the uplink loses cells`` () =
    let pair = Pair({ SessionConfig.defaults with FecAdaptive = true; Rho = 0.02 }, 1, 0.10, 72)
    let payload = Array.init 1_500_000 (fun i -> byte (i * 17))
    let mutable slot = -1
    Assert.True(pair.Client.TryOpenFlow(&slot))
    let mutable sent = 0
    for _ in 1 .. 1500 do
        if sent < payload.Length then
            sent <- sent + pair.Feed(pair.Client, slot, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
    Assert.True(pair.Dropped > 0, "the pair must actually drop cells")
    Assert.True(pair.Client.Tx.Rho > 0.02, sprintf "lossy rho %f" pair.Client.Tx.Rho)

