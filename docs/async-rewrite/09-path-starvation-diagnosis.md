# 多路径发送饥饿：诊断记录（2026-11-18）

## 已落代码

`src/Tbt.Carrier/SessionCarrier.fs` 的 `writeBatch`：

- 新增 `batchNotSent: int[]`（512），缓存 pass 1 每条路径的 `UnsentBytes`。
- pass 2 的触发条件从 `if pass = 1 || not progress` 改为 `if pass = 1 || gated`；`gated` 在 pass 1 任一 `notSent <> 0` 时置真。
- pass 2 只重访 `notSent <> 0` 的路径，避免重复已成功的路径。

依据：`WriteGate.admits`（`SocketTuning.fs:123`）pass 1 只接受 `notSent = 0`，pass 2 接受 `notSent < 0` 或 `<= 16 KiB`。原实现中 pass 2 只在 pass 1 **完全无进展**时执行，多路径下只要有一条路径让 pass 1 有进展，其余 `notSent != 0` 的路径就永久饥饿。注释写的是「used when pass 1 is empty」，实现成了「pass 1 any progress」，与注释不符。

编译：`dotnet build src/Tbt.Cli/Tbt.Cli.fsproj -c Release -p:PublishAot=false` → 0 Warning，0 Error，27.63s。

二进制：`/tmp/tbt-bin/aot/tbtun` sha256 `072abf92835579e6fe71978f1421b1ea549be3c814136f4c4ffa8998fc398efa`；`/tmp/tbt-bin/jit/tbtun.dll` sha256 `4bf70c4ab156382927f64290f0f2464e085ce2c0bc788308d8af823e1d0b66e8`。

## 修复后的 4 路 e2e（`/tmp/tbtun-e2e.cCudee`）

命令：`TBT_BIN=/tmp/tbt-bin TBTUN_ASYNC_MODE=true ALLOW_DIRTY=1 HOST_QUIET_BYPASS=1 tests/e2e/run.sh 20 200 0 4 10`

| 项 | 值 |
|---|---|
| 握手 | `session_up(mux) 5.30s`（修复前 9.10s） |
| echo 8 流 | `8/8`，89.8 Mbit/s |
| echo 64 流 | `64/64`，63.1 Mbit/s |
| ping | `200/200`，p50 40.8，p99 41.4 |
| iperf up | 85.3 Mbit/s |
| iperf -R | 136.0 Mbit/s |
| iperf -P 4 | 84.8 Mbit/s |
| iperf -R -P 4 | 136.0 Mbit/s |

## 决定性证据：仍只有一条路径发送

```
client-mux : path_tx=0:0,2:0,1:184039145,3:0
client-bulk: path_tx=0:0,1:0,2:0,3:456300710
server-bulk: path_tx=（服务端是 TUN 模式，无 PathLink/AsyncPathLink，空属正常）
```

修复前是 `path_tx=0:139796340,1:0,2:139932180,3:0`；修复后变成单条（mux 为 path 1，bulk 为 path 3）。因此本次修复改变了被选中的路径，但没有解决饥饿本身。

## 已排除的候选

1. `Session.TryProduce`（`Session.fs:656`）无路径级窗口门控；`pathState` 由 `Array.zeroCreate` 初始化为 `Idle`（枚举值 0）；`NotePathJoined` 把非 `Closing` 的 id 置回 `Idle`。
2. `TxHalf.TryRenderNext`（`TxHalf.fs:637`）无 per-path 分支；`path` 只用于 trace、`carrierWm`、`Seal`。
3. `AsyncPathLink.frameLength`：`SessionCarrier.AddPath`（`SessionCarrier.fs:363`）在 `TakeOver` 之前执行 `link.FrameLength <- session.FrameLength`，`TakeOver` 继承。
4. `WriteGate` 的 pass 2 屏蔽：已修（见上），但饥饿仍在。
5. `UnsentBytes` 探针异常：`AsyncPathLink.UnsentBytes` 是 `try probe.NotSent socket with _ -> 0`，异常时归 0，不会拒绝。

## 剩余嫌疑（下一步按序验证）

1. **IO 线程没有真正处理这些路径**：`AsyncPollerGroup.pickup` 把新路径分到某个线程；若 `PumpTx` 未运行（未 `WatchForIo`、线程未启动、`Paths` 未被遍历），`txTail` 不前进，槽位累积后 `BeginFrame` 返回 false，而 `UnsentBytes` 仍为 0——恰好表现为「pass 1 通过、`TryBeginFrame` 失败」。
2. **`AsyncPathLink.BeginFrame` 的槽位条件**：`txHead - txTail < slotCount`；若 `txTail` 因 `PumpTx` 不运行而停滞，16 槽位很快耗尽。
3. **`Seal(frame, path)` 的 per-path 状态**：`TxHalf.Seal` 可能引用 `carrierWm.[int path]` 或按路径的 counter；需读实现确认。

## 诊断方法

在 `SessionCarrier.Describe` 的 `AsyncPathLink` 分支追加每路径：
`id@incarnation:alive:frameLength:txHead:txTail:unsent`

跑 4 路 5 s（`SKIP_ECHO=1 SKIP_IPERF=1` 或最小 iperf），对比发送路径与不发送路径的 `txHead`：

- `txHead = 0` → `CommitFrame` 从未被调用，问题在 `TryProduce`/`BeginFrame`。
- `txHead > 0 && txTail < txHead` → IO 线程没发，问题在 `PumpTx`/`WatchForIo`/线程分配。
- `txHead = txTail > 0` → 发了但内核拒绝（`UnsentBytes` 应 > 0），问题在门控。

## 附带观察（非 P1-6）

- 客户端 `cons_ack_b=0`：`AsyncPathLink` 分支没有 `TcpInfo`，`Describe` 的 `ackKnown` 在只有异步路径时为 false，`cons_ack_b` 显示 0。不影响数据面，但 `pace_*` 诊断会缺。
- `rtt_skipped`：mux 161、bulk 46；按约 100 s 计 1.6/秒与 0.46/秒。仍有一项高于 1/秒验收。
- `recovered`：mux 392/411031 = 0.095%，bulk 196/334623 = 0.059%，远低于 10%。
