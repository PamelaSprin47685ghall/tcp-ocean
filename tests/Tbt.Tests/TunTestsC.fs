module Tbt.Tests.TunTestsC

open System
open System.Buffers.Binary
open System.Collections.Generic
open Xunit
open Tbt.Carrier
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests
open Tbt.Tests.TunTestsB

[<Fact>]
let ``a blackout on every path does not leave them demoted and dead`` () =
    let r = blackoutRun 2 0.0 1 (fun t -> t >= 500_000_000L && t < 1_800_000_000L) 4000
    let all = r |> List.map (fun (m, _, _) -> m) |> String.concat " || "
    for msg, last, demotions in r do
        let msg = all
        Assert.True(last > 3_500_000_000L, msg)
        // All paths stalled at once: an outage, not a dead path.
        Assert.True((0L = demotions), msg)
        Assert.DoesNotContain("demoted", msg)

[<Fact>]
let ``segments sent before a delivered repair are still re-sent when an outage silences every ACK`` () =
    // netns deadlock (demotions=4, no segment ever sent again): a repaired low segment was
    // delivered, so rackSentNs moved to its repair time. Higher segments sent before that
    // repair were neither below the highest SACK (RACK) nor sent after rackSentNs (tail
    // timer), and the outage left no later ACK to cover them: no repair, ever.
    let clock = ManualClock()
    let group = RateGroup(clock)
    let f = Fixture(1, clock, group, 40000us)
    clock.Now <- clock.Now + 40_000_000L
    f.Open()
    group.Add(f.Path :> IRateMember)
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let starts = List<uint32>()
    let mutable frame = Span<byte>.Empty
    for _ in 1 .. 10 do
        clock.Now <- clock.Now + 1_000_000L
        while starts.Count < 10 && ip.TryBeginFrame(&frame) do
            frame.Fill 0x55uy
            ip.CommitFrame()
            starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
    Assert.True(starts.Count >= 6, sprintf "%d frames" starts.Count)
    let h = starts.Count / 2
    let l = uint32 L
    let be (v: uint32) = [| byte (v >>> 24); byte (v >>> 16); byte (v >>> 8); byte v |]
    let ack (cum: uint32) (sack: byte[]) =
        let struct (s, p) = clientSegment f.Data0 cum Packet.Ack sack ReadOnlySpan.Empty
        f.Path.OnSegment(s, Span p, clock.Now)
    // 40 ms: the first half but 0 SACKed: RACK repairs 0.
    clock.Now <- clock.Now + 30_000_000L
    ack starts.[0] (Array.concat [ [| 1uy; 1uy; 5uy; 10uy |]; be starts.[1]; be (starts.[h - 1] + l) ])
    // 80 ms: the repair of 0 arrives, the first half acked; the rest was lost and the outage begins.
    clock.Now <- clock.Now + 40_000_000L
    ack (starts.[h - 1] + l) [||]
    let before = f.Out.Packets.Count
    for _ in 1 .. 2000 do
        clock.Now <- clock.Now + 1_000_000L
        f.Path.Service clock.Now
        group.Service clock.Now
    let resent = HashSet<uint32>()
    for i in before .. f.Out.Packets.Count - 1 do
        let q = parse f.Out.Packets.[i]
        if q.PayloadLength > 0 then resent.Add q.Seq |> ignore
    Assert.True(resent.Contains starts.[h], sprintf "re-sent %A; %s" (Seq.toList resent) (f.Path.Describe()))

[<Fact>]
let ``a peer without SACK still gets every hole re-sent after a blackout (RTO fallback)`` () =
    // A peer that disables SACK (or a middlebox that strips it) sends only cumulative ACKs:
    // no RACK, and the tail timer does not see the hole. Before the RTO fallback a path made
    // no progress for the 2.2 s after the blackout.
    let r = blackoutRunWith false 2 0.0 1 (fun t -> t >= 500_000_000L && t < 1_800_000_000L) 4000
    for msg, last, _ in r do
        Assert.True(last > 3_500_000_000L, msg)
        // Segments acked only when a repair fills the hole before them give no RTT sample
        // (main: srtt 1389 ms here).
        let srtt = float (System.Text.RegularExpressions.Regex.Match(msg, "srtt_ms=([0-9.]+)").Groups.[1].Value)
        Assert.True(srtt < 200.0, msg)

