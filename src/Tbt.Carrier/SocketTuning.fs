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

    /// Settings for a path socket: no Nagle, small unsent queue (Linux TCP_NOTSENT_LOWAT).
    /// clamp is TCP_WINDOW_CLAMP in bytes (default WindowClamp, spec ch. 9).
    let path (socket: Socket, clamp: int) =
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
            try socket.SetRawSocketOption(6, c.TcpNotsentLowatOption, ReadOnlySpan(BitConverter.GetBytes NotSentLimit))
            with :? SocketException -> ()
        if c.HasTcpWindowClamp then
            // TCP_WINDOW_CLAMP; autotuning stays on (no SO_RCVBUF, spec 3.6).
            try socket.SetRawSocketOption(6, 10, ReadOnlySpan(BitConverter.GetBytes clamp))
            with :? SocketException -> ()
        elif OperatingSystem.IsWindows() then
            // Coarse send-queue cap when SIO_TCP_INFO / notsent is not implemented (D13).
            try socket.SendBufferSize <- 384 * 1024
            with :? SocketException -> ()

    /// Settings for a local flow socket.
    let local (socket: Socket) = socket.NoDelay <- true

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

    /// Linux TCP_INFO.tcpi_bytes_acked (offset 120), or -1 when unavailable.
    /// RFC 4898 thru octets acked: payload the peer has cumulatively ACKed.
    let tryBytesAcked (socket: Socket) =
        match KernelTcpInfo.tryRead socket with
        | ValueSome info -> info.BytesAcked
        | ValueNone -> -1L

/// Two-pass write gating for the client uplink (spec 3.14).
module WriteGate =
    /// Pass 1 admits notsent ≤ 4 KiB (or a probe that cannot tell). Pass 2, run
    /// whenever pass 1 gated a path, admits notsent ≤ 256 KiB.
    let admits (pass: int) (notSent: int) =
        if pass <= 1 then notSent >= 0 && notSent <= SocketTuning.GatePass1Limit
        elif notSent < 0 then true
        else notSent <= SocketTuning.GatePass2Limit
