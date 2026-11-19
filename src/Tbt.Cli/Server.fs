/// Server mode: TCP listener for paths; each flow connects to the target (spec 6.5.9
/// bootstrap classification; failures stay silent).
module Tbt.Cli.Server

open System
open System.Collections.Generic
open System.Net
open System.Diagnostics
open System.Net.Sockets
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Async
open Tbt.Cli.Common

/// A path before its bootstrap is classified.
[<Sealed>]
type private Pending(link: PathLink, since: int64) =
    member _.Link = link
    member _.Since = since
    /// Not ours: read and drop until the peer closes or the time runs out.
    member val Silent = false with get, set

[<Sealed>]
type private Entry(carrier: SessionCarrier, maxPaths: int, admission: JoinAdmission, coordinator: AsyncSessionCoordinator option) =
    member _.Carrier = carrier
    member _.MaxPaths = maxPaths
    member _.Admission = admission
    member _.Coordinator = coordinator

let run (o: Options) =
    let asyncMode = AsyncMode.isEnabled ()
    if asyncMode then log "async mode: one poller and one thread per session (TBTUN_ASYNC_MODE)"
    let mutable asyncGroup: AsyncPollerGroup option = None
    let ensureIoPool () =
        match asyncGroup with
        | Some group -> group
        | None ->
            let group = AsyncPollerGroup(4)
            group.Start()
            log ("async IO pool: " + string group.ThreadCount + " threads")
            asyncGroup <- Some group
            group
    let listenAt = if o.Has "listen" then ValueSome(endpoint (o.Text "listen")) else ValueNone
    let target = endpoint (o.Text "target")
    let psks = readPsks (o.Text "psk-file")
    let mss = o.Int("mss", 1448)
    let maxPaths = o.Int("max-paths", 64)
    let windowClamp = o.Int("window-clamp", SocketTuning.WindowClamp)
    let statsMs = int64 (o.Int("stats-s", 0)) * 1000L
    // "full" = always-read + read-after-Service, each Receive capped at
    // TunRecvBudget so Service / downlink / ACKs interleave (virtual-ACK grace).
    // "adaptive" = same always-read, but each Receive runs until EAGAIN and
    // yields every TunRecvYieldUs (or when a path/session ACK is due).
    // "main" = poller-gated Receive, 4096 cap, no post-Service read.
    let drainMode =
        match o.TextOr("tun-drain", "adaptive").ToLowerInvariant() with
        | "main" -> "main"
        | "adaptive" -> "adaptive"
        | _ -> "full"
    let recvBudget =
        let fallback =
            match drainMode with
            | "main" -> 4096
            | "adaptive" -> 1_000_000
            | _ -> SocketTuning.TunRecvBudget
        o.Int("tun-recv-budget", fallback)
    let recvYieldUs = max 1 (o.Int("tun-recv-yield-us", SocketTuning.TunRecvYieldUs))
    // --tun-sndbuf: 0 = leave the kernel default; -1 = window_clamp × paths
    // (applied when the session comes up); >0 = that many bytes. Default is
    // TunSndBuf (2 MB).
    let sndBufMode = o.Int("tun-sndbuf", SocketTuning.TunSndBuf)
    let fecAdaptive =
        match System.Environment.GetEnvironmentVariable "TBTUN_FEC_ADAPTIVE" with
        | null -> true
        | v -> v <> "0"
    let config = { SessionConfig.defaults with WindowSteps = o.Int("window", 1024); MaxPaths = maxPaths; NewRepair = o.Int("new-repair", 0) <> 0; FecAdaptive = fecAdaptive }
    let entropy = SystemEntropy()
    let clock = CachedClock(SystemClock())
    let probe = SocketTuning.probe ()
    let struct (offerAegis, offerChaCha8, offerChaCha20, offerX25519) = offerFromOptions o
    let offer = ServerOffer(psks, entropy, uint16 config.WindowSteps, maxPaths,
                            offerX25519 = offerX25519, offerChaCha8 = offerChaCha8,
                            offerAegis = offerAegis, offerChaCha20 = offerChaCha20)
    let pending = List<Pending>()
    let sessions = List<Entry>()
    // Kernel TCP listener for paths (the baseline carrier); optional when a TUN is used.
    let listener: Socket | null =
        match listenAt with
        | ValueSome at ->
            let l = new Socket(at.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            l.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)
            l.Bind at
            SocketTuning.fastOpenListen(l, 512)
            l.Listen 512
            l.Blocking <- false
            log ("server on " + at.ToString() + " (kernel TCP) -> " + target.ToString())
            l
        | ValueNone -> null
    // User-space TCP on a TUN device (spec 4.1): the server's carrier for paths.
    let tun: TunStack | null =
        if o.Has "tun" then
            let at = endpoint (o.Text "tun-listen")
            let qlen = o.Int("tun-txqueuelen", SocketTuning.TunTxQueueLen)
            let openSnd =
                if sndBufMode <= 0 then 0 else sndBufMode
            match TunStack.Open(o.Text "tun", at, clock, entropy, maxPaths * 64, windowClamp, qlen, openSnd) with
            | null -> failwith ("cannot open TUN device " + o.Text "tun")
            | t ->
                let qmsg =
                    if qlen <= 0 then "txqueuelen=left-default"
                    elif t.TxQueueOk then "txqueuelen=" + string t.TxQueueLen
                    else "txqueuelen=" + string qlen + " failed errno=" + string t.TxQueueErrno + " (need CAP_NET_ADMIN; continuing)"
                let bmsg =
                    if sndBufMode < 0 then "tun_sndbuf=auto (window_clamp x paths, at session)"
                    elif sndBufMode = 0 then "tun_sndbuf=left-default"
                    elif t.SndBufOk then "tun_sndbuf=" + string t.SndBuf
                    else "tun_sndbuf=" + string t.SndBuf + " failed errno=" + string t.SndBufErrno
                let ymsg = if drainMode = "adaptive" then " recv_yield_us=" + string recvYieldUs else ""
                log ("server on " + at.ToString() + " (user-space TCP on " + o.Text "tun" + ") -> " + target.ToString()
                     + " window_clamp=" + string windowClamp + " " + qmsg + " " + bmsg
                     + " tun_drain=" + drainMode + " recv_budget=" + string recvBudget + ymsg)
                t
        else null
    if isNull listener && isNull tun then failwith "need --listen or --tun with --tun-listen"

    let kernelReply (link: PathLink) (frame: byte[]) =
        link.Queue(ReadOnlySpan responseHead)
        link.Queue(ReadOnlySpan frame)
        link.Send() |> ignore

    let tunReply (path: TunPath) (frame: byte[]) =
        let bytes = Array.append responseHead frame
        path.SendReliable(ReadOnlySpan bytes)

    let reply (path: IPath) (frame: byte[]) =
        match path with
        | :? PathLink as l -> kernelReply l frame
        | :? TunPath as t -> tunReply t frame
        | _ -> ()

    let tryJoinAny (link: IPath) (frame: byte[]) =
        let mutable done' = false
        let mutable i = 0
        while not done' && i < sessions.Count do
            let e = sessions.[i]
            let s = e.Carrier.Session
            match tryJoin s.Keys.PathKey (ReadOnlySpan frame) with
            | ValueSome r ->
                done' <- true
                let c = e.Carrier
                let blocked = s.CloseState <> CloseState.Open || s.PathState r.PathId = PathCloseState.Closing
                let d = e.Admission.Decide(ReadOnlySpan frame, r, c.Paths.Count, c.HasPath r.PathId, blocked)
                match d.Verdict with
                | JoinVerdict.Accept ->
                    link.PathId <- r.PathId
                    reply link d.Ack
                    // A higher incarnation replaces the path (spec 6.5.7.4 rule 6).
                    if c.HasPath r.PathId then c.RemovePath r.PathId
                    c.AddPath link
                | JoinVerdict.Reject
                | JoinVerdict.Replay ->
                    reply link d.Ack
                    link.Close()
                | _ -> link.Close()
            | ValueNone -> ()
            i <- i + 1
        done'

    /// Classify the bootstrap frame (spec 6.5.9). True when the link left the pending list.
    let classify (link: IPath) (frame: byte[]) =
        match offer.TryAccept(ReadOnlySpan frame, unixMs (), mss) with
        | ValueSome a ->
            let bridge = FlowBridge(ValueSome(target :> EndPoint))
            let session = Session(Role.Server, a.Params, bridge, clock, config)
            bridge.Attach session
            let channel = if asyncMode && isNull tun then Some (new RxChannel()) else None
            let ioGroup = if channel.IsSome then Some (ensureIoPool ()) else None
            let carrier = SessionCarrier(session, bridge, ?rxChannel = channel, ?asyncIo = ioGroup)
            link.PathId <- 0us
            reply link a.Reply
            carrier.AddPath link
            let paths = min maxPaths (min (int a.Hello.MaxPaths) (MemTier.maxBulkPaths (MemTier.ofCaps a.Params.Caps)))
            let admission =
                JoinAdmission(a.Params.Keys.PathKey, entropy, clock, a.Params.Keys.SessionId, a.Params.Symbol, a.Params.Caps,
                              paths, mss, config.MaxPaths, a.Params.Keys.Suite)
            // A session gets its own epoll set and thread when the mode is on and
            // the shared TUN stack is not in play; the TUN stays single-threaded.
            let coordinator =
                match ioGroup, channel with
                | Some group, Some rx ->
                    let co = AsyncSessionCoordinator.createWith carrier clock group rx
                    co.StatsEveryMs <- statsMs
                    co.OnStats <- (fun () -> log ("stats " + carrier.Describe() + co.Stats.DescribeWindow()))
                    co.Start()
                    log ("session " + a.Params.Keys.SessionId.ToString() + " on an async coordinator thread")
                    Some co
                | _ -> None
            sessions.Add(Entry(carrier, paths, admission, coordinator))
            if sndBufMode < 0 then
                match tun with
                | null -> ()
                | t ->
                    let n = SocketTuning.tunSndBufFor(windowClamp, paths)
                    t.SetSndBuf n
                    log ("tun_sndbuf=auto " + string n + " (window_clamp x paths=" + string paths + ")")
            log ("session " + a.Params.Keys.SessionId.ToString() + " up, S=" + string a.Params.Symbol
                 + " " + describeOffer a.Params.Keys.Suite a.Params.Caps)
            true
        | ValueNone -> tryJoinAny link frame

    let servicePending (poller: Poller) (now: int64) =
        let mutable i = pending.Count - 1
        while i >= 0 do
            let p = pending.[i]
            let link = p.Link
            let mutable leave = false
            // Read only when the poller reports the socket readable. An entry
            // accepted this round is registered at the top of the next loop pass,
            // so that is the first pass in which it can be read. Reading
            // unconditionally broke the server accept path (sessions stayed 0).
            if poller.IsReadable link.Socket then
                let r = link.Receive()
                if r <> IoResult.WouldBlock then
                    if p.Silent then
                        let mutable f = Span<byte>.Empty
                        link.FrameLength <- max 1 link.Buffered
                        while link.TryTakeFrame(&f) do ()
                        link.FrameLength <- Layout.BootFrameLength
                    else
                        let mutable failed = false
                        let mutable boot = Span<byte>.Empty
                        if link.TryTakeHead(&failed) then
                            if link.TryTakeFrame(&boot) then
                                leave <- classify link (boot.ToArray())
                                if not leave then p.Silent <- true
                        elif failed then p.Silent <- true
                    if not leave && r = IoResult.Closed then
                        link.Close()
                        leave <- true
            if not leave && now - p.Since > (if p.Silent then 60_000L else 15_000L) then
                link.Close()
                leave <- true
            if leave then pending.RemoveAt i
            i <- i - 1

    use poller = new Poller()
    log ("readiness wait: " + string poller.Backend)
    let mutable lastStats = Environment.TickCount64
    let mutable faults = 0L
    let mutable failed = 0L
    let stamp () = Stopwatch.GetTimestamp()
    let toUs (d: int64) = d * 1_000_000L / Stopwatch.Frequency
    let mutable winRecvUs = 0L
    let mutable winRecvUsMax = 0L
    let mutable winSvcUs = 0L
    let mutable winSvcUsMax = 0L
    let mutable winCsvcUs = 0L
    let mutable winIterN = 0
    let mutable winIterUsMax = 0L
    let mutable winSvcGapMax = 0L
    let mutable lastSvcTs = 0L
    let recvYieldTicks = int64 recvYieldUs * Stopwatch.Frequency / 1_000_000L
    let ackWaiting () =
        sessions.Exists(fun e -> e.Carrier.Session.ControlWaiting)
    let recTimed (t: TunStack) (force: bool) =
        if force || poller.IsFdReadable t.Fd then
            let a = stamp ()
            if drainMode = "adaptive" then
                let deadline = a + recvYieldTicks
                t.Receive(recvBudget, yieldWhen=fun () -> stamp () >= deadline || t.AckDue || ackWaiting ()) |> ignore
            else
                t.Receive(recvBudget) |> ignore
            let us = toUs (stamp () - a)
            winRecvUs <- winRecvUs + us
            if us > winRecvUsMax then winRecvUsMax <- us
    let svcTimed (t: TunStack) =
        let nowTs = stamp ()
        if lastSvcTs <> 0L then
            let gap = toUs (nowTs - lastSvcTs)
            if gap > winSvcGapMax then winSvcGapMax <- gap
        lastSvcTs <- nowTs
        let a = nowTs
        t.Service()
        let us = toUs (stamp () - a)
        winSvcUs <- winSvcUs + us
        if us > winSvcUsMax then winSvcUsMax <- us
    while true do
        match listener with
        | null -> ()
        | l -> poller.WatchRead l
        match tun with
        | null -> ()
        | t -> poller.WatchFd t.Fd
        // A coordinator that exits wakes the shared pool descriptor, so the main
        // loop can reap a dead session at once instead of waking on a timer.
        match asyncGroup with
        | Some g when g.WakeFd >= 0 -> poller.WatchFd g.WakeFd
        | _ -> ()
        for p in pending do poller.WatchRead p.Link.Socket
        for e in sessions do
            if e.Coordinator.IsNone then e.Carrier.Watch poller
        let tunBusy =
            match tun with
            | null -> false
            | t -> t.Busy
        // An async session's TxHalf queues belong to its coordinator thread.  Reading
        // WantsToSend (and HasPendingRx) here raced with that thread and corrupted
        // Queue<T>; the crash surfaced as SIGABRT from TxHalf.HasCandidate.  Each
        // coordinator already derives its own wait from WaitMs(), so the main loop
        // only inspects sessions it actually owns.
        let rxPending =
            sessions.Exists(fun e -> e.Coordinator.IsNone && e.Carrier.HasPendingRx)
        let busy =
            tunBusy
            || sessions.Exists(fun e -> e.Coordinator.IsNone && e.Carrier.Session.WantsToSend)
        // The nearest deadline the main loop owns: a pending handshake timeout and
        // the stats line. Everything else is a descriptor event.
        let timerMs =
            let mutable m = Int64.MaxValue
            if pending.Count > 0 then m <- 15_000L
            if statsMs > 0L then
                let due = lastStats + statsMs - Environment.TickCount64
                m <- min m (max 1L due)
            m
        // A socket accepted in this round joins `pending` after this round's Wait, so
        // the poller has not registered it yet.  Waiting for an event on it would stall
        // the bootstrap head until the 10 s / 100 s timeout, which the peer sees as a
        // dead path.  With pending work, poll without blocking and service it at once.
        poller.Wait(
            if rxPending then 0
            elif busy then 1000
            elif sessions.Count > 0 || pending.Count > 0 then
                if timerMs = Int64.MaxValue then -1 else int timerMs
            else
                if timerMs = Int64.MaxValue then 100_000 else int (min 100_000L timerMs)
        )
        clock.Tick() |> ignore
        let iterA = stamp ()
        let now = Environment.TickCount64
        match listener with
        | null -> ()
        | l ->
            if poller.IsReadable l then
                let mutable go = true
                while go do
                    match tryAccept l with
                    | null -> go <- false
                    | s ->
                        SocketTuning.path(s, windowClamp)
                        s.Blocking <- false
                        pending.Add(Pending(PathLink(s, probe), now))
        match tun with
        | null -> ()
        | t ->
            // full: always drain (non-blocking). main: only when the poller says readable.
            recTimed t (drainMode = "full")
            // Bootstrap frames whole on TUN paths: classify (a bad head stays silent).
            for c in t.Fresh do
                if c.BootState = 1 then classify c (c.BootFrame.ToArray()) |> ignore
            t.Fresh.Clear()
        servicePending poller now
        let mutable i = sessions.Count - 1
        let csvcA = stamp ()
        while i >= 0 do
            let entry = sessions.[i]
            let c = entry.Carrier
            match entry.Coordinator with
            | None ->
                // Last resort: an exception fails this session only (causes are fixed where found).
                try c.Service poller
                with ex ->
                    faults <- faults + 1L
                    c.Session.Fail(ex.GetType().Name + ": " + ex.Message)
            | Some _ -> ()
            if not c.Alive then
                if c.Session.Faulted then failed <- failed + 1L
                let why =
                    if c.Session.Faulted then " failed: " + c.Session.Fault
                    elif c.Session.PeerCloseReason >= 0 then " closed by peer (reason " + string c.Session.PeerCloseReason + ")"
                    else " closed"
                log ("session " + c.Session.Keys.SessionId.ToString() + why)
                match entry.Coordinator with
                | Some co -> co.Stop()
                | None -> c.Close()
                sessions.RemoveAt i
            i <- i - 1
        winCsvcUs <- winCsvcUs + toUs (stamp () - csvcA)
        match tun with
        | null -> ()
        | t ->
            svcTimed t
            // full/adaptive: drain again after Service so a decode stall does not overflow tbt0.
            if drainMode = "full" || drainMode = "adaptive" then
                recTimed t true
                for c in t.Fresh do
                    if c.BootState = 1 then classify c (c.BootFrame.ToArray()) |> ignore
                t.Fresh.Clear()
        let iterUs = toUs (stamp () - iterA)
        winIterN <- winIterN + 1
        if iterUs > winIterUsMax then winIterUsMax <- iterUs
        if statsMs > 0L && now - lastStats >= statsMs then
            lastStats <- now
            let mutable fin = 0L
            let mutable fout = 0L
            for e in sessions do
                fin <- fin + e.Carrier.FramesIn
                fout <- fout + e.Carrier.FramesOut
            let struct (recvCalls, recvPkts, recvMax) =
                match tun with
                | null -> struct (0, 0, 0)
                | t -> t.TakeRecvWindow()
            log ("stats sessions=" + string sessions.Count + " sessions_failed=" + string failed
                 + " session_exceptions=" + string faults + " frames_in=" + string fin + " frames_out=" + string fout
                 + " rss_kib=" + string (rssKiB ()) + " alloc_kib=" + string (GC.GetTotalAllocatedBytes false / 1024L)
                 + " gc0=" + string (GC.CollectionCount 0)
                 + " recv_calls=" + string recvCalls + " recv_pkts=" + string recvPkts + " recv_max=" + string recvMax
                 + " recv_us=" + string winRecvUs + " recv_us_max=" + string winRecvUsMax
                 + " svc_us=" + string winSvcUs + " svc_us_max=" + string winSvcUsMax
                 + " csvc_us=" + string winCsvcUs + " iter_n=" + string winIterN + " iter_us_max=" + string winIterUsMax
                 + " svc_gap_us_max=" + string winSvcGapMax
                 + " tun_drain=" + drainMode)
            winRecvUs <- 0L; winRecvUsMax <- 0L; winSvcUs <- 0L; winSvcUsMax <- 0L
            winCsvcUs <- 0L; winIterN <- 0; winIterUsMax <- 0L; winSvcGapMax <- 0L
            // An async session's carrier belongs to its coordinator thread; walking
            // it here would race with that thread's path joins and drops.
            for e in sessions do
                if e.Coordinator.IsNone then log ("stats " + e.Carrier.Describe())
            match tun with
            | null -> ()
            | t -> log ("stats " + t.Describe())
            Tbt.Contracts.Trace.flush ()
    0
