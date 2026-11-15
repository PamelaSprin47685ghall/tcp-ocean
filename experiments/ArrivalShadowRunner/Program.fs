/// Single-session, single-path diagnostic harness. Not a production CLI replacement.
module Tbt.ArrivalShadowRunner.Program

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open Tbt.Contracts
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier

[<Sealed>]
type private Clock() =
    interface IClock with
        member _.NowNs()=Monotonic.nowNs()
        member _.FreshNs()=Monotonic.nowNs()
[<Sealed>]
type private Entropy() =
    interface IEntropy with member _.Fill bytes=RandomNumberGenerator.Fill bytes

type private Mode = Legacy | Extension | Forward | Shadow
let private mode = function
    | "legacy-direct" -> Legacy | "extension-direct" -> Extension
    | "extension-forward" -> Forward | "extension-shadow" -> Shadow
    | _ -> invalidArg "mode" "unknown experiment mode"

/// Strict pairs, no implicit production defaults/options inherited from CLI.
type private Args(argv:string[]) =
    let values=Collections.Generic.Dictionary<string,string>()
    do
        if argv.Length%2<>0 then invalidArg "args" "expected --name value pairs"
        for i in 0..2..argv.Length-2 do
            if not (argv.[i].StartsWith "--") || values.ContainsKey argv.[i] then invalidArg "args" "invalid or duplicate option"
            values.Add(argv.[i],argv.[i+1])
    member _.Get key=values.["--"+key]
    member _.Get(key,fallback)=match values.TryGetValue("--"+key) with true,v->v | _->fallback
    member this.Int(key,fallback)=Int32.Parse(this.Get(key,string fallback),Globalization.CultureInfo.InvariantCulture)

/// Reuses the existing CachedClock.Tick value; no additional clock or proc read.
type private LoopStats() =
    let mutable last=0L
    let mutable maxGap=0L
    let mutable count=0L
    member _.Record(stamp:int64) =
        if last<>0L then maxGap<-max maxGap (stamp-last)
        last<-stamp
        count<-count+1L
    member _.Text="loop_iterations="+string count+" max_loop_tick_gap_ns="+string maxGap

let private pskFile path =
    let keys=File.ReadAllLines path |> Array.map(fun line->line.Split('#').[0].Trim()) |> Array.filter(fun line->line.Length>0)
    if keys.Length<>1 then invalidArg "psk-file" "single synthetic test PSK required"
    let key=Convert.FromHexString keys.[0]
    if key.Length<>32 then invalidArg "psk-file" "expected 32-byte PSK"
    key

let private endpoint (text:string)=IPEndPoint.Parse text
let private unixMs ()=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()

/// Minimal bootstrap using public PathLink framing; a blocking socket only until
/// handshake completion, then the existing readiness-driven carrier takes ownership.
let private connect (at:IPEndPoint) (offer:ClientOffer) =
    let socket=new Socket(at.AddressFamily,SocketType.Stream,ProtocolType.Tcp)
    try
        SocketTuning.path(socket,SocketTuning.WindowClamp)
        socket.SendTimeout<-10000
        socket.ReceiveTimeout<-10000
        let task=socket.ConnectAsync at
        if not (task.Wait 10000) then failwith "connect timeout"
        task.GetAwaiter().GetResult()
        let link=PathLink(socket,SocketTuning.probe())
        link.Queue(ReadOnlySpan(requestHead "diagnostic.invalid"))
        link.Queue(ReadOnlySpan offer.Frame)
        while not link.TxIdle do
            if link.Send()<>IoResult.Progress then failwith "bootstrap send failed"
        let mutable failed=false
        let mutable frame=Span<byte>.Empty
        let mutable reply:byte[] option=None
        while reply.IsNone do
            if link.Receive()<>IoResult.Progress then failwith "bootstrap receive failed"
            if link.TryTakeHead(&failed) && link.TryTakeFrame(&frame) then reply<-Some(frame.ToArray())
            elif failed then failwith "bootstrap header invalid"
        let parameters,_=match offer.TryAccept(ReadOnlySpan reply.Value) with ValueSome(struct(p,h))->p,h | _->failwith "bootstrap authentication failed"
        socket.Blocking<-false
        link,parameters
    with _ -> socket.Dispose();reraise()

