module Tbt.Tests.PathClassTests

open Xunit
open Tbt.Carrier

let private health rate retrans ca rtt minRtt demand =
    { PathHealth.unknown with
        Valid = true
        DeliveryRate = rate
        RetransRate = retrans
        CaState = ca
        RttUs = rtt
        MinRttUs = minRtt
        NotSentBytes = if demand then 1 else 0
        SampledNs = 1L }

[<Fact>]
let ``a flat quiet rate is a policer candidate and a lossy rate is not`` () =
    let flat = Array.create 6 400_000L
    let quiet = PathClass.ofSamples flat 6 0.01 0uy true
    Assert.Equal(PathKind.Policed, quiet.Kind)
    Assert.Equal(400_000L, quiet.CapBps)
    Assert.True(quiet.Confidence >= 600)
    let lossy = PathClass.ofSamples flat 6 0.40 4uy true
    Assert.NotEqual(PathKind.Policed, lossy.Kind)
    let cold = PathClass.ofSamples flat 2 0.0 0uy true
    Assert.Equal(PathKind.Unknown, cold.Kind)

[<Fact>]
let ``uniform plateaus are not a shared bottleneck and correlated stalls are`` () =
    let policed = [| PathKind.Policed; PathKind.Policed; PathKind.Policed; PathKind.Unknown |]
    let kind, conf = PathClass.sessionHint policed 0 4 None
    Assert.Equal(PathKind.Policed, kind)
    Assert.True(conf >= 400)
    let mixed = [| PathKind.Physical; PathKind.Physical; PathKind.Policed |]
    let stalled, _ = PathClass.sessionHint mixed 3 3 (Some false)
    Assert.Equal(PathKind.Physical, stalled)
    let unknown, _ = PathClass.sessionHint [| PathKind.Unknown |] 0 1 None
    Assert.Equal(PathKind.Unknown, unknown)

[<Fact>]
let ``deficit gives a fast path more visits than a slow one and keeps equals fair`` () =
    let wheel = DeficitWheel(4)
    let order = [| 0; 1 |]
    wheel.Refill [| 1; 4 |]
    let mutable fast = 0
    let mutable slow = 0
    for _ in 1 .. 5 do
        match wheel.Pick(order, 2, fun _ -> true) with
        | 0 -> slow <- slow + 1; wheel.Charge(0, 1)
        | 1 -> fast <- fast + 1; wheel.Charge(1, 1)
        | _ -> ()
    Assert.True(fast > slow)
    let even = DeficitWheel(2)
    even.Refill [| 1; 1 |]
    let first = even.Pick(order, 2, fun _ -> true)
    even.Charge(order.[first], 1)
    let second = even.Pick(order, 2, fun _ -> true)
    Assert.NotEqual(first, second)

[<Fact>]
let ``a plateau does not need a median outlier and rotation still runs when idle is false`` () =
    let h = health 500_000L 0.01 0uy 200_000u 180_000u true
    Assert.True(PathRefresher.IsPlateau(h, 0.05))
    Assert.False(PathRefresher.IsPlateau(h, 0.90))
    Assert.False(PathRefresher.IsPlateau({ h with RetransRate = 0.5 }, 0.05))
    let s = 1_000_000_000L
    let r = PathRefresher(60L * s, 1, 0, 1L, 4, 10L * s)
    let peers = [| struct (1us, h); struct (2us, h); struct (3us, h) |]
    for i in 1 .. 3 do r.NoteJoined(uint16 i, 0L)
    Assert.Empty(r.DueRotation(61L * s, peers, [| 2us |], true, true, 60L * s))
    Assert.Equal<uint16[]>([| 2us |], r.DueRotation(61L * s, peers, [| 2us |], true, false, 60L * s))

[<Fact>]
let ``rotation respects age and failed refresh retry for plateau and ordinary paths`` () =
    let h = health 500_000L 0.01 0uy 200_000u 180_000u true
    let s = 1_000_000_000L
    let r = PathRefresher(60L * s, 1, 0, s, 4, 10L * s)
    let peers = [| struct (1us, h) |]
    r.NoteJoined(1us, 0L)
    Assert.Empty(r.DueRotation(2L * s, peers, [| 1us |], true, false, 60L * s))
    Assert.Equal<uint16[]>([| 1us |], r.DueRotation(61L * s, peers, [| 1us |], true, false, 60L * s))
    r.OnFailed(1us, 61L * s)
    Assert.Empty(r.DueRotation(62L * s, peers, [| 1us |], true, false, 60L * s))
    Assert.Empty(r.DueRotation(63L * s, peers, [||], true, false, 60L * s))
    Assert.Equal<uint16[]>([| 1us |], r.DueRotation(71L * s, peers, [| 1us |], true, false, 60L * s))

[<Fact>]
let ``a policed queue is 20 ms at the cap and a missing rate stays a probe`` () =
    // 100 KiB/s * 20 ms = 2000 B, raised to the 4 KiB floor, then softened by 1/8.
    let q = PathBudget.PolicedQueue(100_000L, 1400)
    Assert.True(q >= 1400 && q <= 8192)
    let struct (pass1, pass2) = PathBudget.GateLimits 262144 100_000L 1400
    Assert.True(pass1 > 0 && pass2 >= pass1)
    Assert.True(pass2 < 16384)
    Assert.Equal(4 * 1400, PathBudget.QueueBytes(0L, 20L, 1400))
