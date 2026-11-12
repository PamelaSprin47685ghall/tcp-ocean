module Tbt.Tests.CodecTests

open System
open System.Security.Cryptography
open Xunit
open Tbt.Contracts
open Tbt.Codec
open Tbt.Codec.CodeGraph
open Tbt.Tests.Vectors

let private gseed = 0x0123456789ABCDEFUL
let private symbol = 1344

// ---- the source model of ref/make_vectors.py ----
let private appBytes (t: uint64) (s: int) (n: int) =
    let mutable r = SplitMix64.Rng(SplitMix64.seedStep (SplitMix64.seedStep 0xA5A5A5A5A5A5A5A5UL t) (uint64 s))
    let b = Array.zeroCreate<byte> ((n + 7) / 8 * 8)
    for i in 0 .. b.Length / 8 - 1 do
        BitConverter.TryWriteBytes(Span<byte>(b, i * 8, 8), r.Next()) |> ignore
    b.[.. n - 1]

let private stepUsed (t: uint64) = if t = 5UL then 5 else 16

/// Source D[t, s] of the vector model, or None for an unused slot.
let source (t: uint64) (s: int) : byte[] option =
    let k = stepUsed t
    if s >= k then None
    else
        let n = if t = 5UL && s = k - 1 then 100 else symbol - 16
        let flags = if t = 0UL && s = 0 then 1uy else 0uy
        let b = Array.zeroCreate<byte> symbol
        BitConverter.TryWriteBytes(Span<byte>(b, 0, 2), uint16 n) |> ignore
        b.[2] <- byte (s % 4)
        b.[3] <- flags
        BitConverter.TryWriteBytes(Span<byte>(b, 4, 4), 7u) |> ignore
        BitConverter.TryWriteBytes(Span<byte>(b, 8, 8), (t * 16UL + uint64 s) * uint64 (symbol - 16)) |> ignore
        Array.blit (appBytes t s n) 0 b 16 n
        Some b

let private termsText (terms: Term[]) =
    String.Join(" ", terms |> Array.map (fun x -> $"{x.Step}:{x.Slot}:{x.Coef}"))

let private combineModel (terms: Term[]) =
    let acc = Array.zeroCreate<byte> symbol
    for term in terms do
        match source term.Step (int term.Slot) with
        | Some d -> Gf16.mulAddInto term.Coef (ReadOnlySpan d) (Span acc)
        | None -> ()
    acc

let private digest (b: byte[]) = $"{toHex (ReadOnlySpan b.[.. 15])} / {toHex (ReadOnlySpan(SHA256.HashData b))}"

let private field (f: int) = if f = 2 then CodeField.Gf2 else CodeField.Gf16

[<Fact>]
let ``SplitMix64 and seed derivation match the reference`` () =
    let v = codec.Value
    let mutable r = SplitMix64.Rng(0UL)
    let got = [| for _ in 1..3 -> "0x" + r.Next().ToString("x") |]
    let want = [| for e in v.GetProperty("splitmix64(state=0) first 3").EnumerateArray() -> string (e.GetString()) |]
    Assert.Equal<string[]>(want, got)
    let seed = SplitMix64.seedOf4 gseed SplitMix64.DomParity 16UL 300UL 1UL
    Assert.Equal(str v "seed_of(GSEED, DOM_PARITY, 16, 300, 1)", "0x" + seed.ToString("x"))

[<Fact>]
let ``GF16 tables match the reference`` () =
    let v = codec.Value
    let want = [| for e in v.GetProperty("GF16 2*9, 7*7, 0xF*0xF, inv(7)").EnumerateArray() -> e.GetInt32() |]
    Assert.Equal<int[]>(want, [| int (Gf16.mul 2uy 9uy); int (Gf16.mul 7uy 7uy); int (Gf16.mul 15uy 15uy); int (Gf16.inv 7uy) |])
    let row = [| for e in v.GetProperty("GF16 row MUL[3]").EnumerateArray() -> e.GetInt32() |]
    Assert.Equal<int[]>(row, Array.init 16 (fun b -> int (Gf16.mul 3uy (byte b))))
    let acc = Array.zeroCreate<byte> 5
    Gf16.mulAddInto 7uy (ReadOnlySpan [| 0uy; 1uy; 0x10uy; 0xFFuy; 0xA5uy |]) (Span acc)
    Assert.Equal(str v "gf_mul_bytes(7, 00 01 10 ff a5)", toHex (ReadOnlySpan acc))

