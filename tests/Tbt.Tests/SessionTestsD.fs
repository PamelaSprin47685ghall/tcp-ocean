module Tbt.Tests.SessionTestsD

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
open Tbt.Tests.SessionTestsC

[<Fact>]
let ``a growing NACK deficit does not skip the last-repair wait for that step`` () =
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
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    let tLo = 1UL
    let wait = c.Tx.RepairWaitNs
    clock.Now <- sealedAt + int64 wait
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    let afterFirst = c.Tx.BoostersSent
    Assert.True(afterFirst >= 1L)
    clock.Now <- clock.Now + 10_000_000L
    c.Tx.OnNack(tLo, 1, 30)
    Assert.False(c.Tx.RepairWaiting, "deficit growth must not skip this step's wait")
    Assert.Equal(afterFirst, c.Tx.BoostersSent)
    clock.Now <- clock.Now + int64 wait
    c.Tx.OnNack(tLo, 1, 30)
    Assert.True(c.Tx.RepairWaiting, "a later round is due once this step's wait has passed")

[<Fact>]
let ``seeing a later step does not make a not-yet-due hole eligible`` () =
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
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let earlySealed = clock.Now
    clock.Now <- earlySealed + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    let wait = c.Tx.RepairWaitNs
    // A later step, sealed just now, is seen at once: evidence exists, but reo_wnd
    // after that later seal has not passed, and the later step itself is not due.
    clock.Now <- earlySealed + int64 wait - 1_000_000L
    let lateLo = c.Tx.Head
    guard <- 64
    while c.Tx.Head < lateLo + 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    Assert.True(c.Tx.Head > lateLo, sprintf "head %d lateLo %d" c.Tx.Head lateLo)
    let lateSealed = clock.Now
    c.Tx.OnPeerSeen lateLo
    c.Tx.OnNack(lateLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "t_hi of a just-sealed step must not create eligibility")
    clock.Now <- lateSealed + 1_000_000L
    c.Tx.OnNack(lateLo, 1, 4)
    Assert.False(c.Tx.RepairWaiting, "reorder window after the later seal has not passed")

