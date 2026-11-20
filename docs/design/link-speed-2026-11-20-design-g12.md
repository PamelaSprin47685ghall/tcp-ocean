# 第十二代设计：下行在途上限的机制树与判定

> 本文件只回答一个问题：为什么下行有效在途窗口稳定在 200–285 KB，而 8 MiB 单流信用、
> 1024 步编码窗口与 2515 步 Grant 都远大于它。它不重复前代已证伪的假设，只列出
> 作用点、判据与最小修复面。代码以磁盘为准（HEAD `e0569bb` + 工作树）。

## 1 已确定的事实（不再重复验证）

| 事实 | 证据 |
|---|---|
| 下行吞吐严格 ∝ 1/RTT | 同一构建 256 路 16 MiB：5 ms → 3.358 s、20 ms → 11.305 s、40 ms → 21.909 s；步/秒 × RTT = 2.23 / 3.20 / 3.28 |
| 信用不是限制 | `C0=65,536`、`FlowWindowMax=8<<<20`、`PoolBytes=32<<<20`；单流 `window = min(8 MiB, c0 + sharedPool)` = 8 MiB；生产 `RxLimit=8,388,608` |
| Grant 不是限制 | 服务端 `grant_limit` 2515、`peer_ack` 1811、`Head` 1814，`CanFill` 三项全真 |
| 队列不是限制 | `queueHigh` 恒 64–65，`queueLimit` 256；`queued=0` |
| 等待粒度不是限制 | 非 pace 等待压到 1 ms：11.308 s vs 基线 11.305 s |
| 接收前沿 20 ms 门控不是限制 | `stallNs` 20 ms → 2 ms：11.310 s vs 11.305 s |
| 承载丢包不是限制 | netem rate 1000 mbit 时 boosters 从 32% 降到 9.3%，吞吐不变 |
| pace 不是限制 | 开/关差 0.01%（22.3896 vs 22.3877 s） |
| flow rcvbuf 是必要条件 | `SocketTuning.local` 设 4 MB 后 11.31 s → 2.573 s，4.4× |

## 2 显式上限清单（代码位置）

| 上限 | 值 | 位置 | 是否已被证伪为限制 |
|---|---|---|---|
| 每轮读入预算 | 4096 **包** | `FlowBridge.fs:117` `budget = 4096` | 未达（每轮实际 11–16 包） |
| 每轮写帧预算 | 256 帧 | `SessionCarrier.fs:287` `writeRound` | 未达（每轮约 15–41 帧） |
| 队列上限 | 256 cells | `Session.fs` `queueLimit` | 否（qhigh 65） |
| 编码容量 | `--window` 步 | `Encoder` | 未测（`--window 4096` 未跑） |
| 单步源数 | 32 | `CodeGraph.SlotsPerStep` | 未达（步内 11–16） |
| 封步时机 | 每轮输入结束 | `TxHalf.Flush`：`if filled > 0 then seal filled true` | **步大小 = 每轮读入源数** |
| 单流信用 | 8 MiB | `Flows.fs:133` | 否（RxLimit 8 MiB） |
| 承载接收窗口 | 600,000 B/路 | `SocketTuning.path` `WindowClamp` | 否（聚合 153 MB） |

## 3 机制树：吞吐 = 每轮读入源数 × 轮频 × 每源字节

`writeRound` 每轮最多 256 帧；只要 `lastRoundSent >= 256`，`WaitUs` 立即返回 0 继续。
因此稳态吞吐不受写轮预算约束，而由「每轮能读到多少源」与「轮与轮之间等多久」决定。
每轮 `FlowBridge.Read` 的退出只有三条路径：

```
while go && budget > 0 && session.FlowSending slot do
    if session.FlowRoom slot > 0 && session.TryBeginSourceFor(slot, &app) then
        n = socket.Receive(...)
        if WouldBlock then go <- false        // (A) socket 空
        elif err then ...                      // (B) 硬错误
        elif n = 0 then ...                    // (C) FIN
        else CommitSource; budget <- budget - 1
    else go <- false                           // (D) 信用或 CanFill 拒绝
```

(A) 与 (D) 是仅有的两种「读得少」解释。(D) 已被 §1 排除，只剩 (A)：每轮 socket 缓冲区里
只有约 11–16 个 MTU。于是剩下三个命题。

### P1 通告窗口小（本端 SO_RCVBUF 未按预期生效）

代码请求 4,000,000 B，Linux 加倍后应得 `sk_rcvbuf = 8,000,000`，通告窗口同量级。
若 `ss -tinm` 在服务端 flow socket 上读到 `rcv_space`/`skmem r` 只有 60–130 KB，
则设置未生效。可能原因：设置在 `connect` 之后（错）、`tcp_moderate_rcvbuf` 覆盖、
或请求值被 `rmem_max` 截断后又被 `tcp_adv_win_scale` 再折半。

**判据**：`ss -tinmH` 的 `rcv_space` ≥ 1 MB 且 `skmem r` ≈ 8,000,000 ⇒ P1 证伪。

### P2 轮频 = 1/RTT（每轮只取一个 RTT 的到达量）

