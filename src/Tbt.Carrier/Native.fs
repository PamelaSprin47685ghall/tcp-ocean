/// Linux libc calls for the TUN device, poll(2) and epoll(7). Blittable signatures only (NativeAOT).
module internal Tbt.Carrier.Native

#nowarn "9"

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<Struct; StructLayout(LayoutKind.Sequential)>]
type PollFd =
    val mutable Fd: int
    val mutable Events: int16
    val mutable Revents: int16

[<Literal>]
let PollIn = 1s

[<Literal>]
let PollOut = 4s

[<Literal>]
let PollErr = 8s

[<Literal>]
let PollHup = 16s

[<Literal>]
let EAgain = 11

[<DllImport("libc", EntryPoint = "open", SetLastError = true)>]
extern int sysOpen(nativeint path, int flags)

[<DllImport("libc", EntryPoint = "ioctl", SetLastError = true)>]
extern int sysIoctl(int fd, unativeint request, nativeint arg)

[<DllImport("libc", EntryPoint = "read", SetLastError = true)>]
extern nativeint sysRead(int fd, nativeint buffer, unativeint count)

[<DllImport("libc", EntryPoint = "write", SetLastError = true)>]
extern nativeint sysWrite(int fd, nativeint buffer, unativeint count)

[<DllImport("libc", EntryPoint = "poll", SetLastError = true)>]
extern int sysPoll(nativeint fds, unativeint count, int timeoutMs)

[<DllImport("libc", EntryPoint = "close", SetLastError = true)>]
extern int sysClose(int fd)

[<DllImport("libc", EntryPoint = "socket", SetLastError = true)>]
extern int sysSocket(int domain, int typ, int protocol)

[<DllImport("libc", EntryPoint = "eventfd", SetLastError = true)>]
extern int sysEventFd(uint32 initval, int flags)

/// Linux eventfd with EFD_NONBLOCK. -1 when the call fails.
let eventFd () = sysEventFd (0u, 0x800)

let read (fd: int) (buffer: Span<byte>) =
    use p = fixed &MemoryMarshal.GetReference buffer
    int (sysRead(fd, NativePtr.toNativeInt p, unativeint buffer.Length))

let write (fd: int) (data: ReadOnlySpan<byte>) =
    use p = fixed &MemoryMarshal.GetReference data
    int (sysWrite(fd, NativePtr.toNativeInt p, unativeint data.Length))

let poll (fds: PollFd[]) (count: int) (timeoutMs: int) =
    use p = fixed &fds.[0]
    sysPoll(NativePtr.toNativeInt p, unativeint count, timeoutMs)

let lastError () = Marshal.GetLastPInvokeError()

/// True when libc (or an alias such as libSystem / bionic) resolves in this process.
/// The first call may execute a harmless close(-1).
let hasLibc =
    lazy (
        try
            sysClose -1 |> ignore
            true
        with :? DllNotFoundException | :? EntryPointNotFoundException -> false)

let SIOCOUTQNSD = 0x894Bun

/// ioctl SIOCOUTQNSD; -1 when libc or the ioctl is missing, or the call fails.
let trySiocoutqnsd (handle: nativeint) =
    if not hasLibc.Value then -1
    else
        let mutable value = 0
        try
            use p = fixed &value
            if sysIoctl(int handle, SIOCOUTQNSD, NativePtr.toNativeInt p) = 0 then value
            else -1
        with :? DllNotFoundException | :? EntryPointNotFoundException -> -1

[<DllImport("libc", EntryPoint = "epoll_create1", SetLastError = true)>]
extern int sysEpollCreate1(int flags)

[<DllImport("libc", EntryPoint = "epoll_ctl", SetLastError = true)>]
extern int sysEpollCtl(int epfd, int op, int fd, nativeint ev)

[<DllImport("libc", EntryPoint = "epoll_wait", SetLastError = true)>]
extern int sysEpollWait(int epfd, nativeint events, int maxEvents, int timeoutMs)

[<Literal>]
let EpollCtlAdd = 1

[<Literal>]
let EpollCtlDel = 2

[<Literal>]
let EpollCtlMod = 3

/// struct epoll_event: u32 events, u64 data. Packed (12 B) on x86-64, 16 B elsewhere.
let epollEventSize = if RuntimeInformation.ProcessArchitecture = Architecture.X64 then 12 else 16
let epollDataOffset = if epollEventSize = 12 then 4 else 8

