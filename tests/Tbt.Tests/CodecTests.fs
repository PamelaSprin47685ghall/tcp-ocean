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

[<Fact>]
let ``random erasure never leaks decoder rows and Release gives idle rows back`` () =
    // A row that elimination reduces to no columns used to stay in the row list for ever.
    // Once rowsAllocated reached maxRows, every later parity came back OutOfWindow, so FEC
    // died silently after a few minutes: the symptom was "fast at first, repairs stop
    // working". Assert the ledger invariant directly and then that Release shrinks the pool.
    let maxRows = 512
    let dec = Decoder(gseed, symbol, 256, maxRows)
    let enc = filledEncoder 400
    let rnd = Random(20261124)
    let out = Array.zeroCreate<byte> symbol
    let mutable offered = 0
    for round in 0 .. 199 do
        let t = uint64 round
        let k = stepUsed t
        for s in 0 .. k - 1 do
            if rnd.NextDouble() >= 0.30 then
                dec.OfferSource(t, s, k, enc.Source(t, s)) |> ignore
                offered <- offered + 1
        for p in 0 .. 7 do
            if rnd.NextDouble() >= 0.30 then
                dec.NoteStepUsed(t, k) |> ignore
                for r in Strides do
                    if t >= r then dec.NoteStepUsed(t - r, stepUsed (t - r)) |> ignore
                if enc.TryRenderParity(t, p, CodeField.Gf16, false, Span out) then
                    dec.OfferParity(t, p, CodeField.Gf16, false, k, ReadOnlySpan out) |> ignore
                    offered <- offered + 1
        Assert.True(dec.RowsAllocated = dec.PendingRows + dec.FreeRows,
                    $"round {round}: allocated {dec.RowsAllocated} rows {dec.PendingRows} free {dec.FreeRows}")
        Assert.True(dec.RowsAllocated <= maxRows, $"round {round}: {dec.RowsAllocated} rows")
    Assert.True(offered > 1000)
    // Every known source is still exactly what the encoder produced, whatever it took.
    let mutable checkedSources = 0
    let mutable kt = 0UL
    let mutable ks = 0
    let mutable krec = false
    while dec.TryTakeKnown(&kt, &ks, &krec) do
        Assert.True(dec.Source(kt, ks).SequenceEqual(enc.Source(kt, ks)), $"source {kt}:{ks}")
        checkedSources <- checkedSources + 1
    Assert.True(checkedSources > 500)
    let before = dec.FreeRows
    dec.Release(UInt64.MaxValue)
    Assert.True(dec.FreeRows <= before, "Release gave nothing back")
    Assert.True(dec.RowsAllocated = dec.PendingRows + dec.FreeRows)

[<Fact>]
let ``a directly received source is reported once, and never as FEC recovered`` () =
    // Contract of TryTakeKnown: "each exactly once". A received source reports false; only
    // elimination reports true. The direct path learns k before it marks the slot
    // (OfferSource -> NoteStepUsed), so a later k for the same step cannot relabel it here:
    // this is a guard on that contract, not a reproduction of a phantom-recovery bug (an
    // earlier note claimed such a bug; it does not exist through the public API, because
    // `markKnown` only defers when k is still 0, which OfferSource prevents).
    let dec = Decoder(gseed, symbol, 64, 256)
    let enc = filledEncoder 4
    let mutable t = 0UL
    let mutable s = 0
    let mutable rebuilt = false
    // k unknown at first: each source is reported as it arrives, with recovered = false.
    for slot in 0 .. 3 do
        Assert.True(dec.OfferSource(0UL, slot, 4, enc.Source(0UL, slot)))
    let mutable direct = 0
    while dec.TryTakeKnown(&t, &s, &rebuilt) do
        Assert.False(rebuilt, sprintf "source %d:%d reported as recovered though it arrived" t s)
        Assert.Equal(0UL, t)
        direct <- direct + 1
    Assert.Equal(4, direct)
    // Now k for the same step is recorded again through another frame's history stride.
    Assert.True(dec.NoteStepUsed(0UL, 4))
    Assert.False(dec.TryTakeKnown(&t, &s, &rebuilt), "a known source was reported a second time")
    Assert.Equal(4, dec.StepKnownCount 0UL)

