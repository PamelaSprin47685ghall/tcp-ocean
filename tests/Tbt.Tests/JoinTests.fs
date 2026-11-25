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
    /// Same decision, with `blocked` spelled out: a session that is closing, or one whose carrier
    /// is no longer alive, must reject rather than admit a path it cannot service.
    member r.DecideBlocked(o: JoinOffer, active: int, alive: bool, blocked: bool) =
        r.Admission.Decide(ReadOnlySpan o.Frame, r.Request o, active, alive, blocked)

[<Fact>]
let ``a blocked join is rejected with an authenticated ack instead of being admitted`` () =
    // The server computes `blocked` from the session it is answering for: a session whose last path
    // left (grace expired), one that faulted, or one already reaped this round. The decision must
    // then be a Reject carrying a MAC'd ack — the client can act on it — not an Accept into a
    // session that nothing services, and not silence (which leaves the client waiting out its own
    // handshake timeout).
    let rig = Rig()
    let o = rig.Offer(1, 1u)
    let d = rig.DecideBlocked(o, 0, false, true)
    Assert.Equal(JoinVerdict.Reject, d.Verdict)
    Assert.True(d.Ack.Length > 0, "a reject must still carry the authenticated ack")
    // The reject is a decision, not a refusal to look: the incarnation is recorded, so a repeat of
    // the same frame replays the same bytes instead of being decided again.
    Assert.Equal(1u, rig.Admission.Highest 1us)
    let again = rig.DecideBlocked(o, 0, false, true)
    Assert.Equal(JoinVerdict.Replay, again.Verdict)
    Assert.Equal<byte[]>(d.Ack, again.Ack)
    // A path that was already in use is not activated by a blocked decision.
    Assert.Equal(0u, rig.Admission.Active 1us)

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
let ``a caller that has not authenticated its frame gets Close instead of an exception`` () =
    // Scope, since the name of a test is a contract: this drives `JoinAdmission.Decide` directly
    // with frames the production server never passes it. `Server.decideJoin` calls `Decide` only
    // after `tryJoin` returned a request, and `tryJoin` already rejects a malformed bootstrap
    // wrapper (`ValueNone`). So no peer can reach `Decide` with an unusable frame, and this test
    // says nothing about a peer-triggered crash — the earlier note that claimed otherwise was
    // wrong and is removed.
    //
    // What it does pin is that `Decide` is total over the frames a caller may hand it: the verdict
    // for one it cannot act on is `Close` (no bytes), not an exception thrown out of a decision
    // function. That matters because the caller in production runs inside the server loop.
    //
    // It does NOT explain the one-off `ArgumentOutOfRangeException` seen at this exact statement in
    // an earlier full-suite run (frame built by `JoinOffer`, so its wrapper was well formed):
    // that event remains unexplained. Turning it into `Close` here is not a diagnosis, and the real
    // next step is to keep per-run `TestResults` logs so a recurrence keeps its stack and frame
    // length (design record §5.8a).
    let rig = Rig()
    let r = rig.Request(rig.Offer(1, 1u))
    let decideWith (frame: byte[]) = rig.Admission.Decide(ReadOnlySpan frame, r, 0, false, false)
    // Too short to be a bootstrap frame.
    let short = Array.init 32 (fun i -> byte i)
    let d = decideWith short
    Assert.Equal(JoinVerdict.Close, d.Verdict)
    Assert.Empty(d.Ack)
    // Empty.
    Assert.Equal(JoinVerdict.Close, (decideWith Array.empty<byte>).Verdict)
    // The right length with the wrong wrapper bytes.
    Assert.Equal(JoinVerdict.Close, (decideWith (Array.zeroCreate<byte> 1087)).Verdict)
    // None of those recorded a decision or moved the incarnation: a closed frame is not an event.
    Assert.Equal(0u, rig.Admission.Highest 1us)
    Assert.Equal(0u, rig.Admission.Active 1us)
    // And the function is still usable afterwards.
    Assert.Equal(JoinVerdict.Accept, rig.Decide(rig.Offer(1, 1u), 1, false).Verdict)

[<Fact>]
let ``the production filter rejects a malformed wrapper before any decision is made`` () =
    // The defence that actually stands in front of `Decide`: `tryJoin`. A frame whose 1087-byte
    // bootstrap wrapper is wrong (short, empty, or the right length with wrong bytes) is not
    // authenticated at all, so no decision — and no state change — can happen for it. This is the
    // property a peer-triggered failure would have to defeat; `Decide`'s own guard is unreachable
    // from the server for exactly this reason.
    let pair = Pair(SessionConfig.defaults, 1, 0.0, 21)
    let kPath = pair.ClientParams.Keys.PathKey
    Assert.True((tryJoin kPath (ReadOnlySpan (Array.init 32 (fun i -> byte i)))).IsNone)
    Assert.True((tryJoin kPath (ReadOnlySpan Array.empty<byte>)).IsNone)
    Assert.True((tryJoin kPath (ReadOnlySpan (Array.zeroCreate<byte> 1087))).IsNone)
    // And the genuine frame still authenticates: the filter rejects wrappers, not joins.
    let good = (JoinOffer(kPath, SeededEntropy 700, pair.ClientParams.Keys.SessionId,
                          pair.ClientParams.Symbol, pair.ClientParams.Caps, 4, 1us, 1u, 1448)).Frame
    Assert.True((tryJoin kPath (ReadOnlySpan good)).IsSome)

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
