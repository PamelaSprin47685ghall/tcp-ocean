module Tbt.Tests.ServerJoinRealTests

// The server's JOIN rule at the level the server runs it: real `SessionCarrier`, real
// `JoinAdmission`, real JOIN frames, and a path that records what was done to it.
//
// `ServerJoinTests` pins what the rule *decides* from four booleans, and `JoinTests` pins
// `JoinAdmission.Decide` itself. Neither proves the glue that the server loop actually executes:
// which list is searched first, that a retired entry is decided with `retired = true`, that a
// refusal replies a MAC'd ack and closes the path, that an accept attaches the path, and that a
// frame belonging to no session is left alone rather than answered. That glue is
// `Tbt.Cli.Server.decideJoin` / `tryJoinAnyOf` — the same functions the loop calls, not a copy.

open System
open Xunit
open Tbt.Carrier
open Tbt.Crypto
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Tests.SessionTests
open Tbt.Cli.Server

/// A path that only records. `PathId` is settable because an accept assigns the session's id.
type private RecordingPath(id: uint16) =
    let mutable pathId = id
    let replies = ResizeArray<byte[]>()
    let mutable closes = 0
    member _.Replies = replies
    member _.Closes = closes
    interface IPath with
        member _.PathId with get () = pathId and set v = pathId <- v
        member _.FrameLength with get () = 0 and set (_: int) = ()
        member _.Alive = true
        member _.Attach(_: IPathOwner) = ()
        member _.Backlog with set (_: int) = ()
        member _.Watch(_: Poller, _: bool) = ()
        member _.Receive(_: Poller) = ()
        member _.UnsentBytes = 0
        member _.TryBeginFrame(_: byref<Span<byte>>) = false
        member _.CommitFrame() = ()
        member _.Close() = closes <- closes + 1

/// One server-side session with no local socket: enough for `Alive`, capacity, path state and the
/// close transitions, with no traffic and no threads.
let private serverSession (clock: ManualClock) =
    let pair = Pair({ SessionConfig.defaults with WindowSteps = 512 }, 1, 0.0, 21)
    let p = pair.ClientParams
    let bridge = FlowBridge ValueNone
    let session = Session(Role.Server, p, bridge, clock, { SessionConfig.defaults with WindowSteps = 512 })
    bridge.Attach session
    session, p, bridge

let private targetFor (session: Session) (bridge: FlowBridge) (p: SessionParams) (maxPaths: int) =
    let carrier = SessionCarrier(session, bridge, uplinkPace = false)
    carrier.NoPathGraceNs <- 30_000_000_000L
    let admission =
        JoinAdmission(p.Keys.PathKey, SeededEntropy 7, ManualClock(), p.Keys.SessionId, p.Symbol, p.Caps,
                      maxPaths, 1448, 288)
    carrier, { Carrier = carrier; Admission = admission }

/// A JOIN for this session's keys, on `path`.
let private offer (p: SessionParams) (path: int) (incarnation: uint32) =
    JoinOffer(p.Keys.PathKey, SeededEntropy (200 + path), p.Keys.SessionId, p.Symbol, p.Caps, 4,
              uint16 path, incarnation, 1448)

let private replies (rp: RecordingPath) = rp.Replies :> seq<byte[]>
let private closedCount (rp: RecordingPath) = rp.Closes

[<Fact>]
let ``an accepted join attaches the path and replies one ack without closing it`` () =
    // The negative control for every refusal below: if this ever rejects, the fix has become worse
    // than the bug. It also pins the accept side effects — id assigned, ack replied, path attached —
    // which `JoinAdmission` alone does not cover.
    let clock = ManualClock()
    let session, p, bridge = serverSession clock
    let carrier, t = targetFor session bridge p 8
    let rp = RecordingPath 0us
    let o = offer p 3 1u
    Assert.Equal(JoinOutcome.Accepted, decideJoin (fun link f -> (link :?> RecordingPath).Replies.Add f) t rp o.Frame false)
    Assert.Equal(1, rp.Replies.Count)
    Assert.True(rp.Replies.[0].Length > 0, "an accept must carry the frozen JOIN_ACK")
    Assert.Equal(0, closedCount rp)
    Assert.True(carrier.HasPath 3us, "an accepted join must attach the path to the session")
    Assert.True(carrier.PathCount >= 1)

[<Fact>]
let ``a retired session refuses the join with a MAC'd ack and closes the path`` () =
    // The glue `ServerJoinTests` cannot reach: the retired flag reaching `Decide` as `retired=true`
    // (not merely `Alive`), plus the refusal's two side effects. A silent drop here is what makes a
    // client wait out its own handshake timeout after a path refresh races a session death.
    let clock = ManualClock()
    let session, p, bridge = serverSession clock
    let carrier, t = targetFor session bridge p 8
    let rp = RecordingPath 0us
    let o = offer p 4 1u
    Assert.Equal(JoinOutcome.Refused, decideJoin (fun link f -> (link :?> RecordingPath).Replies.Add f) t rp o.Frame true)
    Assert.Equal(1, rp.Replies.Count)
    Assert.True(rp.Replies.[0].Length > 0, "a refusal must carry a MAC'd ack, not silence")
    Assert.Equal(1, closedCount rp)
    Assert.False(carrier.HasPath 4us, "a refused path must not be attached")

