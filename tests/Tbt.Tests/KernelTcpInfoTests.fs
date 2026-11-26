module Tbt.Tests.KernelTcpInfoTests

open System
open System.Net
open System.Net.Sockets
open Xunit
open Tbt.Carrier

let private snapshotBytes () =
    let bytes = Array.zeroCreate<byte> 256
    let set32 at value = BitConverter.TryWriteBytes(Span(bytes, at, 4), uint32 value) |> ignore
    let set64 at value = BitConverter.TryWriteBytes(Span(bytes, at, 8), uint64 value) |> ignore
    bytes.[1] <- 2uy
    bytes.[7] <- 1uy
    set32 8 240_000
    set32 16 1448
    set32 24 5
    set32 28 7
    set32 32 5
    set32 36 3
    set32 68 40_000
    set32 76 64
    set32 80 80
    set32 100 3
    set64 104 30_000_000L
    set64 120 9_000_000L
    set32 136 1000
    set32 144 1000
    set32 148 20_000
    set32 156 900
    set64 160 25_000_000L
    set64 168 300_000L
    set64 176 80_000L
    set64 184 12_000L
    set32 192 4000
    set64 200 1_500_000L
    set64 208 21_000L
    set32 216 2
    set32 220 11
    set32 228 600_000
    BitConverter.TryWriteBytes(Span(bytes, 240, 2), 2us) |> ignore
    bytes

[<Fact>]
let ``a short TCP_INFO cannot claim zero retransmissions or zero RTO`` () =
    let bytes = snapshotBytes ()
    for length in 0 .. 103 do
        Assert.True((KernelTcpInfo.parse(ReadOnlySpan(bytes, 0, length))).IsNone)
    let old = (KernelTcpInfo.parse(ReadOnlySpan(bytes, 0, 104))).Value
    Assert.Equal(3L, old.TotalRetrans)
    Assert.Equal(-1L, old.TotalRto)
    Assert.Equal(-1L, old.BytesAcked)
    Assert.Equal(-1L, old.PacingRate)
    Assert.Equal(-1L, old.AppLimited)
    Assert.Equal(80L * 1448L, old.CwndBytes)
    Assert.Contains("/-/", KernelTcpInfo.describePath old)

[<Theory>]
// Fields below offset 104 are inside the mandatory prefix: `parse` refuses a
// shorter record (the fact above), so their optional-field rows would read
// ValueNone. Only fields whose end lies at or past the prefix can be unknown.
[<InlineData(120, 128)>]
[<InlineData(136, 140)>]
[<InlineData(144, 148)>]
[<InlineData(148, 152)>]
[<InlineData(156, 160)>]
[<InlineData(160, 168)>]
[<InlineData(176, 184)>]
[<InlineData(184, 192)>]
[<InlineData(192, 196)>]
[<InlineData(200, 208)>]
[<InlineData(208, 216)>]
[<InlineData(216, 220)>]
[<InlineData(220, 224)>]
[<InlineData(228, 232)>]
[<InlineData(240, 242)>]
let ``an optional field is unknown until the kernel returns all its bytes`` offset fieldEnd =
    let bytes = snapshotBytes ()
    let field (info: KernelTcpInfo) =
        match offset with
        | 28 -> info.Sacked
        | 32 -> info.Lost
        | 36 -> info.Retrans
        | 120 -> info.BytesAcked
        | 136 -> info.SegsOut
        | 144 -> info.NotSentBytes
        | 148 -> info.MinRttUs
        | 156 -> info.DataSegsOut
        | 160 -> info.DeliveryRate
        | 176 -> info.RwndLimitedUs
        | 184 -> info.SendBufferLimitedUs
        | 192 -> info.Delivered
        | 200 -> info.BytesSent
        | 208 -> info.BytesRetrans
        | 216 -> info.DsackDups
        | 220 -> info.ReordSeen
        | 228 -> info.SendWindowBytes
        | _ -> info.TotalRto
    for length in offset .. fieldEnd - 1 do
        let info = (KernelTcpInfo.parse(ReadOnlySpan(bytes, 0, length))).Value
        Assert.Equal(-1L, field info)
    let complete = (KernelTcpInfo.parse(ReadOnlySpan(bytes, 0, fieldEnd))).Value
    Assert.True(field complete > 0L)

