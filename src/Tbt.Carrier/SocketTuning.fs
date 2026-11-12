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
    /// Unsent bytes per path that the gate allows before the frame length is known
    /// (spec 6.8: about 16 KiB); after that 2 frames (spec 16.3 Q-1).
    [<Literal>]
    let NotSentLimit = 16384

    let probe () : INotSentProbe =
        if OperatingSystem.IsLinux() then LinuxNotSentProbe() else NoNotSentProbe()

    /// Linux TCP_NOTSENT_LOWAT: poll reports writable while fewer unsent bytes wait.
    let setNotSentLowat (socket: Socket) (bytes: int) =
        if OperatingSystem.IsLinux() then
            try socket.SetRawSocketOption(6, 25, ReadOnlySpan(BitConverter.GetBytes bytes))
            with :? SocketException -> ()

    /// Settings for a path socket: no Nagle, small unsent queue (handshake: NotSentLimit).
    let path (socket: Socket) =
        socket.NoDelay <- true
        setNotSentLowat socket NotSentLimit

    /// Settings for a local flow socket.
    let local (socket: Socket) = socket.NoDelay <- true
