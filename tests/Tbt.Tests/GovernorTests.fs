module Tbt.Tests.GovernorTests

open System
open Xunit
open Tbt.Crypto.Bootstrap
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Tests.SessionTests

[<Fact>]
let ``mem_tier 0 is 1024 MiB with 256 bulk paths; tier 1 is 384 MiB with 96`` () =
    Assert.Equal(0us, MemTier.ofCaps SupportedCaps)
    Assert.Equal(1024L <<< 20, MemTier.budgetBytes MemTier.Ti1024)
    Assert.Equal(256, MemTier.maxBulkPaths MemTier.Ti1024)
    Assert.Equal(32, MemTier.maxLanes MemTier.Ti1024)
    Assert.Equal(384L <<< 20, MemTier.budgetBytes MemTier.Ti384)
    Assert.Equal(96, MemTier.maxBulkPaths MemTier.Ti384)
    Assert.Equal(16, MemTier.maxLanes MemTier.Ti384)
    Assert.True(MemTier.isKnown 0us && MemTier.isKnown 1us)
    Assert.False(MemTier.isKnown 2us || MemTier.isKnown 3us)
    Assert.Equal(MemTier.Ti384, MemTier.ofCaps (MemTier.set SupportedCaps MemTier.Ti384))
    Assert.Equal(0us, MemTier.set SupportedCaps MemTier.Ti384 &&& ~~~Caps.MemTierMask &&& Caps.MemTierMask)

[<Fact>]
let ``governor watermarks only restrict future input and never clear reported custody`` () =
    let g = MemoryGovernor(1_000L)
    let held = 700L
    g.ReportTotal held
    Assert.Equal(GovernorLevel.StopIntake, g.Level)
    Assert.True(g.Order.StopIntake)
    Assert.False(g.Order.RejectNewFlows)
    Assert.Equal(held, g.Used)
    Assert.Equal(1L, g.PauseCount)
    g.ReportTotal 800L
    Assert.Equal(GovernorLevel.ShrinkWindow, g.Level)
    Assert.True(g.Order.ShrinkNewWindows && g.Order.DownlinkHalf)
    g.ReportTotal 900L
    Assert.Equal(GovernorLevel.HoleCap, g.Level)
    Assert.True(g.Order.EnableHoleCap)
    g.ReportTotal 951L
    Assert.Equal(GovernorLevel.RejectFlows, g.Level)
    Assert.True(g.Order.RejectNewFlows)
    // Custody stays until the owner reports a lower occupancy; the governor does not drop it.
    Assert.Equal(951L, g.Used)
    g.ReportTotal 699L
    Assert.Equal(GovernorLevel.Normal, g.Level)
    Assert.False(g.Order.StopIntake)
    Assert.Equal(699L, g.Used)
    Assert.Equal(1L, g.PauseCount)

[<Theory>]
[<InlineData(0L, 0)>]
[<InlineData(699L, 0)>]
[<InlineData(700L, 1)>]
[<InlineData(799L, 1)>]
[<InlineData(800L, 2)>]
[<InlineData(899L, 2)>]
[<InlineData(900L, 3)>]
[<InlineData(950L, 3)>]
[<InlineData(951L, 4)>]
[<InlineData(1000L, 4)>]
let ``governor bands follow the 70/80/90/95 percent table`` (used: int64, level: int) =
    let g = MemoryGovernor(1_000L)
    g.ReportTotal used
    Assert.Equal(enum<GovernorLevel> level, g.Level)

[<Fact>]
let ``handshake 384 mem_tier is negotiated and caps paths at 96`` () =
    let e = SeededEntropy 51
    let offer = ClientOffer(psk, Guid(Array.create 16 9uy), e, uint64 unixMs, 1448, 256, memTier = MemTier.Ti384)
    Assert.Equal(MemTier.Ti384, MemTier.ofCaps offer.Hello.Caps)
    Assert.Equal(256us, offer.Hello.MaxPaths)
    let accepted = (ServerOffer([| psk |], e, 256us, 256)).TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    Assert.True(accepted.IsSome)
    let a = accepted.Value
    Assert.Equal(MemTier.Ti384, MemTier.ofCaps a.Params.Caps)
    let struct (clientParams, sh) = offer.TryAccept(ReadOnlySpan a.Reply).Value
    Assert.Equal(MemTier.Ti384, MemTier.ofCaps clientParams.Caps)
    Assert.Equal(MemTier.Ti384, MemTier.ofCaps sh.Caps)
    Assert.Equal(96us, sh.MaxPaths)
    Assert.Equal(384L <<< 20, MemTier.budgetBytes (MemTier.ofCaps clientParams.Caps))

