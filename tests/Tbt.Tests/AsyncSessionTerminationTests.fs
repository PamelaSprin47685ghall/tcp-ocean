module Tbt.Tests.AsyncSessionTerminationTests

// The coordinator owns three things that outlive a round: the poller, the receive channel and the
// carrier's path set. Its exit is a handover, and a caller that joins it with a timeout cannot tell
// "still running" from "gone" — so the loop must release those resources itself and say that it did.
//
// Every test here blocks on an observable event (`Stopped`, a `BeforeWait` signal) rather than
// sleeping, and each one drives a specific way out:
//   T1 a session that fails on its own (no `Stop`) leaves through the loop's own tail
//   T2 a body exception (`BeforeWait` throws) leaves through the same tail with a fault counted
//   T3 a coordinator that never started releases its poller and channel in `Stop`
//
// The fixture keeps `carrier.Alive` true with no paths by installing `KeepAlive`: without it the
// loop exits on its first condition check (no path means not alive), which is what made an earlier
// attempt at these tests fail with "the loop never completed a round" — a fixture fault, not a
// product one.
//
// NOT covered, stated so nobody assumes otherwise:
//   · `disposeResources` throwing inside the finally (`Poller`/`RxChannel` are concrete types with
//     no injection point) — argued by inspection of the `try ... with _ -> ()` guard.
//   · `Start` racing the loop's own exit. `exited` and `running` are two words, so `Start` can pair
//     a stale `exited` with a fresh `running = false`; closing it needs a single phase word. This is
//     a known risk, documented in `AsyncSession.fs`, not a proven-safe property.

open System
open System.Threading
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Carrier
open Tbt.Async
// `SeededEntropy` and `ManualClock` are the shared test doubles, defined once in SessionTests so
// the suites cannot drift (BridgeTests opens the same module).
open Tbt.Tests.SessionTests

let private psk = Array.init 32 (fun i -> byte (i * 7 + 1))

/// A client-side session over a bridge with no local socket: enough for the coordinator to run its
/// Watch / Wait / Service cycle without any traffic.
let private clientOnBridge () =
    let config = { SessionConfig.defaults with WindowSteps = 512 }
    let entropy = SeededEntropy 11
    let unixMs = 1_000_000L
    let offer = ClientOffer(psk, Guid(Array.create 16 7uy), entropy, uint64 unixMs, 1448, 256)
    let accepted =
        (ServerOffer([| psk |], entropy, uint16 config.WindowSteps, 256))
            .TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    let struct (p, _) = offer.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    let bridge = FlowBridge ValueNone
    let session = Session(Role.Client, p, bridge, ManualClock(), config)
    bridge.Attach session
    struct (session, bridge)

/// A carrier that stays alive with no paths, so the loop keeps running instead of exiting on its
/// first `Alive` check.
let private aliveCarrierWithChannel (session: Session) (bridge: FlowBridge) (channel: RxChannel) =
    let carrier = SessionCarrier(session, bridge, rxChannel = channel)
    carrier.KeepAlive <- Some(fun () -> true)
    carrier

/// Wait for one completed round. `BeforeWait` runs on the loop thread after `Watch`, which is also
/// where the owner thread is recorded, so its signal proves the loop is running and not merely
/// requested — no sleep, no scheduler luck.
let private waitForRound (coord: AsyncSessionCoordinator) =
    use armed = new ManualResetEventSlim(false)
    coord.BeforeWait <- fun _ -> armed.Set()
    coord.Start()
    Assert.True(armed.Wait 10000, "the loop never completed a round")

[<Fact>]
let ``a session that fails on its own releases its poller and channel`` () =
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    let wakeFd = channel.WakeFd
    Assert.True(wakeFd >= 0, "the channel had no eventfd to lose")
    Assert.False(coord.Stopped.IsSet, "Stopped must not be set before the loop runs")
    waitForRound coord
    Assert.True(coord.Alive, "the loop should run while the session is alive and KeepAlive holds")
    // The session fails by itself — nobody calls Stop. The loop is blocked in `Wait`, so wake it the
    // way a frame would: that is what makes the next condition check see a dead session.
    carrier.Session.Fail "injected"
    channel.Wake()
    Assert.True(coord.Stopped.Wait 10000, "the loop never signalled Stopped")
    Assert.False(coord.Alive, "a finished loop must not keep reporting Alive")
    // `WakeFd` is a construction-time field: `Dispose` closes the descriptor but does not rewrite
    // the number, so "WakeFd < 0" would test an implementation detail that was never promised. What
    // the coordinator owes a caller is stated instead: no loop will run again, and the disposal was
    // one-shot — a second Dispose and a late Wake are both harmless no-ops.
    Assert.True(coord.Stopped.IsSet)
    (channel :> IDisposable).Dispose()
    channel.Wake()
    bridge.CloseAll()

