/// Kernel TCP carrier settings (spec 6.8 write gating). The Linux parts sit behind
/// INotSentProbe; other systems use the fallback, which only knows "socket writable".
namespace Tbt.Carrier

open System
open System.Net.Sockets
open System.Text

/// Bytes the kernel holds for a socket that it did not send yet.
type INotSentProbe =
    /// The unsent byte count, or -1 when the system cannot tell.
    abstract NotSent: socket: Socket -> int

/// Linux: ioctl SIOCOUTQNSD (only constructed when the probe succeeded).
[<Sealed>]
type LinuxNotSentProbe() =
    interface INotSentProbe with
        member _.NotSent(socket) = Native.trySiocoutqnsd socket.Handle

/// Android / Linux-layout TCP_INFO.tcpi_notsent_bytes, when SIOCOUTQNSD is absent.
[<Sealed>]
type TcpInfoNotSentProbe() =
    interface INotSentProbe with
        member _.NotSent(socket) =
            match KernelTcpInfo.tryRead socket with
            | ValueSome info when info.NotSentBytes >= 0L && info.NotSentBytes <= int64 Int32.MaxValue ->
                int info.NotSentBytes
            | _ -> -1

[<Sealed>]
type NoNotSentProbe() =
    interface INotSentProbe with
        member _.NotSent(_) = -1