[<Fact>]
let ``a never-repaired tail step is eligible on its own timer after another step was repaired`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
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
    Assert.True(c.Tx.Head >= 5UL, sprintf "head %d" c.Tx.Head)
    let wait = c.Tx.RepairWaitNs
    clock.Now <- sealedAt + int64 wait
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    let afterFirst = c.Tx.BoostersSent
    clock.Now <- clock.Now + 10_000_000L
    c.Tx.OnNack(4UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting, "a due never-repaired tail step has its own deadline")
    Assert.True(c.Tx.BoostersSent = afterFirst, "boosters are queued, not yet sent")
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
    Assert.True(c.Tx.BoostersSent > afterFirst)

[<Fact>]
let ``Karn still samples a never-repaired step after a neighbour was repaired`` () =
    let config = { SessionConfig.defaults with NewRepair = true; Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
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
    Assert.Equal(0L, c.Tx.RttSkipped)
    let wait = c.Tx.RepairWaitNs
    clock.Now <- sealedAt + int64 wait
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.RepairWaiting && n < 64 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
        n <- n + 1
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(2UL, 100us)
    Assert.True(c.Tx.RttSkipped >= 1L, sprintf "skipped %d" c.Tx.RttSkipped)
    let skipped = c.Tx.RttSkipped
    let srtt = c.Tx.SrttNs
    c.Tx.OnPeerAck(4UL, 100us)
    Assert.Equal(skipped, c.Tx.RttSkipped)
    Assert.True(c.Tx.SrttNs <> srtt, sprintf "srtt stayed %g" srtt)

[<Fact>]
let ``a spurious repair widens the reorder window`` () =
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
    let reo0 = c.Tx.ReoWndNs
    Assert.Equal(c.Tx.MinRttNs / 4L, reo0)
    c.Tx.OnPeerSeen 4UL
    clock.Now <- sealedAt + c.Tx.MinRttNs + reo0
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, allowRepair=true))
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.True(c.Tx.SpuriousRepairs >= 1L, sprintf "spurious %d" c.Tx.SpuriousRepairs)
    Assert.True(c.Tx.ReoWndNs > reo0, sprintf "reo grew from %d to %d" reo0 c.Tx.ReoWndNs)
    Assert.True(c.Tx.ReoWndNs <= c.Tx.MinRttNs)

[<Fact>]
let ``an ack that arrives sooner than minRTT after a BOOSTER counts as a spurious repair`` () =
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
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    let tLo = 1UL
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(tLo, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    let frame = Array.zeroCreate<byte> c.FrameLength
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, true))
    Assert.True(c.Tx.BoostersSent >= 1L)
    // Custody of the original arrives immediately: the BOOSTER has not had a minRTT on the path.
    c.Tx.OnPeerAck(c.Tx.Head, 100us)
    Assert.True(c.Tx.SpuriousRepairs >= 1L, sprintf "spurious %d boosters %d" c.Tx.SpuriousRepairs c.Tx.BoostersSent)

[<Fact>]
let ``the grant limit is each report's own ack plus its grant: late, duplicate and reordered reports never amplify it`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 5)
    let c = pair.Client
    let mutable b = -1
    Assert.True(c.TryOpenFlow(&b))
    let chunk = Array.create 65536 7uy
    feedFor c b (ReadOnlySpan chunk) false |> ignore
    let tx = c.Tx
    let head = tx.Head
    Assert.True(head >= 4UL, sprintf "head %d" head)
    let g0 = tx.GrantLimit
    // A late report of an older ack: its window counts from its own ack.
    tx.OnPeerAck(3UL, uint16 (g0 - 3UL + 100UL))
    Assert.Equal(g0 + 100UL, tx.GrantLimit)
    tx.OnPeerAck(1UL, uint16 (g0 - 1UL + 150UL))
    Assert.Equal(g0 + 150UL, tx.GrantLimit)
    Assert.Equal(3UL, tx.PeerAck)
    // A duplicate changes nothing; a smaller grant never takes the limit back.
    tx.OnPeerAck(3UL, uint16 (g0 - 3UL + 100UL))
    tx.OnPeerAck(head, 1us)
    Assert.Equal(g0 + 150UL, tx.GrantLimit)
    // An ack beyond our head is invalid and changes nothing.
    tx.OnPeerAck(head + 1UL, 65535us)
    Assert.Equal(g0 + 150UL, tx.GrantLimit)
    // Random interleaving of a report history: the limit is the max of ack + grant.
    let rnd = Random 3
    let reports = Array.init 200 (fun i -> struct (uint64 (rnd.Next(0, int head + 1)), uint16 (rnd.Next(0, 65536))))
    let mutable model = tx.GrantLimit
    for _ in 1 .. 2000 do
        let struct (a, g) = reports.[rnd.Next reports.Length]
        tx.OnPeerAck(a, g)
        model <- max model (a + uint64 g)
        Assert.Equal(model, tx.GrantLimit)

