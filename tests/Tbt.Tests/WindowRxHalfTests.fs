module Tbt.Tests.WindowRxHalfIntegrationTests

open System
open System.Collections.Generic
open Xunit
open Tbt.Codec
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Session
open Tbt.Wire

type private Clock() =
    member val Time = 1_000_000_000L with get, set
    interface IClock with
        member x.NowNs() = x.Time
        member x.FreshNs() = x.Time

let private setup () =
    let key = Array.init 32 byte
    let send, recv = KeyTable(), KeyTable()
    send.Install(0u, ReadOnlySpan key, 0UL)
    recv.Install(0u, ReadOnlySpan key, 0UL)
    let sealer = CellSealer(Direction.Up, 1024, send, NoHeaderMask())
    let opener = CellOpener(Direction.Up, 1024, recv, NoHeaderMask())
    let clock = Clock()
    let enc = WindowStepEncoder(1024, 16)
    let tx = TxHalf(Direction.Up, 1024, CodeField.Gf2, enc :> ICodecEncoder, sealer, 0.5, false, 0.0, 1.0, 0.0, 1.0, 64, 4, 128, clock, useWindowCodec = true)
    tx.InitialGrant 16UL
    let wd = WindowSnapshotDecoder(1024, 4, 1L <<< 20)
    let rx = RxHalf(1024, opener, 16, 4, wd)
    let received = ResizeArray<byte>()
    let port =
        { new IFlowPort with
            member _.Open(_, _) = ()
            member _.Write(_, data) =
                for b in data do received.Add b
                data.Length
            member _.Finish _ = ()
            member _.Reset _ = ()
            member _.Close _ = ()
            member _.Credit _ = () }
    let flows = FlowTable(port, rx :> ISourceStore, Action<uint64,int>(fun t d -> rx.StepUndelivered(t,d)), CreditPolicy.defaults)
    rx.Attach flows
    tx, rx, wd, flows, received

let private append (tx: TxHalf) (offset: int) (flags: StreamFlags) value =
    let mutable dst = Span<byte>.Empty
    Assert.True(tx.TryBeginSource(&dst))
    dst.[Layout.EnvelopeLength] <- value
    tx.CommitSource({ RealLength = 1; FlowSlot = 1uy; Flags = flags; FlowGeneration = 1u; StreamOffset = uint64 offset }, false) |> ignore

[<Fact>]
let ``encrypted window flow recovers an entirely lost step through nack`` () =
    let tx, rx, _, flows, received = setup ()
    append tx 0 StreamFlags.Open 11uy
    tx.Flush()
    for i = 0 to 15 do append tx (i + 1) StreamFlags.None (byte (20 + i))
    append tx 17 StreamFlags.None 77uy
    tx.Flush()
    let frame = Array.zeroCreate<byte> (Layout.frameLength 1024)
    let mutable now = 1_000_000_000L
    let mutable dropped = 0
    let deliver dropStep =
        Assert.True(tx.TryRenderNext(Span frame, 0us, rx.AckStepLo, rx.Grant, 0us))
        match rx.Open(Span frame, 0us) with
        | ValueSome m ->
            if dropStep && m.Type = CellType.Data && m.StepId = 1UL then dropped <- dropped + 1
            // Isolate the NACK/DATA fallback: proactive parity could otherwise
            // recover the lost step before the receiver requests any repair.
            elif dropStep && m.Type = CellType.Parity then ()
            else rx.OnCoded(m, ReadOnlySpan(frame, Layout.SymbolOffset, 1024), 0us, now)
        | ValueNone -> failwith "authenticated frame rejected"
        now <- now + 1_000_000L
    while tx.HasCandidate do deliver true
    Assert.Equal(16, dropped)
    let mutable span, deficit = 0, 0
    now <- now + 30_000_000L
    Assert.True(rx.TryNack(now, &span, &deficit), "whole-step loss must request repair")
    tx.OnNack(rx.AckStepLo, span, deficit)
    let mutable count = 0
    while tx.HasCandidate && count < 256 do
        deliver false
        count <- count + 1
    flows.Pump 1
    let expected = Array.concat [| [|11uy|]; [|for i in 0..15 -> byte (20+i)|]; [|77uy|] |]
    Assert.Equal<byte[]>(expected, received.ToArray())

