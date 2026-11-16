[<Xunit.Collection("BridgeTests serial")>]
module Tbt.Tests.RepairRangeHistoryTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Wire
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Tests.SessionTests

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``delayed same range feedback retains its wait across a newer span report`` interleave =
    let clock = ManualClock()
    let entropy = SeededEntropy 733
    let offer = ClientOffer(psk,Guid(Array.create 16 7uy),entropy,uint64 unixMs,1448,256)
    let accepted = ServerOffer([|psk|],entropy,4096us,256).TryAccept(ReadOnlySpan offer.Frame,unixMs,1448).Value
    let struct(cp,_) = offer.TryAccept(ReadOnlySpan accepted.Reply).Value
    let c = Session(Role.Client,cp,RecordingPort(),clock,SessionConfig.defaults)
    let s = Session(Role.Server,accepted.Params,RecordingPort(),clock,SessionConfig.defaults)
    Assert.False(c.Tx.NewRepair)
    let buffer = Array.zeroCreate<byte> c.FrameLength
    let readMeta bytes =
        let mutable meta = Unchecked.defaultof<Meta>
        Assert.True(Meta.tryRead (ReadOnlySpan(bytes,Layout.MetaOffset,Layout.MetaLength)) &meta)
        meta
    let deliver (dst:Session) bytes =
        let copy = Array.copy bytes
        Assert.NotEqual(UInt64.MaxValue,dst.OnFrame(0us,Span copy))
        copy
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let sendOneOfStep expected =
        for i in 0..15 do
            let mutable app = Span<byte>.Empty
            Assert.True(c.TryBeginSource(&app))
            app.[0] <- byte(i+1)
            c.CommitSource(slot,1,false)
        Assert.True(c.TryProduce(0us,Span buffer))
        let received = deliver s buffer
        let meta = readMeta received
        Assert.Equal(CellType.Data,meta.Type)
        Assert.Equal(expected,meta.StepId)
        Assert.Equal(0uy,meta.Slot)
        // Model erasure of the other original DATA/PARITY frames, not a fabricated
        // receiver deficit or a direct OnNack call.
        let mutable n = 0
        while c.TryProduce(0us,Span buffer) do
            n <- n+1
            Assert.True(n<100)
    let feedback () =
        clock.Now <- clock.Now + 20_000_000L
        s.Tick()
        Assert.True(s.TryProduce(0us,Span buffer))
        Array.copy buffer
    let receiveFeedback expectedSpan (bytes:byte[]) =
        let counter = c.Opener.PeekCounter(ReadOnlySpan bytes)
        let received = deliver c bytes
        Assert.Equal(CellType.WinAck,(readMeta received).Type)
        let mutable head = Unchecked.defaultof<WinAckHead>
        Assert.True(WinAck.tryReadHead (ReadOnlySpan(received,Layout.SymbolOffset,c.SymbolSize)) &head)
        Assert.Equal(0UL,head.TLo)
        Assert.Equal(expectedSpan,head.Span)
        Assert.True(head.Deficit>0us && head.NackFlags &&& 1uy <> 0uy)
        Assert.Equal(0UL,c.Tx.PeerAck)
        counter,head
    let drainRepairs () =
        let result = ResizeArray<struct(Meta*byte[])>()
        while c.Tx.RepairWaiting do
            Assert.True(c.Tx.TryRenderNext(Span buffer,0us,0UL,4096us,0us))
            let received = deliver s buffer
            let meta = readMeta received
            Assert.Equal(CellType.Booster,meta.Type)
            result.Add(struct(meta,received.[Layout.SymbolOffset .. Layout.SymbolOffset+c.SymbolSize-1]))
            Assert.True(result.Count<100)
        result.ToArray()
    sendOneOfStep 0UL
    Assert.Equal(1UL,s.Rx.Decoder.SeenEnd)
    let a1 = feedback()
    let a2 = feedback() // a distinct, genuinely generated report of the same state
    sendOneOfStep 1UL
    Assert.Equal(2UL,s.Rx.Decoder.SeenEnd)
    let b = feedback()
    // All feedback already exists before repair delivery can advance the decoder.
    // A2 is delayed, not replayed; B's newer cell_seq arrives before it.
    let seqA1,hA1 = receiveFeedback 1uy a1
    let first = drainRepairs()
    Assert.NotEmpty first
    let sentAt = clock.Now
    let wait = c.Tx.RepairWaitNs
    if interleave then
        let seqB,_ = receiveFeedback 2uy b
        Assert.True(seqB>seqA1)
        Assert.NotEmpty(drainRepairs())
    let beforeDrops = c.Opener.ReplayDrops
    let seqA2,hA2 = receiveFeedback 1uy a2
    Assert.True(seqA2>seqA1)
    if interleave then Assert.True(seqA2<c.Opener.RightEdge)
    Assert.Equal(beforeDrops,c.Opener.ReplayDrops)
    Assert.Equal(hA1.Deficit,hA2.Deficit)
    Assert.Equal(hA1.THi,hA2.THi)
    Assert.Equal((if interleave then 1UL else 0UL),c.Tx.PeerSeenHi)
    Assert.True(float(clock.Now-sentAt)<wait)
    let repeated = drainRepairs()
    let overlaps =
        repeated |> Array.sumBy(fun struct(m,payload) ->
            if first |> Array.exists(fun struct(old,bytes) -> old.StepId=m.StepId && old.StepUsed=m.StepUsed && old.Slot=m.Slot && bytes=payload)
            then 1 else 0)
    Assert.True(repeated.Length=0,
        sprintf "interleave=%b accepted seq A1=%d A2=%d; peerAck=%d; elapsed=%dns wait=%gns; first=%d repeated=%d identical seed-domain+payload=%d" interleave seqA1 seqA2 c.Tx.PeerAck (clock.Now-sentAt) wait first.Length repeated.Length overlaps)

