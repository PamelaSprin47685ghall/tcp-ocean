module Tbt.Tests.TunTests

open System
open System.Buffers.Binary
open System.Collections.Generic
open Xunit
open Tbt.Carrier
open Tbt.Tests.SessionTests

let serverIp = 0x0AC80302u
let clientIp = 0x0AC80102u
let L = 1415

[<Sealed>]
type RecordingOutput() =
    let scratch = Array.zeroCreate<byte> 65536
    member val Packets = List<byte[]>()
    interface ITunOutput with
        member o.Write(p) = o.Packets.Add(p.ToArray())
        member _.Scratch = scratch

[<Sealed>]
type RecordingOwner() =
    member val Frames = List<byte[]>()
    member val Fills = 0 with get, set
    interface IPathOwner with
        member o.OnFrame(_, frame) =
            o.Frames.Add(frame.ToArray())
            0UL
        member o.ProduceFill(_, frame) =
            o.Fills <- o.Fills + 1
            frame.Fill 0xABuy
        member _.Frontier = 0UL

let parse (p: byte[]) =
    let mutable s = Unchecked.defaultof<Packet.Segment>
    Assert.True(Packet.tryParse (ReadOnlySpan p) &s)
    s

/// A client segment carrying payload, with optional TCP options.
let clientSegment (seq: uint32) (ack: uint32) (flags: byte) (options: byte[]) (payload: ReadOnlySpan<byte>) =
    let p = Array.zeroCreate<byte> (40 + options.Length + payload.Length)
    payload.CopyTo(Span(p, 40 + options.Length, payload.Length))
    let n = Packet.build (Span p) clientIp serverIp 40000us 8443us seq ack flags 65535us (ReadOnlySpan options) payload.Length 1us
    Assert.Equal(p.Length, n)
    let mutable s = Unchecked.defaultof<Packet.Segment>
    Assert.True(Packet.tryParse (ReadOnlySpan p) &s)
    struct (s, p)

let synOptions = [| 2uy; 4uy; 0x05uy; 0xB4uy; 1uy; 1uy; 4uy; 2uy; 1uy; 3uy; 3uy; 7uy |]

/// A connection through the head and the bootstrap frame, attached, with frames
/// 0..frames-1 of distinct content ready as the client's byte stream.
type Fixture(frames: int) =
    let clock = ManualClock()
    let out = RecordingOutput()
    let owner = RecordingOwner()
    let path = TunPath(out, TunCounters(), serverIp, 8443us, clientIp, 40000us, 1000u, clock)
    let clientIsn = 5000u
    let head = "POST / HTTP/1.1\r\nHost: x\r\n\r\n"B
    let boot = Array.init Tbt.Contracts.Layout.BootFrameLength (fun i -> byte (i * 7))
    let stream = Array.init (frames * L) (fun i -> byte (i / L + 1))
    do
        let struct (syn, sp) = clientSegment clientIsn 0u Packet.Syn synOptions ReadOnlySpan.Empty
        path.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    member _.Clock = clock
    member _.Out = out
    member _.Owner = owner
    member _.Path = path
    member _.Stream = stream
    member _.Head = head
    member _.Boot = boot
    /// Client sequence number of the first stream byte after the bootstrap frame.
    member _.Data0 = clientIsn + 1u + uint32 head.Length + uint32 boot.Length
    member f.Send(seq: uint32, payload: ReadOnlySpan<byte>, options: byte[]) =
        let struct (s, p) = clientSegment seq 1001u Packet.Ack options payload
        path.OnSegment(s, Span p, clock.Now)
    member f.Open() =
        let synAck = parse out.Packets.[0]
        Assert.Equal(Packet.Syn ||| Packet.Ack, synAck.Flags)
        Assert.Equal(clientIsn + 1u, synAck.AckNo)
        let hb = Array.append head boot
        f.Send(clientIsn + 1u, ReadOnlySpan hb, [||])
        Assert.Equal(1, path.BootState)
        Assert.Equal<byte[]>(boot, path.BootFrame.ToArray())
        let ip = path :> IPath
        ip.FrameLength <- L
        ip.Attach owner
        path.FlushAck()