let private attach mode capacity (session:Session) (carrier:SessionCarrier) (path:IPath) =
    let collector=if mode=Shadow then Some(Collector(session,capacity)) else None
    let wrapper=
        if mode=Forward || mode=Shadow then
            Some(ObservedPath(path,session,collector |> Option.map(fun c->c :> IAcceptedObserver)))
        else None
    match wrapper with
    | Some wrapped -> carrier.AddPath(wrapped :> IPath)
    | None -> carrier.AddPath path
    collector,wrapper

/// Boundary-only raw socket evidence, explicitly not SessionCarrier.Describe's
/// standard kernel_path output (a decorator hides the PathLink runtime type).
let private kernelBoundary (link:PathLink) =
    match link.TcpInfo with
    | ValueNone -> "available=0"
    | ValueSome i ->
        "available=1 bytes_acked="+string i.BytesAcked+" total_retrans="+string i.TotalRetrans
            + " rtt_us="+string i.RttUs+" rwnd_limited_us="+string i.RwndLimitedUs
            + " sndbuf_limited_us="+string i.SendBufferLimitedUs

let private finish output role modeText (session:Session) (carrier:SessionCarrier) (collector:Collector option)
                   (wrapper:ObservedPath option) (boundaryStart:string) (boundaryEnd:string) (cpuMs:float) (wallMs:float) (loopStats:LoopStats) =
    let captureOkay=
        match collector,wrapper with
        | Some c,Some w -> c.Complete && not w.DiagnosticFault
        | None,Some w -> not w.DiagnosticFault
        | _ -> true
    let qualityOkay=match collector with Some c->captureOkay && c.ShadowUsable(Monotonic.nowNs()) | None->captureOkay
    use exportProc=Process.GetCurrentProcess()
    let exportCpuStart=exportProc.TotalProcessorTime
    let exportTimer=Stopwatch.StartNew()
    match collector with Some c->c.WriteCsv(output+".csv") | _->()
    let exportWall=exportTimer.Elapsed.TotalMilliseconds
    let exportCpu=(exportProc.TotalProcessorTime-exportCpuStart).TotalMilliseconds
    use writer=new StreamWriter(output+".summary",false)
    writer.WriteLine("role="+role+" mode="+modeText+" negotiated="+string session.ArrivalEvidenceEnabled)
    writer.WriteLine("session="+session.Keys.SessionId.ToString()+" path_id=0 incarnation=1 path_count=1")
    writer.WriteLine("capture_complete="+string captureOkay+" session_fault="+string session.Faulted+" session_closed="+string session.Closed)
    writer.WriteLine("frames_in="+string carrier.FramesIn+" frames_out="+string carrier.FramesOut)
    writer.WriteLine("cpu_ms="+cpuMs.ToString("R",Globalization.CultureInfo.InvariantCulture)+" wall_ms="+wallMs.ToString("R",Globalization.CultureInfo.InvariantCulture))
    writer.WriteLine("export_wall_ms="+exportWall.ToString("R",Globalization.CultureInfo.InvariantCulture)+" export_cpu_ms="+exportCpu.ToString("R",Globalization.CultureInfo.InvariantCulture))
    writer.WriteLine(loopStats.Text)
    writer.WriteLine("raw_path_start "+boundaryStart)
    writer.WriteLine("raw_path_end "+boundaryEnd)
    writer.WriteLine("standard_describe_kernel_path_not_collected=1")
    match collector with
    | None -> writer.WriteLine("shadow_enabled=0")
    | Some c ->
        writer.WriteLine("shadow_enabled=1 reports="+string c.Reports+" recorded="+string c.Recorded+" dropped="+string c.Dropped+" collector_errors="+string c.Errors)
        writer.WriteLine("observer_ticks="+string c.ProcessingTicks+" observer_max_ticks="+string c.MaxProcessingTicks+" stopwatch_hz="+string Stopwatch.Frequency)
        writer.WriteLine("shadow_only=1 control_eligible=0 samples="+string c.Samples+" invalid_reports="+string c.InvalidReports+" rejected_reports="+string c.RejectedReports+" shadow_usable="+string qualityOkay+" shadow_status="+string(c.StatusCode(Monotonic.nowNs())))
    if session.Faulted || not session.Closed || not qualityOkay then 2 else 0

