module Tbt.Tests.WindowCodecAdaptersTests

open System
open Xunit
open Tbt.Codec
open Tbt.Wire
open Tbt.Session

let private symbolSize = 32
let private budget = 16L <<< 20

let private expected (step: uint64) (slot: int) =
    Array.init symbolSize (fun i -> byte ((int (step % 251UL) * 16 + slot * 3 + i) &&& 255))

let private fillStep (enc: WindowStepEncoder) (step: uint64) (used: int) =
    for slot = 0 to used - 1 do
        let mutable span = Span<byte>.Empty
        Assert.True(enc.TryGetFillSlot(slot, &span))
        let bytes = expected step slot
        Assert.Equal(symbolSize, span.Length)
        for i = 0 to symbolSize - 1 do
            span.[i] <- bytes.[i]
    Assert.Equal(step, enc.Seal used)

let private dropped (step: uint64) (slot: int) = (int step * 16 + slot) % 7 = 0

/// One full step, one partially flushed step, one step whose DATA never arrives, one tail.
let private buildStream () =
    let enc = WindowStepEncoder(symbolSize, 16)
    let shapes = [| (0UL, 16); (1UL, 5); (2UL, 16); (3UL, 1) |]
    let filled = ResizeArray<uint64 * int>()
    for (step, used) in shapes do
        fillStep enc step used
        for slot = 0 to used - 1 do
            filled.Add((step, slot))
    enc, filled

let private makeDecoder () = WindowSnapshotDecoder(symbolSize, 8, budget)

let private sendData (enc: WindowStepEncoder) (dec: WindowSnapshotDecoder) (step: uint64) (slot: int) =
    dec.OfferSource(step, slot, enc.StepUsed step, enc.Source(step, slot)) |> ignore

/// Render up to `count` window parities for the window holding `origin`, feeding the decoder.
let private sendParities (enc: WindowStepEncoder) (dec: WindowSnapshotDecoder) (origin: uint64) (count: int) =
    let mutable sent = 0
    let mutable p = 0
    while p < count do
        let dst = Array.zeroCreate<byte> symbolSize
        let mutable meta = Unchecked.defaultof<Meta>
        if (enc :> IWindowEncoderMeta).TryRenderWindowParity(origin, p, Span<byte>(dst), &meta) then
            dec.OfferWindowParity(meta, ReadOnlySpan<byte>(dst)) |> ignore
            (enc :> IWindowEncoderMeta).CommitParityIndex(origin / 4UL)
            sent <- sent + 1
        else
            p <- count
        p <- p + 1
    sent

let private countRecovered (dec: WindowSnapshotDecoder) (filled: ResizeArray<uint64 * int>) =
    filled |> Seq.filter (fun (s, i) -> dec.WasRecovered(s, i)) |> Seq.length

let private drainAndCheck (dec: WindowSnapshotDecoder) (filled: ResizeArray<uint64 * int>) =
    let seen = System.Collections.Generic.HashSet<struct (uint64 * int)>()
    let mutable step = 0UL
    let mutable slot = 0
    let mutable recovered = false
    let mutable guard = 0
    while dec.TryTakeKnown(&step, &slot, &recovered) && guard < 20_000 do
        guard <- guard + 1
        Assert.True(seen.Add(struct (step, slot)), sprintf "duplicate delivery %d/%d" step slot)
        Assert.Equal<byte[]>(expected step slot, dec.Source(step, slot).ToArray())
        dec.FinishKnown step
    for (step, slot) in filled do
        Assert.True(seen.Contains(struct (step, slot)), sprintf "missing source %d/%d" step slot)

[<Fact>]
let ``decoder reports authenticated completion and unknown-step deficits without wrapping`` () =
    let enc, _ = buildStream ()
    let dec = makeDecoder ()
    Assert.False(dec.IsStepComplete 0UL)
    Assert.Equal(16, dec.Deficit(0UL, 1))
    sendData enc dec 0UL 0
    Assert.Equal(1, dec.StepKnownCount 0UL)
    Assert.Equal(15, dec.Deficit(0UL, 1))
    for slot = 1 to 15 do sendData enc dec 0UL slot
    Assert.True(dec.IsStepComplete 0UL)
    Assert.Equal(16, dec.StepKnownCount 0UL)
    let struct (a, b, c, d) = dec.BuildDecodedBitmap 0UL
    Assert.Equal(1UL, a)
    Assert.Equal(0UL, b ||| c ||| d)
    let struct (x, y, z, w) = dec.BuildDecodedBitmap UInt64.MaxValue
    Assert.Equal(0UL, x ||| y ||| z ||| w)
    Assert.Equal(16, dec.Deficit(UInt64.MaxValue, 2))
    Assert.Throws<ArgumentException>(fun () -> dec.Deficit(0UL, 257) |> ignore) |> ignore

