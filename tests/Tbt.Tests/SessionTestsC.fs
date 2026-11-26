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
    // RFC 6298: the first clean sample becomes SRTT itself (400 ms), not an EWMA
    // from a 100 ms guess. The 8 s gap would have pushed SRTT above a second if
    // the oldest step in the jump were used.
    Assert.InRange(c.Tx.SrttNs, 390e6, 410e6)

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
    // minRTT stays 400 ms; the 8 s sample is used as 1.2 s. RFC 6298 seeds SRTT
    // at 400 ms and RTTVAR at 200 ms, so SRTT = 400 + (1200-400)/8 = 500 ms.
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    Assert.InRange(c.Tx.SrttNs, 480e6, 520e6)
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
    c.Tx.OnNack(1UL, 1, 4, 0UL)
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
    c.Tx.OnNack(tLo, 1, 4, 0UL)
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
    c.Tx.OnNack(tLo, 1, 4, 0UL)
    Assert.False(c.Tx.RepairWaiting, "no later-step evidence: the short reo window must not fire")
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4, 0UL)
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
    c.Tx.OnNack(1UL, 1, 4, 0UL)
    Assert.False(c.Tx.RepairWaiting, "later step seen, but its arrival plus reo_wnd has not passed")
    clock.Now <- sealedAt + c.Tx.MinRttNs + c.Tx.ReoWndNs
    c.Tx.OnNack(1UL, 1, 4, 0UL)
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
    c.Tx.OnNack(tLo, 1, 4, 0UL)
    Assert.False(c.Tx.RepairWaiting, "no expected arrival until a minRTT sample")
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.True(c.Tx.MinRttNs <> Int64.MaxValue)
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4, 0UL)
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
    c.Tx.OnNack(tLo, span, 30, 0UL)
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


/// Run ms rounds (1 ms each) with one-way delay owMs, feeding a little data per round.
let private runFor (pair: Pair) (c: Session) (slot: int) (owMs: int64) (ms: int) =
    pair.Delay <- owMs * 1_000_000L
    let data = Array.zeroCreate<byte> 2_000
    for _ in 1 .. ms do
        pair.Feed(c, slot, ReadOnlySpan data, false) |> ignore
        pair.Round() |> ignore

let private minRttPair () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    pair, c, slot

let private near (want: int64) (got: int64) =
    Assert.True(got >= want && got < want + 20_000_000L, sprintf "min rtt %d ms, want ~%d ms" (got / 1_000_000L) (want / 1_000_000L))

[<Fact>]
let ``minRTT survives idle gaps and is not re-seeded by inflated burst samples`` () =
    // Live downlink: minrtt 310 -> 1499 ms across idle gaps between downloads; the
    // samples of each burst are seal-to-ack behind head-of-line waits, not path RTT.
    // Old window: one idle gap moved the true minimum to the previous bucket, the next
    // dropped it, and the burst's 900 ms became minRTT (and 1.05 x it the repair wait).
    let pair, c, slot = minRttPair ()
    runFor pair c slot 165L 3_000
    near 330_000_000L c.Tx.MinRttNs
    for _ in 1 .. 6 do
        pair.Clock.Now <- pair.Clock.Now + 25_000_000_000L
        runFor pair c slot 450L 3_000
        near 330_000_000L c.Tx.MinRttNs
    Assert.True(c.Tx.RepairWaitNs < 400e6, sprintf "repair wait %g" c.Tx.RepairWaitNs)

[<Fact>]
let ``minRTT does not ratchet up during 45 s of inflated samples`` () =
    let pair, c, slot = minRttPair ()
    runFor pair c slot 165L 3_000
    // 2.4 x min: inflated, but under the 3 x guard. The old window adopted it after 20 s.
    runFor pair c slot 400L 45_000
    near 330_000_000L c.Tx.MinRttNs

[<Fact>]
let ``minRTT follows a lasting RTT increase after about a minute`` () =
    let pair, c, slot = minRttPair ()
    runFor pair c slot 50L 3_000
    near 100_000_000L c.Tx.MinRttNs
    runFor pair c slot 125L 75_000
    near 250_000_000L c.Tx.MinRttNs

// ---- Timely (deadline) erasure, the p95 field and the decoded bitmap (spec 6.10) ----