// Optional lifecycle evidence only. No instance or per-write accounting unless requested.
let private lifecycle (args:Args) (bridge:FlowBridge) =
    if args.Int("lifecycle",0)=1 then Some(LifecyclePort(bridge :> IFlowPort,256)) else None
let private port (bridge:FlowBridge) = function
    | Some (p:LifecyclePort) -> p :> IFlowPort
    | None -> bridge :> IFlowPort
let private mark active name = function
    | Some (p:LifecyclePort) -> p.Record(name,active)
    | None -> ()
let private exportLifecycle output result = function
    | None -> result
    | Some (p:LifecyclePort) ->
        p.WriteCsv(output+".lifecycle.csv")
        File.AppendAllText(output+".summary", "lifecycle_enabled=1 lifecycle_complete="+string p.Complete+" lifecycle_dropped="+string p.Dropped+" lifecycle_errors="+string p.Errors+Environment.NewLine)
        if p.Complete then result else 2

let private runClient (args:Args) mode modeText psk =
    let clock=CachedClock(Clock())
    let entropy=Entropy()
    let offer=ClientOffer(psk,Guid.NewGuid(),entropy,uint64(unixMs()),1448,1,offerArrivalEvidence=(mode<>Legacy))
    let link,parameters=connect (endpoint(args.Get "server")) offer
    let bridge=FlowBridge ValueNone
    let config={SessionConfig.defaults with MaxPaths=1}
    let life=lifecycle args bridge
    let session=Session(Role.Client,parameters,port bridge life,clock,config)
    bridge.Attach session
    if session.ArrivalEvidenceEnabled<>(mode<>Legacy) then failwith "unexpected negotiation"
    let carrier=SessionCarrier(session,bridge,uplinkPace=(args.Int("uplink-pace",0)<>0))
    let collector,wrapper=attach mode (args.Int("capacity",16384)) session carrier (link :> IPath)
    let startBoundary=kernelBoundary link
    use listener=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp)
    listener.Bind(endpoint(args.Get "listen"))
    listener.Listen 16
    listener.Blocking<-false
    use poller=new Poller()
    use proc=Process.GetCurrentProcess()
    let cpuStart=proc.TotalProcessorTime
    let timer=Stopwatch.StartNew()
    mark bridge.Active "timer_started" life
    let loopStats=LoopStats()
    let seconds=args.Int("seconds",10)
    if seconds<1 || seconds>120 then invalidArg "seconds" "range 1..120"
    Console.WriteLine("ready role=client path_count=1")
    let mutable closing=false
    while carrier.Alive && timer.Elapsed.TotalSeconds<float(seconds+2) do
        if timer.Elapsed.TotalSeconds>=float seconds && not closing then
            closing<-true
            mark bridge.Active "session_close_before" life
            let closeStarted=session.Close 0uy
            mark bridge.Active (if closeStarted then "session_close_started" else "session_close_refused") life
        if not closing then poller.WatchRead listener
        carrier.Watch poller
        poller.Wait(min 10000 (carrier.WaitMs()))
        loopStats.Record(clock.Tick())
        if not closing && poller.IsReadable listener then
            let mutable accepting=true
            while accepting do
                try
                    let socket=listener.Accept()
                    if not (bridge.TryAdd socket) then socket.Dispose()
                with :? SocketException as ex when ex.SocketErrorCode=SocketError.WouldBlock -> accepting<-false
        carrier.Service poller
    mark bridge.Active "loop_exited" life
    let cpu=(proc.TotalProcessorTime-cpuStart).TotalMilliseconds
    let wall=timer.Elapsed.TotalMilliseconds
    let endBoundary=kernelBoundary link
    mark bridge.Active "summary_export_before" life
    let result=finish (args.Get "output") "client" modeText session carrier collector wrapper startBoundary endBoundary cpu wall loopStats
    mark bridge.Active "summary_export_after" life
    mark bridge.Active "carrier_close_before" life
    carrier.Close()
    mark bridge.Active "carrier_close_after" life
    exportLifecycle (args.Get "output") result life

