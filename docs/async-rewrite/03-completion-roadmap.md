# Async Rewrite Completion Roadmap

> **历史文档（2024-11-17 快照），不是当前状态。**
> 下文 “Completed Infrastructure (80%)” 与 “~20 errors remaining” 描述的是该日期的中间态。
> 真实状态：`dotnet build Tbt.slnx -c Release -p:PublishAot=false` 0 警告/0 错误，
> `Tbt.Session.Tests` 239/239、`Tbt.Async.Tests` 8/8，异步已是生产默认。
> 当前状态以 `02-integration-status.md`、`05-final-status.md`、`06-verification-log.md` 为准。

## Current Status (2024-11-17)

### Completed Infrastructure (80%)
- **2,137 lines** of new async code in `src/Tbt.Async/`
- **9 new modules**: SPSCQueue, MPMCQueue, ComputePool, IOThread, AsyncSocket, AsyncCodec, AsyncPathLink, AsyncSession
- **Architecture design** documented with thread model, message passing, and migration strategy
- **Core primitives** working: lock-free queues, work-stealing thread pool, async socket wrapper

### Compilation Status
- **~20 errors** remaining, all fixable (struct mutability, API mismatches, missing imports)
- **No fundamental design issues** - structure is sound
- **Estimated fix time**: 1-2 days of focused work

## Phase 1: Fix Compilation (1-2 days)

### High Priority Fixes
1. **Struct mutability** (4 errors)
   - `AsyncSessionStats` needs `mutable` fields
   - Fix: Change to class or mark fields mutable

2. **API surface mismatches** (8 errors)
   - `Decoder.OfferData()` doesn't exist - need `OfferParity()` + meta parsing
   - `AsyncEncoder.TryBeginSourceFor()` needs TxHalf integration
   - Fix: Create adapter layer between async wrappers and existing APIs

3. **Type imports** (3 errors)
   - Missing `CodeField` from Tbt.Codec
   - Fix: Add proper module imports

4. **Generic constraints** (5 errors)
   - Some queues need reference types
   - Fix: Already changed RxFrame/TxFrame to classes

### Implementation Steps
```fsharp
// 1. Create adapter layer in AsyncCodec.fs
type FrameAdapter =
    static member ParseRxFrame(frame: RxFrame): Cell option
    static member BuildTxFrame(cell: byte[], pathId: int): TxFrame

// 2. Fix AsyncDecoder API
member _.OfferCell(cell: Cell, callback: DecodeResult -> unit) =
    match cell.Type with
    | CellType.DATA -> 
        // Extract step, slot, data
        this.OfferData(...)
    | CellType.PARITY ->
        // Extract step, slot, parity data
        this.OfferParity(...)

// 3. Integrate with TxHalf
type AsyncTxHalf(txHalf: TxHalf, computePool: ComputePool) =
    member _.BeginSourceAsync(slot: int, callback: Span<byte> -> unit) =
        // Wrap TxHalf.TryBeginSourceFor()
```

## Phase 2: Minimal Integration (2-3 days)

### Goal
Get async mode to compile, instantiate, and pass basic smoke test (establish session, send 1 frame).

### Tasks
1. **Frame parsing** (4 hours)
   - Parse RxFrame bytes → Cell structure
   - Extract Meta from cell header
   - Validate authentication tag

2. **Session coordinator integration** (1 day)
   - Wire AsyncSessionCoordinator to Session
   - Map Session.OnFrame() → AsyncDecoder.OfferCell()
   - Map TxHalf candidates → IOThreadPool.EnqueueSend()

3. **Server/Client bootstrap** (1 day)
   - Add `--async-mode` flag to Server.fs / Client.fs
   - Create IOThreadPool + ComputePool in main()
   - Replace `carrier.Service(poller)` with `coordinator.Start()`

4. **Smoke test** (4 hours)
   - Start client+server in async mode
   - Establish session
   - Send 1 DATA frame
   - Verify decoder receives it
   - Log success

## Phase 3: FlowBridge Async (3-4 days)

### Goal
Parallel flow read/write operations for multi-stream scalability.

### Design
```fsharp
type AsyncFlowBridge(flowBridge: FlowBridge, ioPool: IOThreadPool) =
    let flowThreads = [| FlowThread(0, [|0..127|]); FlowThread(1, [|128..255|]) |]
    
    member _.ReadFlowsAsync() =
        // Each FlowThread reads its slots in parallel
        for t in flowThreads do
            t.TriggerRead()
    
    member _.WriteFlowsAsync() =
        // Each FlowThread writes its slots in parallel
        for t in flowThreads do
            t.TriggerWrite()

type FlowThread(threadId: int, slots: int[]) =
    let mutable running = false
    let readTrigger = new ManualResetEventSlim()
    
    member _.TriggerRead() = readTrigger.Set()
    
    member private _.Loop() =
        while running do
            readTrigger.Wait()
            readTrigger.Reset()
            for slot in slots do
                // bridge.Read(slot)
```

