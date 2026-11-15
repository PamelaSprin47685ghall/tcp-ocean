module Tbt.Tests.ArrivalLifecycleTests

open System
open Xunit
open Tbt.Session
open Tbt.ArrivalShadowRunner

[<Sealed>]
type private Port() =
    member val Result=2 with get,set
    member val Finished=0 with get,set
    member val Closed=0 with get,set
    member val Resets=0 with get,set
    member val Credits=0 with get,set
    member val CloseAllCount=0 with get,set
    member val Failure:Exception option=None with get,set
    member this.CloseAll()=this.CloseAllCount<-this.CloseAllCount+1
    interface IFlowPort with
        member _.Open(_,_) = ()
        member this.Write(_,_) =
            match this.Failure with Some ex->raise ex | None->this.Result
        member this.Finish _ =
            match this.Failure with Some ex->raise ex | None->this.Finished<-this.Finished+1
        member this.Reset _=this.Resets<-this.Resets+1
        member this.Close _=this.Closed<-this.Closed+1
        member this.Credit _=this.Credits<-this.Credits+1

[<Fact>]
let ``lifecycle write counts actual partial successes without reading a clock`` () =
    let raw=Port()
    let mutable clocks=0
    let tap=LifecyclePort(raw,32,nowNs=(fun ()->clocks<-clocks+1;int64 clocks))
    let port=tap:>IFlowPort
    port.Open(0,1u)
    let before=clocks
    for n in [2;0;-1;3] do
        raw.Result<-n
        Assert.Equal(n,port.Write(0,ReadOnlySpan [|1uy;2uy;3uy|]))
    Assert.Equal(before,clocks)
    Assert.Equal(5L,tap.TotalWrittenBytes)
    Assert.Equal(4L,tap.WriteCalls)
    port.Credit 0
    Assert.Equal(1,raw.Credits)
    port.Finish 0
    Assert.Equal(5L,tap.Events.[tap.Recorded-1].WrittenBytes)
    port.Open(0,2u)
    Assert.Equal(0L,tap.Events.[tap.Recorded-1].WrittenBytes)
    Assert.Equal(2u,tap.Events.[tap.Recorded-1].Generation)
    Assert.Equal(5L,tap.TotalWrittenBytes)

[<Fact>]
let ``flow finish differs from runner closeall which bypasses the port`` () =
    let raw=Port()
    let tap=LifecyclePort(raw,32,nowNs=(fun ()->1L))
    let port=tap:>IFlowPort
    port.Open(0,1u)
    port.Finish 0
    Assert.Equal(1,raw.Finished)
    Assert.Equal(0,raw.Closed)
    tap.Record("carrier_close_before",1)
    raw.CloseAll() // Models FlowBridge.CloseAll direct call, not IFlowPort.Close.
    tap.Record("carrier_close_after",0)
    Assert.Equal(1,raw.CloseAllCount)
    Assert.DoesNotContain(tap.Events,fun e->e.Name="close_before")
    Assert.Contains(tap.Events,fun e->e.Name="finish_after")
    Assert.Contains(tap.Events,fun e->e.Name="carrier_close_after" && e.Active=0)
    port.Reset 0
    port.Close 0
    Assert.Equal(1,raw.Resets)
    Assert.Equal(1,raw.Closed)

[<Fact>]
let ``lifecycle clock errors and full capture do not alter product exceptions`` () =
    let raw=Port()
    let failure=InvalidOperationException("test product exception")
    raw.Failure<-Some failure
    let tap=LifecyclePort(raw,1,nowNs=(fun ()->failwith "diagnostic clock failure"))
    let port=tap:>IFlowPort
    let caught=Assert.Throws<InvalidOperationException>(fun ()->port.Finish 0)
    Assert.Same(failure,caught)
    Assert.Equal(2L,tap.Errors)
    Assert.False(tap.Complete)
    let writeCaught=Assert.Throws<InvalidOperationException>(fun ()->port.Write(0,ReadOnlySpan [|1uy|]) |> ignore)
    Assert.Same(failure,writeCaught)
    let full=LifecyclePort(raw,1,nowNs=(fun ()->1L))
    full.Record("first",1)
    full.Record("dropped",1)
    Assert.Equal(1L,full.Dropped)
    Assert.False(full.Complete)
    raw.Failure<-None
    (full:>IFlowPort).Finish 0
    Assert.Equal(1,raw.Finished)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``real bridge socket EOF distinguishes flow Finish from direct CloseAll`` useFinish =
    let p=Tbt.Tests.SessionTests.Pair(SessionConfig.defaults,1,0.0,8)
    let bridge=Tbt.Carrier.FlowBridge ValueNone
    let tap=LifecyclePort(bridge,32,nowNs=(fun ()->1L))
    let session=Session(Role.Client,p.ClientParams,tap,p.Clock,SessionConfig.defaults)
    bridge.Attach session
    use listener=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)
    listener.Bind(System.Net.IPEndPoint(System.Net.IPAddress.Loopback,0))
    listener.Listen 1
    use app=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)
    app.ReceiveTimeout<-2000
    app.Connect(listener.LocalEndPoint |> Option.ofObj |> Option.get)
    let local=listener.Accept()
    try
        Assert.True(bridge.TryAdd local)
        let slot=bridge.Opened.NextUp -1
        Assert.Equal(1,bridge.Active)
        let prefix=[|11uy;12uy;13uy|]
        Assert.Equal(prefix.Length,(tap:>IFlowPort).Write(slot,ReadOnlySpan prefix))
        Assert.Equal(3L,tap.TotalWrittenBytes)
        if useFinish then
            (tap:>IFlowPort).Finish slot
            Assert.Equal(1,bridge.Active) // Half-close is not socket disposal.
        else
            tap.Record("carrier_close_before",bridge.Active)
            bridge.CloseAll() // No received flow FIN is required for local EOF.
            tap.Record("carrier_close_after",bridge.Active)
            Assert.Equal(0,bridge.Active)
        let received=Array.zeroCreate<byte> 3
        let mutable offset=0
        while offset<received.Length do
            let n=app.Receive(received,offset,received.Length-offset,System.Net.Sockets.SocketFlags.None)
            Assert.True(n>0)
            offset<-offset+n
        Assert.Equal<byte[]>(prefix,received)
        Assert.Equal(0,app.Receive(Array.zeroCreate<byte> 1))
        Assert.Equal(useFinish, tap.Events |> Array.exists(fun e->e.Name="finish_after"))
        Assert.DoesNotContain(tap.Events,fun e->e.Name="close_before")
    finally
        bridge.CloseAll()
        local.Dispose()
