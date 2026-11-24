# 异步路径验证日志（2026-11-18）

本文件记录实测证据与静态核对结论，不记录计划。未验证的项目放在最后一节，不用推测填空。

## 编译

| 命令 | 结果 |
|---|---|
| `dotnet build src/Tbt.Async/Tbt.Async.fsproj -p:PublishAot=false` | 0 错误（早期 16 条错误的基线见 `02-integration-status.md`） |
| `dotnet build src/Tbt.Cli/Tbt.Cli.fsproj -c Release -p:PublishAot=false` | Build succeeded，0 Warning，0 Error |
| `dotnet build tests/Tbt.Async.Tests/Tbt.Async.Tests.fsproj -c Release -p:PublishAot=false` | 30.4s，0 Warning，0 Error |

`-p:PublishAot=false` 的既有约束：NU1903（ILCompiler 10.0.12 漏洞告警）与 IL2104（FSharp.Core trim 警告）都会被
`TreatWarningsAsErrors` 拦下。e2e 脚本只在工具链构建里加 `-p:NuGetAudit=false -p:SuppressTrimAnalysisWarnings=true`，
产品构建保持严格。

## 单元测试

新增最小测试工程 `tests/Tbt.Async.Tests`，只引用 `Tbt.Async` / `Tbt.Contracts` / `Tbt.Wire`，通过 `Link`
复用 `tests/Tbt.Tests/AsyncTests.fs`，不连带 `Tbt.Cli` 与 Session/TUN 大文件。它已加入 `Tbt.slnx`。

首轮结果：8 个用例，7 通过、1 失败，耗时 209ms。修 `SPSCQueue` 后重跑：**8/8 通过，193ms**。

- 失败：`SPSCQueue.TryDequeueBatch` 在队列提前空时把返回值设成 `limit`，声称写入了并未写入的元素。
  修复：循环改为 `None -> go <- false`，返回真实写入数。
- 通过：SPSC 单生产单消费 100 万次顺序、批量出队顺序、四窃取者 20 万次不丢不重、owner pop 与 steal 不共享、
  `ComputePool` 单 worker 优先级 high→normal→low、4 worker 2 万任务、`BufferPool` 租借归还平衡、
  `FrameAdapter` 六种 CellType 映射。

## 数据面缺陷（已修，`src/Tbt.Carrier/AsyncPaths.fs`）

1. `AsyncPathLink.TakeOver` 没有把路径 socket 设为非阻塞。IO 线程会在 `Send` 里睡眠，同一线程上的所有路径一起停住。
   现在 `try socket.Blocking <- false with _ -> ()`。
2. tx 槽位没有记录每帧提交长度，`PumpTx` 用全局 `frameLength`。新增 `txLen`，`CommitFrame` 在递增 `txHead`
   前记录长度，`PumpTx` 只发已提交的字节。
3. 槽位复用时没有清零 `txSent`，新帧可能从中途开始发。`BeginFrame` 现在清零。
4. `Send` 返回 0 且无错误时会继续循环。现在视为本轮结束，下一轮重试。

修复前的现象（4 路、20ms 单向、200Mbit/s、SKIP_ECHO）：握手 5.75s 成功，之后会话层每秒只前进一帧，
`frames_in` 每秒 +1、`cons_tx_b` 每秒 +1415 字节、发送全部堆在一条路径、echo 线程全部超时。

## AsyncPathLink 的跨线程协议（静态核对）

- 会话线程只调用 `TryBeginFrame` / `CommitFrame` / `TryTakeFrame` 的公开面；IO 线程只调用 `PumpRx` / `PumpTx`。
- rx 缓冲 `rxStart` / `rxEnd` 只在路径 `Attach` 之后由 IO 线程触碰。`SessionCarrier.AddPath` 的顺序是
  `paths.Add` → `path.Attach` → `group.Add`，所以 IO 线程拿到路径时缓冲已经清空并归它所有。
- tx 槽位：`txHead` 由会话线程 `Volatile.Write`，`txTail` 由 IO 线程写。`CommitFrame` 先写 `txLen.[slot]`
  再 release-write `txHead`；IO 线程 acquire-read `txHead` 后读 `txLen`，长度可见。