### Tasks
1. Implement FlowThread pool (1 day)
2. Wrap FlowBridge operations (1 day)
3. Integrate with AsyncSessionCoordinator (1 day)
4. Test multi-flow concurrency (1 day)

## Phase 4: Testing & Validation (3-4 days)

### Unit Tests
- **Lock-free queue correctness** (1 day)
  - Property: enqueue(x) then dequeue() returns x
  - Concurrency: N producers, M consumers, verify no lost items
  - Stress: 1M operations, check consistency

- **Work-stealing fairness** (4 hours)
  - Enqueue tasks with different priorities
  - Verify high-priority tasks execute first
  - Verify work stealing across workers

### Integration Tests
- **Async session smoke test** (4 hours)
  - Establish session in async mode
  - Send 1000 frames
  - Verify all received and decoded

- **Path failure recovery** (4 hours)
  - Start with 256 paths
  - Kill 64 paths mid-transfer
  - Verify session continues on remaining paths

### Performance Benchmarks
- **Single-stream throughput** (4 hours)
  - Baseline (sync): 10-12 Mbit/s
  - Target (async): 40-60 Mbit/s
  - Measure: iperf3 through tbtun

- **Multi-stream throughput** (4 hours)
  - Baseline (sync): 7.72 Mbit/s for 3 streams
  - Target (async): 90-150 Mbit/s for 3 streams
  - Measure: parallel fast.com downloads

- **Latency distribution** (4 hours)
  - Baseline: p50=?, p99=978ms (service cycle)
  - Target: p50=5ms, p99=20ms
  - Measure: frame-to-frame latency

- **CPU utilization** (2 hours)
  - Baseline: 100% on 1 core
  - Target: 60-80% across all cores
  - Measure: mpstat during transfer

## Phase 5: Production Rollout (1 week)

### Feature Flag
```fsharp
type AsyncMode =
    | Disabled
    | Enabled
    | CanaryPercent of int

let asyncMode =
    match Environment.GetEnvironmentVariable("TBTUN_ASYNC_MODE") with
    | "true" -> Enabled
    | s when s.StartsWith("canary:") -> 
        let pct = Int32.Parse(s.Substring(7))
        CanaryPercent pct
    | _ -> Disabled
```

### Rollout Plan
1. **Week 1**: Deploy with `Disabled`, code in production but not active
2. **Week 2**: Enable on test nodes, run 48-hour stress test
3. **Week 3**: Canary 10% of production traffic
4. **Week 4**: Gradual ramp to 100% if metrics good

### Success Criteria
- **Throughput**: ≥3× improvement on single-stream
- **Latency p99**: <50ms (was 978ms)
- **Stability**: 0 crashes in 48-hour stress test
- **CPU**: No core >90% utilization

### Rollback Triggers
- **Crash**: Any segfault or unhandled exception
- **Regression**: Throughput <50% of baseline
- **Latency**: p99 >2× baseline
- **Memory**: RSS >2× baseline

## Timeline Summary

| Phase | Duration | Deliverable |
|-------|----------|-------------|
| Fix compilation | 1-2 days | Compiles cleanly |
| Minimal integration | 2-3 days | Smoke test passes |
| FlowBridge async | 3-4 days | Multi-stream works |
| Testing & validation | 3-4 days | Benchmarks show gains |
| Production rollout | 1 week | Feature in production |
| **Total** | **2-3 weeks** | Async mode GA |

## Next Immediate Steps

1. Fix remaining compilation errors (today)
2. Implement FrameAdapter for Cell parsing (tomorrow)
3. Wire AsyncSessionCoordinator to Session (day 3)
4. Run first smoke test (day 4)

## Risk Mitigation

### If async mode doesn't improve performance
- Root cause: Threading overhead > parallelism gains
- Mitigation: Keep sync mode as default, async as opt-in
- Fallback: Ship Read-4096 fix only

### If production deployment fails
- Root cause: Subtle race condition or memory leak
- Mitigation: Extensive stress testing before rollout
- Fallback: Instant rollback via feature flag

### If development takes >3 weeks
- Root cause: API integration more complex than estimated
- Mitigation: Ship incremental improvements (IO threads only)
- Fallback: Defer full async to next quarter
