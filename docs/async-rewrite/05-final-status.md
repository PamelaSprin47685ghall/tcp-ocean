# 异步层最终状态

更新：2026-11-18

旧版本把这一层写成「100% 实现、剩余编译修复」。那份描述不成立：旧代码引用不存在的
类型，`AsyncSessionCoordinator.create` 用 `Unchecked.defaultof<Encoder/Decoder>` 构造。
本文只记录可复现的事实。

## 交付清单

| 项 | 状态 | 证据 |
|---|---|---|
| `Tbt.Async` 编译 | 通过 | `dotnet build src/Tbt.Async/Tbt.Async.fsproj -p:PublishAot=false` → `Build succeeded`，0 warning |
| `Tbt.Cli` 编译 | 通过 | `dotnet build src/Tbt.Cli/Tbt.Cli.fsproj -c Release -p:PublishAot=false` → 0 warning |
| 生产入口 | 已接 | `Tbt.Cli.fsproj` 引用 `Tbt.Async`；`AsyncMode.fs` 进入 Compile；`Server.run` / `Client.run` 读取 `TBTUN_ASYNC_MODE` |
| 真实帧解析 | 已接 | `FrameAdapter.tryParse` → `RxHalf.Open`；`FrameAdapter.dispatch` → `Session.OnFrame` |
| 路径 IO 线程池 | 已接 | `AsyncPollerGroup` 4 线程；`AsyncPathLink` 独占 socket 与帧切分 |
| 会话状态机 | 单线程 | `AsyncSessionCoordinator` 独占 `Session`，经 `RxChannel` 收帧 |
| 无锁结构单测 | 已加入 | `tests/Tbt.Tests/AsyncTests.fs`（100 万次 SPSC、四窃取者、优先级、20k 任务、BufferPool、帧类型） |
| 256 路会话 | 待端到端记录 | `MAX_PATHS=256` + `TBTUN_ASYNC_MODE=true` |

## 性能立场

异步层不声明吞吐提升。它把路径 socket 的收发从会话线程移到 IO 线程，并把多个会话从
同一条主循环上分开；单会话的数据面仍是 `SessionCarrier.Service`，编码、窗口与修复
行为不变。

单流吞吐的首要约束是会话窗口（`--window`，默认 1024 步）与在途唯一数据，不是线程数。
要突破这一点，必须改编码/窗口/路径使用方式，那属于 `AGENTS.md` 列为敏感的默认，
需要官方矩阵对照。

## 已知约束

- `-p:PublishAot=false` 是构建本仓库测试与异步工程的既有约束。NativeAOT 发布路径与
  `TreatWarningsAsErrors` 下的 NU1903（`Microsoft.DotNet.ILCompiler` 10.0.12 传递包
  漏洞告警）会中断构建。产品二进制仍走 AOT；e2e 的 AOT 发布显式加
  `-p:NuGetAudit=false`，只影响测试工具自身的构建，不改产品的审计设置。
- `TBTUN_ASYNC_MODE` 只认 `1` / `true` / `on` / `enabled`（大小写不敏感）。其它值、
  包括拼写错误，保持同步路径。
- 异步模式在 `--tun` 下不生效：`TunStack` 是共享对象，不能由多个会话线程同时驱动。

## 回退

`unset TBTUN_ASYNC_MODE` 后重启即回到同步主循环，无需改代码或换二进制。

## 验证命令

```sh
dotnet build src/Tbt.Async/Tbt.Async.fsproj -p:PublishAot=false
dotnet build src/Tbt.Cli/Tbt.Cli.fsproj -c Release -p:PublishAot=false
dotnet test tests/Tbt.Tests/Tbt.Tests.fsproj -c Release
```

端到端：

```sh
TBTUN_ASYNC_MODE=true MAX_PATHS=256 SKIP_ECHO=1 SKIP_IPERF=1 \
  tests/e2e/run.sh 20 200 0 8 10
```

日志应包含 `async mode: ...` 与 `session ... on an async coordinator thread`。
