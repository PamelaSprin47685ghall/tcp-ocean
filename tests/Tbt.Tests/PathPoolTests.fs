module Tbt.Tests.PathPoolTests

open System
open System.Collections.Generic
open Xunit
open Tbt.Carrier

let private noIds () = HashSet<uint16>()

/// The handshake path: present at start, and rebuilt only after it is lost.
let private withHandshake () =
    let s = HashSet<uint16>()
    s.Add 0us |> ignore
    s

let private items (r: struct (uint16 * uint32)[]) =
    [ for struct (id, inc) in r -> (id, inc) ]

[<Fact>]
let ``request returns every missing id once`` () =
    let pool = PathPool(4, 8, 100_000_000L, 2_000_000_000L, 8)
    let got = items (pool.Request(0L, withHandshake ()))
    if not (got = [ (1us, 8u); (2us, 8u); (3us, 8u) ]) then failwith (sprintf "%A" got)

[<Fact>]
let ``request skips ids that are already present`` () =
    let pool = PathPool(4, 8, 100_000_000L, 2_000_000_000L, 8)
    let present = withHandshake ()
    present.Add 1us |> ignore
    present.Add 3us |> ignore
    let got = items (pool.Request(0L, present))
    if not (got = [ (2us, 8u) ]) then failwith (sprintf "%A" got)

[<Fact>]
let ``request never exceeds the in-flight cap`` () =
    let pool = PathPool(8, 2, 100_000_000L, 2_000_000_000L, 8)
    let first = pool.Request(0L, noIds ())
    Assert.Equal(2, first.Length)
    let second = pool.Request(0L, noIds ())
    Assert.Equal(0, second.Length)

[<Fact>]
let ``failed id waits for the backoff and then restarts`` () =
    let pool = PathPool(2, 8, 100_000_000L, 2_000_000_000L, 1)
    let first = items (pool.Request(0L, withHandshake ()))
    if not (first = [ (1us, 1u) ]) then failwith (sprintf "%A" first)
    pool.Failed(1us, 0L)
    Assert.Equal(0, (pool.Request(50_000_000L, withHandshake ())).Length)
    let retry = items (pool.Request(100_000_000L, withHandshake ()))
    if not (retry = [ (1us, 2u) ]) then failwith (sprintf "%A" retry)

[<Fact>]
let ``joined clears the backoff`` () =
    let pool = PathPool(2, 8, 100_000_000L, 2_000_000_000L, 1)
    pool.Request(0L, withHandshake ()) |> ignore
    pool.Failed(1us, 0L)
    pool.Joined 1us
    let again = items (pool.Request(0L, withHandshake ()))
    if not (again = [ (1us, 2u) ]) then failwith (sprintf "%A" again)

[<Fact>]
let ``rebuilding one id keeps its incarnation increasing`` () =
    let pool = PathPool(2, 8, 100_000_000L, 2_000_000_000L, 1)
    let a = items (pool.Request(0L, withHandshake ()))
    pool.Joined 1us
    let b = items (pool.Request(0L, withHandshake ()))
    if not (a = [ (1us, 1u) ]) then failwith (sprintf "%A" a)
    if not (b = [ (1us, 2u) ]) then failwith (sprintf "%A" b)

[<Fact>]
let ``a single-path pool rebuilds the lost handshake path`` () =
    let pool = PathPool(1, 8, 100_000_000L, 2_000_000_000L, 8)
    let got = items (pool.Request(0L, noIds ()))
    if not (got = [ (0us, 8u) ]) then failwith (sprintf "%A" got)
    Assert.Equal(0, (pool.Request(0L, withHandshake ())).Length)

[<Fact>]
let ``the handshake path keeps its incarnation increasing across rebuilds`` () =
    let pool = PathPool(1, 8, 100_000_000L, 2_000_000_000L, 8)
    let a = items (pool.Request(0L, noIds ()))
    pool.Joined 0us
    let b = items (pool.Request(0L, noIds ()))
    if not (a = [ (0us, 8u) ]) then failwith (sprintf "%A" a)
    if not (b = [ (0us, 16u) ]) then failwith (sprintf "%A" b)

