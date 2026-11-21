/// Drives one session over its TCP paths and its local flows (spec 6.6 receive, 6.8
/// write gating): whole frames in, one frame per path per round out.
namespace Tbt.Carrier

open System
open System.Collections.Generic
open System.Collections.Concurrent
open Tbt.Contracts
open Tbt.Session

/// One session-level member of the client uplink RateGroup. Delivery for rate
/// samples is arrival evidence (WIN_ACK t_hi / UniqueArrived), not only
/// cumulative peer_ack: a later-seen step means the bottleneck already carried
/// those cells even if a hole still sits at the frontier. Inflight stays
/// custody-based so a hole cannot shrink the 1.25 BDP cap. Not kernel TCP ACK:
/// the kernel can ACK while the server decoder lags.
type private SessionRateMember(session: Session, frameOf: unit -> int, uniqueOut: unit -> int64, wireOut: unit -> int64) =
    let mutable lastAck = 0UL
    let mutable lastSeen = 0UL
    let mutable lastNs = 0L
    interface IRateMember with
        // Unique payload still awaiting custody, not every BOOSTER copy: counting copies
        // against cwnd left the group unable to send after a repair burst.
        member _.Inflight =
            let fl = int64 (frameOf())
            int (max 0L (uniqueOut() - session.Tx.UniqueAcked * fl))
        member _.Delivered = session.Tx.UniqueArrived * int64 (frameOf())
        member _.CopyEstimate = max 0L (wireOut() - uniqueOut())
        // RateGroup.Service will not take a bw sample while minRTT is unknown.
        // Karn can skip every seal-to-custody sample when BOOSTERs start before
        // the first ack (8-path rtt400 sat at the 64-frame startup cap for 40 s
        // with pace_bw=0). Fall back to SRTT so the group can still estimate.
        member _.MinRttNs =
            let m = session.Tx.MinRttNs
            if m <> System.Int64.MaxValue then m else max 1L (int64 session.Tx.SrttNs)
        member _.LastDeliveredNs =
            if session.Tx.PeerAck > lastAck || session.Tx.PeerSeenHi > lastSeen then
                lastAck <- session.Tx.PeerAck
                lastSeen <- session.Tx.PeerSeenHi
                lastNs <- session.Clock.NowNs()
            lastNs

/// A path change posted to the thread that owns SessionCarrier.paths.
/// The accept loop joins and replaces paths from its own thread while an async
/// session enumerates `paths` in Watch/Service; mutating the list from both
/// threads made the enumerator throw InvalidOperation_EnumFailedVersion.
type private PathOp =
    | PathAdd of IPath
    | PathRemove of uint16

