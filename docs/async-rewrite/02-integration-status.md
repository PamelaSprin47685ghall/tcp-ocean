# 异步层集成状态

更新：2026-11-18

> 本页是 2026-11-18 的早期集成快照，不是现在的配置。生产 TCP 会话后来默认改为异步；固定 1 ms 的 IO 线程等待和旧的 `AsyncDecoder` / `AsyncEncoder` 已移除。见[默认异步与等待](12-event-driven-default.md)。TUN 仍由共享线程服务。

## 当时结论

`src/Tbt.Async` 与 `src/Tbt.Carrier/AsyncPaths.fs` 一起构成**全异步多线程路径 IO**：
每个会话一个线程独占 `Session` 状态机，路径 socket 的 `recv` / `send` 与帧边界切分
交给一组 IO 线程。同步模式仍是默认路径，`TBTUN_ASYNC_MODE` 是开关。

## 线程模型

```
[IO 线程 0..3]  --RxChannel-->  [会话线程]  --每路径 tx 槽位-->  [IO 线程 0..3]
   epoll over its paths            Session.OnFrame / TryProduce
                                   FlowBridge / TUN / Tick
```

- IO 线程：每个线程一个 `Poller`，只做 `WatchRead` / `Wait(1ms)` / `PumpRx` / `PumpTx`，
  不接触 `Session`、`Decoder`、`Encoder`、`FlowBridge`。
- 会话线程：唯一的 `Session` 所有者。每轮先 `rx.Drain carrier`（逐帧 `Session.OnFrame`），
  再 `carrier.Watch → poller.Wait → clock.Tick → BeforeService → carrier.Service`。
- 唤醒：每个 `RxChannel` 有自己的 `eventfd`，IO 线程入队后写；会话线程
  `IsFdReadable` 后 `ClearWake`。共享一个 eventfd 会在两个会话线程抢计数时丢掉唤醒。

## 已落地

- `src/Tbt.Async` 在 `-p:PublishAot=false` 下零错误编译，`TreatWarningsAsErrors` 保持开启。
- `FrameAdapter`：真实调用 `RxHalf.Open`（帧长度检查 + AEAD + `Meta.tryRead`）与
  `Session.OnFrame`（DATA / PARITY / BOOSTER / WIN_ACK / HEARTBEAT / CTRL / FLOW_STATE
  分派）。坏帧计数直接读 `RxHalf.Counters`。
- `AsyncDecoder` / `AsyncEncoder`：用 `Monitor` 串行化真实 `Decoder` 与
  `Session.TryBeginSourceFor` / `CommitSource` / `Flush`。没有复制或伪造编解码状态。
- `AsyncPathLink`：`TakeOver` 接管同步 `PathLink` 的 socket 与已读缓冲；rx 64 KiB
  线性缓冲 + 逐帧拷贝入池化 `RxFrame`；tx 16 个槽位（每槽
  `max 2048 Layout.BootFrameLength` 字节），`CommitFrame` 只发布头指针，IO 线程发送
  并处理部分写。
- `AsyncPollerGroup`：每线程一个 `Poller` 与路径列表，`pending` 队列接收新路径，
  线程循环 `pickup → 清理 closed → WatchForIo → Wait 1ms → PumpRx → PumpTx`。
- `SessionCarrier.AddPath`：先 `paths.Add → path.Attach → group.Add`，保证 IO 线程
  拿到路径时 `Attach` 已排空引导阶段读到的整帧。
- `Describe`：`AsyncPathLink` 的 `cons_tx_b` 与 `path_tx` 已计入。
- 内存：每路径约 `64 KiB + 16 × 2 KiB ≈ 96 KiB`；256 路径 ≈ 24 MiB/会话。

## 明确未做

- **不拆分 `Decoder` / `Encoder` / `FlowBridge`。** 它们共享 `Session` 状态；并行化会
  改变 `AGENTS.md` 列为敏感的默认（写门控、信用会计、修复时序），需要官方矩阵对照。
- **TUN 模式保持同步单线程。** `TunStack` 是共享对象；开关只在 kernel 路径生效。
- **不声明吞吐提升。** 会话级并行把多个会话从一条主循环上分开；单会话吞吐的第一约束
  仍是窗口与在途唯一数据。异步路径 IO 的收益是 256 路 socket 的 `recv` / `send`
  不再占用会话线程。

## 验收方式

- 编译：`dotnet build src/Tbt.Cli/Tbt.Cli.fsproj -c Release -p:PublishAot=false`
- 单测：`dotnet test tests/Tbt.Tests/Tbt.Tests.fsproj -c Release`
- 端到端：`TBTUN_ASYNC_MODE=true tests/e2e/run.sh ...`，日志出现
  `async mode: ...` 与 `session ... on an async coordinator thread`。

## 与旧文档的差异

旧版本把这一层写成 80%/100% 完成，并声称核心基础设施已全部实现。该描述不成立：
旧代码引用不存在的 `Cell`、`Meta.Default`、`Decoder.OfferData`，且
`AsyncSessionCoordinator.create` 用 `Unchecked.defaultof<Encoder/Decoder>` 构造，
启动即失败。本文只记录当前代码可以证明的事实。