[<Fact>]
let ``a failed handshake-path rebuild backs off`` () =
    let pool = PathPool(1, 8, 100_000_000L, 2_000_000_000L, 8)
    pool.Request(0L, noIds ()) |> ignore
    pool.Failed(0us, 0L)
    Assert.Equal(0, (pool.Request(50_000_000L, noIds ())).Length)
    let retry = items (pool.Request(100_000_000L, noIds ()))
    if not (retry = [ (0us, 16u) ]) then failwith (sprintf "%A" retry)

[<Fact>]
let ``present suppresses every id`` () =
    let pool = PathPool(3, 8, 100_000_000L, 2_000_000_000L, 8)
    let present = HashSet<uint16>()
    present.Add 0us |> ignore
    present.Add 1us |> ignore
    present.Add 2us |> ignore
    Assert.Equal(0, (pool.Request(0L, present)).Length)

[<Fact>]
let ``Reserve gives a present id a fresh incarnation above every earlier one and blocks a parallel rebuild`` () =
    let pool = PathPool(4, 4, 100L, 1_000L, 3)
    let first = pool.Request(0L, HashSet<uint16>())
    for struct (id, _) in first do pool.Joined id
    let struct (_, inc0) = first |> Array.find (fun (struct (id, _)) -> id = 2us)
    match pool.Reserve 2us with
    | ValueSome inc ->
        Assert.True(inc > inc0 + 2u, sprintf "inc %d after %d (stride 3)" inc inc0)
        Assert.True(pool.Reserve(2us).IsNone, "already joining")
        // The pool does not rebuild an id while its refresh is in flight.
        let again = pool.Request(0L, HashSet<uint16>([ 0us; 1us; 3us ]))
        Assert.Empty(again)
        pool.Joined 2us
        Assert.True(pool.Reserve(2us).IsSome)
    | ValueNone -> failwith "reserve refused"
    Assert.True(pool.Reserve(9us).IsNone, "outside the pool")

let private ids (xs: int list) = xs |> List.map uint16

[<Fact>]
let ``PathRefresher refreshes the oldest paths first, a few per tick, never before ready`` () =
    let s = 1_000_000_000L
    let r = PathRefresher(60L * s, 2, 2, s, 3, 10L * s)
    for i in 0 .. 9 do r.NoteJoined(uint16 i, int64 i * s)
    let present = ids [ 0 .. 9 ]
    Assert.Empty(r.Due(100L * s, present, false, false))
    // At 65 s ids 0..5 are 60 s old; oldest two go first.
    Assert.Equal<uint16[]>([| 0us; 1us |], r.Due(65L * s, present, true, false))
    // Same tick window: nothing more.
    Assert.Empty(r.Due(65L * s + 1L, present, true, false))
    // Next tick: in-flight cap 3 leaves room for one.
    Assert.Equal<uint16[]>([| 2us |], r.Due(66L * s, present, true, false))
    Assert.Empty(r.Due(67L * s, present, true, false))
    r.OnReplaced(0us, 67L * s)
    r.OnFailed(1us, 67L * s)
    Assert.Equal(1L, r.ReplacedCount)
    Assert.Equal(1L, r.FailedCount)
    // 0 is young again; 1 waits 10 s; 2 is still in flight, so two slots: 3 and 4.
    Assert.Equal<uint16[]>([| 3us; 4us |], r.Due(68L * s, present, true, false))
    r.OnReplaced(2us, 69L * s)
    r.OnReplaced(3us, 69L * s)
    // 1 retries as if joined at 17 s, so the older 5 and 6 still go first.
    Assert.Equal<uint16[]>([| 5us; 6us |], r.Due(78L * s, present, true, false))
    r.OnReplaced(4us, 79L * s)
    r.OnReplaced(5us, 79L * s)
    r.OnReplaced(6us, 79L * s)
    Assert.Equal<uint16[]>([| 7us; 8us |], r.Due(80L * s, present, true, false))

