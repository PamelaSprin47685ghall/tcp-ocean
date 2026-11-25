module Tbt.Tests.ServerJoinTests

// The server's JOIN decision, tested through the production rule rather than a copy of it:
// `Tbt.Cli.joinMustReject` is what `tryJoinEntry` calls, and `retireExpired` is what the
// retirement sweep calls. A test that re-implemented either would pass while the server drifted.
//
// `alive` is not a boolean the rule invents — it is `SessionCarrier.Alive`, whose grace branch is
// the whole reason a client can rejoin a session that has lost every path at once (a burst of
// resets, a NAT rebind, `ss -K`). So the cases below build a real carrier with a real session and
// a real grace, and let the rule read the value the server would read.

open System
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Cli.Server
open Tbt.Tests.SessionTests

let private psk = Array.init 32 (fun i -> byte (i * 11 + 3))

/// A real client-side session over a bridge with no local socket: enough for `Alive` and the
/// close/fault transitions, with no traffic and no threads.
let private sessionWithClock (clock: ManualClock) =
    let config = { SessionConfig.defaults with WindowSteps = 512 }
    let entropy = SeededEntropy 21
    let unixMs = 1_000_000L
    let offer = ClientOffer(psk, Guid(Array.create 16 3uy), entropy, uint64 unixMs, 1448, 256)
    let accepted =
        (ServerOffer([| psk |], entropy, uint16 config.WindowSteps, 256))
            .TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    let struct (p, _) = offer.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    let bridge = FlowBridge ValueNone
    let session = Session(Role.Client, p, bridge, clock, config)
    bridge.Attach session
    session

[<Fact>]
let ``a session inside its path grace is still joinable`` () =
    // The state this protects: every path died at once, the grace is running, and the client's next
    // JOIN must be admitted into the same session. If the rule keyed on "no paths" instead of on
    // `Alive`, this reconnect would be refused and the client would crawl to "no path remains".
    let clock = ManualClock()
    let session = sessionWithClock clock
    let bridge = FlowBridge ValueNone
    let carrier = SessionCarrier(session, bridge, uplinkPace = false)
    carrier.NoPathGraceNs <- 30_000_000_000L // --no-path-grace-s 30, the server default
    Assert.True(carrier.Alive, "a session with no path but a live grace must be alive")
    Assert.False(joinMustReject false CloseState.Open false carrier.Alive,
                 "a JOIN during the path grace must be admitted")
    // A path in PATH_CLOSE is a different matter: the same frame must not be re-admitted.
    Assert.True(joinMustReject false CloseState.Open true carrier.Alive)
[<Fact>]
let ``a session whose grace has run out is not joinable`` () =
    // The defect this pins: the old rule had no `alive` term, so an expired-grace session looked
    // like a healthy empty one (`CloseState = Open`, capacity free) and the JOIN was ACCEPTED into a
    // session the same round then removed and closed — a positive ACK for a path nothing services.
    let clock = ManualClock()
    let session = sessionWithClock clock
    let bridge = FlowBridge ValueNone
    let carrier = SessionCarrier(session, bridge, uplinkPace = false)
    carrier.NoPathGraceNs <- 30_000_000_000L
    Assert.True(carrier.Alive)
    clock.Now <- clock.Now + 31_000_000_000L // one second past the grace
    Assert.False(carrier.Alive, "the grace expired, so the session is no longer serviceable")
    Assert.True(joinMustReject false CloseState.Open false carrier.Alive,
                "an expired-grace session must reject, not admit a path into a closing session")

[<Fact>]
let ``a retired session rejects even if its carrier still reads alive`` () =
    // The retired flag exists so the answer is a function of the argument. A carrier can still read
    // alive for a moment after the entry left `sessions`; accepting then would attach a path to a
    // session that no longer appears in the server's list, so nothing would ever service or close it.
    Assert.True(joinMustReject true CloseState.Open false true)
    // ... and the other three refusals still stand on their own.
    Assert.True(joinMustReject false CloseState.Closing false true)
    Assert.True(joinMustReject false CloseState.Closed false true)

[<Fact>]
let ``a faulted session is refused`` () =
    let clock = ManualClock()
    let session = sessionWithClock clock
    let bridge = FlowBridge ValueNone
    let carrier = SessionCarrier(session, bridge, uplinkPace = false)
    carrier.NoPathGraceNs <- 30_000_000_000L
    Assert.True(carrier.Alive)
    session.Fail "injected"
    Assert.False(carrier.Alive, "a faulted session is not serviceable")
    Assert.True(joinMustReject false CloseState.Open false carrier.Alive)

[<Fact>]
let ``a healthy session with a path accepts`` () =
    // The negative control: the rule must not refuse the ordinary case, or every reconnect would
    // fail and the fix would be worse than the bug.
    Assert.False(joinMustReject false CloseState.Open false true)

[<Fact>]
let ``a retired entry leaves as soon as its own loop reports the release`` () =
    // The addressability window is not a new deadline: a retired entry stops being addressable the
    // moment the session's tail publishes `Stopped`, because after that its keys describe a session
    // whose paths and poller are gone. The deadline is only the backstop for a tail that never ends.
    let reapedAt = 1_000_000L
    Assert.True(retireExpired true reapedAt reapedAt, "a released session must leave at once")
    Assert.False(retireExpired false (reapedAt + 4_999L) reapedAt,
                 "a tail still running keeps the entry addressable inside the window")
    Assert.True(retireExpired false (reapedAt + retireWindowMs + 1L) reapedAt,
                "a tail that never ends must not pin the entry for ever")
