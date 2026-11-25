[<Xunit.Collection("BridgeTests serial")>]
module Tbt.Tests.FlowReportLifecycleTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Wire
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Tests.SessionTests
open Tbt.Tests.SessionTestsB

type private Lifecycle() =
    let clock = ManualClock()
    do clock.Now <- 1L
    let entropy = SeededEntropy 533
    let offer = ClientOffer(psk,Guid(Array.create 16 7uy),entropy,uint64 unixMs,1448,256)
    let accepted = ServerOffer([|psk|],entropy,4096us,256).TryAccept(ReadOnlySpan offer.Frame,unixMs,1448).Value
    let struct(cp,_) = offer.TryAccept(ReadOnlySpan accepted.Reply).Value
    let clientPort,serverPort = RecordingPort(),RecordingPort()
    let client = Session(Role.Client,cp,clientPort,clock,SessionConfig.defaults)
    let server = Session(Role.Server,accepted.Params,serverPort,clock,SessionConfig.defaults)
    let frame = Array.zeroCreate<byte> client.FrameLength
    let moveOne (src:Session) (dst:Session) =
        if not(src.TryProduce(0us,Span frame)) then None
        else
            Assert.NotEqual(UInt64.MaxValue,dst.OnFrame(0us,Span frame))
            let mutable meta = Unchecked.defaultof<Meta>
            Assert.True(Meta.tryRead (ReadOnlySpan(frame,Layout.MetaOffset,Layout.MetaLength)) &meta)
            if meta.Type = CellType.FlowState then
                let mutable head = Unchecked.defaultof<FlowStateHead>
                Assert.True(FlowState.tryReadHead (ReadOnlySpan(frame,Layout.SymbolOffset,server.SymbolSize)) &head)
                Some(meta.Type,head.RecordCount,head.Snapshot)
            else Some(meta.Type,-1,false)
    let drainClient () =
        let mutable n = 0
        while (moveOne client server).IsSome do
            n <- n+1
            Assert.True(n<100)
    member _.Client = client
    member _.ServerPort = serverPort
    member _.StageSnapshot() =
        clock.Now <- clock.Now + 1_000_000_000L
        server.Tick()
    member _.Echo(slot) =
        let mutable span = Span<byte>.Empty
        Assert.True(server.TryBeginSourceFor(slot,&span))
        span.[0] <- 91uy
        server.CommitSource(slot,1,false)
        server.Flush()
        let mutable n = 0
        while (moveOne server client).IsSome do
            n <- n+1
            Assert.True(n<100)
    member _.ClientFin(slot, bytes) =
        let mutable span = Span<byte>.Empty
        Assert.True(client.TryBeginSourceFor(slot,&span))
        span.Slice(0,bytes).Fill 19uy
        client.CommitSource(slot,bytes,true)
        client.Flush()
        drainClient()
    member _.StageServerFin(slot) =
        let mutable span = Span<byte>.Empty
        Assert.True(server.TryBeginSourceClose(&span))
        server.CommitSource(slot,0,true)
        server.Flush()
    member _.Server = server
    member _.Open() =
        let mutable slot = -1
        Assert.True(client.TryOpenFlow(&slot))
        let mutable span = Span<byte>.Empty
        Assert.True(client.TryBeginSourceFor(slot,&span))
        client.CommitSource(slot,0,false)
        client.Flush()
        drainClient()
        Assert.True(server.Flows.[slot].Active)
        slot
    member _.ResetAndAck(slot) =
        client.FailFlow slot
        client.Flush()
        drainClient()
        Assert.False(server.Flows.[slot].Active)
        // Deliver only the real priority WIN_ACK, leaving lower-priority reports
        // pending across slot reuse. No private state/counter mutation.
        clock.Now <- clock.Now + 20_000_000L
        server.Tick()
        Assert.Equal(Some(CellType.WinAck,-1,false),moveOne server client)
        Assert.False(client.Flows.[slot].Active)
    member _.ReportRound() =
        clock.Now <- clock.Now + 20_000_000L
        server.Tick()
        let pages = ResizeArray<struct(int*bool)>()
        let mutable go = true
        let mutable n = 0
        while go do
            match moveOne server client with
            | None -> go <- false
            | Some(kind,count,snapshot) ->
                if kind = CellType.FlowState then pages.Add(struct(count,snapshot))
                n <- n+1
                Assert.True(n<100)
        pages.ToArray()

