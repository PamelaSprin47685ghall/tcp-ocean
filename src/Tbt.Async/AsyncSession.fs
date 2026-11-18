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

/// Drives one SessionCarrier on a dedicated thread with its own Poller.
[<Sealed>]
type AsyncSessionCoordinator(carrier: SessionCarrier, clock: CachedClock, ?poller: Poller,
                             ?rxChannel: RxChannel, ?ioGroup: AsyncPollerGroup) =
    let pl = defaultArg poller (new Poller())
    let channel = rxChannel
    let ioGroup = ioGroup
    let stats = AsyncSessionStats()
    let mutable running = false
    let mutable thread: Thread option = None
    let mutable stopped = 0
    let mutable beforeWait: Poller -> unit = fun _ -> ()
    let mutable beforeService: Poller -> unit = fun _ -> ()
    let stoppedSignal = new ManualResetEventSlim(false)
    let mutable statsEveryMs = 0L
    let mutable onStats: unit -> unit = ignore
    let mutable lastStatsTick = 0L

    let toUs (ticks: int64) = ticks * 1_000_000L / Stopwatch.Frequency

    let loop () =
        Thread.CurrentThread.Name <- "AsyncSession"
        while Volatile.Read(&running) do
            try
                let waitStart = Stopwatch.GetTimestamp()
                carrier.Watch pl
                beforeWait pl
                match channel with
                | Some rx when rx.WakeFd >= 0 -> pl.WatchFd rx.WakeFd
                | _ -> ()
                // WaitMs() is milliseconds; Poller.Wait takes microseconds.  Passing
                // the millisecond value straight through made every timeout 1000x
                // shorter (2 ms became 2 us), so a session with no paths spun at ~86%
                // CPU.  WantsToSend no longer forces a zero timeout: WaitMs already
                // returns 0 when a receive batch is pending, and a send with no window
                // gets its own short backoff.
                let waitMs = carrier.WaitMs()
                pl.Wait(waitMs * 1000)
                stats.WaitUs <- stats.WaitUs + toUs (Stopwatch.GetTimestamp() - waitStart)
                match channel with
                | Some rx when rx.WakeFd >= 0 && pl.IsFdReadable rx.WakeFd -> rx.ClearWake ()
                | _ -> ()
                match channel with
                | Some rx -> rx.Drain carrier |> ignore
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
        stoppedSignal.Set()

    member _.Poller = pl
    member _.Stats = stats
    member _.Alive = Volatile.Read(&running)
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

    /// Start the session thread. Idempotent.
    member _.Start() =
        if Interlocked.Exchange(&stopped, 0) = 1 then
            invalidOp "a stopped coordinator cannot be restarted"
        if not running then
            running <- true
            let t = Thread(ThreadStart(loop))
            t.IsBackground <- true
            t.Name <- "AsyncSession"
            t.Start()
            thread <- Some t

    /// Stop the thread and close the carrier. Idempotent.
    member _.Stop() =
        if Interlocked.Exchange(&stopped, 1) = 0 then
            Volatile.Write(&running, false)
            match thread with
            | None -> ()
            | Some t ->
                t.Join 5000 |> ignore
                thread <- None
            carrier.Close()
            stoppedSignal.Set()
            (pl :> IDisposable).Dispose()

    interface IDisposable with
        member this.Dispose() = this.Stop()

/// Create a coordinator for an existing carrier.
module AsyncSessionCoordinator =
    let create (carrier: SessionCarrier) (clock: CachedClock) =
        new AsyncSessionCoordinator(carrier, clock)

    /// A coordinator that also drains frames read by the shared IO pool.
    let createWith (carrier: SessionCarrier) (clock: CachedClock) (group: AsyncPollerGroup) (channel: RxChannel) =
        new AsyncSessionCoordinator(carrier, clock, ?rxChannel = Some channel, ?ioGroup = Some group)
