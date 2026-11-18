# Async by default, and the polling removed with it

日期：2026-11-18

## 默认

`TBTUN_ASYNC_MODE` 不再是开关，而是回退开关：

| 值 | 行为 |
|---|---|
| 未设置 | 异步（每会话一个 poller + 一个线程） |
| `0` / `false` / `off` / `disabled` | 同步回退 |
| 其他值 | 异步 |

启动日志分别打印
`async mode: session loop on its own thread (TBTUN_ASYNC_MODE)` 或
`async mode: one poller and one thread per session (TBTUN_ASYNC_MODE)`。

## 清除的非事件驱动等待

| 位置 | 旧 | 新 |
|---|---|---|
| `Tbt.Carrier/AsyncPaths.fs` | `poller.Wait 1000`（1 ms 微轮询新路径） | 每 IO 线程一个 eventfd；`Add` 写对应 fd，线程 `poller.Wait -1` 阻塞 |
| `Tbt.Cli/Client.fs` | 异步分支 `Thread.Sleep 50` 等协调器 | `AsyncSessionCoordinator.Stopped.Wait`，仅 stop deadline 用一次定时等待 |
| `Tbt.Async/AsyncSession.fs` | `WantsToSend` 时 `min 1 (WaitMs())` | `0`（非阻塞 pass），队列排空后回到 5 s 阻塞 |
| `Tbt.Async/ComputePool.fs` | `SpinWait 20` → `Yield` → `Sleep 1` | `SemaphoreSlim`；入队 `Release`，空闲 `Wait` |
| `Tbt.NativeLib/ClientHost.cs` | 握手 `Thread.Sleep 5` | `socket.Poll(remaining, SelectRead)` |

保留的等待都是事件驱动：`Poller.Wait` 是 `epoll_wait` / `kqueue`，`poller.Wait 5000` 在可读时立即返回，不是定时轮询。`HostRuntime` 的 `poller.Wait(50_000)` 同此。

## 删除的死代码

`src/Tbt.Async` 中从未进入生产路径的四个文件：

- `IOThread.fs`（`IOThread`/`IOThreadPool`，被 `AsyncPaths` 取代）
- `AsyncPathLink.fs`
- `AsyncFlowBridge.fs`
- `AsyncCodec.fs`

`Tbt.Async.fsproj` 现在只编译 `SPSCQueue`、`MPMCQueue`、`AsyncSocket`（保留 `BufferPool` 给单测）、`ComputePool`、`FrameAdapter`、`AsyncSession`。

## CommitFrame 唤醒（2026-11-18，异步默认后的第一个回归）

现象：异步设为默认并部署两端后，本地到 HND0 的 244 条 TCP 连接建立，但服务端 `frames_in=0`，代理 `000`、20 秒超时。

根因：IO 线程从 `poller.Wait 1000` 改成 `poller.Wait -1` 后，只在 socket 可读或 eventfd 被写时醒来。`AsyncPathLink.CommitFrame` 只递增 `txHead`，不写任何 fd；而一个只有待发数据的 TCP socket 不会产生读事件，`PumpTx` 永远不执行，bootstrap 帧发不出去，握手死锁。

修复：

```fsharp
// AsyncPollerGroup
member _.WakeThread(index: int) =
    let i = ((index % width) + width) % width
    if threadWakes.[i] >= 0 then
        Native.write threadWakes.[i] (ReadOnlySpan<byte> wakeToken) |> ignore

// AsyncPathLink.CommitFrame
let head = Volatile.Read(&txHead)
let idle = head = Volatile.Read(&txTail)
txLen.[head % slotCount] <- Volatile.Read(&frameLength)
Volatile.Write(&txHead, head + 1)
if idle then group.WakeThread(group.ThreadFor pathId)
```

只在队列从空变非空时写一次 eventfd，批量提交不会每帧一次系统调用。

## 验证