/// SACK blocks (left, right) of a parsed packet.
let sackBlocksOf (p: byte[]) =
    let s = parse p
    let o = ReadOnlySpan(p, s.OptionsAt, s.OptionsLength).ToArray()
    let mutable i = 0
    let blocks = List<uint32 * uint32>()
    while i < o.Length do
        match o.[i] with
        | 0uy -> i <- o.Length
        | 1uy -> i <- i + 1
        | k ->
            let len = int o.[i + 1]
            if k = 5uy then
                for b in 0 .. (len - 2) / 8 - 1 do
                    blocks.Add(BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(o, i + 2 + 8 * b, 4)),
                               BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan(o, i + 6 + 8 * b, 4)))
            i <- i + len
    List.ofSeq blocks

[<Fact>]
let ``uplink: the first late copy of a swallowed segment is no duplicate; a second copy is a D-SACK`` () =
    // Receive history, not the cumulative ACK, decides: swallowed bytes were acked but never
    // received, so their first arrival is new (RFC 2883 reports only real duplicates).
    let f = Fixture 10
    f.Open()
    let send i =
        f.Send(f.Data0 + uint32 (i * 1460), ReadOnlySpan(f.Stream, i * 1460, min 1460 (f.Stream.Length - i * 1460)), [||])
    send 0
    for i in 2 .. (f.Stream.Length - 1) / 1460 do send i
    f.Clock.Now <- f.Clock.Now + 16_000_000L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    let top = f.Data0 + uint32 f.Stream.Length
    Assert.Equal(top, (parse f.Out.Packets.[f.Out.Packets.Count - 1]).AckNo)
    Assert.Empty(sackBlocksOf f.Out.Packets.[f.Out.Packets.Count - 1])
    Assert.Equal(1L, f.Path.Counters.VirtualAcks)
    // The kernel's retransmission of the swallowed segment 1: its first copy here.
    let n = f.Out.Packets.Count
    send 1
    f.Path.FlushAck()
    Assert.Equal(n + 1, f.Out.Packets.Count)
    Assert.Empty(sackBlocksOf f.Out.Packets.[n])
    Assert.Equal(top, (parse f.Out.Packets.[n]).AckNo)
    Assert.Equal(1460L, f.Path.Counters.LateFirstBytes)
    Assert.Equal(0L, f.Path.Counters.Dsacks)
    // Its second copy did arrive before: a D-SACK.
    send 1
    f.Path.FlushAck()
    Assert.Equal<(uint32 * uint32) list>([ f.Data0 + 1460u, f.Data0 + 2920u ], sackBlocksOf f.Out.Packets.[f.Out.Packets.Count - 1])
    // A plain duplicate of received and acked data is one too.
    send 0
    f.Path.FlushAck()
    Assert.Equal<(uint32 * uint32) list>([ f.Data0, f.Data0 + 1460u ], sackBlocksOf f.Out.Packets.[f.Out.Packets.Count - 1])
    Assert.Equal(2L, f.Path.Counters.Dsacks)
    Assert.Equal(1460L, f.Path.Counters.LateFirstBytes)

