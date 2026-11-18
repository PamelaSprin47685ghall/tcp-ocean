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

## 待办

- HND0 需要部署 `feea9b05…` 并把 `TBTUN_ASYNC_MODE` 的临时回退 drop-in 删除，两端才都在异步默认下运行。
- 传输必须分块、先删远端旧文件、传完 `xz -dc | sha256sum` 校验后才 install。
- 同步回退保留，用于对照与故障定位。
