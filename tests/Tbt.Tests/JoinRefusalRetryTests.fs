module Tbt.Tests.JoinRefusalRetryTests

// What a client does when the server's MAC'd JOIN reject never arrives (or arrives as a reset):
// it must retry with a *fresh* incarnation and give up bounded — never hang, never activate.
//
// Why this needs pinning. On the TUN carrier `tunReply` is `SendReliable`, which writes the ACK
// segment synchronously and records it in the retransmit ring; but `decideJoin` then calls
// `link.Close()` → `TunPath.Close()` → `Abort()`, which emits RST and sets `state <- Closed`.
// Every retransmit/repair path in `TunPath` is guarded by `state = Established`, so once the RST
// is out that ACK segment is never resent. On the kernel carrier the OS owns retransmission, so
// this is a TUN-only single-shot. Losing it is therefore reachable.
//
// The observable consequence is what these tests fix: not a hang and not a stale activation, but
// a consumed attempt plus a fresh-incarnation retry. `JoinRefusalRetryTests` runs the same
// `JoinAdmission` and `JoinOffer` the two ends run; it is not a socket test and does not claim
// to reproduce the loss itself.

open System
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Session.JoinBootstrap
// The shared doubles (Pair, ManualClock, SeededEntropy), defined once in SessionTests.
open Tbt.Tests.SessionTests

let private pair =
    Pair({ SessionConfig.defaults with WindowSteps = 512 }, 1, 0.0, 21)

let private clientParams = pair.ClientParams

/// A JOIN for this session's keys, as the client renders it for attempt `incarnation`.
let private offerFor (incarnation: uint32) =
    JoinOffer(clientParams.Keys.PathKey, SeededEntropy 200, clientParams.Keys.SessionId,
              clientParams.Symbol, clientParams.Caps, 8, 3us, incarnation, 1448)

/// The server's admission for the same session.
let private admission (maxPaths: int) =
    JoinAdmission(clientParams.Keys.PathKey, SeededEntropy 7, ManualClock(), clientParams.Keys.SessionId,
                  clientParams.Symbol, clientParams.Caps, maxPaths, 1448, 288)

let private decide (adm: JoinAdmission) (frame: byte[]) (closing: bool) =
    match tryJoin clientParams.Keys.PathKey (ReadOnlySpan frame) with
    | ValueSome r -> adm.Decide(ReadOnlySpan frame, r, 0, false, closing)
    | ValueNone -> failwith "the test frame must authenticate against these keys"

[<Fact>]
let ``a closing session's reject ack is never accepted by the client`` () =
    // The whole point of the MAC'd reject: the client must be able to *tell* it was refused. If
    // `TryAccept` ever returned `ValueSome` for a non-Accept status, the client would adopt a
    // session the server has already torn down — the failure mode reverse-engineering the ack
    // must not allow. This is the client-side half of `decideJoin`'s `reply; Close()`.
    let adm = admission 8
    let offer = offerFor 1u
    let d = decide adm offer.Frame true
    Assert.Equal(JoinVerdict.Reject, d.Verdict)
    Assert.True(d.Ack.Length > 0, "a reject must carry a rendered JOIN_ACK, not an empty frame")
    Assert.True((offer.TryAccept(ReadOnlySpan d.Ack)).IsNone,
                "a rejection ack must not be accepted as a join")

[<Fact>]
let ``a replayed reject is byte-identical and still not accepted`` () =
    // A retransmitted JOIN is answered by replaying the frozen ack (spec 6.5.7.4 rule 8): the ack
    // bytes must not be re-rendered, or the same nonce would seal different padding. The client
    // must treat the replay exactly as the refusal it is.
    let adm = admission 8
    let offer = offerFor 2u
    let first = decide adm offer.Frame true
    let second = decide adm offer.Frame true
    Assert.Equal(JoinVerdict.Replay, second.Verdict)
    Assert.Equal<byte[]>(first.Ack, second.Ack)
    Assert.True((offer.TryAccept(ReadOnlySpan second.Ack)).IsNone,
                "a replayed rejection must not be accepted either")

[<Fact>]
let ``a lost ack consumes every attempt, sleeping between attempts but not after the last`` () =
    // The loss case: the attempt reports no reply, so it throws, and `tryRepeated` — the loop
    // `Client.bootstrap` runs (`Client.fs:135`) — must consume exactly `attempts` tries, sleep
    // between them and not after the last, and finish with `GaveUp` carrying the last error.
    //
    // What this does NOT prove: that the client renders a *fresh* JOIN (new nonce, higher
    // incarnation) for each attempt. That lives in `Client.joinOnceAsync`, which builds a new
    // `ClientOffer` per attempt; a test that built its own frames here would only be asserting
    // about its own closure, so this one does not claim it.
    let mutable seen = ResizeArray<int>()
    let mutable sleeps = ResizeArray<int>()
    let result =
        tryRepeated 3 (fun i -> 100 * (i + 1)) sleeps.Add (fun i ->
            seen.Add i
            raise (TimeoutException "no join reply"))
    match result with
    | GaveUp msg -> Assert.Contains("no join reply", msg)
    | Joined -> Assert.Fail("every attempt timed out, so this must not report Joined")
    Assert.Equal<list<int>>([ 0; 1; 2 ], List.ofSeq seen)
    Assert.Equal<list<int>>([ 100; 200 ], List.ofSeq sleeps)   // no sleep after the last attempt

[<Fact>]
let ``the per-attempt bound for a lost ack is finite at both design points`` () =
    // ACK loss must cost a bounded wait even at the worst official profile. If this grew without
    // bound, a single lost reject would stall a path refresh indefinitely; the numbers here are
    // the contract the retry loop above relies on (bootstrapAsync cancels the attempt at this).
    for rttMs, loss in [ 400, 0.30; 20, 0.05; 20, 0.0 ] do
        let t = socketTimeoutMs rttMs loss
        Assert.InRange(t, MinTimeoutMs, MaxTimeoutMs)
        Assert.InRange(maxAttempts loss, 3, 5)
        Assert.InRange(backoffMs rttMs 0, 200, 5_000)