[<Fact>]
let ``a lossy carrier reports a timely erasure rate near its loss, not the permanent-loss floor`` () =
    // The 24 %-loss live link reported cell_loss 0.0001 because the fold waited until the
    // carrier stopped carrying: a lost cell arrives late, not never, so it looked received.
    // A late cell is an erased cell (spec 6.10), and rho is sized from this report.
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, 0.20, 991)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init (8_000 * 964) (fun i -> byte (i * 13 + 7))
    let mutable sent = 0
    for _ in 1 .. 900 do
        if sent < payload.Length then
            sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, min 8_000 (payload.Length - sent)), false)
        pair.Round() |> ignore
    // The uplink receiver is the server: only it sees step cells, so only it can report
    // the erasure rate this direction's FEC controller is sized from.
    let rx = pair.Server.Rx
    let q = int rx.ErasureQ16
    let p = int rx.ErasureP95Q16
    let msg = sprintf "erasure q16 %d p95 %d folds %d recovered %d" q p rx.EstimateFolds rx.Counters.Recovered
    Assert.True(q >= 6_000, msg)
    Assert.True(q <= 32_000, msg)
    // The p95 is over active 10 ms windows, the mean over cells; on a steady loss
    // they agree within one window's granularity.
    Assert.True(p + 2_000 >= q, msg)

/// A lossy path reports enough erasure that redundancy must rise, even when its measured
/// SRTT looks like congestion: with rho pinned at its floor every step's frontier waits for a
/// TCP retransmission, and that wait is what holds SRTT high.
let private rhoAfter (reports: int) (mean: float) (burst: float) (seed: int) =
    // FecAdaptive is what the CLI enables by default (TBTUN_FEC_ADAPTIVE unset => on); the
    // library default is off, so the test must ask for the controller explicitly.
    let config =
        { SessionConfig.defaults with
            FecAdaptive = true
            Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 2, 0.0, seed)
    for _ in 1 .. reports do
        pair.Client.Tx.OnPeerErasure(uint16 (mean * 65536.0), uint16 (burst * 65536.0))
    pair.Client.Tx.Rho

[<Fact>]
let ``rho rises with reported erasure instead of staying at its floor`` () =
    // peerErasure is an EWMA over FecWindowCells (64) reports, so convergence takes ~2 windows;
    // the live link gets one report per WIN_ACK, i.e. a second or two.
    let rho = rhoAfter 120 0.24 0.32 995
    Assert.True(rho >= 0.28, sprintf "rho stuck at %.3f under 24 %% reported erasure" rho)
    Assert.True(rho <= 0.45, sprintf "rho %.3f above its cap" rho)

[<Fact>]
let ``rho stays near its floor on a clean carrier`` () =
    let rho = rhoAfter 120 0.0 0.0 996
    Assert.True(rho <= 0.10, sprintf "rho %.3f on a 0 %% link" rho)

[<Fact>]
let ``erasure evidence is attributed to a rebuilt source or to the clock, never both`` () =
    // The controller is sized from ErasureQ16, which says how much was erased. The three
    // counters say why: a source the decoder had to rebuild is certain proof a cell did not
    // arrive, whereas a source late by the step clock alone may merely have been queued
    // behind another path. Without the split, "0.24 reported on a lossless link" is an
    // unattributable number, which is exactly the ambiguity that stalled three live rounds.
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, 0.20, 997)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init (8_000 * 964) (fun i -> byte (i * 13 + 7))
    let mutable sent = 0
    for _ in 1 .. 900 do
        if sent < payload.Length then
            sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, min 8_000 (payload.Length - sent)), false)
        pair.Round() |> ignore
    let rx = pair.Server.Rx
    let due = rx.Counters.TimelyDue
    let rebuilt = rx.Counters.ErasureRecovered
    let clockOnly = rx.Counters.ErasureLateClock
    // The ledger must actually have accounted sources, or the other assertions are vacuous.
    Assert.True(due > 0L, "the timely ledger never accounted a source")
    // A rebuilt source is also late, so it is counted once, in the stronger bucket only.
    // The two erased buckets therefore partition a subset of `due`, never more.
    Assert.True(rebuilt + clockOnly <= due, sprintf "rebuilt %d + clock %d > due %d" rebuilt clockOnly due)
    // A source is rebuilt at most once, so its account can never exceed the decoder's own.
    Assert.True(rebuilt <= rx.Counters.Recovered, sprintf "rebuilt %d > recovered %d" rebuilt rx.Counters.Recovered)
    // 20 % cell loss must register as rebuilt sources; a zero here means the split is unwired.
    Assert.True(rebuilt > 0L, sprintf "20 %% loss produced no rebuilt source (due %d)" due)

