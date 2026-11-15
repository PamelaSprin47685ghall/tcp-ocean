/// Kernel TCP carrier settings (spec 6.8 write gating). The Linux parts sit behind
/// INotSentProbe; other systems use the fallback, which only knows "socket writable".
namespace Tbt.Carrier

open System
open System.Net.Sockets
open System.Runtime.InteropServices

/// Bytes the kernel holds for a socket that it did not send yet.
type INotSentProbe =
    /// The unsent byte count, or -1 when the system cannot tell.
    abstract NotSent: socket: Socket -> int

module private SocketNative =
    [<DllImport("libc", SetLastError = true)>]
    extern int ioctl(nativeint fd, unativeint request, int& value)

/// Linux: ioctl SIOCOUTQNSD.
[<Sealed>]
type LinuxNotSentProbe() =
    static let SIOCOUTQNSD = 0x894Bun
    interface INotSentProbe with
        member _.NotSent(socket) =
            let mutable value = 0
            if SocketNative.ioctl(socket.Handle, SIOCOUTQNSD, &value) = 0 then value else -1

[<Sealed>]
type NoNotSentProbe() =
    interface INotSentProbe with
        member _.NotSent(_) = -1

module SocketTuning =
    /// Unsent bytes per path that the gate allows (spec 6.8: about 16 KiB).
    [<Literal>]
    let NotSentLimit = 16384

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
        if OperatingSystem.IsLinux() then LinuxNotSentProbe() else NoNotSentProbe()

    /// Settings for a path socket: no Nagle, small unsent queue (Linux TCP_NOTSENT_LOWAT).
    /// clamp is TCP_WINDOW_CLAMP in bytes (default WindowClamp, spec ch. 9).
    let path (socket: Socket, clamp: int) =
        socket.NoDelay <- true
        if OperatingSystem.IsLinux() then
            try socket.SetRawSocketOption(6, 25, ReadOnlySpan(BitConverter.GetBytes NotSentLimit))
            with :? SocketException -> ()
            // TCP_WINDOW_CLAMP; autotuning stays on (no SO_RCVBUF, spec 3.6).
            try socket.SetRawSocketOption(6, 10, ReadOnlySpan(BitConverter.GetBytes clamp))
            with :? SocketException -> ()

    /// Settings for a local flow socket.
    let local (socket: Socket) = socket.NoDelay <- true

    /// Linux TCP_INFO.tcpi_bytes_acked (offset 120), or -1 when unavailable.
    /// RFC 4898 thru octets acked: payload the peer has cumulatively ACKed.
    let tryBytesAcked (socket: Socket) =
        if not (OperatingSystem.IsLinux()) then -1L
        else
            try
                let buf = Array.zeroCreate<byte> 256
                let n = socket.GetRawSocketOption(6, 11, Span buf) // SOL_TCP, TCP_INFO
                if n >= 128 then BitConverter.ToInt64(buf, 120) else -1L
            with :? SocketException -> -1L
