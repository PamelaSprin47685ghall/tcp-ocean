module Tbt.Tests.WireTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Wire

[<Theory>]
[<InlineData(1448, 1344)>]
[<InlineData(1440, 1280)>]
[<InlineData(1428, 1280)>]
[<InlineData(1420, 1280)>]
[<InlineData(1320, 1216)>]
[<InlineData(536, 1024)>]
[<InlineData(9000, 1344)>]
let ``symbol length follows the MSS table of spec 3.1`` (mss: int, s: int) =
    Assert.Equal(s, FrameMath.symbolForMss mss)
    Assert.True(Layout.isLegalSymbol s)

[<Fact>]
let ``a clamped symbol does not claim one segment per frame`` () =
    Assert.True(FrameMath.fitsOneSegment 1344 1448)
    Assert.False(FrameMath.fitsOneSegment 1024 536)

[<Theory>]
[<InlineData(1415, 816)>]
[<InlineData(1351, 494)>]
[<InlineData(1287, 1192)>]
[<InlineData(1087, 939)>]
let ``2^32 mod L is not zero, so frame position needs 64-bit sequence`` (l: int, rem: int) =
    Assert.Equal(uint64 rem, (1UL <<< 32) % uint64 l)

[<Fact>]
let ``sequence extension crosses the 32-bit wrap and rejects the half-space boundary`` () =
    let b = 0xFFFE0000UL
    Assert.Equal(ValueSome 0x1_0000_0010UL, FrameMath.extendSeq b 0x10u)
    Assert.Equal(ValueSome 0xFFFD0000UL, FrameMath.extendSeq b 0xFFFD0000u)
    Assert.Equal(ValueNone, FrameMath.extendSeq b (uint32 (b + 0x80000000UL)))
    Assert.Equal(ValueNone, FrameMath.extendSeq 5UL 0xFFFFFFF0u)

[<Fact>]
let ``hole split matches the example of spec 6.7.3`` () =
    let struct (iLo, iHi) = FrameMath.wholeFrames 1000UL 1415 143200UL 144600UL
    Assert.Equal(101UL, iLo)
    Assert.Equal(101UL, iHi)
    let struct (a, b) = FrameMath.wholeFrames 1000UL 1415 (1000UL + 1415UL * 5UL) (1000UL + 1415UL * 8UL)
    Assert.Equal(5UL, a)
    Assert.Equal(8UL, b)
    Assert.Equal(ValueSome(struct (101UL, 785)), FrameMath.framePosition 1000UL 1415 144700UL)

[<Theory>]
[<InlineData(1344, "580")>]
[<InlineData(1280, "540")>]
[<InlineData(1216, "500")>]
[<InlineData(1152, "4c0")>]
let ``chunk header is three hex digits of S + 64`` (s: int, hex: string) =
    let f = Array.zeroCreate<byte> (Layout.frameLength s)
    FrameMath.writeChunkHeader s (Span f)
    Assert.Equal(hex + "\r\n", Text.Encoding.ASCII.GetString(f, 0, 5))
    f.[f.Length - 2] <- 13uy
    f.[f.Length - 1] <- 10uy
    Assert.True(FrameMath.checkFraming s (ReadOnlySpan f))
    f.[f.Length - 1] <- 0uy
    Assert.False(FrameMath.checkFraming s (ReadOnlySpan f))

[<Fact>]
let ``source envelope layout matches the reference vector`` () =
    let b = Array.zeroCreate<byte> 1344
    SourceEnvelope.write { RealLength = 1328; FlowSlot = 2uy; Flags = StreamFlags.Open; FlowGeneration = 7u; StreamOffset = 0UL } (Span b)
    Assert.Equal(Vectors.str Vectors.codec.Value "envelope(1328,2,0x01,7,0)", Vectors.toHex (ReadOnlySpan b.[.. 15]))
    let mutable e = Unchecked.defaultof<SourceEnvelope>
    Assert.True(SourceEnvelope.tryRead (ReadOnlySpan b) &e)
    Assert.Equal(1328, e.RealLength)
    b.[3] <- 0x10uy
    Assert.False(SourceEnvelope.tryRead (ReadOnlySpan b) &e)
    b.[3] <- 0uy
    b.[0] <- 0x31uy   // 1329 > S - 16
    Assert.False(SourceEnvelope.tryRead (ReadOnlySpan b) &e)

[<Fact>]
let ``meta round trips and rejects reserved bytes and bad slots`` () =
    let k = [| 16; 16; 5; 16; 1; 16 |]
    let m =
        { Type = CellType.Parity; Flags = MetaFlags.Urgent ||| MetaFlags.Fill; Slot = 47uy; StepUsed = 16uy
          Aux = 3us; RxWinGrant = 4096us; StepId = 300UL; AckStepLo = 290UL; CarrierWm = 299UL
          KHist = Meta.packKHist (ReadOnlySpan k); ErasureQ16 = 3277us }
    let b = Array.zeroCreate<byte> 40
    Meta.write m (Span b)
    let mutable r = Unchecked.defaultof<Meta>
    Assert.True(Meta.tryRead (ReadOnlySpan b) &r)
    Assert.Equal(m, r)
    for i in 0 .. 5 do Assert.Equal(k.[i], Meta.kHistAt r.KHist i)
    // DECISIONS.md D2: LE24, nibble i = stride i; t-1 is the low nibble of byte 32.
    Assert.Equal("fff4f0", Vectors.toHex (ReadOnlySpan b.[32 .. 34]))
    b.[38] <- 1uy
    Assert.False(Meta.tryRead (ReadOnlySpan b) &r)
    b.[38] <- 0uy
    b.[2] <- 48uy
    Assert.False(Meta.tryRead (ReadOnlySpan b) &r)
    b.[2] <- 0uy
    b.[0] <- 7uy
    Assert.False(Meta.tryRead (ReadOnlySpan b) &r)

