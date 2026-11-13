module Tbt.Tests.JoinTests

open System
open Xunit
open Tbt.Crypto
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Tests.SessionTests

// Server admission of path joins (spec 6.5.7.4).

type private Rig() =
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 21)
    let p = pair.ClientParams
    let clock = ManualClock()
    let mutable seed = 1000
    member val Admission =
        JoinAdmission(p.Keys.PathKey, SeededEntropy 5, clock, p.Keys.SessionId, p.Symbol, p.Caps, 4, 1448, 288)
    member _.Clock = clock
    /// A client JOIN (fresh nonce unless seedOf repeats one).
    member _.Offer(path: int, incarnation: uint32) =
        seed <- seed + 1
        JoinOffer(p.Keys.PathKey, SeededEntropy seed, p.Keys.SessionId, p.Symbol, p.Caps, 4, uint16 path, incarnation, 1448)
    member _.OfferSeeded(seedOf: int, path: int, incarnation: uint32) =
        JoinOffer(p.Keys.PathKey, SeededEntropy seedOf, p.Keys.SessionId, p.Symbol, p.Caps, 4, uint16 path, incarnation, 1448)
    member _.Request(o: JoinOffer) = (tryJoin p.Keys.PathKey (ReadOnlySpan o.Frame)).Value
    member r.Decide(o: JoinOffer, active: int, alive: bool) =
        r.Admission.Decide(ReadOnlySpan o.Frame, r.Request o, active, alive, false)

[<Fact>]
let ``a join is decided once: its repeat replays the same frozen JOIN_ACK bytes and never activates`` () =
    let rig = Rig()
    let o = rig.Offer(1, 1u)
    let d = rig.Decide(o, 1, false)
    Assert.Equal(JoinVerdict.Accept, d.Verdict)
    Assert.True((o.TryAccept(ReadOnlySpan d.Ack)).IsSome)
    Assert.Equal(1u, rig.Admission.Highest 1us)
    Assert.Equal(1u, rig.Admission.Active 1us)
    for _ in 1 .. 3 do
        let again = rig.Decide(o, 2, true)
        Assert.Equal(JoinVerdict.Replay, again.Verdict)
        // Byte for byte: no new padding under the same key and zero nonce.
        Assert.Equal<byte[]>(d.Ack, again.Ack)

[<Fact>]
let ``an old or equal incarnation and a reused nonce with another transcript are closed without an ACK`` () =
    let rig = Rig()
    let a = rig.OfferSeeded(7, 1, 3u)
    Assert.Equal(JoinVerdict.Accept, rig.Decide(a, 1, false).Verdict)
    // Same nonce (same seed draws it first), other transcript.
    let b = rig.OfferSeeded(7, 1, 4u)
    Assert.NotEqual<byte[]>(a.Frame, b.Frame)
    let db = rig.Decide(b, 2, true)
    Assert.Equal(JoinVerdict.Close, db.Verdict)
    Assert.Empty(db.Ack)
    Assert.Equal(JoinVerdict.Close, rig.Decide(rig.Offer(1, 3u), 2, true).Verdict)
    Assert.Equal(JoinVerdict.Close, rig.Decide(rig.Offer(1, 2u), 2, true).Verdict)
    Assert.Equal(3u, rig.Admission.Highest 1us)
    // A higher incarnation with a new nonce replaces the path.
    let c = rig.Offer(1, 5u)
    Assert.Equal(JoinVerdict.Accept, rig.Decide(c, 2, true).Verdict)
    Assert.Equal(5u, rig.Admission.Active 1us)

[<Fact>]
let ``a refusal consumes the incarnation and keeps the active one; its repeat stays a refusal`` () =
    let rig = Rig()
    Assert.Equal(JoinVerdict.Accept, rig.Decide(rig.Offer(2, 1u), 1, false).Verdict)
    // Session full (4 paths): CAPACITY for a new path id.
    let o = rig.Offer(3, 1u)
    let d = rig.Decide(o, 4, false)
    Assert.Equal(JoinVerdict.Reject, d.Verdict)
    let ack = (o.TryOpen(ReadOnlySpan d.Ack)).Value
    Assert.Equal(Join.JoinStatus.Capacity, ack.Status)
    Assert.True((o.TryAccept(ReadOnlySpan d.Ack)).IsNone)
    Assert.Equal(1u, rig.Admission.Highest 3us)
    Assert.Equal(0u, rig.Admission.Active 3us)
    // Room again: the same request is not rewritten to ACCEPT.
    let again = rig.Decide(o, 1, false)
    Assert.Equal(JoinVerdict.Replay, again.Verdict)
    Assert.Equal<byte[]>(d.Ack, again.Ack)
    // Replacing a live path does not need a free slot; a refused replacement keeps the old.
    Assert.Equal(JoinVerdict.Accept, rig.Decide(rig.Offer(2, 2u), 4, true).Verdict)
    let lane = rig.Offer(300, 1u)
    Assert.Equal(JoinVerdict.Close, rig.Decide(lane, 1, false).Verdict)

[<Fact>]
let ``a full decision cache refuses new joins without forgetting, and frees only digests whose ACK expired`` () =
    let rig = Rig()
    let mutable accepted = 0
    // 288 frozen ACKs within their time: the next new join is refused, no state change.
    for i in 1 .. 288 do
        let d = rig.Decide(rig.Offer(1, uint32 i), 1, true)
        if d.Verdict = JoinVerdict.Accept then accepted <- accepted + 1
    Assert.Equal(288, accepted)
    Assert.Equal(288, rig.Admission.FrozenCount)
    let next = rig.Offer(1, 289u)
    Assert.Equal(JoinVerdict.Close, rig.Decide(next, 1, true).Verdict)
    Assert.Equal(1L, rig.Admission.Refused)
    Assert.Equal(288u, rig.Admission.Highest 1us)
    // After the frozen time the bytes go, the digests stay: the same join is closed, not re-rendered.
    rig.Clock.Now <- rig.Clock.Now + JoinAdmission.FrozenNs
    Assert.Equal(JoinVerdict.Accept, rig.Decide(next, 1, true).Verdict)
    Assert.Equal(1, rig.Admission.FrozenCount)
    // Many more decisions over time: digests stay bounded at 1024, old incarnations stay closed.
    for i in 290 .. 2000 do
        rig.Clock.Now <- rig.Clock.Now + JoinAdmission.FrozenNs
        Assert.Equal(JoinVerdict.Accept, rig.Decide(rig.Offer(1, uint32 i), 1, true).Verdict)
    Assert.True(rig.Admission.Records <= 1024)
    Assert.Equal(JoinVerdict.Close, rig.Decide(rig.Offer(1, 1500u), 1, true).Verdict)