[<Fact>]
let ``live flow sends two changed reports then becomes quiet`` () =
    let p = Lifecycle()
    let slot = p.Open()
    Assert.Equal(2,p.Server.Flows.[slot].ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(1,p.Server.Flows.[slot].ReportsDue)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(0,p.Server.Flows.ReportsDue)
    Assert.Empty(p.ReportRound())

[<Fact>]
let ``closed slot reporting is cleaned without disturbing another live slot`` () =
    let p = Lifecycle()
    let closed = p.Open()
    let live = p.Open()
    p.ResetAndAck closed
    Assert.False(p.Server.Flows.Reportable closed)
    Assert.True(p.Server.Flows.Reportable live)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(0,p.Server.Flows.[closed].ReportsDue)
    Assert.Equal(1,p.Server.Flows.[live].ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)
    Assert.Equal<struct(int*bool)>([|struct(1,false)|],p.ReportRound())
    Assert.Equal(0,p.Server.Flows.ReportsDue)

[<Fact>]
let ``slot generation reuse cannot leave phantom reports and persistent empty FLOW_STATE`` () =
    let p = Lifecycle()
    for expected in 0..255 do
        let slot = p.Open()
        Assert.Equal(expected,slot)
        p.ResetAndAck slot
    let reused = p.Open()
    Assert.Equal(0,reused)
    Assert.Equal(2u,p.Server.Flows.[reused].Gen)
    // Drain the two legitimate reports of the new generation. Closed slots are
    // not reportable in current main; this test does not add terminal tombstones.
    // Crossing one second may legitimately produce a snapshot, which does not
    // consume the two incremental report marks. Inspect actual header flags.
    let initial = Array.init 5 (fun _ -> p.ReportRound()) |> Array.concat
    let nonemptyDeltas = initial |> Array.filter(fun struct(n,snapshot) -> n>0 && not snapshot)
    Assert.Equal<struct(int*bool)>([|struct(1,false);struct(1,false)|],nonemptyDeltas)
    Assert.Equal(0,p.Server.Flows.[reused].ReportsDue)
    let residual = p.Server.Flows.ReportsDue
    // Only another80ms: no new1s snapshot should occur in this interval.
    let later = Array.init 4 (fun _ -> p.ReportRound())
    Assert.False(later |> Array.concat |> Array.exists(fun struct(_,snapshot) -> snapshot))
    Assert.True(residual = 0 && (later |> Array.forall Array.isEmpty),
                sprintf "phantom global ReportsDue=%d; subsequent FLOW_STATE (record count,snapshot)=%A" residual later)


[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
let ``actual close retires one marked flow regardless of remaining repeat count`` remaining =
    let p = Lifecycle()
    let slot = p.Open()
    for _ in 1..(2-remaining) do p.ReportRound() |> ignore
    Assert.Equal(remaining,p.Server.Flows.[slot].ReportsDue)
    p.ResetAndAck slot
    Assert.Equal(0,p.Server.Flows.[slot].ReportsDue)
    Assert.Equal(0,p.Server.Flows.ReportsDue)
    for _ in 1..4 do
        p.Server.PumpFlow slot
        Assert.False(p.Server.Flows.TakeReport slot)
    Assert.Equal(0,p.Server.Flows.ReportsDue)

[<Fact>]
let ``local allocation reuse also starts with no prior generation report mark`` () =
    let p = Lifecycle()
    let first = p.Open()
    p.Echo first
    Assert.Equal(2,p.Client.Flows.[first].ReportsDue)
    p.ResetAndAck first
    Assert.Equal(0,p.Client.Flows.ReportsDue)
    for expected in 1..255 do
        let slot = p.Open()
        Assert.Equal(expected,slot)
        p.ResetAndAck slot
    let reused = p.Open()
    Assert.Equal(first,reused)
    Assert.Equal(2u,p.Client.Flows.[reused].Gen)
    Assert.Equal(0,p.Client.Flows.ReportsDue)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``one sided FIN and unacknowledged local FIN do not retire live reports`` blocked =
    let p = Lifecycle()
    let slot = p.Open()
    if blocked then p.ServerPort.WriteLimit <- 0
    p.ClientFin(slot, if blocked then 1 else 0)
    let flow = p.Server.Flows.[slot]
    Assert.True(flow.Active)
    Assert.True(flow.RxDone)
    Assert.False(flow.TxDone)
    if blocked then
        Assert.True(flow.Reserve.Count > 0)
        Assert.True(flow.RxFinPending)
    Assert.Equal(2,flow.ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)
    p.StageServerFin slot
    Assert.True(flow.TxEndSent)
    Assert.False(flow.TxDone) // no authenticated custody ACK yet
    Assert.True(flow.Active)
    Assert.Equal(2,flow.ReportsDue)
    Assert.Equal(1,p.Server.Flows.ReportsDue)

[<Fact>]
let ``closing retires marks without cancelling an already frozen snapshot`` () =
    let p = Lifecycle()
    let slot = p.Open()
    p.StageSnapshot()
    p.ResetAndAck slot
    Assert.Equal(0,p.Server.Flows.ReportsDue)
    let pages = p.ReportRound()
    Assert.Equal<struct(int*bool)>([|struct(1,true)|],pages)
    Assert.Empty(p.ReportRound())

[<Fact>]
let ``custody close replaces old report mark with exactly one pending next generation mark`` () =
    let port = RecordingPort()
    let flows = FlowTable(port,ZeroStore(),Action<uint64,int>(fun _ _ -> ()),CreditPolicy.defaults)
    let source step gen flags offset length =
        let envelope = { RealLength=length; FlowSlot=3uy; Flags=flags; FlowGeneration=gen; StreamOffset=offset }
        Assert.True(flows.OnSource(step,0,envelope))
        flows.Pump 3
    // OPEN and FIN must be distinct: OPEN establishes the old generation mark.
    source 0UL 1u StreamFlags.Open 0UL 10
    Assert.Equal(2,flows.[3].ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    source 1UL 1u StreamFlags.Fin 10UL 0
    flows.NextEnvelope(3,10,true) |> ignore
    flows.NoteEndStep(3,5UL)
    source 2UL 2u StreamFlags.Open 0UL 10
    Assert.Equal(1u,flows.[3].Gen)
    Assert.Equal(1,port.Opened.Count)
    Assert.Equal(2,flows.[3].ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    // Insufficient custody does not prematurely close or retire a mark.
    flows.OnPeerAck 5UL
    Assert.Equal(1u,flows.[3].Gen)
    Assert.Equal(1,flows.ReportsDue)
    flows.OnPeerAck 6UL
    Assert.Equal<int list>([3],List.ofSeq port.Closed)
    Assert.Equal(struct(3,2u),port.Opened.[1])
    Assert.Equal(2,flows.[3].ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    Assert.True(flows.TakeReport 3)
    Assert.Equal(1,flows.ReportsDue)
    Assert.True(flows.TakeReport 3)
    Assert.Equal(0,flows.ReportsDue)
    for _ in 1..3 do
        flows.OnPeerAck 6UL
        flows.Pump 3
        Assert.False(flows.TakeReport 3)
    Assert.Equal(0,flows.ReportsDue)
    Assert.Single(port.Closed) |> ignore

[<Fact>]
let ``custody ACK with pending FIN reserve retains mark until actual drain closes flow`` () =
    let port = RecordingPort()
    port.WriteLimit <- 0
    let flows = FlowTable(port,ZeroStore(),Action<uint64,int>(fun _ _ -> ()),CreditPolicy.defaults)
    let source step flags offset length =
        let e = { RealLength=length; FlowSlot=3uy; Flags=flags; FlowGeneration=1u; StreamOffset=offset }
        Assert.True(flows.OnSource(step,0,e))
        flows.Pump 3
    source 0UL StreamFlags.Open 0UL 10
    source 1UL StreamFlags.Fin 10UL 0
    flows.NextEnvelope(3,0,true) |> ignore
    flows.NoteEndStep(3,5UL)
    flows.OnPeerAck 6UL
    let f = flows.[3]
    Assert.True(f.Active && f.TxDone && f.RxDone && f.RxFinPending)
    Assert.True(f.Reserve.Count > 0)
    Assert.Equal(2,f.ReportsDue)
    Assert.Equal(1,flows.ReportsDue)
    Assert.Empty(port.Closed)
    port.WriteLimit <- Int32.MaxValue
    flows.Pump 3
    Assert.False(f.Active)
    Assert.Equal(0,f.ReportsDue)
    Assert.Equal(0,flows.ReportsDue)
    Assert.Single(port.Closed) |> ignore

// ---- S3: segmented reserve ring and the fluid credit loop -----------------------------

/// Zeroed symbols of one full session symbol: enough payload for a 1328-byte source.
type private SymbolStore() =
    let symbol = Array.zeroCreate<byte> 1344
    interface ISourceStore with
        member _.Source(_, _) = ReadOnlySpan symbol

[<Fact>]
let ``segmented reserve keeps every byte across chunk seams and frees its chunks`` () =
    let q = ByteQueue(4 <<< 20)
    let chunk = 65536
    let data = Array.init (2 * chunk + 17) (fun i -> byte (i &&& 0xFF))
    q.Write(ReadOnlySpan data)
    Assert.Equal(data.Length, q.Count)
    Assert.Equal(q.Cap - data.Length, q.Free)
    // Read it out in pieces that are deliberately not chunk aligned: each Peek must return
    // the next contiguous run and never more than the rest of the front chunk.
    let seen = ResizeArray<byte>()
    let mutable guard = 0
    while q.Count > 0 && guard < 10_000 do
        let s = q.Peek()
        Assert.True(s.Length > 0 && s.Length <= chunk)
        let take = min s.Length 5000
        seen.AddRange(s.Slice(0, take).ToArray())
        q.Skip take
        guard <- guard + 1
    Assert.Equal<byte[]>(data, seen.ToArray())
    Assert.Equal(0, q.Count)
    Assert.True(q.Peek().IsEmpty)
    // A drained reserve reuses its ring, so a second write still reads back exactly.
    let again = Array.init 700 (fun i -> byte (255 - i % 251))
    q.Write(ReadOnlySpan again)
    let slice = q.Peek().Slice(0, again.Length).ToArray()
    Assert.Equal<byte[]>(again, slice)
    q.Clear()
    Assert.Equal(0, q.Count)
    Assert.True(q.Peek().IsEmpty)

[<Fact>]
let ``segmented reserve survives randomized write peek skip clear`` () =
    let q = ByteQueue(8 <<< 20)
    let rnd = Random 20261124
    let model = ResizeArray<byte>()
    for round in 1 .. 500 do
        match rnd.Next 4 with
        | 0
        | 1 ->
            let n = rnd.Next(0, if round % 41 = 0 then 200_000 else 6_000)
            Assert.True(q.Free >= n)
            let data = Array.init n (fun _ -> byte (rnd.Next 256))
            q.Write(ReadOnlySpan data)
            model.AddRange data
        | 2 ->
            let n = min (rnd.Next(0, 70_000)) (min model.Count (q.Peek().Length))
            if n > 0 then
                let expected = model.GetRange(0, n).ToArray()
                let actual = q.Peek().Slice(0, n).ToArray()
                Assert.Equal<byte[]>(expected, actual)
                q.Skip n
                model.RemoveRange(0, n)
        | _ ->
            if rnd.Next 8 = 0 then
                q.Clear()
                model.Clear()
        Assert.Equal(model.Count, q.Count)
        Assert.Equal(q.Cap - model.Count, q.Free)
        let m = min (min model.Count 900) (q.Peek().Length)
        if m > 0 then
            let expected = model.GetRange(0, m).ToArray()
            let actual = q.Peek().Slice(0, m).ToArray()
            Assert.Equal<byte[]>(expected, actual)
    q.Clear()
    Assert.Equal(0, q.Count)

[<Fact>]
let ``credit is announced at flow open and refreshed in fine steps`` () =
    let port = RecordingPort()
    let flows = FlowTable(port, SymbolStore(), Action<uint64,int>(fun _ _ -> ()), CreditPolicy.defaults)
    let source (step: uint64) (offset: uint64) (flags: StreamFlags) =
        let e =
            { RealLength = 1328; FlowSlot = 3uy; Flags = flags; FlowGeneration = 1u
              StreamOffset = offset }
        Assert.True(flows.OnSource(step, 0, e))
        flows.Pump 3
    // OPEN announces the fair share immediately: before this, the peer ran the first round
    // trip on C0 (64 KiB) whatever the path BDP, one RTT of slow start on every flow.
    source 0UL 0UL StreamFlags.Open
    let opened = flows.[3].RxLimit
    let c0 = uint64 CreditPolicy.defaults.Initial
    Assert.True(opened > c0, sprintf "limit %d at open, C0 %d" opened c0)
    Assert.True(opened <= uint64 CreditPolicy.defaults.FlowWindowMax)
    Assert.Equal(1, flows.RxActive)
    Assert.Equal(1, flows.ReportsDue)
    // ~300 KiB more with a reader that takes everything: the sixteenth-of-window step
    // (at least half of C0, at most 256 KiB) must have grown the promise, where the old
    // window/8 rule needed a whole 1 MiB before it reported again.
    let mutable step = 1UL
    let mutable offset = 1328UL
    let mutable delivered = 1328
    while delivered < 300_000 do
        source step offset StreamFlags.None
        step <- step + 1UL
        offset <- offset + 1328UL
        delivered <- delivered + 1328
    Assert.True(flows.[3].RxLimit > opened, sprintf "limit stalled at %d after %d bytes" opened delivered)
    // The budget invariant holds while the promise grows.
    Assert.True(flows.CommittedBytes <= int64 CreditPolicy.defaults.PoolBytes)
    Assert.True(flows.PoolUsed <= int64 CreditPolicy.defaults.PoolBytes - int64 256 * int64 CreditPolicy.defaults.Initial)

// ---- S6: the send-side step<->flow link (Trace.Fill / Trace.Put) -----------------------
// `Fill` names the encoding step a committed source entered; `Put` carries that flow's
// cumulative stream offset AFTER that source. Pairing is positional — every Fill is followed
// immediately by its Put — so consecutive Put deltas of one (slot, generation) are exactly
// the byte range the Fill before them explains. Both come from Session.CommitSource, the one
// funnel for every send path. `Seal` cannot serve this: it is recorded for urgent/interactive
// steps only, so a bulk step that carried a probe's echo had no send-side record at all.
[<Fact>]
let ``committed sources trace their step and stream offset across interleaved flows`` () =
    let tmp = IO.Path.GetTempFileName()
    let l = Lifecycle()
    let slotA = l.Open()
    let slotB = l.Open()
    Assert.NotEqual(slotA, slotB)
    let gen (slot: int) = int l.Server.Flows.[slot].Gen
    let commit (slot: int) (len: int) =
        let mutable span = Span<byte>.Empty
        Assert.True(l.Server.TryBeginSourceFor(slot, &span), sprintf "no send room for slot %d" slot)
        span.Slice(0, len).Fill 7uy
        l.Server.CommitSource(slot, len, false)
    Tbt.Contracts.Trace.start tmp
    try
        // Two flows, unequal lengths, interleaved: A 5, B 7, A 3, B 11.
        commit slotA 5
        commit slotB 7
        commit slotA 3
        commit slotB 11
        Tbt.Contracts.Trace.flush ()
    finally
        Tbt.Contracts.TraceGate.On <- false
    // With the gate off the same call must add nothing, so the four records below cannot be
    // explained by a trace that writes unconditionally.
    commit slotA 9
    Tbt.Contracts.Trace.flush ()
    let rows = IO.File.ReadAllLines tmp |> Array.map (fun line -> line.Split(' '))
    IO.File.Delete tmp
    let pairs = rows |> Array.filter (fun p -> p.[0] = "12" || p.[0] = "13")
    Assert.Equal(8, pairs.Length)
    for i in 0 .. 3 do
        Assert.Equal("12", pairs.[2 * i].[0])                      // Fill first
        Assert.Equal("13", pairs.[2 * i + 1].[0])                  // its Put second
        Assert.Equal(pairs.[2 * i].[2], pairs.[2 * i + 1].[2])     // same flow slot
        Assert.Equal(pairs.[2 * i].[3], pairs.[2 * i + 1].[3])     // same generation
    // Commit order across both flows — not "all of A, then all of B".
    let order = [| slotA; slotB; slotA; slotB |]
    for i in 0 .. 3 do
        Assert.Equal(string order.[i], pairs.[2 * i].[2])
    // Generations come from the live flows, so a hard-coded 0 could not pass.
    Assert.Equal(string (gen slotA), pairs.[0].[3])
    Assert.Equal(string (gen slotB), pairs.[2].[3])
    // Steps are non-decreasing in commit order: the encoder's head only moves forward.
    let steps = pairs |> Array.filter (fun p -> p.[0] = "12") |> Array.map (fun p -> uint64 p.[1])
    for i in 1 .. steps.Length - 1 do
        Assert.True(steps.[i] >= steps.[i - 1], sprintf "step went backwards: %d then %d" steps.[i - 1] steps.[i])
    // The load-bearing claim: per flow, each Put delta equals the length of the Fill before it.
    // Deltas are used instead of absolute offsets so this cannot pass on a lucky starting value.
    let lengths = [| (slotA, 5); (slotB, 7); (slotA, 3); (slotB, 11) |]
    let lastPut = System.Collections.Generic.Dictionary<int, uint64>()
    for i in 0 .. 3 do
        let (slot, len) = lengths.[i]
        let put = uint64 pairs.[2 * i + 1].[1]
        match lastPut.TryGetValue slot with
        | true, previous -> Assert.Equal(uint64 len, put - previous)
        | _ -> ()
        lastPut.[slot] <- put

// ---- FLOW_STATE page atomicity: one bad record rejects the whole page (spec 6.10b.2) ----

let private pageOf (epoch: uint32) (seq: uint32) (records: FlowRecord list) : byte[] =
    let page = Array.zeroCreate<byte> 1344
    let head =
        { Snapshot = false; LastPage = false; RecordCount = List.length records; TotalFlows = 0
          PageIndex = 0; StateSeq = seq; StateEpoch = epoch; SnapshotId = 0u; AllKindsKnown = true }
    FlowState.writeHead head (Span<byte>(page))
    records |> List.iteri (fun i r -> FlowState.writeFlow i r (Span<byte>(page)))
    page

[<Fact>]
let ``a page whose record names a slot outside this end's space moves nothing`` () =
    let port = RecordingPort()
    let flows = FlowTable(port, SymbolStore(), Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
    let openFlow (slot: int) (gen: uint32) =
        let e =
            { RealLength = 0; FlowSlot = byte slot; Flags = StreamFlags.Open
              FlowGeneration = gen; StreamOffset = 0UL }
        Assert.True(flows.OnSource(0UL, slot, e))
        flows.[slot].Start(gen, CreditPolicy.defaults)
    openFlow 0 1u
    openFlow 1 1u
    // The first record is perfectly good and would raise slot 0's limit. The second names slot
    // 300 — outside this end's 256-slot space. Nothing at all may move.
    let good =
        { State = FlowLifecycle.Open; FlowSlot = 0; FlowGeneration = 1u
          DeliveredOffset = 1_000_000UL; CreditBytes = 1_000_000u }
    let bad = { good with FlowSlot = 300 }
    let page = pageOf 5u 1u [ good; bad ]
    let applied = flows.OnPeerPage(5u, 1u, ReadOnlySpan page, 2, 222L)
    Assert.False applied
    Assert.Equal(uint64 CreditPolicy.defaults.Initial, flows.[0].TxLimit)
    Assert.Equal(0UL, flows.[0].TxDeliveredReported)
    Assert.Equal(0L, flows.[0].TxReportsAccepted)
    Assert.Equal(0L, flows.[0].TxReportsForeign)
    // The same page without the bad record applies both slots' credit normally.
    let page2 = pageOf 5u 1u [ good; { good with FlowSlot = 1 } ]
    Assert.True(flows.OnPeerPage(5u, 1u, ReadOnlySpan page2, 2, 222L))
    Assert.Equal(1_000_000UL, flows.[0].TxDeliveredReported)
    Assert.Equal(1_000_000UL, flows.[1].TxDeliveredReported)
    Assert.Equal(1L, flows.[0].TxReportsAccepted)

[<Fact>]
let ``an out-of-range slot handed straight to OnPeerCredit is refused, not indexed`` () =
    let port = RecordingPort()
    let flows = FlowTable(port, SymbolStore(), Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
    let r =
        { State = FlowLifecycle.Open; FlowSlot = 4095; FlowGeneration = 1u
          DeliveredOffset = 1UL; CreditBytes = 1u }
    // Defense in depth: even a caller that bypassed the wire reader must not reach the array.
    Assert.Throws<ArgumentOutOfRangeException>(fun () -> flows.OnPeerCredit(1u, 1u, r, 0L) |> ignore)
        |> ignore
    Assert.Equal(256, flows.SlotCount)

// ---- peer-report observation: what the sender may believe about the peer, and how stale ----
// `TxDeliveredReported` is the peer's rx CUSTODY frontier (`RxDelivered` on the far side), not
// its application read position, and it arrives one FLOW_STATE page late. The tests below pin
// the accept/observe contract: freshness is a LOCAL age of acceptance (no clock agreement with
// the peer is needed or assumed), a duplicate page must not look fresh, a foreign generation must
// not touch the state, and a peer invariant violation is counted but cannot shrink credit.
[<Fact>]
let ``peer credit reports record the delivered offset with a local acceptance age`` () =
    let port = RecordingPort()
    let flows = FlowTable(port, SymbolStore(), Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
    let openFlow (slot: int) (gen: uint32) =
        let e =
            { RealLength = 0; FlowSlot = byte slot; Flags = StreamFlags.Open
              FlowGeneration = gen; StreamOffset = 0UL }
        Assert.True(flows.OnSource(0UL, slot, e))
        flows.[slot].Start(gen, CreditPolicy.defaults)
    openFlow 0 1u
    let f = flows.[0]
    let record (slot: int) (gen: uint32) (delivered: uint64) (credit: uint32) : Tbt.Wire.FlowRecord =
        { State = FlowLifecycle.Open; FlowSlot = slot; FlowGeneration = gen
          DeliveredOffset = delivered; CreditBytes = credit }
    // Nothing reported yet: no offset, no age. `-1` in the stats line, never a fabricated 0 age.
    Assert.Equal(0UL, f.TxDeliveredReported)
    Assert.Equal(0L, f.TxReportNs)
    Assert.Equal(0L, f.TxReportsAccepted)
    // A new page: accepted, offset stored, age anchored on OUR clock. The limit does NOT move:
    // `Start` already installed C0 (64 KiB) and the limit only grows, so a peer limit below C0
    // can never shrink it. A reader must not read "TxLimit unchanged" as "report ignored" —
    // `TxReportsAccepted` and `TxDeliveredReported` are the evidence that it was accepted.
    flows.OnPeerCredit(5u, 1u, record 0 1u 1000UL 5000u, 111_000_000L)
    Assert.Equal(1L, f.TxReportsAccepted)
    Assert.Equal(1000UL, f.TxDeliveredReported)
    Assert.Equal(111_000_000L, f.TxReportNs)
    Assert.Equal(uint64 CreditPolicy.defaults.Initial, f.TxLimit)
    // The same page again must NOT refresh freshness: a repeat is not new evidence.
    flows.OnPeerCredit(5u, 1u, record 0 1u 1000UL 5000u, 222_000_000L)
    Assert.Equal(1L, f.TxReportsAccepted)
    Assert.Equal(1L, f.TxReportsStale)
    Assert.Equal(111_000_000L, f.TxReportNs)
    // An older epoch, and an older seq in the same epoch, are both stale.
    flows.OnPeerCredit(4u, 9u, record 0 1u 2000UL 5000u, 333_000_000L)
    flows.OnPeerCredit(5u, 0u, record 0 1u 2000UL 5000u, 333_000_000L)
    Assert.Equal(3L, f.TxReportsStale)
    Assert.Equal(1000UL, f.TxDeliveredReported)
    // A report for another generation of the same slot is foreign: it may not move this
    // generation's offset, age or limit.
    flows.OnPeerCredit(9u, 9u, record 0 2u 9999UL 9999u, 444_000_000L)
    Assert.Equal(1L, f.TxReportsForeign)
    Assert.Equal(1000UL, f.TxDeliveredReported)
    Assert.Equal(uint64 CreditPolicy.defaults.Initial, f.TxLimit)
    Assert.Equal(111_000_000L, f.TxReportNs)
    // A backwards custody offset is a peer invariant violation: counted, but the stored value
    // keeps the higher one so a bad page cannot make the sender believe less was delivered.
    flows.OnPeerCredit(6u, 1u, record 0 1u 500UL 100u, 555_000_000L)
    Assert.Equal(1L, f.TxDeliveredRegressions)
    Assert.Equal(1000UL, f.TxDeliveredReported)
    Assert.Equal(uint64 CreditPolicy.defaults.Initial, f.TxLimit)
    Assert.Equal(555_000_000L, f.TxReportNs)
    // A page that DOES exceed the current limit grows it, so the growth path is exercised too.
    flows.OnPeerCredit(7u, 1u, record 0 1u 200_000UL 100_000u, 666_000_000L)
    Assert.Equal(300_000UL, f.TxLimit)
    Assert.Equal(200_000UL, f.TxDeliveredReported)
    // Accepted is a REPORT count, not a limit-change count: the epoch-6 page above was accepted
    // (and counted a regression) even though it could not raise the limit, so by here three
    // pages have advanced epoch:seq — 5:1, 6:1, 7:1.
    Assert.Equal(3L, f.TxReportsAccepted)
    // ... and a later page below that high-water mark neither shrinks the limit nor rewinds the
    // stored offset: `TxLimit` is monotone per (slot, generation) by contract.
    flows.OnPeerCredit(8u, 1u, record 0 1u 1000UL 100u, 777_000_000L)
    Assert.Equal(300_000UL, f.TxLimit)
    Assert.Equal(200_000UL, f.TxDeliveredReported)
    Assert.Equal(2L, f.TxDeliveredRegressions)

[<Fact>]
let ``a report for an unused slot, or with credit disabled, is foreign and changes nothing`` () =
    let port = RecordingPort()
    let flows = FlowTable(port, SymbolStore(), Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
    let record (slot: int) (gen: uint32) : Tbt.Wire.FlowRecord =
        { State = FlowLifecycle.Open; FlowSlot = slot; FlowGeneration = gen
          DeliveredOffset = 4096UL; CreditBytes = 4096u }
    // Slot 7 was never started: Gen = 0, so no generation can match and nothing may be learned.
    flows.OnPeerCredit(1u, 1u, record 7 1u, 10L)
    Assert.Equal(1L, flows.[7].TxReportsForeign)
    Assert.Equal(0UL, flows.[7].TxDeliveredReported)
    Assert.Equal(0L, flows.[7].TxReportNs)
    // Credit disabled: the peer's limits are meaningless here, so the report is foreign too and
    // must not manufacture an offset or an age the stats line would present as measured.
    let off = { CreditPolicy.defaults with Enabled = false }
    let flows2 = FlowTable(port, SymbolStore(), Action<uint64, int>(fun _ _ -> ()), off)
    let e =
        { RealLength = 0; FlowSlot = 1uy; Flags = StreamFlags.Open
          FlowGeneration = 1u; StreamOffset = 0UL }
    Assert.True(flows2.OnSource(0UL, 1, e))
    flows2.[1].Start(1u, off)
    flows2.OnPeerCredit(1u, 1u, record 1 1u, 10L)
    Assert.Equal(1L, flows2.[1].TxReportsForeign)
    Assert.Equal(0UL, flows2.[1].TxDeliveredReported)
    Assert.Equal(0L, flows2.[1].TxReportsAccepted)
    Assert.Equal(UInt64.MaxValue, flows2.[1].TxLimit)

// ---- credit accounting: `TxLimit - TxOffset` is a CREDIT remainder, not an in-flight measure ----
// A prior analysis computed "unacked = window - room" and then BDP ratios and queueing delays
// from it. That is circular: `room` is *defined* as `TxLimit - TxOffset`, so the identity
// `TxLimit - TxOffset = window - unacked` only restates `unacked := window - room`. The two
// tests below pin the real semantics with states that need no peer and no clock.
[<Fact>]
let ``TxRoom is a credit remainder and cannot be read as in-flight bytes`` () =
    let port = RecordingPort()
    let flows = FlowTable(port, SymbolStore(), Action<uint64, int>(fun _ _ -> ()), CreditPolicy.defaults)
    let openFlow (slot: int) (gen: uint32) =
        let e =
            { RealLength = 0; FlowSlot = byte slot; Flags = StreamFlags.Open
              FlowGeneration = gen; StreamOffset = 0UL }
        Assert.True(flows.OnSource(0UL, slot, e))
    openFlow 0 1u
    // `Start` is what installs the credit state; delivering an OPEN source alone does not, so a
    // slot that was never started has TxLimit = 0 and TxRoom = 0 (nothing may be sent). Make the
    // started state explicit here instead of inferring it from the envelope.
    flows.[0].Start(1u, CreditPolicy.defaults)
    let f = flows.[0]
    // A freshly opened flow starts at C0 (credit.Initial), NOT at the flow window, and nothing
    // has been sent yet. So an "in-flight = window - room" reading would claim
    // FlowWindowMax - C0 bytes in flight for a flow whose TxOffset is still zero.
    Assert.Equal(uint64 CreditPolicy.defaults.Initial, f.TxLimit)
    Assert.Equal(0UL, f.TxOffset)
    let window = uint64 CreditPolicy.defaults.FlowWindowMax
    let room = uint64 (flows.TxRoom 0)
    Assert.Equal(uint64 CreditPolicy.defaults.Initial, room)
    let claimedInFlight = if window > room then window - room else 0UL
    // The contradiction, stated as an invariant: a true in-flight count can never exceed the
    // bytes actually sent (TxOffset). This formula reports megabytes while TxOffset is 0.
    Assert.True(claimedInFlight > f.TxOffset,
                sprintf "formula claims %d B in flight while only %d B were ever sent" claimedInFlight f.TxOffset)
    // And an upper bound that DOES hold locally: nothing can be unacknowledged beyond TxOffset.
    Assert.True(f.TxOffset >= 0UL)

[<Fact>]
let ``with credit disabled TxRoom is the Int32 sentinel, so no byte accounting applies`` () =
    let port = RecordingPort()
    let policy = { CreditPolicy.defaults with Enabled = false }
    let flows = FlowTable(port, SymbolStore(), Action<uint64, int>(fun _ _ -> ()), policy)
    let e =
        { RealLength = 0; FlowSlot = 0uy; Flags = StreamFlags.Open
          FlowGeneration = 1u; StreamOffset = 0UL }
    Assert.True(flows.OnSource(0UL, 0, e))
    // As above: only `Start` installs the credit sentinel for this slot.
    flows.[0].Start(1u, policy)
    // Flows.Start sets TxLimit to UInt64.MaxValue when credit is off, and TxRoom turns that
    // into Int32.MaxValue. Subtracting it from a window would underflow; a reader that treats
    // it as "bytes in flight" is reading a sentinel as a measurement.
    Assert.Equal(UInt64.MaxValue, flows.[0].TxLimit)
    Assert.Equal(System.Int32.MaxValue, flows.TxRoom 0)
