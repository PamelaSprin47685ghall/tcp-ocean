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