let private runServer (args:Args) mode modeText psk =
    let clock=CachedClock(Clock())
    let entropy=Entropy()
    let config={SessionConfig.defaults with MaxPaths=1}
    let offer=ServerOffer([|psk|],entropy,uint16 config.WindowSteps,1,offerArrivalEvidence=(mode<>Legacy))
    let tun=
        match TunStack.Open(args.Get "tun",endpoint(args.Get "tun-listen"),clock,entropy,1) with
        | null -> failwith "TUN unavailable"
        | value -> value
    use poller=new Poller()
    let startup=Stopwatch.StartNew()
    let mutable state:(Session*SessionCarrier*TunPath*Collector option*ObservedPath option*FlowBridge*LifecyclePort option) option=None
    Console.WriteLine("ready role=server path_count=0")
    let classify () =
        for path in tun.Fresh do
            if path.BootState=1 then
                if state.IsSome then (path :> IPath).Close()
                else
                    match offer.TryAccept(ReadOnlySpan(path.BootFrame.ToArray()),unixMs(),1448) with
                    | ValueNone -> (path :> IPath).Close()
                    | ValueSome accepted ->
                        let bridge=FlowBridge(ValueSome(endpoint(args.Get "target") :> EndPoint))
                        let life=lifecycle args bridge
                        let session=Session(Role.Server,accepted.Params,port bridge life,clock,config)
                        if session.ArrivalEvidenceEnabled<>(mode<>Legacy) then failwith "unexpected negotiation"
                        bridge.Attach session
                        let carrier=SessionCarrier(session,bridge)
                        path.SendReliable(ReadOnlySpan(Array.append responseHead accepted.Reply))
                        let collector,wrapper=attach mode (args.Int("capacity",16384)) session carrier (path :> IPath)
                        state<-Some(session,carrier,path,collector,wrapper,bridge,life)
        tun.Fresh.Clear()
    while state.IsNone && startup.Elapsed.TotalSeconds<15.0 do
        poller.WatchFd tun.Fd
        poller.Wait 10000
        clock.Tick() |> ignore
        tun.Receive(SocketTuning.TunRecvBudget) |> ignore
        classify()
        tun.Service()
    let session,carrier,path,collector,wrapper,bridge,life=match state with Some value->value | _->failwith "session startup timeout"
    use proc=Process.GetCurrentProcess()
    let cpuStart=proc.TotalProcessorTime
    let timer=Stopwatch.StartNew()
    mark bridge.Active "timer_started" life
    let loopStats=LoopStats()
    let seconds=args.Int("seconds",10)
    if seconds<1 || seconds>120 then invalidArg "seconds" "range 1..120"
    let boundary ()="kind=tun remote_ip="+string path.RemoteIp+" inflight="+string path.Inflight+" sent_segments="+string path.Counters.SegmentsSent+" fault_count="+string path.Counters.Faults
    let startBoundary=boundary()
    while carrier.Alive && timer.Elapsed.TotalSeconds<float(seconds+5) do
        poller.WatchFd tun.Fd
        carrier.Watch poller
        poller.Wait(if tun.Busy || session.WantsToSend then 1000 else 10000)
        loopStats.Record(clock.Tick())
        tun.Receive(SocketTuning.TunRecvBudget) |> ignore
        classify()
        carrier.Service poller
        tun.Service()
        tun.Receive(SocketTuning.TunRecvBudget) |> ignore
        classify()
    mark bridge.Active "loop_exited" life
    let cpu=(proc.TotalProcessorTime-cpuStart).TotalMilliseconds
    let wall=timer.Elapsed.TotalMilliseconds
    mark bridge.Active "summary_export_before" life
    let result=finish (args.Get "output") "server" modeText session carrier collector wrapper startBoundary (boundary()) cpu wall loopStats
    mark bridge.Active "summary_export_after" life
    mark bridge.Active "carrier_close_before" life
    carrier.Close()
    mark bridge.Active "carrier_close_after" life
    exportLifecycle (args.Get "output") result life

[<EntryPoint>]
let main argv =
    try
        let args=Args argv
        let modeText=args.Get "mode"
        let selected=mode modeText
        let psk=pskFile(args.Get "psk-file")
        match args.Get "role" with
        | "client" -> runClient args selected modeText psk
        | "server" -> runServer args selected modeText psk
        | _ -> invalidArg "role" "client or server required"
    with ex ->
        // No args, PSK file contents, payloads or secret-bearing exception messages.
        Console.Error.WriteLine("diagnostic_runner_failed type="+ex.GetType().Name)
        2