[<Fact>]
let ``a duplicate DATA cell cannot withdraw the same pending rebuild twice`` () =
    // Duplicates are normal here: a repair sends the same cell again and TCP retransmits a
    // segment. The ledger withdrew one pending erasure per arriving DATA cell of that slot
    // while the decoder's rebuild flag stayed set, so a second copy withdrew a second one and
    // erasures were silently under-counted. Two consumer-visible consequences are checked:
    // withdrawals never outnumber rebuilds, and the same session replays identically.
    let run () =
        let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
        let pair = Pair(config, 4, 0.20, 998)
        pair.Delay <- 20_000_000L
        let c = pair.Client
        let mutable slot = -1
        Assert.True(c.TryOpenFlow(&slot))
        let payload = Array.init (8_000 * 964) (fun i -> byte (i * 13 + 7))
        let mutable sent = 0
        for _ in 1 .. 900 do
            if sent < payload.Length then
                sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, min 8_000 (payload.Length - sent)), false)
            pair.Round() |> ignore
        let rx = pair.Server.Rx
        (rx.Counters.ErasureRecovered, rx.Counters.ErasureCancelled, rx.Counters.TimelyDue, rx.ErasureQ16)
    let rebuilt, cancelled, due, rate = run ()
    Assert.True(rebuilt > 0L, "20 % loss produced no rebuilt source, so the check would be vacuous")
    // One withdrawn rebuild, at most one acknowledgement: a duplicate must not pay twice.
    Assert.True(cancelled <= rebuilt, sprintf "cancelled %d > rebuilt %d" cancelled rebuilt)
    // The rate the controller reads must sit near the injected 20 % loss: not collapsed to
    // zero (duplicates and withdrawals eating the evidence) and not saturated near 1.0.
    let q = int rate
    Assert.True(q >= 3_000 && q <= 32_000, sprintf "erasure q16 %d is outside the 20 %% band" q)
    // Determinism: identical inputs must give identical accounting, not a race-dependent count.
    let rebuilt2, cancelled2, due2, rate2 = run ()
    Assert.Equal(rebuilt, rebuilt2)
    Assert.Equal(cancelled, cancelled2)
    Assert.Equal(due, due2)
    Assert.Equal(rate, rate2)

[<Fact>]
let ``nack waits before deadline when frontier is near uint64 maximum`` () =
    let frontier = UInt64.MaxValue - 10UL
    let mutable bitmapQueries = 0
    let decoder =
        { new Tbt.Codec.ICodecDecoder with
            member _.Frontier = frontier
            member _.SeenEnd = frontier + 1UL
            member _.KConflicts = 0L
            member _.BaseStep = frontier
            member _.PendingRows = 0
            member _.RowsAllocated = 0
            member _.FreeRows = 0
            member _.StaleRows = 0L
            member _.RowMemoryBytes = 0L
            member _.IsStepComplete _ =
                bitmapQueries <- bitmapQueries + 1
                false
            member _.WasRecovered(_, _) = false
            member _.AcknowledgeRecovered(_, _) = ()
            member _.StepKnownCount _ = 0
            member _.BuildDecodedBitmap _ = struct (0UL, 0UL, 0UL, 0UL)
            member _.Unknowns(_, _) = 1
            member _.Deficit(_, _) = 1
            member _.NoteSeen _ = true
            member _.NoteStepUsed(_, _) = true
            member _.OfferSource(_, _, _, _) = true
            member _.OfferParity(_, _, _, _, _, _) = Tbt.Codec.EquationResult.Useless
            member _.OfferBooster(_, _, _, _) = Tbt.Codec.EquationResult.Useless
            member _.TryTakeKnown(_, _, _) = false
            member _.StepUsed _ = 1
            member _.Source(_, _) = ReadOnlySpan<byte>.Empty
            member _.Release _ = () }
    let opener = Tbt.Crypto.CellOpener(Tbt.Contracts.Direction.Up, 64, Tbt.Crypto.KeyTable(), Tbt.Crypto.NoHeaderMask())
    let rx = RxHalf(64, CodeField.Gf16, decoder, opener, 64, 4)
    let mutable span = 0
    let mutable deficit = 0
    Assert.False(rx.TryNack(1_000_000L, &span, &deficit))
    let struct (b0, b1, b2, b3) = rx.FillDecodedBitmap()
    Assert.Equal(0UL, b0 ||| b1 ||| b2 ||| b3)
    Assert.Equal(0, bitmapQueries)