[<Fact>]
let ``rejected authenticated window metadata cannot change progress or erasure`` () =
    let _, rx, wd, _, received = setup ()
    let payload = Array.zeroCreate<byte> 1024
    SourceEnvelope.write { RealLength = 1; FlowSlot = 1uy; Flags = StreamFlags.Open; FlowGeneration = 1u; StreamOffset = 0UL } (Span payload)
    payload.[Layout.EnvelopeLength] <- 23uy
    let data = WindowBridge.sourceMeta 0UL 0 1
    // OnCoded is called after authentication by the production carrier.
    rx.OnCoded(data, ReadOnlySpan payload, 0us, 1_000_000_000L)
    let before = received.ToArray()
    let ack = rx.AckStepLo
    let erasure = rx.ErasureQ16
    let conflicts = wd.Conflicts
    rx.OnCoded({ data with StepUsed = 2uy }, ReadOnlySpan payload, 0us, 1_001_000_000L)
    rx.OnCoded({ data with StepId = 100UL }, ReadOnlySpan payload, 0us, 1_002_000_000L)
    let struct (parity, _) = WindowBridge.snapshotParity 1UL 0 [| 1 |]
    rx.OnCoded({ parity with KHist = parity.KHist ||| 0x800000u }, ReadOnlySpan payload, 0us, 1_003_000_000L)
    Assert.Equal<byte[]>(before, received.ToArray())
    Assert.Equal(ack, rx.AckStepLo)
    Assert.Equal(erasure, rx.ErasureQ16)
    Assert.True(wd.Conflicts > conflicts)

[<Fact>]
let ``entirely lost step recovered by parity contributes to deadline erasure`` () =
    let _, rx, wd, _, received = setup ()
    let enc = WindowStepEncoder(1024, 16)
    for slot = 0 to 15 do
        let mutable dst = Span<byte>.Empty
        Assert.True(enc.TryGetFillSlot(slot, &dst))
        let flags = if slot = 0 then StreamFlags.Open else StreamFlags.None
        SourceEnvelope.write { RealLength = 1; FlowSlot = 1uy; Flags = flags; FlowGeneration = 1u; StreamOffset = uint64 slot } dst
        dst.[Layout.EnvelopeLength] <- byte slot
    enc.Seal 16 |> ignore
    let metaApi = enc :> IWindowEncoderMeta
    let symbol = Array.zeroCreate<byte> 1024
    let mutable meta = Unchecked.defaultof<Meta>
    // All DATA was lost; use independent parity snapshots only.
    for parity = 0 to 47 do
        Assert.True(metaApi.TryRenderWindowParity(0UL, parity, Span symbol, &meta))
        rx.OnCoded(meta, ReadOnlySpan symbol, 0us, 1_000_000_000L + int64 parity * 1_000_000L)
        metaApi.CommitParityIndex 0UL
    rx.Service 1_400_000_000L
    Assert.True(rx.Counters.Recovered > 0L)
    Assert.True(rx.ErasureQ16 > 0us, "recovered sources must contribute to deadline erasure")
    Assert.True(wd.PublishedLimit > 0UL)
    Assert.True(received.Count > 0)

[<Fact>]
let ``heartbeat makes an entirely unseen step eligible for repair`` () =
    let _, rx, _, _, _ = setup ()
    rx.OnHeartbeat 0UL
    let mutable span, deficit = 0, 0
    Assert.True(rx.TryNack(1_000_000_000L, &span, &deficit))
    Assert.True(span > 0)
    Assert.True(deficit > 0)