[<Fact>]
let ``handshake unknown mem_tier is refused`` () =
    let e = SeededEntropy 52
    Assert.Throws<ArgumentException>(fun () ->
        ClientOffer(psk, Guid.NewGuid(), e, uint64 unixMs, 1448, 256, memTier = 2us) |> ignore)
    |> ignore

[<Fact>]
let ``session at 95 percent plus rejects new flows but keeps existing ones and close sources`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 53)
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    c.Governor.ReportTotal(c.Governor.Budget + 1L)
    Assert.Equal(GovernorLevel.RejectFlows, c.Governor.Level)
    let mutable other = -1
    Assert.False(c.TryOpenFlow(&other))
    // Existing flow still sends; a close source is future-of-end, not new intake.
    let mutable app = Span<byte>.Empty
    Assert.True(c.TryBeginSourceClose(&app))
    c.CommitSource(slot, 0, true)

[<Fact>]
let ``session at 70 percent stops new bulk intake and does not drop in-flight reporting`` () =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 54)
    let c = pair.Client
    let mutable slot = -1
    Assert.True(c.TryOpenFlow(&slot))
    let payload = Array.init 2000 byte
    Assert.True(pair.Feed(c, slot, ReadOnlySpan payload, false) > 0)
    let at70 = c.Governor.Budget * 70L / 100L + 1L
    c.Governor.ReportTotal at70
    Assert.Equal(GovernorLevel.StopIntake, c.Governor.Level)
    Assert.Equal(at70, c.Governor.Used)
    let mutable app = Span<byte>.Empty
    Assert.False(c.TryBeginSource(&app))
    // Lowering the report (the owner released bytes) lifts the gate; custody was not discarded by the governor.
    c.Governor.ReportTotal 0L
    Assert.Equal(GovernorLevel.Normal, c.Governor.Level)
    Assert.True(c.TryBeginSource(&app))

[<Fact>]
let ``module reports do not double-count and pressure halves future bulk only`` () =
    let g = MemoryGovernor(10_000L)
    g.Report(GovernorModules.EncoderRing, 4000L)
    g.Report(GovernorModules.DecoderRing, 3000L)
    g.Report(GovernorModules.FlowRxBuffers, 1000L)
    Assert.Equal(8000L, g.Used)
    g.Report(GovernorModules.EncoderRing, 2000L)
    Assert.Equal(6000L, g.Used)
    g.Report(GovernorModules.EncoderRing, 4000L)
    Assert.Equal(GovernorLevel.ShrinkWindow, g.Level)
    Assert.True(g.Order.DownlinkHalf && g.Order.ShrinkNewWindows)
    g.NoteExternalRxScratch 500L
    Assert.Equal(8500L, g.Used)
    g.NoteExternalRxScratch 0L
    Assert.Equal(8000L, g.Used)
    g.Report(GovernorModules.DecoderRows, 500L)
    g.Report(GovernorModules.TxQueues, 250L)
    Assert.Equal(8750L, g.Used)
    g.Report(GovernorModules.DecoderRows, 0L)
    g.Report(GovernorModules.TxQueues, 0L)
    Assert.Equal(8000L, g.Used)
    // DownlinkHalf and EnableHoleCap are pressure flags. They have no separate byte owner
    // in RxHalf, so applying them here would invent a limit. ShrinkNewWindows is the
    // action Session consumes, and it only caps windows opened after the flag rises.

[<Fact>]
let ``delivery reserve reports rented chunks, not the credit promise`` () =
    let q = ByteQueue(1 <<< 20)
    Assert.Equal(0, q.Rented)
    q.Write(ReadOnlySpan(Array.zeroCreate<byte> 100))
    Assert.Equal(65536, q.Rented)
    Assert.Equal(100, q.Count)
    q.Clear()
    Assert.Equal(0, q.Rented)
    Assert.Equal(0, q.Count)
