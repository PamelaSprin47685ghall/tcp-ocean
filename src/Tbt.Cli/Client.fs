/// Client mode: local TCP listener; each accepted connection is one flow (spec 4 topology:
/// the TBTun client carries opaque TCP flows to the server's target).
module Tbt.Cli.Client

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Async
open Tbt.Cli.Common

/// Connect, send head and bootstrap frame, wait for the reply head and bootstrap frame.
/// `timeoutMs` is used for Connect, Send, and Receive so TCP can retransmit a JOIN
/// inside one attempt. The socket is disposed if this throws.
let private bootstrap (server: IPEndPoint) (host: string) (boot: byte[]) (windowClamp: int) (timeoutMs: int) =
    let socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
    try
        // The client carrier is kernel TCP, not a TUN: there is no tbt0-style qdisc
        // this process owns. Write gating (TCP_NOTSENT_LOWAT) keeps unsent bytes
        // small; a queue overflow, if any, is on the host path and shows up as
        // kernel retranmissions, not as a userspace virtual ACK.
        SocketTuning.path(socket, windowClamp)
        SocketTuning.fastOpenConnect socket
        socket.ReceiveTimeout <- timeoutMs
        socket.SendTimeout <- timeoutMs
        let connect = socket.ConnectAsync server
        if not (connect.Wait timeoutMs) then failwith "connect timed out"
        connect.GetAwaiter().GetResult()
        let link = PathLink(socket, SocketTuning.probe ())
        link.Queue(ReadOnlySpan(requestHead host))
        link.Queue(ReadOnlySpan boot)
        while not link.TxIdle do
            if link.Send() <> IoResult.Progress then failwith "path closed during bootstrap"
        let mutable failed = false
        let mutable reply = Span<byte>.Empty
        let mutable result: byte[] | null = null
        while isNull result do
            if link.Receive() <> IoResult.Progress then failwith "path closed during bootstrap"
            if link.TryTakeHead(&failed) && link.TryTakeFrame(&reply) then result <- reply.ToArray()
            elif failed then failwith "bad reply head"
        socket.Blocking <- false
        struct (link, nonNull result)
    with _ ->
        socket.Dispose()
        reraise()