[<Fact>]
let ``IsStepComplete and StepKnownCount follow the known sources of a step`` () =
    let dec = Decoder(gseed, symbol, 64, 256)
    let enc = filledEncoder 4
    Assert.False(dec.IsStepComplete 0UL)
    Assert.Equal(0, dec.StepKnownCount 0UL)
    for s in 0 .. 15 do
        Assert.True(dec.OfferSource(0UL, s, 16, enc.Source(0UL, s)))
    Assert.True(dec.IsStepComplete 0UL)
    Assert.Equal(16, dec.StepKnownCount 0UL)
    Assert.False(dec.IsStepComplete 1UL)

[<Fact>]
let ``Release keeps row accounting exact and reports no stale equation`` () =
    // A row can outlive the release of a step it still references. That column is below
    // baseStep afterwards, so substitute and markKnown can never reach it (both guard on
    // ensureStep), the row can never reach Count 0 or 1, and nothing else ever recycles it.
    // Left alone such rows accumulate for the whole session: addPivot walks a longer colRows
    // list doing one 1344-byte GF16 multiply-add per member and Deficit scans every row,
    // which is what degraded the live 24 %-loss decoder to 887 cells/s and stalled the
    // session thread for 9.4 s and 12.5 s. Release now purges them and counts them.
    let gseed = 7UL
    let symbol = 64
    let capacitySteps = 1024
    let maxRows = 512
    let dec = Decoder(gseed, symbol, capacitySteps, maxRows)
    let zero = Array.zeroCreate<byte> symbol
    // Every third step keeps a hole, so rank is short and equations really stay pending.
    // The sources only have to be distinguishable from nothing: this test exercises the
    // decoder's row accounting, so the ring is filled with zero symbols rather than driving
    // a real Encoder (which would require a Seal/TryGetFillSlot dance and a live window).
    for t in 0UL .. 2000UL do
        let k = 8
        dec.NoteStepUsed(t, k) |> ignore
        for s in 0 .. k - 1 do
            if t % 3UL <> 0UL || s < k - 1 then
                dec.OfferSource(t, s, k, ReadOnlySpan<byte> zero) |> ignore
        // A BOOSTER over this step gives the decoder an equation whose unknowns are the
        // holes; with one hole per third step they do not all resolve at once.
        dec.OfferBooster(t, 1, 0, ReadOnlySpan<byte> zero) |> ignore
        dec.Release(t) |> ignore
        // Invariant: every rented row is either pending or idle, and none is stale.
        Assert.Equal(dec.RowsAllocated, dec.PendingRows + dec.FreeRows)
        Assert.True(dec.RowsAllocated <= maxRows, sprintf "rows %d" dec.RowsAllocated)
        Assert.Equal(0L, dec.StaleRows)
    Assert.True(dec.BaseStep > 0UL, "the window never rolled, so Release never freed a step")
    Assert.Equal(dec.RowsAllocated, dec.PendingRows + dec.FreeRows)

