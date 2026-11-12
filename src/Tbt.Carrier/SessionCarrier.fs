/// Drives one session over its TCP paths and its local flows (spec 6.6 receive, 6.8
/// write gating): whole frames in, one frame per path per round out.
namespace Tbt.Carrier

open System
open System.Collections.Generic
open Tbt.Session

[<Sealed>]
type SessionCarrier(session: Session, bridge: FlowBridge) =
    let paths = List<IPath>()
    let mutable next = 0
    let mutable framesIn = 0L
    let mutable framesOut = 0L
    /// Local slots visited by Watch and Service (RS-3: grows with armed flows, not 256).
    let mutable slotVisits = 0L

    let prune () =
        let mutable i = paths.Count - 1
        while i >= 0 do
            if not paths.[i].Alive then
                paths.[i].Close()
                paths.RemoveAt i
            i <- i - 1

    let writeRound () =
        let backlog = session.Tx.Queued
        for link in paths do link.Backlog <- backlog
        let mutable budget = 256
        let mutable progress = true
        while progress && budget > 0 && paths.Count > 0 && session.WantsToSend do
            progress <- false
            let n = paths.Count
            let mutable j = 0
            while j < n && budget > 0 do
                let link = paths.[(next + j) % n]
                let mutable frame = Span<byte>.Empty
                if link.TryBeginFrame(&frame) && session.TryProduce(link.PathId, frame) then
                    link.CommitFrame()
                    framesOut <- framesOut + 1L
                    budget <- budget - 1
                    progress <- true
                j <- j + 1
            next <- (next + 1) % n

    member _.Session = session
    member _.Bridge = bridge
    member _.Paths = paths
    member _.FramesIn = framesIn
    member _.FramesOut = framesOut
    member _.SlotVisits = slotVisits
    member _.Alive = paths.Count > 0 && not session.Faulted && not session.Closed

    /// One line of counters for the log.
    member _.Describe() =
        let tx = session.Tx
        let rx = session.Rx
        "paths=" + string paths.Count + " frames_in=" + string framesIn + " frames_out=" + string framesOut
        + " tx_head=" + string tx.Head + " peer_ack=" + string tx.PeerAck + " grant_limit=" + string tx.GrantLimit
        + " queued=" + string tx.Queued + " srtt_ms=" + string (int (tx.SrttNs / 1e6))
        + " ctrl=" + string session.CtrlSent + "/" + string session.CtrlExecuted + "/" + string session.CtrlRejected
        + " close=" + string (int session.CloseState) + " rx_ack=" + string rx.AckStepLo + " rx_base=" + string rx.Decoder.BaseStep
        + " recovered=" + string rx.Counters.Recovered + " interactive=" + string session.InteractiveSources + "/" + string session.Tx.UrgentSteps
        + " urgent_full=" + string session.Tx.UrgentFull + " flows=" + string bridge.Active
        + " opened=" + string session.Flows.Counters.Opened + " closed=" + string session.Flows.Counters.Closed
        + " local_fail=" + string session.Flows.Counters.LocalFailures
        + " stale=" + string session.Flows.Counters.StaleSources
        + " flow_state=" + string session.FlowStatesSent + "/" + string session.FlowStatesRejected
        + " fills=" + string tx.FillsSent

    /// Add a path whose bootstrap is done; frames it already holds come in at once.
    member this.AddPath(link: IPath) =
        link.FrameLength <- session.FrameLength
        paths.Add link
        link.Attach this

    member _.HasPath(pathId: uint16) = paths.Exists(fun p -> p.PathId = pathId)

    member _.Watch(poller: Poller) =
        let sending = session.WantsToSend
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
        let armed = bridge.Armed
        for pass in 0 .. 1 do
            let mutable slot = armed.NextUp -1
            while slot >= 0 do
                slotVisits <- slotVisits + 1L
                match bridge.Local slot with
                | null -> ()
                | s ->
                    if (session.IsInteractive slot = (pass = 0)) && poller.IsReadable s && bridge.WantsRead slot then
                        bridge.Read slot
                slot <- armed.NextUp slot
        session.Flush()
        session.Tick()
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