[<Fact>]
let ``uplink: a swallowed hole is ACKed but never delivered, even when the original arrives late`` () =
    // Reproduces the 64-path mux-uplink conservation gap (~27% frames_out never
    // frames_in on 133e946 s7): after the 15 ms grace, the cumulative ACK jumps
    // over the hole (the client kernel would count those bytes in tcpi_bytes_acked)
    // but they are not in BytesAccepted and never reach OnFrame. A late first copy
    // is LateFirstBytes only — TunPath does not hand it to the frame reader
    // (spec 6.8: recovery is the source-retention lease / BOOSTER, not the original).
    let f = Fixture 10
    f.Open()
    let send i =
        f.Send(f.Data0 + uint32 (i * 1460), ReadOnlySpan(f.Stream, i * 1460, min 1460 (f.Stream.Length - i * 1460)), [||])
    send 0
    for i in 2 .. (f.Stream.Length - 1) / 1460 do send i
    let acceptedBefore = f.Path.BytesAccepted
    let framesBefore = f.Owner.Frames.Count
    f.Clock.Now <- f.Clock.Now + 16_000_000L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    let ack = parse f.Out.Packets.[f.Out.Packets.Count - 1]
    Assert.Equal(f.Data0 + uint32 f.Stream.Length, ack.AckNo)
    Assert.Equal(1L, f.Path.Counters.VirtualAcks)
    Assert.Equal(1460L, f.Path.Counters.BytesSwallowed)
    Assert.Equal(1460L, f.Path.BytesSwallowed)
    Assert.Equal(int64 f.Stream.Length, f.Path.BytesCumAcked)
    Assert.Equal(int64 f.Stream.Length - 1460L, f.Path.BytesAccepted)
    Assert.Equal(acceptedBefore, f.Path.BytesAccepted)
    // Frames that overlap the hole never arrive.
    let lostLo, lostHi = 1460, 2920
    let expected = [ for k in 0 .. 9 do if k * L >= lostHi || (k + 1) * L <= lostLo then yield k ]
    let got = [ for fr in f.Owner.Frames -> int fr.[0] - 1 ] |> List.sort
    Assert.Equal<int list>(expected, got)
    // The original segment later: counted, still not delivered.
    send 1
    Assert.Equal(1460L, f.Path.Counters.LateFirstBytes)
    Assert.Equal(framesBefore, f.Owner.Frames.Count)
    Assert.Equal(int64 f.Stream.Length - 1460L, f.Path.BytesAccepted)

[<Fact>]
let ``uplink: a duplicate above the cumulative ACK waits out the grace, then goes as a D-SACK`` () =
    let f = Fixture 10
    f.Open()
    let send i =
        f.Send(f.Data0 + uint32 (i * 1460), ReadOnlySpan(f.Stream, i * 1460, min 1460 (f.Stream.Length - i * 1460)), [||])
    send 0
    f.Path.FlushAck()
    let n = f.Out.Packets.Count
    send 3
    send 4
    f.Path.FlushAck()
    Assert.Equal(n, f.Out.Packets.Count)
    // Segment 4 again, inside the held interval: a duplicate, above the cumulative ACK. Its
    // RFC 2883 form names the holding SACK block, which would show the hole: nothing yet.
    send 4
    f.Path.FlushAck()
    Assert.Equal(n, f.Out.Packets.Count)
    // The grace ends, the gap is swallowed: now the duplicate is below the ACK, a lone D-SACK.
    f.Clock.Now <- f.Clock.Now + 16_000_000L
    f.Path.Service f.Clock.Now
    f.Path.FlushAck()
    Assert.Equal(n + 1, f.Out.Packets.Count)
    let a = f.Out.Packets.[n]
    Assert.Equal(f.Data0 + 5u * 1460u, (parse a).AckNo)
    Assert.Equal<(uint32 * uint32) list>([ f.Data0 + 4u * 1460u, f.Data0 + 5u * 1460u ], sackBlocksOf a)
    Assert.Equal(1L, f.Path.Counters.Dsacks)

[<Fact>]
let ``uplink: outside the lease an above-ACK duplicate uses the RFC 2883 two-block form`` () =
    // The peer's FIN is seen (out of order, after segment 6): the lease ends (spec 6.8.2),
    // so holes are reported by SACK at once.
    let f = Fixture 10
    f.Open()
    let send i =
        f.Send(f.Data0 + uint32 (i * 1460), ReadOnlySpan(f.Stream, i * 1460, min 1460 (f.Stream.Length - i * 1460)), [||])
    send 0
    send 2
    send 5
    let struct (fs, fp) = clientSegment (f.Data0 + uint32 (6 * 1460)) 1001u (Packet.Ack ||| Packet.Fin) [||] (ReadOnlySpan(f.Stream, 6 * 1460, 1460))
    f.Path.OnSegment(fs, Span fp, f.Clock.Now)
    f.Path.FlushAck()
    let n = f.Out.Packets.Count
    // Segment 6 again: D-SACK [6, 7), then its block [5, 7), then the other block [2, 3).
    send 6
    f.Path.FlushAck()
    Assert.Equal(n + 1, f.Out.Packets.Count)
    let b = sackBlocksOf f.Out.Packets.[n]
    let at k = f.Data0 + uint32 (k * 1460)
    Assert.Equal<(uint32 * uint32) list>([ (at 6, at 7); (at 5, at 7); (at 2, at 3) ], b)
    Assert.Equal(at 1, (parse f.Out.Packets.[n]).AckNo)