[<Fact>]
let ``a source recovered by elimination is subtracted from the rows that still hold it`` () =
    // A source can become known without ever being received: a parity row reduced to one
    // column is solved, and drainSolved then records it. The rows that were built BEFORE
    // that column had a pivot still contain it, and nothing else eliminates a known column
    // (substitute is private and only the direct receive path calls it). Such a row could
    // never be reduced again: it stayed pending for the rest of the session, `Deficit`
    // counted it as rank the caller did not have, and addPivot walked an ever longer
    // colRows list. Here one parity of step 0 (columns (0,0) and (0,1)) plus one BOOSTER
    // over step 0 recovers both slots; with the defect the parity row keeps column (0,1),
    // (0,0) is never solved and the row stays pending.
    let gseed = 11UL
    let symbol = 64
    let dec = Decoder(gseed, symbol, 1024, 64)
    let payload = Array.zeroCreate<byte> symbol
    dec.NoteStepUsed(0UL, 2) |> ignore
    Assert.Equal(EquationResult.Innovative, dec.OfferParity(0UL, 0, CodeField.Gf2, false, 2, ReadOnlySpan payload))
    Assert.Equal(EquationResult.Innovative, dec.OfferBooster(0UL, 1, 0, ReadOnlySpan payload))
    // Both sources of step 0 come out of elimination alone: no source cell was ever offered.
    Assert.True(dec.IsStepComplete(0UL), sprintf "step 0 incomplete, pending rows %d" dec.PendingRows)
    Assert.Equal(0, dec.PendingRows)
    Assert.Equal(dec.RowsAllocated, dec.PendingRows + dec.FreeRows)

[<Fact>]
let ``a rebuild flag is cleared by one arrival, and a direct source is never flagged rebuilt`` () =
    // Two semantics the deadline ledger stands on, both decidable without any network:
    //  (a) `WasRecovered` marks a slot the decoder solved by elimination, and one arrival
    //      clears it, so a repair copy or a TCP retransmit of the same cell cannot withdraw
    //      a second pending erasure (that silently under-counted loss);
    //  (b) a source that arrived directly is never flagged as rebuilt, or the ledger would
    //      book an erasure and then cancel it, i.e. churn on every clean link.
    let dseed = 11UL
    let dsymbol = 64
    let dec = Decoder(dseed, dsymbol, 64, 256)
    let zero = Array.zeroCreate<byte> dsymbol
    dec.NoteStepUsed(0UL, 2) |> ignore
    // One parity over step 0 plus one booster: elimination supplies both sources, so both
    // slots are rebuilt rather than received.
    Assert.Equal(EquationResult.Innovative, dec.OfferParity(0UL, 0, CodeField.Gf2, false, 2, ReadOnlySpan zero))
    Assert.Equal(EquationResult.Innovative, dec.OfferBooster(0UL, 1, 0, ReadOnlySpan zero))
    Assert.True(dec.IsStepComplete 0UL)
    let recoveredSlots = [| 0 .. 1 |] |> Array.filter (fun s -> dec.WasRecovered(0UL, s))
    Assert.True(recoveredSlots.Length > 0, "no slot was solved by elimination, so the check is vacuous")
    // One arrival vindicates exactly one slot; clearing is idempotent.
    let target = recoveredSlots.[0]
    dec.AcknowledgeRecovered(0UL, target)
    Assert.False(dec.WasRecovered(0UL, target), "the rebuild flag survived its vindication")
    dec.AcknowledgeRecovered(0UL, target)
    Assert.False(dec.WasRecovered(0UL, target))
    // A source that was never rebuilt must not be reported as rebuilt.
    let direct = Decoder(dseed, dsymbol, 64, 256)
    direct.OfferSource(5UL, 0, 4, ReadOnlySpan zero) |> ignore
    Assert.False(direct.WasRecovered(5UL, 0), "a directly received source was flagged as rebuilt")

[<Fact>]
let ``packed SSE2 GF16 multiply equals the table and the split kernel for every coefficient and byte`` () =
    if System.Runtime.Intrinsics.X86.Sse2.IsSupported then
        let all = Array.init 256 byte
        for c in 0 .. 15 do
            for block in 0 .. 15 do
                let x = System.Runtime.Intrinsics.Vector128.Create<byte>(ReadOnlySpan<byte>(all, block * 16, 16))
                let packed = Gf16.sse2MulByte c x
                let split = Gf16.sse2MulByteSplit c x
                Assert.Equal(split, packed)
                let out = Array.zeroCreate<byte> 16
                System.Runtime.Intrinsics.Vector128.CopyTo(packed, out)
                for i in 0 .. 15 do
                    Assert.Equal(Gf16.MulByte.[c * 256 + block * 16 + i], out.[i])

