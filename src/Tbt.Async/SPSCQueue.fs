/// Single-Producer Single-Consumer lock-free queue for async message passing.
/// Based on Lamport's bounded queue with cache-line padding to avoid false sharing.
namespace Tbt.Async

open System
open System.Runtime.InteropServices
open System.Threading

/// Cache-line padding to prevent false sharing between producer and consumer.
type private CachePadding() = class end

/// Single-producer, single-consumer bounded lock-free queue.
/// Thread-safe for exactly one producer thread and one consumer thread.
/// Uses power-of-2 sizing for fast modulo via bitwise AND.
[<Sealed>]
type SPSCQueue<'T>(capacity: int) =
    do
        if capacity <= 0 || (capacity &&& (capacity - 1)) <> 0 then
            invalidArg "capacity" "Capacity must be a positive power of 2"
    
    let buffer: 'T[] = Array.zeroCreate capacity
    let mask = capacity - 1
    
    // Producer writes head, consumer reads head
    let mutable head = 0L
    let padAfterHead = CachePadding()
    
    // Consumer writes tail, producer reads tail
    let mutable tail = 0L
    let padAfterTail = CachePadding()
    
    /// Number of elements currently in the queue (approximate, may be stale).
    member _.Count =
        let h = Volatile.Read(&head)
        let t = Volatile.Read(&tail)
        int (h - t)
    
    /// Capacity of the queue.
    member _.Capacity = capacity
    
    /// Try to enqueue an item. Returns false if the queue is full.
    /// MUST be called only from the producer thread.
    member _.TryEnqueue(item: 'T) =
        let currentHead = head
        let currentTail = Volatile.Read(&tail)
        
        // Check if queue is full: head is one lap ahead of tail
        if currentHead - currentTail >= int64 capacity then
            false
        else
            let index = int (currentHead &&& int64 mask)
            buffer.[index] <- item
            // Release barrier: ensure item write completes before head increment
            Volatile.Write(&head, currentHead + 1L)
            true
    
    /// Try to dequeue an item. Returns None if the queue is empty.
    /// MUST be called only from the consumer thread.
    member _.TryDequeue() =
        let currentTail = tail
        let currentHead = Volatile.Read(&head)
        
        // Check if queue is empty
        if currentTail >= currentHead then
            None
        else
            let index = int (currentTail &&& int64 mask)
            let item = buffer.[index]
            buffer.[index] <- Unchecked.defaultof<'T>  // help GC
            // Release barrier: ensure item read completes before tail increment
            Volatile.Write(&tail, currentTail + 1L)
            Some item
    
    /// Dequeue multiple items into a span. Returns the number of items dequeued.
    /// MUST be called only from the consumer thread.
    member this.TryDequeueBatch(destination: Span<'T>, maxCount: int) =
        let mutable count = 0
        let limit = min maxCount destination.Length
        let mutable go = true
        // Stop on the first empty slot and return how many were really written.
        // Setting count to limit on empty would claim unwritten elements.
        while go && count < limit do
            match this.TryDequeue() with
            | Some item ->
                destination.[count] <- item
                count <- count + 1
            | None -> go <- false
        count
    
    /// Peek at the head item without dequeuing. Returns None if empty.
    /// MUST be called only from the consumer thread.
    member _.TryPeek() =
        let currentTail = tail
        let currentHead = Volatile.Read(&head)
        
        if currentTail >= currentHead then
            None
        else
            let index = int (currentTail &&& int64 mask)
            Some buffer.[index]
