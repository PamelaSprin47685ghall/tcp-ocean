/// Linux libc calls for the TUN device and poll(2). Blittable signatures only (NativeAOT).
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

/// Attach to the TUN device name (IFF_TUN | IFF_NO_PI), non-blocking. The device is made
/// beforehand (ip tuntap add ... user <user>), so the server needs no root. -1 on failure.
let openTun (name: string) =
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
