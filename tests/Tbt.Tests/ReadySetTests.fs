module Tbt.Tests.ReadySetTests

open System
open Xunit
open Tbt.Contracts

let private walk (rs: ReadySet) =
    let got = Collections.Generic.List<int>()
    let mutable s = rs.NextUp -1
    while s >= 0 do
        got.Add s
        s <- rs.NextUp s
    got.ToArray()

[<Fact>]
let ``ready set: add, remove, contains and count, duplicates count once`` () =
    let rs = ReadySet()
    Assert.True(rs.IsEmpty)
    for s in [ 0; 63; 64; 200; 255; 63 ] do rs.Add s
    Assert.Equal(5, rs.Count)
    Assert.True(rs.Contains 64 && rs.Contains 255 && not (rs.Contains 65))
    rs.Remove 63
    rs.Remove 63
    rs.Remove 1
    Assert.Equal(4, rs.Count)
    Assert.Equal<int[]>([| 0; 64; 200; 255 |], walk rs)

[<Fact>]
let ``ready set: NextAfter wraps, NextUp does not, empty gives -1`` () =
    let rs = ReadySet()
    Assert.Equal(-1, rs.NextAfter -1)
    Assert.Equal(-1, rs.NextUp -1)
    rs.Add 5
    rs.Add 130
    Assert.Equal(5, rs.NextAfter -1)
    Assert.Equal(130, rs.NextAfter 5)
    Assert.Equal(5, rs.NextAfter 130)
    Assert.Equal(5, rs.NextAfter 255)
    Assert.Equal(5, rs.NextAfter 4)
    // Only one slot: wraps to itself.
    rs.Remove 130
    Assert.Equal(5, rs.NextAfter 5)
    Assert.Equal(-1, rs.NextUp 5)
    Assert.Equal(-1, rs.NextUp 255)

[<Fact>]
let ``ready set: full (256) and empty (0) differ, walk visits every slot once in order`` () =
    let rs = ReadySet()
    for s in 0 .. 255 do rs.Add s
    Assert.Equal(256, rs.Count)
    Assert.Equal<int[]>([| 0 .. 255 |], walk rs)
    for s in 0 .. 255 do Assert.Equal((s + 1) % 256, rs.NextAfter s)
    rs.Clear()
    Assert.Equal(0, rs.Count)
    Assert.Empty(walk rs)

[<Fact>]
let ``ready set: matches a reference set over random operations`` () =
    let rnd = Random 7
    let rs = ReadySet()
    let reference = Array.zeroCreate<bool> 256
    for _ in 1 .. 20000 do
        let s = rnd.Next 256
        let on = rnd.Next 2 = 0
        rs.Set(s, on)
        reference.[s] <- on
        Assert.Equal(reference |> Array.filter id |> Array.length, rs.Count)
        let a = rnd.Next(-1, 256)
        let mutable expected = -1
        let mutable k = 1
        while expected < 0 && k <= 256 do
            let c = (a + k) &&& 255
            if reference.[c] then expected <- c
            k <- k + 1
        Assert.Equal(expected, rs.NextAfter a)
    Assert.Equal<int[]>([| for i in 0 .. 255 do if reference.[i] then i |], walk rs)

[<Fact>]
let ``ready set: invalid slots fail before indexing`` () =
    let rs = ReadySet()
    Assert.Throws<ArgumentException>(fun () -> rs.Add 256) |> ignore
    Assert.Throws<ArgumentException>(fun () -> rs.Remove -1) |> ignore
    Assert.Throws<ArgumentException>(fun () -> rs.Contains 300 |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> rs.NextAfter -2 |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> rs.NextUp 256 |> ignore) |> ignore
    Assert.Equal(0, rs.Count)

[<Fact>]
let ``ready set: operations do not allocate`` () =
    let rs = ReadySet()
    let run () =
        let mutable acc = 0
        for i in 0 .. 9999 do
            rs.Add((i * 37) &&& 255)
            acc <- acc + rs.NextAfter((i * 11) &&& 255) + rs.NextUp((i * 5) &&& 255)
            if rs.Contains((i * 3) &&& 255) then rs.Remove((i * 3) &&& 255)
        acc
    run () |> ignore
    let before = GC.GetAllocatedBytesForCurrentThread()
    run () |> ignore
    Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before)
