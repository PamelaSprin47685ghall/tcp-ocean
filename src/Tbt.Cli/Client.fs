/// Client mode: local TCP listener; each accepted connection is one flow (spec 4 topology:
/// the TBTun client carries opaque TCP flows to the server's target).
module Tbt.Cli.Client

open System
open System.Net
open System.Net.Sockets
open System.Runtime.InteropServices
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Cli.Common

/// Connect, send head and bootstrap frame, wait for the reply head and bootstrap frame.
let private bootstrap (server: IPEndPoint) (host: string) (boot: byte[]) (windowClamp: int) =
    let socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
    SocketTuning.path(socket, windowClamp)
    socket.ReceiveTimeout <- 10_000
    socket.SendTimeout <- 10_000
    socket.Connect server
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

let run (o: Options) =
    let server = endpoint (o.Text "server")
    let host = if o.Has "host" then o.Text "host" else server.Address.ToString()
    let listenAt = endpoint (o.Text "listen")
    let psk = (readPsks (o.Text "psk-file")).[0]
    let pathCount = o.Int("paths", 4)
    let mss = o.Int("mss", 1448)
    let windowClamp = o.Int("window-clamp", SocketTuning.WindowClamp)
    let config = { SessionConfig.defaults with WindowSteps = o.Int("window", 1024) }
    let entropy = SystemEntropy()
    let offer = ClientOffer(psk, Guid.NewGuid(), entropy, uint64 (unixMs ()), mss, max 1 pathCount)
    let struct (link0, reply) = bootstrap server host offer.Frame windowClamp
    match offer.TryAccept(ReadOnlySpan reply) with
    | ValueNone ->
        log "handshake failed"
        1
    | ValueSome(struct (p, hello)) ->
        let bridge = FlowBridge ValueNone
        let session = Session(Role.Client, p, bridge, SystemClock(), config)
        bridge.Attach session
        let carrier = SessionCarrier(session, bridge)
        carrier.AddPath link0
        let maxPaths = int hello.MaxPaths
        for id in 1 .. min pathCount maxPaths - 1 do
            let join = JoinOffer(p.Keys.PathKey, entropy, p.Keys.SessionId, p.Symbol, p.Caps, maxPaths, uint16 id, 1u, mss)
            try
                let struct (link, ack) = bootstrap server host join.Frame windowClamp
                if (join.TryAccept(ReadOnlySpan ack)).IsSome then
                    link.PathId <- uint16 id
                    link.Incarnation <- 1u
                    carrier.AddPath link
                else
                    log ("path " + string id + ": join refused")
                    link.Close()
            with :? SocketException as e -> log ("path " + string id + ": " + e.Message)
        log ("session up: S=" + string p.Symbol + " paths=" + string carrier.Paths.Count + " window_clamp=" + string windowClamp)
        let listener = new Socket(listenAt.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        listener.Bind listenAt
        listener.Listen 128
        listener.Blocking <- false
        use poller = new Poller()
        let statsMs = int64 (o.Int("stats-s", 0)) * 1000L
        let mutable lastStats = Environment.TickCount64
        // SIGINT/SIGTERM: close the session with SESSION_CLOSE (spec 6.10a), at most 2 s.
        let mutable stopAt = 0L
        let onSignal (c: PosixSignalContext) =
            c.Cancel <- true
            if stopAt = 0L then stopAt <- Environment.TickCount64 + 2000L
        use _int = PosixSignalRegistration.Create(PosixSignal.SIGINT, onSignal)
        use _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, onSignal)
        while carrier.Alive && not (stopAt > 0L && Environment.TickCount64 > stopAt) do
            if stopAt > 0L && session.CloseState = CloseState.Open then
                log "closing the session"
                session.Close 0uy |> ignore
            if statsMs > 0L && Environment.TickCount64 - lastStats >= statsMs then
                lastStats <- Environment.TickCount64
                log ("stats " + carrier.Describe() + " rss_kib=" + string (rssKiB ()))
                Tbt.Contracts.Trace.flush ()
            poller.WatchRead listener
            carrier.Watch poller
            poller.Wait 5000
            if poller.IsReadable listener then
                let mutable go = true
                while go do
                    match tryAccept listener with
                    | null -> go <- false
                    | s -> if not (bridge.TryAdd s) then s.Dispose()
            carrier.Service poller
        log (if session.Closed then "session closed" else "all paths closed")
        carrier.Close()
        if session.Closed then 0 else 1
