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
    set32 68 40_000
    set32 76 64
    set32 80 80
    set32 100 3
    set64 104 30_000_000L
    set64 120 9_000_000L
    set32 144 1000
    set64 160 25_000_000L
    set64 168 300_000L
    set64 176 80_000L
    set64 184 12_000L
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
[<InlineData(120, 128)>]
[<InlineData(144, 148)>]
[<InlineData(160, 168)>]
[<InlineData(176, 184)>]
[<InlineData(184, 192)>]
[<InlineData(228, 232)>]
[<InlineData(240, 242)>]
let ``an optional field is unknown until the kernel returns all its bytes`` offset fieldEnd =
    let bytes = snapshotBytes ()
    let field (info: KernelTcpInfo) =
        match offset with
        | 120 -> info.BytesAcked
        | 144 -> info.NotSentBytes
        | 160 -> info.DeliveryRate
        | 176 -> info.RwndLimitedUs
        | 184 -> info.SendBufferLimitedUs
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
