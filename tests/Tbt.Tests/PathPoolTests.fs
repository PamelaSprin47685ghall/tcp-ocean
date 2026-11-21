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
    let r = PathRefresher(60L * s, 2, s, 3, 10L * s)
    for i in 0 .. 9 do r.NoteJoined(uint16 i, int64 i * s)
    let present = ids [ 0 .. 9 ]
    Assert.Empty(r.Due(100L * s, present, false))
    // At 65 s ids 0..5 are 60 s old; oldest two go first.
    Assert.Equal<uint16[]>([| 0us; 1us |], r.Due(65L * s, present, true))
    // Same tick window: nothing more.
    Assert.Empty(r.Due(65L * s + 1L, present, true))
    // Next tick: in-flight cap 3 leaves room for one.
    Assert.Equal<uint16[]>([| 2us |], r.Due(66L * s, present, true))
    Assert.Empty(r.Due(67L * s, present, true))
    r.OnReplaced(0us, 67L * s)
    r.OnFailed(1us, 67L * s)
    Assert.Equal(1L, r.ReplacedCount)
    Assert.Equal(1L, r.FailedCount)
    // 0 is young again; 1 waits 10 s; 2 is still in flight, so two slots: 3 and 4.
    Assert.Equal<uint16[]>([| 3us; 4us |], r.Due(68L * s, present, true))
    r.OnReplaced(2us, 69L * s)
    r.OnReplaced(3us, 69L * s)
    // 1 retries as if joined at 17 s, so the older 5 and 6 still go first.
    Assert.Equal<uint16[]>([| 5us; 6us |], r.Due(78L * s, present, true))
    r.OnReplaced(4us, 79L * s)
    r.OnReplaced(5us, 79L * s)
    r.OnReplaced(6us, 79L * s)
    Assert.Equal<uint16[]>([| 7us; 8us |], r.Due(80L * s, present, true))

[<Fact>]
let ``PathRefresher is off with a zero age and ages unseen ids from first sight`` () =
    let s = 1_000_000_000L
    let off = PathRefresher(0L, 4, s, 8, s)
    Assert.False(off.Enabled)
    Assert.Empty(off.Due(1000L * s, ids [ 1; 2 ], true))
    let r = PathRefresher(60L * s, 4, s, 8, s)
    Assert.Empty(r.Due(10L * s, ids [ 7 ], true))
    Assert.Empty(r.Due(69L * s, ids [ 7 ], true))
    Assert.Equal<uint16[]>([| 7us |], r.Due(70L * s, ids [ 7 ], true))
