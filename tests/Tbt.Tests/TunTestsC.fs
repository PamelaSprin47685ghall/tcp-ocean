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
let ``uplink: a spurious retransmit (D-SACK) widens the RACK window instead of amplifying repairs`` () =
    // On a 24 % loss link reordering is the norm. With a hardcoded 0 window every reordered
    // segment was declared lost, which multiplied repairs 1.5-2x on the wire. The RFC 8985
    // signal for "this path reorders" is the D-SACK: the peer reports bytes it already
    // acknowledged, so a repair was spurious. That must widen the window (capped at
    // min(minRtt/4, 20 ms)) instead of leaving RACK at zero and repairing again.
    let f = Fixture 1
    f.Open()
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let starts = List<uint32>()
    let mutable frame = Span<byte>.Empty
    for _ in 1 .. 8 do
        f.Clock.Now <- f.Clock.Now + 1_000_000L
        if ip.TryBeginFrame(&frame) then
            frame.Fill 0x55uy
            ip.CommitFrame()
            starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
    Assert.True(starts.Count >= 6, sprintf "%d frames" starts.Count)
    let l = uint32 L
    Assert.Equal(0L, f.Path.ReoWndNs)
    let rep0 = f.Path.Counters.RepairRequests
    // 30 ms later starts.[0] is cumulatively acked while the newest one is still unacked.
    f.Clock.Now <- f.Clock.Now + 30_000_000L
    let struct (s1, p1) = clientSegment f.Data0 (starts.[0] + l) Packet.Ack [||] ReadOnlySpan.Empty
    f.Path.OnSegment(s1, Span p1, f.Clock.Now)
    // A late copy of starts.[0] arrives (it was already acked): the peer sees a duplicate,
    // reports it in a block below sndUna — a D-SACK, the reordering evidence.
    let struct (s2, p2) = clientSegment f.Data0 (starts.[0] + l) Packet.Ack (sackOption [ (starts.[0], starts.[0] + l) ]) ReadOnlySpan.Empty
    f.Path.OnSegment(s2, Span p2, f.Clock.Now)
    Assert.True(f.Path.ReoWndNs > 0L, f.Path.Describe())
    Assert.True(f.Path.ReoWndNs <= 20_000_000L, sprintf "reo %d" f.Path.ReoWndNs)
    // One repair request per segment at most: the window must not turn reordering into a storm.
    Assert.True(f.Path.Counters.RepairRequests - rep0 <= int64 (starts.Count - 1), f.Path.Describe())

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
let ``uplink: a peer without window scale gets the unscaled 65535-byte window`` () =
    // Without a WS option the peer never scales the window, so advertising
    // 600000 >>> 7 = 4687 B would be a 4.6 KB window: about 41 KB/s on a 113 ms path.
    let clock = ManualClock()
    let out = RecordingOutput()
    let path = TunPath(out, TunCounters(), serverIp, 8443us, clientIp, 40002us, 1000u, clock, RateGroup(clock))
    let mssOnly = [| 2uy; 4uy; 0x05uy; 0xB4uy |]
    let struct (syn, sp) = clientSegment 7000u 0u Packet.Syn mssOnly ReadOnlySpan.Empty
    path.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    let payload = [| 1uy |]
    let struct (seg, p) = clientSegment 7001u 1001u (Packet.Ack ||| Packet.Psh) [||] (ReadOnlySpan payload)
    path.OnSegment(seg, Span p, clock.Now)
    path.FlushAck()
    let acks = [ for p in out.Packets -> parse p ]
    let ack = acks |> List.findBack (fun a -> a.Flags &&& Packet.Syn = 0uy && a.Flags &&& Packet.Ack <> 0uy)
    // The largest window TCP can express without scaling, not 600000 >>> 7 = 4687.
    Assert.Equal(65535, int ack.Window)

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

// ---- clock jumps: what the counter is, and what it is not -------------------------------

// A testbed that invalidates a segment whenever `clock_jumps` rose inside its window is trusting
// this counter to mean "a measured timeline was cut". The counter is raised by `catchUp`, which
// only compares the previous call **on this path** with now. These tests pin the three properties
// that decide how the aggregate may be read:
//
//   P1  it fires when the path has nothing in flight, so it is not evidence that a send timeline
//       was cut — only that this path was not visited for longer than the stall limit;
//   P2  the threshold is strictly greater than 300 ms;
//   P3  one gap is counted once per open path (TunStack.Service visits every connection on each
//       tick and they all read one clock), so the aggregate is path-multiplied: four paths show
//       +4 for a single gap. Multiplying the aggregate by 300 ms to obtain "stall seconds" is
//       therefore wrong by the path count and must not be done.

