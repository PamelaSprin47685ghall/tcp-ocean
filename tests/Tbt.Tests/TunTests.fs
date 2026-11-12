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
type Fixture(frames: int, clock: ManualClock, group: RateGroup, clientPort: uint16) =
    let out = RecordingOutput()
    let owner = RecordingOwner()
    let path = TunPath(out, TunCounters(), serverIp, 8443us, clientIp, clientPort, 1000u, clock, group)
    let clientIsn = 5000u
    let head = "POST / HTTP/1.1\r\nHost: x\r\n\r\n"B
    let boot = Array.init Tbt.Contracts.Layout.BootFrameLength (fun i -> byte (i * 7))
    let stream = Array.init (frames * L) (fun i -> byte (i / L + 1))
    do
        let struct (syn, sp) = clientSegment clientIsn 0u Packet.Syn synOptions ReadOnlySpan.Empty
        path.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    new(frames: int) =
        let clock = ManualClock()
        Fixture(frames, clock, RateGroup(clock), 40000us)
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

let sackOption (blocks: (uint32 * uint32) list) =
    let o = Array.zeroCreate<byte> (4 + 8 * blocks.Length)
    o.[0] <- 1uy
    o.[1] <- 1uy
    o.[2] <- 5uy
    o.[3] <- byte (2 + 8 * blocks.Length)
    blocks |> List.iteri (fun i (a, b) ->
        BinaryPrimitives.WriteUInt32BigEndian(Span(o, 4 + 8 * i, 4), a)
        BinaryPrimitives.WriteUInt32BigEndian(Span(o, 8 + 8 * i, 4), b))
    o

[<Fact>]
let ``downlink: a late ACK of a repaired segment's first copy does not move the RACK time`` () =
    // Scripted sender: frames 0..3 every 20 ms; frame 1 is reported missing (SACK of 2) and
    // repaired; 1 ms later the ACK covers frame 1: that ACK is for its first copy (no copy
    // could arrive in 1 ms). RFC 8985 6.2: the RACK time stays at frame 2's send time, so
    // segments sent between frame 2 and the repair are not declared lost on the next SACK.
    let f = Fixture 1
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let starts = List<uint32>()
    let sentAt = List<int64>()
    let send () =
        f.Clock.Now <- f.Clock.Now + 20_000_000L
        let mutable frame = Span<byte>.Empty
        Assert.True(ip.TryBeginFrame(&frame))
        frame.Fill 0x22uy
        ip.CommitFrame()
        starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
        sentAt.Add f.Clock.Now
    for _ in 0 .. 3 do send ()
    let ack (cum: uint32) (blocks: (uint32 * uint32) list) =
        let struct (s, p) = clientSegment f.Data0 cum Packet.Ack (sackOption blocks) ReadOnlySpan.Empty
        f.Path.OnSegment(s, Span p, f.Clock.Now)
    let l = uint32 L
    // Frame 0 acked at +45 ms after it was sent: min RTT 45 ms.
    f.Clock.Now <- sentAt.[0] + 45_000_000L
    ack (starts.[0] + l) []
    // Frame 2 SACKed: frame 1 is lost, repaired now.
    f.Clock.Now <- sentAt.[2] + 45_000_000L
    let lost0 = f.Path.Counters.SegmentsLost
    ack (starts.[0] + l) [ (starts.[2], starts.[2] + l) ]
    Assert.Equal(lost0 + 1L, f.Path.Counters.SegmentsLost)
    Assert.Equal(sentAt.[2], f.Path.RackSentNs)
    // 1 ms later the first copy of frame 1 is acked (late, not lost).
    f.Clock.Now <- f.Clock.Now + 1_000_000L
    ack (starts.[2] + l) []
    Assert.Equal(sentAt.[2], f.Path.RackSentNs)

