module Tbt.Tests.TunAckSchedulingTests

open System
open Xunit
open Tbt.Carrier
open Tbt.Contracts
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests
open Tbt.Tests.TunTestsC

type private AckOwner() =
    member val Frontier = 0UL with get, set
    interface IPathOwner with
        member _.OnFrame(_, _) = 0UL
        member owner.Frontier = owner.Frontier
        member _.ProduceFill(_, frame) =
            frame.Clear()
            true
        member _.Fail(reason) = failwith reason

type private AckFixture(sack: bool) =
    let clock = ManualClock()
    let output = RecordingOutput()
    let owner = AckOwner()
    let path = TunPath(output, TunCounters(), serverIp, 8443us, clientIp, 40000us, 1000u, clock, RateGroup(clock))
    let head = "POST / HTTP/1.1\r\nHost: x\r\n\r\n"B
    let bootstrap = Array.append head (Array.zeroCreate<byte> Layout.BootFrameLength)
    let data0 = 5001u + uint32 bootstrap.Length
    let frame = Array.create L 0x11uy
    do
        let options = if sack then synOptions else [| 2uy; 4uy; 0x05uy; 0xB4uy; 1uy; 3uy; 3uy; 7uy |]
        path.OnSyn(5000u, ReadOnlySpan options, 1460, clock.Now)

    new() = AckFixture(true)
    member _.Clock = clock
    member _.Path = path
    member _.Out = output
    member _.Owner = owner
    member _.At(index: int) = data0 + uint32 (index * L)
    member _.LastPacket = output.Packets.[output.Packets.Count - 1]

    member _.SendPacket(seq: uint32, flags: byte, data: ReadOnlySpan<byte>) =
        let struct (segment, packet) = clientSegment seq 1001u flags [||] data
        path.OnSegment(segment, Span packet, clock.Now)

    member this.Bootstrap(complete: bool) =
        let length = if complete then bootstrap.Length else bootstrap.Length - 1
        this.SendPacket(5001u, Packet.Ack, ReadOnlySpan(bootstrap, 0, length))

    member this.Open() =
        this.Bootstrap true
        let link = path :> IPath
        link.FrameLength <- L
        link.Attach owner
        path.FlushAck()

    member this.SendFrame(index: int) =
        this.SendPacket(this.At index, Packet.Ack, ReadOnlySpan frame)

    member this.Gap() =
        this.SendFrame 0
        path.FlushAck()
        this.SendFrame 2

[<Fact>]
let ``a normal ACK still yields at once and the query has no send side effect`` () =
    let f = AckFixture()
    f.Open()
    f.SendFrame 0
    let before = f.Out.Packets.Count
    Assert.True(f.Path.AckPending)
    for _ in 1 .. 20 do Assert.True(f.Path.AckNeedsService f.Clock.Now)
    Assert.Equal(before, f.Out.Packets.Count)
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(f.At 1, (parse f.LastPacket).AckNo)
    Assert.False(f.Path.AckNeedsService f.Clock.Now)

[<Fact>]
let ``a lease gap does not yield until the exact grace deadline`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    let before = f.Out.Packets.Count
    Assert.True(f.Path.AckPending)
    for elapsed in [ 0L; 1L; 14_999_999L ] do
        Assert.False(f.Path.AckNeedsService(f.Clock.Now + elapsed))
    Assert.True(f.Path.AckNeedsService(f.Clock.Now + 15_000_000L))
    // A future query must not change the current receive frontier or its timers.
    Assert.False(f.Path.AckNeedsService f.Clock.Now)
    Assert.Equal(0L, f.Path.Counters.VirtualAcks)
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    f.Clock.Now <- f.Clock.Now + 15_000_000L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(f.At 3, (parse f.LastPacket).AckNo)
    Assert.Equal(1L, f.Path.Counters.VirtualAcks)
    Assert.False(f.Path.AckNeedsService f.Clock.Now)

[<Fact>]
let ``a below-ACK D-SACK still yields during a lease gap`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    f.SendFrame 0
    let before = f.Out.Packets.Count
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(f.At 1, (parse f.LastPacket).AckNo)
    Assert.Equal<(uint32 * uint32) list>([ (f.At 0, f.At 1) ], sackBlocksOf f.LastPacket)
    Assert.Equal(1L, f.Path.Counters.Dsacks)
    Assert.True(f.Path.AckPending)
    Assert.False(f.Path.AckNeedsService f.Clock.Now)

