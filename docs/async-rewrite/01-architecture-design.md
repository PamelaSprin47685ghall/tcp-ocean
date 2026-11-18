# Async Architecture Design

## Thread Model

### IO Thread Pool (4 threads, pinned to cores 0-3)
Each IO thread owns a subset of paths and runs an independent epoll loop:

```
IOThread-0: paths 0-63
  - epoll_wait() on 64 sockets
  - Non-blocking receive → frame to RxQueue
  - Non-blocking send from TxQueue
  - No decoder/encoder logic
  
IOThread-1: paths 64-127
IOThread-2: paths 128-191
IOThread-3: paths 192-255
```

**Ownership:**
- Each IOThread owns its PathLink sockets
- No locking on socket operations
- Frame buffers allocated per-thread

### Compute Thread Pool (variable, work-stealing)
CPU-bound tasks dispatched here:

```
ComputeWorker (N threads, N = CPU cores - 4):
  - FEC recovery (GF(16) matrix solve)
  - Encoder step sealing
  - Crypto operations (if not hardware-accelerated)
```

**Task queue:**
- Lock-free MPMC queue
- Work-stealing between workers
- Priority: recovery > encoding

### Session Thread (1 thread, core 4)
Orchestrates the session state machine:

```
SessionThread:
  - Pull decoded frames from RxQueue
  - Dispatch to flows
  - Pull flow data for encoding
  - Dispatch encoding tasks
  - Handle control messages (NACK, BOOSTER)
  - Rate control and pacing
```

**No compute:**
- Delegates all heavy work to ComputePool
- Fast path for already-decoded frames
- Async waits on task completion

### Flow Threads (2 threads)
Handle application socket IO:

```
FlowThread-0: even flow slots
FlowThread-1: odd flow slots
  - epoll on target sockets
  - Non-blocking read/write
  - Backpressure signaling to SessionThread
```

## Data Structures

### Lock-Free Structures

#### RxQueue (per IOThread → SessionThread)
```fsharp
type RxQueue = 
    private {
        buffer: Frame[]  // power-of-2 size, e.g. 8192
        mutable head: int64  // producer (IOThread)
        mutable tail: int64  // consumer (SessionThread)
    }
    
// Single-producer, single-consumer
// IOThread writes, SessionThread reads
// Atomics only on head/tail indices
```

#### TxQueue (SessionThread → per IOThread)
```fsharp
type TxQueue = 
    private {
        buffer: Cell[]  // sealed cells ready to send
        mutable head: int64
        mutable tail: int64
        pathMask: uint64  // which paths can send this cell
    }
```

#### ComputeTask Queue (MPMC)
```fsharp
type ComputeTask =
    | RecoverStep of stepId:uint64 * decoder:Decoder * callback:(unit -> unit)
    | SealStep of stepId:uint64 * encoder:Encoder * callback:(unit -> unit)
    
type TaskQueue =
    // Chase-Lev work-stealing deque
    // https://www.dre.vanderbilt.edu/~schmidt/PDF/work-stealing-dequeue.pdf
    private {
        buffer: ComputeTask[]
        mutable top: int
        mutable bottom: int
    }
```

### Thread-Safe Structures (with locking)

#### Decoder/Encoder Coordination
```fsharp
// Decoder: multiple ComputeWorkers may access
type AsyncDecoder = 
    private {
        core: Decoder  // protected by decoderLock
        decoderLock: SpinLock
        pendingRecovery: ConcurrentDictionary<uint64, TaskCompletionSource<unit>>
    }
    
    member this.TryTakeKnownAsync(): Task<KnownFrame option>
    member this.OfferParityAsync(step, slot, symbol): Task<bool>
```

#### Flow State
```fsharp
// Per-flow state, accessed by FlowThread and SessionThread
type AsyncFlowState =
    private {
        slot: int
        rxBuffer: CircularBuffer  // FlowThread → SessionThread
        txBuffer: CircularBuffer  // SessionThread → FlowThread
        rxLock: SpinLock
        txLock: SpinLock
        rxReady: ManualResetEventSlim
        txReady: ManualResetEventSlim
    }
```