[<Fact>]
let ``window snapshot interface preserves authenticated parity outcomes`` () =
    let enc, _ = buildStream ()
    let dec = makeDecoder ()
    let snapshot = dec :> IWindowDecoderSnapshot
    Assert.True(snapshot.SafeGrant > 0us)
    let dst = Array.zeroCreate<byte> symbolSize
    let mutable meta = Unchecked.defaultof<Meta>
    Assert.True((enc :> IWindowEncoderMeta).TryRenderWindowParity(0UL, 0, Span dst, &meta))
    Assert.Equal(EquationResult.Innovative, snapshot.OfferWindowParity(meta, ReadOnlySpan dst))
    Assert.Equal(EquationResult.Redundant, snapshot.OfferWindowParity(meta, ReadOnlySpan dst))
    let malformed = { meta with KHist = meta.KHist ||| 0x800000u }
    Assert.Equal(EquationResult.Useless, snapshot.OfferWindowParity(malformed, ReadOnlySpan dst))
    Assert.True(dec.Conflicts > 0L)

[<Fact>]
let ``parity index is committed only after a successful send`` () =
    let enc, _ = buildStream ()
    let api = enc :> IWindowEncoderMeta
    let bytes = Array.zeroCreate<byte> symbolSize
    let retry = Array.zeroCreate<byte> symbolSize
    let mutable meta = Unchecked.defaultof<Meta>
    let mutable again = Unchecked.defaultof<Meta>
    Assert.Equal(0, api.NextParityIndex 0UL)
    Assert.True(api.TryRenderWindowParity(0UL, 0, Span bytes, &meta))
    Assert.Equal(0, api.NextParityIndex 0UL)
    Assert.True(api.TryRenderWindowParity(0UL, 0, Span retry, &again))
    Assert.Equal<byte[]>(bytes, retry)
    Assert.Equal(meta, again)
    api.CommitParityIndex 0UL
    for p = 1 to 47 do
        Assert.Equal(p, api.NextParityIndex 0UL)
        Assert.True(api.TryRenderWindowParity(0UL, p, Span bytes, &meta))
        api.CommitParityIndex 0UL
    Assert.Equal(-1, api.NextParityIndex 0UL)
    enc.Release 4UL
    Assert.Equal(-1, api.NextParityIndex 0UL)

[<Fact>]
let ``maximum reservation fits the matrix payload budget`` () =
    let dec = WindowSnapshotDecoder(1344, 160, budget)
    Assert.True(dec.ReservedBytes > 0L)
    Assert.True(dec.ReservedBytes <= budget)

[<Fact>]
let ``parity recovers symbols and raw DATA closes the rest byte-exactly`` () =
    let enc, filled = buildStream ()
    let dec = makeDecoder ()
    dec.NextGrant() |> ignore

    for (step, slot) in Seq.sortByDescending (fun (s, i) -> (s, i)) filled do
        if step <> 2UL && not (dropped step slot) then sendData enc dec step slot

    // Parity budget is enough to make progress but is not assumed to close every hole.
    let sent = sendParities enc dec 0UL 48
    Assert.True(sent > 0, "window parity must be renderable once the window is sealed")
    let recoveredByParity = countRecovered dec filled
    Assert.True(recoveredByParity > 0, "parity must actually reconstruct lost symbols")

    // The protocol's fallback for residual rank deficiency: resend the raw sources.
    for (step, slot) in filled do
        if step = 2UL || dropped step slot then sendData enc dec step slot

    drainAndCheck dec filled
    Assert.True(dec.ReservedBytes <= budget)
    Assert.Equal(0L, dec.Conflicts)
    dec.Advance(1_000_000L)