[<Fact>]
let ``a TCP snapshot keeps byte units and separates RTO count from timer interval`` () =
    let info = (KernelTcpInfo.parse(ReadOnlySpan(snapshotBytes ()))).Value
    Assert.Equal(80L * 1448L, info.CwndBytes)
    Assert.Equal(64L * 1448L, info.SsthreshBytes)
    Assert.Equal(5L * 1448L, info.UnackedBytes)
    Assert.Equal(600_000L, info.SendWindowBytes)
    Assert.Equal(240_000L, info.RtoUs)
    Assert.Equal(2L, info.TotalRto)
    Assert.Equal(25_000_000L, info.DeliveryRate)
    Assert.Equal(30_000_000L, info.PacingRate)
    Assert.Equal(80_000L, info.RwndLimitedUs)
    Assert.Equal(12_000L, info.SendBufferLimitedUs)
    Assert.Equal(1L, info.AppLimited)

[<Fact>]
let ``an unlimited kernel pacing rate is unknown instead of a negative rate`` () =
    let bytes = snapshotBytes ()
    BitConverter.TryWriteBytes(Span(bytes, 104, 8), UInt64.MaxValue) |> ignore
    let info = (KernelTcpInfo.parse(ReadOnlySpan bytes)).Value
    Assert.Equal(-1L, info.PacingRate)
    let totals = KernelTcpTotals()
    totals.Add(ValueSome info)
    Assert.Contains(" kernel_pacing_mbit=-", totals.Describe())

[<Fact>]
let ``a missing path cannot become a valid partial aggregate`` () =
    let info = KernelTcpInfo.parse(ReadOnlySpan(snapshotBytes ()))
    let totals = KernelTcpTotals()
    totals.Add info
    totals.Add ValueNone
    totals.Add info
    Assert.Equal(3, totals.Count)
    Assert.Equal(-1L, totals.BytesAcked)
    Assert.Contains(" kernel_retrans=-", totals.Describe())
    Assert.Contains(" kernel_rto=-", totals.Describe())
    Assert.Contains(" kernel_app_limited=-", totals.Describe())

[<Fact>]
let ``kernel sender totals stay independent in asymmetric directions`` () =
    let bytes = snapshotBytes ()
    let fast = KernelTcpInfo.parse(ReadOnlySpan bytes)
    BitConverter.TryWriteBytes(Span(bytes, 160, 8), 2_500_000UL) |> ignore
    let slow = KernelTcpInfo.parse(ReadOnlySpan bytes)
    let up = KernelTcpTotals()
    let down = KernelTcpTotals()
    up.Add slow
    down.Add fast
    Assert.Contains(" kernel_delivery_mbit=20.000", up.Describe())
    Assert.Contains(" kernel_delivery_mbit=200.000", down.Describe())
    up.Add slow
    Assert.Contains(" kernel_delivery_mbit=40.000", up.Describe())
    Assert.Contains(" kernel_delivery_mbit=200.000", down.Describe())

[<Fact>]
let ``reading a live kernel socket leaves its byte stream and options intact`` () =
    if Platform.current.Value.HasLinuxTcpInfo then
        use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
        listener.Listen 1
        use sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        sender.Connect(nonNull listener.LocalEndPoint)
        use receiver = listener.Accept()
        sender.SendTimeout <- 2000
        receiver.ReceiveTimeout <- 2000
        let before = sender.NoDelay
        let first = KernelTcpInfo.tryRead sender
        Assert.True(first.IsSome)
        let payload = [| 4uy; 3uy; 2uy; 1uy |]
        Assert.Equal(payload.Length, sender.Send payload)
        let result = Array.zeroCreate<byte> payload.Length
        let mutable received = 0
        while received < result.Length do
            let count = receiver.Receive(Span(result, received, result.Length - received))
            Assert.True(count > 0)
            received <- received + count
        Assert.Equal<byte>(payload, result)
        let after = KernelTcpInfo.tryRead sender
        Assert.True(after.IsSome)
        Assert.True(after.Value.CwndBytes > 0L)
        Assert.True(after.Value.BytesAcked >= first.Value.BytesAcked)
        Assert.Equal(before, sender.NoDelay)

[<Fact>]
let ``a small kernel ceiling lowers the flow receive request`` () =
    Assert.Equal(212_992, SocketTuning.flowRecvBufFor 212_992)
    Assert.Equal(1_000_000, SocketTuning.flowRecvBufFor 1_000_000)

[<Fact>]
let ``an unknown or ample ceiling keeps the full receive request`` () =
    Assert.Equal(SocketTuning.FlowRecvBuf, SocketTuning.flowRecvBufFor -1)
    Assert.Equal(SocketTuning.FlowRecvBuf, SocketTuning.flowRecvBufFor 0)
    Assert.Equal(SocketTuning.FlowRecvBuf, SocketTuning.flowRecvBufFor SocketTuning.FlowRecvBuf)
    Assert.Equal(SocketTuning.FlowRecvBuf, SocketTuning.flowRecvBufFor 4_194_304)

