module Tbt.Tests.PollerTests

open System
open System.Net
open System.Net.Sockets
open Xunit
open Tbt.Carrier

let private pair () =
    use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
    listener.Listen 1
    let a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    a.Connect(IPEndPoint(IPAddress.Loopback, (listener.LocalEndPoint :?> IPEndPoint | null |> nonNull).Port))
    let b = listener.Accept()
    a, b

let private waitRead (p: Poller) (s: Socket) =
    p.WatchRead s
    p.Wait 200_000
    p.IsReadable s

let backends = TheoryData<PollerBackend>([ PollerBackend.Epoll; PollerBackend.Poll; PollerBackend.Select ])

[<Theory; MemberData(nameof backends)>]
let ``poller: a socket is readable only with input, and writable when it has space`` (backend: PollerBackend) =
    use p = new Poller(backend)
    let a, b = pair ()
    use a = a
    use b = b
    Assert.False(waitRead p b)
    a.Send([| 1uy |]) |> ignore
    Assert.True(waitRead p b)
    // Level-triggered: still readable while the byte stays unread.
    Assert.True(waitRead p b)
    b.Receive(Array.zeroCreate<byte> 8) |> ignore
    Assert.False(waitRead p b)
    p.WatchWrite a
    p.Wait 200_000
    Assert.True(p.IsWritable a)

[<Fact>]
let ``poller: epoll is the default on Linux`` () =
    use p = new Poller()
    Assert.Equal((if OperatingSystem.IsLinux() then PollerBackend.Epoll else PollerBackend.Select), p.Backend)

[<Fact>]
let ``poller: epoll sees a new socket that gets the number of a closed one`` () =
    use p = new Poller(PollerBackend.Epoll)
    let a, b = pair ()
    let fd = int b.Handle
    Assert.False(waitRead p b)
    a.Dispose()
    b.Dispose()
    // Open pairs until one end gets the old descriptor number.
    let mutable found = None
    let spare = Collections.Generic.List<Socket>()
    for _ in 1 .. 20 do
        if found.IsNone then
            let c, d = pair ()
            if int d.Handle = fd then found <- Some(c, d)
            elif int c.Handle = fd then found <- Some(d, c)
            else spare.Add c; spare.Add d
    match found with
    | None -> failwith "no descriptor reuse"
    | Some(writer, reader) ->
        use writer = writer
        use reader = reader
        writer.Send([| 7uy |]) |> ignore
        Assert.True(waitRead p reader)
    for s in spare do s.Dispose()

[<Fact>]
let ``poller: a socket left out of a round is not reported`` () =
    use p = new Poller(PollerBackend.Epoll)
    let a, b = pair ()
    use a = a
    use b = b
    let c, d = pair ()
    use c = c
    use d = d
    a.Send([| 1uy |]) |> ignore
    Assert.True(waitRead p b)
    // Next round watches only d: b's pending input must not end the wait.
    let t0 = Diagnostics.Stopwatch.GetTimestamp()
    Assert.False(waitRead p d)
    Assert.False(p.IsReadable b)
    Assert.True(Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds >= 150.0)