[<Fact>]
let ``a body exception still runs the release path and counts a fault`` () =
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    // Throw from inside the round, after the owner thread is recorded and before `Wait`: this is
    // the path the body's `try ... with` catches and turns into `Session.Fail`.
    coord.BeforeWait <- fun _ -> raise (InvalidOperationException "injected round fault")
    coord.Start()
    Assert.True(coord.Stopped.Wait 10000, "the loop never signalled Stopped after a body fault")
    Assert.False(coord.Alive)
    Assert.True(coord.Stats.Faults > 0L, "the fault was not counted")
    Assert.True(coord.Stopped.IsSet, "the release path did not reach the Stopped signal")
    (channel :> IDisposable).Dispose()
    bridge.CloseAll()

[<Fact>]
let ``start is idempotent while running and refused once the loop has finished`` () =
    // The lifecycle is one word: `Start` claims idle → running with a CAS, the loop publishes
    // finished with an exchange. Two consequences are observable, and both used to be wrong:
    //   · a second `Start` while the loop lives must not create a second thread;
    //   · a `Start` after the loop left must refuse, because the poller and the channel are
    //     disposed — a new loop would fault on every round for ever.
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    waitForRound coord
    // Idempotent: no exception, still one live loop, and the observable owner does not change.
    coord.Start()
    coord.Start()
    Assert.True(coord.Alive, "a repeated Start must leave the existing loop running")
    // End it by itself, then the coordinator is one-shot.
    carrier.Session.Fail "injected"
    channel.Wake()
    Assert.True(coord.Stopped.Wait 10000, "the loop never signalled Stopped")
    Assert.False(coord.Alive)
    Assert.Throws<InvalidOperationException>(fun () -> coord.Start()) |> ignore
    // Repeated Stop stays a no-op and must not resurrect anything.
    coord.Stop()
    coord.Stop()
    Assert.True(coord.Stopped.IsSet)
    Assert.False(coord.Alive)
    bridge.CloseAll()

[<Fact>]
let ``a stop of a running loop returns after the loop wakes, not after the join timeout`` () =
    // The loop's only armed descriptor may be the receive-channel wake fd, and a stop request is
    // not a frame. Without a wake, `Stop` waits out its whole join and comes back with `Stopped`
    // unset — the assertion below fails, so this discriminates the wake from the timeout.
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    waitForRound coord
    Assert.True(coord.Alive)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    coord.Stop()
    sw.Stop()
    Assert.True(coord.Stopped.IsSet, "Stop returned with the loop claim unset")
    Assert.False(coord.Alive)
    // Generous bound: this fails only when the wake is missing (join timeout is 5 s).
    Assert.True(sw.ElapsedMilliseconds < 4000L, sprintf "Stop took %d ms" sw.ElapsedMilliseconds)
    bridge.CloseAll()

[<Fact>]
let ``start is refused once a stop has been requested, even before the loop has gone`` () =
    // `phase` stays 1 for the whole teardown (it becomes 2 only after the release), so the
    // idempotent branch `| 1 -> ()` used to answer "fine" to a Start issued in that window. A
    // caller then believes a loop is running while the only one is already leaving. The request is
    // the honest test for that window — not the phase, which is still describing the loop that
    // exists rather than the one that will not.
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    let mutable verdict = "never ran"
    let mutable first = true
    coord.BeforeWait <-
        fun _ ->
            if first then
                first <- false
                // Stop from inside the loop: it cannot join itself, so it requests and returns with
                // `phase` still 1 — exactly the window under test.
                coord.Stop()
                try
                    coord.Start()
                    verdict <- "returned"
                with :? InvalidOperationException ->
                    verdict <- "refused"
    coord.Start()
    Assert.True(coord.Stopped.Wait 10000, "the loop never signalled Stopped")
    Assert.Equal("refused", verdict)
    Assert.False(coord.Alive)
    // ... and it stays refused afterwards.
    Assert.Throws<InvalidOperationException>(fun () -> coord.Start()) |> ignore
    bridge.CloseAll()

