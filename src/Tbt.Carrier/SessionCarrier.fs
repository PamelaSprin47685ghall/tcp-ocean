/// Drives one session over its TCP paths and its local flows (spec 6.6 receive, 6.8
/// write gating): whole frames in, one frame per path per round out.
namespace Tbt.Carrier

open System
open System.Collections.Generic
open Tbt.Contracts
open Tbt.Session

/// One session-level member of the client uplink RateGroup. Delivery is session
/// custody (peer_ack), not TCP ACK: the kernel can ACK while the server decoder lags.
type private SessionRateMember(session: Session, frameOf: unit -> int, uniqueOut: unit -> int64, wireOut: unit -> int64) =
    let mutable lastAck = 0UL
    let mutable lastNs = 0L
    interface IRateMember with
        // Unique payload still awaiting custody, not every BOOSTER copy: counting copies
        // against cwnd left the group unable to send after a repair burst.
        member _.Inflight =
            let fl = int64 (frameOf())
            int (max 0L (uniqueOut() - session.Tx.UniqueAcked * fl))
        member _.Delivered = session.Tx.UniqueAcked * int64 (frameOf())
        member _.CopyEstimate = max 0L (wireOut() - uniqueOut())
        member _.MinRttNs = session.Tx.MinRttNs
        member _.LastDeliveredNs =
            if session.Tx.PeerAck > lastAck then
                lastAck <- session.Tx.PeerAck
                lastNs <- session.Clock.NowNs()
            lastNs