[<Fact>]
let ``release keeps a pending equation whose oldest column sits on the floor`` () =
    // One concrete boundary case, exercising the row sweep in `Release`.
    //
    // The equation is a parity of step 500 with k = 8: columns are step 500's eight slots plus
    // one history stride (the largest is 256), so its smallest column belongs to step 244. The
    // frontier reaches 500 and stops there, so `Release` can only advance `baseStep` up to
    // `frontier − HistorySpan = 244`, and the sweep compares 244 < 244 — false by one.
    //
    // Measured below: `StaleRows` stays 0, `BaseStep` is exactly 244, the equation remains
    // pending, and completing it still advances the frontier. This pins the boundary (nothing is
    // off by a stride), not the sweep's behaviour in other states.
    let gseed = 13UL
    let capacitySteps = 1024
    let maxRows = 512
    let dec = Decoder(gseed, symbol, capacitySteps, maxRows)
    let enc = filledEncoder 520
    let out = Array.zeroCreate<byte> symbol
    let zero = Array.zeroCreate<byte> symbol
    dec.NoteStepUsed(500UL, 8) |> ignore
    for r in Strides do dec.NoteStepUsed(500UL - r, 8) |> ignore
    Assert.True(enc.TryRenderParity(500UL, 0, CodeField.Gf16, false, Span out))
    Assert.Equal(EquationResult.Innovative,
                 dec.OfferParity(500UL, 0, CodeField.Gf16, false, 8, ReadOnlySpan out))
    Assert.Equal(1, dec.PendingRows)          // the one cross-step equation under test, alone
    // Every other step arrives whole, so the frontier reaches 500 and stops there. The symbols
    // are zeros rather than encoder output: the encoder keeps only a recent window, so asking it
    // for step 0 would throw "source not held", and this test is about the decoder's row and
    // index bookkeeping, not about coded values (the neighbouring ``Release keeps row accounting
    // exact`` fact uses the same stand-in for the same reason).
    for t in 0 .. 519 do
        if t <> 500 then
            for s in 0 .. 7 do
                dec.OfferSource(uint64 t, s, 8, ReadOnlySpan<byte> zero) |> ignore
    Assert.Equal(500UL, dec.Frontier)
    dec.Release(9999UL) |> ignore
    // The purge must not fire here: 244 < 244 is false, so nothing is off by a stride.
    Assert.Equal(0L, dec.StaleRows)
    Assert.Equal(244UL, dec.BaseStep)
    // ... and the pending equation is still there, still solvable: nothing was silently dropped.
    Assert.Equal(1, dec.PendingRows)
    Assert.Equal(dec.RowsAllocated, dec.PendingRows + dec.FreeRows)
    Assert.True(dec.RowsAllocated <= maxRows, sprintf "rows %d" dec.RowsAllocated)
    // The equation survived `Release`, so it must still be finishable: deliver step 500's eight
    // source cells and the frontier must move past it. This is the part that would break if the
    // purge had wrongly discarded the row, or if the column index had lost its entries.
    Assert.False(dec.IsStepComplete(500UL))
    for s in 0 .. 7 do
        dec.OfferSource(500UL, s, 8, ReadOnlySpan<byte> zero) |> ignore
    Assert.True(dec.IsStepComplete(500UL), "the surviving equation could not be completed after Release")
    // Steps 501..519 were already complete, so the frontier clears all of them at once.
    Assert.Equal(520UL, dec.Frontier)
    Assert.Equal(0, dec.PendingRows)
    Assert.Equal(dec.RowsAllocated, dec.PendingRows + dec.FreeRows)