/// Direct API robustness fixtures below are not claimed to be naturally generated
/// receiver feedback. They isolate range history without changing private state.
type private ApiHistory(capacity) =
    let pair = Pair({SessionConfig.defaults with WindowSteps=capacity},1,0.0,937)
    let c,s = pair.Client,pair.Server
    let frame = Array.zeroCreate<byte> c.FrameLength
    let mutable slot = -1
    do
        Assert.True(c.TryOpenFlow(&slot))
        c.Tx.InitialGrant 10000UL
    let drain () =
        let items = ResizeArray<Meta>()
        while c.Tx.TryRenderNext(Span frame,0us,0UL,4096us,0us) do
            Assert.NotEqual(UInt64.MaxValue,s.OnFrame(0us,Span frame))
            let mutable m = Unchecked.defaultof<Meta>
            Assert.True(Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &m)
            items.Add m
        items.ToArray()
    member _.Tx = c.Tx
    member _.Clock = pair.Clock
    member _.AddStep() =
        let mutable app = Span<byte>.Empty
        Assert.True(c.TryBeginSource(&app))
        app.[0] <- 11uy
        c.CommitSource(slot,1,false)
        c.Flush()
        drain() |> ignore
    member _.Nack(lo,span,deficit) =
        c.Tx.OnNack(lo,span,deficit)
        drain()
    member _.Expire() = pair.Clock.Now <- pair.Clock.Now + int64(c.Tx.RepairWaitNs) + 1L

let private seeds expectedStart count (cells:Meta[]) =
    Assert.Equal(count,cells.Length)
    for i in 0..cells.Length-1 do
        Assert.Equal(CellType.Booster,cells.[i].Type)
        Assert.Equal(byte(expectedStart+i),cells.[i].Slot)

