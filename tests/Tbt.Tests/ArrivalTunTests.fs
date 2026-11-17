module Tbt.Tests.ArrivalTunTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Session
open Tbt.Carrier
open Tbt.Tests.SessionTests
open Tbt.Tests.TunTests

/// Product TunPath TCP/pacer and SessionCarrier, with a deterministic packet-level
/// client peer. This is NOT Linux kernel TCP or a real TUN device.
[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``arrival evidence survives real TunPath packet pacing in both directions`` clientPacing =
    let p=Pair(SessionConfig.defaults,1,0.0,8,arrivalClient=true,arrivalServer=true)
    let group=RateGroup(p.Clock)
    let f=Fixture(0,p.Clock,group,40000us)
    group.Add(f.Path :> IRateMember)
    f.Open()
    let upBridge,downBridge=FlowBridge ValueNone,FlowBridge ValueNone
    upBridge.Attach p.Client
    downBridge.Attach p.Server
    let up=SessionCarrier(p.Client,upBridge,uplinkPace=clientPacing)
    let down=SessionCarrier(p.Server,downBridge)
    down.AddPath(f.Path :> IPath)
    let mutable clientSeq=f.Data0
    let mutable serverAck=1001u
    let mutable outputAt=f.Out.Packets.Count
    let mutable duplicateSegments=0
    let mutable duplicateBytes=0L
    let mutable peerStreamBytes=0L
    let buffer=Array.zeroCreate<byte> p.Client.FrameLength
    let send (payload:byte[]) =
        let struct (segment,packet)=clientSegment clientSeq serverAck Packet.Ack [||] (ReadOnlySpan payload)
        f.Path.OnSegment(segment,Span packet,p.Clock.Now)
        clientSeq<-clientSeq+uint32 payload.Length
    let mutable pathId=0us
    let mutable frameLength=p.Client.FrameLength
    let ip=
        { new IPath with
            member _.PathId with get()=pathId and set v=pathId<-v
            member _.FrameLength with get()=frameLength and set v=frameLength<-v
            member _.Alive=true
            member _.UnsentBytes=0
            member _.Attach _=()
            member _.Backlog with set (_:int)=()
            member _.Watch(_,_) = ()
            member _.Receive _=()
            member _.TryBeginFrame(frame:byref<Span<byte>>) = frame<-Span buffer;true
            member _.CommitFrame() = send buffer
            member _.Close()=() }
    up.AddPath ip
    let step () =
        p.Clock.Now<-p.Clock.Now+1_000_000L
        p.Client.Tick()
        p.Server.Tick()
        f.Path.Service p.Clock.Now
        group.Service p.Clock.Now
        up.WriteBatch 256 |> ignore
        down.WriteBatch 256 |> ignore
        while outputAt<f.Out.Packets.Count do
            let packet=f.Out.Packets.[outputAt]
            outputAt<-outputAt+1
            let segment=parse packet
            if segment.PayloadLength>0 then
                Assert.Equal(p.Client.FrameLength,segment.PayloadLength)
                // The TCP peer discards already acknowledged sequence ranges, including
                // product repair refills that arrive after the original stream bytes.
                if segment.Seq >= serverAck then
                    Assert.Equal(serverAck,segment.Seq)
                    peerStreamBytes<-peerStreamBytes+int64 segment.PayloadLength
                    serverAck<-segment.Seq+uint32 segment.PayloadLength
                    let frame=packet.AsSpan(segment.PayloadAt,segment.PayloadLength).ToArray()
                    p.Client.OnFrame(0us,Span frame) |> ignore
                else
                    Assert.True(segment.Seq+uint32 segment.PayloadLength<=serverAck)
                    duplicateSegments<-duplicateSegments+1
                    duplicateBytes<-duplicateBytes+int64 segment.PayloadLength
        // Packet peer sends cumulative TCP ACKs at 20 ms, not every controller poll.
        if p.Clock.Now % 20_000_000L=0L then send [||]
    let mutable slot= -1
    Assert.True(p.Client.TryOpenFlow(&slot))
    let feed (session:Session) (data:byte[]) = Assert.Equal(data.Length,p.Feed(session,slot,ReadOnlySpan data,false))
    feed p.Client (Array.create 8000 7uy)
    for _ in 1..1000 do step()
    Assert.Equal(8000L,p.ServerPort.Stream(slot).Length)
    feed p.Server (Array.create 2000 9uy)
    for _ in 1..1000 do step()
    Assert.Equal(2000L,p.ClientPort.Stream(slot).Length)
    let count (e:Tbt.Wire.CodedArrivalEvidence voption)=
        Assert.True(e.IsSome)
        Assert.False(e.Value.Overflow)
        e.Value.Bytes
    Assert.Equal(count p.Server.Rx.AcceptedCodedFrameEvidence,count p.Client.PeerAcceptedCodedFrameEvidence)
    Assert.Equal(count p.Client.Rx.AcceptedCodedFrameEvidence,count p.Server.PeerAcceptedCodedFrameEvidence)
    Assert.True(count p.Client.PeerAcceptedCodedFrameEvidence>count p.Server.PeerAcceptedCodedFrameEvidence)
    Assert.True(f.Path.Counters.SegmentsSent>0L)
    Assert.Equal(0L,f.Path.Counters.Faults)
    Assert.Equal(0L,f.Path.Inflight)
    Assert.True(group.Ready)
    Assert.True(count p.Client.Rx.AcceptedCodedFrameEvidence<=uint64 peerStreamBytes)
    Assert.Equal(int64 duplicateSegments*int64 p.Client.FrameLength,duplicateBytes)
    printfn "arrival TunPath packet-peer pacing=%b deliveredStreamBytes=%d discardedDuplicateSegments=%d discardedDuplicateBytes=%d acceptedCodedBytes=%d" clientPacing peerStreamBytes duplicateSegments duplicateBytes (count p.Client.Rx.AcceptedCodedFrameEvidence)
