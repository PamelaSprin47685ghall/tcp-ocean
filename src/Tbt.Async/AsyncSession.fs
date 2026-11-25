/// Session coordinator: one carrier, one poller, one thread.
///
/// The coordinator does not re-implement the receive pipeline. It runs the same
/// Watch / Wait / Tick / Service cycle that Server.run runs on its main thread,
/// so a session gets its own epoll set and its own core while the protocol
/// defaults stay untouched.
namespace Tbt.Async

open System
open System.Diagnostics
open System.Threading
open Tbt.Contracts
open Tbt.Carrier
open Tbt.Session

/// Counters for one coordinator thread.
[<Sealed>]
type AsyncSessionStats() =
    member val Cycles = 0L with get, set
    member val ServiceUs = 0L with get, set
    member val ServiceUsMax = 0L with get, set
    member val WaitUs = 0L with get, set
    member val Faults = 0L with get, set

    /// One line for the stats log: this window's cycle count, the time spent
    /// inside Service, and the longest single Service call. Reading clears the
    /// window so consecutive lines cover consecutive periods; `Faults` stays
    /// cumulative because a session that failed once must keep saying so.
    /// `ServiceUsMax` is the real latency figure: `ServiceUs` grows with the
    /// traffic served in one window, so it measures CPU occupancy, not delay.
    member this.DescribeWindow() =
        let line =
            " async_cycles=" + string this.Cycles
            + " async_svc_us=" + string this.ServiceUs
            + " async_svc_us_max=" + string this.ServiceUsMax
            + " async_wait_us=" + string this.WaitUs
            + " async_faults=" + string this.Faults
        this.Cycles <- 0L
        this.ServiceUs <- 0L
        this.ServiceUsMax <- 0L
        this.WaitUs <- 0L
        line