[<Fact>]
let ``PathRefresher is off with a zero age and ages unseen ids from first sight`` () =
    let s = 1_000_000_000L
    let off = PathRefresher(0L, 4, 4, s, 8, s)
    Assert.False(off.Enabled)
    Assert.Empty(off.Due(1000L * s, ids [ 1; 2 ], true, false))
    let r = PathRefresher(60L * s, 4, 4, s, 8, s)
    Assert.Empty(r.Due(10L * s, ids [ 7 ], true, false))
    Assert.Empty(r.Due(69L * s, ids [ 7 ], true, false))
    Assert.Equal<uint16[]>([| 7us |], r.Due(70L * s, ids [ 7 ], true, false))

[<Fact>]
let ``PathRefresher works faster while the session is idle`` () =
    let s = 1_000_000_000L
    let r = PathRefresher(60L * s, 1, 4, s, 8, s)
    let present = ids [ 0 .. 9 ]
    for i in 0 .. 9 do r.NoteJoined(uint16 i, 0L)
    Assert.Equal(1, r.Due(61L * s, present, true, false).Length)
    Assert.Equal(4, r.Due(62L * s, present, true, true).Length)
    Assert.Equal(3, r.Due(63L * s, present, true, true).Length)

[<Fact>]
let ``NoPathWatch does not exit while initial joins are in flight`` () =
    let ms = 1_000_000L
    let w = NoPathWatch(3000L * ms, 30_000L * ms)
    // "session up" with paths=0: 64 JOINs in flight for 10 s (slow server after restart).
    for t in 0L .. 100L .. 10_000L do
        Assert.False(w.ShouldExit(t * ms, true, 64), sprintf "exited at %d ms with joins in flight" t)
    // The first path lands: the watch resets.
    Assert.False(w.ShouldExit(10_100L * ms, false, 10))
    Assert.False(w.Watching)

[<Fact>]
let ``NoPathWatch exits 3 s after the last join attempt, or at the cap`` () =
    let ms = 1_000_000L
    let w = NoPathWatch(3000L * ms, 30_000L * ms)
    Assert.False(w.ShouldExit(0L, true, 4))
    Assert.False(w.ShouldExit(1000L * ms, true, 0))   // backoff gap counts from the last busy look
    Assert.False(w.ShouldExit(2900L * ms, true, 0))
    Assert.True(w.ShouldExit(3000L * ms, true, 0))
    let c = NoPathWatch(3000L * ms, 30_000L * ms)
    for t in 0L .. 500L .. 29_500L do Assert.False(c.ShouldExit(t * ms, true, 1))
    Assert.True(c.ShouldExit(30_000L * ms, true, 1))  // a dead session's JOINs never land

[<Fact>]
let ``CcNudgePlan spreads nudges over the period and skips young sockets`` () =
    let s = 1_000_000_000L
    let keys = Array.init 60 (fun i -> box i)
    let p = CcNudgePlan<obj>(30L * s, s, false)
    Assert.Empty(p.Due(s, keys, fun _ -> 0L))              // first seen: not yet eligible
    Assert.Empty(p.Due(s + s / 2L, keys, fun _ -> 0L))     // within the tick
    let picked = System.Collections.Generic.List<obj>()
    for t in 31L .. 60L do picked.AddRange(p.Due(t * s, keys, fun _ -> 0L))
    // 60 sockets over a 30 s period at 1 s ticks: 2 per tick, each exactly once.
    Assert.Equal(60, picked.Count)
    Assert.Equal(60, (System.Collections.Generic.HashSet<obj>(picked, HashIdentity.Reference)).Count)
    Assert.Equal(60L, p.Nudged)

[<Fact>]
let ``CcNudgePlan worst pick takes the lowest score first`` () =
    let s = 1_000_000_000L
    let keys = Array.init 10 (fun i -> box i)
    let p = CcNudgePlan<obj>(10L * s, s, true)
    p.Due(0L + s, keys, fun _ -> 0L) |> ignore
    let first = p.Due(11L * s, keys, fun k -> int64 (100 - unbox<int> k))
    Assert.Equal<obj[]>([| box 9 |], first)
    Assert.Empty(CcNudgePlan<obj>(0L, s, false).Due(100L * s, keys, fun _ -> 0L))