/// Drive one RxHalf deadline ledger with controllable virtual time.
///
/// Three DATA cells of step 0 arrive (slots 0..2 of k=4) and a BOOSTER over the same step then
/// leaves only slot 3 unknown, so elimination solves it: that report is `recovered = true`.
/// The BOOSTER's `StepUsed` is its SPAN (`Decoder.OfferBooster(tLo, span, seq, payload)`), not
/// the step's source count: passing 4 made the row span four steps, so the row never reduced to
/// a single unknown and nothing was rebuilt — which is exactly why an earlier attempt at this
/// test asserted `ErasureRecovered = 1` against 0.
let private runRebuild (arriveInside: bool) (duplicate: bool) (never: bool) =
    let symbol = 64
    let dec = Tbt.Codec.Decoder(11UL, symbol, 64, 256)
    let opener =
        Tbt.Crypto.CellOpener(Tbt.Contracts.Direction.Up, symbol, Tbt.Crypto.KeyTable(), Tbt.Crypto.NoHeaderMask())
    let rx = RxHalf(symbol, CodeField.Gf16, dec, opener, 64, 4)
    let zero = Array.zeroCreate<byte> symbol
    // Sink-only flow table: these cells carry zero envelopes, so no flow delivers anything and
    // the ledger under test is the only thing that moves. Attached because `drainKnown`
    // dereferences it for every known source.
    let port =
        { new IFlowPort with
            member _.Open(_, _) = ()
            member _.Write(_, data) = data.Length
            member _.Finish _ = ()
            member _.Reset _ = ()
            member _.Close _ = ()
            member _.Credit _ = () }
    let store =
        { new ISourceStore with
            member _.Source(_, _) = ReadOnlySpan<byte> zero }
    rx.Attach(FlowTable(port, store, Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults))
    let mk kind slot spanOrK t : Tbt.Wire.Meta =
        { Type = kind
          Flags = Tbt.Wire.MetaFlags.None
          Slot = byte slot
          StepUsed = byte spanOrK
          Aux = 0us
          RxWinGrant = 0us
          StepId = t
          AckStepLo = 0UL
          CarrierWm = 0UL
          KHist = 0u
          ErasureQ16 = 0us }
    let feed kind slot spanOrK t now =
        rx.OnCoded(mk kind slot spanOrK t, ReadOnlySpan<byte> zero, 0us, now)
    for slot in 0 .. 2 do
        feed Tbt.Contracts.CellType.Data slot 4 0UL (int64 slot * 1_000_000L)
    // span = 1: a dense row over step 0 alone.
    feed Tbt.Contracts.CellType.Booster 0 1 0UL 3_000_000L
    if never then
        // Nothing more arrives; only the timer can settle it.
        rx.Service 500_000_000L
        // Advance the window clock with one ordinary cell of the next step: `windowTotals`
        // treats the last arrival as "now", so a settled sample would otherwise sit outside
        // the read ring.
        feed Tbt.Contracts.CellType.Data 0 4 1UL 500_000_000L
    elif arriveInside then
        feed Tbt.Contracts.CellType.Data 3 4 0UL 50_000_000L
        if duplicate then feed Tbt.Contracts.CellType.Data 3 4 0UL 50_000_000L
    rx

[<Fact>]
let ``a rebuild is withdrawn by the first in-deadline arrival of its own cell, once`` () =
    // The ledger's job: a source FEC rebuilt is an erasure only if its own cell is still absent
    // at the step's deadline. A duplicate (repair copy, TCP retransmission) must not withdraw a
    // second debt, so the cancelled count has to stay at 1 however many copies arrive.
    let rx = runRebuild true false false
    // Discriminators first: a vacuous scenario would pass the counts below for the wrong reason.
    Assert.Equal(1L, rx.Counters.Recovered)
    Assert.Equal(4L, rx.Counters.TimelyDue)
    Assert.Equal(1L, rx.Counters.ErasureRecovered)
    Assert.Equal(1L, rx.Counters.ErasureCancelled)
    let dup = runRebuild true true false
    Assert.Equal(1L, dup.Counters.ErasureCancelled)

[<Fact>]
let ``a rebuild whose cell never arrives inside the deadline is booked as erased`` () =
    // No arrival at all: the timer sweep must settle the pending rebuild as an erasure once the
    // deadline has passed, without waiting for another frame of that step.
    let rx = runRebuild false false true
    Assert.Equal(1L, rx.Counters.ErasureRecovered)
    Assert.Equal(0L, rx.Counters.ErasureCancelled)
    Assert.True(rx.ErasureQ16 > 0us, sprintf "settled erasure never reached the window (q16 %d)" rx.ErasureQ16)

[<Fact>]
let ``a cell that arrives after the deadline stays an erasure`` () =
    // The spec's rule is "late counts as erased", so the ledger must NOT un-erase a rebuild just
    // because the cell eventually turned up. Only an arrival inside the deadline may withdraw
    // it; here the cell lands at 200 ms against a deadline of about 91 ms.
    let rx =
        let symbol = 64
        let dec = Tbt.Codec.Decoder(11UL, symbol, 64, 256)
        let opener =
            Tbt.Crypto.CellOpener(Tbt.Contracts.Direction.Up, symbol, Tbt.Crypto.KeyTable(), Tbt.Crypto.NoHeaderMask())
        let rx = RxHalf(symbol, CodeField.Gf16, dec, opener, 64, 4)
        let zero = Array.zeroCreate<byte> symbol
        let port =
            { new IFlowPort with
                member _.Open(_, _) = ()
                member _.Write(_, data) = data.Length
                member _.Finish _ = ()
                member _.Reset _ = ()
                member _.Close _ = ()
                member _.Credit _ = () }
        let store =
            { new ISourceStore with
                member _.Source(_, _) = ReadOnlySpan<byte> zero }
        rx.Attach(FlowTable(port, store, Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults))
        let mk kind slot spanOrK t : Tbt.Wire.Meta =
            { Type = kind
              Flags = Tbt.Wire.MetaFlags.None
              Slot = byte slot
              StepUsed = byte spanOrK
              Aux = 0us
              RxWinGrant = 0us
              StepId = t
              AckStepLo = 0UL
              CarrierWm = 0UL
              KHist = 0u
              ErasureQ16 = 0us }
        let feed kind slot spanOrK t now =
            rx.OnCoded(mk kind slot spanOrK t, ReadOnlySpan<byte> zero, 0us, now)
        for slot in 0 .. 2 do
            feed Tbt.Contracts.CellType.Data slot 4 0UL (int64 slot * 1_000_000L)
        feed Tbt.Contracts.CellType.Booster 0 1 0UL 3_000_000L
        feed Tbt.Contracts.CellType.Data 3 4 0UL 200_000_000L
        rx
    Assert.Equal(1L, rx.Counters.ErasureRecovered)
    Assert.Equal(0L, rx.Counters.ErasureCancelled)
    Assert.True(rx.ErasureQ16 > 0us, sprintf "late cell did not stay an erasure (q16 %d)" rx.ErasureQ16)