[<Fact>]
let ``API range history preserves expiry growth and independent span seed progress`` () =
    let p = ApiHistory(512)
    for _ in 1..3 do p.AddStep()
    let first = p.Nack(0UL,1,1)
    seeds 0 4 first
    seeds 0 4 (p.Nack(0UL,2,1))
    Assert.Empty(p.Nack(0UL,1,1))
    // Existing deficit-growth exception remains eligible before elapsed wait.
    seeds 4 5 (p.Nack(0UL,1,2))
    Assert.Empty(p.Nack(0UL,1,1))
    seeds 0 4 (p.Nack(1UL,1,1)) // distinct live Lo is immediately independent
    p.Expire()
    seeds 9 4 (p.Nack(0UL,1,1))
    p.Expire()
    seeds 4 4 (p.Nack(0UL,2,1))
    Assert.Equal(2L,p.Tx.RepairRepeats)

[<Fact>]
let ``API interleaved ranges retain their own 256 seeds then use DATA`` () =
    let p = ApiHistory(512)
    p.AddStep(); p.AddStep()
    for round in 0..3 do
        if round>0 then p.Expire()
        seeds (round*64) 64 (p.Nack(0UL,1,60))
        seeds (round*64) 64 (p.Nack(0UL,2,60))
    p.Expire()
    let fallback = p.Nack(0UL,1,60)
    Assert.Single(fallback) |> ignore
    Assert.Equal(CellType.Data,fallback.[0].Type)
    Assert.Equal(0UL,fallback.[0].StepId)

[<Fact>]
let ``API oversized round fallback does not consume remaining seeds`` () =
    let p = ApiHistory(512)
    p.AddStep(); p.AddStep()
    for round in 0..4 do
        if round>0 then p.Expire()
        seeds (round*50) 50 (p.Nack(0UL,1,45))
    // Larger deficit is the existing immediate exception;64 cannot fit in6.
    let fallback = p.Nack(0UL,1,60)
    Assert.Single(fallback) |> ignore
    Assert.Equal(CellType.Data,fallback.[0].Type)
    seeds 0 4 (p.Nack(0UL,2,1))
    p.Expire()
    seeds 250 4 (p.Nack(0UL,1,1))

[<Fact>]
let ``API custody frontier ignores old range and ring tag does not alias reused Lo`` () =
    let p = ApiHistory(512)
    p.AddStep(); p.AddStep()
    seeds 0 4 (p.Nack(0UL,1,1))
    p.Tx.OnPeerAck(1UL,4096us)
    Assert.Empty(p.Nack(0UL,1,60))
    seeds 0 4 (p.Nack(1UL,1,1))
    p.Tx.OnPeerAck(p.Tx.Head,4096us)
    while p.Tx.Head<=512UL do
        p.AddStep()
        if p.Tx.Head<=512UL then p.Tx.OnPeerAck(p.Tx.Head,4096us)
    Assert.Equal(512UL,p.Tx.PeerAck)
    Assert.Empty(p.Nack(0UL,1,60)) // retained history cannot resurrect old work
    seeds 0 4 (p.Nack(512UL,1,1)) // same ring index, different absolute Lo
    Assert.Empty(p.Nack(512UL,1,1))

[<Fact>]
let ``API invalid range cannot index history or disturb a valid range`` () =
    let p = ApiHistory(512)
    p.AddStep(); p.AddStep()
    seeds 0 4 (p.Nack(0UL,1,1))
    for span in [Int32.MinValue;-1;0] do Assert.Empty(p.Nack(0UL,span,1))
    Assert.Empty(p.Nack(UInt64.MaxValue,4,1))
    Assert.Empty(p.Nack(p.Tx.Head,4,1))
    Assert.Empty(p.Nack(0UL,1,0))
    Assert.Empty(p.Nack(0UL,1,1))
    p.Expire()
    seeds 4 4 (p.Nack(0UL,1,1))

