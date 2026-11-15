module Tbt.Tests.ArrivalShadowTests

open System
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
open Tbt.Wire
open Tbt.Session
open Tbt.Session.ArrivalShadow
open Tbt.Tests.SessionTests

let private id = { SessionId=Guid.Parse "00000000-0000-0000-0000-000000000001"; Direction=Direction.Up }
let private evidence n = { Bytes=n; Overflow=false }
let private bound = evidence 1000000UL
let private sampler () = Sampler(id,true,100,1000000000L)
let private observe (s:Sampler) n us ns = s.Observe(evidence n,bound,us,ns)
let private sampled = function Sampled s -> s | x -> failwithf "sample expected, got %A" x

[<Fact>]
let ``first late report anchors and fresh feedback is not new delivery`` () =
    let s=sampler()
    Assert.Equal(Unknown,s.Snapshot(0L).Status)
    Assert.Equal(Baseline,observe s 100UL 1000000UL 10000000000L)
    Assert.True(s.Snapshot(10000000000L).LastSample.IsNone)
    let anchor=s.Snapshot(10000000000L).GrowthAnchor
    Assert.Equal(FeedbackOnly,observe s 100UL 2000000UL 11000000000L)
    let snap=s.Snapshot(11000000000L)
    Assert.Equal(anchor,snap.GrowthAnchor)
    Assert.Equal(FreshFeedback,snap.Status)
    Assert.True(snap.AbsoluteRemoteAgeNs.IsNone)
    Assert.True(snap.LastSample.IsNone)
    Assert.Equal(StaleFeedback,s.Snapshot(12000000001L).Status)
    Assert.Equal(anchor,s.Snapshot(12000000001L).GrowthAnchor)

[<Theory>]
[<InlineData(1)>]
[<InlineData(20)>]
[<InlineData(1000)>]
let ``compressed delayed reports cannot divide by final feedback gap`` gapNs =
    let s=sampler()
    observe s 0UL 0UL 10000000000L |> ignore
    let first=observe s 100UL 1000000UL (10000000000L+int64 gapNs) |> sampled
    let next=observe s 200UL 2000000UL (10000000000L+2L*int64 gapNs) |> sampled
    Assert.Equal(100.0,first.ObservedCodedBytesPerSecond)
    Assert.Equal(100.0,next.ObservedCodedBytesPerSecond)
    Assert.True(next.ShadowOnly)

[<Fact>]
let ``batch loss and idle blackout retain full coverage anchor`` () =
    let s=sampler()
    observe s 0UL 0UL 0L |> ignore
    for i in 1..9 do
        Assert.Equal(FeedbackOnly,observe s 0UL (uint64 i*1000000UL) (int64 i*1000000000L))
    Assert.Equal(StaleFeedback,s.Snapshot(15000000000L).Status)
    let result=observe s 1000UL 10000000UL 20000000000L |> sampled
    Assert.Equal(20000000000L,result.CoverageNs)
    Assert.Equal(50.0,result.ObservedCodedBytesPerSecond)
    Assert.Equal(0UL,result.From.Bytes)
    Assert.Equal(Some 1000000000L,s.Snapshot(21000000000L).SampleAgeNs)

[<Fact>]
let ``repeated and reordered reports neither sample nor refresh feedback`` () =
    let s=sampler()
    observe s 0UL 0UL 0L |> ignore
    observe s 200UL 2000000UL 2000000000L |> ignore
    let previous=s.Snapshot(2000000000L)
    Assert.Equal(IgnoredRepeated,observe s 200UL 2000000UL 2500000000L)
    Assert.Equal(IgnoredReordered,observe s 100UL 1000000UL 2600000000L)
    Assert.Equal(IgnoredReordered,observe s 200UL 1500000UL 2700000000L)
    let after=s.Snapshot(3100000000L)
    Assert.Equal(previous.GrowthAnchor,after.GrowthAnchor)
    Assert.Equal(previous.LastSample,after.LastSample)
    Assert.Equal(previous.LastFeedbackLocalNs,after.LastFeedbackLocalNs)
    Assert.Equal(StaleFeedback,after.Status)