[<Fact>]
let ``packets built here parse back with valid checksums`` () =
    let payload = Array.init 1001 byte
    let struct (s, _) = clientSegment 0xFFFFFFF0u 77u (Packet.Ack ||| Packet.Psh) [| 1uy; 1uy; 1uy; 1uy |] (ReadOnlySpan payload)
    Assert.Equal(0xFFFFFFF0u, s.Seq)
    Assert.Equal(77u, s.AckNo)
    Assert.Equal(1001, s.PayloadLength)
    Assert.Equal(4, s.OptionsLength)

[<Fact>]
let ``uplink: frames after a hole come out at once, the hole is swallowed after the grace`` () =
    let f = Fixture 10
    f.Open()
    let segs = [ for i in 0 .. (f.Stream.Length - 1) / 1460 -> i ]
    let send i =
        f.Send(f.Data0 + uint32 (i * 1460), ReadOnlySpan(f.Stream, i * 1460, min 1460 (f.Stream.Length - i * 1460)), [||])
    // Segment 1 is lost; the others arrive.
    send 0
    f.Path.FlushAck()
    let acksBefore = f.Out.Packets.Count
    for i in segs do if i >= 2 then send i
    let lostLo, lostHi = 1460, 2920
    let expected = [ for k in 0 .. 9 do if k * L >= lostHi || (k + 1) * L <= lostLo then yield k ]
    let got = [ for fr in f.Owner.Frames -> int fr.[0] - 1 ] |> List.sort
    Assert.Equal<int list>(expected, got)
    // Inside the grace: no duplicate ACK, no SACK.
    f.Path.FlushAck()
    Assert.Equal(acksBefore, f.Out.Packets.Count)
    f.Clock.Now <- f.Clock.Now + 16_000_000L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    let ack = parse f.Out.Packets.[f.Out.Packets.Count - 1]
    Assert.Equal(f.Data0 + uint32 f.Stream.Length, ack.AckNo)
    Assert.Equal(1L, f.Path.Counters.VirtualAcks)
    Assert.True(f.Path.Counters.CellsDropped >= 2L)

[<Fact>]
let ``downlink: a hole shown by SACK is refilled at once with new content, two copies`` () =
    let f = Fixture 1
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let starts = List<uint32>()
    for _ in 1 .. 3 do
        f.Clock.Now <- f.Clock.Now + 20_000_000L
        let mutable frame = Span<byte>.Empty
        Assert.True(ip.TryBeginFrame(&frame))
        frame.Fill 0x11uy
        ip.CommitFrame()
        starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
    let n = f.Out.Packets.Count
    // The client holds frames 0 and 2: cumulative ACK past 0, SACK for 2.
    let o = Array.zeroCreate<byte> 12
    o.[0] <- 1uy
    o.[1] <- 1uy
    o.[2] <- 5uy
    o.[3] <- 10uy
    BinaryPrimitives.WriteUInt32BigEndian(Span(o, 4, 4), starts.[2])
    BinaryPrimitives.WriteUInt32BigEndian(Span(o, 8, 4), starts.[2] + uint32 L)
    f.Clock.Now <- f.Clock.Now + 40_000_000L
    let struct (s, p) = clientSegment (f.Data0) (starts.[1]) Packet.Ack o ReadOnlySpan.Empty
    f.Path.OnSegment(s, Span p, f.Clock.Now)
    Assert.Equal(1, f.Owner.Fills)
    let repairs = [ for i in n .. f.Out.Packets.Count - 1 -> parse f.Out.Packets.[i], f.Out.Packets.[i] ]
    Assert.Equal(2, repairs.Length)
    for (r, bytes) in repairs do
        Assert.Equal(starts.[1], r.Seq)
        Assert.Equal(L, r.PayloadLength)
        Assert.True(bytes.AsSpan(r.PayloadAt, r.PayloadLength).IndexOfAnyExcept(0xABuy) < 0)