/// Drives one RxHalf deadline ledger with a virtual clock: no network, no crypto, no wall clock.
let private deadlineRx () =
    let symbol = 64
    let dec = Tbt.Codec.Decoder(11UL, symbol, 64, 256)
    let opener =
        Tbt.Crypto.CellOpener(Tbt.Contracts.Direction.Up, symbol, Tbt.Crypto.KeyTable(), Tbt.Crypto.NoHeaderMask())
    let rx = RxHalf(symbol, CodeField.Gf16, dec, opener, 64, 4)
    let zero = Array.zeroCreate<byte> symbol
    // Sink-only flow table: zero envelopes deliver nothing, and `drainKnown` needs a table.
    let port =
        { new IFlowPort with
            member _.Open(_, _) = ()
            member _.Write(_, data) = data.Length
            member _.Finish _ = ()
            member _.Reset _ = ()
            member _.Close _ = ()
            member _.Credit _ = () }
    let store =
        { new ISourceStore with
            member _.Source(_, _) = ReadOnlySpan<byte> zero }
    rx.Attach(FlowTable(port, store, Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults))
    let feed kind slot spanOrK t (now: int64) =
        let m : Tbt.Wire.Meta =
            { Type = kind
              Flags = Tbt.Wire.MetaFlags.None
              Slot = byte slot
              StepUsed = byte spanOrK
              Aux = 0us
              RxWinGrant = 0us
              StepId = t
              AckStepLo = 0UL
              CarrierWm = 0UL
              KHist = 0u
              ErasureQ16 = 0us }
        rx.OnCoded(m, ReadOnlySpan<byte> zero, 0us, now)
    rx, feed

[<Fact>]
let ``a slow step still books a rebuilt source, while its clock evidence is withheld`` () =
    // Two steps, identical direct arrivals, differing only in when the fourth source shows up.
    //  (a) 300 ms, inside a second: past the 90 ms deadline, so the CLOCK counts it.
    //  (b) 1.5 s: the step is serialised/starved rather than delayed by the network, so the
    //      clock must say nothing — a span over a second is what `timelySlow` exists for.
    // The point of the pair is the asymmetry: withholding the clock judgement must NOT withhold
    // a source FEC had to rebuild, because a rebuild is evidence no timing heuristic can fake.
    // On the single-path live run (1 path, ~24 % loss) the ledger reported 445 rebuilt and 156
    // clock-late out of 2738 accounted sources, i.e. ~22 % — the rebuild bucket stayed live.
    let clockRx, clockFeed = deadlineRx ()
    for slot in 0 .. 2 do clockFeed Tbt.Contracts.CellType.Data slot 4 0UL (int64 slot * 1_000_000L)
    clockFeed Tbt.Contracts.CellType.Data 3 4 0UL 300_000_000L
    Assert.Equal(1L, clockRx.Counters.ErasureLateClock)
    Assert.Equal(0L, clockRx.Counters.ErasureRecovered)

    let slowRx, slowFeed = deadlineRx ()
    for slot in 0 .. 2 do slowFeed Tbt.Contracts.CellType.Data slot 4 1UL (int64 slot * 1_000_000L)
    // Span = 1: a dense row over step 1 alone, so it leaves only slot 3 unknown and elimination
    // solves it. (Passing the source count here instead builds a multi-step row that never
    // reduces, and then nothing is rebuilt.)
    slowFeed Tbt.Contracts.CellType.Booster 0 1 1UL 1_500_000_000L
    Assert.Equal(0L, slowRx.Counters.ErasureLateClock)
    Assert.Equal(1L, slowRx.Counters.ErasureRecovered)
    Assert.Equal(4L, slowRx.Counters.TimelyDue)

