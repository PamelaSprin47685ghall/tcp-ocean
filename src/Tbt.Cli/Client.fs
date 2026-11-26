/// Client mode: local TCP listener; each accepted connection is one flow (spec 4 topology:
/// the TBTun client carries opaque TCP flows to the server's target).
module Tbt.Cli.Client

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Collections.Generic
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Async
open Tbt.Cli.Common

/// A finished background JOIN, delivered to the session thread. The background
/// task never touches `SessionCarrier`; only this event crosses threads.
type private PathEvent =
    | PathJoined of uint16 * PathLink
    | PathFailed of uint16 * string
    /// A refresh JOIN (higher incarnation of a live id) succeeded or failed.
    | PathRefreshed of uint16 * PathLink
    | PathRefreshFailed of uint16 * string

let private queueBootstrap (link: PathLink) (host: string) (boot: byte[]) =
    link.Queue(ReadOnlySpan(requestHead host))
    link.Queue(ReadOnlySpan boot)

let private tryExtractBootstrapReply (link: PathLink) (failed: byref<bool>) : byte[] | null =
    let mutable reply = Span<byte>.Empty
    if link.TryTakeHead(&failed) && link.TryTakeFrame(&reply) then reply.ToArray()
    else null

let private tryAcceptJoinReply (join: JoinOffer) (ack: byte[]) =
    (join.TryAccept(ReadOnlySpan ack)).IsSome

/// Connect, send head and bootstrap frame, wait for the reply head and bootstrap frame
/// without blocking OS threads or thread-pool workers. `timeoutMs` bounds the full attempt.
let private bootstrapAsync (server: IPEndPoint) (host: string) (boot: byte[]) (windowClamp: int) (notsentLowat: int) (userTimeoutMs: int) (timeoutMs: int) (stopToken: CancellationToken) : Task<struct (PathLink * byte[])> =
    task {
        let socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        use cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken)
        cts.CancelAfter timeoutMs
        let ct = cts.Token
        let mutable err: exn | null = null
        let mutable okResult: struct (PathLink * byte[]) = Unchecked.defaultof<_>
        try
            SocketTuning.pathWith(socket, windowClamp, notsentLowat, userTimeoutMs)
            SocketTuning.fastOpenConnect socket
            do! socket.ConnectAsync(server, ct).AsTask()
            let link = PathLink(socket, SocketTuning.probe ())
            queueBootstrap link host boot
            let! sendRes = link.SendAsync(ct).AsTask()
            if sendRes <> IoResult.Progress then failwith "path closed during bootstrap"
            let mutable failed = false
            let mutable result: byte[] | null = null
            while isNull result do
                let extracted = tryExtractBootstrapReply link &failed
                if not (isNull extracted) then
                    result <- extracted
                elif failed then
                    failwith "bad reply head"
                else
                    let! rxRes = link.ReceiveAsync(ct).AsTask()
                    if rxRes <> IoResult.Progress then failwith "path closed during bootstrap"
            socket.Blocking <- false
            okResult <- struct (link, nonNull result)
        with ex ->
            SocketTuning.closeGraceful socket
            err <- ex
        match err with
        | null -> return okResult
        | ex ->
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw()
            return okResult
    }

let private bootstrap (server: IPEndPoint) (host: string) (boot: byte[]) (windowClamp: int) (notsentLowat: int) (userTimeoutMs: int) (timeoutMs: int) =
    let socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
    try
        SocketTuning.pathWith(socket, windowClamp, notsentLowat, userTimeoutMs)
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
        SocketTuning.closeGraceful socket
        reraise()

