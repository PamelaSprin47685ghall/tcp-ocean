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
[<Sealed>]
type ComputeWorker(workerId: int, taskQueue: MPMCTaskQueue) =
    let stats = WorkerStats()
    let mutable running = false
    let mutable thread: Thread option = None

    let toUs (ticks: int64) = ticks * 1_000_000L / Stopwatch.Frequency

    let loop () =
        let mutable idleSpins = 0
        while Volatile.Read(&running) do
            match taskQueue.TryDequeue workerId with
            | Some task ->
                idleSpins <- 0
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
                idleSpins <- idleSpins + 1
                stats.IdleSpins <- stats.IdleSpins + 1L
                if idleSpins < 100 then Thread.SpinWait 20
                elif idleSpins < 1000 then Thread.Yield() |> ignore
                else Thread.Sleep 1

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
    let workers = Array.init workerCount (fun i -> ComputeWorker(i, taskQueue))
    let mutable started = false

    member _.Start() =
        if not started then
            started <- true
            for w in workers do w.Start()

    member _.Stop() =
        for w in workers do w.Stop()
        started <- false

    member _.EnqueueRecovery(work: unit -> unit, ?cancellationToken: CancellationToken) =
        taskQueue.Enqueue
            { Priority = TaskPriority.High
              Work = work
              CancellationToken = defaultArg cancellationToken CancellationToken.None }
        |> ignore

    member _.EnqueueEncoding(work: unit -> unit, ?cancellationToken: CancellationToken) =
        taskQueue.Enqueue
            { Priority = TaskPriority.Normal
              Work = work
              CancellationToken = defaultArg cancellationToken CancellationToken.None }
        |> ignore

    member _.EnqueueBackground(work: unit -> unit, ?cancellationToken: CancellationToken) =
        taskQueue.Enqueue
            { Priority = TaskPriority.Low
              Work = work
              CancellationToken = defaultArg cancellationToken CancellationToken.None }
        |> ignore

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
