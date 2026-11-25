/// The server's user-space TCP endpoint on a TUN device (spec 4.1): one listening address,
/// one TunPath per client connection. Packets in: parse, find the connection, hand over.
/// The inbound socket stays a plain TCP listener when no TUN is configured (kernel mode).
namespace Tbt.Carrier

open System
open System.Collections.Generic
open System.Net
open Tbt.Contracts

[<Sealed>]
/// Intervals between successive service-loop ticks: one observation per tick, no per-path
/// multiplication, plus the start instant of the **most recent** over-limit interval.
///
/// Why this is separate from `TunCounters.MaxGapNs`: `catchUp` runs on four entries (segment
/// arrival, send, timer service, send gate), so its gap means "time since anything last touched
/// this path", counted once per open path. A testbed asks a different question — "did the loop
/// stop running for >300 ms?" — and only a tick-level observation answers it.
///
/// **Resolution: ~1 ms, not ns.** `TunStack` is handed the caller's clock, and production builds
/// it as `CachedClock(SystemClock())` (Server.fs / Client.fs): `NowNs` returns a cached
/// `CLOCK_MONOTONIC` sample, refreshed when it is ≥ 1 ms old or every 32 reads. So an interval
/// here is the difference of two samples and carries that clock's staleness — roughly ±1 ms —
/// in both directions. It is NOT nanosecond-accurate, and a value sitting next to the 300 ms
/// limit can fall on either side of it for that reason alone.
///
/// **Comparability.** The underlying source is `CLOCK_MONOTONIC`, which on Linux is system-wide
/// per boot, so these values share a time base with a `monotonic_ns` taken by another process in
/// the same boot and the same monotonic namespace — `segments.json` records exactly such stamps.
/// That is a shared base, not a shared instant: the ±1 ms quantisation above still applies, so a
/// comparison can place an interval in a window but cannot certify sub-millisecond placement.
/// It must NOT be read across a reboot or across a separate `CLOCK_MONOTONIC` namespace (a
/// container with its own time namespace). A reader that cannot establish "same boot, same
/// namespace" must refuse to compare rather than guess.
///
/// **What one stats line can say.** `OverCount` counts every over-limit interval since start;
/// `LastOverNs` / `LastOverStartNs` describe only the **most recent** one. A single line therefore
/// cannot enumerate the intervals inside its window, nor place more than the last of them: if
/// several happened since the previous line, only the last is locatable. The millisecond fields
/// truncate (`/1_000_000L`), so 299.9 ms prints as 299.
///
/// Bounded on purpose: the last over-limit interval only. Keeping a ring here would be a second,
/// silently truncating trace; a worst value, a count, and the last start instant are what a 1 s
/// stats line can actually be read against.
type ServiceGapLog(stallNs: int64) =
    let mutable started = false
    let mutable lastTickNs = 0L
    let mutable maxNs = 0L
    let mutable overCount = 0L
    let mutable lastOverStartNs = 0L
    let mutable lastOverNs = 0L
    let mutable backwards = 0L
    let mutable resync = false

    /// One tick. `nowNs` is expected to come from the same monotonic source as every other call.
    ///
    /// Three cases, and only three:
    ///   * forward (gap > 0): the interval is measured.
    ///   * a strictly backwards stamp: the time base changed (a restart that reset the clock, or a
    ///     different source). The interval across that discontinuity is meaningless, so it is
    ///     dropped and the observation is counted in `BackwardsObservations`. Crucially the stamp
    ///     is NOT adopted as the new baseline: doing that would make the next ordinary tick look
    ///     like a gap spanning the whole backwards jump — a fabricated stall. A flag resyncs the
    ///     baseline on the next call instead.
    ///   * equal stamps: a cached clock legitimately repeats a sample (gap = 0). Nothing to
    ///     measure, nothing wrong, baseline unchanged.
    member _.Observe(nowNs: int64) =
        if not started then
            started <- true
            lastTickNs <- nowNs
        elif resync then
            resync <- false
            lastTickNs <- nowNs
        else
            let gap = nowNs - lastTickNs
            if gap > 0L then
                if gap > maxNs then maxNs <- gap
                if gap > stallNs then
                    overCount <- overCount + 1L
                    lastOverStartNs <- lastTickNs
                    lastOverNs <- gap
                lastTickNs <- nowNs
            elif gap < 0L then
                backwards <- backwards + 1L
                resync <- true

    /// Largest interval between two ticks, sub-limit intervals included. 0 until two comparable
    /// ticks run.
    member _.MaxNs = maxNs
    /// Ticks that followed an interval longer than the stall limit.
    member _.OverCount = overCount
    /// Start of the most recent over-limit interval (monotonic ns); 0 when there is none.
    member _.LastOverStartNs = lastOverStartNs
    /// Length of that interval (ns); 0 when there is none.
    member _.LastOverNs = lastOverNs
    /// Observations refused because the stamp went backwards. Each one also discarded the next
    /// interval (the baseline is resynced), so a non-zero value means at least that many
    /// measurable intervals were dropped — not that a stall occurred.
    member _.BackwardsObservations = backwards
    /// Monotonic stamp of the most recent tick — the anchor that places this stats line in the
    /// same time base as a segment's own `monotonic_ns`, subject to the ~1 ms resolution above.
    member _.LastTickNs = lastTickNs

