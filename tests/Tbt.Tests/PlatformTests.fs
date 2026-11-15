module Tbt.Tests.PlatformTests

open System
open System.Net
open System.Net.Sockets
open Xunit
open Tbt.Carrier

[<Fact>]
let ``Linux advertises the historical fast paths, and a probe is never a silent zero`` () =
    let c = Platform.current.Value
    if OperatingSystem.IsLinux() then
        Assert.True(c.HasLibc)
        Assert.True(c.HasEpoll)
        Assert.True(c.HasPoll)
        Assert.True(c.CanAdoptTunFd)
        Assert.True(c.HasSiocoutqnsd)
        Assert.True(c.HasLinuxTcpInfo)
        Assert.True(c.HasTcpInfoNotSent)
        Assert.True(c.HasTcpNotsentLowat)
        Assert.Equal(25, c.TcpNotsentLowatOption)
        Assert.True(c.HasTcpWindowClamp)
        Assert.Contains("epoll", Platform.describe ())
        Assert.DoesNotContain("epoll=no", Platform.describe ())
        // Default probe still uses SIOCOUTQNSD, not the TCP_INFO fallback.
        use listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        listener.Bind(IPEndPoint(IPAddress.Loopback, 0))
        listener.Listen 1
        use sender = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        sender.Connect(nonNull listener.LocalEndPoint)
        use _receiver = listener.Accept()
        Assert.True(SocketTuning.probe().NotSent sender >= 0)
    else
        if not c.HasLibc then
            Assert.False(c.HasEpoll)
            Assert.False(c.HasPoll)
            Assert.False(c.CanAdoptTunFd)
            Assert.False(c.HasSiocoutqnsd)
            Assert.False(c.HasDevNetTun)