[<Fact>]
let ``microsecond pending growth samples on equal highest count later timestamp`` () =
    let s=sampler()
    observe s 0UL 10UL 10000L |> ignore
    Assert.Equal(Deferred,observe s 100UL 10UL 10500L)
    Assert.Equal(100UL,s.Snapshot(10500L).HighestObservedBytes)
    Assert.True(s.Snapshot(10500L).LastSample.IsNone)
    let result=observe s 100UL 11UL 11000L |> sampled
    Assert.Equal(100UL,result.DeltaBytes)
    Assert.Equal(1000L,result.CoverageNs)

[<Fact>]
let ``timestamp regression compares most recent idle report and stays shadow invalid`` () =
    let s=sampler()
    observe s 0UL 0UL 0L |> ignore
    observe s 0UL 100UL 100000L |> ignore
    Assert.Equal(Invalidated ReceiverTimestampRegression,observe s 100UL 50UL 200000L)
    Assert.Equal(Invalidated ReceiverTimestampRegression,observe s 200UL 200UL 300000L)
    Assert.Equal(Invalid ReceiverTimestampRegression,s.Snapshot(300000L).Status)
    Assert.Equal(Some {Bytes=0UL;ReceiverNs=0L;LocalNs=0L},s.Snapshot(300000L).GrowthAnchor)
    let fresh=Sampler({id with SessionId=Guid.NewGuid()},true,100,1000000000L)
    Assert.Equal(Baseline,observe fresh 200UL 200UL 300000L)

[<Fact>]
let ``overflow and excessive timestamp are explicit sticky invalid reasons`` () =
    let s=sampler()
    Assert.Equal(Invalidated Overflow,s.Observe({Bytes=0UL;Overflow=true},bound,0UL,0L))
    Assert.Equal(Invalidated Overflow,observe s 100UL 10UL 10000L)
    let t=sampler()
    Assert.Equal(Invalidated ReceiverTimestampOutOfRange,observe t 0UL UInt64.MaxValue 0L)
    Assert.Equal(Invalidated ReceiverTimestampOutOfRange,observe t 100UL 10UL 10000L)
    let g=sampler()
    Assert.Equal(Invalidated Overflow,g.Observe(evidence 0UL,{Bytes=0UL;Overflow=true},0UL,0L))

[<Fact>]
let ``invalid evidence and local clock regression cannot alter growth or freshness`` () =
    let s=sampler()
    observe s 0UL 10UL 10000L |> ignore
    Assert.Equal(RejectedEvidence,observe s 1UL 20UL 20000L)
    Assert.Equal(RejectedEvidence,s.Observe(evidence 1000100UL,bound,30UL,30000L))
    let before=s.Snapshot(30000L)
    Assert.Equal(RejectedLocalTime,observe s 100UL 40UL -1L)
    Assert.Equal(before,s.Snapshot(30000L))
    Assert.Equal(RejectedLocalTime,observe s 100UL 40UL 29999L)
    let after=s.Snapshot(30000L)
    Assert.Equal(before,after)
    Assert.Equal(Some 10000L,after.LastFeedbackLocalNs)
    Assert.Equal(0UL,after.HighestObservedBytes)

[<Fact>]
let ``disabled sampler never creates experimental evidence`` () =
    let s=Sampler(id,false,100,1000000000L)
    Assert.Equal(Disabled,observe s 100UL 100UL 100000L)
    Assert.Equal(Unknown,s.Snapshot(100000L).Status)
    Assert.True(s.Snapshot(100000L).GrowthAnchor.IsNone)

let private membership group identity path incarnation enabled =
    { GroupId=group; Identity=identity; PathId=path; Incarnation=incarnation; Negotiated=enabled }
let private fresh identity =
    let s=Sampler(identity,true,100,1000000000L)
    observe s 0UL 0UL 0L |> ignore
    s.Snapshot(0L)

[<Fact>]
let ``coverage deduplicates session directions not paths or rate sums`` () =
    let members=[|membership 1u id 0us 1u true;membership 1u id 1us 1u true|]
    let observation=fresh id
    let result=coverage 1u members [|observation;observation|]
    Assert.Equal(2,result.PathCount)
    Assert.Equal(1,result.SessionDirectionCount)
    Assert.Single(result.Observations) |> ignore
    Assert.Empty(result.IncompleteCoverage)
    Assert.False(result.ControlEligible)
    let other={id with Direction=Direction.Down}
    let both=coverage 1u (Array.append members [|membership 1u other 0us 1u true|]) [|observation;fresh other|]
    Assert.Equal(2,both.SessionDirectionCount)
    Assert.Equal(2,both.Observations.Length)

