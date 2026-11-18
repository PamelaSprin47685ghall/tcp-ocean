# 写闸门阈值与一次空二进制部署事故（2026-11-18）

## 1. 为什么要改阈值

真实链路（310 ms RTT、256 条承载 TCP）下行长期停在 15–16 Mbit/s，而 HND0 直连
proof.ovh 是 125 Mbit/s。诊断计数把范围一步步缩小：

- `gate1` 每秒只通过约 508 次，`iter_n = 96` 轮/秒 → 每轮约 5 条路径被放行；
- `gate2` 每轮再补约 4 条；
- `next` 每轮 +1，256 条路径要 16 轮才轮一遍；
- 每路径每批最多 4 帧（Q-3）→ `6 帧/s/路径 × 256 ≈ 1536 帧/s`，与实测
  `frames_out ≈ 1578/s` 完全吻合。

`WriteGate.admits` 的 pass 1 只接受 `notSent = 0`。在 256 条长 RTT 路径上，
几乎每条路径总有几百字节未发送，于是 pass 1 每秒只放行个位数路径，其余全部
等轮转。pass 2 的 16 KiB 阈值也只有 BDP（7.75 MB）的 0.2%。

这不是吞吐目标与策略的冲突，而是阈值按单路径 40 ms 测试床标定、没有随路径数
与 RTT 缩放。

## 2. 改动

`src/Tbt.Carrier/SocketTuning.fs`：

```fsharp
[<Literal>]
let GatePass1Limit = 4096      // 4 KiB
[<Literal>]
let GatePass2Limit = 262144    // 256 KiB，约为 310 ms × 200 Mbit/s BDP 的 3 %
```

```fsharp
let admits (pass: int) (notSent: int) =
    if pass <= 1 then notSent >= 0 && notSent <= SocketTuning.GatePass1Limit
    elif notSent < 0 then true
    else notSent <= SocketTuning.GatePass2Limit
```

- pass 1 保留 `-1`（探针不可用）拒绝语义，未知只在 pass 2 放行。
- `NotSentLimit = 16384` 仍是 `TCP_NOTSENT_LOWAT` 的取值，未改；socket 选项与
  闸门阈值从此分开。
- `SessionCarrier.writeBatch` 的 `gated` 触发逻辑不变：pass 1 任一 `notSent <> 0`
  即让 pass 2 运行，且只重访被门控的路径。

契约测试同步更新（`tests/Tbt.Tests/BridgeTests.fs`）：

- `write gate: first pass admits up to 4 KiB, second up to 256 KiB, unknown only on the second`
  —— 明确断言 4096 通过、4097 拒绝、262144 通过、262145 拒绝、`-1` 只在 pass 2 通过。
- `write batch prefers empty paths and only then those with notsent at most 256 KiB`
  —— 路径 `unsent = 300000` 才被拒绝，`8000` 由 pass 2 接收。

`dotnet test tests/Tbt.Session.Tests` → **239/239**（8.0 s）。

## 3. 一次空二进制部署事故

部署脚本在 `rsync` 尚未完成时就到 HND0 上解压 `/tmp/tbtun-gate.xz`。文件不存在，
`xz -dc` 输出空文件；脚本没有校验就 `install`，`/usr/local/bin/tbtun` 变成 0 字节
（sha256 `e3b0c442…`），`tbtun-hnd0` 反复重启，生产链路中断。

回滚：`systemctl stop` → `cp -a /usr/local/bin/tbtun.bak-gate /usr/local/bin/tbtun`
→ `systemctl start`。恢复后 sha256 `32b2f22b…`、`active`、NRestarts 0、本地 222 条
连接、代理 204。

铁律：

1. 解压后必须 `sha256sum` 与本地产物一致才 `install`；任何一步失败立即停止。
2. 不能在前一个传输任务未完成时开始远端安装。
3. `systemctl is-active` 返回 `activating` 按失败处理。
4. 回滚备份用 `cp -a` 保留，覆盖前先 `systemctl stop`（否则 `Text file busy`）。

## 4. 验证状态

- 本地：`889bd3f3…`，`active`。
- HND0：`32b2f22b…`，`active`（回滚后），新压缩包待校验。
- 下一步：确认 HND0 上 `/tmp/tbtun-gate.xz` 的 sha256 与本地一致，解压校验
  `889bd3f3…` 后再 `stop → install → start`，然后跑 20 秒下载读 `gate1` 增量。