[<Fact>]
let ``the reported erasure decays when the link goes quiet`` () =
    // The window statistics are horizon-limited (64 x 10 ms): a sample counts only while it is
    // recent. With the last arrival as the only clock, a link that went quiet froze that
    // horizon, so the last burst stayed "current" for ever and every WIN_ACK kept reporting it —
    // the peer held rho at the level that burst justified and spent parity on loss that had
    // already stopped. `Service` is the timer, so it must move the clock too.
    let rx, feed = deadlineRx ()
    // Four steps, each with three direct sources and a BOOSTER that solves the fourth: four
    // rebuilds, sixteen accounted sources, all settling into one 10 ms window on their deadline.
    for t in 0UL .. 3UL do
        for slot in 0 .. 2 do
            feed Tbt.Contracts.CellType.Data slot 4 t (int64 t * 1_000_000L + int64 slot * 1_000_000L)
        feed Tbt.Contracts.CellType.Booster 0 1 t (int64 t * 1_000_000L + 5_000_000L)
    // Past every deadline (about 90 ms), so the rebuilds become erasures.
    rx.Service 500_000_000L
    // One ordinary cell just to place the sample inside the read horizon; the rate itself was
    // produced by the settlements above.
    feed Tbt.Contracts.CellType.Data 0 4 50UL 500_000_000L
    let during = int rx.ErasureQ16
    Assert.True(during >= 8_000, sprintf "settled erasures are not visible (q16 %d)" during)
    // No further cells: only the timer runs. The burst is now far outside the horizon, so the
    // reported rate must fall back instead of repeating itself for ever.
    for step in 1 .. 8 do
        rx.Service (500_000_000L + int64 step * 250_000_000L)
    let after = int rx.ErasureQ16
    Assert.True(after < during / 2, sprintf "erasure stayed at %d after %d" after during)
    // A fresh burst must be reported again: the horizon moved forward, it was not broken.
    //
    // The timestamps are monotonic with the quiet period above. Feeding step 60 at 60 ms while
    // the clock already stood at 2.6 s would be a go-backwards input, and `windowAdd` drops a
    // settlement whose window has been superseded on purpose, so the sample would vanish for a
    // reason the test invented rather than a reason the product has.
    let burst2 = 2_600_000_000L
    for i in 0UL .. 3UL do
        let t = 60UL + i
        let at = burst2 + int64 i * 1_000_000L
        for slot in 0 .. 2 do
            feed Tbt.Contracts.CellType.Data slot 4 t (at + int64 slot * 1_000_000L)
        feed Tbt.Contracts.CellType.Booster 0 1 t (at + 5_000_000L)
    rx.Service (burst2 + 200_000_000L)
    feed Tbt.Contracts.CellType.Data 0 4 64UL (burst2 + 200_000_000L)
    Assert.True(int rx.ErasureQ16 >= 8_000, sprintf "a new burst was not reported (q16 %d)" (int rx.ErasureQ16))

[<Fact>]
let ``settled counters report what the windows received, not what was rebuilt`` () =
    // `ErasureRecovered` counts REPORTS: a rebuild is counted the moment FEC solves a slot,
    // before the step deadline decides whether any cell was really absent. On a 0 %-loss shaper
    // that read 18.4 % while nothing was lost. The settled pair is the OUTCOME that reached the
    // 10 ms windows, i.e. what the peer's controller can act on; keeping the two apart is the
    // only way to tell a measurement artefact from loss.
    let rx, feed = deadlineRx ()
    for slot in 0 .. 2 do
        feed Tbt.Contracts.CellType.Data slot 4 0UL (int64 slot * 1_000_000L)
    // span = 1: a dense row over step 0 alone, so it leaves only slot 3 unknown.
    feed Tbt.Contracts.CellType.Booster 0 1 0UL 3_000_000L
    Assert.Equal(1L, rx.Counters.ErasureRecovered)
    // Reported, but not settled: the step deadline has not passed, so nothing reached a window.
    Assert.Equal(0L, rx.Counters.RebuildSettledDue)
    rx.Service 500_000_000L
    // The cell never arrived, so the rebuild became one erased source out of one accounted
    // source. The three direct arrivals were booked when they landed and are not in this pair.
    Assert.Equal(1L, rx.Counters.RebuildSettledMissing)
    Assert.Equal(1L, rx.Counters.RebuildSettledDue)

    // A rebuild the slot's own cell vindicates inside the deadline is not an erasure, but it
    // WAS an accounted source, so it must still appear in `due` — otherwise the mean would be
    // erased/(due - vindicated) and one reordered path could push it above the truth.
    let rx2, feed2 = deadlineRx ()
    for slot in 0 .. 2 do
        feed2 Tbt.Contracts.CellType.Data slot 4 0UL (int64 slot * 1_000_000L)
    feed2 Tbt.Contracts.CellType.Booster 0 1 0UL 3_000_000L
    feed2 Tbt.Contracts.CellType.Data 3 4 0UL 50_000_000L
    Assert.Equal(1L, rx2.Counters.ErasureCancelled)
    // A duplicate copy of the same cell (repair retransmit) must not withdraw a second debt.
    feed2 Tbt.Contracts.CellType.Data 3 4 0UL 60_000_000L
    Assert.Equal(1L, rx2.Counters.ErasureCancelled)
    rx2.Service 500_000_000L
    Assert.Equal(0L, rx2.Counters.RebuildSettledMissing)
    Assert.Equal(1L, rx2.Counters.RebuildSettledDue)
    // Counters only grow, so a reader can difference two stats lines.
    Assert.True(rx2.Counters.ErasureCancelled <= rx2.Counters.ErasureRecovered)

