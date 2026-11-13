module Tbt.Tests.BootstrapTests

open System
open System.Collections.Generic
open Xunit
open Tbt.Session.JoinBootstrap

[<Fact>]
let ``at 400 ms and 30% loss the socket timeout covers five RTO doublings from 1 s`` () =
    // transmissionsFor 0.3 = 6, so k = 5; wait = 1000 * 31 + 400 = 31400.
    Assert.Equal(6, transmissionsFor 0.30)
    Assert.Equal(1000, initialRtoMs 400)
    Assert.Equal(31_400, socketTimeoutMs 400 0.30)
    Assert.Equal(5, maxAttempts 0.30)

[<Fact>]
let ``zero loss keeps the 10 s floor; a long RTT grows the timeout and is capped`` () =
    Assert.Equal(4, transmissionsFor 0.0)
    Assert.Equal(MinTimeoutMs, socketTimeoutMs 400 0.0)
    Assert.Equal(MinTimeoutMs, socketTimeoutMs 20 0.0)
    // rtt = 2000 ms → rto0 = 4000; k = 3 at zero loss; 4000 * 7 + 2000 = 30000.
    Assert.Equal(30_000, socketTimeoutMs 2000 0.0)
    Assert.Equal(MaxTimeoutMs, socketTimeoutMs 4000 0.30)

[<Fact>]
let ``backoff doubles with RTT and is capped; attempts rise with loss`` () =
    Assert.Equal(400, backoffMs 400 0)
    Assert.Equal(800, backoffMs 400 1)
    Assert.Equal(1600, backoffMs 400 2)
    Assert.Equal(3200, backoffMs 400 3)
    Assert.Equal(5000, backoffMs 400 4)
    Assert.Equal(200, backoffMs 0 0)
    Assert.Equal(3, maxAttempts 0.0)
    Assert.Equal(4, maxAttempts 0.05)
    Assert.Equal(5, maxAttempts 0.30)

[<Fact>]
let ``tryRepeated succeeds on a later attempt, sleeps only between failures, and never throws`` () =
    let sleeps = List<int>()
    let mutable seen = 0
    let r =
        tryRepeated 5 (backoffMs 400) sleeps.Add (fun i ->
            seen <- seen + 1
            if i < 2 then failwith ("transient " + string i))
    Assert.Equal(Joined, r)
    Assert.Equal(3, seen)
    Assert.Equal<int[]>([| 400; 800 |], Array.ofSeq sleeps)

[<Fact>]
let ``tryRepeated gives up after the budget and swallows the last exception`` () =
    let sleeps = List<int>()
    let r =
        tryRepeated 3 (fun i -> 10 * (i + 1)) sleeps.Add (fun i ->
            failwith ("path closed during bootstrap #" + string i))
    match r with
    | GaveUp msg -> Assert.Equal("path closed during bootstrap #2", msg)
    | Joined -> failwith "expected exhaustion"
    Assert.Equal<int[]>([| 10; 20 |], Array.ofSeq sleeps)

[<Fact>]
let ``a non-path-0 exhaustion is a GaveUp value, not an exception the caller must catch`` () =
    // Mirrors the JOIN loop: the helper itself is the catch-all.
    let r = tryRepeated 2 (fun _ -> 0) ignore (fun _ -> failwith "connect timed out")
    match r with
    | GaveUp msg -> Assert.Equal("connect timed out", msg)
    | Joined -> failwith "expected exhaustion"