[<Fact>]
let ``coverage reports mixed unknown conflicting stale and cross group ownership`` () =
    let unknown={id with SessionId=Guid.Empty}
    let mixed=coverage 1u [|membership 1u id 0us 1u false;membership 1u unknown 0us 1u true|] [||]
    for reason in [LegacyMember;UnknownIdentity;MissingObservation] do Assert.Contains(reason,mixed.IncompleteCoverage)
    Assert.Contains(EmptyGroup,(coverage 1u [||] [||]).IncompleteCoverage)
    let cross=coverage 1u [|membership 1u id 0us 1u true;membership 2u id 1us 1u true|] [|fresh id|]
    Assert.Contains(ConflictingMembership,cross.IncompleteCoverage)
    let snapshot=fresh id
    let conflict=coverage 1u [|membership 1u id 0us 1u true|] [|snapshot;{snapshot with HighestObservedBytes=100UL}|]
    Assert.Contains(ConflictingObservation,conflict.IncompleteCoverage)
    for status,reason in [Unknown,UnknownFeedback;StaleFeedback,StaleFeedbackEvidence;Invalid Overflow,InvalidEvidence] do
        let result=coverage 1u [|membership 1u id 0us 1u true|] [|{snapshot with Status=status}|]
        Assert.Contains(reason,result.IncompleteCoverage)

[<Fact>]
let ``scope snapshot replacement keeps new incarnation and never inherits old session`` () =
    let old=membership 1u id 0us 1u true
    let current=membership 1u id 0us 2u true
    let conflict=coverage 1u [|old;current|] [|fresh id|]
    Assert.Contains(ConflictingMembership,conflict.IncompleteCoverage)
    // Caller unloads the exact old incarnation, not all bindings with that path ID.
    let members=[|old;current|] |> Array.filter (fun m -> m<>old)
    let clean=coverage 1u members [|fresh id|]
    Assert.Equal(1,clean.PathCount)
    Assert.Empty(clean.IncompleteCoverage)
    let replacement={id with SessionId=Guid.NewGuid()}
    let missing=coverage 1u [|membership 1u replacement 0us 1u true|] [|fresh id|]
    Assert.Contains(MissingObservation,missing.IncompleteCoverage)