- `dotnet build Tbt.slnx -c Release -p:PublishAot=false` → 0 warning / 0 error，47 s。
- `Tbt.Session.Tests` 239/239，8.2 s；`Tbt.Async.Tests` 8/8，218 ms。
- AOT `eb2b9cc8…` 部署本地后启动日志出现 async 行。
- 修复版 AOT `feea9b05…`：构建 0 error，本地待验证；HND0 需要重新传输。

## accept 读门控（第二个回归，已修）

现象：本地客户端与服务端的 TCP 连接全部建立，但对端很快进入 `CLOSE-WAIT`；服务端 `sessions=0`、
`recv_calls=0`，代理超时。

根因在 `Server.run` 的两个细节：

```fsharp
// 旧：servicePending 依赖 poller 的就绪表
if poller.IsReadable link.Socket then
    let r = link.Receive()

// 旧：Wait 超时只看 rxPending / busy / sessions.Count
poller.Wait(if rxPending then 0 elif busy then 1000 elif sessions.Count > 0 then 10_000 else 100_000)
```

1. 新 accept 的 socket 是在本轮 `Wait` 之后才加入 `pending` 的，本轮还没 `WatchRead` 它，
   `IsReadable` 为假；下一轮注册与 `stale` 清空的时序稍有偏差，读就会持续失败。
2. `Wait` 的超时没有考虑 `pending.Count`。已有 pending socket 时仍可能等 10/100 秒，
   bootstrap head 在这段时间里读不到，15 秒后 `Pending` 超时 `link.Close()`，对端看到的就是 `CLOSE-WAIT`。

修复：

```fsharp
// servicePending：直接读，WouldBlock 无害
match link.Receive() with
| IoResult.Progress -> ...
| IoResult.WouldBlock -> ()
| _ -> failed <- true

// Wait：只要还有 pending socket 就非阻塞轮转
poller.Wait(
    if rxPending || pending.Count > 0 then 0
    elif busy then 1000
    elif sessions.Count > 0 then 10_000
    else 100_000)
```

`PathLink.Receive` 的语义已核对：`WouldBlock` 返回 `WouldBlock`，只有 `n = 0` 或真实错误才是
`Closed`，所以去掉门控不会把半包误判为断链。

## 异步服务端的待查问题（未闭环）

两端同为 `d1fcac35…` 且都开异步时，HND0 `sessions=0`、`frames_in=0`、代理 `000`；
而“本地异步客户端 + HND0 同步服务端”曾稳定 `204`。因此问题在**异步服务端路径**，
不在客户端。目前尚未定位到具体行。

隔离方法（每次只改一个变量）：

| 本地客户端 | HND0 服务端 | 预期 |
|---|---|---|
| 同步（drop-in） | 同步（drop-in） | 已确认 204，生产回退组合 |
| 异步 | 同步 | 曾确认 204（`d1fcac35` + 同步 drop-in） |
| 同步 | 异步 | 待测；若失败即可锁定服务端 |
| 异步 | 异步 | 待测 |

下一步的诊断点：在 `classify` 入口记录 `TryTakeHead` / `TryTakeFrame` 的返回值、`pending` 数量、
本轮 `Wait` 的毫秒数，跑 20 秒即可区分“没读到 head”与“读到但 coordinator 创建失败”。

## 部署与回退

- HND0 的 SSH 上行只有 5–9 KB/s。传 1.58 MB 的 xz 必须走隧道（`/tmp/connect.py` 作为
  `scp -o ProxyCommand=...`），或先删远端旧文件再分块 `cat >>`；**不要**在截断的压缩流上用
  `rsync --inplace`，块结构混合会拼出长度正确但内容损坏的文件。
- `install` 前必须 `xz -dc | sha256sum` 与本地 AOT 一致；空文件事故的 sha 是 `e3b0c442…`。
- 两端 drop-in 可随时重建：`Environment=TBTUN_ASYNC_MODE=0`，`daemon-reload && restart`。
- 同步回退保留，用于对照与故障定位。
