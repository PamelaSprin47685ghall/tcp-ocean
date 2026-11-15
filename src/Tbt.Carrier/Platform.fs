/// Runtime capability probes for the JIT client and the shared carrier.
/// Linux results match the historical IsLinux() paths: epoll, SIOCOUTQNSD,
/// TCP_NOTSENT_LOWAT, TCP_WINDOW_CLAMP, and Linux TCP_INFO all stay on.
/// Other systems get a graceful fallback; missing hardware or SELinux denials
/// are not treated as success (a short or failed TCP_INFO is not a zero queue).
namespace Tbt.Carrier

open System
open System.Net.Sockets
open System.Runtime.InteropServices

[<Struct>]
type PlatformCapabilities =
    { /// libc (or libSystem / bionic) P/Invokes resolve on this process.
      HasLibc: bool
      /// epoll_create1 works (Linux and Android bionic).
      HasEpoll: bool
      /// POSIX poll(2) works (Linux, Android, macOS).
      HasPoll: bool
      /// /dev/net/tun exists; opening it still needs a prepared device.
      HasDevNetTun: bool
      /// A raw file descriptor can be read and written (injected TUN / VpnService fd).
      CanAdoptTunFd: bool
      /// ioctl SIOCOUTQNSD works on a TCP socket (desktop Linux; SELinux denies this on Android).
      HasSiocoutqnsd: bool
      /// getsockopt(TCP_INFO) returns the Linux/bionic layout (option 11).
      HasLinuxTcpInfo: bool
      /// That TCP_INFO record covers tcpi_notsent_bytes (offset 144).
      HasTcpInfoNotSent: bool
      /// setsockopt TCP_NOTSENT_LOWAT is accepted (Linux 25 or Darwin 0x201).
      HasTcpNotsentLowat: bool
      /// The option number to use with IPPROTO_TCP when HasTcpNotsentLowat is set.
      TcpNotsentLowatOption: int
      /// setsockopt TCP_WINDOW_CLAMP (Linux 10) is accepted.
      HasTcpWindowClamp: bool }

module Platform =
    let private tryCall (f: unit -> bool) =
        try f ()
        with
        | :? DllNotFoundException | :? EntryPointNotFoundException -> false
        | :? SocketException -> false

    let private withTcpSocket (f: Socket -> 'a) (fallback: 'a) =
        try
            use s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            f s
        with
        | :? SocketException | :? ObjectDisposedException | :? DllNotFoundException
        | :? EntryPointNotFoundException -> fallback

    let private trySetOption (s: Socket) (name: int) (value: int) =
        try
            s.SetRawSocketOption(6, name, ReadOnlySpan(BitConverter.GetBytes value))
            true
        with :? SocketException -> false

    let private tcpInfoLength (s: Socket) =
        try
            let buffer = Array.zeroCreate<byte> 256
            s.GetRawSocketOption(6, 11, Span buffer)
        with :? SocketException | :? ObjectDisposedException -> 0

    let private probe () : PlatformCapabilities =
        let libc = Native.hasLibc.Value
        let epoll = libc && tryCall (fun () ->
            let fd = Native.epollCreate ()
            if fd >= 0 then
                Native.sysClose fd |> ignore
                true
            else false)
        let poll = libc && tryCall (fun () ->
            let fds = Array.zeroCreate<Native.PollFd> 1
            Native.poll fds 0 0 |> ignore
            true)
        let devTun = libc && IO.File.Exists "/dev/net/tun"
        let sioc = libc && withTcpSocket (fun s -> Native.trySiocoutqnsd s.Handle >= 0) false
        let infoLen = withTcpSocket tcpInfoLength 0
        // Linux/Android TCP_NOTSENT_LOWAT is 25; Darwin is 0x201. 16384 is the gate (spec 6.8).
        let lowatOpt, hasLowat =
            withTcpSocket (fun s ->
                if trySetOption s 25 16384 then 25, true
                elif trySetOption s 0x201 16384 then 0x201, true
                else 0, false) (0, false)
        let clamp = withTcpSocket (fun s -> trySetOption s 10 600000) false
        { HasLibc = libc
          HasEpoll = epoll
          HasPoll = poll
          HasDevNetTun = devTun
          CanAdoptTunFd = libc
          HasSiocoutqnsd = sioc
          HasLinuxTcpInfo = infoLen >= 104
          HasTcpInfoNotSent = infoLen >= 148
          HasTcpNotsentLowat = hasLowat
          TcpNotsentLowatOption = lowatOpt
          HasTcpWindowClamp = clamp }

    let current = lazy (probe ())

    let describe () =
        let c = current.Value
        let flag name v = if v then name else name + "=no"
        String.concat " "
            [| flag "libc" c.HasLibc
               flag "epoll" c.HasEpoll
               flag "poll" c.HasPoll
               flag "devnet_tun" c.HasDevNetTun
               flag "tun_fd" c.CanAdoptTunFd
               flag "siocoutqnsd" c.HasSiocoutqnsd
               flag "tcp_info" c.HasLinuxTcpInfo
               flag "tcp_info_notsent" c.HasTcpInfoNotSent
               flag "tcp_notsent_lowat" c.HasTcpNotsentLowat
               flag "tcp_window_clamp" c.HasTcpWindowClamp |]