[<Fact>]
let ``real Session reports feed independent shadow without changing accounting or recovery`` () =
    let p=Pair(SessionConfig.defaults,1,0.0,8,arrivalClient=true,arrivalServer=true)
    let parameters=p.ClientParams
    let table=KeyTable(parameters.Keys.Suite)
    table.Install(0u,ReadOnlySpan(parameters.Keys.DownKey 0u),0UL)
    let mask : IHeaderMask =
        if parameters.Caps &&& Caps.HpMask=0us then NoHeaderMask()
        else HeaderMasks.Create(parameters.Keys.Suite,ReadOnlySpan parameters.Keys.HpDown)
    let opener=CellOpener(Direction.Down,parameters.Symbol,table,mask)
    let shadow=Sampler({SessionId=parameters.Keys.SessionId;Direction=Direction.Up},true,p.Client.FrameLength,1000000000L)
    let mutable reports=0
    let step () =
        p.Clock.Now<-p.Clock.Now+1000000L
        p.Client.Tick()
        p.Server.Tick()
        for sender,receiver,isReportDirection in [p.Client,p.Server,false;p.Server,p.Client,true] do
            let frame=Array.zeroCreate<byte> sender.FrameLength
            if sender.TryProduce(0us,Span frame,false) then
                if isReportDirection then
                    let copy=Array.copy frame
                    let mutable sequence=0UL
                    Assert.True(opener.TryOpen(Span copy,0us,&sequence))
                    let mutable meta=Unchecked.defaultof<Meta>
                    Assert.True(Meta.tryRead (ReadOnlySpan(copy,Layout.MetaOffset,Layout.MetaLength)) &meta)
                    if meta.Type=CellType.WinAck then
                        let mutable head=Unchecked.defaultof<WinAckHead>
                        let mutable report=Unchecked.defaultof<CodedArrivalEvidence>
                        Assert.True(WinAck.tryReadHeadWithArrival (ReadOnlySpan(copy,Layout.SymbolOffset,sender.SymbolSize)) &head &report)
                        shadow.Observe(report,p.Client.Tx.GeneratedCodedFrameEvidence.Value,head.RxTimeUs,p.Clock.Now) |> ignore
                        reports<-reports+1
                receiver.OnFrame(0us,Span frame) |> ignore
    let mutable slot= -1
    Assert.True(p.Client.TryOpenFlow(&slot))
    for n in [1000;2000] do
        Assert.Equal(n,p.Feed(p.Client,slot,ReadOnlySpan(Array.create n 7uy),false))
        for _ in 1..300 do step()
    Assert.Equal(3000L,p.ServerPort.Stream(slot).Length)
    Assert.True(reports>=2)
    let snapshot=shadow.Snapshot(p.Clock.Now)
    Assert.True(snapshot.LastSample.IsSome)
    Assert.Equal(p.Client.PeerAcceptedCodedFrameEvidence.Value.Bytes,snapshot.HighestObservedBytes)
    Assert.False(p.Client.PeerAcceptedCodedFrameEvidence.Value.Overflow)
    let actual=p.Client.PeerAcceptedCodedFrameEvidence
    shadow.Observe(evidence snapshot.HighestObservedBytes,p.Client.Tx.GeneratedCodedFrameEvidence.Value,UInt64.MaxValue,p.Clock.Now) |> ignore
    Assert.Equal(Invalid ReceiverTimestampOutOfRange,shadow.Snapshot(p.Clock.Now).Status)
    Assert.Equal(actual,p.Client.PeerAcceptedCodedFrameEvidence) // shadow invalidity cannot poison live accounting

[<Fact>]
let ``larger generated bound without received growth cannot manufacture a sample`` () =
    let s=sampler()
    Assert.Equal(Baseline,s.Observe(evidence 0UL,evidence 0UL,0UL,0L))
    for i in 1..100 do
        // Generated repairs are only an upper bound. They do not enter the numerator.
        Assert.Equal(FeedbackOnly,s.Observe(evidence 0UL,evidence (uint64 i*100UL),uint64 i*1000UL,int64 i*1000000L))
    let snapshot=s.Snapshot(100000000L)
    Assert.Equal(0UL,snapshot.HighestObservedBytes)
    Assert.True(snapshot.LastSample.IsNone)
    Assert.Equal(Some {Bytes=0UL;ReceiverNs=0L;LocalNs=0L},snapshot.GrowthAnchor)


[<Theory>]
[<InlineData(0UL)>]
[<InlineData(1UL)>]
[<InlineData(18446744073709551615UL)>]
let ``invalid generated bound is shadow sticky not a reordered peer report`` generated =
    let s=sampler()
    Assert.Equal(Baseline,s.Observe(evidence 100UL,evidence 100UL,0UL,0L))
    Assert.Equal(Invalidated InvalidGeneratedBound,s.Observe(evidence 0UL,evidence generated,1UL,1000L))
    Assert.Equal(Invalid InvalidGeneratedBound,s.Snapshot(1000L).Status)
    Assert.Equal(100UL,s.Snapshot(1000L).HighestObservedBytes)
    Assert.Equal(Invalidated InvalidGeneratedBound,s.Observe(evidence 200UL,evidence 200UL,2UL,2000L))

[<Fact>]
let ``bad local time cannot poison generated highwater or invalidity`` () =
    let s=sampler()
    s.Observe(evidence 100UL,evidence 100UL,10UL,10000L) |> ignore
    let before=s.Snapshot(10000L)
    Assert.Equal(RejectedLocalTime,s.Observe(evidence 0UL,evidence 0UL,20UL,9999L))
    Assert.Equal(before,s.Snapshot(10000L))
    Assert.Equal(FeedbackOnly,s.Observe(evidence 100UL,evidence 100UL,20UL,20000L))
