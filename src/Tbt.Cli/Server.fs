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
type private Entry(carrier: SessionCarrier, maxPaths: int) =
    member _.Carrier = carrier
    member _.MaxPaths = maxPaths

let run (o: Options) =
    let listenAt = endpoint (o.Text "listen")
    let target = endpoint (o.Text "target")
    let psks = readPsks (o.Text "psk-file")
    let mss = o.Int("mss", 1448)
    let maxPaths = o.Int("max-paths", 64)
    let statsMs = int64 (o.Int("stats-s", 0)) * 1000L
    let config = { SessionConfig.defaults with WindowSteps = o.Int("window", 1024); MaxPaths = maxPaths }
    let entropy = SystemEntropy()
    let clock = SystemClock()
    let probe = SocketTuning.probe ()
    let offer = ServerOffer(psks, entropy, uint16 config.WindowSteps, maxPaths)
    let pending = List<Pending>()
    let sessions = List<Entry>()
    let listener = new Socket(listenAt.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
    listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true)
    listener.Bind listenAt
    listener.Listen 512
    listener.Blocking <- false
    log ("server on " + listenAt.ToString() + " -> " + target.ToString())

    let reply (link: PathLink) (frame: byte[]) =
        link.Queue(ReadOnlySpan responseHead)
        link.Queue(ReadOnlySpan frame)
        link.Send() |> ignore

    let tryJoinAny (link: PathLink) (frame: byte[]) =
        let mutable done' = false
        let mutable i = 0
        while not done' && i < sessions.Count do
            let e = sessions.[i]
            let s = e.Carrier.Session
            match tryJoin s.Keys.PathKey (ReadOnlySpan frame) with
            | ValueSome r ->
                done' <- true
                if int r.PathId < e.MaxPaths && not (e.Carrier.HasPath r.PathId) then
                    let ack = joinAck s.Keys.PathKey entropy s.Keys.SessionId s.SymbolSize s.Caps e.MaxPaths (ReadOnlySpan frame) r
                                      Tbt.Crypto.Join.JoinStatus.Accept mss
                    link.PathId <- r.PathId
                    link.Incarnation <- r.Incarnation
                    reply link ack
                    e.Carrier.AddPath link
                else link.Close()
            | ValueNone -> ()
            i <- i + 1
        done'

    /// Classify the bootstrap frame (spec 6.5.9). True when the link left the pending list.
    let classify (p: Pending) (frame: byte[]) =
        match offer.TryAccept(ReadOnlySpan frame, unixMs (), mss) with
        | ValueSome a ->
            let bridge = FlowBridge(ValueSome(target :> EndPoint))
            let session = Session(Role.Server, a.Params, bridge, clock, config)
            bridge.Attach session
            let carrier = SessionCarrier(session, bridge, probe)
            p.Link.PathId <- 0us
            reply p.Link a.Reply
            carrier.AddPath p.Link
            sessions.Add(Entry(carrier, min maxPaths (int a.Hello.MaxPaths)))
            log ("session " + a.Params.Keys.SessionId.ToString() + " up, S=" + string a.Params.Symbol)
            true
        | ValueNone ->
            if tryJoinAny p.Link frame then true
            else
                p.Silent <- true
                false

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
                        if link.TryTakeFrame(&boot) then leave <- classify p (boot.ToArray())
                    elif failed then p.Silent <- true
                if not leave && r = IoResult.Closed then
                    link.Close()
                    leave <- true
            if not leave && now - p.Since > (if p.Silent then 60_000L else 15_000L) then
                link.Close()
                leave <- true
            if leave then pending.RemoveAt i
            i <- i - 1

    let poller = Poller()
    let mutable lastStats = Environment.TickCount64
    while true do
        poller.WatchRead listener
        for p in pending do poller.WatchRead p.Link.Socket
        for e in sessions do e.Carrier.Watch poller
        poller.Wait 5000
        let now = Environment.TickCount64
        if poller.IsReadable listener then
            let mutable go = true
            while go do
                match tryAccept listener with
                | null -> go <- false
                | s ->
                    SocketTuning.path s
                    s.Blocking <- false
                    pending.Add(Pending(PathLink s, now))
        servicePending poller now
        let mutable i = sessions.Count - 1
        while i >= 0 do
            let c = sessions.[i].Carrier
            c.Service poller
            if not c.Alive then
                log ("session " + c.Session.Keys.SessionId.ToString() + " closed")
                c.Close()
                sessions.RemoveAt i
            i <- i - 1
        if statsMs > 0L && now - lastStats >= statsMs then
            lastStats <- now
            let mutable fin = 0L
            let mutable fout = 0L
            for e in sessions do
                fin <- fin + e.Carrier.FramesIn
                fout <- fout + e.Carrier.FramesOut
            log ("stats sessions=" + string sessions.Count + " frames_in=" + string fin + " frames_out=" + string fout
                 + " rss_kib=" + string (rssKiB ()) + " alloc_kib=" + string (GC.GetTotalAllocatedBytes false / 1024L)
                 + " gc0=" + string (GC.CollectionCount 0))
            for e in sessions do log ("stats " + e.Carrier.Describe())
    0