[<Fact>]
let ``WIN_ACK carries 78 path stats at S = 1344 and round trips`` () =
    Assert.Equal(78, WinAck.maxPaths 1344)
    Assert.Equal(70, WinAck.maxPaths 1216)
    let b = Array.zeroCreate<byte> 1344
    let h =
        { TLo = 1000UL; Deficit = 3us; Span = 2uy; NackFlags = 1uy; ErasureMeanQ16 = 19661us; ErasureP95Q16 = 30000us
          THi = 1200UL; RxTimeUs = 123456789UL; PathCount = 2; DecodedBitmap0 = 5UL; DecodedBitmap1 = 0UL
          DecodedBitmap2 = 0UL; DecodedBitmap3 = 1UL <<< 63; CtrlAckHi = 9u; CtrlAckBitmap = 6UL }
    WinAck.writeHead h (Span b)
    let p = { PathId = 287us; Flags = 0uy; HighestCellSeqLow32 = 77u; CellsRx10ms = 35us; Late10ms = 1us; OwdDeltaUs = -1500 }
    WinAck.writePathStat 1 p (Span b)
    let mutable r = Unchecked.defaultof<WinAckHead>
    Assert.True(WinAck.tryReadHead (ReadOnlySpan b) &r)
    Assert.Equal(h, r)
    Assert.Equal(p, WinAck.readPathStat (ReadOnlySpan b) 1)
    b.[32] <- 79uy
    Assert.False(WinAck.tryReadHead (ReadOnlySpan b) &r)

let private refusesSeqZero (buffer: byte[]) =
    try
        (Ctrl.write 0u CtrlOp.KeyReady (Span buffer)).Length < 0
    with :? ArgumentException -> true

[<Fact>]
let ``CTRL enforces exact body lengths and zero tail`` () =
    let b = Array.zeroCreate<byte> 1344
    let body = Ctrl.write 1u CtrlOp.KeyPrepare (Span b)
    Assert.Equal(12, body.Length)
    let mutable h = Unchecked.defaultof<CtrlHead>
    Assert.True(Ctrl.tryReadHead (ReadOnlySpan b) &h)
    Assert.Equal(CtrlOp.KeyPrepare, h.Op)
    b.[6] <- 13uy
    Assert.False(Ctrl.tryReadHead (ReadOnlySpan b) &h)
    b.[6] <- 12uy
    b.[100] <- 1uy
    Assert.False(Ctrl.tryReadHead (ReadOnlySpan b) &h)
    b.[100] <- 0uy
    b.[4] <- 10uy
    Assert.False(Ctrl.tryReadHead (ReadOnlySpan b) &h)
    Assert.True(refusesSeqZero (Array.zeroCreate 64), "control_seq 0 must be refused")

[<Fact>]
let ``FLOW_STATE format 2 counts 256 flows and rejects half-valid pages`` () =
    Assert.Equal(66, FlowState.maxRecords 1344)
    Assert.Equal(59, FlowState.maxRecords 1216)
    let b = Array.zeroCreate<byte> 1344
    let head n total snapshot last = { Snapshot = snapshot; LastPage = last; RecordCount = n; TotalFlows = total; PageIndex = 0
                                       StateSeq = 9u; StateEpoch = 0u; SnapshotId = (if snapshot then 9u else 0u); AllKindsKnown = true }
    // Empty snapshot: one page, total 0.
    FlowState.writeHead (head 0 0 true true) (Span b)
    let mutable h = Unchecked.defaultof<FlowStateHead>
    Assert.True(FlowState.tryReadHead (ReadOnlySpan b) &h)
    Assert.Equal(0, h.TotalFlows)
    // A full snapshot page claiming 256 flows is representable (u16 count).
    FlowState.writeHead (head 2 256 true false) (Span b)
    FlowState.writeFlow 0 { State = FlowLifecycle.Open; FlowSlot = 255; FlowGeneration = 1u; DeliveredOffset = 10UL; CreditBytes = 65536u } (Span b)
    FlowState.writeSession 1 { InteractiveActive = true; InteractiveSourceCredit = 64us; MicroCredit = 64us; PoolFreeKiB = 1024u } (Span b)
    Assert.True(FlowState.tryReadHead (ReadOnlySpan b) &h)
    Assert.Equal(256, h.TotalFlows)
    Assert.Equal(255, (FlowState.readFlow (ReadOnlySpan b) 0).FlowSlot)
    // A repeated FLOW key rejects the whole page.
    FlowState.writeFlow 1 { State = FlowLifecycle.Open; FlowSlot = 255; FlowGeneration = 1u; DeliveredOffset = 11UL; CreditBytes = 1u } (Span b)
    Assert.False(FlowState.tryReadHead (ReadOnlySpan b) &h)
    // Offset overflow rejects the page.
    FlowState.writeFlow 1 { State = FlowLifecycle.Open; FlowSlot = 3; FlowGeneration = 1u; DeliveredOffset = UInt64.MaxValue; CreditBytes = 1u } (Span b)
    Assert.False(FlowState.tryReadHead (ReadOnlySpan b) &h)
    // An increment must not carry snapshot fields.
    FlowState.writeHead { head 0 5 false false with SnapshotId = 0u } (Span b)
    Assert.False(FlowState.tryReadHead (ReadOnlySpan b) &h)
