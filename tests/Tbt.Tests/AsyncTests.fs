/// Concurrency tests for the async infrastructure. These are self-contained: no
/// sockets, no timing assumptions beyond a generous join timeout.
module Tbt.Tests.AsyncTests

open System
open System.Collections.Concurrent
open System.Threading
open Xunit
open Tbt.Async
open Tbt.Contracts
open Tbt.Wire

[<Fact>]
let ``spsc: one producer and one consumer see every item in order`` () =
    let q = SPSCQueue<int>(1024)
    let count = 1_000_000
    let seen = ref 0
    let consumer =
        Thread(fun () ->
            let mutable expected = 0
            while expected < count do
                match q.TryDequeue() with
                | Some value ->
                    if value <> expected then failwithf "order: expected %d, got %d" expected value
                    expected <- expected + 1
                    seen.Value <- expected
                | None -> Thread.SpinWait 10)
    consumer.Start()
    for i in 0 .. count - 1 do
        while not (q.TryEnqueue i) do Thread.SpinWait 10
    consumer.Join()
    Assert.Equal(count, seen.Value)
    Assert.Equal(0, q.Count)

[<Fact>]
let ``spsc: batch dequeue returns the same order as single dequeue`` () =
    let q = SPSCQueue<int>(64)
    for i in 0 .. 40 do Assert.True(q.TryEnqueue i)
    let buffer = Array.zeroCreate<int> 16
    let mutable read = 0
    let mutable n = q.TryDequeueBatch(Span(buffer), buffer.Length)
    while n > 0 do
        for i in 0 .. n - 1 do
            Assert.Equal(read + i, buffer.[i])
        read <- read + n
        n <- q.TryDequeueBatch(Span(buffer), buffer.Length)
    Assert.Equal(41, read)

[<Fact>]
let ``work stealing: four thieves take each item exactly once`` () =
    let capacity = 1024
    let q = WorkStealingQueue<int>(capacity)
    let count = 200_000
    let seen = Array.zeroCreate<int> count
    let producerDone = ref 0
    // A ref cell, not `let mutable`: the thieves capture it in closures, and F#
    // rejects taking the address of a captured mutable local.
    let stolen = ref 0
    let thieves =
        [| for _ in 1 .. 4 ->
             let t =
                 Thread(fun () ->
                     let mutable running = true
                     while running do
                         match q.TrySteal() with
                         | Some value ->
                             Interlocked.Increment(&seen.[value]) |> ignore
                             Interlocked.Increment(&stolen.contents) |> ignore
                         | None ->
                             if Volatile.Read(&producerDone.contents) = 1 && q.Count = 0 then running <- false
                             else Thread.Yield() |> ignore)
             t.Start()
             t |]
    let mutable pushed = 0
    while pushed < count do
        if q.Push pushed then pushed <- pushed + 1
        else Thread.Yield() |> ignore
    Volatile.Write(&producerDone.contents, 1) |> ignore
    for t in thieves do Assert.True(t.Join 30000, "thief did not stop")
    let mutable bad = 0
    for i in 0 .. count - 1 do
        if seen.[i] <> 1 then bad <- bad + 1
    Assert.Equal(0, bad)
    Assert.Equal(count, stolen.Value)
    Assert.Equal(0, q.Count)

[<Fact>]
let ``work stealing: owner pop and thief never share an item`` () =
    let q = WorkStealingQueue<int>(64)
    for i in 0 .. 63 do Assert.True(q.Push i)
    let seen = Array.zeroCreate<int> 64
    let mutable taken = 0
    // Pop half at the bottom while two thieves take from the top.
    for _ in 0 .. 31 do
        match q.TryPop() with
        | Some value ->
            Interlocked.Increment(&seen.[value]) |> ignore
            taken <- taken + 1
        | None -> ()
    while taken < 64 do
        match q.TrySteal() with
        | Some value ->
            Interlocked.Increment(&seen.[value]) |> ignore
            taken <- taken + 1
        | None -> ()
    for i in 0 .. 63 do Assert.Equal(1, seen.[i])

[<Fact>]
let ``mpmc: concurrent producers never exceed a lane and every accepted task is dequeued`` () =
    let capacity = 64
    let producers = 8
    let attemptsPerProducer = capacity * 4
    let q = MPMCTaskQueue(capacity, 1)
    let accepted = Array.zeroCreate<int> (producers * attemptsPerProducer)
    let mutable acceptedCount = 0
    let ready = new CountdownEvent(producers)
    let go = new ManualResetEventSlim(false)
    let workers =
        Array.init producers (fun p ->
            Thread(fun () ->
                ready.Signal() |> ignore
                go.Wait()
                for a in 0 .. attemptsPerProducer - 1 do
                    let slot = p * attemptsPerProducer + a
                    let task =
                        { Priority = TaskPriority.High
                          Work = (fun () -> ())
                          CancellationToken = CancellationToken.None }
                    if q.Enqueue task then
                        accepted.[slot] <- 1
                        Interlocked.Increment(&acceptedCount) |> ignore))
    for w in workers do w.Start()
    ready.Wait() |> ignore
    go.Set()
    for w in workers do w.Join()
    Assert.Equal(capacity, Volatile.Read(&acceptedCount))
    Assert.Equal(capacity, q.ApproximateCount)
    let mutable dequeued = 0
    while (match q.TryDequeue 0 with Some _ -> dequeued <- dequeued + 1; true | None -> false) do ()
    Assert.Equal(capacity, dequeued)
    Assert.Equal(0, q.ApproximateCount)
    Assert.Equal(capacity, accepted |> Array.sum)