[<Fact>]
let ``256 flows opened one after another with readers that take nothing stay within the 32 MiB pool`` () =
    let config = { SessionConfig.defaults with WindowSteps = 1024 }
    let pair = Pair(config, 4, 0.0, 9)
    pair.ServerPort.WriteLimit <- 0
    let c = pair.Client
    let s = pair.Server
    let pool = int64 config.Credit.PoolBytes
    let c0 = int64 config.Credit.Initial
    let limits = Array.zeroCreate<uint64> 256
    let check () =
        Assert.True(s.Flows.CommittedBytes <= pool, sprintf "committed %d > pool %d" s.Flows.CommittedBytes pool)
        for i in 0 .. 255 do
            let l = s.Flows.[i].RxLimit
            if s.Flows.[i].Active then
                Assert.True(l >= limits.[i], sprintf "slot %d limit %d fell below %d" i l limits.[i])
                limits.[i] <- l
    // The first flow fills its whole promise: the bytes sit in its reserve (held, not consumed).
    let payload = Array.create (9 <<< 20) 5uy
    let mutable first = -1
    Assert.True(c.TryOpenFlow(&first))
    let mutable sent = 0
    for _ in 1 .. 1500 do
        sent <- sent + pair.Feed(c, first, ReadOnlySpan(payload, sent, payload.Length - sent), false)
        pair.Round() |> ignore
        check ()
    Assert.Equal(config.Credit.FlowWindowMax, sent)
    let mutable app = Span<byte>.Empty
    let mutable opened = 1
    for _ in 1 .. 255 do
        let mutable b = -1
        Assert.True(c.TryOpenFlow(&b))
        Assert.True(c.TryBeginSourceFor(b, &app))
        c.CommitSource(b, 0, false)
        c.Flush()
        opened <- opened + 1
        for _ in 1 .. 20 do
            pair.Round() |> ignore
            check ()
    for _ in 1 .. 200 do
        pair.Round() |> ignore
        check ()
    Assert.Equal(256, s.Flows.RxActive)
    // Every flow keeps at least C0; the sum of promises would be ~161 MiB with a per-flow split.
    let total = [ for i in 0 .. 255 -> int64 s.Flows.[i].RxLimit ] |> List.sum
    Assert.True(total <= pool, sprintf "sum of limits %d" total)
    for i in 0 .. 255 do Assert.True(int64 s.Flows.[i].RxLimit >= c0)
    // The reader of the first flow takes its bytes: the pool frees and the others can grow.
    let usedBefore = s.Flows.PoolUsed
    pair.ServerPort.WriteLimit <- Int32.MaxValue
    s.PumpFlow first
    for _ in 1 .. 50 do
        pair.Round() |> ignore
        check ()
    Assert.True(s.Flows.PoolUsed <= usedBefore, sprintf "%d -> %d" usedBefore s.Flows.PoolUsed)


[<Fact>]
let ``unpaced repair slots do not preempt unsent originals, except a 1-in-4 share`` () =
    // Empty original queue: a waiting repair may go at once.
    Assert.True(RepairSendOrder.canSendRepair 0 true 0)
    Assert.True(RepairSendOrder.canSendRepair 0 true RepairSendOrder.OriginalsBeforeRepair)
    // Originals waiting: no repair until the share is due.
    Assert.False(RepairSendOrder.canSendRepair 10 true 0)
    Assert.False(RepairSendOrder.canSendRepair 10 true (RepairSendOrder.OriginalsBeforeRepair - 1))
    Assert.True(RepairSendOrder.canSendRepair 10 true RepairSendOrder.OriginalsBeforeRepair)
    // No repair waiting: never a repair slot, even if the streak is due.
    Assert.False(RepairSendOrder.canSendRepair 0 false 0)
    Assert.False(RepairSendOrder.canSendRepair 10 false RepairSendOrder.OriginalsBeforeRepair)


[<Fact>]
let ``allowRepair false sends unsent originals while a BOOSTER waits`` () =
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
    clock.Now <- sealedAt + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    clock.Now <- sealedAt + int64 c.Tx.RepairWaitNs
    c.Tx.OnNack(1UL, 1, 4)
    Assert.True(c.Tx.RepairWaiting)
    Assert.True(c.Tx.Queued > 0, "originals must still be waiting")
    let queued0 = c.Tx.Queued
    let boost0 = c.Tx.BoostersSent
    let frame = Array.zeroCreate<byte> c.FrameLength
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, allowRepair=false))
    Assert.Equal(boost0, c.Tx.BoostersSent)
    Assert.True(c.Tx.Queued < queued0, sprintf "queued %d -> %d" queued0 c.Tx.Queued)
    Assert.True(c.Tx.RepairWaiting, "the BOOSTER must stay queued")
    // A repair slot (allowRepair=true) may still take the BOOSTER first; that is
    // the primitive writeBatch uses for an admitted unpaced or paced repair slot.
    Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, allowRepair=true))
    Assert.True(c.Tx.BoostersSent > boost0)


