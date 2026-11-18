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
                // With the IO pool the session poller no longer sees socket
                // writability, so a pending send must shorten the 5 s idle wait.
                // A pending send needs an immediate pass, not a 1 ms sleep:
                // zero makes epoll return at once and Service drains a batch.
                let waitMs = if carrier.Session.WantsToSend then 0 else carrier.WaitMs()
                pl.Wait waitMs
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