[<Fact>]
let ``compute pool: one worker runs high, then normal, then low`` () =
    use pool = ComputePool.create 1 64
    let order = ConcurrentQueue<string>()
    use finished = new CountdownEvent(3)
    pool.EnqueueBackground(fun () -> order.Enqueue "low"; finished.Signal() |> ignore) |> ignore
    pool.EnqueueEncoding(fun () -> order.Enqueue "normal"; finished.Signal() |> ignore) |> ignore
    pool.EnqueueRecovery(fun () -> order.Enqueue "high"; finished.Signal() |> ignore) |> ignore
    pool.Start()
    Assert.True(finished.Wait 10000, "tasks did not finish")
    Assert.Equal<string[]>([| "high"; "normal"; "low" |], order.ToArray())

[<Fact>]
let ``compute pool: a full lane refuses the next task`` () =
    // Do not start the pool: the workers would drain the lane and hide the bound.
    use pool = ComputePool.create 1 2
    Assert.True(pool.EnqueueRecovery(fun () -> ()))
    Assert.True(pool.EnqueueRecovery(fun () -> ()))
    Assert.False(pool.EnqueueRecovery(fun () -> ()))

[<Fact>]
let ``compute pool: four workers execute every task exactly once`` () =
    use pool = ComputePool.create 4 1024
    let count = 20_000
    let seen = Array.zeroCreate<int> count
    use finished = new CountdownEvent(count)
    pool.Start()
    for i in 0 .. count - 1 do
        let index = i
        let submit () =
            match index % 3 with
            | 0 -> pool.EnqueueRecovery(fun () -> Interlocked.Increment(&seen.[index]) |> ignore; finished.Signal() |> ignore)
            | 1 -> pool.EnqueueEncoding(fun () -> Interlocked.Increment(&seen.[index]) |> ignore; finished.Signal() |> ignore)
            | _ -> pool.EnqueueBackground(fun () -> Interlocked.Increment(&seen.[index]) |> ignore; finished.Signal() |> ignore)
        while not (submit ()) do Thread.Yield() |> ignore
    Assert.True(finished.Wait 30000, "tasks did not finish")
    let mutable bad = 0
    for i in 0 .. count - 1 do
        if seen.[i] <> 1 then bad <- bad + 1
    Assert.Equal(0, bad)

[<Fact>]
let ``buffer pool: rent and return is balanced`` () =
    let pool = BufferPool(1024, 8)
    let rented = Array.init 8 (fun _ -> pool.TryRent())
    Assert.All(rented, fun buffer -> Assert.True(buffer.IsSome))
    Assert.True((pool.TryRent()).IsNone)
    for buffer in rented do pool.Return buffer.Value
    let again = Array.init 8 (fun _ -> pool.TryRent())
    Assert.All(again, fun buffer -> Assert.True(buffer.IsSome))

let private metaOf kind =
    { Type = kind
      Flags = MetaFlags.None
      Slot = 3uy
      StepUsed = 5uy
      Aux = 0us
      RxWinGrant = 1us
      StepId = 7UL
      AckStepLo = 2UL
      CarrierWm = 0UL
      KHist = 0u
      ErasureQ16 = 0us }

[<Fact>]
let ``frame adapter names every cell type`` () =
    match FrameAdapter.kindOf (metaOf CellType.Data) with
    | Coded(CellType.Data, 7UL, 3, 5) -> ()
    | other -> failwithf "DATA: %A" other
    match FrameAdapter.kindOf (metaOf CellType.Parity) with
    | Coded(CellType.Parity, 7UL, 3, 5) -> ()
    | other -> failwithf "PARITY: %A" other
    match FrameAdapter.kindOf (metaOf CellType.Booster) with
    | Coded(CellType.Booster, 7UL, 3, 5) -> ()
    | other -> failwithf "BOOSTER: %A" other
    Assert.Equal<FrameKind>(Ack, FrameAdapter.kindOf (metaOf CellType.WinAck))
    Assert.Equal<FrameKind>(Heartbeat 7UL, FrameAdapter.kindOf (metaOf CellType.Heartbeat))
    Assert.Equal<FrameKind>(Control, FrameAdapter.kindOf (metaOf CellType.Ctrl))
    Assert.Equal<FrameKind>(FlowState, FrameAdapter.kindOf (metaOf CellType.FlowState))
