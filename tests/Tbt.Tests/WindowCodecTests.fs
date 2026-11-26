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
let ``sparse active masks select only active distinct slots deterministically`` () =
    let masks = [| 1UL; 1UL <<< 63; 0x8000000100010001UL; 0xAAAAAAAAAAAAAAAAUL; UInt64.MaxValue |]
    for active in masks do
        let count = int (System.Numerics.BitOperations.PopCount active)
        for degree in 1 .. count do
            for seed in [| 0UL; 1UL; UInt64.MaxValue |] do
                let selected = WindowCodec.coefficientsFromMask active degree seed
                Assert.Equal(selected, WindowCodec.coefficientsFromMask active degree seed)
                Assert.NotEqual(0UL, selected)
                Assert.Equal(0UL, selected &&& ~~~active)
                Assert.Equal(degree, int (System.Numerics.BitOperations.PopCount selected))
        Assert.Equal(active, WindowCodec.coefficientsFromMask active count 42UL)
    Assert.Throws<ArgumentException>(fun () -> WindowCodec.coefficientsFromMask 0UL 1 0UL |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> WindowCodec.coefficientsFromMask 1UL 0 0UL |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> WindowCodec.coefficientsFromMask 1UL 2 0UL |> ignore) |> ignore
    for count in 1 .. 64 do
        let active = if count = 64 then UInt64.MaxValue else (1UL <<< count) - 1UL
        Assert.Equal(WindowCodec.coefficients count 1 42UL, WindowCodec.coefficientsFromMask active 1 42UL)

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

[<Fact>]
let ``every tail length 1..64 recovers a dropped source without waiting for 64`` () =
    for k = 1 to 64 do
        let src = sources k 24 (k + 3)
        let dropped = k / 2
        let decoder = WindowCodec.Decoder(k, 24, 1L <<< 20)
        for i = 0 to k - 1 do
            if i <> dropped then Assert.True(decoder.Add(1UL <<< i, src.[i]))
        // A single sparse parity may miss the dropped index. A finite run of
        // distinct seeds must cover it; the masks stay stateless.
        let mutable got = None
        let mutable j = 0
        while got.IsNone && j < k do
            let mask = WindowCodec.WindowSessionCodec.parityMask 7UL j k
            let parity = WindowCodec.WindowSessionCodec.encodeParity 7UL j src
            // Consistent rows are duplicates, not failures: only rank decides recovery.
            decoder.Add(mask, parity) |> ignore
            if mask &&& (1UL <<< dropped) <> 0UL then
                got <- decoder.TryGet dropped
            j <- j + 1
        match got with
        | Some actual -> Assert.Equal<byte[]>(src.[dropped], actual)
        | None -> failwithf "tail %d did not recover index %d" k dropped
        Assert.True(WindowCodec.WindowSessionCodec.degree k >= 1 && WindowCodec.WindowSessionCodec.degree k <= min 15 k)
        Assert.True(WindowCodec.WindowSessionCodec.degree k % 2 = 1 || k < 4, sprintf "tail %d has even degree %d" k (WindowCodec.WindowSessionCodec.degree k))

[<Fact>]
let ``160 full windows respect the decoder payload budget and backpressure`` () =
    let symbolSize = 1344
    let pool = WindowCodec.WindowCodecPool(symbolSize, 160, WindowCodec.WindowSessionCodec.MaxDecoderBytes)
    for id in 0UL .. 159UL do
        Assert.True(pool.TryOpen(id, 64))
    Assert.False(pool.TryOpen(160UL, 64))
    let expected = 160L * (64L * 8L + 65L * int64 symbolSize)
    Assert.Equal(expected, pool.AllocatedBytes)
    Assert.True(pool.AllocatedBytes <= WindowCodec.WindowSessionCodec.MaxDecoderBytes)
    pool.ReleaseThrough 0UL
    Assert.True(pool.TryOpen(160UL, 64))
    Assert.Equal(expected, pool.AllocatedBytes)

[<Fact>]
let ``window pool enforces its window count and frees storage at the ack frontier`` () =
    let size = 32
    let one = int64 8 * 8L + 9L * int64 size
    let pool = WindowCodec.WindowCodecPool(size, WindowCodec.WindowSessionCodec.MaxWindows, one * int64 WindowCodec.WindowSessionCodec.MaxWindows)
    for id in 1UL .. uint64 WindowCodec.WindowSessionCodec.MaxWindows do
        Assert.True(pool.TryOpen(id, 8), sprintf "open %d" id)
    Assert.False(pool.TryOpen(uint64 (WindowCodec.WindowSessionCodec.MaxWindows + 1), 8))
    Assert.True(pool.AllocatedBytes <= WindowCodec.WindowSessionCodec.MaxDecoderBytes)
    Assert.Equal(int64 WindowCodec.WindowSessionCodec.MaxWindows * one, pool.AllocatedBytes)
    pool.ReleaseThrough 2UL
    Assert.Equal(int64 (WindowCodec.WindowSessionCodec.MaxWindows - 2) * one, pool.AllocatedBytes)
    Assert.True(pool.TryGet 2UL |> ValueOption.isNone)
    Assert.True(pool.TryGet 3UL |> ValueOption.isSome)
    Assert.True(pool.TryOpen(1000UL, 8))
    let tight = WindowCodec.WindowCodecPool(size, 4, one * 2L)
    Assert.True(tight.TryOpen(1UL, 8))
    Assert.True(tight.TryOpen(2UL, 8))
    Assert.False(tight.TryOpen(3UL, 8))
    tight.ReleaseThrough 1UL
    Assert.True(tight.TryOpen(3UL, 8))
    Assert.True(tight.AllocatedBytes <= tight.MaxBytes)