/// Drives one SessionCarrier on a dedicated thread with its own Poller.
[<Sealed>]
type AsyncSessionCoordinator(carrier: SessionCarrier, clock: CachedClock, ?poller: Poller,
                             ?rxChannel: RxChannel, ?ioGroup: AsyncPollerGroup) =
    let pl = defaultArg poller (new Poller())
    let channel = rxChannel
    let ioGroup = ioGroup
    // A path posted from the accept / PathPool thread has to reach this loop: the loop's only
    // armed descriptor can be the receive-channel wake fd, and a post is not a frame. Installing
    // the wake here — where the channel is owned — is what makes `AddPath` a wakeup instead of a
    // queue entry that waits for an unrelated socket event.
    do
        match channel with
        | Some rx when rx.WakeFd >= 0 -> carrier.WakePathOps <- Some(fun () -> rx.Wake())
        | _ -> ()

    /// Dispose the poller and the channel exactly once. Called by the loop on its way out, or by
    /// `Stop` when no loop can still be running.
    ///
    /// `Stop` used to dispose both unconditionally, including after a timed-out join. That is a
    /// use-after-dispose: the loop may sit in `pl.Wait` or `rx.Drain` on those very objects. A
    /// join timeout is not proof that a thread stopped, so it must not license tearing its
    /// descriptors down; the owner disposes them when it actually returns.
    let mutable resourcesDisposed = 0
    let disposeResources () =
        if System.Threading.Interlocked.Exchange(&resourcesDisposed, 1) = 0 then
            (pl :> IDisposable).Dispose()
            match channel with
            | Some rx -> (rx :> IDisposable).Dispose()
            | None -> ()
    let stats = AsyncSessionStats()
    /// Serialises `Start` against `Stop`.
    ///
    /// Both are cold paths, so a lock costs nothing, and they must not interleave: without it,
    /// `Start` could claim the thread slot after `Stop` had already joined `None` and released the
    /// poller and channel, and the new loop would then run against disposed descriptors — a fault
    /// loop reporting the same error every round.
    let startGate = obj ()
    /// The coordinator's lifecycle, as one word.
    ///
    ///   0 = idle      no loop has ever run
    ///   1 = running   a loop exists: starting, running, or finishing its release
    ///   2 = finished  exactly one loop has completed and released its resources
    ///
    /// One word, so the transitions are linearizable: `Start` claims it with `CAS(1, 0)`, and the
    /// loop releases it with `Exchange(2)` after `disposeResources`. The previous pair
    /// (`exited` + `running`) was two words, and `Start` read them in that order — the tail wrote
    /// them in the other. A `Start` that read `exited = 0`, let the tail run to completion, then
    /// read `running = false` concluded "nothing is running" and created a second thread on a
    /// poller and channel that thread had just disposed: a fault loop on dead descriptors, for
    /// ever, with `Session.Fail` on every round.
    let mutable phase = 0
    /// 1 once someone asked the loop to stop; the loop's condition reads it.
    ///
    /// Deliberately not folded into `phase`: a stop *request* must not claim `finished`, which is
    /// the meaning of 2 — "the release has completed and nothing is in use".
    let mutable stopRequested = 0
    let mutable thread: Thread option = None
    /// 1 once some `Stop` call has taken responsibility for settling this coordinator.
    ///
    /// `Stop` used to hold `startGate` across `t.Join 5000`. That made the gate a resource the
    /// *join* depended on, so a thread the loop itself ran could deadlock against it: an embedder
    /// callback (`OnStats`, `BeforeService`, `BeforeWait`) that stops its own coordinator, arriving
    /// while another thread is already in `Stop`, blocks on the gate that thread holds while it
    /// waits for the loop to exit — a five-second stall, and the same for a concurrent `Start`.
    /// Ownership of the settle is now claimed once, here, and everything that can block (the
    /// wake, the join, `carrier.Close`, `Dispose`) runs outside the gate.
    let mutable settleOwner = 0
    let mutable beforeWait: Poller -> unit = fun _ -> ()
    let mutable beforeService: Poller -> unit = fun _ -> ()
    let stoppedSignal = new ManualResetEventSlim(false)
    let mutable statsEveryMs = 0L
    let mutable onStats: unit -> unit = ignore
    let mutable lastStatsTick = 0L

    let toUs (ticks: int64) = ticks * 1_000_000L / Stopwatch.Frequency

    /// The round loop proper.
    ///
    /// Note where the `try` is: it is inside the body, so it catches `Watch`/`Wait`/`Service`.
    /// It cannot catch the condition `Volatile.Read(&stopRequested) = 0 && carrier.Alive`, and
    /// that condition is not side-effect free — `carrier.Alive` reads the published path-id set,
    /// the pending op queue and the session's clock. If it throws (or if `Service` rethrows past
    /// the catch, or the thread is interrupted), the exception used to leave `loop` with nothing
    /// released: no path slabs returned, no poller or channel eventfd disposed, `Stopped` never
    /// set, and an unhandled exception on a background thread that .NET turns into process
    /// termination. `loop` below is the only entry point and wraps this in `try/finally`, so every
    /// way out runs the release.
    let loopBody () =
        while Volatile.Read(&stopRequested) = 0 && carrier.Alive do
            try
                let waitStart = Stopwatch.GetTimestamp()
                carrier.Watch pl
                beforeWait pl
                match channel with
                | Some rx when rx.WakeFd >= 0 -> pl.WatchFd rx.WakeFd
                | _ -> ()
                // WaitUs names the one event this thread is waiting for: descriptor
                // readiness, a path drain that frees the write gate, a frame from the
                // IO pool, or the next protocol timer. -1 blocks until one arrives.
                // A fixed cap here was a poll: 22 rounds per second at idle, and a
                // 50 ms ceiling on every wait even when data was ready.
                let waitUs = carrier.WaitUs()
                // The stats line is the one deadline this thread owns, so an
                // otherwise unlimited wait must still end there; that is a timer,
                // not a poll interval.
                let waitUs =
                    if statsEveryMs <= 0L then waitUs
                    else
                        let statsUs = max 1L ((lastStatsTick + statsEveryMs - Environment.TickCount64) * 1000L)
                        if waitUs < 0 then int statsUs else int (min (int64 waitUs) statsUs)
                pl.Wait(waitUs)
                stats.WaitUs <- stats.WaitUs + toUs (Stopwatch.GetTimestamp() - waitStart)
                match channel with
                | Some rx when rx.WakeFd >= 0 && pl.IsFdReadable rx.WakeFd -> rx.ClearWake ()
                | _ -> ()
                match channel with
                | Some rx ->
                    // Bounded: an unbounded drain is not a queue walk, it is a livelock. At
                    // 100 Mbit/s the IO threads refill faster than frames are decoded, the
                    // queue never empties, and the coordinator cannot send WIN_ACK, FLOW_STATE
                    // or a stats line until ingress pauses — measured as 9.4 s and 12.5 s
                    // holes on the live 24 %-loss path, with the peer's credit frozen. A
                    // budget of 256 frames keeps control frames flowing every few
                    // milliseconds; re-arming the wake immediately resumes the rest.
                    rx.Drain(carrier, 256) |> ignore
                    if rx.HasItems then rx.Wake ()
                | None -> ()
                clock.Tick() |> ignore
                beforeService pl
                let serviceStart = Stopwatch.GetTimestamp()
                carrier.Service pl
                let us = toUs (Stopwatch.GetTimestamp() - serviceStart)
                stats.Cycles <- stats.Cycles + 1L
                stats.ServiceUs <- stats.ServiceUs + us
                if us > stats.ServiceUsMax then stats.ServiceUsMax <- us
                // Stats that walk the carrier's collections must run here, on the
                // thread that owns them. A watcher thread calling Describe() while
                // this thread joins or drops a path made the enumerator throw
                // InvalidOperation_EnumFailedVersion.
                if statsEveryMs > 0L then
                    let tick = Environment.TickCount64
                    if tick - lastStatsTick >= statsEveryMs then
                        lastStatsTick <- tick
                        try onStats () with _ -> ()
            with ex ->
                // One session fails alone; the server keeps its other sessions.
                stats.Faults <- stats.Faults + 1L
                carrier.Session.Fail(ex.GetType().Name + ": " + ex.Message)
    /// The one exit path for the session thread.
    ///
    /// Three rules, and each one answers a way the old tail leaked:
    ///
    ///   · `loopBody` is called inside `try`, so the loop *condition* is covered too. Catching
    ///     only inside the body left `carrier.Alive` throwing straight out of `loop`.
    ///   · the carrier is closed on EVERY exit, not only when someone asked. A session that
    ///     failed by itself leaves its paths holding pooled slabs, and this thread is the only
    ///     one allowed to release them — there is no later opportunity. That also removes the
    ///     lost-close window: `Stop` may time out its join and ask *after* any check performed
    ///     here, so a check-then-close tail can always miss that request.
    ///   · every step of the release is individually guarded and the whole tail is a `finally`,
    ///     so a throwing close or `Dispose` cannot skip `phase`, the resource release or the
    ///     `Stopped` signal.
    ///
    /// Order inside the `finally` is load-bearing: the release runs before `phase` becomes 2, so
    /// 2 means "nothing of this coordinator is in use any more" — a restart must never run a loop
    /// on a poller that is about to be disposed. `disposeResources` is idempotent, so `Stop`
    /// racing this tail is harmless.
    ///
    /// The transition to 2 is a single `Exchange`, which is what makes the lifecycle
    /// linearizable: `Start` claims the same word with `CAS(0 → 1)`, so the two can never both
    /// win and no `Start` can observe "not finished, not running" while this tail is between two
    /// stores. (The earlier `exited` + `running` pair could, and then started a second loop on
    /// disposed descriptors.)
    let loop () =
        Thread.CurrentThread.Name <- "AsyncSession"
        try
            try
                loopBody ()
            with ex ->
                // An exception escaping the *condition* would otherwise leave this background
                // thread unhandled, and .NET terminates the process for that — one session's fault
                // would take down the daemon and every other session with it. The session's own
                // error channel already exists (`Session.Fail`), so report it there: the fault stays
                // visible to the operator, the peer and the tests, without a process kill.
                stats.Faults <- stats.Faults + 1L
                (try carrier.Session.Fail("loop: " + ex.GetType().Name + ": " + ex.Message)
                 with _ -> ())
        finally
            (try carrier.Close() with ex ->
                // A failed close must not kill the release below, and the session must still say
                // so: the paths it did not close hold pooled buffers.
                stats.Faults <- stats.Faults + 1L
                (try carrier.Session.Fail("close: " + ex.GetType().Name + ": " + ex.Message)
                 with _ -> ()))
            // The server's main poller may be blocked with no descriptor of its own to
            // watch for this exit; wake it so the dead session is reaped at once.
            (try ioGroup |> Option.iter (fun g -> g.Wake()) with _ -> ())
            // From here this thread no longer touches the carrier's path set, so an observer that
            // sees `ownerExited` may close it (returning pooled buffers) without racing a frame.
            (try carrier.NoteOwnerExit() with _ -> ())
            // The release is complete. This single exchange is the loop's linearization point and
            // the word a concurrent `Start` competes with; publishing it after `disposeResources`
            // makes 2 mean "nothing of this coordinator is in use any more", so `Stopped` can be
            // trusted as a memory barrier as well as a liveness fact.
            (try disposeResources () with _ -> ())
            System.Threading.Interlocked.Exchange(&phase, 2) |> ignore
            stoppedSignal.Set()

    member _.Poller = pl
    member _.Stats = stats
    member _.Alive = Volatile.Read(&phase) = 1
    /// Set once the session loop has exited; lets a caller block instead of spin.
    member _.Stopped = stoppedSignal

    /// Emit a stats line every N ms, on the session thread.
    member _.StatsEveryMs
        with get () = statsEveryMs
        and set (value: int64) = statsEveryMs <- value

    member _.OnStats
        with get () = onStats
        and set (value: unit -> unit) = onStats <- value

    /// Runs on the coordinator thread after Watch and before Wait. A descriptor
    /// registered here takes part in the wait that is about to start; a watch
    /// call after Wait would clear the results Wait just filled.
    member _.BeforeWait
        with get () = beforeWait
        and set (value: Poller -> unit) = beforeWait <- value

    /// Runs on the coordinator thread after Wait and before Service. The client
    /// watches its local listener and attaches joined paths here, on the same
    /// thread that owns the carrier.
    member _.BeforeService
        with get () = beforeService
        and set (value: Poller -> unit) = beforeService <- value

    /// Start the session thread. Idempotent while the loop exists; one-shot for the coordinator.
    ///
    /// The claim is a single `CAS(idle → running)`, which is the only thing that licenses
    /// creating a thread. It competes with the loop's own `Exchange(finished)`, so exactly one
    /// of the two can win a given transition:
    ///
    ///   · `Start` wins (0 → 1): the thread it creates is the only loop.
    ///   · the loop wins (1 → 2): the coordinator has released its poller and channel, and this
    ///     `Start` sees 2 and refuses. It can never create a loop on a disposed descriptor.
    ///   · while the loop lives, phase stays 1, so a concurrent `Start` sees 1 and does nothing —
    ///     no second thread, and no "restart" of a loop that is already running.
    member _.Start() =
        lock startGate (fun () ->
            // A stop request means the loop is leaving. `phase` is still 1 during that window
            // (it becomes 2 only after the release), so the `| 1 -> ()` branch below would
            // return normally — a caller would conclude "a loop is running" about one that is
            // already on its way out. Returning here is only honest while no stop has been
            // asked for.
            if Volatile.Read(&stopRequested) <> 0 then
                invalidOp "a stopping coordinator cannot be started"
            match System.Threading.Interlocked.CompareExchange(&phase, 1, 0) with
            | 0 ->
                try
                    let t = Thread(ThreadStart(loop))
                    t.IsBackground <- true
                    t.Name <- "AsyncSession"
                    t.Start()
                    thread <- Some t
                with _ ->
                    // No thread exists, so nothing else will ever publish the completion. Publish
                    // the same one a loop would: release what the coordinator owns, raise the
                    // signal, and only then rethrow. Setting `phase = 2` alone would claim
                    // "finished and released" (invariant I2) while the carrier's paths still hold
                    // pooled slabs and the poller and channel descriptors are still open, and it
                    // would leave `Stopped` false for a coordinator that can never run again.
                    (try carrier.Close () with _ -> ())
                    (try disposeResources () with _ -> ())
                    System.Threading.Interlocked.Exchange(&phase, 2) |> ignore
                    stoppedSignal.Set()
                    reraise ()
            | 1 -> ()
            | _ -> invalidOp "a finished coordinator cannot be restarted")

    /// Stop the loop and release the carrier. Idempotent.
    ///
    /// The stop is a *request* plus a bounded wait, never a claim: `phase` moves to 2 only when
    /// the loop itself says so (its tail) or when this call proves no loop exists. Two
    /// consequences, both deliberate:
    ///
    ///   · `Stopped` is raised only when "no loop will run from here on" is provable — a join
    ///     success, no thread at all, or the loop's own publication. A timed-out join no longer
    ///     reports an exit that did not happen.
    ///   · nothing here touches the poller or the channel unless the loop is provably gone. A join
    ///     timeout is not evidence that the thread stopped, and disposing a descriptor that thread
    ///     is blocked on would turn a slow stop into a use-after-dispose.
    ///
    /// The loop is woken first. Its only armed descriptor may be the receive-channel wake fd, and
    /// a stop is not a frame, so without the wake it would sit in `epoll_wait` until the join
    /// expired — five seconds of waiting for something the wake delivers at once.
    ///
    /// Nothing that can block runs while `startGate` is held. The gate decides *who* settles and
    /// nothing else; the wake, the join, `carrier.Close` and `Dispose` all run after it is
    /// released. Holding it across the join is what made the gate part of what the loop had to
    /// acquire to exit, so a `Stop` or `Start` issued from one of the loop's own callbacks
    /// (`OnStats`, `BeforeService`, `BeforeWait`) deadlocked against the joining thread for the
    /// whole join timeout. And a thread that *is* the loop never waits for the loop: it returns,
    /// and the tail it is running publishes `Stopped`.
    member _.Stop() =
        // Phase 1: claim, under the gate, only what has to be atomic — and nothing that can block.
        let mutable settle = false
        let mutable captured: Thread option = None
        lock startGate (fun () ->
            // Only the request and the capture must be atomic together: the request wakes the loop,
            // and the capture names the thread whose exit the settle waits for. (`Stop` is
            // idempotent, so whether this call was the first to ask changes nothing.)
            Interlocked.Exchange(&stopRequested, 1) |> ignore
            captured <- thread
            settle <- Interlocked.CompareExchange(&settleOwner, 1, 0) = 0)
        let selfLoop =
            match captured with
            | Some t -> obj.ReferenceEquals(t, Thread.CurrentThread)
            | None -> false
        // Another thread is already settling: its join is the thing that ends the loop, so waiting
        // for the publication is right for a caller — but never for the loop itself, which is what
        // that join is waiting on. (`Stop` from inside the loop returns; the tail finishes.)
        if not settle && not selfLoop && Volatile.Read(&phase) <> 2 then
            stoppedSignal.Wait 6000 |> ignore
        if settle then
            (try carrier.RequestClose () with _ -> ())
            match captured with
            | None ->
                // No loop was ever created, so this call owns the release.
                //
                // Order is the contract, and this branch used to get it backwards: it published
                // `phase = 2` (via CAS) *before* `carrier.Close` and `disposeResources`. But 2 is
                // read as "nothing of this coordinator is in use any more" — that is what the
                // loop's tail publishes it to mean, and what `Stopped` is trusted for. Publishing
                // it first lets a concurrent `Stop` take the `phase = 2` shortcut, skip its own
                // wait and raise `Stopped` while the carrier still holds the paths' pooled slabs
                // and the poller and the channel eventfd are still open: a caller that acts on
                // `Stopped` would then race the very teardown it was promised had finished.
                //
                // So: release first, publish afterwards — the same order as the loop's tail and as
                // the `Start`-thread-failure branch, so `phase = 2` means the same thing on every
                // path.
                (try carrier.Close () with _ -> ())
                (try disposeResources () with _ -> ())
                // Only now does the coordinator become one-shot for `Start`. (`stopRequested`
                // already refuses a restart; this keeps `phase` describing reality as well.)
                Interlocked.Exchange(&phase, 2) |> ignore
                stoppedSignal.Set()
            | Some t when obj.ReferenceEquals(t, Thread.CurrentThread) ->
                // The loop stopped itself. Its tail releases everything and publishes `Stopped`;
                // joining itself would be a deadlock, and the caller is that thread.
                ()
            | Some t ->
                // Outside the gate: the join must not be able to hold a lock the loop, or a
                // concurrent `Start`, needs.
                let joined = t.Join 5000
                if joined then
                    // Proven gone: this thread may finish the release. Both steps are idempotent,
                    // so the loop's own tail may also have run them.
                    (try carrier.NoteOwnerExit () with _ -> ())
                    (try carrier.Close () with _ -> ())
                    (try disposeResources () with _ -> ())
                    Interlocked.Exchange(&phase, 2) |> ignore
                    stoppedSignal.Set()
                elif Volatile.Read(&phase) = 2 then stoppedSignal.Set()
        elif Volatile.Read(&phase) = 2 then stoppedSignal.Set()

    /// Ask the loop to stop, and return at once.
    ///
    /// `Stop` joins the loop. A caller that runs a service loop of its own — the server's reap runs
    /// once per dead session, inside the loop that also serves every other session — therefore
    /// blocks for as long as that session's tail takes (closing every path socket, then the local
    /// bridge). The tail releases everything on *every* exit, so the join buys nothing such a
    /// caller cannot observe another way: this raises the same request `Stop` raises first, and the
    /// loop's own tail performs the release and publishes `Stopped`. A caller that needs completion
    /// waits on `Stopped`, which never blocks the thread that owns the other sessions.
    member _.RequestClose() =
        Interlocked.Exchange(&stopRequested, 1) |> ignore
        (try carrier.RequestClose () with _ -> ())

    interface IDisposable with
        member this.Dispose() = this.Stop()

/// Create a coordinator for an existing carrier.
module AsyncSessionCoordinator =
    let create (carrier: SessionCarrier) (clock: CachedClock) =
        new AsyncSessionCoordinator(carrier, clock)

    /// A coordinator that also drains frames read by the shared IO pool.
    let createWith (carrier: SessionCarrier) (clock: CachedClock) (group: AsyncPollerGroup) (channel: RxChannel) =
        new AsyncSessionCoordinator(carrier, clock, ?rxChannel = Some channel, ?ioGroup = Some group)