let private runCore (o: Options) =
    validateCodec o
    let asyncMode = AsyncMode.isEnabled ()
    let server = endpoint (o.Text "server")
    let hopPorts =
        if o.Has "server-ports" then portList (o.Text "server-ports")
        else [| server.Port |]
    let mutable hop = 0
    let nextServer () =
        let port = hopPorts.[hop % hopPorts.Length]
        hop <- hop + 1
        IPEndPoint(server.Address, port)
    let host = if o.Has "host" then o.Text "host" else server.Address.ToString()
    let listenAt = endpoint (o.Text "listen")
    let psks = readPsks (o.Text "psk-file")
    let pathCount = bounded o "paths" 4 1 256
    let mss = bounded o "mss" 1448 576 9000
    let windowClamp = o.Int("window-clamp", SocketTuning.WindowClamp)
    let notsentLowat = SocketTuning.notsentLowatFor (o.Int("notsent-lowat", SocketTuning.NotSentLimit))
    let userTimeoutMs = SocketTuning.userTimeoutMsFor (o.Int("user-timeout-ms", SocketTuning.UserTimeoutMs))
    let txSlots = SocketTuning.txSlotsFor (o.Int("tx-slots", 64))
    let fecAdaptive =
        match System.Environment.GetEnvironmentVariable "TBTUN_FEC_ADAPTIVE" with
        | null -> true
        | v -> v <> "0"
    let config = { SessionConfig.defaults with Credit = creditFromOptions o; WindowSteps = o.Int("window", 1024); NewRepair = o.Int("new-repair", 0) <> 0; FecAdaptive = fecAdaptive }
    let entropy = SystemEntropy()
    let struct (offerAegis, offerChaCha8, offerChaCha20, offerX25519) = offerFromOptions o
    let rttMs = max 1 (o.Int("rtt-ms", JoinBootstrap.DefaultRttMs))
    let loss = min 0.9 (max 0.0 (o.Float("loss", JoinBootstrap.DefaultLoss)))
    let timeoutMs = JoinBootstrap.socketTimeoutMs rttMs loss
    let attempts = JoinBootstrap.maxAttempts loss
    let backoff = JoinBootstrap.backoffMs rttMs
    // Fresh ClientOffer on each attempt: a nonce the server already accepted is a replay.
    let mutable accepted = ValueNone
    match JoinBootstrap.tryRepeated attempts backoff Thread.Sleep (fun attempt ->
        // Rotate through every line in the PSK file: a peer that has rolled to a later key still joins.
        let offer = ClientOffer(psks.[attempt % psks.Length], Guid.NewGuid(), entropy, uint64 (unixMs ()), mss, max 1 pathCount,
                                  offerX25519 = offerX25519, offerChaCha8 = offerChaCha8,
                                  offerAegis = offerAegis, offerChaCha20 = offerChaCha20)
        let struct (link0, reply) = bootstrap (nextServer ()) host offer.Frame windowClamp notsentLowat userTimeoutMs timeoutMs
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
        let gatePass2 = max 4096 (o.Int("gate-pass2", SocketTuning.GatePass2Limit))
        let rxChannel = if asyncMode then Some (new RxChannel()) else None
        // The keepalive predicate needs the pool, which is built below.
        let mutable poolRef = Unchecked.defaultof<PathPool>
        let ioGroup =
            if asyncMode then
                let group = AsyncPollerGroup(o.Int("io-threads", min 4 (max 1 Environment.ProcessorCount)))
                group.Start()
                Some group
            else None
        let requestedMin =
            defaultMinActivePaths
                (if o.Has "min-active-paths" then Some (o.Int("min-active-paths", 16)) else None)
                pathCount
        let carrier = SessionCarrier(session, bridge, uplinkPace=pace, tupleMbit=tupleMbit,
                                     ?rxChannel=rxChannel, ?asyncIo=ioGroup, gatePass2=gatePass2,
                                     txSlots=txSlots, minActivePaths=max 1 requestedMin)
        carrier.KeepAlive <-
            Some (fun () ->
                not (isNull (box poolRef)) && not session.Closed
                && carrier.PathCount < poolRef.Target)
        carrier.AddPath link0
        let maxPaths = int hello.MaxPaths
        let targetPaths = min pathCount maxPaths
        // Forward as soon as path 0 is up; remaining JOINs run asynchronously with bounded
        // concurrency and are attached on this thread via `pending`.
        log ("session up: S=" + string p.Symbol + " paths=" + string carrier.PathCount
             + " target_paths=" + string targetPaths + " server_max_paths=" + string maxPaths
             + " window_clamp=" + string windowClamp + " " + describeOffer p.Keys.Suite p.Caps)
        if pathCount > maxPaths then
            log ("note: requested --paths " + string pathCount + " exceeds server --max-paths "
                 + string maxPaths + "; aligned to " + string targetPaths)
        let concurrency = JoinBootstrap.clampConcurrency (o.Int("join-concurrency", JoinBootstrap.DefaultConcurrency))
        let mutable stopAt = 0L
        let pool = PathPool(targetPaths, concurrency, 100_000_000L, 2_000_000_000L, max 1 attempts)
        poolRef <- pool
        // Health-targeted refresh (default "degraded"): zero idle churn/allocation, never kills
        // healthy fast paths; only refreshes aged paths that show genuine Loss/BBR collapse.
        // --path-refresh-mode blind restores the legacy fixed-age rolling refresh for A/B checks.
        let refreshBlind = o.TextOr("path-refresh-mode", "degraded").ToLowerInvariant() = "blind"
        let refresher =
            PathRefresher(int64 (max 0 (o.Int("path-refresh-s", 60))) * 1_000_000_000L,
                          max 0 (o.Int("path-refresh-per-tick", 1)),
                          max 0 (o.Int("path-refresh-idle-per-tick", if refreshBlind then 8 else 0)),
                          int64 (max 50 (o.Int("path-refresh-tick-ms", 1000))) * 1_000_000L,
                          max 1 (o.Int("path-refresh-inflight", 12)),
                          10_000_000_000L)
        refresher.Jitter <- float (max 0 (min 90 (o.Int("path-refresh-jitter-pct", 0)))) / 100.0
        let events = ConcurrentQueue<PathEvent>()
        let pending = ResizeArray<PathLink>()
        let applying = HashSet<uint16>()
        let mutable readyLogged = false
        use joinCts = new CancellationTokenSource()
        let joinSem = new SemaphoreSlim(concurrency, concurrency)
        let wakeCoordinator () =
            match rxChannel with
            | Some ch -> ch.Wake()
            | None -> ()
        // Non-blocking async JOIN: no LongRunning OS threads, no Thread.Sleep backoff.
        let joinOnceAsync (id: uint16) (inc: uint32) : Task<PathLink> =
            task {
                let join = JoinOffer(p.Keys.PathKey, entropy, p.Keys.SessionId, p.Symbol, p.Caps, maxPaths, id, inc, mss, p.Keys.Suite)
                let! struct (link, ack) = bootstrapAsync (nextServer ()) host join.Frame windowClamp notsentLowat userTimeoutMs timeoutMs joinCts.Token
                if tryAcceptJoinReply join ack then
                    link.PathId <- id
                    link.Incarnation <- inc
                    return link
                else
                    link.Close()
                    return failwith "join refused"
            }
        let joinOneAsync (id: uint16) (baseInc: uint32) : Task =
            task {
                let mutable acquired = false
                try
                    do! joinSem.WaitAsync joinCts.Token
                    acquired <- true
                    let mutable attempt = 0
                    let mutable joined = false
                    let mutable lastErr = ""
                    while not joined && attempt < attempts && not joinCts.IsCancellationRequested do
                        let mutable retryDelay = 0
                        try
                            let! link = joinOnceAsync id (baseInc + uint32 attempt)
                            events.Enqueue(PathJoined(id, link))
                            joined <- true
                        with ex ->
                            lastErr <- ex.Message
                            attempt <- attempt + 1
                            if attempt < attempts && not joinCts.IsCancellationRequested then
                                retryDelay <- backoff (attempt - 1)
                        if retryDelay > 0 then
                            do! Task.Delay(retryDelay, joinCts.Token)
                    if not joined then
                        events.Enqueue(PathFailed(id, lastErr))
                with
                | :? OperationCanceledException -> ()
                | :? ObjectDisposedException -> ()
                if acquired then
                    try joinSem.Release() |> ignore with _ -> ()
                wakeCoordinator ()
            }
        // One attempt: on failure the old connection simply stays.
        let refreshOneAsync (id: uint16) (inc: uint32) : Task =
            task {
                try
                    let! link = joinOnceAsync id inc
                    events.Enqueue(PathRefreshed(id, link))
                with ex ->
                    events.Enqueue(PathRefreshFailed(id, ex.Message))
                wakeCoordinator ()
            }
        let refreshStart = Dictionary<uint16, int64>()
        let mutable lastFramesIn = 0L
        let mutable lastIdleCheckNs = 0L
        let mutable sessionIdle = false
        let mutable refreshWakePending = false
        let mutable refreshMsSum = 0L
        let mutable refreshMsMax = 0L
        let refreshDone (id: uint16) =
            match refreshStart.TryGetValue id with
            | true, t0 ->
                let ms = (clock.FreshNs() - t0) / 1_000_000L
                refreshMsSum <- refreshMsSum + ms
                refreshMsMax <- max refreshMsMax ms
                refreshStart.Remove id |> ignore
            | _ -> ()
        let drainEvents () =
            let mutable ev = Unchecked.defaultof<PathEvent>
            while events.TryDequeue(&ev) do
                match ev with
                | PathJoined(id, link) ->
                    pool.Joined id
                    refresher.NoteJoined(id, clock.FreshNs())
                    if session.Closed || stopAt > 0L then link.Close()
                    else pending.Add link
                | PathFailed(id, msg) ->
                    pool.Failed(id, clock.FreshNs())
                    log ("path " + string id + ": " + msg)
                | PathRefreshed(id, link) ->
                    pool.Joined id
                    refreshDone id
                    refresher.OnReplaced(id, clock.FreshNs())
                    if session.Closed || stopAt > 0L then link.Close()
                    else pending.Add link
                | PathRefreshFailed(id, _) ->
                    pool.Joined id
                    refreshDone id
                    refresher.OnFailed(id, clock.FreshNs())
        let attachJoined () =
            if pending.Count > 0 then
                for link in pending do
                    // A refresh replaces the live connection of the same id; the server
                    // already swapped it when it accepted the higher incarnation.
                    let refresh = carrier.HasPath link.PathId
                    if refresh then carrier.RemovePath link.PathId
                    carrier.AddPath link
                    applying.Add link.PathId |> ignore
                    if not refresh || not readyLogged then
                        log ("path " + string link.PathId + " joined, paths=" + string carrier.PathCount)
                pending.Clear()
            // In async mode AddPath posts to the carrier and the session thread
            // applies it. Paths.Count read before that queue drains reported
            // paths=1 in the e2e handshake check while the session already had
            // every path. Report readiness only after the posted adds are applied.
            if carrier.PathOpsPending = 0 then applying.Clear()
            if not readyLogged && pool.InFlight = 0 && pending.Count = 0
               && carrier.PathOpsPending = 0 && carrier.PathCount >= pool.Target then
                readyLogged <- true
                log ("paths ready: paths=" + string carrier.PathCount)
        let presentIds () =
            let s = HashSet<uint16>()
            // Path 0 is the one the handshake itself created. While its first
            // PathAdd is still queued nothing for it is in `carrier.Paths`, so
            // hide it: otherwise the first pass would JOIN a second path 0
            // beside the handshake path. Once the add is applied, `Paths`
            // reports it and, after a loss, the pool rebuilds it.
            if carrier.PathOpsPending > 0 then s.Add 0us |> ignore
            for id in carrier.PathIds do s.Add id |> ignore
            for link in pending do s.Add link.PathId |> ignore
            for id in applying do s.Add id |> ignore
            s
        let plan () =
            if not session.Closed && stopAt = 0L then
                let work = pool.Request(clock.FreshNs(), presentIds ())
                for i in 0 .. work.Length - 1 do
                    let struct (id, baseInc) = work.[i]
                    joinOneAsync id baseInc |> ignore
                if readyLogged then
                    // Idle: fewer than ~100 cells arrived since the last look.
                    let now = clock.FreshNs()
                    if now - lastIdleCheckNs >= 1_000_000_000L then
                        let fin = carrier.FramesIn
                        sessionIdle <- fin - lastFramesIn < 100L
                        lastFramesIn <- fin
                        lastIdleCheckNs <- now
                    // Only blind-mode refresh needs an idle 1 s timer; degraded mode does zero work when idle.
                    if refreshBlind && refresher.Enabled && not refreshWakePending then
                        match rxChannel with
                        | Some ch ->
                            refreshWakePending <- true
                            task {
                                do! Task.Delay 1000
                                refreshWakePending <- false
                                ch.Wake()
                            } |> ignore
                        | None -> ()
                    let due =
                        if refreshBlind then refresher.Due(now, carrier.PathIds, true, sessionIdle)
                        else
                            let degraded = refresher.DueDegraded(now, carrier.SnapshotPathHealth(), true, sessionIdle)
                            let rotateS = o.Int("path-rotate-s", 60)
                            if rotateS <= 0 || degraded.Length > 0 then degraded
                            else
                                let rotated =
                                    refresher.DueRotation(now, carrier.SnapshotPathHealth(), carrier.PlateauIds(),
                                                          true, sessionIdle, int64 rotateS * 1_000_000_000L)
                                if rotated.Length > 0 then rotated else degraded
                    for id in due do
                        match pool.Reserve id with
                        | ValueSome inc ->
                            refreshStart.[id] <- clock.FreshNs()
                            refreshOneAsync id inc |> ignore
                        | ValueNone -> refresher.OnFailed(id, clock.FreshNs())
        let closePool () =
            drainEvents ()
            for link in pending do link.Close()
            pending.Clear()
        attachJoined ()
        plan ()
        let listener = new Socket(listenAt.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        listener.Bind listenAt
        SocketTuning.fastOpenListen(listener, 512)
        listener.Listen 128
        listener.Blocking <- false
        use poller = new Poller()
        let statsMs = int64 (o.Int("stats-s", 0)) * 1000L
        let mutable lastStats = Environment.TickCount64
        // SIGINT/SIGTERM (or Ctrl-C on Windows): close the session with SESSION_CLOSE (spec 6.10a), at most 2 s.
        // The async main thread blocks on the coordinator's signal; the interrupt must
        // wake it, else SIGTERM set stopAt and nobody looked at it again (a client with
        // no path ignored SIGTERM until kill -9).
        use interrupted = new ManualResetEventSlim(false)
        use _interrupt = onInterrupt (fun () ->
            if stopAt = 0L then stopAt <- Environment.TickCount64 + 2000L
            interrupted.Set())
        // Registration must happen before Wait: a WatchRead after Wait clears
        // the readiness Wait just filled. Accept runs after Wait, where the
        // listener's readiness is still visible.
        let watchListener (pl: Poller) = pl.WatchRead listener
        let noPathExitMs = int64 (max 0 (o.Int("no-path-exit-ms", 3000)))
        let noPathWatch = NoPathWatch(noPathExitMs * 1_000_000L, int64 (max 0 (o.Int("no-path-cap-s", 30))) * 1_000_000_000L)
        let mutable noPathWakePending = false
        let housekeeping () =
            drainEvents ()
            attachJoined ()
            plan ()
            // Every path is gone but the pool still wants paths: the handshake
            // session itself died, and a JOIN cannot rebuild it in this process.
            // Exit so the service manager starts a fresh handshake. JOINs still in
            // flight (initial joins right after the handshake, or rejoins after a
            // mass reset into a session the server keeps --no-path-grace-s) hold the
            // exit off; see NoPathWatch.
            let noPath = carrier.PathOpsPending = 0 && carrier.PathCount = 0 && pending.Count = 0
            let exitNow = noPathWatch.ShouldExit(clock.FreshNs(), noPath, pool.InFlight + events.Count)
            if noPathWatch.Watching && not noPathWakePending then
                // Nothing else may wake an empty session: come back every 250 ms.
                match rxChannel with
                | Some ch ->
                    noPathWakePending <- true
                    Task.Run(fun () ->
                        Thread.Sleep 250
                        noPathWakePending <- false
                        ch.Wake()) |> ignore
                | None -> ()
            if stopAt = 0L && not session.Closed && pool.Target > 0 && exitNow then
                log "session lost: no path remains, exiting for restart"
                Environment.Exit 75
            if stopAt > 0L && session.CloseState = CloseState.Open then
                log "closing the session"
                session.Close 0uy |> ignore
            // In async mode the coordinator emits the stats line on its own
            // thread, where the per-window counters live; this path serves the
            // synchronous fallback.
            if not asyncMode && statsMs > 0L && Environment.TickCount64 - lastStats >= statsMs then
                lastStats <- Environment.TickCount64
                log ("stats " + carrier.Describe() + " rss_kib=" + string (rssKiB ())
                         + " refresh=" + string refresher.StartedCount + "/" + string refresher.ReplacedCount + "/" + string refresher.FailedCount
                         + " refresh_ms=" + string (if refresher.ReplacedCount + refresher.FailedCount > 0L then refreshMsSum / (refresher.ReplacedCount + refresher.FailedCount) else 0L) + "/" + string refreshMsMax)
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
            coordinator.StatsEveryMs <- statsMs
            coordinator.OnStats <-
                (fun () ->
                    lastStats <- Environment.TickCount64
                    log ("stats " + carrier.Describe() + " rss_kib=" + string (rssKiB ())
                         + " refresh=" + string refresher.StartedCount + "/" + string refresher.ReplacedCount + "/" + string refresher.FailedCount
                         + " refresh_ms=" + string (if refresher.ReplacedCount + refresher.FailedCount > 0L then refreshMsSum / (refresher.ReplacedCount + refresher.FailedCount) else 0L) + "/" + string refreshMsMax
                         + coordinator.Stats.DescribeWindow())
                    Tbt.Contracts.Trace.flush ())
            coordinator.Start()
            // Block on the coordinator's completion signal. The only timed part
            // is an explicit stop deadline, which is a single wait, not a poll.
            let mutable go = true
            while go && carrier.Alive
                  && not (stopAt > 0L && Environment.TickCount64 > stopAt) do
                let waitMs =
                    if stopAt <= 0L then Timeout.Infinite
                    else max 1 (int (stopAt - Environment.TickCount64))
                if stopAt <= 0L then
                    let i = WaitHandle.WaitAny([| coordinator.Stopped.WaitHandle; interrupted.WaitHandle |], waitMs)
                    go <- i <> 0
                else go <- not (coordinator.Stopped.Wait waitMs)
            coordinator.Stop()
        else
            while carrier.Alive && not (stopAt > 0L && Environment.TickCount64 > stopAt) do
                beforeWait poller
                carrier.Watch poller
                // WaitMs() is milliseconds; Poller.Wait takes microseconds.
                // While a JOIN is in flight, cap the wait so the result reaches
                // this thread without a second event source.
                let waitUs = carrier.WaitMs() * 1000
                let waitUs = if pool.InFlight > 0 then min waitUs 50_000 else waitUs
                poller.Wait waitUs
                clock.Tick() |> ignore
                acceptNow poller
                carrier.Service poller
        log (if session.Closed then "session closed" else "all paths closed")
        joinCts.Cancel()
        if not asyncMode then attachJoined ()
        closePool ()
        carrier.Close()
        if session.Closed then 0 else 1

let run (o: Options) =
    if o.Has "help" then
        Console.Error.WriteLine "usage: tbtun client --server HOST:PORT --listen ADDR:PORT --psk-file FILE [--paths 4] [--server-ports PORTS] [--min-active-paths min(paths,64)] [--window 1024] [--join-concurrency 32] [--path-refresh-s 60] [--path-rotate-s 60] [--path-refresh-mode degraded|blind] [--stats-s 0]"
        0
    else runCore o