module SocketTuning =
    /// Unsent bytes per path that the gate allows (spec 6.8: about 16 KiB).
    [<Literal>]
    let NotSentLimit = 16384

    /// Accepted request range for the experiment switch; the product default is unchanged.
    let notsentLowatFor (requested: int) =
        if requested < 4096 then 4096
        elif requested > 262144 then 262144
        else requested

    /// Async tx slots per path. The ring is the only place a frame can wait before send(2),
    /// so by Little's law it caps a path at `slots * frameLength / RTT`: 16 slots at 2 KiB is
    /// 32 KiB, i.e. 17 Mbit/s on a 341 ms path. The live 24 %-loss run measured exactly that
    /// (19.4 Mbit/s) while the carrier itself was saturated. 64 slots is 128 KiB per path
    /// (4 MiB across 32 pooled paths), and up to 256 is allowed for a long-RTT link.
    let txSlotsFor (requested: int) =
        if requested < 16 then 16
        elif requested > 256 then 256
        else requested

    /// A carrier path whose sent data stays unacknowledged this long is dead.
    /// Linux RTO starts near 200 ms and backs off; 30 s is five to six retries,
    /// which the live 314 ms / 18-27 % loss path can still recover, while a
    /// half-open path (peer process gone, FIN/RST lost) produces nothing.
    [<Literal>]
    let UserTimeoutMs = 30000

    /// Requested TCP_USER_TIMEOUT, clamped. Zero disables the option and keeps
    /// the pre-2026-11-20-16 behaviour exactly.
    let userTimeoutMsFor (requested: int) =
        if requested <= 0 then 0
        elif requested < 1000 then 1000
        elif requested > 300000 then 300000
        else requested

    /// Pass 1 of the write gate admits a path whose unsent queue is at most this.
    /// Zero was too strict for a long-RTT bundle: with 256 paths alive, every path
    /// almost always has a few hundred bytes unsent, so only one or two passed and
    /// the rest waited for their turn in the `next` rotation (about 6 frames per
    /// second per path). A probe that cannot tell (-1) also passes.
    [<Literal>]
    let GatePass1Limit = 4096

    /// Pass 2 relaxes the gate to this. The planning BDP at 310 ms and 200 Mbit/s
    /// is 7.75 MB, so 256 KiB is about 3 % of it: enough to drain a burst without
    /// letting the queue at a bottleneck grow.
    [<Literal>]
    let GatePass2Limit = 262144

    /// Per-path receive window (spec ch. 9: TCP_WINDOW_CLAMP 600 000 B, 3 x BDP of the planning
    /// path). It also bounds what the peer may have in flight on one path, so a queue at the
    /// bottleneck cannot grow past it.
    [<Literal>]
    let WindowClamp = 600000
    /// Default txqueuelen on the server TUN (SIOCSIFTXQLEN). Sized so a 64-path
    /// burst can sit in the qdisc while userspace is in a budgeted Receive / Service
    /// turn (~20k × ~1.5 KiB). The server sets this itself at startup; the testbed
    /// script is only a backup (tbt0 only — veths stay at the kernel default).
    [<Literal>]
    let TunTxQueueLen = 20000

    /// Packets one Receive may drain before returning to Service. Keeps ACK /
    /// downlink latency inside the virtual-ACK grace (~15 ms). A larger burst
    /// stays in the TUN qdisc and is taken on later iterations.
    [<Literal>]
    let TunRecvBudget = 128

    /// How long an adaptive Receive may run before yielding to Service / ACK.
    [<Literal>]
    let TunRecvYieldUs = 2000

    /// TUNSETSNDBUF default. 2 MB is large enough that tun-fd writes do not
    /// EAGAIN on the downlink, without the seconds of queueing a
    /// txqueuelen×1500 (30 MiB) buffer would add. 0 at Open means leave the
    /// kernel default.
    [<Literal>]
    let TunSndBuf = 2_000_000

    /// TUN send buffer large enough for a full window on every path, or 0 to
    /// leave the kernel default (caller passes 0 through to Open).
    let tunSndBufFor (windowClamp: int, paths: int) =
        if paths <= 0 || windowClamp <= 0 then 0
        else windowClamp * paths

    let probe () : INotSentProbe =
        let c = Platform.current.Value
        if c.HasSiocoutqnsd then LinuxNotSentProbe()
        elif c.HasTcpInfoNotSent then TcpInfoNotSentProbe()
        else NoNotSentProbe()

    /// Re-run the congestion module's init on a live socket (Linux): switch to
    /// `via` and back to `cc`. BBR restarts in STARTUP; cwnd and the connection stay.
    let reinitCongestion (socket: Socket, cc: string, via: string) =
        if not (OperatingSystem.IsLinux()) then false
        else
            try
                socket.SetRawSocketOption(6, 13, ReadOnlySpan<byte>(Encoding.ASCII.GetBytes via))
                socket.SetRawSocketOption(6, 13, ReadOnlySpan<byte>(Encoding.ASCII.GetBytes cc))
                true
            with
            | :? SocketException -> false
            | :? ObjectDisposedException -> false

    /// Settings for a path socket: no Nagle, small unsent queue (Linux TCP_NOTSENT_LOWAT).
    /// clamp is TCP_WINDOW_CLAMP in bytes (default WindowClamp, spec ch. 9).
    let pathWith (socket: Socket, clamp: int, notsentLowat: int, userTimeoutMs: int) =
        let notsentLowat = if notsentLowat <= 0 then NotSentLimit else notsentLowat
        socket.NoDelay <- true
        // Linux TCP_CONGESTION (level 6, option 13). Some hosts default to cubic;
        // on a path carrying a 14-19 % retransmit rate cubic collapsed cwnd to
        // 2-4 MSS, so the client's ACK stream arrived late and the peer's BBR (it
        // already ran BBR) cut its send rate: three consecutive 30 s downloads
        // fell 7.38 -> 4.77 -> 2.85 Mbit/s while the client's own minrtt stayed
        // at 320-329 ms and cell_loss was zero. BBR is the product requirement;
        // ask the kernel for it and stay silent when the module is absent.
        if OperatingSystem.IsLinux() then
            try socket.SetRawSocketOption(6, 13, ReadOnlySpan<byte>(Encoding.ASCII.GetBytes "bbr"))
            with :? SocketException -> ()
        let c = Platform.current.Value
        if c.HasTcpNotsentLowat then
            try socket.SetRawSocketOption(6, c.TcpNotsentLowatOption, ReadOnlySpan(BitConverter.GetBytes notsentLowat))
            with :? SocketException -> ()
        if c.HasTcpUserTimeout && userTimeoutMs > 0 then
            // A path with sent bytes but no ACK closes with ETIMEDOUT; the
            // carrier then prunes it and PathPool rejoins the id. An idle path
            // has nothing unacknowledged and is never touched.
            try socket.SetRawSocketOption(6, 18, ReadOnlySpan(BitConverter.GetBytes userTimeoutMs))
            with :? SocketException -> ()
        if c.HasTcpWindowClamp then
            // TCP_WINDOW_CLAMP; autotuning stays on (no SO_RCVBUF, spec 3.6).
            try socket.SetRawSocketOption(6, 10, ReadOnlySpan(BitConverter.GetBytes clamp))
            with :? SocketException -> ()
        elif OperatingSystem.IsWindows() then
            // Coarse send-queue cap when SIO_TCP_INFO / notsent is not implemented (D13).
            try socket.SendBufferSize <- 384 * 1024
            with :? SocketException -> ()

    /// Product settings: the historical 16 KiB unsent queue.
    let path (socket: Socket, clamp: int) = pathWith (socket, clamp, NotSentLimit, UserTimeoutMs)

    /// Bytes a flow socket asks for. The kernel clamps SO_RCVBUF to net.core.rmem_max,
    /// so a host whose ceiling is smaller silently gets a smaller window; rmemMax and
    /// flowRecvBufFor keep the startup log honest about which value was used.
    [<Literal>]
    let FlowRecvBuf = 4_000_000

    /// The kernel ceiling for SO_RCVBUF, or -1 when it cannot be read (non-Linux).
    let rmemMax () =
        try
            match Int32.TryParse((System.IO.File.ReadAllText "/proc/sys/net/core/rmem_max").Trim()) with
            | true, v -> v
            | _ -> -1
        with _ -> -1

    /// The SO_RCVBUF request the kernel will accept. A larger request is clamped to
    /// net.core.rmem_max silently, so ask for the smaller of the two.
    let flowRecvBufFor (ceiling: int) =
        if ceiling > 0 && ceiling < FlowRecvBuf then ceiling else FlowRecvBuf

    /// What Linux stores in sk_rcvbuf for a request: twice the requested value. The
    /// doubled number, not the request, is the receive window the flow may advertise.
    let flowRecvWinFor (request: int) = if request > 0 then request * 2 else request

    /// Settings for a local flow socket. Its receive buffer must cover one BDP of the
    /// session path: kernel autotuning grows too slowly on a long RTT, and a single flow
    /// is then capped at rcvbuf / RTT regardless of how many carrier paths exist.
    let local (socket: Socket) =
        socket.NoDelay <- true
        try socket.ReceiveBufferSize <- flowRecvBufFor (rmemMax ()) with :? SocketException -> ()

    /// Linux TCP_FASTOPEN_CONNECT (30). No-op where the kernel rejects client TFO.
    let fastOpenConnect (socket: Socket) =
        if OperatingSystem.IsLinux() then
            try socket.SetRawSocketOption(6, 30, ReadOnlySpan(BitConverter.GetBytes 1))
            with :? SocketException -> ()

    /// Linux TCP_FASTOPEN (23) queue. Must run before Listen.
    let fastOpenListen (socket: Socket, queue: int) =
        if OperatingSystem.IsLinux() then
            try socket.SetRawSocketOption(6, 23, ReadOnlySpan(BitConverter.GetBytes queue))
            with :? SocketException -> ()

    /// Gracefully tear down a TCP socket so FIN is sent cleanly before closing,
    /// reducing lingering half-closed states during path replacement.
    let closeGraceful (socket: Socket) =
        try socket.Shutdown SocketShutdown.Both with _ -> ()
        try socket.Dispose() with _ -> ()

    /// Linux TCP_INFO.tcpi_bytes_acked (offset 120), or -1 when unavailable.
    /// RFC 4898 thru octets acked: payload the peer has cumulatively ACKed.
    let tryBytesAcked (socket: Socket) =
        match KernelTcpInfo.tryRead socket with
        | ValueSome info -> info.BytesAcked
        | ValueNone -> -1L