[<Sealed>]
type SessionCarrier(session: Session, bridge: FlowBridge, ?uplinkPace: bool, ?tupleMbit: float,
                    ?rxChannel: RxChannel, ?asyncIo: AsyncPollerGroup, ?gatePass2: int, ?txSlots: int) =
    let pace = defaultArg uplinkPace false
    let tupleMbit = defaultArg tupleMbit 0.0
    let gatePass2 = defaultArg gatePass2 SocketTuning.GatePass2Limit
    let txSlots = SocketTuning.txSlotsFor (defaultArg txSlots 16)
    let channel = rxChannel
    let io = asyncIo
    let paths = List<IPath>()
    /// Posted from a watching thread; applied by the thread that owns `paths`.
    let pathOps = ConcurrentQueue<PathOp>()
    let pathOpsInline = io.IsNone
    /// Posted path ops not yet applied. The client logs "paths ready" only when
    /// this reaches zero: Paths.Count read before the queue drains is stale.
    let mutable pathOpsPending = 0
    let mutable next = 0
    let mutable rxNext = 0
    let mutable rxMore = false
    let mutable framesIn = 0L
    let mutable framesOut = 0L
    /// Frames the last writeRound put out, against its 256-frame budget. A full
    /// budget means data still waits; a short one means every path refused.
    let mutable lastRoundSent = 0
    /// Bytes of DATA/PARITY/BOOSTER put on the wire (not WIN_ACK/CTRL/heartbeat).
    let mutable wireOut = 0L
    /// First successfully rendered DATA/initial PARITY bytes on charged submissions.
    /// Repeated DATA and BOOSTER copies remain in wireOut, not uniqueOut.
    let mutable uniqueOut = 0L
    let group = RateGroup(session.Clock)
    let member' = SessionRateMember(session, (fun () -> session.FrameLength), (fun () -> uniqueOut), (fun () -> wireOut))
    let mutable samplePrimed = false
    do
        if pace then
            group.Add member'
            group.ProtectRecovery <- true
        // Async IO: authenticate/decrypt cells on the IO threads (TBT_NO_PREOPEN=1 turns it off).
        match channel with
        | Some rx when isNull (Environment.GetEnvironmentVariable "TBT_NO_PREOPEN") ->
            rx.PreOpenerFactory <- Func<RxPreOpener>(fun () -> session.CreatePreOpener())
            rx.OnPreOpened <-
                PreOpenedHandler(fun path frame c ->
                    framesIn <- framesIn + 1L
                    session.OnFramePreOpened(path, frame, c))
        | _ -> ()
    let pacers = Dictionary<uint16, PathPacer>()
    /// Local slots visited by Watch and Service (RS-3: grows with armed flows, not 256).
    let mutable slotVisits = 0L
    /// Unpaced writeBatch: originals sent since the last admitted repair.
    let mutable originalsSinceRepair = 0
    /// A path change breaks the interval between aggregate kernel ACK samples.
    let mutable pathVersion = 0L
    let mutable statsPathVersion = -1L
    let mutable statsNs = 0L
    let mutable statsAck = -1L

    let pacerOf (pathId: uint16) =
        if tupleMbit <= 0.0 then None
        else
            match pacers.TryGetValue pathId with
            | true, p -> Some p
            | _ ->
                let p = PathPacer(tupleMbit)
                pacers.[pathId] <- p
                Some p

    let pathAllows (pathId: uint16) (bytes: int) (now: int64) =
        match pacerOf pathId with
        | None -> true
        | Some p ->
            p.SetMinRtt session.Tx.MinRttNs
            p.TryTake(bytes, now)

    let chargePath (pathId: uint16) (bytes: int) =
        match pacerOf pathId with
        | Some p -> p.OnSent bytes
        | None -> ()

    let uniqueInflight (fl: int) =
        int (max 0L (uniqueOut - session.Tx.UniqueAcked * int64 fl))

    /// Restart the group's sample on the first send opportunity that has an RTT
    /// (true min-RTT, or SRTT when Karn has skipped every seal-to-custody sample),
    /// so the first delivery rate is not averaged over the handshake delay (that
    /// sample was ~1 frame / several seconds and left cwnd at 4 frames forever).
    let primeSample () =
        if pace && not samplePrimed then
            let rtt =
                let m = session.Tx.MinRttNs
                if m <> Int64.MaxValue then m else int64 session.Tx.SrttNs
            if rtt > 0L then
                group.Remove member'
                group.Add member'
                samplePrimed <- true

    /// 64 unique frames is enough to finish several encoder steps and get a
    /// real RTT sample, but far below a many-path flood. Once the group has a
    /// healthy cwnd, TryTake is the cap (1.25 BDP).
    let startupCap (fl: int) = 64 * fl

    let groupAllows (fl: int) (repair: bool) (bulk: bool) =
        if repair then
            if group.Ready && group.TryTakeRepair(fl, fl) then true
            else not group.Ready && uniqueInflight fl <= startupCap fl
        elif group.Ready && group.TryTake(fl, bulk) then true
        else uniqueInflight fl + fl <= startupCap fl

    /// One half-open probe per second. `Poll` is a syscall; a peer that closed
    /// must not keep a dead slot or `PathPool` can never rejoin that id. Async
    /// paths already have their own `CheckAlive`.
    let mutable lastHalfOpenProbeNs = 0L

    let prune () =
        let mutable i = paths.Count - 1
        while i >= 0 do
            if not paths.[i].Alive then
                paths.[i].Close()
                paths.RemoveAt i
                pathVersion <- pathVersion + 1L
            i <- i - 1

    let hot = ReadySet()
    /// Q-3 (spec 16.3): one batch gives each path at most 4 frames, within budget. Returns frames sent.
    /// When uplinkPace is set, WIN_ACK/CTRL/heartbeat skip the group; new data uses TryTake
    /// (aggregate 1.25 BDP); BOOSTERs use the charged repair reserve (TryTakeRepair / OnRepair)
    /// and are skipped when that reserve is dry so new data can still go.
    /// When unpaced (server downlink, or client with pacing off), unsent originals go first.
    /// A repair is admitted only if the original queue is empty, or after every
    /// RepairSendOrder.OriginalsBeforeRepair originals, so a BOOSTER storm cannot
    /// freeze tx_head. TryRenderNext still prefers a BOOSTER when allowRepair is set
    /// (a paced repair slot, or an unpaced shared slot).
    /// notSent reported by each path in pass 1 of the current batch. Pass 2 reuses
    /// it instead of asking the kernel twice, and only revisits paths whose gate
    /// refused them in pass 1.
    let batchNotSent = Array.zeroCreate<int> 512
    /// writeBatch gate diagnostics. Cumulative; the difference between two
    /// Describe windows is the per-window rate.
    let mutable gate1Pass = 0L
    let mutable gate1Deny = 0L
    let mutable gate2Pass = 0L
    let mutable gate2Deny = 0L
    let mutable beginFail = 0L
    let mutable produceFail = 0L

    let writeBatch (budget0: int) =
        let mutable budget = budget0
        let mutable progress = true
        let mutable gated = false
        let mutable passes = 0
        let fl = session.FrameLength
        let now = session.Clock.NowNs()
        primeSample ()
        while progress && passes < 4 && budget > 0 && paths.Count > 0 && session.WantsToSend do
            progress <- false
            gated <- false
            passes <- passes + 1
            let n = paths.Count
            // Pass 2 relaxes the gate to notSent <= 16 KiB (spec 3.14). It must run
            // whenever pass 1 gated at least one path, not only when pass 1 made no
            // progress at all: with 256 paths a single idle path sets `progress`, and
            // the other 255 were skipped every round. The live link then sent about
            // six frames per Service iteration (3 Mbit/s) while 50 MB sat queued.
            for pass in 1 .. 2 do
              if pass = 1 || gated then
                let mutable j = 0
                while j < n && budget > 0 do
                    let idx = (next + j) % n
                    let link = paths.[idx]
                    let mutable frame = Span<byte>.Empty
                    let notSent =
                        if pass = 1 then
                            let v = link.UnsentBytes
                            batchNotSent.[idx] <- v
                            if v <> 0 then gated <- true
                            v
                        else batchNotSent.[idx]
                    let eligible = pass = 1 || notSent <> 0
                    let admitted =
                        if not eligible then false
                        elif WriteGate.admitsWith gatePass2 pass notSent then
                            if pass = 1 then gate1Pass <- gate1Pass + 1L
                            else gate2Pass <- gate2Pass + 1L
                            true
                        else
                            if pass = 1 then gate1Deny <- gate1Deny + 1L
                            else gate2Deny <- gate2Deny + 1L
                            false
                    let begun = admitted && link.TryBeginFrame(&frame)
                    if admitted && not begun then beginFail <- beginFail + 1L
                    if begun then
                        let mutable allowRepair = true
                        let mutable charge = 0
                        let mutable attempt = false
                        let mutable unpacedRepair = false
                        let mutable unpacedData = false
                        if session.ControlWaiting then
                            attempt <- true
                        elif not pace then
                            if RepairSendOrder.canSendRepair session.Tx.Queued session.RepairWaiting originalsSinceRepair then
                                attempt <- true
                                unpacedRepair <- true
                            elif session.WantsToSend then
                                attempt <- true
                                allowRepair <- false
                                unpacedData <- true
                        elif session.RepairWaiting && groupAllows fl true (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                            attempt <- true
                            charge <- 1
                        elif groupAllows fl false (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                            // allowRepair=false: a dry repair reserve must not block new data.
                            attempt <- true
                            allowRepair <- false
                            charge <- 2
                        let uniqueBefore = session.Tx.UniqueRendered
                        let produced = attempt && session.TryProduce(link.PathId, frame, allowRepair)
                        if attempt && not produced then produceFail <- produceFail + 1L
                        if produced then
                            let uniqueDelta = session.Tx.UniqueRendered - uniqueBefore
                            link.CommitFrame()
                            framesOut <- framesOut + 1L
                            budget <- budget - 1
                            progress <- true
                            if unpacedRepair then originalsSinceRepair <- 0
                            elif unpacedData then originalsSinceRepair <- originalsSinceRepair + 1
                            if charge = 1 || charge = 2 then
                                uniqueOut <- uniqueOut + uniqueDelta * int64 fl
                            if charge = 1 then
                                wireOut <- wireOut + int64 fl
                                group.OnRepair fl
                                chargePath link.PathId fl
                            elif charge = 2 then
                                wireOut <- wireOut + int64 fl
                                group.OnSent fl
                                chargePath link.PathId fl
                    j <- j + 1
            next <- (next + 1) % n
        budget0 - budget

    let readPass (poller: Poller) (interactive: bool) =
        let armed = bridge.Armed
        let mutable slot = armed.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s ->
                if session.IsInteractive slot = interactive then
                    if interactive then hot.Add slot
                    if bridge.WantsRead slot then bridge.Read slot
            slot <- armed.NextUp slot

    /// Send at most 256 frames in batches; the interactive input is read again between batches.
    let writeRound () =
        let backlog = session.Tx.Queued
        for link in paths do link.Backlog <- backlog
        if pace && backlog >= RateGroup.BacklogFrames then group.NoteBacklog()
        let mutable budget = 256
        let mutable go = true
        while go do
            let sent = writeBatch budget
            budget <- budget - sent
            go <- sent > 0 && budget > 0 && session.WantsToSend
            if go && not hot.IsEmpty then
                let mutable slot = hot.NextUp -1
                while slot >= 0 do
                    slotVisits <- slotVisits + 1L
                    if session.IsInteractive slot then
                        if bridge.WantsRead slot then bridge.Read slot
                    slot <- hot.NextUp slot
        lastRoundSent <- 256 - budget

    member _.Session = session
    member _.Bridge = bridge
    member _.Paths = paths
    /// Path adds/removes posted but not yet applied by the session thread.
    member _.PathOpsPending = System.Threading.Interlocked.CompareExchange(&pathOpsPending, 0, 0)
    member _.FramesIn = framesIn
    member _.FramesOut = framesOut
    member _.SlotVisits = slotVisits
    /// One Q-3 batch (tests): at most 4 frames per path.
    member _.WriteBatch(budget: int) = writeBatch budget
    /// Optional owner predicate: the session stays alive with no path while the
    /// owner is still rebuilding its path pool. None keeps the old rule.
    member val KeepAlive: (unit -> bool) option = None with get, set
    member this.Alive =
        (paths.Count > 0 || not pathOps.IsEmpty
         || (match this.KeepAlive with Some f -> f () | None -> false))
        && not session.Faulted && not session.Closed
    member _.HasPendingRx = rxMore
    member _.UplinkPace = pace
    member _.TupleMbit = tupleMbit
    /// Poller wait when the uplink group is token-limited: a 5 s wait would stall the pipe.
    member _.WaitMs() =
        if rxMore then 0
        elif not pace then 5000
        elif session.ControlWaiting || not session.WantsToSend then 5000
        else
            let fl = session.FrameLength
            group.Service(session.Clock.NowNs())
            if groupAllows fl false false || groupAllows fl true false then 5000
            else 2

    /// Microseconds to block before the next Watch/Service; -1 blocks until a
    /// descriptor fires. Every event that can let this thread send again has a
    /// signal of its own: a path drain wakes the RxChannel eventfd, a local socket
    /// reports readiness, and each protocol timer reports its deadline. 0 means
    /// work is already runnable. This is an event wait; a fixed interval here
    /// would wake an idle session and add latency to a busy one.
    member _.WaitUs() =
        // No path can carry anything: a socket or listener event is the only thing
        // that can change this, so do not spin on a control cell that has nowhere to
        // go. A join posted from another thread also wakes this loop.
        if paths.Count = 0 then -1
        elif rxMore then 0
        elif session.ControlWaiting then 0
        elif lastRoundSent >= 256 then 0
        else
            let timerUs = session.NextTimerUs()
            if not session.WantsToSend then int timerUs
            elif not pace then int timerUs
            else
                let fl = session.FrameLength
                group.Service(session.Clock.NowNs())
                if groupAllows fl false false || groupAllows fl true false then 0
                else
                    let paceUs = max 1L ((group.TokenWaitNs fl + 999L) / 1000L)
                    if timerUs < 0L then int paceUs else int (min timerUs paceUs)

    /// One line of counters for the log.
    member _.Describe() =
        let tx = session.Tx
        let rx = session.Rx
        let mutable consTx = 0L
        let mutable consAck = 0L
        let mutable ackKnown = true
        let mutable i = 0
        let pathTx = System.Text.StringBuilder()
        let pathTcp = System.Text.StringBuilder()
        let kernel = KernelTcpTotals()
        while i < paths.Count do
            match paths.[i] with
            | :? PathLink as pl ->
                consTx <- consTx + pl.BytesWritten
                let info = pl.TcpInfo
                kernel.Add info
                let acked = match info with ValueSome value -> value.BytesAcked | ValueNone -> -1L
                if acked < 0L then ackKnown <- false
                else consAck <- consAck + acked
                if pathTx.Length > 0 then pathTx.Append(',') |> ignore
                pathTx.Append(int pl.PathId).Append(':').Append(pl.BytesWritten) |> ignore
                if acked >= 0L then pathTx.Append('/').Append(acked) |> ignore
                if pathTcp.Length > 0 then pathTcp.Append(',') |> ignore
                pathTcp.Append(int pl.PathId).Append('@').Append(pl.Incarnation).Append(':') |> ignore
                match info with
                | ValueSome value -> pathTcp.Append(KernelTcpInfo.describePath value) |> ignore
                | ValueNone -> pathTcp.Append('-') |> ignore
            | :? AsyncPathLink as ap ->
                consTx <- consTx + ap.BytesWritten
                if pathTx.Length > 0 then pathTx.Append(',') |> ignore
                pathTx.Append(int ap.Id).Append(':').Append(ap.BytesWritten) |> ignore
                if pathTcp.Length > 0 then pathTcp.Append(',') |> ignore
                pathTcp.Append(int ap.Id).Append('@').Append(int ap.Incarnation).Append(":-") |> ignore
            | _ -> ()
            i <- i + 1
        let now = session.Clock.FreshNs()
        let creditDiag = System.Text.StringBuilder()
        let mutable shown = 0
        let mutable fs = 0
        while fs < 256 && shown < 4 do
            let f = session.Flows.[fs]
            if f.Active then
                if creditDiag.Length > 0 then creditDiag.Append(' ') |> ignore
                creditDiag.Append(fs).Append(':').Append(f.Gen).Append(':')
                    .Append(f.TxOffset).Append('/').Append(f.TxLimit).Append(':')
                    .Append(f.RxDelivered).Append('/').Append(f.Reserve.Count) |> ignore
                shown <- shown + 1
            fs <- fs + 1
        let ackRate =
            if kernel.Count = 0 || not ackKnown || statsAck < 0L || consAck < statsAck
               || pathVersion <> statsPathVersion || now <= statsNs then "-"
            else
                (float (consAck - statsAck) * 8000.0 / float (now - statsNs)).ToString("F3", Globalization.CultureInfo.InvariantCulture)
        statsNs <- now
        statsAck <- if ackKnown then consAck else -1L
        statsPathVersion <- pathVersion
        "paths=" + string paths.Count + " frames_in=" + string framesIn + " frames_out=" + string framesOut
        + " tx_direction=" + (if session.Role = Role.Client then "up" else "down")
        + " send_control=" + (if pace then "group" elif kernel.Count > 0 then "kernel" else "tun")
        + " tx_head=" + string tx.Head + " peer_ack=" + string tx.PeerAck + " grant_limit=" + string tx.GrantLimit
        + " queued=" + string tx.Queued + " srtt_ms=" + string (int (tx.SrttNs / 1e6))
        + " minrtt_ms=" + (if tx.MinRttNs = Int64.MaxValue then "-" else string (tx.MinRttNs / 1_000_000L))
        + " rtt_skipped=" + string tx.RttSkipped + " rho=" + tx.Rho.ToString("F3", Globalization.CultureInfo.InvariantCulture)
        + " cell_loss=" + (if tx.CellLoss < 0.0 then "-" else tx.CellLoss.ToString("F4", Globalization.CultureInfo.InvariantCulture))
        + " boosters=" + string tx.BoostersSent + " repair_repeats=" + string tx.RepairRepeats
        + " spurious_repairs=" + string tx.SpuriousRepairs
        + " ctrl=" + string session.CtrlSent + "/" + string session.CtrlExecuted + "/" + string session.CtrlRejected
        + " close=" + string (int session.CloseState) + " rx_ack=" + string rx.AckStepLo + " rx_base=" + string rx.Decoder.BaseStep
        + " recovered=" + string rx.Counters.Recovered + " interactive=" + string session.InteractiveSources + "/" + string session.Tx.UrgentSteps
        + " efloor=" + string rx.EstimateFloor + " efolds=" + string rx.EstimateFolds
        + " elast=" + string rx.LastFoldedExpected + "/" + string rx.LastFoldedReceived + "@" + string rx.LastFoldedStep
        + " urgent_full=" + string session.Tx.UrgentFull + " flows=" + string bridge.Active
        + " flowread=" + string bridge.ReadCalls + "/" + string bridge.ReadBytes + "/" + string bridge.WouldBlock
        + " opened=" + string session.Flows.Counters.Opened + " closed=" + string session.Flows.Counters.Closed
        + " local_fail=" + string session.Flows.Counters.LocalFailures
        + " stale=" + string session.Flows.Counters.StaleSources
        + " flow_state=" + string session.FlowStatesSent + "/" + string session.FlowStatesRejected
        + " flow_credit=" + (if creditDiag.Length = 0 then "-" else creditDiag.ToString())
        + " fills=" + string tx.FillsSent
        + " gate1=" + string gate1Pass + "/" + string gate1Deny
        + " gate2=" + string gate2Pass + "/" + string gate2Deny
        + " beginfail=" + string beginFail + " producefail=" + string produceFail
        + " sealed=" + string session.Tx.SealedCells + " dropack=" + string session.Tx.DroppedAcked
        + " qhigh=" + string session.Tx.QueueHigh
        + " cons_tx_b=" + string consTx + " cons_ack_b=" + (if ackKnown then string consAck else "-")
        + " path_tx=" + pathTx.ToString()
        + (if kernel.Count = 0 then ""
           else kernel.Describe() + " kernel_ack_mbit=" + ackRate + " kernel_generation=" + string pathVersion
                + " kernel_path=" + pathTcp.ToString())
        + (if not pace then ""
           else
            " pace_bw_mbit=" + (group.Bandwidth * 8000.0).ToString("F1")
            + " pace_inflight=" + string (max 0L (uniqueOut - tx.UniqueAcked * int64 session.FrameLength))
            + " pace_phase=" + group.Phase
            + " pace_pacing_mbit=" + (group.PacingRate * 8000.0).ToString("F1")
            + " pace_full_rounds=" + string group.FullPipeRounds
            + " pace_app_rounds=" + string group.AppLimitedRounds
            + " pace_repair_kib=" + string (group.RepairBytes / 1024L))

    /// Add a path whose bootstrap is done; frames it already holds come in at once.
    member this.AddPath(link: IPath) =
        if pathOpsInline then this.AddPathNow link
        else
            System.Threading.Interlocked.Increment(&pathOpsPending) |> ignore
            pathOps.Enqueue(PathAdd link)

    member private this.AddPathNow(link: IPath) =
        // A JOIN of a path_id whose PATH_CLOSE transaction has ended must clear the
        // session block (spec 6.10a). A path still in Closing is not reusable.
        session.NotePathJoined link.PathId |> ignore
        if session.PathState link.PathId <> PathCloseState.Closing then
            link.FrameLength <- session.FrameLength
            let path, ioPath =
                match io, channel with
                | Some group, Some rx ->
                    match link with
                    | :? PathLink as pl ->
                        let asyncPath = AsyncPathLink.TakeOver(pl, group, rx, txSlots)
                        asyncPath :> IPath, Some asyncPath
                    | _ -> link, None
                | _ -> link, None
            paths.Add path
            pathVersion <- pathVersion + 1L
            // Attach drains the bytes already read. Only after that may an IO
            // thread pick the path up and touch its receive buffer.
            path.Attach this
            match ioPath, io with
            | Some asyncPath, Some group -> group.Add asyncPath
            | _ -> ()

    member _.HasPath(pathId: uint16) =
        paths.Exists(fun p -> p.PathId = pathId)
        || (pathOps |> Seq.exists (function PathAdd p -> p.PathId = pathId | _ -> false))

    /// Close and drop the path with pathId (replaced by a higher incarnation).
    member this.RemovePath(pathId: uint16) =
        if pathOpsInline then this.RemovePathNow pathId
        else
            System.Threading.Interlocked.Increment(&pathOpsPending) |> ignore
            pathOps.Enqueue(PathRemove pathId)

    member private this.RemovePathNow(pathId: uint16) =
        let i = paths.FindIndex(fun p -> p.PathId = pathId)
        if i >= 0 then
            let id = paths.[i].PathId
            paths.[i].Close()
            paths.RemoveAt i
            pathVersion <- pathVersion + 1L
            pacers.Remove id |> ignore

    /// Apply posted path changes on the thread that owns `paths`.
    member private this.ApplyPathOps() =
        let mutable op = Unchecked.defaultof<PathOp>
        while pathOps.TryDequeue(&op) do
            try
                match op with
                | PathAdd p -> this.AddPathNow p
                | PathRemove id -> this.RemovePathNow id
            finally
                System.Threading.Interlocked.Decrement(&pathOpsPending) |> ignore

    member this.Watch(poller: Poller) =
        this.ApplyPathOps()
        let sending =
            if not pace then session.WantsToSend
            elif session.ControlWaiting then true
            elif not session.WantsToSend then false
            else
                let fl = session.FrameLength
                group.Service(session.Clock.NowNs())
                groupAllows fl false false || groupAllows fl true false
        for link in paths do link.Watch(poller, sending)
        // Always arm the local sockets. The old code skipped them while the bulk
        // queue was full (`CanFill` false). The session thread then woke only on
        // tunnel RX frames, so a download read about one cell per wake and ran at
        // 1.5 Mbit/s instead of 10. Read still backs off inside
        // TryBeginSourceFor, so arming a socket costs one WouldBlock, not a stall.
        let armed = bridge.Armed
        let mutable slot = armed.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s -> poller.WatchRead s
            slot <- armed.NextUp slot
        let writers = bridge.Writers
        let mutable slot = writers.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s -> poller.WatchWrite s
            slot <- writers.NextUp slot

    /// Handle what the last wait reported, then send.
    member _.Service(poller: Poller) =
        let n = paths.Count
        let mutable rxBudget = 256
        let mutable more = false
        if n > 0 then
            let start = rxNext % n
            let mutable j = 0
            while j < n do
                let link = paths.[(start + j) % n]
                match link with
                | :? PathLink as pl ->
                    if rxBudget > 0 then
                        if pl.ReceiveBudgeted(poller, &rxBudget) then more <- true
                    elif pl.HasBufferedFrame || poller.IsReadable pl.Socket then
                        more <- true
                | _ -> link.Receive poller
                j <- j + 1
            rxNext <- (start + 1) % n
        rxMore <- more
        let nowNs = session.Clock.NowNs()
        if nowNs - lastHalfOpenProbeNs >= 1_000_000_000L then
            lastHalfOpenProbeNs <- nowNs
            for link in paths do
                match link with
                | :? PathLink as pl -> pl.CheckAlive() |> ignore
                | _ -> ()
        prune ()
        let writers = bridge.Writers
        let mutable slot = writers.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s -> if poller.IsWritable s then bridge.OnWritable slot
            slot <- writers.NextUp slot
        // Deliveries staged while the RxChannel was drained go out now.
        bridge.Flush()
        bridge.Settle()
        // Interactive flows read first (spec 16.3 Q-3: bulk input after the interactive queue).
        hot.Clear()
        readPass poller true
        readPass poller false
        session.Flush()
        session.Tick()
        if pace then
            primeSample ()
            group.Service(session.Clock.NowNs())
        writeRound ()
        bridge.Flush()
        bridge.Settle()
        prune ()

    /// Close every path and local socket.
    member _.Close() =
        for link in paths do link.Close()
        let mutable pendingOp = Unchecked.defaultof<PathOp>
        while pathOps.TryDequeue(&pendingOp) do
            match pendingOp with
            | PathAdd p -> p.Close()
            | PathRemove _ -> ()
        pathOpsPending <- 0
        paths.Clear()
        bridge.CloseAll()

    interface IPathOwner with
        member _.OnFrame(path, frame) =
            framesIn <- framesIn + 1L
            session.OnFrame(path, frame)
        member _.Frontier = session.Frontier
        member _.ProduceFill(path, frame) = session.ProduceFill(path, frame)
        member _.Fail(reason) = session.Fail reason