[<Fact>]
let ``exhausted parity budget falls back to retransmitted DATA`` () =
    let enc, filled = buildStream ()
    let dec = makeDecoder ()
    dec.NextGrant() |> ignore
    for (step, slot) in filled do
        if not (step = 1UL && slot = 0) then sendData enc dec step slot
    Assert.Equal(48, sendParities enc dec 0UL 48)
    // Index 48 has no renderable snapshot: the protocol must retransmit the source.
    let dst = Array.zeroCreate<byte> symbolSize
    let mutable meta = Unchecked.defaultof<Meta>
    Assert.False((enc :> IWindowEncoderMeta).TryRenderWindowParity(0UL, 48, Span<byte>(dst), &meta))
    sendData enc dec 1UL 0
    drainAndCheck dec filled

[<Fact>]
let ``durable flow references pin custody and grants never retract`` () =
    let enc, filled = buildStream ()
    let dec = makeDecoder ()
    dec.NextGrant() |> ignore
    for (step, slot) in filled do
        sendData enc dec step slot

    let publishedBefore = dec.PublishedLimit
    Assert.True(publishedBefore > 0UL, "initial grant must be published before data")
    // Register the durable reference the flow table would hold for this step.
    dec.StepUndelivered(0UL, 1)
    let seen = System.Collections.Generic.HashSet<struct (uint64 * int)>()
    let mutable step = 0UL
    let mutable slot = 0
    let mutable recovered = false
    let take () =
        while dec.TryTakeKnown(&step, &slot, &recovered) do
            seen.Add(struct (step, slot)) |> ignore
            dec.FinishKnown step
    take ()
    dec.Advance(2_000_000L)
    let pinned = dec.AllocatedBytes
    Assert.True(pinned > 0L, "a pinned window must keep its matrix storage")
    Assert.True(dec.PublishedLimit >= publishedBefore, "published credit must never retract")

    dec.Advance(3_000_000L)
    Assert.True(dec.AllocatedBytes > 0L, "custody cannot release while a reference is outstanding")

    dec.StepUndelivered(0UL, -1)
    // Consume the queue so no temporary reference remains, then release.
    take ()
    Assert.Equal(filled.Count, seen.Count)
    dec.Advance(4_000_000L)
    Assert.Equal(0L, dec.AllocatedBytes)
    Assert.True(dec.PublishedLimit >= publishedBefore)

[<Fact>]
let ``finishing without a borrowed source cannot consume custody references`` () =
    let enc, filled = buildStream ()
    let dec = makeDecoder ()
    for (step, slot) in filled do sendData enc dec step slot
    Assert.Throws<InvalidOperationException>(fun () -> dec.FinishKnown 0UL) |> ignore
    drainAndCheck dec filled
    Assert.Throws<InvalidOperationException>(fun () -> dec.FinishKnown 0UL) |> ignore
    dec.Advance(1_000_000L)
    Assert.Equal(0L, dec.AllocatedBytes)

[<Fact>]
let ``tampered parity metadata is rejected rather than accepted`` () =
    let enc, filled = buildStream ()
    let dec = makeDecoder ()
    dec.NextGrant() |> ignore
    for (step, slot) in filled do
        if not (dropped step slot) then sendData enc dec step slot
    let dst = Array.zeroCreate<byte> symbolSize
    let mutable meta = Unchecked.defaultof<Meta>
    Assert.True((enc :> IWindowEncoderMeta).TryRenderWindowParity(0UL, 0, Span<byte>(dst), &meta))
    let tampered = { meta with KHist = meta.KHist ^^^ 0x000Fu }
    let outcome = dec.OfferWindowParity(tampered, ReadOnlySpan<byte>(dst))
    Assert.NotEqual<WindowCodec.AddOutcome>(WindowCodec.AddOutcome.Innovative, outcome)
    Assert.True(dec.Conflicts >= 1L)

[<Fact>]
let ``a released window never reopens for a stale frame`` () =
    let enc, filled = buildStream ()
    let dec = makeDecoder ()
    dec.NextGrant() |> ignore
    for (step, slot) in filled do
        sendData enc dec step slot
    drainAndCheck dec filled
    dec.Advance(5_000_000L)
    dec.Advance(6_000_000L)
    let late = dec.OfferSource(0UL, 0, enc.StepUsed 0UL, enc.Source(0UL, 0))
    Assert.NotEqual<WindowCodec.AddOutcome>(WindowCodec.AddOutcome.Innovative, late)
