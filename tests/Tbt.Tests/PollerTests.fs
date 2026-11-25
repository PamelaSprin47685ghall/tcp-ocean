module Tbt.Tests.PollerTests

open System
open System.Diagnostics
open System.Net
open System.Net.Sockets
open Xunit
open Tbt.Carrier
open Tbt.Contracts

let private pair () =
    use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
    listener.Listen 1
    let a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    a.Connect(IPEndPoint(IPAddress.Loopback, (listener.LocalEndPoint :?> IPEndPoint).Port))
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
let ``poller: default backend follows the epoll/poll/select capability`` () =
    use p = new Poller()
    let expected =
        let c = Platform.current.Value
        if c.HasEpoll then PollerBackend.Epoll
        elif c.HasPoll then PollerBackend.Poll
        else PollerBackend.Select
    Assert.Equal(expected, p.Backend)

[<Fact>]
let ``poller: epoll sees a new socket that gets the number of a closed one`` () =
    if not Platform.current.Value.HasEpoll then () else
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
    if not Platform.current.Value.HasEpoll then () else
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

[<Fact>]
let ``rx channel: a bounded drain leaves the rest queued and stays resumable`` () =
    // The unbounded drain is a livelock under load: when the IO threads refill faster than
    // frames are decoded, TryDequeue never fails and the session thread never leaves the
    // loop, so it cannot send a WIN_ACK, run a protocol timer or log a stats line. Live
    // forensics measured that as 9.4 s and 12.5 s holes with the peer's credit frozen. A
    // budget makes the pass finite while leaving the remainder queued for the next one.
    use ch = new RxChannel()
    let frames = 1000
    let counter =
        { new IPathOwner with
            member _.OnFrame(_, _) = 0UL
            member _.Frontier = 0UL
            member _.ProduceFill(_, _) = false
            member _.Fail _ = () }
    for _ in 1 .. frames do
        ch.Enqueue(RxFrame(0us, Array.zeroCreate<byte> 64, 64, false, 0UL))
    Assert.Equal(frames, ch.Count)
    Assert.Equal(256, ch.Drain(counter, 256))
    Assert.Equal(frames - 256, ch.Count)
    Assert.True ch.HasItems
    Assert.Equal(frames - 256, ch.Drain(counter, System.Int32.MaxValue))
    Assert.Equal(0, ch.Count)
    Assert.False ch.HasItems

[<Fact>]
let ``rx channel: repeated wakes are cleared then observed again`` () =
    // A session wakes once per round. A reused read buffer must drain the
    // descriptor completely each time, so the next wake stays observable;
    // otherwise a stale clear would swallow a later real wake.
    let ch = new Tbt.Carrier.RxChannel()
    try
        if ch.WakeFd >= 0 then
            use p = new Poller()
            // Native.read reaches libc through P/Invoke. Its first call fixes up
            // the thunk and allocates ~32 bytes once; measure the steady state,
            // which is what the per-round Array.zeroCreate in ClearWake broke.
            ch.Wake()
            ch.ClearWake()
            let mutable clearAlloc = 0L
            for _ in 1 .. 1000 do
                ch.Wake()
                p.WatchFd ch.WakeFd
                Assert.True(p.Wait 0 |> ignore; p.IsFdReadable ch.WakeFd)
                let before = GC.GetAllocatedBytesForCurrentThread()
                ch.ClearWake()
                clearAlloc <- clearAlloc + (GC.GetAllocatedBytesForCurrentThread() - before)
                // Wait a full round after the clear: a leftover count here would
                // mean ClearWake left the descriptor readable and spun the loop.
                Assert.False(p.Wait 0 |> ignore; p.IsFdReadable ch.WakeFd)
            // Native.read uses a stack Span and the 8-byte buffer belongs to the
            // channel, so clearing a wake must allocate nothing. This is what the
            // per-round Array.zeroCreate in the old code violated.
            Assert.Equal(0L, clearAlloc)
    finally
        (ch :> IDisposable).Dispose()

/// Async IO: a frame committed while the IO thread is blocked must reach the
/// socket at once. `CommitFrame` publishes `txHead` with a full fence and then
/// checks the thread's sleeping flag, while the IO loop publishes that flag with
/// a full fence before it re-scans the pending slots: the two fenced store/load
/// pairs form a Dekker handshake, so one side always sees the other. Before that
/// handshake the commit could land between the loop's last scan and `epoll_wait`,
/// and the frame then waited for the whole 1 s timeout.
[<Fact>]
let ``async path: a frame committed while the IO thread blocks leaves at once`` () =
    if not Platform.current.Value.HasEpoll then () else
    use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
    listener.Listen 1
    let a = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    a.Connect(IPEndPoint(IPAddress.Loopback, (listener.LocalEndPoint :?> IPEndPoint).Port))
    use a = a
    use b = listener.Accept()
    b.ReceiveTimeout <- 5_000
    use channel = new RxChannel()
    let group = new AsyncPollerGroup(1)
    let link = new PathLink(a, SocketTuning.probe())
    link.PathId <- 0us
    let frameLength = 1024
    link.FrameLength <- frameLength
    let path = AsyncPathLink.TakeOver(link, group, channel, 16)
    // The pool picks a path up only from its queue: TakeOver moves the socket,
    // it does not register the path with the IO thread.
    group.Add(path :> IPathIo)
    group.Start()
    try
        let p = path :> IPath
        let payload = Array.create frameLength 0x5Auy
        let readBuffer = Array.zeroCreate<byte> (8 * frameLength)
        let mutable received = 0
        let iterations = 24
        let worst = ref 0L
        for _ in 1 .. iterations do
            // Let the IO thread drain and block in its 1 s wait, so the commit lands
            // inside the window this test exists for.
            Threading.Thread.Sleep 5
            let mutable frame = Span<byte>.Empty
            Assert.True(p.TryBeginFrame(&frame))
            payload.AsSpan().CopyTo frame
            let started = Stopwatch.GetTimestamp()
            p.CommitFrame()
            let mutable delivered = false
            while not delivered do
                if b.Poll(300_000, SelectMode.SelectRead) then
                    let n = b.Receive(readBuffer)
                    Assert.True(n > 0)
                    received <- received + n
                    if received >= frameLength then delivered <- true
                else
                    failwithf "a committed frame waited more than 300 ms (%d/%d bytes)" received (frameLength * iterations)
            let elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            if elapsed > float worst.Value then worst.Value <- int64 elapsed
            received <- received - frameLength
        Assert.True(worst.Value < 300L, sprintf "worst commit-to-send %d ms" worst.Value)
    finally
        (path :> IPath).Close()
        group.Stop()
        (channel :> IDisposable).Dispose()