[<Fact>]
let ``vectorized multiply-add equals the scalar table on every coefficient`` () =
    let rnd = Random(7)
    for c in 0 .. 15 do
        let src = Array.zeroCreate<byte> 1347
        rnd.NextBytes src
        let a = Array.zeroCreate<byte> 1347
        rnd.NextBytes a
        let b = Array.copy a
        Gf16.mulAddInto (byte c) (ReadOnlySpan src) (Span a)
        for i in 0 .. b.Length - 1 do
            b.[i] <- b.[i] ^^^ Gf16.MulByte.[c * 256 + int src.[i]]
        Assert.Equal<byte[]>(b, a)

[<Theory>]
[<InlineData(300, 0, 16)>]
[<InlineData(300, 1, 16)>]
[<InlineData(300, 1, 2)>]
[<InlineData(300, 47, 16)>]
[<InlineData(0, 3, 16)>]
[<InlineData(3, 2, 2)>]
let ``parity term lists match the reference`` (t: int, p: int, f: int) =
    let buf = Array.zeroCreate<Term> MaxTerms
    let n = parityTerms gseed (uint64 t) p (field f) 16 false (Span buf)
    Assert.Equal(str codec.Value $"parity_terms(t={t},p={p},field={f})", termsText buf.[.. n - 1])

[<Theory>]
[<InlineData(300, 1, 2, 1, false)>]
[<InlineData(300, 2, 2, 3, false)>]
[<InlineData(300, 5, 2, 1, true)>]
[<InlineData(300, 3, 2, 3, true)>]
[<InlineData(300, 3, 16, 3, true)>]
[<InlineData(300, 1, 16, 1, true)>]
[<InlineData(300, 2, 16, 3, false)>]
let ``small-step and URGENT term lists match the reference`` (t: int, p: int, f: int, k: int, urgent: bool) =
    let buf = Array.zeroCreate<Term> MaxTerms
    let n = parityTerms gseed (uint64 t) p (field f) k urgent (Span buf)
    let key = $"parity_terms(t={t},p={p},field={f},k={k},urgent={(if urgent then 1 else 0)})"
    Assert.Equal(str codec.Value key, termsText buf.[.. n - 1])

[<Theory>]
[<InlineData(0, 0, 16)>]
[<InlineData(0, 1, 16)>]
[<InlineData(0, 1, 2)>]
[<InlineData(5, 0, 2)>]
[<InlineData(5, 2, 16)>]
[<InlineData(6, 1, 16)>]
[<InlineData(300, 0, 2)>]
[<InlineData(300, 1, 2)>]
[<InlineData(300, 1, 16)>]
[<InlineData(300, 47, 16)>]
[<InlineData(300, 47, 2)>]
let ``parity symbols match the reference`` (t: int, p: int, f: int) =
    // The reference combines with k = 16 for the term list and treats unused slots as zero.
    let buf = Array.zeroCreate<Term> MaxTerms
    let n = parityTerms gseed (uint64 t) p (field f) 16 false (Span buf)
    Assert.Equal(str codec.Value $"parity(t={t},p={p},field={f}) first16 / sha256", digest (combineModel buf.[.. n - 1]))

[<Theory>]
[<InlineData(300, 1, 0)>]
[<InlineData(300, 1, 1)>]
[<InlineData(298, 4, 0)>]
[<InlineData(5, 1, 0)>]
let ``BOOSTER symbols match the reference`` (tLo: int, span: int, seq: int) =
    let buf = Array.zeroCreate<Term> MaxTerms
    let n = boosterTerms gseed (uint64 tLo) span seq (Span buf)
    Assert.Equal(str codec.Value $"booster(t_lo={tLo},span={span},seq={seq}) first16 / sha256", digest (combineModel buf.[.. n - 1]))