[<Fact>]
let ``API normalization shares keys for max span and head clipping`` () =
    let p = ApiHistory(512)
    for _ in 1..5 do p.AddStep()
    let wide = p.Nack(0UL,100,1)
    seeds 0 4 wide
    Assert.All(wide,fun m -> Assert.Equal(4uy,m.StepUsed))
    Assert.Empty(p.Nack(0UL,4,1))
    let clipped = p.Nack(3UL,4,1)
    seeds 0 4 clipped
    Assert.All(clipped,fun m -> Assert.Equal(2uy,m.StepUsed))
    Assert.Empty(p.Nack(3UL,2,1))
    Assert.Empty(p.Nack(3UL,3,1))
    p.Expire()
    seeds 4 4 (p.Nack(0UL,4,1))
    seeds 4 4 (p.Nack(3UL,2,1))

[<Fact>]
let ``API same range is blocked one nanosecond before wait and allowed exactly at wait`` () =
    let p = ApiHistory(512)
    p.AddStep();p.AddStep()
    p.Clock.Now <- p.Clock.Now + 400_000_000L
    p.Tx.OnPeerAck(1UL,4096us)
    Assert.Equal(400_000_000L,p.Tx.MinRttNs)
    Assert.Equal(420_000_000.0,p.Tx.RepairWaitNs)
    seeds 0 4 (p.Nack(1UL,1,1))
    p.Clock.Now <- p.Clock.Now + 419_999_999L
    Assert.Empty(p.Nack(1UL,1,1))
    p.Clock.Now <- p.Clock.Now + 1L
    seeds 4 4 (p.Nack(1UL,1,1))

// Test-only inspection; no production observer API or mutation of private state.
let private historyArray (tx:TxHalf) =
    let flags = System.Reflection.BindingFlags.Instance ||| System.Reflection.BindingFlags.NonPublic
    let field = typeof<TxHalf>.GetField("repairHistory",flags) |> nonNull
    field.GetValue(tx) |> nonNull :?> Array

[<Fact>]
let ``history allocates only on first valid default repair and reuses its fixed table`` () =
    let p = ApiHistory(512)
    p.Clock.Now <- 0L // Lo=0/time=0 must not collide with the empty entry sentinel.
    p.AddStep()
    Assert.Equal(0,(historyArray p.Tx).Length)
    for span in [Int32.MinValue;-1;0] do Assert.Empty(p.Nack(0UL,span,1))
    Assert.Empty(p.Nack(UInt64.MaxValue,4,1))
    Assert.Empty(p.Nack(p.Tx.Head,4,1))
    Assert.Empty(p.Nack(0UL,1,0))
    Assert.Equal(0,(historyArray p.Tx).Length)
    p.Tx.NewRepair <- true
    p.Nack(0UL,1,1) |> ignore
    Assert.Equal(0,(historyArray p.Tx).Length)
    p.Tx.NewRepair <- false
    seeds 0 4 (p.Nack(0UL,1,1))
    let table = historyArray p.Tx
    Assert.Equal(512*4,table.Length)
    p.Expire()
    seeds 4 4 (p.Nack(0UL,1,1))
    Assert.Same(table,historyArray p.Tx)

[<Fact>]
let ``mode changes preserve default wait growth and seed exhaustion history`` () =
    let p = ApiHistory(512)
    p.AddStep()
    seeds 0 4 (p.Nack(0UL,1,1))
    let table = historyArray p.Tx
    let switch () =
        p.Tx.NewRepair <- true
        p.Nack(0UL,1,1) |> ignore
        p.Tx.NewRepair <- false
        Assert.Same(table,historyArray p.Tx)
    switch()
    Assert.Empty(p.Nack(0UL,1,1))
    seeds 4 5 (p.Nack(0UL,1,2))
    switch()
    Assert.Empty(p.Nack(0UL,1,2))
    p.Expire()
    seeds 9 4 (p.Nack(0UL,1,1))
    for round in 0..2 do
        p.Expire()
        seeds (13+round*64) 64 (p.Nack(0UL,1,60))
        switch()
    p.Expire()
    seeds 205 50 (p.Nack(0UL,1,45))
    switch()
    p.Expire()
    let fallback = p.Nack(0UL,1,1)
    Assert.Single(fallback) |> ignore
    Assert.Equal(CellType.Data,fallback.[0].Type)
