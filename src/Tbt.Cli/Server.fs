/// Server mode: TCP listener for paths; each flow connects to the target (spec 6.5.9
/// bootstrap classification; failures stay silent).
module Tbt.Cli.Server

open System
open System.Collections.Generic
open System.Threading
open System.Net
open System.Diagnostics
open System.Net.Sockets
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Async
open Tbt.Cli.Common

/// Whether a JOIN for this session must be refused rather than admitted.
///
/// Four conditions, and each one has a reason:
///   · `retired` — the entry has left `sessions`, so an accept would attach a path to a session that
///     nothing services and nothing closes. It must be a function of the argument, not of a
///     momentary reading of the carrier.
///   · `closeState <> Open` — the session is closing or closed: admitting would hand out a path that
///     is torn down in the same breath.
///   · `pathClosing` — the incarnation in the frame belongs to a path already in PATH_CLOSE.
///   · `not alive` — the session is faulted, closed, or out of path grace. Grace matters: while it
///     holds, `alive` is true with zero paths, which is exactly the state a client rejoins from
///     after a burst of resets. That case must stay admissible; only after the grace expires does
///     the session become unserviceable.
///
/// Kept at module scope, not inlined in the decision, so the rule can be tested on its own without
/// constructing a whole server loop.
let joinMustReject (retired: bool) (closeState: CloseState) (pathClosing: bool) (alive: bool) =
    retired || closeState <> CloseState.Open || pathClosing || not alive

/// How long a reaped entry stays addressable for JOIN when its tail has not finished.
///
/// The blocking reap used to keep a dying session in `sessions` for up to `t.Join 5000`. This is the
/// same bound, applied without waiting for it: an entry leaves earlier the moment its own loop
/// publishes `Stopped` (its keys are then meaningless), and never later than this. It bounds the
/// retained memory; it does not extend any protocol deadline.
let retireWindowMs = 5000L

/// Whether a retired entry should be dropped from the addressable list.
///
/// `released` is the session's own claim that its teardown finished; the deadline is the backstop
/// for a tail that never finishes, so the list cannot grow without bound.
let retireExpired (released: bool) (nowMs: int64) (reapedAtMs: int64) =
    released || nowMs - reapedAtMs > retireWindowMs

/// What a JOIN decision needs from one session: its carrier and its admission state.
///
/// `[<Struct>]` so the server hands one over per JOIN frame without allocating; a record distinct
/// from `Entry` so the rule can be driven with a real carrier and a real admission and no server
/// loop (an `Entry` is private here and carries a coordinator).
[<Struct>]
type JoinTarget =
    { Carrier: SessionCarrier
      Admission: JoinAdmission }

/// What one authenticated JOIN frame did.
[<RequireQualifiedAccess>]
type JoinOutcome =
    /// The frame did not authenticate for this target: the caller tries the next session.
    | NotMine
    /// The path is attached; the session services it from here on.
    | Accepted
    /// A MAC'd reject/replay ack was replied and the path closed — the client can act on it.
    | Refused
    /// A terminal verdict with nothing actionable: the path is closed.
    | Closed

/// Decide one authenticated JOIN against one session (spec 6.5.7.4). True when the frame was
/// consumed, i.e. anything but `NotMine`.
///
/// `blocked` also covers `not c.Alive`. Without it, a session whose last path left and whose grace
/// expired looks like a healthy empty session to `Decide` (its `CloseState` is still `Open`,
/// capacity is free), so a JOIN racing the reaper would be ACCEPTED into a session that the same
/// round removes and closes — a positive ACK for a path that is closed a moment later. With it,
/// such a JOIN gets a MAC'd reject, which is what the client can act on.
///
/// `retired` makes the answer for a reaped session a function of the argument, not of a race with
/// its tail: accepting is impossible even if `Alive` were to read true for a moment.
///
/// `reply` is the path's transmit, the one thing this rule cannot own (kernel `PathLink` and TUN
/// `TunPath` differ). Everything that decides, and every side effect on the session, lives here, so
/// the server loop and the tests run one implementation rather than two that can drift.
let decideJoin (reply: IPath -> byte[] -> unit) (t: JoinTarget) (link: IPath) (frame: byte[]) (retired: bool) =
    let s = t.Carrier.Session
    match tryJoin s.Keys.PathKey (ReadOnlySpan frame) with
    | ValueSome r ->
        let c = t.Carrier
        // `retired` first: see the note above on why it cannot be inferred from `Alive`.
        let blocked =
            joinMustReject retired s.CloseState (s.PathState r.PathId = PathCloseState.Closing) c.Alive
        let d = t.Admission.Decide(ReadOnlySpan frame, r, c.PathCount, c.HasPath r.PathId, blocked)
        match d.Verdict with
        | JoinVerdict.Accept ->
            link.PathId <- r.PathId
            reply link d.Ack
            // A higher incarnation replaces the path (spec 6.5.7.4 rule 6).
            if c.HasPath r.PathId then c.RemovePath r.PathId
            c.AddPath link
            JoinOutcome.Accepted
        | JoinVerdict.Reject
        | JoinVerdict.Replay ->
            reply link d.Ack
            link.Close()
            JoinOutcome.Refused
        | _ ->
            link.Close()
            JoinOutcome.Closed
    | ValueNone -> JoinOutcome.NotMine