## Message Passing

### Frame Path: Network → Application

```
IOThread (path receive)
    ↓ [RxQueue, lock-free]
SessionThread (parse meta, dispatch)
    ↓ (if parity) [TaskQueue, lock-free]
ComputeWorker (FEC recovery)
    ↓ [callback]
SessionThread (decoder.TryTakeKnown)
    ↓ (if source) [FlowRxBuffer, locked]
FlowThread (write to target socket)
```

### Frame Path: Application → Network

```
FlowThread (read from target socket)
    ↓ [FlowTxBuffer, locked]
SessionThread (BeginSource)
    ↓ [TaskQueue, lock-free]
ComputeWorker (encoder.Seal)
    ↓ [TxQueue, lock-free]
IOThread (send on path socket)
```

## Backpressure

### Flow → Session
```fsharp
type FlowBackpressure =
    | ReadMore      // flow buffer has space
    | ReadPause     // flow buffer full, pause sources
    | ReadClosed    // flow closed
```

### Session → IO
```fsharp
type SendBackpressure =
    | SendMore      // TxQueue has space
    | SendPause     // TxQueue full, pause encoding
```

### Compute → Session
```fsharp
// ComputeTask returns:
// - Success: callback invoked, session can proceed
// - QueueFull: task rejected, session must retry or pause
```

## Incremental Migration Strategy

### Phase 1: Dual-Mode Support (2-3 days)
- Add AsyncPathLink alongside PathLink
- Add RxQueue/TxQueue infrastructure
- Keep existing Service() loop as fallback
- Feature flag: `--async-io` enables new path

### Phase 2: IO Layer (3-4 days)
- Implement IOThread pool
- Migrate PathLink.Receive → async
- Migrate PathLink.Send → async
- Benchmark: should eliminate 256-socket serialization

### Phase 3: Compute Layer (4-5 days)
- Implement ComputeWorker pool
- AsyncDecoder with parallel recovery
- AsyncEncoder with parallel sealing
- Benchmark: should eliminate 978ms peaks

### Phase 4: Flow Layer (2-3 days)
- FlowThread pool
- Async flow reads/writes
- Benchmark: multi-stream should scale linearly

### Phase 5: Integration (3-4 days)
- SessionThread coordinator
- Remove old Service() loop
- End-to-end async path
- Comprehensive testing

### Phase 6: Optimization (ongoing)
- CPU affinity tuning
- Queue size tuning
- Lock contention profiling
- Memory allocation reduction

## Risk Mitigation

### Correctness Risks
- **Race conditions**: Extensive property-based testing
- **Deadlocks**: Lock ordering discipline, avoid nested locks
- **Memory barriers**: Use .NET memory model guarantees

### Performance Risks
- **Context switching overhead**: Pin threads to cores
- **Cache thrashing**: Align data structures, thread-local pools
- **Queue contention**: Size queues to avoid blocking

### Rollback Plan
- Keep old code path with feature flag
- A/B test both implementations
- Metrics: throughput, latency p50/p99, CPU usage

## Expected Outcomes

### Throughput
- **Single-stream**: 10-12 Mbit/s → 40-60 Mbit/s (4-5×)
- **Multi-stream (3×)**: 7.72 Mbit/s → 90-150 Mbit/s (12-20×)

### Latency
- **Service cycle**: 50ms avg, 978ms p99 → 5ms avg, 20ms p99
- **Frame latency**: Reduced head-of-line blocking

### CPU Utilization
- **Single-thread**: 100% on one core → 60-80% across all cores
- **Compute overlap**: Encoding while receiving next batch

## Open Questions

1. **Decoder thread-safety**: Can we make Decoder lock-free with per-step locks?
2. **Flow fairness**: How to ensure no flow starves under high load?
3. **Memory pooling**: Reuse frame buffers or allocate per-receive?
4. **Crypto offload**: Can ChaCha20 use hardware acceleration?