[<Fact>]
let ``a stop from inside the loop returns while another thread is stopping`` () =
    // The gate decides *who* settles a coordinator; it is not a lock the join may depend on.
    // When `Stop` held `startGate` across `t.Join 5000`, a stop issued from one of the loop's own
    // callbacks — reached here through `BeforeWait`, the same way an embedder's `OnStats` would —
    // had to acquire the gate that the joining thread was holding while it waited for that very
    // loop. The loop then stalled for the whole join timeout, and a concurrent `Start` with it.
    //
    // The handshake below waits on real state (`CloseRequested` is raised inside `Stop`), never on
    // a sleep, so the ordering under test is the one that runs.
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    use inCallback = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    let mutable once = true
    let mutable selfStopMs = -1L
    coord.BeforeWait <-
        fun _ ->
            if once then
                once <- false
                inCallback.Set()
                release.Wait 10000 |> ignore
                let sw = System.Diagnostics.Stopwatch.StartNew()
                coord.Stop() // the loop stopping itself, while a foreign Stop is joining it
                sw.Stop()
                selfStopMs <- sw.ElapsedMilliseconds
    coord.Start()
    Assert.True(inCallback.Wait 10000, "the loop never reached its callback")
    let foreign = Thread(fun () -> coord.Stop())
    foreign.IsBackground <- true
    foreign.Start()
    // `CloseRequested` is raised inside `Stop`; observing it proves that stop is under way.
    let deadline = System.Diagnostics.Stopwatch.StartNew()
    while not carrier.CloseRequested && deadline.ElapsedMilliseconds < 5000L do
        Thread.Yield() |> ignore
    Assert.True(carrier.CloseRequested, "the foreign Stop never entered")
    release.Set()
    Assert.True(foreign.Join 20000, "the foreign Stop never returned")
    Assert.True(coord.Stopped.Wait 10000, "Stopped was never published")
    Assert.True(selfStopMs >= 0L, "the loop never reached its own Stop")
    // 5 s is the join timeout: a stop that blocks on the gate held by a joiner reports that.
    Assert.True(selfStopMs < 1000L, sprintf "the loop's own Stop blocked for %d ms" selfStopMs)
    Assert.False(coord.Alive)
    bridge.CloseAll()

[<Fact>]
let ``stopping a coordinator that never started still releases its resources`` () =
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    Assert.True(channel.WakeFd >= 0)
    coord.Stop()
    // There is no thread to wait for, so `Stopped` is a statement about "no loop will run again",
    // and the resources are released on this thread.
    Assert.True(coord.Stopped.IsSet, "stopping without a thread must not leave Stopped unset")
    Assert.False(coord.Alive)
    // Idempotent, and one-shot: a restart would run a loop on a disposed poller.
    coord.Stop()
    Assert.Throws<InvalidOperationException>(fun () -> coord.Start()) |> ignore
    bridge.CloseAll()

/// A path whose `Close` parks until the test lets it go, and counts the calls it received.
///
/// This is how the publication order becomes observable: `Stopped` claims "nothing of this
/// coordinator is in use any more", so while a path is still inside `Close`, no thread may be able
/// to observe it. The park is a real event pair, not a sleep, so the window under test is held open
/// for as long as the assertion needs.
type private ParkingClosePath(id: uint16) =
    let entered = new ManualResetEventSlim(false)
    let resume = new ManualResetEventSlim(false)
    let mutable closes = 0
    member _.Entered = entered
    member _.Resume = resume
    member _.Closes = Volatile.Read(&closes)
    interface IPath with
        member _.PathId with get () = id and set (_: uint16) = ()
        member _.FrameLength with get () = 1448 and set (_: int) = ()
        member _.Alive = true
        member _.Attach(_owner: IPathOwner) = ()
        member _.Backlog with set (_: int) = ()
        member _.Watch(_poller: Poller, _sending: bool) = ()
        member _.Receive(_poller: Poller) = ()
        member _.UnsentBytes = 0
        member _.TryBeginFrame(frame: byref<Span<byte>>) = false
        member _.CommitFrame() = ()
        member _.Close() =
            entered.Set()
            resume.Wait 20000 |> ignore
            Interlocked.Increment(&closes) |> ignore

[<Fact>]
let ``a stop with no loop publishes completion only after the release has finished`` () =
    // The never-started branch is the one that used to publish `phase = 2` before releasing:
    //   CAS(phase, 2, 0) ; carrier.Close() ; disposeResources() ; Set
    // A second `Stop` then saw 2, skipped its wait, and raised `Stopped` while the carrier still
    // held the paths' pooled slabs and the poller and channel were still open. `Stopped` is read as
    // "the teardown finished", so any caller acting on it raced the teardown it was promised.
    //
    // The park inside `Path.Close` holds that window open: with the defect, the second `Stop`
    // returns at once and `Stopped` is set; with the release-first order, it cannot return until
    // `Resume` is set, and must not have published anything before that.
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let parking = ParkingClosePath 4us
    // No async group ⇒ inline ops ⇒ the path is in the carrier's set immediately.
    carrier.AddPath parking
    Assert.Equal(1, carrier.PathCount)
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    let settler = Thread(fun () -> coord.Stop()) // never started ⇒ this Stop owns the release
    settler.IsBackground <- true
    settler.Start()
    Assert.True(parking.Entered.Wait 10000, "the release never reached the path")

    let second = Thread(fun () -> coord.Stop())
    second.IsBackground <- true
    second.Start()
    // Discriminator 1: it must not be able to come back while the release is inside the path.
    Assert.False(second.Join 300, "a second Stop returned while the release was still in progress")
    // Discriminator 2: and it must not have published `Stopped` for the same reason.
    Assert.False(coord.Stopped.IsSet, "Stopped was published before the release finished")
    Assert.False(coord.Alive)
    Assert.Equal(0, parking.Closes)

    parking.Resume.Set()
    Assert.True(settler.Join 20000, "the settling Stop never returned")
    Assert.True(second.Join 20000, "the second Stop never returned")
    Assert.True(coord.Stopped.IsSet, "Stopped was never published")
    // Exactly one close: a second one would mean two drainers each claimed the path.
    Assert.Equal(1, parking.Closes)
    Assert.Equal(0, carrier.PathCount)
    bridge.CloseAll()