/// Try the live sessions first, then the retired ones (spec 5.8 message order).
///
/// The order is the contract, not a detail: in the round a session dies its entry is still in the
/// live list and is refused by `not alive`; from the next round on it is only in the retired list
/// and is refused by `retired`. Both answers carry a MAC'd ack, so a path refresh racing the death
/// is refused rather than silently dropped.
///
/// Accessors instead of arrays: the server owns two mutable lists and passes their index/count, so
/// this costs no per-frame allocation and the loop stays the only thing that mutates them.
let tryJoinAnyOf (reply: IPath -> byte[] -> unit)
                 (liveAt: int -> JoinTarget) (liveCount: int)
                 (retiredAt: int -> JoinTarget) (retiredCount: int)
                 (link: IPath) (frame: byte[]) =
    let mutable done' = false
    let mutable i = 0
    while not done' && i < liveCount do
        done' <- decideJoin reply (liveAt i) link frame false <> JoinOutcome.NotMine
        i <- i + 1
    let mutable j = 0
    while not done' && j < retiredCount do
        done' <- decideJoin reply (retiredAt j) link frame true <> JoinOutcome.NotMine
        j <- j + 1
    done'

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
    /// The two fields a JOIN decision reads, handed to `decideJoin` so the loop and the tests run
    /// one rule. A struct, so this costs no allocation per JOIN frame.
    member e.Target = { Carrier = carrier; Admission = admission }

