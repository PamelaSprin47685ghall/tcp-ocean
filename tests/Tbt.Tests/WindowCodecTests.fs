module Tbt.Tests.WindowCodecTests

open System
open Xunit
open Tbt.Codec

let private sources (count: int) (size: int) (seed: int) =
    Array.init count (fun i -> Array.init size (fun j -> byte ((i * 31 + j * 17 + seed) &&& 255)))

let private xorSources (sources: byte[][]) (mask: uint64) =
    let size = sources.[0].Length
    let output = Array.zeroCreate<byte> size
    for i = 0 to sources.Length - 1 do
        if mask &&& (1UL <<< i) <> 0UL then
            for j = 0 to size - 1 do output.[j] <- output.[j] ^^^ sources.[i].[j]
    output

[<Fact>]
let ``stateless coefficients match fixed seed and encode matches naive XOR`` () =
    let sources = sources 16 64 5
    let a = WindowCodec.coefficients 16 5 12345UL
    let b = WindowCodec.coefficients 16 5 12345UL
    let c = WindowCodec.coefficients 16 5 54321UL
    Assert.Equal(a, b)
    Assert.NotEqual(a, c)
    Assert.Equal(5, System.Numerics.BitOperations.PopCount a)
    Assert.Equal<byte[]>(xorSources sources a, WindowCodec.encode sources a)

[<Fact>]
let ``deterministic full rank burst recovery returns the original sources`` () =
    let src = sources 16 48 7
    let decoder = WindowCodec.Decoder(16, 48, 1L <<< 20)
    let dropped = [| 1; 5; 9; 12 |]
    for i = 0 to 15 do
        if not (Array.contains i dropped) then Assert.True(decoder.Add(1UL <<< i, src.[i]))
    let equations =
        [| (dropped.[0], dropped.[1]); (dropped.[1], dropped.[2]); (dropped.[2], dropped.[3]); (dropped.[3], -1) |]
    for (a, b) in equations do
        let mask = (1UL <<< a) ||| (if b >= 0 then 1UL <<< b else 0UL)
        Assert.True(decoder.Add(mask, xorSources src mask))
    Assert.Equal(16, decoder.Rank)
    for i = 0 to 15 do
        match decoder.TryGet i with
        | Some actual -> Assert.Equal<byte[]>(src.[i], actual)
        | None -> failwithf "source %d not recovered" i
    decoder.Release()
    Assert.Equal(0, decoder.Rank)
    Assert.True(decoder.TryGet 0 |> Option.isNone)

[<Fact>]
let ``fixed seed random loss and shuffled duplicate parity stay idempotent`` () =
    let src = sources 32 32 11
    let rng = Random(2026)
    let missing = Array.init 12 (fun i -> i * 2 + 1)
    let decoder = WindowCodec.Decoder(32, 32, 1L <<< 20)
    for i = 0 to 31 do
        if not (Array.contains i missing) then Assert.True(decoder.Add(1UL <<< i, src.[i]))
    let parity =
        missing
        |> Array.mapi (fun i idx ->
            let partner = (rng.Next(32) + i + 1) % 32
            let mask = (1UL <<< idx) ||| (1UL <<< partner)
            (mask, xorSources src mask))
    let shuffled = parity |> Array.sortBy (fun (mask, _) -> int (mask &&& 0xFFFFUL))
    for (mask, data) in shuffled do decoder.Add(mask, data) |> ignore
    for (mask, data) in parity do
        Assert.False(decoder.Add(mask, data))
    for i in missing do
        match decoder.TryGet i with
        | Some actual -> Assert.Equal<byte[]>(src.[i], actual)
        | None -> failwithf "source %d not recovered" i

[<Fact>]
let ``rank deficit never outputs a source and contradictory rows are rejected`` () =
    let src = sources 8 16 9
    let short = WindowCodec.Decoder(8, 16, 1L <<< 16)
    let mask = 1UL ||| 2UL
    Assert.True(short.Add(mask, xorSources src mask))
    Assert.Equal(1, short.Rank)
    Assert.True(short.TryGet 0 |> Option.isNone)
    Assert.True(short.TryGet 1 |> Option.isNone)
    Assert.False(short.Add(mask, xorSources src mask))
    let decoder = WindowCodec.Decoder(8, 16, 1L <<< 16)
    Assert.True(decoder.Add(mask, xorSources src mask))
    let bad = Array.copy (xorSources src mask)
    bad.[0] <- bad.[0] ^^^ 1uy
    Assert.Throws<InvalidOperationException>(fun () -> decoder.Add(mask, bad) |> ignore) |> ignore

[<Fact>]
let ``decoder stays inside its configured byte budget`` () =
    let maxBytes = 16L * 8L + 17L * 64L
    let decoder = WindowCodec.Decoder(16, 64, maxBytes)
    Assert.True(decoder.AllocatedBytes <= maxBytes)
    let src = sources 16 64 2
    for i = 0 to 15 do decoder.Add(1UL <<< i, src.[i]) |> ignore
    Assert.True(decoder.AllocatedBytes <= maxBytes)
    Assert.Throws<ArgumentException>(fun () -> WindowCodec.Decoder(16, 64, maxBytes - 1L) |> ignore) |> ignore