[<Fact>]
let ``reinitCongestion keeps the socket on its congestion module`` () =
    if OperatingSystem.IsLinux()
       && (System.IO.File.ReadAllText "/proc/sys/net/ipv4/tcp_available_congestion_control").Contains "reno" then
        use l = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
        l.Bind(System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)); l.Listen 1
        use c = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
        c.Connect(l.LocalEndPoint)
        let cc = (System.IO.File.ReadAllText "/proc/sys/net/ipv4/tcp_congestion_control").Trim()
        Assert.True(SocketTuning.reinitCongestion(c, cc, "reno"))
        let buf = Array.zeroCreate<byte> 16
        let n = c.GetRawSocketOption(6, 13, Span buf)
        Assert.Equal(cc, System.Text.Encoding.ASCII.GetString(buf, 0, n).TrimEnd('\000'))
        c.Dispose()
        Assert.False(SocketTuning.reinitCongestion(c, cc, "reno"))

[<Fact>]
let ``PathRefresher jitter spreads a new session's first refresh over maxAge*(1 +- j)`` () =
    let s = 1_000_000_000L
    let mk j =
        let r = PathRefresher(60L * s, 256, 256, s, 256, 10L * s)
        r.Jitter <- j
        for i in 0 .. 255 do r.NoteJoined(uint16 i, 0L)
        r
    let present = ids [ 0 .. 255 ]
    // Fixed age: the whole pool is due in the same second (the refresh wave).
    let fixedR = mk 0.0
    Assert.Empty(fixedR.Due(59L * s, present, true, true))
    Assert.Equal(256, fixedR.Due(60L * s, present, true, true).Length)
    // 50 % jitter: nothing before 30 s, everything by 90 s, no second carries a wave.
    let r = mk 0.5
    let mutable total = 0
    let mutable most = 0
    for t in 1L .. 91L do
        let n = r.Due(t * s, present, true, true).Length
        if t < 30L then Assert.Equal(0, n)
        total <- total + n
        most <- max most n
    Assert.Equal(256, total)
    Assert.True(most <= 16, sprintf "largest per-second batch %d" most)

let private mkHealth (caState: byte) (deliveryRate: int64) (retransRate: float) (notSent: int) =
    { PathHealth.unknown with
        SampledNs = 1_000_000_000L
        CaState = caState
        RttUs = 113_000u
        MinRttUs = 112_000u
        CwndBytes = 65536u
        DeliveryRate = deliveryRate
        NotSentBytes = notSent
        RetransRate = retransRate
        Valid = true }

[<Fact>]
let ``PathRefresher DueDegraded does zero idle churn and only replaces degraded paths under load`` () =
    let s = 1_000_000_000L
    let r = PathRefresher(60L * s, 2, 0, s, 4, 10L * s)
    for id in 0us .. 4us do r.NoteJoined(id, 0L)
    let paths =
        [| struct (0us, mkHealth 0uy 2_000_000L 0.02 0)       // healthy fast
           struct (1us, mkHealth 0uy 2_100_000L 0.01 0)       // healthy fast
           struct (2us, mkHealth 0uy 1_900_000L 0.03 0)       // healthy fast
           struct (3us, mkHealth 4uy 2_000_000L 0.20 0)       // stuck in TCP_CA_Loss
           struct (4us, mkHealth 0uy   200_000L 0.10 4096) |] // BBR collapsed < med/4 with demand
    // Idle: ZERO refreshes even when degraded paths are past maxAge.
    Assert.Empty(r.DueDegraded(120L * s, paths, true, true))
    // Active: picks the two degraded paths (Loss first, then BBR-collapsed), never healthy 0..2.
    let picked = r.DueDegraded(121L * s, paths, true, false)
    Assert.Equal<uint16[]>([| 3us; 4us |], picked)
    // Next tick: remaining healthy paths are left untouched.
    Assert.Empty(r.DueDegraded(122L * s, paths, true, false))

