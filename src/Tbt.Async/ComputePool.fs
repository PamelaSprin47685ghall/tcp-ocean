/// Compute thread pool for CPU-bound tasks with priority and work stealing.
namespace Tbt.Async

open System
open System.Diagnostics
open System.Threading

/// Statistics for one compute worker. A class: the worker updates it from its
/// own thread and the owner reads it from another, so a struct copy would hide
/// every update.
[<Sealed>]
type WorkerStats() =
    member val TasksExecuted = 0L with get, set
    member val TasksStolen = 0L with get, set
    member val TasksFailed = 0L with get, set
    member val IdleSpins = 0L with get, set
    member val TotalWorkUs = 0L with get, set

/// One worker thread. It only calls the task queue and the task body.
///
/// The worker blocks on a semaphore instead of spinning: an idle pool must not
/// burn a core, and a queued task must wake a worker at once.
[<Sealed>]
type ComputeWorker(workerId: int, taskQueue: MPMCTaskQueue, wake: SemaphoreSlim) =
    let stats = WorkerStats()
    let mutable running = false
    let mutable thread: Thread option = None

    let toUs (ticks: int64) = ticks * 1_000_000L / Stopwatch.Frequency

    let loop () =
        while Volatile.Read(&running) do
            wake.Wait()
            // A wake can land before the matching Enqueue is visible, or a stolen
            // task can leave a sibling's wake with an empty queue. Drain until
            // empty so a task that won the race is not stranded behind one wake.
            let mutable go = true
            while go && Volatile.Read(&running) do
                match taskQueue.TryDequeue workerId with
                | Some task ->
                    if not task.CancellationToken.IsCancellationRequested then
                        let start = Stopwatch.GetTimestamp()
                        try
                            task.Work()
                            stats.TasksExecuted <- stats.TasksExecuted + 1L
                        with _ ->
                            // A failing task must not kill the worker.
                            stats.TasksFailed <- stats.TasksFailed + 1L
                        stats.TotalWorkUs <- stats.TotalWorkUs + toUs (Stopwatch.GetTimestamp() - start)
                | None ->
                    stats.IdleSpins <- stats.IdleSpins + 1L
                    go <- false

    /// Stop the loop without waiting for the thread to join.
    member _.RequestStop() =
        Volatile.Write(&running, false)
        wake.Release() |> ignore

    /// Start the worker thread. Idempotent.
    member _.Start() =
        if not running then
            running <- true
            let t = Thread(ThreadStart(loop))
            t.IsBackground <- true
            t.Name <- sprintf "ComputeWorker-%d" workerId
            t.Priority <- ThreadPriority.AboveNormal
            t.Start()
            thread <- Some t

    /// Stop the worker thread. Idempotent.
    member _.Stop() =
        Volatile.Write(&running, false)
        wake.Release() |> ignore
        match thread with
        | None -> ()
        | Some t ->
            t.Join 5000 |> ignore
            thread <- None

    member _.Stats = stats
    member _.WorkerId = workerId

/// A pool of compute workers over one priority queue.
[<Sealed>]
type ComputePool(workerCount: int, queueCapacity: int) =
    do
        if workerCount < 1 then invalidArg "workerCount" "need at least one worker"
        if queueCapacity < 2 || (queueCapacity &&& (queueCapacity - 1)) <> 0 then
            invalidArg "queueCapacity" "capacity must be a positive power of 2"

    let taskQueue = MPMCTaskQueue(queueCapacity, workerCount)
    let wake = new SemaphoreSlim(0)
    let workers = Array.init workerCount (fun i -> ComputeWorker(i, taskQueue, wake))
    let mutable started = false

    let submit (priority: TaskPriority) (work: unit -> unit) (cancellationToken: CancellationToken option) =
        let ok =
            taskQueue.Enqueue
                { Priority = priority
                  Work = work
                  CancellationToken = defaultArg cancellationToken CancellationToken.None }
        if ok then wake.Release() |> ignore
        ok

    member _.Start() =
        if not started then
            started <- true
            for w in workers do w.Start()

    member _.Stop() =
        for w in workers do w.RequestStop()
        for w in workers do w.Stop()
        started <- false

    /// False when the high lane is full: the caller must retry or drop.
    member _.EnqueueRecovery(work: unit -> unit, ?cancellationToken: CancellationToken) =
        submit TaskPriority.High work cancellationToken

    member _.EnqueueEncoding(work: unit -> unit, ?cancellationToken: CancellationToken) =
        submit TaskPriority.Normal work cancellationToken

    member _.EnqueueBackground(work: unit -> unit, ?cancellationToken: CancellationToken) =
        submit TaskPriority.Low work cancellationToken

    member _.PendingCount = taskQueue.ApproximateCount
    member _.WorkerCount = workerCount
    member _.GetStats() = workers |> Array.map (fun w -> w.WorkerId, w.Stats)

    interface IDisposable with
        member this.Dispose() = this.Stop()

module ComputePool =
    /// Workers = cores minus the IO/session threads, never fewer than two.
    let createAuto (queueCapacity: int) =
        let workers = max 2 (Environment.ProcessorCount - 4)
        new ComputePool(workers, queueCapacity)

    let create (workerCount: int) (queueCapacity: int) =
        new ComputePool(workerCount, queueCapacity)
