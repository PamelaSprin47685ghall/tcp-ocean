# Current Architecture Bottlenecks (Pre-Async)

## Measurements (2024-11-17)

### Performance
- **Single-stream**: 10-12 Mbit/s average, peaks 65 Mbit/s
- **Multi-stream (3 concurrent)**: 7.72 Mbit/s total (worse than single!)
- **Degradation**: Performance drops across runs (11.78 → 10.15 → ...)
- **Target environment**: 330ms RTT, high packet loss

### Service Loop Profile
- **Average cycle time**: 50 ms
- **Peak cycle time**: 978 ms
- **FEC recovery rate**: 15-47% of received frames
- **BOOSTER repairs**: 1574 per test

## Architecture Analysis

### Single-Threaded Event Loop
```
Main Thread (100% CPU):
  poller.Wait(1ms)
  ↓
  Service():
    for each of 256 paths:
      ReceiveBudgeted() → read socket, parse frame
    ↓
    Decoder.TryTakeKnown() → GF(16) matrix operations
    ↓
    for each active flow:
      FlowBridge.Read() → read target socket (budget 4096)
    ↓
    Encoder.Seal() → GF(16) encoding
    ↓
    for each path:
      Send() → write socket
```

**Bottlenecks:**
1. **256 socket operations serialized** - one slow socket blocks all
2. **FEC recovery in main loop** - 978ms peaks freeze everything
3. **Flow reads sequential** - multi-flow gets no parallelism
4. **Encoding blocks sending** - can't overlap compute and IO

### Why Multi-Stream Is Slower

> **2026-11-19 更正：本节结论已被推翻，保留为历史记录。** 异步协调器成为默认后，
> `FlowBridge` 只访问 ready set 里的槽位，256 条路径各有独立 IO 线程，下面的
> 「串行读」已不存在；生产单流实测 71.3 Mbit/s（下载）、61.5 Mbit/s（纯隧道上传）。
> 而且「多流更慢」本身不是要解决的问题：tbtun 的职责就是把一条本地 TCP 流拆成
> 256 条公网路径，单流即已占满 256 路，多流倍率推不出瓶颈。

With `max-connections: 1` in mihomo, 3 streams multiplex over one smux connection.
But tbtun processes flows sequentially in FlowBridge.Read():
```fsharp
while slot >= 0 do
    if bridge.WantsRead slot then bridge.Read slot  // 4096 sources each
    slot <- armed.NextUp slot
```
3 flows × 4096 sources = serialized 12k sources, no concurrency benefit.

## Fixed vs Remaining

### ✓ Fixed: FlowBridge.Read Budget
- **Was**: 16 sources/call (~21.5 KB)
- **Now**: 4096 sources/call (~5.5 MB)
- **Result**: kernel_rwnd_limited 98.9% → 0.017%

### ⚠ Architectural Limits
Cannot fix with tuning:
1. Single-thread CPU saturation
2. FEC compute blocking IO
3. No flow-level parallelism
4. Sequential 256-path polling

## Target State (Async)

### IO Threads
- Dedicated epoll threads per path group (e.g. 4 threads × 64 paths)
- Async socket operations (SocketAsyncEventArgs)
- Lock-free frame queues to Session

### Compute Thread Pool
- Work-stealing FEC recovery tasks
- Parallel encoding of independent steps
- CPU affinity for cache locality

### Flow Processing
- Concurrent flow reads/writes
- Per-flow async state machines
- Backpressure propagation

### Expected Gains
- **Single-stream**: 40-60 Mbit/s (3-5× improvement)
- **Multi-stream**: Linear scaling up to CPU cores
- **Stability**: No 978ms stalls, consistent latency
