/// Server mode: TCP listener for paths; each flow connects to the target (spec 6.5.9
/// bootstrap classification; failures stay silent).
module Tbt.Cli.Server

open System
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Cli.Common

/// A path before its bootstrap is classified.
[<Sealed>]
type private Pending(link: PathLink, since: int64) =
    member _.Link = link
    member _.Since = since
    /// Not ours: read and drop until the peer closes or the time runs out.
    member val Silent = false with get, set

[<Sealed>]
type private Entry(carrier: SessionCarrier, maxPaths: int, admission: JoinAdmission) =
    member _.Carrier = carrier
    member _.MaxPaths = maxPaths
    member _.Admission = admission

let run (o: Options) =
    let listenAt = if o.Has "listen" then ValueSome(endpoint (o.Text "listen")) else ValueNone
    let target = endpoint (o.Text "target")
    let psks = readPsks (o.Text "psk-file")
    let mss = o.Int("mss", 1448)
    let maxPaths = o.Int("max-paths", 64)
    let windowClamp = o.Int("window-clamp", SocketTuning.WindowClamp)
    let statsMs = int64 (o.Int("stats-s", 0)) * 1000L
    let config = { SessionConfig.defaults with WindowSteps = o.Int("window", 1024); MaxPaths = maxPaths }
    let entropy = SystemEntropy()
    let clock = SystemClock()
    let probe = SocketTuning.probe ()
    let offer = ServerOffer(psks, entropy, uint16 config.WindowSteps, maxPaths)
    let pending = List<Pending>()
    let sessions = List<Entry>()
    // Kernel TCP listener for paths (the baseline carrier); optional when a TUN is used.
    let listener: Socket | null =
        match listenAt with
        | ValueSome at ->
            let l = new Socket(at.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            l.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)
            l.Bind at
            l.Listen 512
            l.Blocking <- false
            log ("server on " + at.ToString() + " (kernel TCP) -> " + target.ToString())
            l
        | ValueNone -> null
    // User-space TCP on a TUN device (spec 4.1): the server's carrier for paths.
    let tun: TunStack | null =
        if o.Has "tun" then
            let at = endpoint (o.Text "tun-listen")
            match TunStack.Open(o.Text "tun", at, clock, entropy, maxPaths * 64, windowClamp) with
            | null -> failwith ("cannot open TUN device " + o.Text "tun")
            | t ->
                log ("server on " + at.ToString() + " (user-space TCP on " + o.Text "tun" + ") -> " + target.ToString()
                     + " window_clamp=" + string windowClamp)
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
                let d = e.Admission.Decide(ReadOnlySpan frame, r, c.Paths.Count, c.HasPath r.PathId, s.CloseState <> CloseState.Open)
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
            let carrier = SessionCarrier(session, bridge)
            link.PathId <- 0us
            reply link a.Reply
            carrier.AddPath link
            let paths = min maxPaths (int a.Hello.MaxPaths)
            let admission =
                JoinAdmission(a.Params.Keys.PathKey, entropy, clock, a.Params.Keys.SessionId, a.Params.Symbol, a.Params.Caps,
                              paths, mss, config.MaxPaths)
            sessions.Add(Entry(carrier, paths, admission))
            log ("session " + a.Params.Keys.SessionId.ToString() + " up, S=" + string a.Params.Symbol)
            true
        | ValueNone -> tryJoinAny link frame

    let servicePending (poller: Poller) (now: int64) =
        let mutable i = pending.Count - 1
        while i >= 0 do
            let p = pending.[i]
            let link = p.Link
            let mutable leave = false
            if poller.IsReadable link.Socket then
                let r = link.Receive()
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
    while true do
        match listener with
        | null -> ()
        | l -> poller.WatchRead l
        match tun with
        | null -> ()
        | t -> poller.WatchFd t.Fd
        for p in pending do poller.WatchRead p.Link.Socket
        for e in sessions do e.Carrier.Watch poller
        let tunBusy =
            match tun with
            | null -> false
            | t -> t.Busy
        let busy = tunBusy || sessions.Exists(fun e -> e.Carrier.Session.WantsToSend)
        poller.Wait(if busy then 1000 elif sessions.Count > 0 then 10_000 else 100_000)
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
            if poller.IsFdReadable t.Fd then t.Receive()
            // Bootstrap frames whole on TUN paths: classify (a bad head stays silent).
            for c in t.Fresh do
                if c.BootState = 1 then classify c (c.BootFrame.ToArray()) |> ignore
            t.Fresh.Clear()
        servicePending poller now
        let mutable i = sessions.Count - 1
        while i >= 0 do
            let c = sessions.[i].Carrier
            // Last resort: an exception fails this session only (causes are fixed where found).
            try c.Service poller
            with ex ->
                faults <- faults + 1L
                c.Session.Fail(ex.GetType().Name + ": " + ex.Message)
            if not c.Alive then
                if c.Session.Faulted then failed <- failed + 1L
                let why =
                    if c.Session.Faulted then " failed: " + c.Session.Fault
                    elif c.Session.PeerCloseReason >= 0 then " closed by peer (reason " + string c.Session.PeerCloseReason + ")"
                    else " closed"
                log ("session " + c.Session.Keys.SessionId.ToString() + why)
                c.Close()
                sessions.RemoveAt i
            i <- i - 1
        match tun with
        | null -> ()
        | t -> t.Service()
        if statsMs > 0L && now - lastStats >= statsMs then
            lastStats <- now
            let mutable fin = 0L
            let mutable fout = 0L
            for e in sessions do
                fin <- fin + e.Carrier.FramesIn
                fout <- fout + e.Carrier.FramesOut
            log ("stats sessions=" + string sessions.Count + " sessions_failed=" + string failed
                 + " session_exceptions=" + string faults + " frames_in=" + string fin + " frames_out=" + string fout
                 + " rss_kib=" + string (rssKiB ()) + " alloc_kib=" + string (GC.GetTotalAllocatedBytes false / 1024L)
                 + " gc0=" + string (GC.CollectionCount 0))
            for e in sessions do log ("stats " + e.Carrier.Describe())
            match tun with
            | null -> ()
            | t -> log ("stats " + t.Describe())
            Tbt.Contracts.Trace.flush ()
    0