[<Fact>]
let ``UniqueArrived counts cells of a seen-but-unacked later step`` () =
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let mutable guard = 64
    while c.Tx.Head < 5UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    Assert.True(c.Tx.Head >= 5UL, sprintf "head %d" c.Tx.Head)
    // Drain some originals so uniqueCells of the later steps are those already sealed.
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable n = 0
    while c.Tx.Queued > 0 && n < 4096 do
        Assert.True(c.Tx.TryRenderNext(Span frame, 0us, 1UL, 100us, 0us, allowRepair=false))
        n <- n + 1
    Assert.Equal(0L, c.Tx.UniqueAcked)
    Assert.Equal(0L, c.Tx.UniqueArrived)
    c.Tx.OnPeerAck(1UL, 100us)
    let afterAck = c.Tx.UniqueAcked
    Assert.True(afterAck > 0L, "custody of step 0 should count")
    Assert.Equal(afterAck, c.Tx.UniqueArrived)
    let seen = c.Tx.Head - 1UL
    Assert.True(seen > 1UL, sprintf "seen %d" seen)
    c.Tx.OnPeerSeen seen
    Assert.True(c.Tx.UniqueArrived > afterAck,
        sprintf "t_hi %d must add arrival cells ahead of peer_ack; arrived %d acked %d" seen c.Tx.UniqueArrived afterAck)
    Assert.Equal(afterAck, c.Tx.UniqueAcked)

[<Fact>]
let ``offered caps follow D4 and D5: FLOWSTATE only with FLOWSTATE2, no LANE`` () =
    let c = SupportedCaps
    Assert.Equal(0us, c &&& Caps.Lane)
    Assert.Equal(0us, c &&& Caps.X25519)
    Assert.Equal(0us, c &&& Caps.UserRx)
    Assert.Equal(0us, c &&& Caps.Dl16)
    Assert.Equal(Caps.FlowState ||| Caps.FlowState2, c &&& (Caps.FlowState ||| Caps.FlowState2))
    Assert.True(c &&& Caps.HpMask <> 0us)
    Assert.True(c &&& Caps.StraddleOk <> 0us)
    Assert.True(c &&& Caps.JoinAck2 <> 0us)

[<Fact>]
let ``a step queued before send is not counted as path RTT`` () =
    // Production 256-path downlinks queue cells in the sender for seconds. The RTT
    // sample must run from the first time the step went to a path socket, not from
    // its seal: a seal-to-ack sample grows with the queue, and the windowed minimum
    // then ratchets and drags the group's sample interval, cwnd and pacing down.
    let config = { SessionConfig.defaults with Credit = { CreditPolicy.defaults with Enabled = false } }
    let pair = Pair(config, 1, 0.0, 4)
    let c = pair.Client
    let clock = pair.Clock
    c.Tx.InitialGrant 10_000UL
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let chunk = Array.create 65536 7uy
    let frame = Array.zeroCreate<byte> c.FrameLength
    // Establish a 400 ms path minimum with an immediate send.
    let mutable guard = 64
    while c.Tx.Head < 2UL && guard > 0 do
        pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
        c.Tx.Flush()
        guard <- guard - 1
    let mutable n = 0
    while n < 64 && c.Tx.TryRenderNext(Span frame, 0us, 10UL, 100us, 0us, true) do
        n <- n + 1
    clock.Now <- clock.Now + 400_000_000L
    c.Tx.OnPeerAck(1UL, 100us)
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    // Six rounds: a cell waits 3 s in the sender's queue, then crosses a 400 ms path.
    for _ in 1 .. 6 do
        let before = c.Tx.Head
        guard <- 64
        while c.Tx.Head < before + 1UL && guard > 0 do
            pair.Feed(c, slot, ReadOnlySpan chunk, false) |> ignore
            c.Tx.Flush()
            guard <- guard - 1
        clock.Now <- clock.Now + 3_000_000_000L
        n <- 0
        while n < 64 && c.Tx.TryRenderNext(Span frame, 0us, 10UL, 100us, 0us, true) do
            n <- n + 1
        clock.Now <- clock.Now + 400_000_000L
        c.Tx.OnPeerAck(before + 1UL, 100us)
    Assert.Equal(400_000_000L, c.Tx.MinRttNs)
    Assert.True(c.Tx.SrttNs < 600_000_000.0, sprintf "srtt %g" c.Tx.SrttNs)
