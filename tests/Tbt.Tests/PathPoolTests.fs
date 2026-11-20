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
