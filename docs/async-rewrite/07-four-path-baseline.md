# 四路异步基线（2026-11-18）

## 二进制

- 服务端 AOT：`/tmp/tbt-bin/aot/tbtun`，sha256 `e9dd5974254b3207dffbd12485ce3ba60db065905c49568738714b03aeba906a`
- 客户端 JIT：`/tmp/tbt-bin/jit/tbtun.dll`，应用宿主 `/tmp/tbt-bin/jit/tbtun`
- 构建：`dotnet publish src/Tbt.Cli -c Release -r linux-x64 -p:NuGetAudit=false -p:SuppressTrimAnalysisWarnings=true`；JIT 用 `dotnet build -c Release -p:NuGetAudit=false`
- 复用：`TBT_BIN=/tmp/tbt-bin`，`run.sh` 跳过 publish

## 命令

```
TBT_BIN=/tmp/tbt-bin TBTUN_ASYNC_MODE=true ALLOW_DIRTY=1 HOST_QUIET_BYPASS=1 \
  tests/e2e/run.sh 20 200 0 4 10
```

## 结果

| 项 | 值 |
|---|---|
| 握手 | `session_up(mux) 9.10s`，`handshake 9.11s (limit 10s)` |
| 协商 | `paths=4 window=1024 window_clamp=600000` |
| echo 8 流 | `ok=8/8`，`1.21s`，`106.1 Mbit/s`（复跑 `0.58s`，`222.4`） |
| echo 64 流 | `ok=64/64`，`0.38s`，`54.5 Mbit/s` |
| ping | `200/200`，p50 41.0 ms，p99 41.7，max 41.8 |
| bulk up | `144.3 Mbit/s`（183435264 B / 10.2 s） |
| bulk down | `144.2` / `144.0 Mbit/s` |
| ping-under-bulk | `200/200`，p50 50.4，p99 53.5 |
| plain TCP 基线 | 1 flow 188.4；4 flows 189.6 |
| 服务端 RSS | `75616 KiB`，peak 75616，`cpu_s=8.80` |
| 客户端 RSS | `127064 KiB` |

## 服务端统计（每秒窗口）

- `csvc_us`：398 / 409 / 411 / 432 / 444 / 454
- `iter_us_max`：52 / 58 / 59 / 66 / 67 / 87

## 客户端最终统计

```
frames_in=442837 frames_out=197720 srtt_ms=68 minrtt_ms=40 rtt_skipped=140
boosters=289 recovered=253 cons_tx_b=279728520
path_tx=0:139796340,1:0,2:139932180,3:0
flows=0 opened=85 closed=85 local_fail=1 stale=897
```

## P1 判定

| 项 | 验收 | 实测 | 结论 |
|---|---|---|---|
| P1-7 SRTT 不变式 | `SrttNs >= MinRttNs` | 服务端 51.1 / 63.7 / 79.0 对 min 40.1；客户端 68 对 40 | 成立 |
| P1-7 `rtt_skipped` 增速 | < 1/秒 | 140 / 约 105 s ≈ 1.33/秒 | 略超 |
| P1-8 FEC 恢复率 | < 10% | 253 / 442837 = 0.057% | 达标 |
| P1-9 `csvc_us` p99 | < 100 ms | 最大 454 µs | 达标 |
| P1-5 单流稳定性 | 5×15 s 标准差 < 20% | 未跑 | 待测 |
| P1-6 多流 | 3 流 ≥ 1.5× 单流 | 未跑 | 待测 |

## 观察：path 1/3 闲置

- 客户端 `path_tx=0:139796340,1:0,2:139932180,3:0`：只有 path 0/2 承载。
- 服务端对应 `acc_b=0`（path 1/3），`peer_wnd=1296`（path 0/2 为 `1048560`），`inflight=0`，`phase=startup`。
- `Session.TryProduce` 本身无路径级窗口门控，只查 `pathState`、`tx.CanSeal`、`tx.TryRenderNext`；嫌疑集中在 `WriteGate.admits` 的 `probe.NotSent` 判定、`writeBatch` 的 `next` 轮转预算，或 `tx.TryRenderNext` 的 per-path 分支。
- 影响：4 路实际只用 2 路，`bulk` 144.3 Mbit/s 对 plain TCP 189.6 的缺口可能与此相关。
- 下一步：在 `writeBatch` 临时打印每 pass 的 `(pathId, notSent, admits, TryProduce)`，或对 `Session.TryProduce` 做 4 路径轮流调用的单测。

## 未验证

- 256 路握手（bg_28 在跑）。
- P1-5 / P1-6 矩阵。
- P2-10 SSE2 A/B：HND0 无 dotnet，且 AOT 二进制不受 `DOTNET_EnableHWIntrinsic` 影响。
- P2-11 `Tbt.Tests` 编译计时。