[<Fact>]
let ``an above-ACK D-SACK waits for the grace without forcing receive yields`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    f.SendFrame 2
    let before = f.Out.Packets.Count
    Assert.False(f.Path.AckNeedsService f.Clock.Now)
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    f.Clock.Now <- f.Clock.Now + 15_000_000L
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(f.At 3, (parse f.LastPacket).AckNo)
    Assert.Equal<(uint32 * uint32) list>([ (f.At 2, f.At 3) ], sackBlocksOf f.LastPacket)

[<Fact>]
let ``a peer without SACK does not make a duplicate eligible inside the grace`` () =
    let f = AckFixture(false)
    f.Open()
    f.Gap()
    f.SendFrame 0
    let before = f.Out.Packets.Count
    Assert.False(f.Path.AckNeedsService f.Clock.Now)
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    f.Clock.Now <- f.Clock.Now + 15_000_000L
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Empty(sackBlocksOf f.LastPacket)
    Assert.Equal(0L, f.Path.Counters.Dsacks)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``a bootstrap without an attached lease still sends its ACK`` complete =
    let f = AckFixture()
    f.Bootstrap complete
    f.Path.FlushAck()
    Assert.Equal((if complete then 1 else 0), f.Path.BootState)
    // Before Attach, later bytes cannot grant a lease or be virtually ACKed.
    f.SendFrame 2
    let before = f.Out.Packets.Count
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(0L, f.Path.Counters.VirtualAcks)
    Assert.True((parse f.LastPacket).AckNo <= f.At 0)

[<Fact>]
let ``an out-of-order FIN ends the lease and makes its gap ACK eligible`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    f.SendPacket(f.At 3, Packet.Ack ||| Packet.Fin, ReadOnlySpan.Empty)
    let before = f.Out.Packets.Count
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(f.At 1, (parse f.LastPacket).AckNo)
    Assert.Equal<(uint32 * uint32) list>([ (f.At 2, f.At 3) ], sackBlocksOf f.LastPacket)
    Assert.Equal(0L, f.Path.Counters.VirtualAcks)

[<Fact>]
let ``decode-frontier evidence still requests Service before the grace expires`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    // The next complete frame records the authenticated watermark for this gap.
    f.SendFrame 3
    Assert.False(f.Path.AckNeedsService f.Clock.Now)
    f.Owner.Frontier <- 1UL
    let before = f.Out.Packets.Count
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    Assert.Equal(0L, f.Path.Counters.FastSwallows)
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(before + 1, f.Out.Packets.Count)
    Assert.Equal(f.At 4, (parse f.LastPacket).AckNo)
    Assert.Equal(1L, f.Path.Counters.FastSwallows)

[<Fact>]
let ``a piggybacked ACK does not hide an expired gap from Service`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    let link = f.Path :> IPath
    let mutable frame = Span<byte>.Empty
    Assert.True(link.TryBeginFrame(&frame))
    frame.Clear()
    link.CommitFrame()
    Assert.False(f.Path.AckPending)
    Assert.False(f.Path.AckNeedsService f.Clock.Now)
    Assert.True(f.Path.AckNeedsService(f.Clock.Now + 15_000_000L))
    Assert.Equal(0L, f.Path.Counters.VirtualAcks)

[<Fact>]
let ``clock-jump queries leave grace adjustment to Service`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    let before = f.Out.Packets.Count
    f.Clock.Now <- f.Clock.Now + 400_000_000L
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    Assert.Equal(0L, f.Path.Counters.ClockJumps)
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    f.Path.Service f.Clock.Now
    Assert.Equal(1L, f.Path.Counters.ClockJumps)
    Assert.Equal(0L, f.Path.Counters.VirtualAcks)
    Assert.False(f.Path.AckNeedsService f.Clock.Now)
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
    f.Clock.Now <- f.Clock.Now + 14_000_000L
    Assert.True(f.Path.AckNeedsService f.Clock.Now)
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(1L, f.Path.Counters.VirtualAcks)
    Assert.Equal(before + 1, f.Out.Packets.Count)

[<Fact>]
let ``a closed path does not request ACK service`` () =
    let f = AckFixture()
    f.Open()
    f.Gap()
    f.Path.Abort()
    let before = f.Out.Packets.Count
    Assert.False(f.Path.AckPending)
    Assert.False(f.Path.AckNeedsService(f.Clock.Now + 15_000_000L))
    f.Path.FlushAck()
    Assert.Equal(before, f.Out.Packets.Count)
