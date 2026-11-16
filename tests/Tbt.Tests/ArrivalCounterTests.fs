module Tbt.Tests.ArrivalCounterTests

open System
open System.Collections.Generic
open Xunit
open Tbt.Contracts
open Tbt.Codec
open Tbt.Crypto
open Tbt.Session
open Tbt.Wire

// Public TxHalf events and authenticated output, checked against a deliberately
// slow absolute-identity oracle. This is not measured arrival or throughput.
type private Fixture() =
    let clock = SessionTests.ManualClock()
    let keys = KeyTable()
    do keys.Install(0u, ReadOnlySpan(Array.create 32 17uy), 0UL)
    let sealer = CellSealer(Direction.Up, 32, keys, NoHeaderMask())
    let opener = CellOpener(Direction.Up, 32, keys, NoHeaderMask())
    let encoder = Encoder(73UL, 32, 512)
    let tx = TxHalf(Direction.Up, 32, CodeField.Gf16, encoder, sealer, 0.25, 1, 4096, clock)
    do tx.InitialGrant 100_000UL
    let frame = Array.zeroCreate<byte> (Layout.frameLength 32)
    let identities = HashSet<struct(CellType * uint64 * byte)>()
    let outstanding = Dictionary<uint64,int64>()
    let mutable acked = 0L
    let mutable ack = 0UL
    let mutable hi = 0UL
    let mutable arrived = 0L
    let count limit = acked + (outstanding |> Seq.sumBy(fun kv -> if kv.Key < limit then kv.Value else 0L))
    let refresh () =
        arrived <- acked + (outstanding |> Seq.sumBy(fun kv -> if kv.Key<=hi && kv.Key<tx.Head then kv.Value else 0L))
    let check () =
        Assert.Equal(count ack,tx.UniqueAcked)
        Assert.Equal(arrived,tx.UniqueArrived)
        Assert.Equal(int64 identities.Count,tx.UniqueRendered)
    let inspect countOriginal =
        let mutable counter = 0UL
        Assert.True(opener.TryOpen(Span frame,0us,&counter))
        let mutable m = Unchecked.defaultof<Meta>
        Assert.True(Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &m)
        if countOriginal && (m.Type=CellType.Data || m.Type=CellType.Parity) then
            if identities.Add(struct(m.Type,m.StepId,m.Slot)) then
                let mutable n=0L
                outstanding.TryGetValue(m.StepId,&n) |> ignore
                outstanding.[m.StepId] <- n+1L
        check()
    member _.Tx = tx
    member _.Add(k,urgent) =
        for _ in 1..k do
            let mutable dst = Span<byte>.Empty
            Assert.True(tx.TryBeginSource(&dst,urgent))
            tx.CommitSource({RealLength=0;FlowSlot=0uy;Flags=StreamFlags.None;FlowGeneration=1u;StreamOffset=0UL},urgent) |> ignore
        tx.Flush()
        check()
    member _.Render(n,allowRepair) =
        for _ in 1..n do
            if tx.TryRenderNext(Span frame,0us,0UL,512us,0us,allowRepair=allowRepair) then inspect true
    member _.Fill() =
        tx.RenderFill(Span frame,0us,0UL,512us,0us) |> ignore
        inspect false
    member _.Ack(value) =
        tx.OnPeerAck(value,512us)
        if value <= tx.Head && value > ack then
            acked <- count value
            for t in outstanding.Keys |> Seq.filter(fun t -> t<value) |> Seq.toArray do
                outstanding.Remove t |> ignore
            ack <- value
            hi <- max hi (ack-1UL)
            refresh()
        check()
    member _.Seen(value) =
        tx.OnPeerSeen value
        if value > hi then
            hi <- value
            refresh()
        check()
    member _.Repair(lo,span) =
        clock.Now <- clock.Now + int64(ceil tx.RepairWaitNs) + 1L
        tx.OnNack(lo,span,1)
        check()