[<Fact>]
let ``partial step parity and source envelopes match the reference`` () =
    let buf = Array.zeroCreate<Term> MaxTerms
    let n = parityTerms gseed 5UL 1 CodeField.Gf2 5 false (Span buf)
    Assert.Equal(str codec.Value "parity(t=5,p=1,field=2,k=5) first16 / sha256", digest (combineModel buf.[.. n - 1]))
    Assert.Equal(str codec.Value "source(5,4) envelope", toHex (ReadOnlySpan (source 5UL 4).Value.[.. 15]))
    Assert.Equal(str codec.Value "source(0,0) first32", toHex (ReadOnlySpan (source 0UL 0).Value.[.. 31]))

/// Fill an encoder with the vector model for steps 0..steps-1.
let private filledEncoder (steps: int) =
    let enc = Encoder(gseed, symbol, 1024)
    for t in 0 .. steps - 1 do
        let k = stepUsed (uint64 t)
        for s in 0 .. k - 1 do
            let mutable slot = Span<byte>.Empty
            Assert.True(enc.TryGetFillSlot(s, &slot))
            (source (uint64 t) s).Value.CopyTo(slot)
        Assert.Equal(uint64 t, enc.Seal k)
    enc

[<Fact>]
let ``encoder renders the reference parity and BOOSTER symbols`` () =
    let enc = filledEncoder 302
    let out = Array.zeroCreate<byte> symbol
    Assert.True(enc.TryRenderParity(300UL, 47, CodeField.Gf16, false, Span out))
    Assert.Equal(str codec.Value "parity(t=300,p=47,field=16) first16 / sha256", digest out)
    Assert.True(enc.TryRenderParity(5UL, 1, CodeField.Gf2, false, Span out))
    Assert.Equal(str codec.Value "parity(t=5,p=1,field=2,k=5) first16 / sha256", digest out)
    Assert.True(enc.TryRenderBooster(298UL, 4, 0, Span out))
    Assert.Equal(str codec.Value "booster(t_lo=298,span=4,seq=0) first16 / sha256", digest out)
    Assert.False(enc.TryRenderBooster(299UL, 4, 0, Span out))   // step 302 is not sealed
    Assert.True(enc.TryRenderBooster(5UL, 1, 0, Span out))
    Assert.Equal(str codec.Value "booster(t_lo=5,span=1,seq=0) first16 / sha256", digest out)

[<Fact>]
let ``encoder refuses parity that needs a released source`` () =
    let enc = filledEncoder 301
    enc.Release 50UL
    let out = Array.zeroCreate<byte> symbol
    Assert.False(enc.TryRenderParity(300UL, 0, CodeField.Gf16, false, Span out))   // needs step 44
    Assert.True(enc.TryRenderParity(300UL, 0, CodeField.Gf16, true, Span out))     // URGENT: no history

/// Run a full stream through erasure at rate eps; return (recovered all?, decoder).
let private roundTrip (f: CodeField) (eps: float) (rho: float) (steps: int) (seed: int) =
    let enc = filledEncoder steps
    let dec = Decoder(gseed, symbol, 1024, 8192)
    let rnd = Random(seed)
    let out = Array.zeroCreate<byte> symbol
    let mutable acc = 0.0
    for t in 0 .. steps - 1 do
        let k = stepUsed (uint64 t)
        for s in 0 .. k - 1 do
            if rnd.NextDouble() >= eps then
                Assert.True(dec.OfferSource(uint64 t, s, k, enc.Source(uint64 t, s)))
        acc <- acc + float k * rho
        let m = int (floor acc)
        acc <- acc - float m
        for p in 0 .. m - 1 do
            if rnd.NextDouble() >= eps then
                // A real receiver learns k of the history steps from meta.k_hist.
                Assert.True(dec.NoteStepUsed(uint64 t, k))
                for r in Strides do
                    if uint64 t >= r then Assert.True(dec.NoteStepUsed(uint64 t - r, stepUsed (uint64 t - r)))
                Assert.True(enc.TryRenderParity(uint64 t, p, f, false, Span out))
                dec.OfferParity(uint64 t, p, f, false, k, ReadOnlySpan out) |> ignore
    enc, dec