let private barePath (clock: ManualClock) =
    TunPath(RecordingOutput(), TunCounters(), serverIp, 8443us, clientIp, 40009us, 1000u, clock, RateGroup(clock))

[<Fact>]
let ``a service gap with nothing in flight is still counted as a clock jump`` () =
    let clock = ManualClock()
    let path = barePath clock
    path.Service clock.Now                      // baseline: seenNs = now0
    clock.Now <- clock.Now + 1_000_000_000L     // one second in which the path was never visited
    path.Service clock.Now
    Assert.Equal(1L, path.Counters.ClockJumps)
    // Nothing was ever sent, so there was no stamp to shift. The count says "not visited for
    // >300 ms", not "a segment's RTT was discarded".
    Assert.Equal(0L, path.Counters.SegmentsSent)

[<Fact>]
let ``the stall threshold is strictly greater than 300 ms`` () =
    let clock = ManualClock()
    let path = barePath clock
    path.Service clock.Now
    clock.Now <- clock.Now + 300_000_000L
    path.Service clock.Now
    Assert.Equal(0L, path.Counters.ClockJumps)
    clock.Now <- clock.Now + 300_000_001L
    path.Service clock.Now
    Assert.Equal(1L, path.Counters.ClockJumps)

[<Fact>]
let ``a gap with data in flight counts the same as a gap while idle`` () =
    // Same arithmetic in both states: the counter is blind to whether any stamp actually moved,
    // so its height cannot separate "nothing needed cutting" from "a cut happened".
    let clock = ManualClock()
    let path = barePath clock
    let struct (syn, sp) = clientSegment 5000u 0u Packet.Syn synOptions ReadOnlySpan.Empty
    path.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    path.Service clock.Now
    path.SendReliable(ReadOnlySpan(Array.create 3000 7uy))
    Assert.True(path.Counters.SegmentsSent > 0L)
    clock.Now <- clock.Now + 1_000_000_000L
    path.Service clock.Now
    Assert.Equal(1L, path.Counters.ClockJumps)

[<Fact>]
let ``one stack-loop gap is counted once per open path, so the sum is path-multiplied`` () =
    let clock = ManualClock()
    let paths = [ for _ in 1 .. 4 -> barePath clock ]
    for p in paths do p.Service clock.Now
    clock.Now <- clock.Now + 400_000_000L
    for p in paths do p.Service clock.Now
    Assert.All(paths, fun p -> Assert.Equal(1L, p.Counters.ClockJumps))
    // One gap, four increments: this is the +4 (and +8 for two gaps) the field log showed.
    Assert.Equal(4L, paths |> List.sumBy (fun p -> p.Counters.ClockJumps))

// ---- repairs are paced (review item d) ---------------------------------------------------

/// Send frames on f for ms milliseconds (at most one per ms); their start seqs.

// ---- service-loop gaps: length and what was outstanding at the time ----------------------
//
// `ClockJumps` alone cannot answer the question a testbed asks ("was this measurement window
// cut?"): it rises for a path with nothing outstanding too, where the shift moves no stamp.
// These tests pin the split, and pin what it may and may not claim.
//
// The sample is taken at the START of `catchUp`, so it describes the state *when the gap was
// noticed*, as left by whatever last touched this path. It does not describe the gap's interior:
// `OnSyn` mutates state without sampling, and nothing here can see what happened between two
// calls. The tests below therefore assert the entry-time classification only — and one of them
// shows a call in the middle of a would-be gap splitting it in two, so the counters never claim
// one long interior.

let private gapPath (clock: ManualClock) (counters: TunCounters) =
    TunPath(RecordingOutput(), counters, serverIp, 8443us, clientIp, 40009us, 1000u, clock, RateGroup(clock))

[<Fact>]
let ``a gap is measured as written, including gaps under the stall limit`` () =
    let clock = ManualClock()
    let counters = TunCounters()
    let p = gapPath clock counters
    p.Service clock.Now                       // baseline: seenNs = t0
    clock.Now <- clock.Now + 120_000_000L     // 120 ms, below the 300 ms limit
    p.Service clock.Now
    Assert.Equal(120_000_000L, counters.MaxGapNs)
    Assert.Equal(0L, counters.ClockJumps)     // no cut
    Assert.Equal(0L, counters.GapsWithUnacked + counters.GapsWithWork + counters.GapsWithoutWork)
    // A larger gap replaces the maximum, and this one does cut the timeline.
    clock.Now <- clock.Now + 1_000_000_000L
    p.Service clock.Now
    Assert.Equal(1_000_000_000L, counters.MaxGapNs)
    Assert.Equal(1L, counters.ClockJumps)