[<Fact>]
let ``PathRefresher DueDegraded replaces a zero-rate path with backlog when peers still deliver`` () =
    let s = 1_000_000_000L
    let r = PathRefresher(60L * s, 2, 0, s, 4, 10L * s)
    for id in 0us .. 4us do r.NoteJoined(id, 0L)
    let live =
        [| struct (0us, mkHealth 0uy 2_000_000L 0.01 0)
           struct (1us, mkHealth 0uy 2_100_000L 0.01 0)
           struct (2us, mkHealth 0uy 1_900_000L 0.02 0)
           struct (3us, { mkHealth 0uy 0L 0.0 8192 with RttUs = 200_000u })
           struct (4us, mkHealth 0uy 1_800_000L 0.02 0) |]
    Assert.Equal<uint16[]>([| 3us |], r.DueDegraded(121L * s, live, true, false))
    // Every path at zero is a shared stall, not a candidate to kill.
    let stalled = PathRefresher(60L * s, 4, 0, s, 8, 10L * s)
    for id in 0us .. 3us do stalled.NoteJoined(id, 0L)
    let allZero =
        [| for id in 0us .. 3us -> struct (id, { mkHealth 0uy 0L 0.0 4096 with RttUs = 200_000u }) |]
    Assert.Empty(stalled.DueDegraded(121L * s, allZero, true, false))

[<Fact>]
let ``CcNudgePlan precomputes score once per eligible key and DueDegraded skips idle and healthy sockets`` () =
    let s = 1_000_000_000L
    let keys = Array.init 16 (fun i -> box i)
    let p = CcNudgePlan<obj>(10L * s, s, true)
    p.Due(s, keys, fun _ -> 0L) |> ignore
    let mutable calls = 0
    let picked =
        p.Due(11L * s, keys, fun k ->
            calls <- calls + 1
            int64 (100 - unbox<int> k))
    // O(n) precomputation, not O(n log n) calls inside comparator.
    Assert.Equal(keys.Length, calls)
    Assert.Equal<obj[]>([| box 15; box 14 |], picked)
    // DueDegraded: idle => empty; active => only Loss / BBR-collapsed.
    let p2 = CcNudgePlan<obj>(10L * s, s, true)
    let snaps =
        [| struct (keys.[0], mkHealth 0uy 2_000_000L 0.01 0)
           struct (keys.[1], mkHealth 0uy 2_000_000L 0.01 0)
           struct (keys.[2], mkHealth 0uy 2_000_000L 0.01 0)
           struct (keys.[3], mkHealth 4uy 2_000_000L 0.20 0)
           struct (keys.[4], mkHealth 0uy   100_000L 0.05 8192) |]
    Assert.Empty(p2.DueDegraded(s, snaps, true))
    Assert.Empty(p2.DueDegraded(12L * s, snaps, false))
    let nudged = p2.DueDegraded(13L * s, snaps, true)
    Assert.Equal<obj[]>([| keys.[3] |], nudged)

[<Fact>]
let ``WriteGate adaptivePass2Limit keeps the carrier fed and clamps cleanly`` () =
    // Floored at one cwnd / 64 KiB: half a cwnd starved a socket whose cwnd SACK recovery had
    // shrunk to 15-30 KiB, so the kernel stayed app-limited and cwnd never grew back.
    Assert.Equal(262144, WriteGate.adaptivePass2Limit 262144 0u)
    Assert.Equal(65536, WriteGate.adaptivePass2Limit 262144 8000u)
    Assert.Equal(65536, WriteGate.adaptivePass2Limit 262144 65536u)
    Assert.Equal(98304, WriteGate.adaptivePass2Limit 262144 131072u)
    Assert.Equal(98304, WriteGate.adaptivePass2Limit 262144 2_000_000u)

[<Fact>]
let ``PathPool Saturated reflects live exhaustion and clears when no requested slot is exhausted`` () =
    let pool = PathPool(2, 4, 100L, 1_000L, 1)
    let first = pool.Request(0L, noIds ())
    Assert.Equal(2, first.Length)
    Assert.False(pool.Saturated)
    pool.Joined 0us
    pool.Joined 1us
    let present = HashSet<uint16>([ 0us; 1us ])
    Assert.Empty(pool.Request(10L, present))
    Assert.False(pool.Saturated)