type TunStack(fd: int, local: IPEndPoint, clock: IClock, entropy: IEntropy, maxConnections: int,
              ?windowClamp: int, ?ownsFd: bool) =
    let ownsFd = defaultArg ownsFd false
    let mutable closed = 0
    let windowClamp = defaultArg windowClamp SocketTuning.WindowClamp
    let localIp =
        let b = local.Address.GetAddressBytes()
        Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan b)
    let localPort = uint16 local.Port
    let rx: byte[] = Array.zeroCreate 65536
    let scratch: byte[] = Array.zeroCreate 65536
    let conns = Dictionary<uint64, TunPath>()
    let fresh = List<TunPath>()
    /// One rate group per client address (its paths share a bottleneck).
    let groups = Dictionary<uint32, RateGroup>()
    let counters = TunCounters()
    /// Tick-level service-loop intervals. Same 300 ms limit as the timeline shift in `TunPath`,
    /// so "over limit" means the same thing in both places, but the two are parsed differently:
    /// this one must stay per-tick. See `ServiceGapLog` for what the numbers may be compared with.
    let serviceGap = ServiceGapLog(300_000_000L)
    let mutable packetsIn = 0L
    let mutable packetsOut = 0L
    let mutable writeFailures = 0L
    let mutable parseFail = 0L
    let mutable wrongDst = 0L
    let mutable rstNoConn = 0L
    let mutable recvCapped = 0L
    let mutable recvYielded = 0L
    /// Safety cap on one Receive drain (non-blocking, should hit EAGAIN first).
    let recvBudgetMax = 1_000_000
    let mutable winRecvCalls = 0
    let mutable winRecvPkts = 0
    let mutable winRecvMax = 0
    let mutable lastReceiveCount = 0
    let mutable txqLen = -1
    let mutable txqOk = false
    let mutable txqErrno = 0
    let mutable sndBuf = -1
    let mutable sndBufOk = false
    let mutable sndBufErrno = 0
    let isn () =
        let b = Array.zeroCreate<byte> 4
        entropy.Fill(Span b)
        BitConverter.ToUInt32(b, 0)

    let output =
        { new ITunOutput with
            member _.Write(packet) =
                if Native.write fd packet < 0 then writeFailures <- writeFailures + 1L
                else packetsOut <- packetsOut + 1L
            member _.Scratch = scratch }

    let key (ip: uint32) (port: uint16) = (uint64 ip <<< 16) ||| uint64 port

    /// RST for a segment that has no connection (RFC 793 reset generation).
    let reset (seg: Packet.Segment) =
        rstNoConn <- rstNoConn + 1L
        if seg.Flags &&& Packet.Rst = 0uy then
            let ackFlag = seg.Flags &&& Packet.Ack <> 0uy
            let seq = if ackFlag then seg.AckNo else 0u
            let ack = seg.Seq + uint32 seg.PayloadLength + (if seg.Flags &&& Packet.Syn <> 0uy then 1u else 0u)
            let n = Packet.build (Span scratch) localIp seg.SrcIp localPort seg.SrcPort seq ack
                                 (if ackFlag then Packet.Rst else Packet.Rst ||| Packet.Ack) 0us ReadOnlySpan.Empty 0 0us
            output.Write(ReadOnlySpan(scratch, 0, n))

    member _.Fd = fd
    /// Call on the owner thread after all users of this stack have stopped.
    /// Attach borrows its descriptor; Open owns and closes it exactly once.
    member _.Close() =
        if System.Threading.Interlocked.Exchange(&closed, 1) = 0 && ownsFd then
            Native.sysClose fd |> ignore
    member _.IsClosed = System.Threading.Volatile.Read(&closed) <> 0
    /// txqueuelen after the startup ioctl, or -1 if it was not attempted.
    member _.TxQueueLen = txqLen
    member _.TxQueueOk = txqOk
    member _.TxQueueErrno = txqErrno
    member _.SndBuf = sndBuf
    member _.SndBufOk = sndBufOk
    member _.SndBufErrno = sndBufErrno

    /// SIOCSIFTXQLEN on the named device (typically needs CAP_NET_ADMIN).
    static member TrySetTxQueueLen(name: string, qlen: int) = Native.tryTxQueueLen name (Some qlen)
    static member TryGetTxQueueLen(name: string) = Native.tryTxQueueLen name None
    member _.LastReceiveCount = lastReceiveCount
    /// Per-stats-window receive counts; resets the window.
    member _.TakeRecvWindow() =
        let c, p, m = winRecvCalls, winRecvPkts, winRecvMax
        winRecvCalls <- 0; winRecvPkts <- 0; winRecvMax <- 0
        struct (c, p, m)
    member _.Counters = counters
    member _.Connections = conns.Count
    /// Connections whose bootstrap frame is whole and that wait for classification.
    member _.Fresh = fresh

    /// True when Service can flush an ACK or close a gap now.
    member _.AckDue =
        let now = clock.NowNs()
        let mutable d = false
        for c in conns.Values do if c.AckNeedsService now then d <- true
        d

    /// TUNSETSNDBUF; bytes <= 0 leaves the kernel default (no ioctl).
    member this.SetSndBuf(bytes: int) =
        if bytes <= 0 then
            this.RecordSndBuf(true, 0, 0)
        else
            let struct (ok, got, errno) = Native.trySetTunSndBuf fd bytes
            this.RecordSndBuf(ok, got, errno)

    /// Read packets the device holds now (until EAGAIN, the per-call budget, or
    /// `yieldWhen`). Returns how many packets were read. A small cap used to leave
    /// a burst in the TUN queue; during a decode stall that overflowed tbt0 and
    /// became a swallowed hole. `budget` bounds one call so the server loop can
    /// return to Service/downlink. `yieldWhen` (adaptive drain) stops after at
    /// least one packet when Service/ACK should run, without waiting for the cap.
    member _.Receive(?budget: int, ?yieldWhen: unit -> bool) =
        let mutable go = true
        let mutable budget = min recvBudgetMax (max 1 (defaultArg budget recvBudgetMax))
        let mutable read = 0
        let shouldYield = defaultArg yieldWhen (fun () -> false)
        while go && budget > 0 do
            if read > 0 && shouldYield () then
                recvYielded <- recvYielded + 1L
                go <- false
            else
                let n = Native.read fd (Span rx)
                if n <= 0 then go <- false
                else
                    budget <- budget - 1
                    read <- read + 1
                    packetsIn <- packetsIn + 1L
                    let mutable seg = Unchecked.defaultof<Packet.Segment>
                    let packet = Span(rx, 0, n)
                    if not (Packet.tryParse (Span<byte>.op_Implicit packet) &seg) then
                        parseFail <- parseFail + 1L
                    elif seg.DstIp <> localIp || seg.DstPort <> localPort then
                        wrongDst <- wrongDst + 1L
                    else
                        let k = key seg.SrcIp seg.SrcPort
                        let now = clock.NowNs()
                        match conns.TryGetValue k with
                        | true, c when not c.Closed ->
                            let before = c.BootState
                            c.OnSegment(seg, packet, now)
                            if before = 0 && c.BootState <> 0 && not c.Attached then fresh.Add c
                        | _ ->
                            if seg.Flags &&& Packet.Syn <> 0uy && seg.Flags &&& Packet.Ack = 0uy
                               && conns.Count < maxConnections then
                                conns.Remove k |> ignore
                                let g =
                                    match groups.TryGetValue seg.SrcIp with
                                    | true, g -> g
                                    | _ ->
                                        let g = RateGroup(clock)
                                        groups.[seg.SrcIp] <- g
                                        g
                                let c = TunPath(output, counters, localIp, localPort, seg.SrcIp, seg.SrcPort, isn (), clock, g, windowClamp)
                                g.Add c
                                conns.[k] <- c
                                c.OnSyn(seg.Seq, Span<byte>.op_Implicit (packet.Slice(seg.OptionsAt, seg.OptionsLength)),
                                        1460, now)
                            else reset seg
        if budget = 0 && go then recvCapped <- recvCapped + 1L
        lastReceiveCount <- read
        winRecvCalls <- winRecvCalls + 1
        winRecvPkts <- winRecvPkts + read
        if read > winRecvMax then winRecvMax <- read
        read

    /// Timers and pending ACKs of every connection; closed ones leave the table.
    member _.Service() =
        let now = clock.NowNs()
        // Tick-level observation, deliberately here and not in `TunPath.catchUp`: one record per
        // loop pass, so it is not multiplied by the path count and its start instant is the
        // previous pass, not "whenever this path was last touched".
        serviceGap.Observe now
        let mutable dead = 0
        for c in conns.Values do
            c.Service now
            c.FlushAck()
            if c.Closed then dead <- dead + 1
        if dead > 0 then
            let gone = List<uint64>()
            for kv in conns do if kv.Value.Closed then gone.Add kv.Key
            for k in gone do
                let c = conns.[k]
                match groups.TryGetValue c.RemoteIp with
                | true, g ->
                    g.Remove c
                    if g.Count = 0 then groups.Remove c.RemoteIp |> ignore
                | _ -> ()
                conns.Remove k |> ignore
        for g in groups.Values do g.Service now

    /// True when a connection needs the next wait to be short.
    member _.Busy =
        let mutable b = false
        for c in conns.Values do if c.Busy then b <- true
        b

    member _.Describe() =
        "tun conns=" + string conns.Count + " pkts_in=" + string packetsIn + " pkts_out=" + string packetsOut
        + " write_fail=" + string writeFailures + " seg_sent=" + string counters.SegmentsSent
        + " seg_lost=" + string counters.SegmentsLost + " borrow_retx=" + string counters.BorrowRetx
        + " tail_repairs=" + string counters.TailRepairs + " rtos=" + string counters.Rtos + " dsacks=" + string counters.Dsacks + " demotions=" + string counters.Demotions + " faults=" + string counters.Faults
        + " clock_jumps=" + string counters.ClockJumps + " jump_skips=" + string counters.JumpSamplesSkipped + " up_holes=" + string counters.UpHoles
        // `gap_max_ms` is the largest service-loop interval on any path (idle included, sub-limit
        // included). Of the jumps over the stall limit, `gap_unacked` are the only ones whose
        // shift refuses measurements (the ring was non-empty at entry, so those segments get no
        // RTT/rate sample); `gap_work` had a reorder interval or armed RTO only; `gap_idle` had
        // nothing outstanding. All three are per-path sums (one loop gap with n paths adds n);
        // `unacked_gap_max_ms` is a max, not a sum. None of them describes the interior of a gap.
        + " gap_max_ms=" + string (counters.MaxGapNs / 1_000_000L)
        + " gap_unacked=" + string counters.GapsWithUnacked
        + " gap_work=" + string counters.GapsWithWork
        + " gap_idle=" + string counters.GapsWithoutWork
        + " unacked_gap_max_ms=" + string (counters.MaxUnackedGapNs / 1_000_000L)
        // Tick-level, once per loop pass (not per path). `tick_mono_ns` is the last tick's stamp in
        // the same base as a segment's `start.monotonic_ns`, but the clock is cached (~1 ms), so a
        // reader may place this line in a window — not sub-millisecond. `svc_gap_last_*` locate the
        // most recent over-limit interval only; `svc_gaps_backwards` counts refusals, after each of
        // which the next interval was discarded.
        + " tick_mono_ns=" + string serviceGap.LastTickNs
        + " svc_gap_max_ms=" + string (serviceGap.MaxNs / 1_000_000L)
        + " svc_gaps_over=" + string serviceGap.OverCount
        + " svc_gap_last_start_ns=" + string serviceGap.LastOverStartNs
        + " svc_gap_last_ms=" + string (serviceGap.LastOverNs / 1_000_000L)
        + " svc_gaps_backwards=" + string serviceGap.BackwardsObservations
        + " virtual_ack=" + string counters.VirtualAcks + " fast_swallow=" + string counters.FastSwallows
        + " cells_rx=" + string counters.CellsReceived + " cells_spanning=" + string counters.CellsSpanning
        + " cells_dropped=" + string counters.CellsDropped + " swallow_b=" + string counters.BytesSwallowed + " late_first_b=" + string counters.LateFirstBytes
        + " parse_fail=" + string parseFail + " wrong_dst=" + string wrongDst + " rst_no_conn=" + string rstNoConn + " recv_capped=" + string recvCapped + " recv_yielded=" + string recvYielded
        + " reasm_grows=" + string counters.ReasmGrows + " reasm_pressure=" + string counters.ReasmPressure + " gap_grows=" + string counters.GapGrows + " gap_pressure=" + string counters.GapPressure
        + String.Join("", groups.Values |> Seq.map (fun g -> "\n  " + g.Describe()))
        + String.Join("", conns.Values |> Seq.map (fun c -> "\n  path " + string (c :> IPath).PathId + " " + c.Describe()))


    /// Adopt an already-open TUN file descriptor (Android VpnService.Builder.establish(),
    /// a test socketpair, or any host that already created the device). Does not run
    /// TUNSETIFF or SIOCSIFTXQLEN — the host owns the interface. Returns null when
    /// this process cannot read raw fds, or fd is negative. The caller keeps ownership
    /// of the descriptor.
    static member Attach(fd: int, local: IPEndPoint, clock: IClock, entropy: IEntropy, maxConnections: int,
                         ?windowClamp: int, ?sndBuf: int) : TunStack | null =
        if fd < 0 || not Platform.current.Value.CanAdoptTunFd then null
        else
            let t = TunStack(fd, local, clock, entropy, maxConnections, ?windowClamp=windowClamp)
            t.SetSndBuf(defaultArg sndBuf SocketTuning.TunSndBuf)
            t

    /// Open the TUN device name; null when it fails. Sizes the TUN qdisc
    /// (SIOCSIFTXQLEN, default TunTxQueueLen) so a 64-path burst is not dropped
    /// while this process is in a budgeted Receive / Service turn. sndBuf > 0
    /// sets TUNSETSNDBUF; 0 leaves the kernel default; omitted uses TunSndBuf.
    static member Open(name: string, local: IPEndPoint, clock: IClock, entropy: IEntropy, maxConnections: int,
                       ?windowClamp: int, ?txQueueLen: int, ?sndBuf: int) : TunStack | null =
        let fd = Native.openTun name
        if fd < 0 then null
        else
            let t = TunStack(fd, local, clock, entropy, maxConnections, ?windowClamp=windowClamp, ownsFd=true)
            let qlen = defaultArg txQueueLen SocketTuning.TunTxQueueLen
            if qlen > 0 then
                let struct (ok, got, errno) = Native.tryTxQueueLen name (Some qlen)
                t.RecordTxQueue(ok, got, errno)
            // Owner of the fd can set this without CAP_NET_ADMIN. sndBuf <= 0
            // means "leave the kernel default".
            t.SetSndBuf(defaultArg sndBuf SocketTuning.TunSndBuf)
            t

    member private _.RecordTxQueue(ok, got, errno) =
        txqOk <- ok; txqLen <- got; txqErrno <- errno
    member private _.RecordSndBuf(ok, got, errno) =
        sndBufOk <- ok; sndBuf <- got; sndBufErrno <- errno
