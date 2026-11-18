/// IO thread for managing a subset of paths with dedicated epoll.
namespace Tbt.Async

open System
open System.Net.Sockets
open System.Threading
open System.Collections.Generic
open Tbt.Contracts

/// Frame received from a path, ready for session processing.
[<Sealed>]
type RxFrame(pathId: int, data: byte[], length: int, timestamp: int64) =
    member _.PathId = pathId
    member _.Data = data
    member _.Length = length
    member _.Timestamp = timestamp

/// Deprecated struct for backward compat
[<Struct>]
type RxFrameStruct =
    { PathId: int
      Data: byte[]
      Length: int
      Timestamp: int64 }

/// Frame to send on a path.
[<Sealed>]
type TxFrame(pathId: int, data: ReadOnlyMemory<byte>, timestamp: int64) =
    member _.PathId = pathId
    member _.Data = data
    member _.Timestamp = timestamp

/// Deprecated struct for backward compat
[<Struct>]
type TxFrameStruct =
    { PathId: int
      Data: ReadOnlyMemory<byte>
      Timestamp: int64 }

/// Statistics for an IO thread.
[<Struct>]
type IOThreadStats =
    { mutable FramesReceived: int64
      mutable FramesSent: int64
      mutable BytesReceived: int64
      mutable BytesSent: int64
      mutable ReceiveErrors: int64
      mutable SendErrors: int64
      mutable EpollCalls: int64
      mutable EpollTimeUs: int64 }

/// IO thread manages a subset of paths with dedicated epoll loop.
[<Sealed>]
type IOThread(threadId: int, pathIds: int[], rxQueue: SPSCQueue<RxFrame>, txQueues: SPSCQueue<TxFrame>[], coreId: int option) =
    let mutable stats = 
        { FramesReceived = 0L; FramesSent = 0L
          BytesReceived = 0L; BytesSent = 0L
          ReceiveErrors = 0L; SendErrors = 0L
          EpollCalls = 0L; EpollTimeUs = 0L }
    
    let paths = Dictionary<int, AsyncSocket>()
    let bufferPool = BufferPool(2048, pathIds.Length * 4)
    
    let mutable running = false
    let mutable thread: Thread option = None
    
    // Pending send operations per path
    let pendingSends = Dictionary<int, Queue<TxFrame>>()
    
    let rec handleReceive (pathId: int) (bytesReceived: int) (error: SocketError) =
        if error <> SocketError.Success then
            stats.ReceiveErrors <- stats.ReceiveErrors + 1L
        elif bytesReceived > 0 then
            match paths.TryGetValue(pathId) with
            | true, sock ->
                // Copy to owned buffer
                match bufferPool.TryRent() with
                | Some buffer ->
                    let data = sock.RecvBuffer
                    data.Slice(0, bytesReceived).CopyTo(Span(buffer, 0, bytesReceived))
                    
                    let frame = RxFrame(pathId, buffer, bytesReceived, DateTime.UtcNow.Ticks * 100L)
                    
                    if rxQueue.TryEnqueue(frame) then
                        stats.FramesReceived <- stats.FramesReceived + 1L
                        stats.BytesReceived <- stats.BytesReceived + int64 bytesReceived
                    else
                        // Queue full, drop frame
                        bufferPool.Return(buffer)
                    
                    // Continue receiving
                    sock.ReceiveAsync(handleReceive pathId)
                | None ->
                    // Buffer pool exhausted
                    ()
            | false, _ -> ()
    
    let rec handleSend (pathId: int) (bytesSent: int) (error: SocketError) =
        if error <> SocketError.Success then
            stats.SendErrors <- stats.SendErrors + 1L
        else
            stats.FramesSent <- stats.FramesSent + 1L
            stats.BytesSent <- stats.BytesSent + int64 bytesSent
        
        // Try to send next queued frame
        match pendingSends.TryGetValue(pathId) with
        | true, queue when queue.Count > 0 ->
            let nextFrame = queue.Dequeue()
            match paths.TryGetValue(pathId) with
            | true, sock ->
                sock.SendAsync(nextFrame.Data.Span, handleSend pathId)
            | false, _ -> ()
        | _ -> ()
    
    let processTxQueues () =
        // Drain all tx queues for this thread's paths
        for pathId in pathIds do
            let queueIdx = pathId / (256 / txQueues.Length)
            let txQueue = txQueues.[queueIdx]
            
            let mutable keepDraining = true
            while keepDraining do
                match txQueue.TryDequeue() with
                | Some frame when frame.PathId = pathId ->
                    match paths.TryGetValue(pathId) with
                    | true, sock ->
                        // Try immediate send or queue
                        match pendingSends.TryGetValue(pathId) with
                        | true, queue when queue.Count > 0 ->
                            // Already sending, enqueue
                            queue.Enqueue(frame)
                        | _ ->
                            // Start send
                            let queue = Queue<TxFrame>()
                            pendingSends.[pathId] <- queue
                            sock.SendAsync(frame.Data.Span, handleSend pathId)
                    | false, _ -> ()
                | _ ->
                    keepDraining <- false
    
    let ioLoop () =
        match coreId with
        | Some core ->
            Threading.Thread.CurrentThread.Name <- sprintf "IOThread-%d-Core%d" threadId core
        | None ->
            Threading.Thread.CurrentThread.Name <- sprintf "IOThread-%d" threadId
        
        // Start async receives on all paths
        for kvp in paths do
            kvp.Value.ReceiveAsync(handleReceive kvp.Key)
        
        while running do
            let start = DateTime.UtcNow.Ticks
            
            // Process outgoing frames
            processTxQueues()
            
            // Yield to allow async callbacks to run
            Thread.Sleep(0)
            
            let elapsed = (DateTime.UtcNow.Ticks - start) / 10L
            stats.EpollCalls <- stats.EpollCalls + 1L
            stats.EpollTimeUs <- stats.EpollTimeUs + elapsed
            
            // Rate limit loop
            if elapsed < 100L then
                Thread.SpinWait(100)
    
    /// Add a path socket to this IO thread.
    member _.AddPath(pathId: int, socket: Socket) =
        if not (Array.contains pathId pathIds) then
            invalidArg "pathId" "Path ID not assigned to this IO thread"
        
        let asyncSock = AsyncSocket.wrap socket 2048
        paths.[pathId] <- asyncSock
        
        if running then
            // Start receiving immediately
            asyncSock.ReceiveAsync(handleReceive pathId)
    
    /// Remove a path from this IO thread.
    member _.RemovePath(pathId: int) =
        match paths.TryGetValue(pathId) with
        | true, sock ->
            sock.Close()
            paths.Remove(pathId) |> ignore
            pendingSends.Remove(pathId) |> ignore
        | false, _ -> ()
    
    /// Start the IO thread.
    member _.Start() =
        if not running then
            running <- true
            let t = Thread(ThreadStart(ioLoop))
            t.IsBackground <- true
            t.Priority <- ThreadPriority.Highest
            t.Start()
            thread <- Some t
    
    /// Stop the IO thread.
    member _.Stop() =
        running <- false
        match thread with
        | Some t -> t.Join(5000) |> ignore
        | None -> ()
        
        for kvp in paths do
            kvp.Value.Close()
        paths.Clear()
    
    /// Get IO thread statistics.
    member _.Stats = stats
    
    member _.ThreadId = threadId