[<Fact>]
let ``requesting a close is enough: the loop releases and publishes Stopped without a join`` () =
    // The server's reap removes a dead session from the loop that serves every other session, so it
    // must not join. That is only sound if the loop's own tail performs the whole release and says
    // so — this pins exactly that, with no `Stop` call anywhere.
    //
    // Discriminator: if the tail did not release on its own, `Stopped` would never be set (nothing
    // else can set it here), and the carrier's paths would keep their pooled slabs.
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    waitForRound coord
    Assert.True(coord.Alive)
    coord.RequestClose ()
    Assert.True(coord.Stopped.Wait 10000, "the loop's tail never published Stopped")
    Assert.False(coord.Alive)
    // The tail closed the carrier too: nothing can be added to it afterwards, and anything already
    // in it was closed by that same release.
    Assert.Equal(0, carrier.PathCount)
    Assert.True(carrier.CloseRequested, "the loop's tail did not close the carrier")
    bridge.CloseAll()

[<Fact>]
let ``requesting a close stays instant while the loop's tail is still releasing`` () =
    // This is the property the server's reap relies on. The reap runs inside the loop that serves
    // every other session, and a session's tail may close up to `--max-paths` sockets; if asking it
    // to stop could block, one slow teardown would stall every other session in the process. So the
    // request must return immediately, and the release must continue on the thread that owns those
    // paths.
    //
    // The park inside `Path.Close` holds the tail open for as long as the assertions need — an event
    // pair, not a sleep — so "instant" and "still releasing" are both observed rather than assumed.
    let struct (session, bridge) = clientOnBridge ()
    let channel = new RxChannel()
    let carrier = aliveCarrierWithChannel session bridge channel
    let parking = ParkingClosePath 5us
    carrier.AddPath parking // inline carrier: applied immediately
    Assert.Equal(1, carrier.PathCount)
    let coord = new AsyncSessionCoordinator(carrier, CachedClock(ManualClock()), rxChannel = channel)
    waitForRound coord
    // The loop is now blocked in `Wait`; a later round must not re-enter the test's callback.
    coord.BeforeWait <- fun _ -> ()
    let sw = System.Diagnostics.Stopwatch.StartNew()
    coord.RequestClose ()
    sw.Stop()
    // (a) the request itself must not wait for the deadline.
    Assert.True(sw.ElapsedMilliseconds < 1000L, sprintf "RequestClose took %d ms" sw.ElapsedMilliseconds)
    // (b) the tail must have started releasing, and be parked inside the path's Close.
    Assert.True(parking.Entered.Wait 10000, "the loop's tail never reached the path")
    // `Alive` is true here *correctly*: the loop thread is still running, it is just past its last
    // round and inside the release. The completion signal is `Stopped`, published after the release —
    // that is the contract a caller waits on. (The server does not reap on this member anyway; it
    // keys on `Carrier.Alive`, which is the path/grace predicate.)
    Assert.False(coord.Stopped.IsSet, "Stopped must not be published while the release is in progress")
    // (c) a second request — what a later reap pass would do — is equally instant and idle.
    let again = System.Diagnostics.Stopwatch.StartNew()
    coord.RequestClose ()
    again.Stop()
    Assert.True(again.ElapsedMilliseconds < 1000L, sprintf "a repeated RequestClose took %d ms" again.ElapsedMilliseconds)
    Assert.Equal(0, parking.Closes)
    // (d) letting the tail finish completes the handover exactly once.
    parking.Resume.Set()
    Assert.True(coord.Stopped.Wait 10000, "the loop's tail never published Stopped")
    Assert.Equal(1, parking.Closes)
    Assert.Equal(0, carrier.PathCount)
    bridge.CloseAll()
