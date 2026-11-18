/// Async path link with dedicated IO thread handling.
namespace Tbt.Async

open System
open System.Net
open System.Net.Sockets
open Tbt.Contracts
open Tbt.Carrier

/// Async path link that delegates IO to an IOThreadPool.
[<Sealed>]
type AsyncPathLink(pathId: int, socket: Socket, ioPool: IOThreadPool) =
    let mutable frameLength = Layout.BootFrameLength
    let mutable closed = false
    
    // Stats
    let mutable framesIn = 0L
    let mutable framesOut = 0L
    let mutable bytesIn = 0L
    let mutable bytesOut = 0L
    
    do
        // Add socket to IO thread pool
        ioPool.AddPath(pathId, socket)
    
    /// Path ID.
    member _.PathId = pathId
    
    /// Frame length for this path.
    member _.FrameLength 
        with get() = frameLength
        and set(v) = frameLength <- v
    
    /// Send a frame on this path. Queues to IO thread pool.
    member _.Send(frame: ReadOnlySpan<byte>) =
        if not closed && frame.Length > 0 then
            let txFrame = TxFrame(pathId, ReadOnlyMemory(frame.ToArray()), DateTime.UtcNow.Ticks * 100L)
            
            ioPool.EnqueueSend(txFrame)
            framesOut <- framesOut + 1L
            bytesOut <- bytesOut + int64 frame.Length
    
    /// Close this path.
    member _.Close() =
        if not closed then
            closed <- true
            ioPool.RemovePath(pathId)
    
    /// Get statistics.
    member _.Stats = (framesIn, framesOut, bytesIn, bytesOut)
    
    interface IDisposable with
        member this.Dispose() = this.Close()

/// Collection of async path links.
[<Sealed>]
type AsyncPathCollection(ioPool: IOThreadPool) =
    let paths = Collections.Generic.Dictionary<int, AsyncPathLink>()
    let pathsLock = obj()
    
    /// Add a new path.
    member _.Add(pathId: int, socket: Socket) =
        lock pathsLock (fun () ->
            if not (paths.ContainsKey(pathId)) then
                let path = new AsyncPathLink(pathId, socket, ioPool)
                paths.[pathId] <- path
                true
            else
                false)
    
    /// Remove a path.
    member _.Remove(pathId: int) =
        lock pathsLock (fun () ->
            match paths.TryGetValue(pathId) with
            | true, path ->
                path.Close()
                paths.Remove(pathId) |> ignore
                true
            | false, _ -> false)
    
    /// Get a path.
    member _.TryGet(pathId: int) =
        lock pathsLock (fun () ->
            match paths.TryGetValue(pathId) with
            | true, path -> Some path
            | false, _ -> None)
    
    /// Send a frame on a specific path.
    member this.Send(pathId: int, frame: ReadOnlySpan<byte>) =
        match this.TryGet(pathId) with
        | Some path -> path.Send(frame)
        | None -> ()
    
    /// Get all path IDs.
    member _.PathIds() =
        lock pathsLock (fun () ->
            paths.Keys |> Seq.toArray)
    
    /// Close all paths.
    member _.CloseAll() =
        lock pathsLock (fun () ->
            for kvp in paths do
                kvp.Value.Close()
            paths.Clear())
    
    interface IDisposable with
        member this.Dispose() = this.CloseAll()