若每轮 Read 只调用一次（`readCalls` 增量 ÷ 秒 ≈ 步/秒），且 WouldBlock 占绝大多数，
则每轮取走的是「上一轮之后一个 RTT 内到达的数据」。此时吞吐 = 到达率，而到达率
受 target 的 cwnd/rwnd 与本端读取节奏共同塑形，表现为 ∝ 1/RTT。

**判据**：`flowread` 增量中 `readCalls/秒` ÷ `步/秒` ≈ 1 且 `wouldBlock/readCalls` > 95%
⇒ P2 成立；若 `readCalls/秒` 远大于步/秒（每步多次 Read），则轮频不是 1/RTT。

### P3 每步源数受限（封步过早）

`Flush` 每轮封一个部分步。若一轮只能读 11–16 源，步就固定在 11–16 源（约 16–24 KB），
每步还要付 parity（rho=0.03 时约 0.5 cells）与 grant 记账。步频 × 步字节 = 吞吐。

**判据**：增大 `--window` 不改变吞吐 ⇒ 编码容量不是限制，P3 只是 P2 的表现形式；
若 `readBytes/readCalls` 恒定 ~24 KB 而 `readCalls/秒` 很低，则 P2 为主因。

## 4 修复候选（按命题，单变量）

| 命题 | 最小修复 | 触及面 | 回滚 |
|---|---|---|---|
| P1 | 把 `SocketTuning.local` 的 SO_RCVBUF 设置移到 `connect` 之前并在日志打印 `getsockopt` 有效值；必要时显式设 `TCP_WINDOW_CLAMP` 或 `tcp_rmem` 上限 | 仅 flow socket 建立顺序 | 恢复一行 |
| P2 | 在 `readPass` 中对同一 slot 连续 Read 直到 WouldBlock 或 `--window` 填满（当前已如此），改为**先写后读**：每轮 `writeRound` 后立即再跑一次 `readPass`，或在 `WouldBlock` 后把该 slot 重新 arm 到 `poller` 的可读事件而非等 `NextTimerUs` | `SessionCarrier` 轮序 | 恢复轮序 |
| P3 | 把 `Flush` 的「每轮必封」改为「步内源数 ≥ 阈值或本轮无更多可读时才封」，让一步跨多轮累积到 32 源 | `TxHalf.Flush` 调用点 | 恢复调用条件 |

三项都不改协议、信用语义、编码参数、路径数与 FEC 默认。

## 5 CASE 映射

- **CASE-1**（中位）：P1/P2/P3 任一修复后，同 URL 好轮中位 ≥ 基线 1.5×。
- **CASE-3**（RTT）：修复不得把 `srtt` 抬过 1.6× minRTT；P2 的「立即再跑」若造成自排队即判失败。
- **CASE-9**（前沿/信用）：`rx_ack` 步/秒与交付字节率偏差 ≤20%；`flow_credit` 余量 ≥1 MiB。
- **CASE-11**（内存）：P1 的 8 MB/流 × 并发流数须有上界，单流 RSS ≤16 MB。
- **CASE-12**（停摆）：修复不改变停摆率定义；停摆轮单列，不进中位。
- **CASE-6**（矩阵）：4 路无丢包 bulk 与 iperf 不退化；`--window` 默认不变。

## 6 失败隔离与契约

- 每个候选只改一个变量；同一批不夹带敏感默认（`GatePass2Limit`、`roundCap`、FEC 默认）。
- 回环床先验证（`unshare -rn` + netem，无需 sudo，不碰生产与 mihomo）。
- 生产部署前备份 + sha256；部署后 30 s 内必须见 256 路，否则 60 s 内回滚。
- 服务端改动只能经 17893 隧道 HTTP 上传（CASE-0 步骤 4c），上传后 `sha256sum` 必须相符。
- 测量单轮 ≤15 s、单条命令 ≤60 s；原始目录保留。

## 7 残余风险

1. 三个命题可能同时成立一部分；届时按「先 P1（最便宜、可静态确认）→ 再 P3（单点）→ 最后 P2（轮序）」顺序做，不并批。
2. P2 的「立即再跑」会提高空转 CPU；必须以 `rx_ack`/`cpu` 双指标判，不能只看吞吐。
3. 生产时段停摆率 5/10 属 mihomo 层，不在本设计修复面；任何生产判定必须先把停摆轮单列。
4. 回环床的 target 是 python http.server，单线程；若 P2 修复后床内吞吐受 target 限制，需换 `iperf3 -s` 或 `nginx` 复测，且必须在结果文档声明更换。

## 8 判定结果（第十二代实测，2026-11-20 13:29 UTC）

床上 16 MiB 为 2.568 s / 6.53 MB/s，与 `--window`（1024 对 4096）和 `--paths`
（256 对 1）均无关，差 <1%；flow socket `rb=8,000,000`。P1、P2、P3 全部证伪。
`stallNs 20→2 ms` 使 `rx_ack` 步率从 42.4 升到 57.6 步/s（1.36×），
但 10 轮生产 A/B 的好轮中位 0.605 MB/s、停摆 3/10，均无改善，已回滚。
长窗口三分账显示 `RxDelivered` 121.4 MB 对 curl 47.7 MB，积压在 mihomo 层；
该层被用户禁止实验，只记录。**在现有约束下，tbtun 侧没有可验证的吞吐修复面。**
后继入口：CASE-13 单路半开检测、CASE-8 慢消费长尾、CASE-2 同窗口重传重测。
