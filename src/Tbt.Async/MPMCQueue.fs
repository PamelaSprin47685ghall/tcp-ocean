/// Work-stealing task queue with three priority levels.
///
/// The global queues are Multi-Producer/Multi-Consumer; a worker queue is a
/// bounded Chase-Lev deque owned by one worker (push/pop) and stolen from the
/// top by the others.
namespace Tbt.Async

open System
open System.Collections.Concurrent
open System.Threading

/// Bounded Chase-Lev deque: one owner pushes and pops at the bottom, many
/// thieves take from the top. No item is lost or returned twice.
[<Sealed>]
type WorkStealingQueue<'T>(capacity: int) =
    do
        if capacity < 2 || (capacity &&& (capacity - 1)) <> 0 then
            invalidArg "capacity" "capacity must be a power of two, at least 2"

    let buffer: 'T[] = Array.zeroCreate capacity
    let mask = capacity - 1
    let mutable top = 0L
    let mutable bottom = 0L

    /// Push at the bottom. Owner thread only.
    member _.Push(item: 'T) =
        let b = Volatile.Read(&bottom)
        let t = Volatile.Read(&top)
        if b - t >= int64 capacity then false
        else
            buffer.[int (b &&& int64 mask)] <- item
            Interlocked.MemoryBarrier()
            Volatile.Write(&bottom, b + 1L)
            true

    /// Pop from the bottom. Owner thread only.
    member _.TryPop() =
        let b = Volatile.Read(&bottom) - 1L
        Volatile.Write(&bottom, b)
        Interlocked.MemoryBarrier()
        let t = Volatile.Read(&top)
        if b < t then
            Volatile.Write(&bottom, t)
            None
        elif b = t then
            // The last item: the owner and a thief may both want it.
            if Interlocked.CompareExchange(&top, t + 1L, t) = t then
                Volatile.Write(&bottom, t + 1L)
                let i = int (b &&& int64 mask)
                let item = buffer.[i]
                buffer.[i] <- Unchecked.defaultof<'T>
                Some item
            else
                Volatile.Write(&bottom, t)
                None
        else
            let i = int (b &&& int64 mask)
            let item = buffer.[i]
            buffer.[i] <- Unchecked.defaultof<'T>
            Some item

    /// Steal from the top. Any thread but the owner.
    member _.TrySteal() =
        let t = Volatile.Read(&top)
        Interlocked.MemoryBarrier()
        let b = Volatile.Read(&bottom)
        if t >= b then None
        else
            let item = buffer.[int (t &&& int64 mask)]
            if Interlocked.CompareExchange(&top, t + 1L, t) = t then Some item
            else None

    /// Approximate number of items (may be stale).
    member _.Count =
        let b = Volatile.Read(&bottom)
        let t = Volatile.Read(&top)
        int (max 0L (b - t))

/// Priority levels for compute tasks.
type TaskPriority =
    | High = 0      // FEC recovery (latency-sensitive)
    | Normal = 1    // encoding
    | Low = 2       // background work

/// One unit of compute work.
[<Struct>]
type ComputeTask =
    { Priority: TaskPriority
      Work: unit -> unit
      CancellationToken: CancellationToken }

/// Three global MPMC queues plus one owner deque per worker.
[<Sealed>]
type MPMCTaskQueue(queueCapacity: int, workerCount: int) =
    do
        if workerCount < 1 then invalidArg "workerCount" "need at least one worker"

    let workerQueues = Array.init workerCount (fun _ -> WorkStealingQueue<ComputeTask>(queueCapacity))
    let high = ConcurrentQueue<ComputeTask>()
    let normal = ConcurrentQueue<ComputeTask>()
    let low = ConcurrentQueue<ComputeTask>()

    /// Enqueue according to priority. Always succeeds (the global queues are unbounded).
    member _.Enqueue(task: ComputeTask) =
        match task.Priority with
        | TaskPriority.High -> high.Enqueue task
        | TaskPriority.Normal -> normal.Enqueue task
        | _ -> low.Enqueue task
        true

    /// High, then normal, then own queue, then a steal, then low.
    member _.TryDequeue(workerId: int) =
        let own = workerQueues.[workerId].TryPop()
        match own with
        | Some task -> Some task
        | None ->
            let mutable task = Unchecked.defaultof<ComputeTask>
            if high.TryDequeue(&task) then Some task
            else
                let mutable task = Unchecked.defaultof<ComputeTask>
                if normal.TryDequeue(&task) then Some task
                else
                    let mutable stolen = None
                    let mutable i = 1
                    while i < workerCount && stolen.IsNone do
                        stolen <- workerQueues.[(workerId + i) % workerCount].TrySteal()
                        i <- i + 1
                    match stolen with
                    | Some task -> Some task
                    | None ->
                        let mutable task = Unchecked.defaultof<ComputeTask>
                        if low.TryDequeue(&task) then Some task else None

    /// Approximate total pending tasks.
    member _.ApproximateCount =
        let mutable total = high.Count + normal.Count + low.Count
        for q in workerQueues do total <- total + q.Count
        total

    member _.WorkerCount = workerCount
