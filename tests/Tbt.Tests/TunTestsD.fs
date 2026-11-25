module Tbt.Tests.TunTestsD

open System
open System.Buffers.Binary
open System.Collections.Generic
open Xunit
open Tbt.Carrier
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests
open Tbt.Tests.TunTestsB
open Tbt.Tests.TunTestsC

let private sendFor (f: Fixture) (ms: int) =
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    let starts = List<uint32>()
    for _ in 1 .. ms do
        f.Clock.Now <- f.Clock.Now + 1_000_000L
        let mutable frame = Span<byte>.Empty
        let mutable n = 0
        while n < 1 && ip.TryBeginFrame(&frame) do
            frame.Fill 0x55uy
            ip.CommitFrame()
            starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
            n <- n + 1
    starts

[<Fact>]
let ``RACK only asks for repairs: the pacer spaces what goes out, and every hole is repaired`` () =
    let f = Fixture 1
    f.Open()
    let l = uint32 L
    let ip = f.Path :> IPath
    ip.Backlog <- 100
    // Warm up 300 ms: one frame per ms, each acked 40 ms later (the window opens).
    let warm = List<struct (uint32 * int64)>()
    for _ in 1 .. 300 do
        f.Clock.Now <- f.Clock.Now + 1_000_000L
        let mutable fr = Span<byte>.Empty
        if ip.TryBeginFrame(&fr) then
            fr.Fill 0x33uy
            ip.CommitFrame()
            warm.Add(struct ((parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq, f.Clock.Now))
        let mutable top = 0u
        for struct (q, t) in warm do if f.Clock.Now - t >= 40_000_000L then top <- q + l
        if top > 0u then
            let struct (s, p) = clientSegment f.Data0 top Packet.Ack [||] ReadOnlySpan.Empty
            f.Path.OnSegment(s, Span p, f.Clock.Now)
        f.Path.Service f.Clock.Now
    // Everything acked, then 30 frames (one per ms) of which only the last arrives.
    f.Clock.Now <- f.Clock.Now + 50_000_000L
    let struct (s0, p0) = clientSegment f.Data0 (let struct (q, _) = warm.[warm.Count - 1] in q + l) Packet.Ack [||] ReadOnlySpan.Empty
    f.Path.OnSegment(s0, Span p0, f.Clock.Now)
    let starts = sendFor f 30
    let n = starts.Count
    Assert.True(n >= 25, sprintf "sent %d" n)
    // 40 ms later the client SACKs only the last frame: all frames before it are lost.
    f.Clock.Now <- f.Clock.Now + 40_000_000L
    let before = f.Out.Packets.Count
    let c = f.Path.Counters
    let req0 = c.RepairRequests
    let def0 = c.RepairsDeferred
    let struct (s, p) = clientSegment f.Data0 starts.[0] Packet.Ack (sackOption [ (starts.[n - 1], starts.[n - 1] + l) ]) ReadOnlySpan.Empty
    f.Path.OnSegment(s, Span p, f.Clock.Now)
    Assert.Equal(int64 (n - 1), c.RepairRequests - req0)
    // At once: what the tokens and the bounded reserve (16 frames) allow, 2 copies each.
    let atOnce = f.Out.Packets.Count - before
    Assert.True(atOnce <= 2 * 12, sprintf "%d packets at once for %d repairs" atOnce (n - 1))
    Assert.True(atOnce < 2 * (n - 1), sprintf "%d packets: nothing waited" atOnce)
    Assert.True(c.RepairsDeferred > def0)
    // A waiting repair goes before any new frame.
    let ip = f.Path :> IPath
    let mutable frame = Span<byte>.Empty
    Assert.False(ip.TryBeginFrame(&frame))
    // As time passes the rest goes out, each lost frame repaired once (2 copies), no more.
    for _ in 1 .. 200 do
        f.Clock.Now <- f.Clock.Now + 1_000_000L
        f.Path.Service f.Clock.Now
    let repaired = HashSet<uint32>()
    let mutable copies = 0
    for i in before .. f.Out.Packets.Count - 1 do
        let q = parse f.Out.Packets.[i]
        if q.PayloadLength > 0 && starts.Contains q.Seq then
            repaired.Add q.Seq |> ignore
            copies <- copies + 1
    for i in 0 .. n - 2 do Assert.Contains(starts.[i], repaired)
    Assert.DoesNotContain(starts.[n - 1], repaired)
    Assert.True(copies >= 2 * (n - 1))

[<Fact>]
let ``a demoted path's repairs are charged to the group and counted, one probe per backed-off timer`` () =
    let clock = ManualClock()
    let group = RateGroup(clock)
    let fa = Fixture(1, clock, group, 40000us)
    let fb = Fixture(1, clock, group, 40001us)
    clock.Now <- clock.Now + 40_000_000L
    fa.Open()
    fb.Open()
    group.Add(fa.Path :> IRateMember)
    group.Add(fb.Path :> IRateMember)
    let l = uint32 L
    let lanes = [| for f in [ fa; fb ] -> f, List<uint32>(), List<int64>(), ref 0 |]
    let step (bAcks: bool) =
        clock.Now <- clock.Now + 1_000_000L
        for k in [ 1; 0 ] do
            let f, starts, sentAt, acked = lanes.[k]
            if k = 0 || bAcks then
                while acked.Value < sentAt.Count && clock.Now - sentAt.[acked.Value] >= 40_000_000L do
                    acked.Value <- acked.Value + 1
                if acked.Value > 0 then
                    let struct (s, p) = clientSegment f.Data0 (starts.[acked.Value - 1] + l) Packet.Ack [||] ReadOnlySpan.Empty
                    f.Path.OnSegment(s, Span p, clock.Now)
            let ip = f.Path :> IPath
            ip.Backlog <- 100
            let mutable frame = Span<byte>.Empty
            let mutable n = 0
            while n < 4 && ip.TryBeginFrame(&frame) do
                frame.Fill 0x44uy
                ip.CommitFrame()
                starts.Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
                sentAt.Add clock.Now
                n <- n + 1
            f.Path.Service clock.Now
        group.Service clock.Now
    for _ in 1 .. 1000 do step true
    for _ in 1 .. 2000 do step false
    let c = fb.Path.Counters
    Assert.True(c.Demotions >= 1L)
    Assert.True(c.DemotedRepairs > 0L, "no repair counted while demoted")
    // Probes back off: 1 per timer, the timer x2 per firing up to x64 (46 ms x 64 ~ 3 s).
    let probes0 = c.DemotedRepairs
    let bytes0 = group.RepairBytes
    for _ in 1 .. 3000 do step false
    let probes = c.DemotedRepairs - probes0
    Assert.InRange(probes, 1L, 4L)
    // Each probe is charged to the group: two copies of one frame (HOLE-2 keeps two so
    // one more loss of the repair still recovers; the count never escalates past two).
    Assert.True(group.RepairBytes - bytes0 >= probes * 2L * int64 L)

[<Fact>]
let ``uplink: the swallowed-range record is bounded; a forgotten range's late copy counts as a duplicate`` () =
    let f = Fixture 200
    f.Open()
    let send i =
        f.Send(f.Data0 + uint32 (i * 1460), ReadOnlySpan(f.Stream, i * 1460, 1460), [||])
    // 70 holes, one at a time: segment 2k is lost, 2k + 1 arrives, the gap is swallowed.
    for k in 0 .. 69 do
        send (2 * k + 1)
        f.Clock.Now <- f.Clock.Now + 16_000_000L
        f.Path.Service f.Clock.Now
        f.Path.FlushAck()
    Assert.Equal(70L, f.Path.Counters.VirtualAcks)
    Assert.Equal(6L, f.Path.Counters.SwallowedForgotten)
    // A recent hole's first copy: new. The oldest hole's (forgotten): taken for a duplicate.
    send 138
    f.Path.FlushAck()
    Assert.Equal(1460L, f.Path.Counters.LateFirstBytes)
    Assert.Equal(0L, f.Path.Counters.Dsacks)
    send 0
    f.Path.FlushAck()
    Assert.Equal(1L, f.Path.Counters.Dsacks)

[<Fact>]
let ``downlink: frames sent right after a box pause, before the paths' timers ran, keep their real send time`` () =
    // Facts: 4 paths share one rate group and send steadily, every frame acked 40 ms after it
    // went out. The box pauses 81 s (the peer too: no ACK during it). On resume the session
    // asks for frames first (TryBeginFrame), before any path's Service or an ACK; then the
    // timers run; then those frames are acked 40 ms later. Their RTT is 40 ms. (It was -81 s:
    // the stall shift moved their send time into the future, a negative min RTT made the
    // group's cwnd negative and the downlink stopped for good, server log GVA9jQ.)
    let clock = ManualClock()
    let group = RateGroup(clock)
    let fs = [| for k in 0 .. 3 -> Fixture(1, clock, group, 40000us + uint16 k) |]
    let l = uint32 L
    let starts = [| for _ in fs -> List<uint32>() |]
    let sentAt = [| for _ in fs -> List<int64>() |]
    let acked = Array.zeroCreate<int> fs.Length
    for f in fs do
        f.Open()
        group.Add(f.Path :> IRateMember)
        (f.Path :> IPath).Backlog <- 100
    let ack k (cum: uint32) =
        let f = fs.[k]
        let struct (s, p) = clientSegment f.Data0 cum Packet.Ack [||] ReadOnlySpan.Empty
        f.Path.OnSegment(s, Span p, clock.Now)
    let trySend k =
        let f = fs.[k]
        let mutable frame = Span<byte>.Empty
        if (f.Path :> IPath).TryBeginFrame(&frame) then
            frame.Fill 0x33uy
            (f.Path :> IPath).CommitFrame()
            starts.[k].Add (parse f.Out.Packets.[f.Out.Packets.Count - 1]).Seq
            sentAt.[k].Add clock.Now
            true
        else false
    let tick () =
        clock.Now <- clock.Now + 1_000_000L
        for k in 0 .. fs.Length - 1 do
            let a0 = acked.[k]
            while acked.[k] < sentAt.[k].Count && clock.Now - sentAt.[k].[acked.[k]] >= 40_000_000L do
                acked.[k] <- acked.[k] + 1
            if acked.[k] > a0 then ack k (starts.[k].[acked.[k] - 1] + l)
            for _ in 1 .. 4 do trySend k |> ignore
            fs.[k].Path.Service clock.Now
        group.Service clock.Now
    for _ in 1 .. 1000 do tick ()
    let sentBefore = starts |> Array.sumBy (fun x -> x.Count)
    Assert.True(sentBefore > 1000, sprintf "frames before the pause: %d" sentBefore)
    let minBefore = (fs.[0].Path :> IRateMember).MinRttNs
    clock.Now <- clock.Now + 81_000_000_000L
    let first = [| for k in 0 .. fs.Length - 1 -> starts.[k].Count |]
    for k in 0 .. fs.Length - 1 do trySend k |> ignore
    for k in 0 .. fs.Length - 1 do fs.[k].Path.Service clock.Now
    group.Service clock.Now
    // The frames that were in flight at the pause stay unacked (lost with the pause); the
    // first acks after it cover the frames sent on resume.
    for k in 0 .. fs.Length - 1 do
        acked.[k] <- first.[k]
        for i in first.[k] .. sentAt.[k].Count - 1 do sentAt.[k].[i] <- clock.Now
    let sent0 = starts |> Array.sumBy (fun x -> x.Count)
    for _ in 1 .. 300 do tick ()
    for k in 0 .. fs.Length - 1 do
        let m = (fs.[k].Path :> IRateMember).MinRttNs
        Assert.True(m > 0L && m <= minBefore + 1_000_000L, sprintf "path %d min RTT %d ns (before %d)" k m minBefore)
        Assert.True(fs.[k].Path.SrttNs > 0.0, sprintf "path %d srtt %.0f" k fs.[k].Path.SrttNs)
    Assert.True(group.Cwnd L > 8 * L, sprintf "group cwnd %d" (group.Cwnd L))
    let after = (starts |> Array.sumBy (fun x -> x.Count)) - sent0
    Assert.True(after > 300, sprintf "frames in 300 ms after the pause: %d (before: %d in 1 s)" after sentBefore)

[<Fact>]
let ``uplink: more than 32 partial frames reassemble instead of being dropped`` () =
    // 1 in-order partial plus 16 OFO intervals of two partial frames each is 33 slots
    // (enough to force the slot table past 32; the gap table also grows).
    // A fixed 32-slot table would silently drop the 33rd; the window-budget table
    // grows, keeps every fragment, and delivers each frame once the rest arrives.
    let f = Fixture 50
    f.Open()
    let dropped0 = f.Path.Counters.CellsDropped
    // Current frame held in a slot; rcvNxt advances over the prefix (no new gap).
    f.Send(f.Data0, ReadOnlySpan(f.Stream, 0, 100), [||])
    let pairs = 16
    for k in 0 .. pairs - 1 do
        // Frames 2+3k and 3+3k: skip one frame between pairs so intervals do not merge.
        let a = (2 + 3 * k) * L + 50
        f.Send(f.Data0 + uint32 a, ReadOnlySpan(f.Stream, a, L), [||])
    Assert.True(f.Path.Counters.ReasmGrows >= 1L, "slot table should grow past 32")
    Assert.Equal(0L, f.Path.Counters.ReasmPressure)
    Assert.Equal(dropped0, f.Path.Counters.CellsDropped)
    // Complete the 32 OFO frames (leave frame 0 partial so rcvNxt stays put).
    for k in 0 .. pairs - 1 do
        let fa = (2 + 3 * k) * L
        let fb = fa + L
        f.Send(f.Data0 + uint32 fa, ReadOnlySpan(f.Stream, fa, 50), [||])
        f.Send(f.Data0 + uint32 (fb + 50), ReadOnlySpan(f.Stream, fb + 50, L - 50), [||])
    let got = [ for fr in f.Owner.Frames -> int fr.[0] - 1 ] |> List.sort
    let expected = [ for k in 0 .. pairs - 1 do yield 2 + 3 * k; yield 3 + 3 * k ]
    Assert.Equal<int list>(expected, got)
    Assert.Equal(dropped0, f.Path.Counters.CellsDropped)
    Assert.Equal(0L, f.Path.Counters.ReasmPressure)

[<Fact>]
let ``uplink: more than 16 OFO gaps are kept instead of early-retired`` () =
    // 40 disjoint partial frames after a hole: a fixed 16-gap table would
    // early-retire (swallow) the oldest and increment CellsDropped. The
    // window-budget table grows, stores every gap, and does not virtually ACK them.
    let f = Fixture 50
    f.Open()
    let dropped0 = f.Path.Counters.CellsDropped
    let acks0 = f.Path.Counters.VirtualAcks
    f.Send(f.Data0, ReadOnlySpan(f.Stream, 0, 100), [||])
    let n = 40
    for k in 0 .. n - 1 do
        let at = (2 + k) * L
        f.Send(f.Data0 + uint32 at, ReadOnlySpan(f.Stream, at, 100), [||])
    Assert.True(f.Path.Counters.GapGrows >= 1L, "gap table should grow past 16")
    Assert.Equal(0L, f.Path.Counters.GapPressure)
    Assert.Equal(dropped0, f.Path.Counters.CellsDropped)
    Assert.Equal(acks0, f.Path.Counters.VirtualAcks)
    // Complete the 40 OFO frames; they must all deliver (none were swallowed).
    for k in 0 .. n - 1 do
        let at = (2 + k) * L + 100
        f.Send(f.Data0 + uint32 at, ReadOnlySpan(f.Stream, at, L - 100), [||])
    let got = [ for fr in f.Owner.Frames -> int fr.[0] - 1 ] |> List.sort
    let expected = [ 2 .. 41 ]
    Assert.Equal<int list>(expected, got)
    Assert.Equal(dropped0, f.Path.Counters.CellsDropped)
    Assert.Equal(acks0, f.Path.Counters.VirtualAcks)
    Assert.Equal(0L, f.Path.Counters.GapPressure)

[<Fact>]
let ``server TUN txqueuelen helper fails cleanly on a missing device`` () =
    Assert.Equal(20000, SocketTuning.TunTxQueueLen)
    Assert.Equal(128, SocketTuning.TunRecvBudget)
    let struct (ok, _, errno) = TunStack.TrySetTxQueueLen("tbtun-no-such-dev", SocketTuning.TunTxQueueLen)
    Assert.False(ok)
    Assert.True(errno > 0)

// SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC on AF_UNIX. Each write is one read,
// like a TUN fd, so we can test the per-call budget without /dev/net/tun.
module private UnixPair =
    open System.Runtime.InteropServices
    [<DllImport("libc", EntryPoint="socketpair", SetLastError=true)>]
    extern int sysSocketpair(int domain, int typ, int protocol, int[] sv)
    [<DllImport("libc", EntryPoint="close", SetLastError=true)>]
    extern int sysClose(int fd)
    [<DllImport("libc", EntryPoint="write", SetLastError=true)>]
    extern nativeint sysWrite(int fd, byte[] buf, nativeint n)
    let AF_UNIX = 1
    let typ = 2 ||| 0x800 ||| 0x80000 // SOCK_DGRAM | SOCK_NONBLOCK | SOCK_CLOEXEC
    let isAvailable = Platform.current.Value.CanAdoptTunFd && Platform.current.Value.HasLibc
    let openPair () =
        if not isAvailable then failwith "posix socketpair is not available on this platform"
        let sv = Array.zeroCreate<int> 2
        if sysSocketpair(AF_UNIX, typ, 0, sv) <> 0 then failwith "socketpair"
        sv
    let writeOne fd =
        let buf = [| 0x45uy; 0uy; 0uy; 8uy; 0uy; 0uy; 0uy; 0uy |]
        if int (sysWrite(fd, buf, nativeint buf.Length)) <> buf.Length then failwith "write"
    let close fd = sysClose fd |> ignore

[<Fact>]
let ``budgeted TUN Receive stops at the cap so Service can interleave`` () =
    if not UnixPair.isAvailable then ()
    else
        let sv = UnixPair.openPair()
        try
            let clock = ManualClock() :> Tbt.Contracts.IClock
            let entropy = SeededEntropy 1 :> Tbt.Contracts.IEntropy
            let local = System.Net.IPEndPoint(System.Net.IPAddress.Parse "10.200.3.2", 8443)
            let stack = TunStack(sv.[0], local, clock, entropy, 4)
            for _ in 1 .. 10 do UnixPair.writeOne sv.[1]
            Assert.Equal(3, stack.Receive(3))
            Assert.Equal(3, stack.LastReceiveCount)
            Assert.Contains("recv_capped=1", stack.Describe())
            // Seven datagrams remain on the fd — a Service() in the server loop
            // would run now, before the next budgeted drain.
            Assert.Equal(3, stack.Receive(3))
            Assert.Equal(3, stack.LastReceiveCount)
            Assert.Contains("recv_capped=2", stack.Describe())
            Assert.Equal(4, stack.Receive(128))
            Assert.Equal(4, stack.LastReceiveCount)
            Assert.Contains("recv_capped=2", stack.Describe())
            Assert.Equal(0, stack.Receive(128))
        finally
            UnixPair.close sv.[0]
            UnixPair.close sv.[1]

[<Fact>]
let ``adaptive Receive yields so Service can interleave before EAGAIN`` () =
    if not UnixPair.isAvailable then ()
    else
        let sv = UnixPair.openPair()
        try
            let clock = ManualClock() :> Tbt.Contracts.IClock
            let entropy = SeededEntropy 1 :> Tbt.Contracts.IEntropy
            let local = System.Net.IPEndPoint(System.Net.IPAddress.Parse "10.200.3.2", 8443)
            let stack = TunStack(sv.[0], local, clock, entropy, 4)
            for _ in 1 .. 10 do UnixPair.writeOne sv.[1]
            let mutable calls = 0
            let n = stack.Receive(1_000_000, yieldWhen=fun () -> calls <- calls + 1; calls >= 4)
            Assert.Equal(4, n)
            Assert.Equal(4, stack.LastReceiveCount)
            Assert.Contains("recv_yielded=1", stack.Describe())
            Assert.Contains("recv_capped=0", stack.Describe())
            Assert.Equal(6, stack.Receive(1_000_000))
            Assert.Equal(0, stack.Receive(1_000_000))
            Assert.Contains("recv_yielded=1", stack.Describe())
        finally
            UnixPair.close sv.[0]
            UnixPair.close sv.[1]


[<Fact>]
let ``TunStack.Attach refuses a bad fd and accepts an injected descriptor`` () =
    let clock = ManualClock() :> Tbt.Contracts.IClock
    let entropy = SeededEntropy 2 :> Tbt.Contracts.IEntropy
    let local = System.Net.IPEndPoint(System.Net.IPAddress.Parse "10.200.3.2", 8443)
    Assert.True(isNull (TunStack.Attach(-1, local, clock, entropy, 4)))
    if not UnixPair.isAvailable then ()
    else
        let sv = UnixPair.openPair()
        try
            let stack = TunStack.Attach(sv.[0], local, clock, entropy, 4, sndBuf = 0)
            Assert.False(isNull stack)
            let stack = nonNull stack
            Assert.Equal(sv.[0], stack.Fd)
            UnixPair.writeOne sv.[1]
            Assert.Equal(1, stack.Receive(8))
        finally
            UnixPair.close sv.[0]
            UnixPair.close sv.[1]

[<Fact>]
let ``TunStack.Close is idempotent and owns Open descriptors only`` () =
    if not UnixPair.isAvailable then ()
    else
        let clock = ManualClock() :> Tbt.Contracts.IClock
        let entropy = SeededEntropy 3 :> Tbt.Contracts.IEntropy
        let local = System.Net.IPEndPoint(System.Net.IPAddress.Parse "10.200.3.2", 8443)
        // Owned descriptor: Close releases the fd, and a second Close is harmless.
        let owned = UnixPair.openPair()
        let stack = TunStack(owned.[0], local, clock, entropy, 4, ownsFd = true)
        stack.Close()
        Assert.True(stack.IsClosed)
        stack.Close()
        let closedWrite = UnixPair.sysWrite(owned.[0], [| 0x45uy |], nativeint 1)
        Assert.Equal(nativeint -1, closedWrite)
        UnixPair.close owned.[1]
        // Borrowed descriptor: Close leaves the caller's fd usable.
        let borrowed = UnixPair.openPair()
        try
            let attached = TunStack.Attach(borrowed.[0], local, clock, entropy, 4, sndBuf = 0)
            Assert.False(isNull attached)
            let attached = nonNull attached
            attached.Close()
            Assert.True(attached.IsClosed)
            UnixPair.writeOne borrowed.[1]
            Assert.Equal(1, attached.Receive(8))
        finally
            UnixPair.close borrowed.[0]
            UnixPair.close borrowed.[1]

[<Fact>]
let ``tun sndbuf auto is window_clamp times paths, or zero to leave the kernel default`` () =
    Assert.Equal(2_000_000, SocketTuning.TunSndBuf)
    Assert.Equal(2000, SocketTuning.TunRecvYieldUs)
    Assert.Equal(2_400_000, SocketTuning.tunSndBufFor(SocketTuning.WindowClamp, 4))
    Assert.Equal(4_800_000, SocketTuning.tunSndBufFor(SocketTuning.WindowClamp, 8))
    Assert.Equal(38_400_000, SocketTuning.tunSndBufFor(SocketTuning.WindowClamp, 64))
    Assert.Equal(0, SocketTuning.tunSndBufFor(SocketTuning.WindowClamp, 0))