[<Fact>]
let ``a gap with nothing outstanding cuts no sample`` () =
    let clock = ManualClock()
    let counters = TunCounters()
    let p = gapPath clock counters
    p.Service clock.Now
    clock.Now <- clock.Now + 2_000_000_000L
    p.Service clock.Now
    Assert.Equal(1L, counters.ClockJumps)
    Assert.Equal(1L, counters.GapsWithoutWork)
    Assert.Equal(0L, counters.GapsWithUnacked)
    Assert.Equal(0L, counters.GapsWithWork)
    // Nothing was in the ring at entry, so no delivery sample is refused, and the longest
    // measurement-refusing gap is zero.
    Assert.Equal(0L, counters.MaxUnackedGapNs)
    Assert.Equal(0L, counters.JumpSamplesSkipped)
    Assert.Equal(0L, counters.SegmentsSent)

[<Fact>]
let ``a gap entered with an unacked segment is the class that refuses samples`` () =
    let clock = ManualClock()
    let counters = TunCounters()
    let p = gapPath clock counters
    let noSack = [| 2uy; 4uy; 0x05uy; 0xB4uy; 1uy; 3uy; 3uy; 7uy |]
    let struct (syn, sp) = clientSegment 5000u 0u Packet.Syn noSack ReadOnlySpan.Empty
    p.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    p.Service clock.Now
    p.SendReliable(ReadOnlySpan(Array.create 3000 7uy))
    Assert.True(p.Counters.SegmentsSent > 0L)
    clock.Now <- clock.Now + 800_000_000L
    p.Service clock.Now
    Assert.Equal(1L, counters.ClockJumps)
    Assert.Equal(1L, counters.GapsWithUnacked)
    Assert.Equal(0L, counters.GapsWithoutWork)
    // Raw gap, not the shifted `gap - 1 ms`: the measurement is taken before the shift.
    Assert.Equal(800_000_000L, counters.MaxUnackedGapNs)

// ---- tick-level service gaps: what a stats line can and cannot be compared with ------------
//
// These pin the one property the whole mapping rests on: the numbers are plain monotonic ns taken
// from the caller, so a segment's own `monotonic_ns` (same boot, same CLOCK_MONOTONIC) is
// comparable to them without any wall-clock anchor. They also pin the two ways a reader could
// misread it: a first tick invents no interval, and a backwards stamp contributes nothing.

[<Fact>]
let ``the first tick invents no interval and later ticks measure the real one`` () =
    let log = ServiceGapLog 300_000_000L
    Assert.Equal(0L, log.MaxNs)
    log.Observe 5_000_000_000L           // first tick: no previous stamp, so no interval
    Assert.Equal(0L, log.MaxNs)
    Assert.Equal(0L, log.OverCount)
    Assert.Equal(5_000_000_000L, log.LastTickNs)
    log.Observe 5_100_000_000L           // 100 ms, under the limit
    Assert.Equal(100_000_000L, log.MaxNs)
    Assert.Equal(0L, log.OverCount)
    log.Observe 5_900_000_000L           // 800 ms, over the limit
    Assert.Equal(800_000_000L, log.MaxNs)
    Assert.Equal(1L, log.OverCount)
    // The start of that interval is the previous tick, in the same time base: a reader can place
    // the gap inside a segment window without a wall clock.
    Assert.Equal(5_100_000_000L, log.LastOverStartNs)
    Assert.Equal(800_000_000L, log.LastOverNs)
    Assert.Equal(5_900_000_000L, log.LastTickNs)

[<Fact>]
let ``a backwards monotonic stamp resyncs instead of fabricating a long gap`` () =
    // A restart that reset CLOCK_MONOTONIC, or a switch to a different source, produces a stamp
    // below the previous one. The interval across that discontinuity is meaningless: it must be
    // dropped. Critically the backwards stamp must NOT become the new baseline — if it did, the
    // next ordinary tick would look like a gap spanning the whole backwards jump, inventing a
    // stall that never happened.
    let log = ServiceGapLog 300_000_000L
    log.Observe 1_000_000_000L
    log.Observe 1_200_000_000L
    Assert.Equal(200_000_000L, log.MaxNs)
    log.Observe 50_000_000L                      // backwards: time base changed
    Assert.Equal(200_000_000L, log.MaxNs)         // nothing invented
    Assert.Equal(0L, log.OverCount)
    Assert.Equal(1L, log.BackwardsObservations)
    Assert.Equal(1_200_000_000L, log.LastTickNs)  // previous baseline kept, not the bad stamp
    log.Observe 50_100_000_000L                   // first tick of the new base: resync, no interval
    Assert.Equal(200_000_000L, log.MaxNs)         // the 48.9 s jump is NOT a measured gap
    Assert.Equal(0L, log.OverCount)
    Assert.Equal(50_100_000_000L, log.LastTickNs)
    log.Observe 50_500_000_000L                   // a real 400 ms interval in the new base
    Assert.Equal(400_000_000L, log.MaxNs)
    Assert.Equal(1L, log.OverCount)

