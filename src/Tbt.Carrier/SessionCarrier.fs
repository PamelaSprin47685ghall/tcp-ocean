/// Drives one session over its TCP paths and its local flows (spec 6.6 receive, 6.8
/// write gating): whole frames in, one frame per path per round out.
namespace Tbt.Carrier

open System
open System.Collections.Generic
open Tbt.Session

[<Sealed>]
type SessionCarrier(session: Session, bridge: FlowBridge, probe: INotSentProbe) =
    let paths = List<PathLink>()
    let frame: byte[] = Array.zeroCreate session.FrameLength
    let mutable next = 0
    let mutable framesIn = 0L
    let mutable framesOut = 0L

    let drain (link: PathLink) =
        let mutable f = Span<byte>.Empty
        while link.TryTakeFrame(&f) do
            framesIn <- framesIn + 1L
            session.OnFrame(link.PathId, f)

    let remove (i: int) =
        paths.[i].Close()
        paths.RemoveAt i

    /// The kernel may take one more frame for link now.
    let gateOpen (link: PathLink) =
        link.TxIdle
        && (let n = probe.NotSent link.Socket
            n < SocketTuning.NotSentLimit)

    let writeRound () =
        let mutable i = paths.Count - 1
        while i >= 0 do
            if paths.[i].Send() = IoResult.Closed then remove i
            i <- i - 1
        let mutable budget = 256
        let mutable progress = true
        while progress && budget > 0 && paths.Count > 0 && session.WantsToSend do
            progress <- false
            let n = paths.Count
            let mutable j = 0
            while j < n && budget > 0 do
                let link = paths.[(next + j) % n]
                if gateOpen link && session.TryProduce(link.PathId, Span frame) then
                    link.Queue(ReadOnlySpan frame)
                    link.Send() |> ignore
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
    member _.Alive = paths.Count > 0

    /// One line of counters for the log.
    member _.Describe() =
        let tx = session.Tx
        let rx = session.Rx
        "paths=" + string paths.Count + " frames_in=" + string framesIn + " frames_out=" + string framesOut
        + " tx_head=" + string tx.Head + " peer_ack=" + string tx.PeerAck + " grant_limit=" + string tx.GrantLimit
        + " queued=" + string tx.Queued + " rx_ack=" + string rx.AckStepLo + " rx_base=" + string rx.Decoder.BaseStep
        + " recovered=" + string rx.Counters.Recovered + " flows=" + string bridge.Active
        + " opened=" + string session.Flows.Counters.Opened + " closed=" + string session.Flows.Counters.Closed
        + " local_fail=" + string session.Flows.Counters.LocalFailures
        + " stale=" + string session.Flows.Counters.StaleSources
        + " flow_state=" + string session.FlowStatesSent + "/" + string session.FlowStatesRejected

    /// Add a path whose bootstrap is done; frames already buffered are taken at once.
    member _.AddPath(link: PathLink) =
        link.FrameLength <- session.FrameLength
        paths.Add link
        drain link

    member _.HasPath(pathId: uint16) = paths.Exists(fun p -> p.PathId = pathId)

    member _.Watch(poller: Poller) =
        let sending = session.WantsToSend
        for link in paths do
            poller.WatchRead link.Socket
            if not link.TxIdle || sending then poller.WatchWrite link.Socket
        let fill = session.CanFill
        for slot in 0 .. 255 do
            match bridge.Local slot with
            | null -> ()
            | s ->
                if fill && bridge.WantsRead slot then poller.WatchRead s
                if bridge.WantsWrite slot then poller.WatchWrite s

    /// Handle what the last wait reported, then send.
    member _.Service(poller: Poller) =
        let mutable i = paths.Count - 1
        while i >= 0 do
            let link = paths.[i]
            if poller.IsReadable link.Socket then
                let r = link.Receive()
                drain link
                if r = IoResult.Closed then remove i
            i <- i - 1
        for slot in 0 .. 255 do
            match bridge.Local slot with
            | null -> ()
            | s -> if poller.IsWritable s then bridge.OnWritable slot
        bridge.Settle()
        for slot in 0 .. 255 do
            match bridge.Local slot with
            | null -> ()
            | s -> if poller.IsReadable s && bridge.WantsRead slot then bridge.Read slot
        session.Flush()
        session.Tick()
        writeRound ()

    /// Close every path and local socket.
    member _.Close() =
        for link in paths do link.Close()
        paths.Clear()
        bridge.CloseAll()