[<Fact>]
let ``async tx slots hold a full burst on a long-RTT path`` () =
    // The ring is the only place a frame waits before send(2), so it caps a path at
    // slots * frameLength / RTT. 16 slots (the old default) is 32 KiB, i.e. 17 Mbit/s on the
    // live 341 ms path — exactly the 19.4 Mbit/s the real 24 %-loss run measured while the
    // carrier itself was saturated. The ceiling has to be above that.
    Assert.Equal(16, SocketTuning.txSlotsFor 0)
    Assert.Equal(64, SocketTuning.txSlotsFor 64)
    Assert.Equal(128, SocketTuning.txSlotsFor 128)
    Assert.Equal(256, SocketTuning.txSlotsFor 512)

[<Fact>]
let ``window clamp failures are reported and never claimed as applied`` () =
    let socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    socket.Dispose()
    let r = SocketTuning.applyWindowClamp(socket, 600000)
    Assert.Equal(600000, r.Requested)
    Assert.False(r.Applied)
    Assert.False(String.IsNullOrWhiteSpace r.Error)

[<Fact>]
let ``linux stores twice the requested receive buffer`` () =
    Assert.Equal(8_000_000, SocketTuning.flowRecvWinFor SocketTuning.FlowRecvBuf)
    Assert.Equal(425_984, SocketTuning.flowRecvWinFor 212_992)
    Assert.Equal(0, SocketTuning.flowRecvWinFor 0)

[<Fact>]
let ``the Linux 6 diagnostic tail is parsed at its documented offsets`` () =
    let bytes = Array.zeroCreate<byte> 280
    let set32 at value = BitConverter.TryWriteBytes(Span(bytes, at, 4), uint32 value) |> ignore
    let set64 at value = BitConverter.TryWriteBytes(Span(bytes, at, 8), uint64 value) |> ignore
    set32 16 1448
    set32 28 7
    set32 32 5
    set32 36 3
    set32 136 1000
    set32 148 20_000
    set32 156 900
    set32 192 4000
    set64 200 1_500_000UL
    set64 208 21_000UL
    set32 216 2
    set32 220 11
    let info = (KernelTcpInfo.parse(ReadOnlySpan bytes)).Value
    Assert.Equal(7L, info.Sacked)
    Assert.Equal(5L, info.Lost)
    Assert.Equal(3L, info.Retrans)
    Assert.Equal(1000L, info.SegsOut)
    Assert.Equal(20_000L, info.MinRttUs)
    Assert.Equal(900L, info.DataSegsOut)
    Assert.Equal(4000L, info.Delivered)
    Assert.Equal(1_500_000L, info.BytesSent)
    Assert.Equal(21_000L, info.BytesRetrans)
    Assert.Equal(2L, info.DsackDups)
    Assert.Equal(11L, info.ReordSeen)
    // Both spellings read the same snapshot field.
    Assert.Equal(info.Lost, info.LostSegs)
    Assert.Equal(info.Retrans, info.RetransSegs)

[<Fact>]
let ``a truncated record leaves the diagnostic tail unknown instead of zero`` () =
    let bytes = Array.zeroCreate<byte> 280
    BitConverter.TryWriteBytes(Span(bytes, 136, 4), 1000u) |> ignore
    let short = (KernelTcpInfo.parse(ReadOnlySpan(bytes, 0, 140))).Value
    Assert.Equal(1000L, short.SegsOut)
    Assert.Equal(-1L, short.MinRttUs)
    Assert.Equal(-1L, short.BytesSent)
    Assert.Equal(-1L, short.ReordSeen)
    // An incomplete sample must never rank as healthy.
    Assert.False((PathHealth.ofInfo 1L short 0.0).Valid)

[<Fact>]
let ``a health snapshot clamps unknown fields and carries a retransmit ratio`` () =
    let bytes = Array.zeroCreate<byte> 280
    let set32 at value = BitConverter.TryWriteBytes(Span(bytes, at, 4), uint32 value) |> ignore
    set32 16 1448
    set32 80 40
    set32 100 9
    set32 136 500
    set32 148 30_000
    let info = (KernelTcpInfo.parse(ReadOnlySpan bytes)).Value
    let health = PathHealth.ofInfo 12L info 0.25
    Assert.True health.Valid
    Assert.Equal(12L, health.SampledNs)
    Assert.Equal(uint32 (40 * 1448), health.CwndBytes)
    Assert.Equal(30_000u, health.MinRttUs)
    Assert.Equal(9u, health.TotalRetrans)
    Assert.Equal(0.25, health.RetransRate)
    Assert.False PathHealth.unknown.Valid