[<Fact>]
let ``repeating a refused join replays the same ack bytes instead of deciding again`` () =
    // Idempotence across the glue: a retransmitted JOIN must not produce a second, different ack.
    let clock = ManualClock()
    let session, p, bridge = serverSession clock
    let carrier, t = targetFor session bridge p 8
    let o = offer p 5 1u
    let reply = fun (link: IPath) (f: byte[]) -> (link :?> RecordingPath).Replies.Add f
    let first = RecordingPath 0us
    let second = RecordingPath 0us
    Assert.Equal(JoinOutcome.Refused, decideJoin reply t first o.Frame true)
    Assert.Equal(JoinOutcome.Refused, decideJoin reply t second o.Frame true)
    Assert.Equal<byte[]>(first.Replies.[0], second.Replies.[0])
    Assert.Equal(1, second.Replies.Count)
    Assert.Equal(1, closedCount second)

[<Fact>]
let ``a session inside its path grace still accepts, and one past the grace refuses`` () =
    // `Alive` is not a boolean the rule invents: it is `SessionCarrier.Alive`, whose grace branch is
    // exactly the state a client rejoins from when every path dropped at once. A session with no
    // path but a live grace must accept; the same session past the grace must refuse rather than
    // admit a path into a session the same round removes.
    let clock = ManualClock()
    let session, p, bridge = serverSession clock
    let carrier, t = targetFor session bridge p 8
    Assert.True(carrier.Alive, "a session with no path but a live grace is alive")
    let inGrace = RecordingPath 0us
    Assert.Equal(JoinOutcome.Accepted,
                 decideJoin (fun _ _ -> ()) t inGrace (offer p 6 1u).Frame false)
    // Now a second, fresh session driven past its grace.
    let clock2 = ManualClock()
    let session2, p2, bridge2 = serverSession clock2
    let _carrier2, t2 = targetFor session2 bridge2 p2 8
    clock2.Now <- clock2.Now + 31_000_000_000L
    let pastGrace = RecordingPath 0us
    Assert.Equal(JoinOutcome.Refused,
                 decideJoin (fun link f -> (link :?> RecordingPath).Replies.Add f) t2 pastGrace (offer p2 7 1u).Frame false)
    Assert.True(pastGrace.Replies.[0].Length > 0, "an expired-grace refusal is still an ack, not silence")

[<Fact>]
let ``a join for keys no session knows is left to the next candidate`` () =
    // The last branch, and the reason `classify`'s fall-through stays silent: a frame that does not
    // authenticate is not answered here. Answering would mean inventing an ack for a session whose
    // keys we do not hold.
    let clock = ManualClock()
    let session, p, bridge = serverSession clock
    let _carrier, t = targetFor session bridge p 8
    let other = Pair({ SessionConfig.defaults with WindowSteps = 512 }, 1, 0.0, 99)
    let rp = RecordingPath 0us
    Assert.Equal(JoinOutcome.NotMine,
                 decideJoin (fun link f -> (link :?> RecordingPath).Replies.Add f) t rp
                     (offer other.ClientParams 8 1u).Frame false)
    Assert.Equal(0, rp.Replies.Count)
    Assert.Equal(0, closedCount rp)

[<Fact>]
let ``the retired list is searched only after every live session declines`` () =
    // Discrimination requires the *same* target in both lists. If the order were reversed, the
    // retired branch (`retired = true`) would refuse and close the path, so `Closes` would be 1 and
    // nothing would be attached, while the reply count stayed 1 — the return value alone cannot
    // tell the two implementations apart. With the specified order the live branch accepts first.
    let clock = ManualClock()
    let session, p, bridge = serverSession clock
    let carrier, t = targetFor session bridge p 8
    let reply = fun (link: IPath) (f: byte[]) -> (link :?> RecordingPath).Replies.Add f
    let liveFirst = RecordingPath 0us
    Assert.True(tryJoinAnyOf reply (fun _ -> t) 1 (fun _ -> t) 1 liveFirst (offer p 9 1u).Frame)
    Assert.Equal(1, liveFirst.Replies.Count)
    Assert.Equal(0, closedCount liveFirst)
    Assert.True(carrier.HasPath 9us, "the live branch accepted, so the path is attached")
    // With no live candidate the retired window answers, and refuses: an entry that left `sessions`
    // must not receive a path, but it still owes the client a MAC'd ack.
    let retiredOnly = RecordingPath 0us
    Assert.True(tryJoinAnyOf reply (fun _ -> t) 0 (fun _ -> t) 1 retiredOnly (offer p 10 1u).Frame)
    Assert.Equal(1, retiredOnly.Replies.Count)
    Assert.Equal(1, closedCount retiredOnly)
    Assert.False(carrier.HasPath 10us)
    // Neither list knows the frame: nothing is answered, and `classify` reports it.
    let stranger = Pair({ SessionConfig.defaults with WindowSteps = 512 }, 1, 0.0, 55)
    let unknown = RecordingPath 0us
    Assert.False(tryJoinAnyOf reply (fun _ -> t) 1 (fun _ -> t) 1 unknown (offer stranger.ClientParams 11 1u).Frame)
    Assert.Equal(0, unknown.Replies.Count)
    Assert.Equal(0, closedCount unknown)