[<Fact>]
let ``a one-cell burst window votes in the percentile, not only in the mean`` () =
    // Spec 6.10 defines `erasure_p95_q16` as the 95th percentile of the 10 ms WINDOW SERIES,
    // with no minimum sample count per window. A one-cell window that was entirely erased is a
    // real 10 ms observation, and it is the burst evidence the mean cannot carry.
    //
    // The rejected `winDue >= 16` floor dropped such windows. To tell that apart from the
    // correct behaviour the fixture needs a SECOND, wide clean window: with it, the mean is
    // 1/17 (small) while the percentile must still be 1.0 (the burst). Under the floor the lone
    // 1-cell window was excluded, `n` fell to 1 and the p95 collapsed to 0 — the burst vanished.
    let rx, feed = deadlineRx ()
    // Window ordinal 9: one source solved by a BOOSTER that never arrives ⇒ due 1, erased 1.
    for slot in 0 .. 2 do
        feed Tbt.Contracts.CellType.Data slot 4 0UL (int64 slot * 1_000_000L)
    feed Tbt.Contracts.CellType.Booster 0 1 0UL 3_000_000L
    rx.Service 500_000_000L
    // Window ordinal 60: four clean steps, four on-time sources each ⇒ due 16, erased 0.
    // The steps stay inside the decoder window (capacitySteps = 64 with baseStep = 0). Steps past
    // 63 would be rejected as out of window, so this "wide clean window" would hold no due cells
    // at all and the fixture would assert against an empty population.
    for t in 30UL .. 33UL do
        for slot in 0 .. 3 do
            feed Tbt.Contracts.CellType.Data slot 4 t (600_000_000L + int64 (t - 30UL) * 100_000L + int64 slot * 1_000L)
    // o = 65: window 9 is 56 windows old (inside the 64-window horizon), window 60 closed (5 old).
    rx.Service 650_000_000L
    let mean = int rx.ErasureQ16
    let p95 = int rx.ErasureP95Q16
    Assert.True(mean > 0 && mean < 20_000, sprintf "mean %d is not the small 1/17 blend" mean)
    Assert.True(p95 >= 65_000, sprintf "p95 %d lost the one-cell burst (floor behaviour)" p95)

[<Fact>]
let ``the p95 diagnostics name the window at the p95 rank, not the maximum`` () =
    // `p95` is a nearest-rank order statistic, so with 20 samples the rank is 18 and the
    // extreme window normally sits above it. Reporting the maximum-rate window would therefore
    // describe a sample the percentile did not use. Here 19 windows are clean and one window
    // holds a single late cell out of four: the maximum is 0.25, the rank-18 sample is 0.0, and
    // the diagnostics must agree with the reported value rather than with the extreme.
    let rx, feed = deadlineRx ()
    for w in 0 .. 18 do
        let at = int64 w * 20_000_000L
        for slot in 0 .. 3 do
            feed Tbt.Contracts.CellType.Data slot 4 (uint64 w) (at + int64 slot * 1_000_000L)
    let lastAt = 19L * 20_000_000L
    for slot in 0 .. 2 do
        feed Tbt.Contracts.CellType.Data slot 4 19UL (lastAt + int64 slot * 1_000_000L)
    // One cell of the last step arrives 200 ms late, i.e. past the ~90 ms step deadline.
    feed Tbt.Contracts.CellType.Data 3 4 19UL (lastAt + 200_000_000L)
    rx.Service 600_000_000L
    let p95 = int rx.ErasureP95Q16
    Assert.True(rx.P95Candidates >= 19, sprintf "only %d closed windows were candidates" rx.P95Candidates)
    // The named window must reproduce the reported percentile. If the diagnostics tracked the
    // extreme window (1 erased of 1 due = 1.00) while the rank-18 sample is a clean window, this
    // relation breaks and the assertion below fails.
    let named = if rx.P95PickDue = 0 then 0.0 else float rx.P95PickErased / float rx.P95PickDue
    Assert.True(abs (named * 65536.0 - float p95) < 2.0,
                sprintf "p95 %d disagrees with the named window %d/%d" p95 rx.P95PickErased rx.P95PickDue)
    // And the extreme really is not what was reported: the late cell is a different window.
    Assert.Equal(0, p95)
    Assert.Equal(0, rx.P95PickErased)
    Assert.True(rx.P95PickDue > 0, "the named window has no due cells, so it cannot be a real sample")
    // Repeated reads must be stable: the counters are per-evaluation, not accumulating.
    let due2 = rx.P95PickDue
    let cand2 = rx.P95Candidates
    Assert.Equal(p95, int rx.ErasureP95Q16)
    Assert.Equal(due2, rx.P95PickDue)
    Assert.Equal(cand2, rx.P95Candidates)