[<Fact>]
let ``uplink: no D-SACK to a peer that did not negotiate SACK`` () =
    let clock = ManualClock()
    let out = RecordingOutput()
    let path = TunPath(out, TunCounters(), serverIp, 8443us, clientIp, 40001us, 1000u, clock, RateGroup(clock))
    let noSack = [| 2uy; 4uy; 0x05uy; 0xB4uy; 1uy; 3uy; 3uy; 7uy |]
    let struct (syn, sp) = clientSegment 5000u 0u Packet.Syn noSack ReadOnlySpan.Empty
    path.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    let payload = Array.create 100 1uy
    let seg () =
        let struct (s, p) = clientSegment 5001u 1001u Packet.Ack [||] (ReadOnlySpan payload)
        path.OnSegment(s, Span p, clock.Now)
    seg ()
    path.FlushAck()
    seg ()
    path.FlushAck()
    Assert.Empty(sackBlocksOf out.Packets.[out.Packets.Count - 1])
    Assert.Equal(0L, path.Counters.Dsacks)

[<Fact>]
let ``uplink: the advertised window is the spec's 600 000 B per path and bytes past it are not taken`` () =
    // 4 MiB per path let the client kernel build a ~350 ms standing queue at the bottleneck.
    let f = Fixture 600
    f.Open()
    let ack = parse f.Out.Packets.[f.Out.Packets.Count - 1]
    let w = int ack.Window <<< 7
    Assert.True(w <= SocketTuning.WindowClamp && w > SocketTuning.WindowClamp - 128, sprintf "window %d" w)
    // A segment wholly past the window: nothing of it is delivered.
    let past = f.Data0 + uint32 SocketTuning.WindowClamp + 1460u
    f.Send(past, ReadOnlySpan(f.Stream, 0, 1460), [||])
    Assert.Empty(f.Owner.Frames)
    Assert.Equal(0L, f.Path.Counters.UpHoles)

[<Fact>]
let ``uplink: --window-clamp overrides the advertised path window`` () =
    // SYN-ACK advertises 65535 (unscaled); the clamp applies once the path is established.
    let clock = ManualClock()
    let out = RecordingOutput()
    let clamp = 4 * 1024 * 1024
    let path = TunPath(out, TunCounters(), serverIp, 8443us, clientIp, 40000us, 1000u, clock, RateGroup(clock), clamp)
    let struct (syn, sp) = clientSegment 5000u 0u Packet.Syn synOptions ReadOnlySpan.Empty
    path.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    let synAck = parse out.Packets.[0]
    let payload = [| 1uy |]
    let struct (ackPkt, ap) = clientSegment 5001u (synAck.Seq + 1u) (Packet.Ack ||| Packet.Psh) [||] (ReadOnlySpan payload)
    path.OnSegment(ackPkt, Span ap, clock.Now)
    path.FlushAck()
    let acks = [ for p in out.Packets -> parse p ]
    let ack = acks |> List.findBack (fun a -> a.Flags &&& Packet.Syn = 0uy && a.Flags &&& Packet.Ack <> 0uy)
    let w = int ack.Window <<< 7
    Assert.True(w <= clamp && w > clamp - 128, sprintf "window %d" w)
    // Default construction is unchanged.
    let d = Fixture 1
    d.Open()
    let dw = int (parse d.Out.Packets.[d.Out.Packets.Count - 1]).Window <<< 7
    Assert.True(dw <= SocketTuning.WindowClamp && dw > SocketTuning.WindowClamp - 128, sprintf "default window %d" dw)

// ---- repairs are paced (review item d) ---------------------------------------------------

/// Send frames on f for ms milliseconds (at most one per ms); their start seqs.