/// IO thread pool managing all paths.
[<Sealed>]
type IOThreadPool(threadCount: int, rxQueueCapacity: int, txQueueCapacity: int, coreIds: int[] option) =
    let pathsPerThread = 256 / threadCount
    
    // One rx queue per IO thread → session thread
    let rxQueues = Array.init threadCount (fun _ -> SPSCQueue<RxFrame>(rxQueueCapacity))
    
    // One tx queue per path group ← session thread
    let txQueues = Array.init threadCount (fun _ -> SPSCQueue<TxFrame>(txQueueCapacity))
    
    let threads = 
        Array.init threadCount (fun i ->
            let startPath = i * pathsPerThread
            let pathIds = [| startPath .. startPath + pathsPerThread - 1 |]
            let coreId = 
                match coreIds with
                | Some cores when i < cores.Length -> Some cores.[i]
                | _ -> None
            IOThread(i, pathIds, rxQueues.[i], txQueues, coreId))
    
    let mutable started = false
    
    /// Start all IO threads.
    member _.Start() =
        if not started then
            started <- true
            for t in threads do
                t.Start()
    
    /// Stop all IO threads.
    member _.Stop() =
        for t in threads do
            t.Stop()
        started <- false
    
    /// Add a path to the appropriate IO thread.
    member _.AddPath(pathId: int, socket: Socket) =
        let threadIdx = pathId / pathsPerThread
        threads.[threadIdx].AddPath(pathId, socket)
    
    /// Remove a path from its IO thread.
    member _.RemovePath(pathId: int) =
        let threadIdx = pathId / pathsPerThread
        threads.[threadIdx].RemovePath(pathId)
    
    /// Enqueue a frame to send on a path.
    member _.EnqueueSend(frame: TxFrame) =
        let threadIdx = frame.PathId / pathsPerThread
        txQueues.[threadIdx].TryEnqueue(frame) |> ignore
    
    /// Try to dequeue received frames from all threads.
    member _.TryDequeueReceived(destination: Span<RxFrame>, maxCount: int) =
        let mutable total = 0
        let mutable i = 0
        while i < threadCount && total < maxCount do
            let available = min (maxCount - total) (destination.Length - total)
            let count = rxQueues.[i].TryDequeueBatch(destination.Slice(total, available), available)
            total <- total + count
            i <- i + 1
        total
    
    /// Get aggregated statistics from all IO threads.
    member _.GetStats() =
        threads |> Array.map (fun t -> t.ThreadId, t.Stats)
    
    interface IDisposable with
        member this.Dispose() = this.Stop()

/// Factory for creating IO thread pools.
module IOThreadPool =
    /// Create IO thread pool with 4 threads pinned to cores 0-3.
    let createDefault(rxQueueCapacity: int, txQueueCapacity: int) =
        let coreIds = [| 0; 1; 2; 3 |]
        new IOThreadPool(4, rxQueueCapacity, txQueueCapacity, Some coreIds)
    
    /// Create IO thread pool with custom thread count.
    let create(threadCount: int, rxQueueCapacity: int, txQueueCapacity: int) =
        new IOThreadPool(threadCount, rxQueueCapacity, txQueueCapacity, None)
