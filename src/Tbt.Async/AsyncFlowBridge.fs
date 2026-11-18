/// Async FlowBridge with concurrent flow processing.
namespace Tbt.Async

open System
open System.Threading
open System.Collections.Generic
open Tbt.Carrier
open Tbt.Session

/// Flow worker thread that processes a subset of flow slots.
[<Sealed>]
type FlowWorker(workerId: int, slots: int[], bridge: FlowBridge) =
    let mutable running = false
    let mutable thread: Thread option = None
    let readTrigger = new ManualResetEventSlim()
    let writeTrigger = new ManualResetEventSlim()
    
    let workerLoop () =
        Threading.Thread.CurrentThread.Name <- sprintf "FlowWorker-%d" workerId
        
        while running do
            // Wait for read or write signal
            WaitHandle.WaitAny([| readTrigger.WaitHandle; writeTrigger.WaitHandle |], 100) |> ignore
            
            if readTrigger.IsSet then
                readTrigger.Reset()
                // Read from target sockets
                for slot in slots do
                    match bridge.Local slot with
                    | null -> ()
                    | _ -> if bridge.WantsRead slot then bridge.Read slot
            
            if writeTrigger.IsSet then
                writeTrigger.Reset()
                // Write to target sockets
                for slot in slots do
                    match bridge.Local slot with
                    | null -> ()
                    | _ -> if bridge.WantsWrite slot then bridge.OnWritable slot
    
    member _.TriggerRead() = readTrigger.Set()
    member _.TriggerWrite() = writeTrigger.Set()
    
    member _.Start() =
        if not running then
            running <- true
            let t = Thread(ThreadStart(workerLoop))
            t.IsBackground <- true
            t.Start()
            thread <- Some t
    
    member _.Stop() =
        running <- false
        match thread with
        | Some t -> t.Join(5000) |> ignore
        | None -> ()
    
    interface IDisposable with
        member this.Dispose() = this.Stop()

/// Async wrapper for FlowBridge with parallel flow processing.
[<Sealed>]
type AsyncFlowBridge(bridge: FlowBridge, workerCount: int) =
    let slotsPerWorker = 256 / workerCount
    let workers = 
        Array.init workerCount (fun i ->
            let startSlot = i * slotsPerWorker
            let slots = [| startSlot .. startSlot + slotsPerWorker - 1 |]
            new FlowWorker(i, slots, bridge))
    
    let mutable started = false
    
    /// Start all flow workers.
    member _.Start() =
        if not started then
            started <- true
            for w in workers do
                w.Start()
    
    /// Stop all flow workers.
    member _.Stop() =
        for w in workers do
            w.Stop()
        started <- false
    
    /// Trigger async read on all flows.
    member _.TriggerReadAll() =
        for w in workers do
            w.TriggerRead()
    
    /// Trigger async write on all flows.
    member _.TriggerWriteAll() =
        for w in workers do
            w.TriggerWrite()
    
    /// Get the underlying bridge.
    member _.Bridge = bridge
    
    interface IDisposable with
        member this.Dispose() = this.Stop()

/// Factory for creating async flow bridges.
module AsyncFlowBridge =
    /// Create async flow bridge with 2 workers (even/odd slots).
    let create (bridge: FlowBridge) =
        new AsyncFlowBridge(bridge, 2)
