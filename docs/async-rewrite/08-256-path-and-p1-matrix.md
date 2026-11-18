# 256 路握手与 P1 矩阵现状（2026-11-18）

## 256 路异步握手（P0-2 达成）

命令：

```
TBT_BIN=/tmp/tbt-bin MAX_PATHS=256 HANDSHAKE_S=120 TBTUN_ASYNC_MODE=true \
  ALLOW_DIRTY=1 HOST_QUIET_BYPASS=1 SKIP_ECHO=1 SKIP_IPERF=1 SKIP_REF=1 \
  timeout 340 tests/e2e/run.sh 20 200 0 256 10
```

结果：

- `session_up(mux): 9.26s`
- `handshake(mux): 9.27s (limit 120s)`
- `negotiated(mux): paths=256 window=1024 window_clamp=600000`
- `server(mux): rss_kib=46188 peak_rss_kib=46188 cpu_s=0.23`
- 日志未出现 `session close`：SKIP_* 组合下的收尾分支未打印，或 `timeout 340` 在收尾时截断。握手与协商本身已达成验收。

内存：256 路径与会话约 45 MiB，与「每路径 64 KiB rx + 16×2 KiB tx ≈ 96 KiB × 256 ≈ 24 MiB + 基础约 20 MiB」的估算吻合，符合「内存受控」。

## P1 矩阵未产生样本

```
TBT_BIN=/tmp/tbt-bin TBTUN_ASYNC_MODE=true ALLOW_DIRTY=1 HOST_QUIET_BYPASS=1 \
  tests/e2e/p1-matrix.sh 5 15
```

返回 rc=0，但输出：

```
single-flow up: no samples
single-flow down: no samples
P1-5 target: standard deviation at or above 20%
```

日志仅 104 B，且从启动到返回不足一分钟，说明 5 轮 `paths=1` 与 1 轮 `paths=3` 并未真正执行（脚本提前退出，或解析路径与 `run.sh` 输出不匹配）。

下一步：读 `tests/e2e/p1-matrix.sh` 的参数解析、`TBT_BIN` 校验、每轮 `run.sh` 调用与 goodput 抓取逻辑。

## path 1/3 闲置：静态定位结果

已排除：

- `Session.TryProduce`（`Session.fs:656`）无路径级窗口门控；`pathState` 由 `Array.zeroCreate` 初始化，而 `PathCloseState.Idle = 0`；`NotePathJoined` 把非 `Closing` 的 id 置回 `Idle`。
- `TxHalf.TryRenderNext`（`TxHalf.fs:637`）无 per-path 分支；`path` 只用于 trace、`carrierWm` 与 `Seal`。
- `AsyncPathLink.frameLength` 不是根因：`SessionCarrier.AddPath`（`SessionCarrier.fs:363`）在 `TakeOver` 之前执行 `link.FrameLength <- session.FrameLength`，`TakeOver` 继承该值；`BeginFrame` 用它决定槽位 span。

剩余唯一嫌疑：`writeBatch` 的 pass 语义（`SessionCarrier.fs:148`）：

```fsharp
for pass in 1 .. 2 do
  if pass = 1 || not progress then
```

`progress` 在 pass 1 任一路径成功后置 true，pass 2 因此永不执行。pass 1 只接受 `NotSent = 0`；若 path 1/3 的 `probe.NotSent` 返回 `-1`（探针不可用），它们只在 pass 2 被接受，而 pass 2 被 path 0/2 的持续进展屏蔽。

候选最小修复（属敏感默认，需单测与矩阵对照，暂不改）：

1. `WriteGate.admits` 的 pass 1 接受 `notSent <= 0`，把探针不可用的 `-1` 与 0 同等视为空闲。
2. 每轮固定跑一次 pass 2，遍历时跳过 pass 1 已成功的路径。
3. `AsyncPathLink.UnsentBytes` 在探针失败时返回 0 而不是 -1。

验证手段：临时在 `writeBatch` 打印 `(pass, pathId, notSent, admits, TryProduce)`，跑 4 路 5 s 即可定论。

## 已知未验证

- P1-5 单流 5×15 s 标准差。
- P1-6 3 流并发倍率。
- P2-10 SSE2 A/B（HND0 无 dotnet；AOT 不受 `DOTNET_EnableHWIntrinsic` 影响）。
- P2-11 `Tbt.Tests` 编译计时。