[<Fact>]
let ``TryAdd structural outcomes are atomic and never pollute stored rows`` () =
    let src = sources 8 16 21
    let d = WindowCodec.Decoder(8, 16, 1L <<< 16)
    let mask = 1UL ||| 2UL
    Assert.Equal(WindowCodec.AddOutcome.Innovative, d.TryAdd(mask, ReadOnlySpan(xorSources src mask)))
    Assert.Equal(1, d.Rank)
    Assert.Equal(WindowCodec.AddOutcome.Duplicate, d.TryAdd(mask, ReadOnlySpan(xorSources src mask)))
    Assert.Equal(1, d.Rank)
    // contradictory equation returns Invalid and leaves rank + rows intact
    let bad = Array.copy (xorSources src mask)
    bad.[0] <- bad.[0] ^^^ 1uy
    Assert.Equal(WindowCodec.AddOutcome.Invalid, d.TryAdd(mask, ReadOnlySpan bad))
    Assert.Equal(1, d.Rank)
    // the original equation is still consistent: state was not polluted
    Assert.Equal(WindowCodec.AddOutcome.Duplicate, d.TryAdd(mask, ReadOnlySpan(xorSources src mask)))
    Assert.Equal(WindowCodec.AddOutcome.Invalid, d.TryAdd(0UL, ReadOnlySpan(Array.zeroCreate<byte> 16)))
    Assert.Equal(WindowCodec.AddOutcome.Invalid, d.TryAdd(UInt64.MaxValue, ReadOnlySpan(Array.zeroCreate<byte> 16)))

[<Fact>]
let ``TryCopyKnown copies solved symbols into caller span without allocating`` () =
    let src = sources 8 16 33
    let d = WindowCodec.Decoder(8, 16, 1L <<< 16)
    let dst = Array.zeroCreate<byte> 16
    Assert.False(d.TryCopyKnown(0, Span dst))
    d.TryAdd(1UL, ReadOnlySpan src.[0]) |> ignore
    Assert.True(d.TryCopyKnown(0, Span dst))
    Assert.Equal<byte[]>(src.[0], dst)
    // overwrite with a different value: copy must fully replace the span
    Array.fill dst 0 16 0xFFuy
    Assert.True(d.TryCopyKnown(0, Span dst))
    Assert.Equal<byte[]>(src.[0], dst)

[<Fact>]
let ``reserved matrices survive release and enforce the full budget`` () =
    let one = 64L * 8L + 65L * 32L
    let pool = WindowCodec.WindowCodecPool(32, 2, one * 2L, reserveCount = 2)
    Assert.Equal(one * 2L, pool.ReservedBytes)
    Assert.Equal(2, pool.IdleReservedCount)
    Assert.True(pool.TryOpen(1UL, 64))
    Assert.True(pool.TryOpen(2UL, 64))
    Assert.False(pool.TryOpen(3UL, 64))
    let d = pool.TryGet(1UL).Value
    Assert.True(d.Add(1UL, Array.create 32 7uy))
    Assert.True(pool.Remove(1UL))
    Assert.Equal(1, pool.IdleReservedCount)
    Assert.True(pool.TryOpen(3UL, 64))
    Assert.Equal(0, pool.TryGet(3UL).Value.Rank)
    pool.ReleaseThrough(3UL)
    Assert.Equal(2, pool.IdleReservedCount)
    Assert.Equal(0L, pool.AllocatedBytes)
    Assert.Throws<ArgumentException>(fun () -> WindowCodec.WindowCodecPool(32, 2, one * 2L - 1L, reserveCount = 2) |> ignore) |> ignore

[<Fact>]
let ``pool Remove returns exact bytes and rolls back a freshly opened window`` () =
    let size = 32
    let one = int64 8 * 8L + 9L * int64 size
    let pool = WindowCodec.WindowCodecPool(size, 4, one * 4L)
    Assert.True(pool.TryOpen(7UL, 8))
    Assert.Equal(one, pool.AllocatedBytes)
    Assert.True(pool.Remove(7UL))
    Assert.Equal(0L, pool.AllocatedBytes)
    Assert.False(pool.Remove(7UL))
    Assert.False(pool.TryGet(7UL) |> ValueOption.isSome)
    Assert.True(pool.TryOpen(8UL, 8))
    Assert.Equal(one, pool.AllocatedBytes)

[<Fact>]
let ``steady TryAdd and TryCopyKnown allocate zero bytes on the hot path`` () =
    let count = 16
    let size = 48
    let src = sources count size 77
    let d = WindowCodec.Decoder(count, size, 1L <<< 20)
    let dst = Array.zeroCreate<byte> size
    // warm up all rows and all allocation sites before measuring
    for i = 0 to count - 1 do
        d.TryAdd(1UL <<< i, ReadOnlySpan src.[i]) |> ignore
        d.TryCopyKnown(i, Span dst) |> ignore
    let mutable outcome = WindowCodec.AddOutcome.Invalid
    let before = GC.GetAllocatedBytesForCurrentThread()
    for _ = 1 to 64 do
        for i = 0 to count - 1 do
            outcome <- d.TryAdd(1UL <<< i, ReadOnlySpan src.[i])
            d.TryCopyKnown(i, Span dst) |> ignore
    let after = GC.GetAllocatedBytesForCurrentThread()
    Assert.Equal(WindowCodec.AddOutcome.Duplicate, outcome)
    Assert.Equal(0L, after - before)