let private assertRecovered (enc: Encoder) (dec: Decoder) (upTo: int) =
    for t in 0 .. upTo - 1 do
        for s in 0 .. stepUsed (uint64 t) - 1 do
            Assert.True(dec.Source(uint64 t, s).SequenceEqual(enc.Source(uint64 t, s)), $"source {t}:{s}")

[<Theory>]
[<InlineData(16, 0.05, 0.25, 11)>]
[<InlineData(2, 0.05, 0.25, 12)>]
[<InlineData(16, 0.20, 0.60, 13)>]
let ``decoder recovers every source under random erasure`` (f: int, eps: float, rho: float, seed: int) =
    let steps = 400
    let enc, dec = roundTrip (field f) eps rho steps seed
    // The tail has no later parities; check the part that the code covers.
    Assert.True(dec.Frontier >= uint64 (steps - 40), $"frontier {dec.Frontier}")
    assertRecovered enc dec (int dec.Frontier)

[<Fact>]
let ``decoder reports each known source exactly once`` () =
    let enc, dec = roundTrip CodeField.Gf16 0.1 0.4 200 5
    let seen = Collections.Generic.HashSet<struct (uint64 * int)>()
    let mutable t = 0UL
    let mutable s = 0
    let mutable recovered = false
    while dec.TryTakeKnown(&t, &s, &recovered) do
        Assert.True(seen.Add(struct (t, s)), $"duplicate {t}:{s}")
        Assert.True(s < stepUsed t)
    for st in 0UL .. dec.Frontier - 1UL do
        for sl in 0 .. stepUsed st - 1 do
            Assert.Contains(struct (st, sl), seen)
    ignore enc

[<Fact>]
let ``BOOSTER closes a rank deficit`` () =
    let enc = filledEncoder 20
    let dec = Decoder(gseed, symbol, 1024, 1024)
    // Steps 0..9 arrive complete; step 10 loses slots 3, 7 and 9, and has no parity.
    for t in 0 .. 9 do
        let k = stepUsed (uint64 t)
        for s in 0 .. k - 1 do dec.OfferSource(uint64 t, s, k, enc.Source(uint64 t, s)) |> ignore
    for s in 0 .. 15 do
        if s <> 3 && s <> 7 && s <> 9 then dec.OfferSource(10UL, s, 16, enc.Source(10UL, s)) |> ignore
    Assert.Equal(10UL, dec.Frontier)
    Assert.Equal(3, dec.Deficit(10UL, 1))
    let out = Array.zeroCreate<byte> symbol
    let mutable seq = 0
    while dec.Frontier = 10UL && seq < 16 do
        Assert.True(enc.TryRenderBooster(10UL, 1, seq, Span out))
        dec.OfferBooster(10UL, 1, seq, ReadOnlySpan out) |> ignore
        seq <- seq + 1
    Assert.Equal(11UL, dec.Frontier)
    assertRecovered enc dec 11

[<Fact>]
let ``decoder never outputs an undetermined source`` () =
    // Two unknowns, one equation: neither source is determined.
    let enc = filledEncoder 2
    let dec = Decoder(gseed, symbol, 64, 64)
    for s in 0 .. 15 do
        if s > 1 then dec.OfferSource(0UL, s, 16, enc.Source(0UL, s)) |> ignore
    let out = Array.zeroCreate<byte> symbol
    Assert.True(enc.TryRenderParity(0UL, 0, CodeField.Gf16, true, Span out))
    Assert.Equal(EquationResult.Innovative, dec.OfferParity(0UL, 0, CodeField.Gf16, true, 16, ReadOnlySpan out))
    Assert.Equal(0UL, dec.Frontier)
    Assert.Equal(1, dec.Deficit(0UL, 1))
    Assert.Equal(EquationResult.Redundant, dec.OfferParity(0UL, 0, CodeField.Gf16, true, 16, ReadOnlySpan out))
    Assert.True(enc.TryRenderParity(0UL, 1, CodeField.Gf16, true, Span out))
    Assert.Equal(EquationResult.Innovative, dec.OfferParity(0UL, 1, CodeField.Gf16, true, 16, ReadOnlySpan out))
    Assert.Equal(1UL, dec.Frontier)
    assertRecovered enc dec 1