[<Fact>]
let ``equal cached stamps are not treated as a discontinuity`` () =
    // CachedClock legitimately returns the same sample twice; gap = 0 is normal, not backwards.
    let log = ServiceGapLog 300_000_000L
    log.Observe 1_000_000_000L
    log.Observe 1_000_000_000L
    log.Observe 1_000_000_000L
    Assert.Equal(0L, log.MaxNs)
    Assert.Equal(0L, log.OverCount)
    Assert.Equal(0L, log.BackwardsObservations)
    log.Observe 1_400_000_000L
    Assert.Equal(400_000_000L, log.MaxNs)

[<Fact>]
let ``only the last over-limit interval is kept, and the count keeps growing`` () =
    // Bounded on purpose: no ring, no silent truncation of a history. A reader gets the worst
    // value, a count of over-limit passes, and where the most recent one started.
    let log = ServiceGapLog 300_000_000L
    let mutable t = 0L
    log.Observe t
    t <- t + 400_000_000L
    log.Observe t                        // 400 ms
    t <- t + 1_000_000_000L
    log.Observe t                        // 1 s
    t <- t + 500_000_000L
    log.Observe t                        // 500 ms
    Assert.Equal(3L, log.OverCount)
    Assert.Equal(1_000_000_000L, log.MaxNs)
    // The most recent over-limit interval, not the worst one.
    Assert.Equal(500_000_000L, log.LastOverNs)
    Assert.Equal(t - 500_000_000L, log.LastOverStartNs)

[<Fact>]
let ``the maximum is across paths, while the gap counts stay per path`` () =
    let clock = ManualClock()
    let counters = TunCounters()               // one counter set, as one TunStack shares
    let paths = [ for _ in 1 .. 4 -> gapPath clock counters ]
    for p in paths do p.Service clock.Now
    clock.Now <- clock.Now + 400_000_000L
    for p in paths do p.Service clock.Now
    // One loop gap, four open paths: sums are path-multiplied, the maximum is not.
    Assert.Equal(4L, counters.ClockJumps)
    Assert.Equal(4L, counters.GapsWithoutWork)
    Assert.Equal(400_000_000L, counters.MaxGapNs)
    // A second, smaller loop gap must not lower the maximum.
    clock.Now <- clock.Now + 350_000_000L
    for p in paths do p.Service clock.Now
    Assert.Equal(400_000_000L, counters.MaxGapNs)
    Assert.Equal(8L, counters.ClockJumps)

[<Fact>]
let ``a send inside the interval splits it, so no counter describes one long gap`` () =
    // The classification is per `catchUp` call. `SendReliable` samples too, so a send 400 ms into
    // a 1 s quiet period ends the first interval there: the counters can only ever report the
    // intervals between calls, never a single interior. This is what stops `gap_max_ms` from being
    // read as "the stack was frozen for 1 s".
    let clock = ManualClock()
    let counters = TunCounters()
    let p = gapPath clock counters
    let noSack = [| 2uy; 4uy; 0x05uy; 0xB4uy; 1uy; 3uy; 3uy; 7uy |]
    let struct (syn, sp) = clientSegment 5000u 0u Packet.Syn noSack ReadOnlySpan.Empty
    p.OnSyn(syn.Seq, ReadOnlySpan(sp, syn.OptionsAt, syn.OptionsLength), 1460, clock.Now)
    p.Service clock.Now
    clock.Now <- clock.Now + 400_000_000L
    p.SendReliable(ReadOnlySpan(Array.create 1000 9uy))   // samples: first interval = 400 ms
    clock.Now <- clock.Now + 600_000_000L
    p.Service clock.Now                                   // second interval = 600 ms
    // Two intervals, both over the limit; neither is the 1 s the caller "waited".
    Assert.Equal(2L, counters.ClockJumps)
    Assert.Equal(600_000_000L, counters.MaxGapNs)
    // `SendReliable` samples BEFORE it pushes the segments into the ring, so the first interval is
    // entered with an empty ring and the second one with the segment still unacked. Each interval
    // is classified at its own entry — which is exactly why no counter can describe the interior.
    Assert.Equal(1L, counters.GapsWithoutWork)
    Assert.Equal(1L, counters.GapsWithUnacked)
