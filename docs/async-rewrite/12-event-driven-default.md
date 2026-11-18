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

## 验证

- `dotnet build src/Tbt.Cli -c Release -p:PublishAot=false` → 0 warning / 0 error，26 s。
- `Tbt.Session.Tests` 239/239，8.2 s；`Tbt.Async.Tests` 8/8，218 ms。
- AOT `eb2b9cc8…` 部署本地后：启动日志出现 async 行，`systemctl is-active` = `active`，代理 `204`，到 HND0 的连接 265 条。

## 待办

- HND0 仍是旧版；异步默认要在服务端也生效需要部署 `eb2b9cc8…`。
- 同步回退保留，用于对照与故障定位。