let run (o: Options) =
    let asyncMode = AsyncMode.isEnabled ()
    let server = endpoint (o.Text "server")
    let host = if o.Has "host" then o.Text "host" else server.Address.ToString()
    let listenAt = endpoint (o.Text "listen")
    let psk = (readPsks (o.Text "psk-file")).[0]
    let pathCount = o.Int("paths", 4)
    let mss = o.Int("mss", 1448)
    let windowClamp = o.Int("window-clamp", SocketTuning.WindowClamp)
    let fecAdaptive =
        match System.Environment.GetEnvironmentVariable "TBTUN_FEC_ADAPTIVE" with
        | null -> true
        | v -> v <> "0"
    let config = { SessionConfig.defaults with WindowSteps = o.Int("window", 1024); NewRepair = o.Int("new-repair", 0) <> 0; FecAdaptive = fecAdaptive }
    let entropy = SystemEntropy()
    let struct (offerAegis, offerChaCha8, offerChaCha20, offerX25519) = offerFromOptions o
    let rttMs = max 1 (o.Int("rtt-ms", JoinBootstrap.DefaultRttMs))
    let loss = min 0.9 (max 0.0 (o.Float("loss", JoinBootstrap.DefaultLoss)))
    let timeoutMs = JoinBootstrap.socketTimeoutMs rttMs loss
    let attempts = JoinBootstrap.maxAttempts loss
    let backoff = JoinBootstrap.backoffMs rttMs
    // Fresh ClientOffer on each attempt: a nonce the server already accepted is a replay.
    let mutable accepted = ValueNone
    match JoinBootstrap.tryRepeated attempts backoff Thread.Sleep (fun _ ->
        let offer = ClientOffer(psk, Guid.NewGuid(), entropy, uint64 (unixMs ()), mss, max 1 pathCount,
                                  offerX25519 = offerX25519, offerChaCha8 = offerChaCha8,
                                  offerAegis = offerAegis, offerChaCha20 = offerChaCha20)
        let struct (link0, reply) = bootstrap server host offer.Frame windowClamp timeoutMs
        match offer.TryAccept(ReadOnlySpan reply) with
        | ValueNone ->
            link0.Close()
            failwith "handshake failed"
        | ValueSome(struct (p, hello)) -> accepted <- ValueSome(struct (link0, p, hello))) with
    | JoinBootstrap.GaveUp msg ->
        log ("handshake failed: " + msg)
        1
    | JoinBootstrap.Joined ->
        let struct (link0, p, hello) = accepted.Value
        let bridge = FlowBridge ValueNone
        let clock = CachedClock(SystemClock())
        let session = Session(Role.Client, p, bridge, clock, config)
        bridge.Attach session
        let pace = o.Int("uplink-pace", 0) <> 0
        let tupleMbit = if pace then max 0.0 (o.Float("tuple-mbit", 0.0)) else 0.0
        let rxChannel = if asyncMode then Some (new RxChannel()) else None
        let ioGroup =
            if asyncMode then
                let group = AsyncPollerGroup(4)
                group.Start()
                Some group
            else None
        let carrier = SessionCarrier(session, bridge, uplinkPace=pace, tupleMbit=tupleMbit,
                                     ?rxChannel=rxChannel, ?asyncIo=ioGroup)
        carrier.AddPath link0
        let maxPaths = int hello.MaxPaths
        // Forward as soon as path 0 is up; remaining JOINs run with bounded
        // concurrency and are attached on this thread via `pending`.
        log ("session up: S=" + string p.Symbol + " paths=" + string carrier.Paths.Count
             + " window_clamp=" + string windowClamp + " " + describeOffer p.Keys.Suite p.Caps)
        let concurrency = JoinBootstrap.clampConcurrency (o.Int("join-concurrency", JoinBootstrap.DefaultConcurrency))
        let pending = ConcurrentQueue<PathLink>()
        let mutable readyLogged = false
        let joinOne (id: int) =
            match JoinBootstrap.tryRepeated attempts backoff Thread.Sleep (fun i ->
                let inc = uint32 (i + 1)
                let join = JoinOffer(p.Keys.PathKey, entropy, p.Keys.SessionId, p.Symbol, p.Caps, maxPaths, uint16 id, inc, mss, p.Keys.Suite)
                let struct (link, ack) = bootstrap server host join.Frame windowClamp timeoutMs
                if (join.TryAccept(ReadOnlySpan ack)).IsSome then
                    link.PathId <- uint16 id
                    link.Incarnation <- inc
                    pending.Enqueue link
                else
                    link.Close()
                    failwith "join refused") with
            | JoinBootstrap.Joined -> ()
            | JoinBootstrap.GaveUp msg -> log ("path " + string id + ": " + msg)
        let ids = [| 1 .. min pathCount maxPaths - 1 |]
        let joinTask =
            if ids.Length = 0 then Task.CompletedTask
            else Task.Run(fun () -> JoinBootstrap.runBounded concurrency ids joinOne)
        let attachJoined () =
            let mutable link = Unchecked.defaultof<PathLink>
            while pending.TryDequeue(&link) do
                carrier.AddPath link
                log ("path " + string link.PathId + " joined, paths=" + string carrier.Paths.Count)
            if not readyLogged && joinTask.IsCompleted && pending.IsEmpty then
                readyLogged <- true
                log ("paths ready: paths=" + string carrier.Paths.Count)
        attachJoined ()
        let listener = new Socket(listenAt.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        listener.Bind listenAt
        SocketTuning.fastOpenListen(listener, 512)
        listener.Listen 128
        listener.Blocking <- false
        use poller = new Poller()
        let statsMs = int64 (o.Int("stats-s", 0)) * 1000L
        let mutable lastStats = Environment.TickCount64
        // SIGINT/SIGTERM (or Ctrl-C on Windows): close the session with SESSION_CLOSE (spec 6.10a), at most 2 s.
        let mutable stopAt = 0L
        use _interrupt = onInterrupt (fun () -> if stopAt = 0L then stopAt <- Environment.TickCount64 + 2000L)
        // Registration must happen before Wait: a WatchRead after Wait clears
        // the readiness Wait just filled. Accept runs after Wait, where the
        // listener's readiness is still visible.
        let watchListener (pl: Poller) = pl.WatchRead listener
        let housekeeping () =
            attachJoined ()
            if stopAt > 0L && session.CloseState = CloseState.Open then
                log "closing the session"
                session.Close 0uy |> ignore
            if statsMs > 0L && Environment.TickCount64 - lastStats >= statsMs then
                lastStats <- Environment.TickCount64
                log ("stats " + carrier.Describe() + " rss_kib=" + string (rssKiB ()))
                Tbt.Contracts.Trace.flush ()
        let acceptNow (pl: Poller) =
            if pl.IsReadable listener then
                let mutable go = true
                while go do
                    match tryAccept listener with
                    | null -> go <- false
                    | s -> if not (bridge.TryAdd s) then s.Dispose()
        let beforeWait (pl: Poller) =
            watchListener pl
            housekeeping ()
        let serve (pl: Poller) =
            housekeeping ()
            acceptNow pl

        if asyncMode then
            log "async mode: session loop on its own thread (TBTUN_ASYNC_MODE)"
            let coordinator =
                match ioGroup, rxChannel with
                | Some group, Some rx -> AsyncSessionCoordinator.createWith carrier clock group rx
                | _ -> AsyncSessionCoordinator.create carrier clock
            coordinator.BeforeWait <- watchListener
            coordinator.BeforeService <- serve
            coordinator.Start()
            // Block on the coordinator's completion signal. The only timed part
            // is an explicit stop deadline, which is a single wait, not a poll.
            let mutable go = true
            while go && carrier.Alive
                  && not (stopAt > 0L && Environment.TickCount64 > stopAt) do
                let waitMs =
                    if stopAt <= 0L then Timeout.Infinite
                    else max 1 (int (stopAt - Environment.TickCount64))
                go <- not (coordinator.Stopped.Wait waitMs)
            coordinator.Stop()
        else
            while carrier.Alive && not (stopAt > 0L && Environment.TickCount64 > stopAt) do
                beforeWait poller
                carrier.Watch poller
                poller.Wait(carrier.WaitMs())
                clock.Tick() |> ignore
                acceptNow poller
                carrier.Service poller
        log (if session.Closed then "session closed" else "all paths closed")
        try joinTask.Wait 2000 |> ignore with _ -> ()
        if not asyncMode then attachJoined ()
        carrier.Close()
        if session.Closed then 0 else 1