[<Fact>]
let ``arrival snapshot keeps lazy render semantics and initial step zero`` () =
    let f=Fixture()
    f.Add(1,false)
    f.Render(1,false)
    f.Seen(0UL) // same implicit high-water does not refresh
    Assert.Equal(0L,f.Tx.UniqueArrived)
    f.Seen(1UL) // future evidence is accepted by the existing public API
    Assert.Equal(1L,f.Tx.UniqueArrived)
    f.Render(20,false)
    f.Seen(1UL) // newly rendered cells stay invisible until a refresh event
    Assert.Equal(1L,f.Tx.UniqueArrived)
    f.Ack(1UL)
    Assert.Equal(3L,f.Tx.UniqueArrived)
    f.Add(16,false)
    f.Render(5,false)
    f.Seen(2UL)
    f.Render(100,false)
    f.Ack(2UL)

[<Fact>]
let ``arrival counters preserve mixed urgent order fill and DATA fallback`` () =
    let f=Fixture()
    f.Add(16,false)
    f.Add(1,true)
    f.Seen(1UL)
    f.Render(3,false) // urgent step is rendered before the older bulk step
    f.Seen(2UL)
    f.Fill() // extra parity does not enter first-copy accounting
    for _ in 1..65 do
        f.Repair(0UL,1)
        f.Render(4,true)
    f.Render(1000,true)
    f.Seen(3UL)
    f.Ack(1UL)
    f.Ack(2UL)

[<Theory>]
[<InlineData(17)>]
[<InlineData(71)>]
[<InlineData(103)>]
let ``absolute identity oracle survives retained gaps and encoder ring reuse`` seed =
    let f=Fixture()
    let rnd=Random(seed)
    for batch in 0..44 do
        let first=f.Tx.Head
        for i in 0..23 do
            f.Add(rnd.Next(1,17),i%5=0)
            f.Render(rnd.Next(0,8),false)
            if i%3=0 then f.Seen(first+uint64 i)
            if i%7=0 then f.Fill()
        f.Render(2000,false)
        // Keep custody pinned while t_hi advances across already rendered steps.
        for i in 0..23 do f.Seen(first+uint64 i)
        for i in 1..24 do
            f.Ack(first+uint64 i)
            f.Ack(first) // old grant report must not refresh the snapshot
        f.Ack(f.Tx.Head+1UL) // impossible custody is ignored
        if batch%4=0 then f.Seen(f.Tx.Head+8UL)
    Assert.True(f.Tx.Head>1024UL)

[<Fact>]
let ``arrival oracle survives the 65536 first-copy counter slots wrapping`` () =
    let f=Fixture()
    for _ in 0..65538 do
        f.Add(1,false)
        f.Render(3,false)
        f.Seen(f.Tx.Head-1UL)
        f.Ack(f.Tx.Head)
    Assert.Equal(65539L*3L,f.Tx.UniqueAcked)

[<Fact>]
let ``empty future evidence and custody dropping unrendered originals preserve snapshots`` () =
    let f=Fixture()
    f.Seen(100UL)
    f.Add(1,false)
    f.Render(1,false)
    f.Seen(100UL)
    f.Ack(0UL)
    Assert.Equal(0L,f.Tx.UniqueArrived)
    f.Ack(1UL) // only the rendered first DATA is charged; the two queued parities are dropped
    Assert.Equal(1L,f.Tx.UniqueArrived)
    f.Render(100,false)
    f.Add(16,true)
    f.Render(2,false)
    f.Seen(101UL)
    f.Ack(2UL)
    f.Render(100,false)
    Assert.Equal(3L,f.Tx.UniqueRendered)

[<Fact>]
let ``maximum future seen evidence does not overflow its refresh bounds`` () =
    let f=Fixture()
    f.Seen(UInt64.MaxValue)
    f.Add(1,false)
    f.Render(3,false)
    f.Seen(UInt64.MaxValue)
    Assert.Equal(0L,f.Tx.UniqueArrived)
    f.Ack(1UL)
    Assert.Equal(3L,f.Tx.UniqueArrived)