[<Fact>]
let ``handshake: a repeated SYN gets the same SYN-ACK options (MSS, SACK, window scale)`` () =
    let f = Fixture 1
    let first = f.Out.Packets.[0]
    let struct (syn, sp) = clientSegment 5000u 0u Packet.Syn synOptions ReadOnlySpan.Empty
    f.Path.OnSegment(syn, Span sp, f.Clock.Now)
    Assert.Equal(2, f.Out.Packets.Count)
    let a = parse first
    let b = parse f.Out.Packets.[1]
    Assert.Equal(Packet.Syn ||| Packet.Ack, b.Flags)
    Assert.True(a.OptionsLength > 0)
    Assert.Equal<byte[]>(first.[a.OptionsAt .. a.OptionsAt + a.OptionsLength - 1],
                         f.Out.Packets.[1].[b.OptionsAt .. b.OptionsAt + b.OptionsLength - 1])

[<Fact>]
let ``rate group: one path that never ACKs is demoted and the other paths keep sending`` () =
    // Two paths to one client share a group. Both carry bulk with ACKs after 40 ms; then
    // path B gets no ACK at all (as path 3 in the A/B run). Its in-flight bytes must not hold
    // the shared cwnd: path A goes on, B takes no new frames and keeps repairing.
    let clock = ManualClock()
    let group = RateGroup(clock)
    let fa = Fixture(1, clock, group, 40000us)
    let fb = Fixture(1, clock, group, 40001us)
    // Handshake RTT 40 ms, as the link in the e2e runs.
    clock.Now <- clock.Now + 40_000_000L
    fa.Open()
    fb.Open()
    group.Add(fa.Path :> IRateMember)
    group.Add(fb.Path :> IRateMember)
    let l = uint32 L
    let ack (f: Fixture) (cum: uint32) =
        let struct (s, p) = clientSegment f.Data0 cum Packet.Ack [||] ReadOnlySpan.Empty
        f.Path.OnSegment(s, Span p, clock.Now)
    let lanes =
        [| for f in [ fa; fb ] ->
            let ip = f.Path :> IPath
            ip.Backlog <- 100
            f, ip, List<uint32>(), List<int64>(), ref 0 |]
    let step (bAcks: bool) =
        clock.Now <- clock.Now + 1_000_000L
        let mutable sent = [| 0; 0 |]
        // B is asked first: a dead path that keeps taking new frames takes them from A.
        for k in [ 1; 0 ] do
            let f, ip, starts, sentAt, acked = lanes.[k]
            if k = 0 || bAcks then
                while acked.Value < sentAt.Count && clock.Now - sentAt.[acked.Value] >= 40_000_000L do
                    acked.Value <- acked.Value + 1
                if acked.Value > 0 then ack f (starts.[acked.Value - 1] + l)
            let mutable frame = Span<byte>.Empty
            let mutable go = true
            while go && ip.TryBeginFrame(&frame) do
                frame.Fill 0x44uy
                ip.CommitFrame()
                starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
                sentAt.Add clock.Now
                sent.[k] <- sent.[k] + 1
                if sent.[k] >= 4 then go <- false
            f.Path.Service clock.Now
        group.Service clock.Now
        sent
    for _ in 1 .. 1000 do step true |> ignore
    Assert.True(group.Ready, sprintf "not ready: %s; A %s; sent %d" (group.Describe()) (fa.Path.Describe()) (let _, _, s, _, _ = lanes.[0] in s.Count))
    let countA () = let _, _, starts, _, _ = lanes.[0] in starts.Count
    let countB () = let _, _, starts, _, _ = lanes.[1] in starts.Count
    // B goes dead.
    for _ in 1 .. 1500 do step false |> ignore
    let a0 = countA ()
    let b0 = countB ()
    for _ in 1 .. 1000 do step false |> ignore
    // The last second: A still sends at a real rate, B sends no new frames.
    Assert.True(countA () - a0 > 2000, sprintf "path A sent %d frames in the last second (at most 4000): %s" (countA () - a0) (group.Describe()))
    Assert.Equal(b0, countB ())
    Assert.True(fb.Path.Counters.Demotions >= 1L, sprintf "B not demoted: %s | A %s | B %s | A sent %d" (group.Describe()) (fa.Path.Describe()) (fb.Path.Describe()) (countA () - a0))
    Assert.True(fb.Path.Counters.TailRepairs > 0L, "B made no repairs")