[<Fact>]
let ``a sparse burst keeps its percentile after the windows close`` () =
    // Many almost-clean windows plus two one-cell erased windows: the mean is small (~2/92)
    // but the upper percentile must still show the burst. Under the rejected floor every window
    // here had fewer than 16 due cells, so `n` fell to 0 and the p95 collapsed onto the mean —
    // sparse bursts were invisible by construction.
    let rx, feed = deadlineRx ()
    // 40 steps 20 ms apart, four sources each (k = 4 so the clock judge applies). In steps 10
    // and 30 the fourth source arrives 200 ms late — past its step deadline — so one cell of
    // those two windows is erased while everything else is on time.
    // Fed in TIMESTAMP order, not step order: step 10's late cell lands at 400 ms, after steps
    // 11..19 have already been delivered, exactly as on a wire. Feeding step 11 at 220 ms after
    // that would move `lastCellNs` and the arrival bookkeeping backwards, which no carrier does,
    // so the fixture would be exercising an impossible sequence.
    let events =
        [ for w in 0 .. 39 do
            let baseAt = int64 w * 20_000_000L
            for slot in 0 .. 3 do
                (if slot = 3 && (w = 10 || w = 30) then baseAt + 200_000_000L
                 else baseAt + int64 slot * 1_000_000L), slot, uint64 w ]
        |> List.sortBy (fun (at, slot, w) -> at, w, slot)
    for (at, slot, w) in events do
        feed Tbt.Contracts.CellType.Data slot 4 w at
    // Let every window close — but stay inside the horizon. The statistics only count the
    // last `winSlots` (64) 10 ms windows, i.e. 640 ms; the last burst lands at 800 ms
    // (step 30 starts at 600 ms, its late source 200 ms later), so the settle instant must be
    // just past it. `Service 3 s` would push EVERY window out of range and both statistics
    // would read 0, making the assertion below fail for a reason the fixture invented.
    rx.Service 900_000_000L
    let p95 = int rx.ErasureP95Q16
    let mean = int rx.ErasureQ16
    Assert.True(p95 > mean, sprintf "p95 %d did not exceed the mean %d, so the burst was smoothed away" p95 mean)

[<Fact>]
let ``a clean carrier keeps the timely erasure rate at zero`` () =
    // The controller must not spend wire on parity it does not need: on a 0 %-loss link
    // both the mean and the p95 stay at the floor, so rho stays at rhoFloor.
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, 0.0, 992)
    pair.Delay <- 5_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init (8_000 * 964) (fun i -> byte (i * 13 + 7))
    let mutable sent = 0
    for _ in 1 .. 900 do
        if sent < payload.Length then
            sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, min 8_000 (payload.Length - sent)), false)
        pair.Round() |> ignore
    let rx = pair.Server.Rx
    let q = int rx.ErasureQ16
    let p = int rx.ErasureP95Q16
    let msg = sprintf "erasure q16 %d p95 %d folds %d" q p rx.EstimateFolds
    Assert.True(q <= 1_400, msg)
    Assert.True(p <= 1_400, msg)

[<Fact>]
let ``every decoded bit names a step the decoder really completed`` () =
    // Bit j is step t_lo + 1 + j (spec 6.10). The sender stops repairing steps it sees
    // here, so a bit set for a step the decoder has not completed would lose data.
    // Use 18 % loss so some steps temporarily lag behind later completed steps even with FEC.
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 4, 0.18, 993)
    pair.Delay <- 20_000_000L
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init (8_000 * 964) (fun i -> byte (i * 13 + 7))
    let mutable sent = 0
    let mutable setBits = 0
    for _ in 1 .. 700 do
        if sent < payload.Length then
            sent <- sent + pair.Feed(c, slot, ReadOnlySpan(payload, sent, min 8_000 (payload.Length - sent)), false)
        pair.Round() |> ignore
        let rx = pair.Server.Rx
        let struct (b0, b1, b2, b3) = rx.FillDecodedBitmap()
        let lo = rx.AckStepLo
        for j in 0 .. 255 do
            let bits = if j < 64 then b0 elif j < 128 then b1 elif j < 192 then b2 else b3
            if (bits >>> (j % 64)) &&& 1UL <> 0UL then
                setBits <- setBits + 1
                Assert.True(rx.IsStepComplete(lo + 1UL + uint64 j), sprintf "bit %d set for a step not complete" j)
    // A bitmap that is always zero would satisfy every per-bit check vacuously.
    Assert.True(setBits > 0, "no decoded bit was ever reported")
    Assert.True(pair.Server.Rx.AckStepLo > 0UL)