[<Sealed>]
type SessionCarrier(session: Session, bridge: FlowBridge, ?uplinkPace: bool, ?tupleMbit: float) =
    let pace = defaultArg uplinkPace false
    let tupleMbit = defaultArg tupleMbit 0.0
    let paths = List<IPath>()
    let mutable next = 0
    let mutable framesIn = 0L
    let mutable framesOut = 0L
    /// Bytes of DATA/PARITY/BOOSTER put on the wire (not WIN_ACK/CTRL/heartbeat).
    let mutable wireOut = 0L
    /// DATA/PARITY bytes only (BOOSTER copies go in wireOut but not here).
    let mutable uniqueOut = 0L
    let group = RateGroup(session.Clock)
    let member' = SessionRateMember(session, (fun () -> session.FrameLength), (fun () -> uniqueOut), (fun () -> wireOut))
    let mutable samplePrimed = false
    do if pace then group.Add member'
    let pacers = Dictionary<uint16, PathPacer>()
    /// Local slots visited by Watch and Service (RS-3: grows with armed flows, not 256).
    let mutable slotVisits = 0L

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

    /// Restart the group's sample the first time a min-RTT exists, so the first
    /// delivery rate is not averaged over the handshake delay (that sample was
    /// ~1 frame / several seconds and left cwnd at 4 frames forever).
    let primeSample () =
        if pace && not samplePrimed && session.Tx.MinRttNs <> Int64.MaxValue then
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

    let prune () =
        let mutable i = paths.Count - 1
        while i >= 0 do
            if not paths.[i].Alive then
                paths.[i].Close()
                paths.RemoveAt i
            i <- i - 1

    /// Interactive slots that were readable in this round (re-read between bulk batches).
    let hot = ReadySet()

    /// Q-3 (spec 16.3): one batch gives each path at most 4 frames, within budget. Returns frames sent.
    /// When uplinkPace is set, WIN_ACK/CTRL/heartbeat skip the group; new data uses TryTake
    /// (aggregate 1.25 BDP); BOOSTERs use the charged repair reserve (TryTakeRepair / OnRepair)
    /// and are skipped when that reserve is dry so new data can still go.
    let writeBatch (budget0: int) =
        let mutable budget = budget0
        let mutable progress = true
        let mutable passes = 0
        let fl = session.FrameLength
        let now = session.Clock.NowNs()
        primeSample ()
        while progress && passes < 4 && budget > 0 && paths.Count > 0 && session.WantsToSend do
            progress <- false
            passes <- passes + 1
            let n = paths.Count
            let mutable j = 0
            while j < n && budget > 0 do
                let link = paths.[(next + j) % n]
                let mutable frame = Span<byte>.Empty
                if link.TryBeginFrame(&frame) then
                    let mutable allowRepair = true
                    let mutable charge = 0
                    let mutable attempt = false
                    if not pace || session.ControlWaiting then
                        attempt <- true
                    elif session.RepairWaiting && groupAllows fl true (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                        attempt <- true
                        charge <- 1
                    elif groupAllows fl false (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                        // allowRepair=false: a dry repair reserve must not block new data.
                        attempt <- true
                        allowRepair <- false
                        charge <- 2
                    if attempt && session.TryProduce(link.PathId, frame, allowRepair) then
                        link.CommitFrame()
                        framesOut <- framesOut + 1L
                        budget <- budget - 1
                        progress <- true
                        if charge = 1 then
                            wireOut <- wireOut + int64 fl
                            group.OnRepair fl
                            chargePath link.PathId fl
                        elif charge = 2 then
                            wireOut <- wireOut + int64 fl
                            uniqueOut <- uniqueOut + int64 fl
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
                if session.IsInteractive slot = interactive && poller.IsReadable s then
                    if interactive then hot.Add slot
                    if bridge.WantsRead slot then bridge.Read slot
            slot <- armed.NextUp slot

    /// Send at most 256 frames in batches; the interactive input is read again between batches.
    let writeRound () =
        let backlog = session.Tx.Queued
        for link in paths do link.Backlog <- backlog
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
                    if bridge.WantsRead slot && session.IsInteractive slot then bridge.Read slot
                    slot <- hot.NextUp slot

    member _.Session = session
    member _.Bridge = bridge
    member _.Paths = paths
    member _.FramesIn = framesIn
    member _.FramesOut = framesOut
    member _.SlotVisits = slotVisits
    /// One Q-3 batch (tests): at most 4 frames per path.
    member _.WriteBatch(budget: int) = writeBatch budget
    member _.Alive = paths.Count > 0 && not session.Faulted && not session.Closed
    member _.UplinkPace = pace
    member _.TupleMbit = tupleMbit
    /// Poller wait when the uplink group is token-limited: a 5 s wait would stall the pipe.
    member _.WaitMs() =
        if not pace then 5000
        elif session.ControlWaiting || not session.WantsToSend then 5000
        else
            let fl = session.FrameLength
            group.Service(session.Clock.NowNs())
            if groupAllows fl false false || groupAllows fl true false then 5000
            else 2

    /// One line of counters for the log.
    member _.Describe() =
        let tx = session.Tx
        let rx = session.Rx
        "paths=" + string paths.Count + " frames_in=" + string framesIn + " frames_out=" + string framesOut
        + " tx_head=" + string tx.Head + " peer_ack=" + string tx.PeerAck + " grant_limit=" + string tx.GrantLimit
        + " queued=" + string tx.Queued + " srtt_ms=" + string (int (tx.SrttNs / 1e6))
        + " minrtt_ms=" + (if tx.MinRttNs = Int64.MaxValue then "-" else string (tx.MinRttNs / 1_000_000L))
        + " rtt_skipped=" + string tx.RttSkipped + " boosters=" + string tx.BoostersSent + " repair_repeats=" + string tx.RepairRepeats
        + " ctrl=" + string session.CtrlSent + "/" + string session.CtrlExecuted + "/" + string session.CtrlRejected
        + " close=" + string (int session.CloseState) + " rx_ack=" + string rx.AckStepLo + " rx_base=" + string rx.Decoder.BaseStep
        + " recovered=" + string rx.Counters.Recovered + " interactive=" + string session.InteractiveSources + "/" + string session.Tx.UrgentSteps
        + " urgent_full=" + string session.Tx.UrgentFull + " flows=" + string bridge.Active
        + " opened=" + string session.Flows.Counters.Opened + " closed=" + string session.Flows.Counters.Closed
        + " local_fail=" + string session.Flows.Counters.LocalFailures
        + " stale=" + string session.Flows.Counters.StaleSources
        + " flow_state=" + string session.FlowStatesSent + "/" + string session.FlowStatesRejected
        + " fills=" + string tx.FillsSent
        + (if not pace then ""
           else
            " pace_bw_mbit=" + (group.Bandwidth * 8000.0).ToString("F1")
            + " pace_inflight=" + string (max 0L (uniqueOut - tx.UniqueAcked * int64 session.FrameLength))
            + " pace_phase=" + (if group.InStartup then "startup" else "probe" + string group.Cycle)
            + " pace_repair_kib=" + string (group.RepairBytes / 1024L))

    /// Add a path whose bootstrap is done; frames it already holds come in at once.
    member this.AddPath(link: IPath) =
        link.FrameLength <- session.FrameLength
        paths.Add link
        link.Attach this

    member _.HasPath(pathId: uint16) = paths.Exists(fun p -> p.PathId = pathId)

    /// Close and drop the path with pathId (replaced by a higher incarnation).
    member _.RemovePath(pathId: uint16) =
        let i = paths.FindIndex(fun p -> p.PathId = pathId)
        if i >= 0 then
            let id = paths.[i].PathId
            paths.[i].Close()
            paths.RemoveAt i
            pacers.Remove id |> ignore

    member _.Watch(poller: Poller) =
        let sending =
            if not pace then session.WantsToSend
            elif session.ControlWaiting then true
            elif not session.WantsToSend then false
            else
                let fl = session.FrameLength
                group.Service(session.Clock.NowNs())
                groupAllows fl false false || groupAllows fl true false
        for link in paths do link.Watch(poller, sending)
        let fill = session.CanFill
        let fillInteractive = session.CanFillInteractive
        if fill || fillInteractive then
            let armed = bridge.Armed
            let mutable slot = armed.NextUp -1
            while slot >= 0 do
                slotVisits <- slotVisits + 1L
                match bridge.Local slot with
                | null -> ()
                | s -> if fill || session.IsInteractive slot then poller.WatchRead s
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
        for link in paths do link.Receive poller
        prune ()
        let writers = bridge.Writers
        let mutable slot = writers.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s -> if poller.IsWritable s then bridge.OnWritable slot
            slot <- writers.NextUp slot
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
        prune ()

    /// Close every path and local socket.
    member _.Close() =
        for link in paths do link.Close()
        paths.Clear()
        bridge.CloseAll()

    interface IPathOwner with
        member _.OnFrame(path, frame) =
            framesIn <- framesIn + 1L
            session.OnFrame(path, frame)
        member _.Frontier = session.Frontier
        member _.ProduceFill(path, frame) = session.ProduceFill(path, frame) |> ignore
        member _.Fail(reason) = session.Fail reason