let private runCore (o: Options) =
    let windowMode = windowCodec o
    let asyncMode = AsyncMode.isEnabled ()
    if asyncMode then log "async mode: one poller and one thread per session (TBTUN_ASYNC_MODE)"
    let mutable asyncGroup: AsyncPollerGroup option = None
    let ensureIoPool () =
        match asyncGroup with
        | Some group -> group
        | None ->
            // One IO thread per vCPU, at most four. On a 2-vCPU host four threads
            // only added context switches and split each wakeup's batch.
            let group = AsyncPollerGroup(o.Int("io-threads", min 4 (max 1 Environment.ProcessorCount)))
            group.Start()
            log ("async IO pool: " + string group.ThreadCount + " threads")
            asyncGroup <- Some group
            group
    let listenAt = if o.Has "listen" then ValueSome(endpoint (o.Text "listen")) else ValueNone
    let target = endpoint (o.Text "target")
    let psks = readPsks (o.Text "psk-file")
    let mss = o.Int("mss", 1448)
    let maxPaths = bounded o "max-paths" 64 1 256
    let maxSessions = bounded o "max-sessions" 32 1 4096
    let maxPending = bounded o "max-pending" 64 1 4096
    /// A session whose every path dropped at once stays joinable this long (s).
    let noPathGraceNs = int64 (max 0 (o.Int("no-path-grace-s", 30))) * 1_000_000_000L
    let windowClamp = o.Int("window-clamp", SocketTuning.WindowClamp)
    let statsMs = int64 (o.Int("stats-s", 0)) * 1000L
    // Congestion-control re-init of carrier sockets (0 = off).
    // Default pick "degraded" reads cached PathHealth snapshots (zero getsockopt), does nothing
    // while idle, and only re-inits sockets stuck in Loss or BBR collapse (never healthy fast paths).
    let ccNudgePick = o.TextOr("cc-nudge-pick", "degraded").ToLowerInvariant()
    let ccNudge =
        CcNudgePlan<System.Net.Sockets.Socket>(int64 (max 0 (o.Int("cc-nudge-s", 0))) * 1_000_000_000L,
                                               1_000_000_000L, (ccNudgePick = "worst"))
    let ccNudgeVia = o.TextOr("cc-nudge-via", "reno")
    // Downlink group pacing: 0 (default) keeps the pre-existing unpaced write
    // gate; 1 charges the session RateGroup so a 256-path burst cannot outrun
    // the bottleneck. Direction-neutral: the client has the same switch as
    // --uplink-pace.
    let pace = o.Int("pace", 0) <> 0
    // Pass-2 write-gate queue per path. The default stays SocketTuning.GatePass2Limit;
    // the switch exists so a smaller queue can be compared as one variable.
    let gatePass2 = max 4096 (o.Int("gate-pass2", SocketTuning.GatePass2Limit))
    // Send-side supply: TCP_NOTSENT_LOWAT and async tx slots. Defaults are the
    // product values; the switches exist so the two can be compared alone.
    let notsentLowat = SocketTuning.notsentLowatFor (o.Int("notsent-lowat", SocketTuning.NotSentLimit))
    let userTimeoutMs = SocketTuning.userTimeoutMsFor (o.Int("user-timeout-ms", SocketTuning.UserTimeoutMs))
    let txSlots = SocketTuning.txSlotsFor (o.Int("tx-slots", 64))
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
    let config = { SessionConfig.defaults with Credit = creditFromOptions o; WindowSteps = o.Int("window", 1024); MaxPaths = maxPaths; NewRepair = o.Int("new-repair", 0) <> 0; FecAdaptive = fecAdaptive }
    let entropy = SystemEntropy()
    let clock = CachedClock(SystemClock())
    let probe = SocketTuning.probe ()
    let struct (offerAegis, offerChaCha8, offerChaCha20, offerX25519) = offerFromOptions o
    /// The initial step grant must not exceed the receiver's own window: a peer that honours
    /// a larger grant would send past what this end can hold and the cells would be refused.
    let grant0 =
        if windowMode then uint16 (WindowGeometry.stepsFor mss config.WindowSteps)
        else uint16 config.WindowSteps
    let offer = ServerOffer(psks, entropy, grant0, maxPaths,
                            offerX25519 = offerX25519, offerChaCha8 = offerChaCha8,
                            offerAegis = offerAegis, offerChaCha20 = offerChaCha20,
                            ?windowOffer = (if windowMode then Some Tbt.Crypto.Bootstrap.WindowOffer.defaults else None))
    let pending = List<Pending>()
    let sessions = List<Entry>()
    let mutable stop = 0
    /// Sessions reaped this round, still addressable for a JOIN for a short window.
    ///
    /// The blocking reap used to keep a dying session in `sessions` for up to its 5 s join, and a
    /// JOIN that raced the death got a MAC'd reject because `Decide` saw a closing/sick session.
    /// Removing the entry at once (the non-blocking reap) turned that reject into silence: the new
    /// path was classified as "no session", left pending, and closed silently after 60 s.
    ///
    /// So the entry stays here, with the instant it was reaped, and leaves when the session's own
    /// loop publishes `Stopped` (the release is finished, so its keys must not be honoured) or when
    /// the window closes. The window is the old join timeout, so addressability is what it was; the
    /// difference is that nobody waits for it.
    let retiring = List<struct (Entry * int64)>()
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
    let listenSocks =
        let extras =
            match listenAt with
            | ValueSome at when o.Has "listen-ports" ->
                portList (o.Text "listen-ports")
                |> Array.choose (fun p ->
                    if p = at.Port then None
                    else
                        let ep = IPEndPoint(at.Address, p)
                        let s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)
                        s.Bind ep
                        SocketTuning.fastOpenListen(s, 512)
                        s.Listen 512
                        s.Blocking <- false
                        log ("server also on " + ep.ToString() + " (kernel TCP)")
                        Some s)
            | _ -> Array.empty
        match listener with
        | null -> extras
        | l -> Array.append [| l |] extras
    // Closing the listener unblocks a poll that would otherwise sit for up to 100 s.
    use _signals = onInterrupt (fun () ->
        Volatile.Write(&stop, 1)
        log "shutdown requested"
        match listener with
        | null -> ()
        | l -> try l.Close() with _ -> ())

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
        // Live sessions first, then the retired window: see `tryJoinAnyOf` for why the order is the
        // contract. A reaped entry stays addressable for the window the blocking reap used to give
        // it, so a JOIN racing the death still finds the session's keys and gets the MAC'd reject
        // above instead of silence. The reaping thread does not wait for any tail: entries leave on
        // their own (`Stopped`) or on the deadline.
        tryJoinAnyOf reply
            (fun i -> sessions.[i].Target) sessions.Count
            (fun i ->
                let struct (e, _reapedAt) = retiring.[i]
                e.Target)
            retiring.Count link frame

    /// Classify the bootstrap frame (spec 6.5.9). True when the link left the pending list.
    let classify (link: IPath) (frame: byte[]) =
        match offer.TryAccept(ReadOnlySpan frame, unixMs (), mss) with
        | ValueSome a ->
            let bridge = FlowBridge(ValueSome(target :> EndPoint))
            let session = Session(Role.Server, a.Params, bridge, clock, config)
            bridge.Attach session
            let channel = if asyncMode && isNull tun then Some (new RxChannel()) else None
            let ioGroup = if channel.IsSome then Some (ensureIoPool ()) else None
            let requestedMin =
                defaultMinActivePaths
                    (if o.Has "min-active-paths" then Some (o.Int("min-active-paths", 16)) else None)
                    maxPaths
            let carrier = SessionCarrier(session, bridge, uplinkPace = pace, ?rxChannel = channel, ?asyncIo = ioGroup,
                                         gatePass2 = gatePass2, txSlots = txSlots,
                                         minActivePaths = max 1 requestedMin)
            carrier.NoPathGraceNs <- noPathGraceNs
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
            let rmem = SocketTuning.rmemMax ()
            let flowBuf = SocketTuning.flowRecvBufFor rmem
            log ("session " + a.Params.Keys.SessionId.ToString() + " up, S=" + string a.Params.Symbol
                 + " max_paths=" + string maxPaths + " negotiated_paths=" + string paths
                 + " " + describeOffer a.Params.Keys.Suite a.Params.Caps
                 + " flow_rcvbuf_req=" + string flowBuf
                 + " flow_rcvbuf_eff=" + string (SocketTuning.flowRecvWinFor flowBuf)
                 + " rmem_max=" + string rmem
                 + " pace=" + (if pace then "on" else "off"))
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
    let mutable acceptedThisRound = false
    let mutable lastNudgeFrames = 0L
    let mutable lastNudgeCheckMs = 0L
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
    let shuttingDown () = Volatile.Read &stop <> 0
    while not (shuttingDown ()) do
        for l in listenSocks do poller.WatchRead l
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
            if ccNudge.Enabled then m <- min m 1000L
            m
        // A socket accepted in this round joins `pending` after this round's Wait, so
        // the poller has not registered it yet.  Waiting for an event on it would stall
        // the bootstrap head until the 10 s / 100 s timeout, which the peer sees as a
        // dead path.  With pending work, poll without blocking and service it at once.
        // Poller.Wait takes microseconds; every deadline above is milliseconds.
        // Passing the milliseconds straight through made a 1 s stats timer a 1 ms
        // poll (about 900 idle rounds per second on the main thread).
        let waitMs =
            if rxPending || acceptedThisRound then 0L
            elif busy then 1L
            elif sessions.Count > 0 || pending.Count > 0 then
                if timerMs = Int64.MaxValue then -1L else timerMs
            else
                if timerMs = Int64.MaxValue then 100_000L else min 100_000L timerMs
        acceptedThisRound <- false
        poller.Wait(if waitMs < 0L then -1 else int (min 100_000_000L (waitMs * 1000L)))
        // The pool's shared eventfd is level-triggered: a coordinator that exits
        // writes it once, and unless it is read here every later Wait returns at
        // once. Left unread it pinned the main thread at 100% CPU from the first
        // session close until the process restarted.
        match asyncGroup with
        | Some g when g.WakeFd >= 0 && poller.IsFdReadable g.WakeFd -> g.ClearWake()
        | _ -> ()
        clock.Tick() |> ignore
        let iterA = stamp ()
        let now = Environment.TickCount64
        for l in listenSocks do
            if poller.IsReadable l then
                let mutable go = true
                while go do
                    match tryAccept l with
                    | null -> go <- false
                    | s when shuttingDown () || pending.Count >= maxPending || sessions.Count + retiring.Count >= maxSessions ->
                        // A public scan must not hold a descriptor until the 60 s silent timeout.
                        SocketTuning.closeGraceful s
                    | s ->
                        SocketTuning.pathWith(s, windowClamp, notsentLowat, userTimeoutMs)
                        s.Blocking <- false
                        pending.Add(Pending(PathLink(s, probe), now))
                        acceptedThisRound <- true
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
                | Some co ->
                    // Non-blocking: this thread is the poller for every other session, and the
                    // session's own tail does the release (carrier close, poller and channel
                    // dispose, `Stopped`) on every exit. Joining here would stall all other
                    // sessions while this one closes up to `--max-paths` sockets.
                    co.RequestClose()
                    retiring.Add(struct (entry, now))
                | None -> c.Close()
                sessions.RemoveAt i
            i <- i - 1
        // Retire sweep: a reaped session leaves as soon as its own loop publishes `Stopped` (the
        // release finished, so its keys must not be honoured any more), or when the addressability
        // window the blocking reap used to give it has closed. This bounds the list without
        // waiting for any tail, and without inventing a session limit.
        let mutable r = retiring.Count - 1
        while r >= 0 do
            let struct (e, reapedAt) = retiring.[r]
            let finished =
                match e.Coordinator with
                | Some co -> co.Stopped.IsSet
                | None -> true
            if retireExpired finished now reapedAt then retiring.RemoveAt r
            r <- r - 1
        winCsvcUs <- winCsvcUs + toUs (stamp () - csvcA)
        if ccNudge.Enabled && sessions.Count > 0 && now - lastNudgeCheckMs >= 1000L then
            lastNudgeCheckMs <- now
            let mutable totalFrames = 0L
            for e in sessions do
                totalFrames <- totalFrames + e.Carrier.FramesIn + e.Carrier.FramesOut
            let active = totalFrames - lastNudgeFrames >= 32L
            lastNudgeFrames <- totalFrames
            if ccNudgePick = "degraded" then
                if active then
                    let snaps = ResizeArray<struct (System.Net.Sockets.Socket * PathHealth)>()
                    for e in sessions do
                        let s = e.Carrier.SnapshotSocketHealth()
                        for k in 0 .. s.Length - 1 do snaps.Add s.[k]
                    for sk in ccNudge.DueDegraded(now * 1_000_000L, snaps.ToArray(), true) do
                        SocketTuning.reinitCongestion(sk, "bbr", ccNudgeVia) |> ignore
            else
                let socks = ResizeArray<System.Net.Sockets.Socket>()
                for e in sessions do
                    let s = e.Carrier.PathSockets
                    for k in 0 .. s.Length - 1 do socks.Add s.[k]
                let score (sk: System.Net.Sockets.Socket) =
                    match KernelTcpInfo.tryRead sk with
                    | ValueSome i when i.DeliveryRate >= 0L -> i.DeliveryRate
                    | _ -> Int64.MaxValue
                for sk in ccNudge.Due(now * 1_000_000L, socks.ToArray(), score) do
                    SocketTuning.reinitCongestion(sk, "bbr", ccNudgeVia) |> ignore
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
                 + " tun_drain=" + drainMode + " cc_nudged=" + string ccNudge.Nudged)
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
    log "draining"
    match listener with
    | null -> ()
    | l -> try l.Close() with _ -> ()
    for p in pending do p.Link.Close()
    pending.Clear()
    for e in sessions do
        match e.Coordinator with
        | Some co -> co.RequestClose()
        | None -> e.Carrier.Close()
    let deadline = Environment.TickCount64 + 5_000L
    let mutable left = sessions |> Seq.exists (fun e -> e.Coordinator.IsSome && not e.Coordinator.Value.Stopped.IsSet)
    while left && Environment.TickCount64 < deadline do
        Thread.Sleep 20
        left <- sessions |> Seq.exists (fun e -> e.Coordinator.IsSome && not e.Coordinator.Value.Stopped.IsSet)
    for e in sessions do
        match e.Coordinator with
        | Some co -> if not co.Stopped.IsSet then e.Carrier.Close()
        | None -> ()
    match asyncGroup with
    | Some g -> g.Stop()
    | None -> ()
    match tun with
    | null -> ()
    | t -> t.Close()
    log "stopped"
    0

let run (o: Options) =
    if o.Has "help" then
        Console.Error.WriteLine "usage: tbtun server --target HOST:PORT --psk-file FILE [--listen ADDR:PORT | --tun NAME --tun-listen ADDR:PORT] [--listen-ports PORTS] [--max-paths 64] [--min-active-paths min(max-paths,64)] [--window 1024] [--cc-nudge-s 0] [--cc-nudge-pick degraded|worst|oldest] [--stats-s 0]"
        0
    else runCore o