let epollCreate () =
    if not hasLibc.Value then -1
    else
        try sysEpollCreate1 0x80000 (* EPOLL_CLOEXEC *)
        with :? DllNotFoundException | :? EntryPointNotFoundException -> -1

/// epoll_ctl with one event record in scratch (at least epollEventSize bytes).
let epollCtl (epfd: int) (op: int) (fd: int) (events: uint32) (scratch: byte[]) =
    BitConverter.TryWriteBytes(Span(scratch, 0, 4), events) |> ignore
    BitConverter.TryWriteBytes(Span(scratch, epollDataOffset, 8), uint64 fd) |> ignore
    use p = fixed &scratch.[0]
    sysEpollCtl(epfd, op, fd, NativePtr.toNativeInt p)

/// epoll_wait into buffer (records of epollEventSize bytes).
let epollWait (epfd: int) (buffer: byte[]) (timeoutMs: int) =
    use p = fixed &buffer.[0]
    sysEpollWait(epfd, NativePtr.toNativeInt p, buffer.Length / epollEventSize, timeoutMs)

/// Attach to the TUN device name (IFF_TUN | IFF_NO_PI), non-blocking. The device is made
/// beforehand (ip tuntap add ... user <user>), so the server needs no root. -1 on failure.
let openTun (name: string) =
    if not hasLibc.Value || not (IO.File.Exists "/dev/net/tun") then -1
    else
        let path = "/dev/net/tun\000"B
        use pp = fixed &path.[0]
        let fd = sysOpen(NativePtr.toNativeInt pp, 2 ||| 0x800)
        if fd < 0 then fd
        else
            let ifr = Array.zeroCreate<byte> 40
            let n = Text.Encoding.ASCII.GetBytes(name, 0, min 15 name.Length, ifr, 0)
            ignore n
            BitConverter.TryWriteBytes(Span(ifr, 16, 2), 0x0001s ||| 0x1000s) |> ignore
            use ip = fixed &ifr.[0]
            if sysIoctl(fd, 0x400454caun, NativePtr.toNativeInt ip) < 0 then
                sysClose fd |> ignore
                -1
            else fd

let SIOCGIFTXQLEN = 0x8942un
let SIOCSIFTXQLEN = 0x8943un
/// TUNSETSNDBUF: _IOW('T', 212, int). The owner of the TUN fd can set this
/// without CAP_NET_ADMIN; SIOCSIFTXQLEN often cannot.
let TUNSETSNDBUF = 0x400454d4un

let private ifreq (name: string) =
    let ifr = Array.zeroCreate<byte> 40
    Text.Encoding.ASCII.GetBytes(name, 0, min 15 name.Length, ifr, 0) |> ignore
    ifr

/// Get or set a network device's txqueuelen (SIOCG/SIOCSIFTXQLEN) via a datagram socket.
/// Returns (ok, qlen, errno). CAP_NET_ADMIN is typically required to set.
let tryTxQueueLen (name: string) (setTo: int option) =
    if not hasLibc.Value then struct (false, 0, 38)
    else
        let fd = sysSocket(2, 2, 0) // AF_INET, SOCK_DGRAM
        if fd < 0 then struct (false, 0, lastError ())
        else
            try
                let ifr = ifreq name
                match setTo with
                | Some q ->
                    BitConverter.TryWriteBytes(Span(ifr, 16, 4), q) |> ignore
                    use ip = fixed &ifr.[0]
                    if sysIoctl(fd, SIOCSIFTXQLEN, NativePtr.toNativeInt ip) < 0 then
                        struct (false, q, lastError ())
                    else
                        // Read back the value the kernel kept.
                        use ig = fixed &ifr.[0]
                        if sysIoctl(fd, SIOCGIFTXQLEN, NativePtr.toNativeInt ig) < 0 then
                            struct (true, q, 0)
                        else struct (true, BitConverter.ToInt32(ifr, 16), 0)
                | None ->
                    use ig = fixed &ifr.[0]
                    if sysIoctl(fd, SIOCGIFTXQLEN, NativePtr.toNativeInt ig) < 0 then
                        struct (false, 0, lastError ())
                    else struct (true, BitConverter.ToInt32(ifr, 16), 0)
            finally
                sysClose fd |> ignore

let trySetTunSndBuf (tunFd: int) (bytes: int) =
    if not hasLibc.Value then struct (false, bytes, 38)
    else
        let mutable value = bytes
        use p = fixed &value
        if sysIoctl(tunFd, TUNSETSNDBUF, NativePtr.toNativeInt p) < 0 then
            struct (false, bytes, lastError ())
        else struct (true, bytes, 0)