/// Two-pass write gating for the client uplink (spec 3.14).
module WriteGate =
    /// Pass 1 admits notsent ≤ 4 KiB (or a probe that cannot tell). Pass 2, run
    /// whenever pass 1 gated a path, admits notsent ≤ pass2Limit. The limit is a
    /// parameter, not a constant, so a session can compare a smaller queue
    /// without changing the product default.
    let admitsWith (pass2Limit: int) (pass: int) (notSent: int) =
        if pass <= 1 then notSent >= 0 && notSent <= SocketTuning.GatePass1Limit
        elif notSent < 0 then true
        else notSent <= pass2Limit

    /// Pass 2 admits notsent ≤ 256 KiB: the product default.
    let admits (pass: int) (notSent: int) =
        admitsWith SocketTuning.GatePass2Limit pass notSent

    /// Per-path adaptive pass-2 ceiling scaled to the path's own cwnd so low-BDP
    /// paths do not build 6x self-queueing behind 256 KiB while high-BDP paths
    /// retain the full configured ceiling. No link rate constant is hard-coded.
    let adaptivePass2Limit (configuredMax: int) (cwndBytes: uint32) =
        // One cwnd of headroom, floored at 64 KiB and capped at 96 KiB. Half a cwnd (the
        // previous rule) starved the carrier exactly when SACK recovery shrank cwnd to
        // 15-30 KiB: the kernel stayed application-limited, never filled the pipe, and cwnd
        // had no reason to grow back. The absolute cap matters as much as the share: 256 KiB
        // per path over 32 paths is 8 MB of kernel queue, which the live 24 %-loss run turned
        // into 500-790 ms of self-inflicted RTT (minrtt_ms 626 vs 113 ms physical), and a deep
        // queue is exactly what makes loss look like our own congestion.
        // A sampled DeliveryRate must not use this floor: 64 KiB / 131 KiB/s is 488 ms of
        // self-queue. `rateAwareLimits` owns that case.
        if cwndBytes = 0u then configuredMax
        else
            let want = int (min (uint32 Int32.MaxValue) cwndBytes)
            min configuredMax (min 98304 (max 65536 want))

    /// Bytes a path may hold for `delayMs` at `rateBps`, saturated before the multiply wraps.
    let private bytesFor (rateBps: int64) (delayMs: int64) =
        if rateBps <= 0L || delayMs <= 0L then 0
        elif rateBps > System.Int64.MaxValue / delayMs then System.Int32.MaxValue
        else
            let bytes = rateBps * delayMs / 1000L
            if bytes > int64 System.Int32.MaxValue then System.Int32.MaxValue else int bytes

    let private clamp (lo: int) (hi: int) (v: int) = min hi (max lo v)

    /// Rate-scaled write-gate ceilings. `DeliveryRate` is already sampled by the 50 ms
    /// health refresh, so this costs no syscall. A 131 KiB/s evening path keeps about
    /// 6 KiB (40 ms), not the 64 KiB floor that queued for 488 ms. A 1.3 MiB/s daytime
    /// path opens to its 40 ms budget, capped at 128 KiB. No sample uses a 16 KiB probe
    /// so the first measurement can complete without a one-second self-queue.
    let rateAwareLimits (configuredMax: int) (valid: bool) (deliveryRateBps: int64) (caState: int) =
        let configured = max SocketTuning.GatePass1Limit configuredMax
        if not valid || deliveryRateBps <= 0L then
            struct (SocketTuning.GatePass1Limit, min configured 16384)
        else
            let pass1 = clamp 4096 16384 (bytesFor deliveryRateBps 12L)
            let mutable pass2 = clamp 6144 131072 (bytesFor deliveryRateBps 40L)
            if caState >= 3 then pass2 <- min pass2 8192
            struct (pass1, min configured pass2)