- 槽位只会在 `txHead - txTail < 16` 时被 `BeginFrame` 选中，此时该槽位已被 IO 线程发完并推进 `txTail`，
  所以会话线程清 `txSent.[slot]` 与 IO 线程读它不会重叠。
- `PumpTx` 对 `WouldBlock` 保留 `txSent.[slot]` 偏移，对其它 socket 错误置 `alive = 0`。

## SRTT 钳制（P1-7，静态核对）

`TxHalf.fs` 的 `clampSrttToObservedMin()`：

```
let observed = min minCurNs minPrevNs
if observed <> Int64.MaxValue && srttNs < float observed then srttNs <- float observed
```

它在 `newRepair` 的 Karn 分支与默认 `main` Karn 分支都于更新 `minCurNs` 之后调用。因此一旦 `MinRttNs`
有实值，`SrttNs >= MinRttNs` 成立；10 秒窗口滚动时 `MinRttNs = min minCurNs minPrevNs` 只会下降，
不会让钳制失效。`rttSkipped` 只在 `repairLo < ack && repairNs >= sealAt`（最新步被修复覆盖）时增加，
不再因一次早期 BOOSTER 全局停采。运行期的 `rtt_skipped` 增速仍需 e2e 数据验证。

## e2e

- 4 路，`SKIP_ECHO=1 SKIP_IPERF=1`，`TBTUN_ASYNC_MODE=true`：rc=0，`session_up(mux): 6.05s`，
  `negotiated paths=4 window=1024`，`session close(mux): ok`。日志首行有
  `async mode: one poller and one thread per session`（服务端）与 `async mode: session loop on its own thread`（客户端）。
- 完整 4 路（echo + iperf + ping）与 256 路握手的结果在本文件后续更新中补充。

## 未验证

- 256 路握手。
- 单流连续 5 次 15 秒吞吐稳定性、3 流并行总吞吐、30 分钟长跑的 `csvc_us` 尾部。
- HND0 的 SSE2 开关 A/B（`DOTNET_EnableHWIntrinsic=0`）。
- `Tbt.Tests` 在无并发构建下的单独编译耗时；已知 900s 超时一次、816s 与 496s 各完成一次。

## 接收环 accept 缺陷（2026-11-18，已修）

`AsyncSessionCoordinator.loop` 原先在 `pl.Wait` 之后调用 `beforeService`，而 `Client.cycle` 的第一件事是
`pl.WatchRead listener`。`Poller.watchFd` 在 `stale` 状态下会清空 `count` / `entryOf` / `ownerNow`，
于是本轮 `Wait` 刚填入的 `Revents` 全部失效：`pl.IsReadable listener` 恒假，`tryAccept` 从不执行。

证据（`/tmp/tbtun-e2e.Wjx5r0`，旧代码）：客户端 `flows=0 opened=0`、`frames_out` 每秒 +1、
`cons_tx_b` 每次 +1415（一帧）、`path_tx=0:14150,1:0,2:0,3:0`；服务端 `sessions=0`，`frames_in` 9→11。

修复：`AsyncSessionCoordinator` 新增 `BeforeWait`，在 `carrier.Watch` 之后、`pl.Wait` 之前调用；
`Client.cycle` 拆成 `watchListener`（`WatchRead`，走 `BeforeWait`）与 `serve`（`IsReadable` → accept、
`attachJoined`、stats、close，走 `BeforeService`）。同步模式的 `cycle` 顺序不变。
编译：`dotnet build src/Tbt.Cli -c Release -p:PublishAot=false` → 0 Warning、0 Error、6.03s。

## 帧认证独立复验（2026-11-19）

默认异步、256 路 TCP 回环，HTTP POST 和响应各 4,194,304 B，逐字节相同。两端 `rx_cells` 分别为客户端 3,284、服务端 3,928，`rx_auth_fails` 均为 0。前一字段在认证及 Meta 检查通过后递增，后一字段直接读 `CellOpener`；会话失败数不能替代它们。此为本机测试，不是生产采样。
