# 链路提速：设计指南（2026-11-20）

## 0. 现状核对（第三代审计，2026-11-20 15:39）

本节固定「哪段代码在跑」，因为第 1 节的量化依赖它。

**生产二进制不是工作树。** `/usr/local/bin/tbtun` = `f2b63e00…`（3,963,944 B）；
`tbtun --version` 的服务端 usage 不含 `--pace`，客户端仍是 `--uplink-pace 0`。
因此 `f2b63e00` 早于本轮 `paceOn`/`--pace` 改动，也不含工作树的
`roundCap = max 256 (paths * 2)` 与 `GatePass2Limit = 32768`。
**第 1 节的「512 帧/轮」与「32 KiB 写门」是未部署代码，不是生产行为**；
与生产同代（HEAD `60b466c`）的代码是每轮 256 帧、`GatePass2Limit = 262144`。

**工作树的 21 个改动分三类，不可混为一谈。**

| 类别 | 改动 | 判定 |
|---|---|---|
| 并发安全 | 删除 `PathOp` 队列与 `PathOpsPending`，路径变更改在会话线程内联应用 | 真实缺陷修复：跨线程枚举 `paths` 会抛 `InvalidOperation_EnumFailedVersion`；与吞吐无关 |
| 吞吐默认 | `roundCap` 256→512、`GatePass2Limit` 256 KiB→32 KiB | `AGENTS.md` 列明的敏感默认；无单测、无矩阵对照，不能与 pace 同批上线 |
| 下行 pace | `uplinkPace`→`paceOn`，`Server.fs` 新增 `--pace`（默认 0） | 开关确实接通：`Server.fs:228` 为 `SessionCarrier(session, bridge, paceOn = pace, …)`，不是空开关 |

**故障窗口内的客户端观测（15:39）。** `rx_ack=1`、`rx_base=0`、`recovered=0`
（会话建立后下行一个 coded cell 都没解出）；`tx_head = peer_ack = 15`、`queued=0`；
`flow_credit` 只用 8433 B / 8 MiB；`frames_in` 每秒 +1~3。
因此本窗口不能用于判定 H1/H2：接收侧没有数据可解，发送侧没有数据可发。

**一个独立信号：`srtt_ms=1322`、`minrtt_ms=1207`，而物理 RTT 329 ms。**
`minrtt` 是会话内历史最小值，不会因后续卡死而回涨；会话建立 10 分钟后它仍是 1207 ms，
说明协议层 RTT 采样从第一秒起就被排队或重传污染。这与 HND0 是否卡死无关，
应在恢复后用 CASE-3 单独验证，不能当作故障窗口的产物忽略。

## 1. 症状与根因

生产下行在旧版回滚后实测中位 8–10 Mbit/s（同一 fast.com OCA，经隧道 15 s 轮），
早期记录的“约 2 Mbit/s”是重启前的低谷，不应作为基线；HND0 直连同一 OCA 实测
26,214,400 B / 0.955 s ≈ 27.4 MB/s ≈ 219 Mbit/s。前代记的 45–126 若取自 curl
`speed_download`，单位是 B/s 而不是 Mbit/s。同一窗口里，
HND0 侧 256 条承载 TCP 的重传率 19–23%，协议 SRTT 从空闲的约 320 ms 涨到约 950 ms，
解码前沿每秒只推进数步，流内已解出的数据被缺口拦住数秒；181 次缺口填补中 161 次靠
方程（FEC）恢复，说明丢包本身在制造缺口，而不是修复机制缺席。

代码把这些数字连成一条因果链：

1. `Server.fs:224` 构造会话承载时是
   `SessionCarrier(session, bridge, ?rxChannel = channel, ?asyncIo = ioGroup)`,
   没有传 `uplinkPace`。`SessionCarrier.fs:46` 的默认值是 `false`。
2. 于是下行 `writeBatch` 走的是 unpaced 分支（`SessionCarrier.fs:196-206`）：
   BOOSTER 与新数据都从同一个共享槽位发出，唯一约束是 `WriteGate`
   （pass1 4 KiB、pass2 256 KiB，`SocketTuning.fs:146-154`）和每路 32 个
   `AsyncPathLink` 槽位（约 42 KiB，`AsyncPaths.fs:212-222`）。
3. 每轮最多 512 帧（`roundCap = max 256 (paths * 2)`），256 路会同时放行，
   约 650 KiB 的聚合一瞬间进入 256 条互相独立拥塞控制的内核 TCP。
   每条 TCP 各自认为自己是唯一的流，于是聚合在途远超瓶颈 BDP，排队与尾丢同时出现。
4. 尾丢被内核 TCP 解释为拥塞：重传 19–23%，cwnd 反复减半；协议看到的交付率跌到
   2 Mbit/s，每步缺 cell，修复又要走 NACK 往返（约一个 400 ms RTT），于是每步的
   交付停顿被放大成秒级。

根因不是 FEC、不是编码参数、也不是 256 路本身，而是**同一会话的两个方向用了两套
互不相通的速率范式**：客户端上行有 `RateGroup`（聚合 1.25 BDP 在途、令牌桶、与
per-path 计量分离），服务端下行只有写门与槽位。`RateGroup` 头注释写的就是这个
场景（“paths of a session cross the same bottleneck; a per-path controller sees only
its own share, and the sum keeps a standing queue there”）。

### 1.1 接收前沿状态机（H2 的可检验模型）

`RxHalf` 把每个已认证的 coded cell 交给 `Decoder`，再由 `drainKnown` 把解出的源交给
`FlowTable`。前沿的可观测链条如下：

1. `Decoder.frontier` 只在 `StepRing.IsComplete frontier` 时推进；`IsComplete` 要求该步
   `k` 已知且 `0 .. k-1` 全部 known。缺一个 source 或 `k` 未知，前沿就停。
2. `Decoder.seenEnd` 是已打开的最高步加一；`baseStep` 只在 `Release` 时随前沿滑动。
   `RxHalf.Grant = BaseStep + capacitySteps - Frontier`，因此前沿停住时发送窗口只减不增。
3. 流级信用按交付前沿回收（`rx_ack` 的增量），不按到达字节。前沿停 ⇒ 8 MiB 窗口
   只出不进，服务端最终无窗口可发，表现为“发送队列空、新连接 0 字节”。
4. `serviceHold` 的放行分支只有四条：旧 parity 丢弃；`force`（`lossEvident`：所有
   活跃路径的 `pathStep > f`）；该 parity 在 hold 中已等 20 ms；以及前沿步
   `m.StepId = curF` 且（前沿已停 20 ms 或 `lossEvident`）。后两条都含 20 ms。

若每一步都缺至少一个 source（丢包 19–23% 时几乎必然），前沿步的 parity 最快要 20 ms
才被使用，于是前沿步进上限约 `1 / 20 ms = 50 步/s`。观察到的 20–60 步/s 与该上限
同量级，这是 H2 的第一条量化证据，但它只说明“存在天花板”，不单独证明天花板就是
当前吞吐的主因；必须同时看 `步/s × 每步字节` 是否低于交付字节率（CASE-9）。

20 ms 不是纯延迟。前代把 `m.StepId = curF` 改为无条件成立后，同一 URL 四轮全部失败
（TLS `unexpected eof`，连接 256→0），回滚即恢复。说明等待窗口同时承担同一路径内
到达序的证据作用：提前消费前沿步 parity 会让 decoder 在 source 与 parity 的到达序被
破坏时提前建立方程，改变 `seenEnd`/`baseStep` 与随后的 `OfferSource` 判定。
**该方向关闭。**

H2 的修复因此只能是“在该 parity 确实补 rank 时提前”，候选判据是
`decoder.StepUsed f > 0`（`k` 已知）且 `decoder.Deficit(f, 1) > 0`。该判据尚未实现，
本轮不承诺；先用 CASE-9 测出现状，再决定是否值得写。

三条命题与判定：

| 命题 | 观测 | 判定 |
|---|---|---|
| P1 前沿受限 | `rx_ack` 步/s 接近 50，且 `步/s × 每步字节 < 交付字节率`；`held > 0` | H2 成立，优先修接收 |
| P2 发送受限 | `tx_head - peer_ack` 接近窗口上限、`queued = 0`、步/s 与字节率一致 | H1/H3 至少其一 |
| P3 外层受限 | 直连/隧道同 URL 比值 > 5×、两端 `queued = 0`、`flow_credit` 充足 | H3 成立，先查 smux 与供数 |

观测字段：客户端 `rx_ack`、`rx_base`、`recovered`、`held`、`flow_credit`；服务端
`tx_head`、`peer_ack`、`queued`、`srtt_ms`、`cell_loss`。CASE-9 直接消费这张表。

### 1.2 发送侧已排除的三个量（2026-11-20 第三代）

本地 `unshare -rn` + `netem delay 20ms rate 200mbit` 上，256 路与 4 路各 32 MiB
都正好用 22.39 s（12.0 Mbit/s），`cell_loss=0`、`queued=0`。代码核对后，三个显式
上限都远大于实测的 3–4 步，因此都不是限制量：

| 量 | 位置 | 取值 | 实测对照 |
|---|---|---|---|
| Encoder 容量 | `Encoder.fs` `TryGetFillSlot`：`head - tail >= capacitySteps` 才拒绝 | `--window 1024` → 1024 步 | 在途 3 步 |
| 接收方 Grant | `RxHalf.fs:197-200`：`BaseStep + capacitySteps - Frontier` | `1491 + 1024 - 1811 ≈ 704` | 服务端 `grantLimit` 远大于 `Head` |
| 发送方 CanFill | `TxHalf.fs:291-294`：`Head + min 16 ((grantLimit - peerAck)/8) < grantLimit`、`HeldSteps < Capacity`、`queue.Count < queueLimit` | `queueLimit = TxQueueCells = 256` | 三项全部满足，仍只发 3 步 |

剩下的候选只有两个，且可用一个实验分开：同一构建、同一 256 路会话，只改
`netem delay` 为 10/20/40 ms。

- 若步/s 与 RTT 成反比（3 步/RTT），限制是**按 RTT 计数的定时器节拍**，
  即每轮只有一次发送机会，步数由确认往返决定。
- 若吞吐恒定 12 Mbit/s 不随 RTT 变，限制是**上游每轮读入量**（FlowBridge 从
  target socket 读入的字节数），与网络无关。

两个结果指向完全不同的修复面，因此这个扫描必须先于任何代码改动。

### 1.3 填充侧也已排除，指向 FlowBridge 的读节奏（2026-11-20 第三代）

先把丢包排除：把 netem 的 `rate` 从 200 Mbit/s 提到 1000 Mbit/s（整形队列不再丢包），
同一 256 路 16 MiB 仍用 10.775 s ≈ 12.5 Mbit/s，与 200 Mbit/s 的 11.305 s 同量级；
`boosters/sealed` 从 32% 降到 9.3%，`cell_loss=0.0001`。**丢包与 BOOSTER 都不是限速量。**

再把发送端自身的三个条件排除。在实测点（`grant_limit=2515`、`peer_ack=1811`、
`Head=1814`、`qhigh=65`）`TxHalf.CanFill`（`TxHalf.fs:289-294`）恒为真：

- `1814 + min 16 ((2515 − 1811)/8) = 1830 < 2515`；
- `HeldSteps` 远小于 `Capacity`（`--window` 1024）；
- `queue.Count`（高水位 65）< `queueLimit`（`TxQueueCells = 256`，`Session.fs:66`）。

所以发送端不是被自己的窗口挡住。`qhigh` 恒为 65、`queued=0` 说明队列最多 65 个 cell
在等路径，而上限 256：**队列从未填满，是上游供数不足**。

每步只有约 11.5 个 cell（`sealed 10483 / 908 步`），而 `SlotsPerStep` 允许 32。
步是被 `Flush()`（“input paused: 0-wait flush”）提前封的，说明 `FlowBridge`
每轮只读到约 11 帧就遇到 EAGAIN。

因此下一个读的对象是 `FlowBridge` 的读循环：每次从 target socket 读多少、由什么事件
唤醒、是否受 `TunRecvBudget = 128`（`SocketTuning.fs:71`）或非阻塞 EAGAIN 限制，
以及读循环的调用频率是否由 `SessionCarrier.Service` 的轮次驱动。

### 1.4 正式根因：flow socket 缺接收缓冲（2026-11-20 第三代）

§1.1–§1.3 的模型与下面的对照放在一起，根因已经确定。

**根因。** `SocketTuning.local`（`SocketTuning.fs:128`）只设 `NoDelay`，不设
`SO_RCVBUF`。`FlowBridge` 连 target 与本地应用的 socket 都用它。接收窗口交给内核
自动调优，而 tbtun 的读循环是“每轮读一次、遇到 WouldBlock 就退出”；应用侧读得慢，
内核就判定不需要大窗口，窗口停在 64–128 KB。单流吞吐被钉在 `rcvbuf / RTT`。

**量化。** 40 ms RTT 下 64–128 KB 给出 1.6–3.2 MB/s，实测 1.48 MB/s；
生产 329 ms RTT 下约 330 KB 给出约 1 MB/s ≈ 8 Mbit/s，与生产 8–10 Mbit/s 吻合。

**为什么前面每一条都被证伪。** 承载侧的假设（pace、聚合突发、前沿 20 ms、等待粒度、
丢包、Grant、CanFill、Encoder 容量、FlowRoom）都无法解释一个事实：**4 路与 256 路
的用时差 0.01%**。承载路径数变化时若吞吐不变，限制量就只能在承载之外——即 target
侧那条单流 TCP。本节的每一次证伪都把范围缩小一格，最后只剩这一处。

**修复。**

```fsharp
let local (socket: Socket) =
    socket.NoDelay <- true
    try socket.ReceiveBufferSize <- 4_000_000 with :? SocketException -> ()
```

| 场景 | 修复前 | 修复后 |
|---|---|---|
| netem 20 ms / 200 Mbit，256 路，16 MiB | 11.31 s（1.48 MB/s） | 2.573 s（6.52 MB/s） |
| netem 20 ms / 200 Mbit，4 路，32 MiB | 22.39 s（12.0 Mbit/s） | 4.527 s（59.3 Mbit/s） |
| netem 20 ms / 200 Mbit，256 路，32 MiB | 22.39 s（12.0 Mbit/s） | 4.673 s（57.4 Mbit/s） |
| 无 netem，256 路，128 MiB | 1.458 s（736 Mbit/s） | 1.419 s（757 Mbit/s） |

同轮读数：`qhigh` 65→284、`boosters/sealed` 32%→3.2%、server RSS 104–111 MB、
单测 387 全过、构建 0 warning / 0 error。

**有效值与部署前提（第四代更正）。** Linux 的 `SO_RCVBUF` 先把设定值 clamp 到
`net.core.rmem_max`，再**乘 2** 赋给 `sk_rcvbuf`；`getsockopt` 返回的是设定值。
因此设定 4,000,000 B 时内核实际缓冲是 8,000,000 B——本机“被截断在 4 MB”是错误表述。
生产 HND0 实测 `rmem_max = 16777216`、`tcp_rmem = 4096 131072 16777216`，4 MB 请求
不会被截断，8 MB 有效缓冲覆盖 329 ms × 200 Mbit/s 的 BDP（8.2 MB）。代码已改为
`min(FlowRecvBuf, rmem_max())`（`SocketTuning.flowRecvBufFor`），服务端 session up
日志打印 `flow_rcvbuf_req=<请求> flow_rcvbuf_eff=<2×请求> rmem_max=<上限>`；任一主机
被截断时可直接从日志读出，不再依赖常量猜。

**对本节前文的位置。** §1.1 保留为接收前沿的机制描述，但 H2 作为下行吞吐主因已被
CASE-9 口径的实测证伪（`stallNs` 20→2 ms 无变化）；§1.2、§1.3 保留为排除记录，
不再作为修复依据。

### 1.5 内存上界与合法状态（第四代）

`SO_RCVBUF` 是每流上限，内核按实际到达数据记账，空闲时不占满。单流最坏内核接收
缓冲 = `2 × min(FlowRecvBuf, rmem_max)` = 8 MB；8 MiB 单流信用仍由 `FlowRoom` 把关，
读循环在信用耗尽时停止读，所以 rcvbuf 不会放大应用侧在途。并发上界因此是
`8 MB × 活跃流数`。实测生产 server RSS 70 MB，因为本地代理 `max-connections: 1` 把
多条 curl 复用成一条 VLESS/smux 长连接，tbtun 只看到 1 条 flow。若并发流数上升，
按此顺序收边界：把 `FlowRecvBuf` 收到 1.25 BDP、按流数缩容、或只在接收侧设 rcvbuf
（`local` 只用于 flow socket，不用于 256 条承载路径）。这条上界由 CASE-11 验证。

## 2. 目标与非目标

> **第十代主线（2026-11-20 12:23）**：§2–§8 的 `pace` 主线与 §10 的 smux 窗口
> 都已被实测证伪；§1.4 的 flow socket 接收缓冲修复已在 HEAD（53dc67e）。当前唯一
> 同时解释「生产 0 字节轮、restart 后 `paths=1` 冻结、同一二进制 pace 0 能恢复」的
> 缺陷是**客户端路径池不补建**。本代设计与可编码契约见 §14；修复面只有客户端，
> 服务端保持 `3f05c4a0`、`--pace 0`，等 CASE-13 通过后再做 pace 单变量。

目标：把 flow socket（target 侧与本地应用侧）的接收窗口由内核自动调优改为显式
`SO_RCVBUF`，让单流吞吐脱离 `rcvbuf / RTT` 的 64–128 KB 量级，使生产下行在 329 ms
RTT 下达到基线 2 倍以上（实测 3.4×），同时不让 SRTT 自致倍增、不让承载重传高于旧版。
验收以 `docs/cases/link-speed-2026-11-20-cases.md` 为准；CASE-2 的 5% 重传线当前未达，
见 §6 与本轮 result 的“条件性上线”。

> **第七代修正（2026-11-20 17:05）**：上面的 3.4×（3.5→11.8 Mbit/s）是单一时段的
> 结果，不能当作可重复基线。第七代在同一服务端二进制、同一隧道上复测到
> 0–3 Mbit/s，且 proof.ovh 与 Netflix OCA 两个独立源在同一窗口同时出现 0 字节轮，
> 256 路 ESTAB 全程不变。因此**间歇停摆**是比稳态中位更优先的验收对象：
> 先消除停摆（CASE-12），再谈中位（CASE-1）。主线与三条候选见 §11；
> §2–§4 中以 `pace` 为主线的表述已作废，只保留为历史。

非目标：
- 不改 256 路、`:39443`、TCP-only、TFO/BBR、本地代理 `max-connections/min-streams`。
- 不改 `rho=0.25`、GF(16)、8 MiB 单流信用、32 MiB 池、32 槽/路。
- 不改上行 `RateGroup` 的增益、采样与修复储备规则。`SessionCarrier.fs:64-67` 在
  `pace` 开启时设 `group.ProtectRecovery <- true`，这与既有上行 pace 是同一行为，
  不是本轮新增变量；本文档早先写“保持 false”与代码不符，已按代码更正。
- 不通过缩减路径与流量、放宽断言、改测试床判定来换速度。

## 3. 模块边界与依赖 DAG

```
Tbt.Cli
  Client.fs ── pace 开关（既有 uplink-pace）
  Server.fs ── 新增 pace 开关（下行）
        │
        ▼
Tbt.Carrier
  SessionCarrier ── 方向无关的 pace 语义（本轮的改动面）
        │                │
        │                ├── RateGroup（不改契约）
        │                └── WriteGate（不改阈值）
        ▼
  IPath：PathLink（同步，已有 cwnd/pacing/group 分支）
         AsyncPathLink（异步，已有 UnsentBytes / 32 槽 / TryBeginFrame）
        │
        ▼
Tbt.Session（不改：帧、步、缺口、NACK、BOOSTER 的语义）
```

改动只落在 `SessionCarrier`（把 `uplinkPace` 重命名为 `pace`，方向由调用者决定）
和 `Server.fs`（暴露开关）；`RateGroup`、`WriteGate`、`AsyncPathLink` 的契约不动，
`Tbt.Session` 不动。因此修复不引入第二种速率范式，而是把已有范式补到缺失的一侧。

### 3.1 SessionCarrier 契约

- 构造参数 `?pace: bool`。`false` 保持今天下行/无 pace 客户端的逐字节行为；
  `true` 时新数据走 `group.TryTake` + `group.OnSent`，BOOSTER 走
  `group.TryTakeRepair` + `group.OnRepair`，控制帧（WIN_ACK/CTRL/heartbeat）绕过组。
- `uniqueOut` 只在新数据成功产生（`charge = 2`）时按其唯一字节增长；
  `wireOut` 对 BOOSTER 与新数据都增长。`Inflight` 仍以 `uniqueOut - UniqueAcked*fl`
  计，因此修复副本不会把 cwnd 撑爆。
- 组未 `Ready`（`bw = 0` 或 `minRTT` 未知）时，用 `startupCap = 64 * frameLength`
  的全局在途兜底；`Ready` 后由 `RateGroup.Cwnd` 与令牌桶接管。
- 等待侧：paced 且令牌耗尽时，`WaitUs` 返回 `TokenWaitNs` 的精确值；IO 线程
  `PumpTx` 有进展会 `channel.Wake()`，不引入固定轮询间隔。

### 3.2 RateGroup 契约（不改）

`Add/Remove/Service` 的单次 O(n) 扫描、`bandwidth` 的 10 抽头最大滤波、startup 的
2.885 增长、`Cwnd = gain * bw * minRTT + 4 frames`、`TokenWaitNs` 的零剩余语义
全部保持。下行开启后，`bw` 的种子来自下行自身的交付（`Delivered` 用
唯一到达/ACK 证据），不与上行交叉拷贝；两个方向各有独立的 `RateGroup` 实例。

### 3.3 AsyncPathLink 契约（不改）

`UnsentBytes = 内核 notsent + 槽位未发字节`；`TryBeginFrame/CommitFrame` 仍是
“会话线程占用槽位、IO 线程发出并推进 tail”。32 槽大于 `GatePass2Limit`，因此
写门先于槽位耗尽生效。下行 paced 后，`groupAllows` 成为写门之前的第一道总闸。

### 3.4 Server CLI 契约

新增 `--pace`（`0/1`，默认 `0`）。默认关，保证既有矩阵与回滚路径逐字节不变；
生产部署在 systemd 单元里显式打开。日志的 `send_control` 字段在 async 路径上只
表示“非 kernel”，不能作为 pace 是否开启的证据；新增一行 `pace=on|off` 与
`group bw_mbit/phase/inflight`，让 CASE-2/3 的证据可直接读取。

## 4. CASE 映射

| CASE | 被哪个机制满足 | 证据 |
|---|---|---|
| CASE-1 下行吞吐 | 下行 `RateGroup` 限住聚合同步突发，交付率采样进入 startup 增长 | 5 轮 curl 中位与 CV |
| CASE-2 重传率 | 同上：1.25 BDP 聚合在途 ≤ 瓶颈 BDP | `bytes_retrans/bytes_sent` 配对 |
| CASE-3 RTT/排队 | 同上：队列上界约 0.25 BDP | 有载 SRTT / minRTT、`queued` |
| CASE-4 256 路 | 不改协议、路由、信用；只改发送门 | 双端连接数、回环四批 |
| CASE-5 上行 | 客户端 `uplink-pace` 代码路径不变 | 上行速率、交互 p99 |
| CASE-6 矩阵 | `--pace` 默认关，矩阵二进制与证明路径不变 | `segments.json` |
| CASE-7 故障/回滚 | 开关即回滚：去掉 `--pace` 恢复今天行为 | 2 分钟内换回 |
| CASE-8 长尾 | 不改 IO 线程与 Session | `async_svc_us_max` |

## 5. 故障隔离

- 组采样异常或时钟跳变：`scan` 过滤负的 `MinRttNs`；`Inflight` 不小于 0；
  `Ready=false` 时由 `startupCap` 继续放行，不会因估计坏掉而停摆。
- 单路故障：`prune` 移除、`Remove` 触发 `restartSample`，组重新采样，
  其余路径继续。
- 令牌长期为负（估计过高）：`Service` 的 drain 相位与 8 相探测把 `bw` 拉回；
  `TokenWaitNs` 保证等待有限。
- 服务端 async 循环：必须消费 `WaitUs`；若只消费 `WaitMs` 的 5 s 睡眠，
  评测会发现吞吐停在启动值，CASE-1 失败，回退开关即可恢复。
- 回滚：`--pace` 未传即回到 `f2b63e00…` 的数据面行为；无协议、无状态迁移。

## 6. 打开的风险（必须在实现前验证）

1. **下行 `bw` 种子**：初始 64 帧（约 83 KiB）对 400 ms RTT 的 BDP 只是零头；
   startup 需要 ~3 个 minRTT 才能到 40 Mbit/s。若 256 路里最慢的一路拖住
   `Delivered`，增长会慢。验证：生产影子单轮 15 s 内看 `group bw_mbit` 是否上升；
   矩阵 4/8 路必须不退化。
2. **`WriteGate` 与组闸的先后**：`WriteGate` 用内核 notsent，可能对丢包路径
   返回很大值而 pass2 放行；组闸仍是总闸，但若某路径 notsent 长期高，`UnsentBytes`
   会吃掉组预算的公平性。验证：`path_tx` 分布是否仍覆盖 256 路。
3. **`ProtectRecovery`**：代码在 `pace` 开启时设 `group.ProtectRecovery <- true`
   （`SessionCarrier.fs:64-67`），与既有上行 pace 是同一行为，不是本轮新增变量；
   若下行修复轮次把 `bw` 拉低，下一轮再单独评估把它关掉。
4. **首次 NACK 截止修复**：工作树是否已含该改动决定了“丢包下降后修复是否仍慢”。
   若不含，本轮不夹带；先部署 pacing，单独测量缺口填补时延。
5. **systemd 与二进制**：生产两端必须同版本；`--pace` 只加在服务端单元，
   客户端不动，回滚时只改一行。

## 7. 实施顺序

> 第四代注（2026-11-20）：本节按 pace 路线写的顺序已被 §1.4 的实测证伪。实际执行
> 顺序是 P1 回环基线 → P4b 根因修复 → P6 单变量收口 → P7 生产切换。下面的 pace
> 步骤只保留为历史，不再执行。

1. `SessionCarrier`：`uplinkPace` → `pace`，更新 `Client.fs`/`ClientHost.cs` 调用点，
   行为不变（默认 `false`）。
2. `Server.fs`：新增 `--pace`，透传 `pace = o.Int("pace", 0) <> 0`，打印
   `pace=on/off` 与组统计。
3. 单测：paced 下行在小规模会话上验证 `uniqueOut` 只计新数据、BOOSTER 走修复桶、
   `Ready=false` 时 64 帧兜底、`--pace 0` 与今天逐步一致。
4. 矩阵与 256 路回环。
5. 生产影子：服务端换二进制 + `--pace 1`，客户端不动，按 CASE-1/2/3 单变量对照。
6. 达门槛后写入 systemd 并复测；失败则去掉 `--pace`。

## 8. 验证命令的硬约束

用户明确要求：**单次测试不超过 60 s**。因此每轮 `curl --max-time` ≤ 15 s，
5 轮合计 ≤ 75 s 但每次进程独立且 ≤ 15 s；矩阵脚本按现行 `seconds=10`；
生产对照单次 ≤ 60 s。任何超过 60 s 的单次测量视为无效证据。

## 9. 第五代：信用回收语义与三候选判定（2026-11-20 16:40）

本节更正第四代的三处推理，并给出决定性的最小测量。它不重复 §1.4 已证的
flow socket 接收缓冲修复；那条修复仍然成立，仍是下行从 3.5 提到 11.8 Mbit/s 的原因。

### 9.1 更正一：信用按「写入本地应用」回收，不按解码前沿

前文 §1.1 第 3 条写「流级信用按交付前沿回收（`rx_ack` 的增量）」。读代码后这是错的。
真实链条在 `src/Tbt.Session/Flows.fs`：

| 位置 | 事实 |
|---|---|
| `Flows.fs:222` | `consumed f = f.RxDelivered - uint64 f.Reserve.Count` |
| `Flows.fs:509` | `f.RxDelivered <- f.RxDelivered + uint64 taken`，在把 Reserve 的字节写进本地 socket 时增长 |
| `Flows.fs:524` | `refreshCredit f` 在该次写入之后调用 |
| `Flows.fs:368` | 对端 `OnPeerCredit` 用 `DeliveredOffset + CreditBytes` 提高 `TxLimit` |

因此 `flow_credit` 的余量反映的是「本地应用 socket 还能再收多少」，不是「解码前沿
推进到哪里」。解码慢会让数据到不了 Reserve；本地应用读得慢会让数据堆在 Reserve，
两者都表现为信用不回收，但只有后者的 `Reserve.Count` 会长期偏高。

### 9.2 更正二：WINDOW_CLAMP 不是下一层上限

第四代把「`TCP_WINDOW_CLAMP = 600000` / 329 ms ≈ 14.6 Mbit/s」当作与实测吻合的下一层
上限。算术本身对，作用点错了：`SocketTuning.path` 的 clamp 设在**每条承载 socket**
上，256 路的聚合在途上限是 `256 × 600 KB ≈ 153 MB`，而应用层单流信用只有 8 MiB。
8 MiB 先耗尽，所以 clamp 不可能是单流 11.8 Mbit/s 的原因。600 KB→2 MB 的对照不必做；
做了也不会有 20% 收益。

### 9.3 信用周转模型

生产实测：8 MiB 单流信用几乎用满（余 9.8 KB），交付 11.8 Mbit/s = 1.475 MB/s。
若把信用看成在途字节，周转时间是

$$\tau = \frac{8\ \text{MiB}}{1.475\ \text{MB/s}} = 5.4\ \text{s}$$

而物理 RTT 只有 329 ms。差的 5.07 s 不在网络上，只能在「解码 → Reserve → 本地应用
socket」这一段。这是可证伪的：若这 5 s 真在 Reserve，`Reserve.Count` 或
`ss -tinm` 里 18443 socket 的 `send-q` 必然长期高位。

### 9.4 三条竞争候选与判据

| 候选 | 机制 | 判据（同一 15 s 窗口内采） |
|---|---|---|
| C1 本地代理消费慢 | tbtun 把数据写 18443 的 socket，本地代理不读或读得慢，`Send` 短写、`RxDelivered` 停 | 18443 的 `send-q` 长期 > 256 KB；`bytes_acked` 增量 < 隧道 egress 需求；客户端 `flow_credit` 低位与 `Reserve.Count` 高位同时出现 |
| C2 RxHalf 交付排队 | 解码前沿推进慢，数据到不了 Reserve，信用也不回收 | `rx_ack` 步/s × 每步字节 > 实际交付字节率；`Reserve.Count` 低位而 `rx_base` 落后 `rx_ack` |
| C3 smux 单连接限速 | 本地代理的 VLESS/smux 单连接自身封顶，18443 读写都很快 | 18443 `send-q` ≈ 0、tbtun 的 Write 不阻塞，但本地代理出站吞吐低于目标；需本地代理侧指标或换 `max-connections` 对照 |

判定顺序：先取 C1 所需的 `send-q` 与 `bytes_acked`。若 C1 不成立，再看 C2 的
`Reserve.Count` 与 `rx_ack` 步/s。只有前两者都被排除才需要 C3 的本地代理侧证据。

### 9.5 判定的最小测量（≤60 s）

1. 起一轮 15 s `curl --proxy 127.0.0.1:17893` 下载，记录 `size_download`、`time_total`。
2. 同一窗口每秒取：客户端 stats 一行的 `rx_ack`、`rx_base`、`flow_credit`、`queued`、
   `srtt_ms`；`ss -tinm '( sport = :18443 )'` 的 `send-q`、`bytes_acked`（或 `bytes_sent`）。
3. 窗口首尾各取一次服务端 stats 的 `tx_head`、`peer_ack`。
4. 判定：`18443 send-q` 高位且 `bytes_acked` 增量 ≈ 交付量 ⇒ C1；否则按 9.4 继续。

本测量不改任何生产状态，也不部署二进制；它是全部后续修复的前置。

## 10. 第六代：限制在 smux 流接收窗口，修复是配置级（2026-11-20 16:48）

### 10.1 作用点

下行数据的 smux stream 接收方是**本机本地代理的出站会话**。smux 的流控由接收方
通告窗口，发送方（HND0 本地代理）按通告值发送；本机配置的 `max-stream-receive-window`
就是本机通告的单流窗口。它不是 HND0 侧参数，也不受 `max-connections` 影响。

### 10.2 证据与量纲

| 观测 | 值 | 含义 |
|---|---|---|
| 隧道吞吐 | 13.05 Mbit/s | 1.63 MB/s |
| 生产 RTT | 314 ms | |
| 在途量 | 1.63 MB/s × 0.314 s ≈ **512 KB** | 与 smux 单流窗口同量级 |
| 服务端 `flowread` | 5019 次 Read / 52.56 MB / 4869 次 WouldBlock（**97%**） | target socket 被读空，缺数据 |
| 服务端 `queued` / `flow_credit` 余 | 0 / 8.39 MB | 发送端不缺窗口，缺上游供数 |
| 18443 `send-q` | 18 次快照全 0 | 排除“本地代理消费慢” |

### 10.3 变更与契约

- `proxies[].smux.max-stream-receive-window: 4194304`（4 MiB）。
- 本地代理 v1.19.33 接受该字段：把配置写到 `/tmp/tbt-本地代理-test.yaml` 后
  `/opt/proxy/本地代理 -t -f` 输出 `test is successful`。
- 不关 smux、不改 `max-connections: 1` / `min-streams: 1`，不违反 CASE 集的
  “不可改约束”。
- tbtun 二进制、协议、信用、编码、路径数全部不动；tbtun 对本地代理只是透明 TCP 承载。

### 10.4 失败隔离与回滚

1. 写回前 `本地代理 -t` 预检；任一字段错误即停止，不重启。
2. 备份 `/run/hnd0tbt4.yaml` 到 `/run/hnd0tbt4.yaml.bak-g6`，记录 sha256。
3. `systemctl restart proxy-hnd0tbt4`（Restart=always，中断 < 2 s）。
4. 30 s 内必须满足：服务 `active`、`ss -tan '( dport = :39443 )'` ≥ 250 路、
   经 17893 一次 HTTPS 请求成功；任一不满足即 `install` 备份 + restart，60 s 内回到旧值。
5. 配置是唯一变更，回滚不需要重新部署任何二进制。

### 10.5 内存上界

每流窗口 4 MiB 是**通告值**，实际驻留受本地代理的 smux 总接收缓冲（默认值）钳制。
并发 N 条 stream 的最坏驻留 ≤ `N × 4 MiB`，但总缓冲先到上限；因此并发场景必须看
`RSS` 而不只看单流。CASE-11 的单流增量 ≤16 MB 仍是上界判据。

### 10.6 CASE 映射

| CASE | 满足方式 |
|---|---|
| CASE-1 生产下行 | 同 URL 5 轮中位 ≥ 基线 1.5×，无 0 字节轮 |
| CASE-3 RTT/自排队 | 有载 `srtt ≤ 1.6 × minrtt` |
| CASE-9 前沿/信用 | 服务端 WouldBlock 占比下降、`rx_ack` 步/s 与交付字节率偏差 ≤20% |
| CASE-11 内存 | 单流 RSS 增量 ≤16 MB |
| CASE-2 承载重传 | 不在本次修复范围；窗口变大后若重传率不降，单独处理，不用它阻断提速判定 |

## 11. 第七代：停摆优先，稳态其次（2026-11-20 17:05）

本节是当前唯一主线。它替换 §2–§4 的 `pace` 主线，更正 §9–§10 的三处证据读法，
并把验收顺序从「中位吞吐」改为「先停摆率、后中位」。

### 11.1 三处证据更正

| 位置 | 原写法 | 事实 |
|---|---|---|
| `s7.md` 第七节第 1 条 | 「本地代理不暴露 smux 流缓冲、窗口不可调」 | 与文末第六代更正矛盾：v1.19.33 接受 `max-stream-receive-window`，`本地代理 -t` 通过。以更正为准 |
| `s7.md` §5、`§10.2` | 用 `flowread=2958/52561777/2808` 推出「每次 Read 17.8 KiB、95% WouldBlock、读到空」 | 这是**会话累计**，不是该轮瞬时。可用于跨轮对比，不能用来描述单轮行为；瞬时必须取轮前后增量 |
| `result.md` 第六代 | 「4 MiB 窗口无收益」 | 该 A/B 的基准本身在崩塌（新配置 2/5 轮 0 字节、旧配置 2/3 轮 0 字节）。只能记「未观察到收益」，不能记「证明无效」 |

### 11.2 第七代复测（09:04–09:05 UTC）

同一服务端二进制 `661ad970`、同一隧道、256 路 ESTAB 全程：

| 轮 | 源 | code | size (B) | Mbit/s |
|---|---|---|---|---|
| 1 | proof.ovh 100 MB | 200 | 3,096,558（12 s 截止） | 2.06 |
| 2 | proof.ovh 100 MB | 000 | 0（连接超时） | 0 |
| 3 | proof.ovh 100 MB | 200 | 3,751,882（10 s 截止） | 3.00 |
| 4 | Netflix OCA 25 MiB | 000 | 0（TLS `unexpected eof`） | 0 |
| 5 | Netflix OCA 25 MiB | 000 | 0（连接超时） | 0 |

两个独立源在同一时段同时停摆，因此「单源限流」不成立。s7 的 13.05 Mbit/s 是
少数好时段，第四代的 11.8 Mbit/s 同理。**没有一条跨时段可重复的基线。**

### 11.3 验收顺序

1. **CASE-12 停摆率**（新增）：10 轮中 0 字节轮 ≤1；停摆后下一次请求 ttfb ≤3 s。
   0 字节轮即使 `code=200` 也计入。停摆轮必须单列，不能从 CASE-1 的中位数里静默剔除。
2. **CASE-1 中位**：只在停摆率达标后才有意义；门槛按当轮 HND0 直连的 1/5 取相对值。
3. **CASE-2/3 承载健康**：与停摆机制无关时不作为阻断项。

### 11.4 三条候选与唯一判据

三条都必须在**同一个停摆窗口**内取增量，不得跨轮拼接。字段来源：
客户端 `journalctl -u tbtun-hnd0` 的 stats 行；服务端同式（SSH 可用时）；
`ss -tinm` 的 18443 与 39443 连接。

| 候选 | 机制 | 成立判据 | 反证判据 |
|---|---|---|---|
| S1 接收前沿停 | 解码前沿不推进，数据到不了 Reserve，信用也不回收 | 停摆窗口内 `rx_ack` 增量 = 0，`rx_base − rx_ack` 不变，而服务端 `tx_head − peer_ack > 0` 或 `queued > 0` | 服务端 `tx_head = peer_ack` 且 `flowread` 增量 = 0 ⇒ 不是 S1，转 S2/S3 |
| S2 单流窗口 | 本地代理的 VLESS/smux 单流接收窗口把在途钉在 `窗口 / RTT` | 停摆窗口内服务端 `flowread` 增量 ≈ 0（target 无数据可读），同时 HND0 裸连同 URL 正常；客户端 18443 `send-q ≈ 0` | `flowread` 增量正常而客户端 `rx_ack` 不动 ⇒ 回到 S1 |
| S3 读节奏 | 服务端每轮只读一次，`WouldBlock` 后等定时器，成功读的字节数远小于一轮预算 | 成功 Read 的字节数 < 256 帧的一半，且轮次频率 ≈ `1 / NextTimerUs` | 成功 Read 接近整轮供给 ⇒ 不是 S3 |

设计上**不预设** S1/S2/S3 哪条成立。S2 的配置级修复（4 MiB 窗口）已在基准崩塌期
试过一次，只能记「未观察到收益」；若 S2 判据成立，重做时必须先建立停摆率基线，
再在同一好时段内做 A/B。

### 11.5 代码作用点（与磁盘一致，2026-11-20 17:05 核对）

| 位置 | 事实 | 与候选的关系 |
|---|---|---|
| `SocketTuning.local`（`SocketTuning.fs:209-212`） | 只设 `NoDelay` + `ReceiveBufferSize <- flowRecvBufFor (rmemMax())`；没有 `WindowClamp` | 下行接收侧在服务端，已生效；本机客户端 `f2b63e00` 不含此改动，只影响上行 |
| `FlowBridge.Read`（`FlowBridge.fs:117-155`） | `budget = 4096` 是**包数**，每次成功 `Receive` 减 1；`WouldBlock` 或 `FlowRoom ≤ 0` 即退出 | S3 的作用点；退出条件决定每轮供给 |
| `SessionCarrier.writeRound`（`SessionCarrier.fs:288-306`） | 每轮预算 256 帧，`lastRoundSent <- 256 - budget` | 每轮上限；不是当前限制（队列从未填满） |
| `SessionCarrier.WaitUs`（`SessionCarrier.fs:342-356`） | 非 pace 且 `lastRoundSent < 256` 时返回 `session.NextTimerUs()` | S3 的节拍来源；第五代压到 1 ms 无收益，说明单靠加密轮次不够 |
| `SocketTuning.path` | `TCP_WINDOW_CLAMP = 600000` 在**每条承载 path** 上，聚合 256 × 600 KB | 不是单流限制（§9.2 已证） |
| `GatePass2Limit = 262144` | 每路 BDP 的 5–6 倍；服务端 `srtt 927 / minrtt 310` 是其症状候选 | 独立变量，未做对照；不得与其它变量同批 |

### 11.6 CASE 映射（替换 §4）

| CASE | 满足方式 | 判据 |
|---|---|---|
| CASE-12 停摆率 | 消除停摆机制 | 10 轮 0 字节轮 ≤1，恢复 ttfb ≤3 s |
| CASE-1 中位 | 停摆消除后的稳态 | 中位 ≥ 当轮直连的 1/5，CV < 20% |
| CASE-2 承载重传 | 与停摆机制解耦 | 配对 `bytes_retrans/bytes_sent` ≤5%，不阻断停摆修复 |
| CASE-3 RTT/排队 | 停摆窗口内 `srtt = minrtt` | 有载 SRTT ≤1.6 × minRTT |
| CASE-4/5/6/7/9/11 | 不变 | 见 cases.md |
| 上行 CASE-5 | 本机客户端仍 `f2b63e00`，行为逐字节不变 | 必须实测，不再用机制论证代替 |

### 11.7 `pace` 的定位

保留为附录，不进入主线：回环 20 ms/200 Mbit 下 `--pace 0` 22.3896 s、
`--pace 1` 22.3877 s，差 0.01%；影子实验 8.52（开）对 18.11（关）方向相反。
任何以 `pace` 为唯一变量的实验都必须允许它失败。

### 11.8 风险

1. 停摆窗口极短（秒级），stats 采样 1 s 可能只抓到边界。必须在停摆轮内至少取到
   一次停摆前与停摆后的完整一行，并记录墙钟时间戳。
2. HND0 SSH 间歇不可用（本轮与第六代各有超时），服务端增量可能抓不到；抓不到时
   只做客户端侧判定，并在证据表标注「服务端窗口缺失」，不得用累计值补。
3. 本机客户端 `f2b63e00` 与服务端 `661ad970` 不同代；任何客户端侧修复都要先解决
   部署通道（CASE-0），不得把二进制差异混进停摆归因。

## 12. 第八代：停摆定位于 smux stream 投递（2026-11-20 17:20–17:25）

本节是当前唯一主线，接续 §11。它把 S4 从「候选」变成有栈证据的定位，并给出
第七代未试的配置候选。

### 12.1 本轮证据（原始目录 `/tmp/tbt-g8-audit-*`、`/tmp/tbt-g8-flow-*`、`/tmp/tbt-g8-dir-*`）

| 测量 | 结果 |
|---|---|
| HND0 直连 proof.ovh 100 MB | 200，104,857,600 B / 6.589 s ≈ 127 Mbit/s |
| 经 17893 三轮（每轮 ≤20 s） | 0 B（code=000）/ 2,047,910 B / 10,829,806 B |
| 0 字节轮 | 18443 `bytes_sent` 208,841,171→215,340,063（+6.5 MB/15 s）；17893 的 `bytes_sent` 恒 39 B（仅 CONNECT 响应） |
| 停摆轮前后 | `opened=22 closed=21 local_fail=12 stale=27356` 完全不变 |
| tbtun 侧 | `RxDelivered` +10 MB/21 s、`rx_ack` +664 步/21 s、18443 `skmem r0`、`bytes_sent=bytes_acked`、`app_limited`、`rcv_wnd` 470 KB |

### 12.2 goroutine 现场（`/run/hnd0tbt4.log:515-780`）

停摆窗口内对 pid 2235045 发 SIGQUIT（systemd `Restart=always` 3 s 自愈）。栈：

- goroutine 73：`本地代理/common/net.Relay.func2 → sing/common/bufio.Copy →
  sing-mux.clientConn.Read → wrapStream.Read → smux.(*stream).Read →
  smux.(*stream).waitRead`（select，等数据）。
- goroutine 39：`smux.(*Session).recvLoop → io.ReadFull`（读底层 frame）。
- goroutine 40：`smux.(*Session).sendLoop`（select）。
- goroutine 184：Relay 另一方向阻塞在 curl accept 连接的 `RawRead`（IO wait）。

实现是 `github.com/metacubex/smux@v0.0.0-20260105030934-d0c8756d3141` 与
`sing-mux@v0.3.12`，**不是 xtaci/smux**。SIGQUIT 时 curl 以
`SSL_read: unexpected eof` 结束，证明断的是这条卡住的 stream。

### 12.3 机制

recvLoop 读入并解析 frame（tbtun 的 18443 有字节流动），但没有唤醒 curl 连接对应
stream 的 `waitRead`。数据被投递到另一条 stream，或投递到 `min-streams` 预开的池
stream 而池 stream 没有下游读者。这解释了「tbtun 交付健康、本地代理内存活、curl
收 0」三者同时成立。

### 12.4 修复候选与契约

| 序 | 候选 | 判据 | 回滚 |
|---|---|---|---|
| R1 | `smux.min-streams: 1 → 0` | 隔离实例停摆率显著低于同构 `min-streams=1` 对照 | 改回 1，restart |
| R2 | `smux.enabled: false` + 短请求建立优化 | 第六代大文件提升、短请求 3/3 超时，原因未查 | 改回 true |
| R3 | R1/R2 均无效：记录并保持生产 | 不部署 | — |

契约：只改本地代理配置 `/run/hnd0tbt4.yaml`；tbtun 二进制、256 路、TCP-only、
8 MiB 信用、`rho=0.25`、GF(16) 全部不动。生产改动前备份
`/run/hnd0tbt4.yaml.bak-g8` 并记 sha256；回滚
`cp .bak-g8 /run/hnd0tbt4.yaml && systemctl restart proxy-hnd0tbt4`，<60 s。

### 12.5 CASE 映射

- CASE-12 停摆率 ≤1/10：主判据。
- CASE-1 中位 ≥ 当轮 HND0 直连 1/5：停摆达标后才有意义。
- CASE-3 `srtt ≤1.6×minrtt`、CASE-4 256 路：不退化。
- 短请求 `generate_204` ttfb ≤3 s：R2 的硬门槛。

### 12.6 第八代追加实验（同日 17:30–17:35）

- `min-streams: 0` 证伪：三个隔离/生产实例同步停摆，3 轮无区分
  （`/tmp/tbt-g8-p2-1795166731`）。
- `initial-stream-receive-window` + `max-stream-receive-window` 各 4 MiB：17911 对 17912，
  3 轮 0/131K/459K 对 0/377K/0，坏时段不可判定，未观察到收益
  （`/tmp/tbt-g8-winr-1795166926`）。
- HND0 服务端 09:27:25：`tx_head=peer_ack=51504`、`queued=0`、`grant_limit=52208`、
  `srtt 1412/minrtt 726`、`cell_loss 0.1632`、`send_control=tun`。
- 结论修正：12.3 的「frame 未唤醒该 stream」应读作「frame 被 16.3% cell loss 与
  726 ms RTT 延迟」，不是 smux 的投递 bug；修复面回到 tbtun 承载（组级 pace / FEC
  自适应），两者都属需重新实现或敏感默认，本代未部署。

### 12.7 服务端组级 pace 的实现与床判定（本代）

- 代码：`Server.fs` 的 `--pace`（默认 0）→ `SessionCarrier(uplinkPace = pace)`；
  `Program.fs` usage 同步。构建 0 warning、单测 388 全过。
- 256 路床（`netem delay 100ms rate 200mbit`，16 MiB）：pace 1 用时中位 4.084 s 对
  4.378 s（-6.7%），`cell_loss` 0.26% 对 0.75%，`srtt` 2.3× 对 1.26×
  （`/tmp/g8-bed-*`）。
- 判定：pace 有机制收益，保持默认 0；生产部署门槛是同一时段好轮 ≥10 Mbit/s，且
  CASE-3 `srtt ≤1.6×` 不恶化。床未复现生产 16.3% cell_loss，不能据床数据宣称生产提速。

### 12.8 FEC 自适应与 rho=0.495 的来源（本代）

代码事实：

- `Server.fs:80-82` 与 `Client.fs:63-65` 把 `TBTUN_FEC_ADAPTIVE` 未设置解释为
  `true`；`SessionConfig.defaults.FecAdaptive = false` 被 CLI 层覆盖。生产 server
  的 `/proc/<pid>/environ` 没有该变量，因此服务端自适应开启。
- `TxHalf.adaptRho`：`target = clamp(0.02, 0.50, 3.0 × peerErasure / (1 − peerErasure))`；
  `rho` 每次向 target 靠拢 `+0.02 / −0.005`；`TxHalf.CellLoss = peerErasure`。
- 生产 `rho` 0.495 ≈ `RhoMax` 0.50 ⇒ `peerErasure` ≈ 0.142，与 stats `cell_loss`
  0.1632 同量级（EWMA 滞后）。

机制：`rho=0.495` 使下行发送量约为有效载荷的 1.5×，链路有效载荷约 67%。高 erasure
推高 rho、rho 增加拥塞、拥塞又抬高迟到/丢失，存在正反馈；但 erasure 估计
（`RxHalf.foldEstimate`，`stable = min over live paths`，`reorderSteps =
capacitySteps/2`）已修过两个历史误报（固定 reorder margin 与 pathSeenNs），
不能仅凭机制断言当前 16.3% 是误报。

最小验证（不改默认）：同一时段、同一床，用 `TBTUN_FEC_ADAPTIVE=0` 固定 `rho=0.25`
对照，观察 `cell_loss`、`boosters/sealed` 与吞吐；若固定 0.25 更快或 `cell_loss`
更低，正反馈成立，才进入敏感默认变更（需机制说明 + 单测 + 官方矩阵）。

床对照（本代，`netem delay 100ms rate 200mbit`，256 路，16 MiB，各 1 次）：

| FEC | 用时 (s) | 吞吐 (Mbit/s) | rho | cell_loss | boosters/sealed |
|---|---|---|---|---|---|
| 自适应 | 4.411 | 30.4 | 0.030 | 0.0057 | 301/14045 |
| 固定 0.25 | 4.451 | 30.2 | 0.250 | 0.0053 | 160/16137 |

床的 erasure 只有 0.57%，自适应把 `rho` 降到 0.03（省带宽），固定 0.25 发更多
parity；吞吐差在噪声内。床无法复现生产 16.3%，因此不能据此改默认。真正需要的是
在一段真实高 erasure 的窗口里读 `peerErasure` 与 `TxHalf.adaptRho` 的输入，确认
16.3% 是链路丢失还是 path 生命周期造成的在途丢失。

## 13. 第九代：路径池不补建（当前主线缺陷）

### 13.1 事实

`src/Tbt.Cli/Client.fs` 在启动时用 `joinTask` 建立 `min pathCount maxPaths` 条路径，
之后 `housekeeping()` 只调用 `attachJoined()` 排空 `pending`，没有「目标路径数 −
当前路径数」的比较，也不重新 `joinOne`。`SessionCarrier` 的存活条件是
`paths.Count > 0 || pathOps 非空`，因此只要有一条半开的 `Alive` path，会话就继续。

生产复现（12:14–12:20）：服务端 restart 后 HND0 `estab=0`、未建立会话；本机
`paths=1`、`rx_ack` 与 `frames_in` 冻结、大量 CLOSE-WAIT，`systemctl restart/kill`
超时，只有 `SIGKILL` 后自动重启才恢复 256 路。同一二进制在 pace 0 的 restart 中
恢复成功，说明这是概率性缺陷而非 pace 的服务端行为。

### 13.2 修复契约

`housekeeping()` 每轮比较 `carrier.Paths.Count + carrier.PathOpsPending +
pending.Count` 与目标路径数；不足时后台补建缺失 id。三条必须满足：

1. 同一 id 重建的 `Incarnation` 单调递增，否则服务端按重放拒绝。
2. 连接在 `Task.Run` 中进行，不得在会话线程里做 socket 握手。
3. 补建有指数退避与并发上限；服务端重启后 255 条连接不得同时涌入。

### 13.3 失败隔离与验收

补建失败只影响路径数，不关闭会话；`paths` 计数与每条路径的 `pathStep` 是唯一
观测面。CASE-12 追加判据：服务端 restart 后 30 s 内客户端 `paths` 回到 ≥250，
否则判失败，且不得以人工重启客户端作为恢复手段。

## 14. 第十代：路径池补建的可编码契约（2026-11-20 12:23）

### 14.1 主线与修复面

已证伪的三条不再投入：下行组级 pace（§12.7，开/关差 0.01%；生产 `--pace 1` 反而触发
本缺陷）、smux 单流窗口（§10，4 MiB 无收益）、本地代理转发层（用户明确禁止作为实验对象）。
`SocketTuning.local` 的 `SO_RCVBUF` 已在 HEAD。剩下的缺陷是可用性而非算法：路径池从
256 掉到 1，会话仍 `Alive`（`paths.Count > 0`），聚合承载能力按路径数比例消失，
而 `rx_ack`/`frames_in` 因为剩余那条路径不承载任何内容而冻结。

修复面只有客户端：`Tbt.Cli.Client` 的装配 + 新增 `Tbt.Carrier.PathPool`（纯账本）。
`SessionCarrier`、`Tbt.Session`、协议、信用、编码、路径数全部不改。服务端保持
`3f05c4a0`、`--pace 0`；`--pace` 在 CASE-13 通过前不进入生产。

### 14.2 模块边界与依赖

```
Tbt.Cli.Client
  housekeeping() 每轮：drainEvents → attachJoined → plan → wake
        │                    │
        │                    └── Task.Run(runBounded) → bootstrap/joinOne
        │                          （后台线程，只投递事件，不碰 carrier）
        ▼
Tbt.Carrier.PathPool（新增，纯逻辑，只由会话线程调用）
        │
        ▼
Tbt.Carrier.SessionCarrier（契约不变：AddPath / Paths / PathOpsPending / Alive）
Tbt.Session.JoinBootstrap（复用 tryRepeated / runBounded / clampConcurrency）
```

信息隐藏：`PathPool` 隐藏「缺哪些 id、何时重试、下一个 incarnation 是几」；
`Client` 隐藏 socket 与事件投递；`SessionCarrier` 不知道补建存在。三者不共享可变状态，
唯一的跨线程结构是一个 `ConcurrentQueue<PathEvent>`。

### 14.3 PathPool 契约

```fsharp
type PathEvent =
    | Joined of uint16 * PathLink
    | Failed of uint16 * string

type PathPool(target: int, concurrency: int, baseDelayNs: int64, capDelayNs: int64) =
    /// 本轮要启动的 (id, incarnation)，数量 ≤ min(concurrency, missing)。
    /// 返回即把 id 置入 inFlight，并递增该 id 的 incarnation。
    member Request: nowNs: int64 * present: Set<uint16> -> struct (uint16 * uint32)[]
    member Joined: id: uint16 -> unit
    member Failed: id: uint16 * nowNs: int64 -> unit
    member InFlight: int
```

- `target = min pathCount maxPaths`；附加 id 为 `1 .. target-1`，path 0 由握手建立。
- `present` 由调用者给出：`Paths` 的 PathId ∪ `pending` 的 id ∪ `applying` 的 id。
  PathPool 不持有 socket，也不读 carrier。
- `incarnation` 从 1 起，对同一 id **严格单调**，成功与失败都继续递增；服务端按
  `(id, inc)` 判重放，回退或重复都会被拒。
- `Failed`：`inFlight.Remove`；`attempt ← min(attempt+1, 8)`；
  `nextAt ← now + min(capDelay, baseDelay <<< attempt)`。
- `Joined`：`inFlight.Remove`；`attempt ← 0`；`nextAt ← 0`。

不变式：
- P-1 同一 id 在 `inFlight` 时不会被 `Request` 再次返回。
- P-2 `present` 中的 id 不会被返回。
- P-3 `now < nextAt` 的 id 不会被返回。
- P-4 incarnation 只增不回绕；到顶时饱和并记录一次 fault，不重建该 id。
- P-5 `Request` 返回项数 ≤ `concurrency`，且不重复 id。

### 14.4 客户端装配顺序

`housekeeping()` 每轮固定四步，全部在会话线程：

1. `drainEvents()`：排空事件。`Joined` 时若 `session.Closed || stopAt > 0` 则
   `link.Close()`，否则加入 `pending`；`Failed` 只记日志。更新 PathPool。
2. `attachJoined()`：逐个 `carrier.AddPath`，id 加入 `applying`；
   `carrier.PathOpsPending = 0` 时清空 `applying`。保持今天的两处调用点
   （`beforeWait` 与 async 的 `BeforeService`）。
3. `plan()`：`present = paths ∪ pending ∪ applying`；
   `pool.Request(now, present)` 返回的每个 `(id, inc)` 启动
   `Task.Run(fun () -> runBounded concurrency [| id |] (joinOne inc))`。
   `joinOne` 成功投递 `Joined`，`GaveUp` 或异常投递 `Failed`，绝不直接操作 carrier。
4. `wake()`：有 `inFlight` 或本轮新增 `pending` 时，async 模式调用
   `rxChannel.Wake()`；sync 模式把本轮 `poller.Wait` 上限压到 50 ms。
   无补建、无事件时等待行为与今天逐字节相同（5 s 空闲），不引入空转。

关闭路径：`stopAt` 或 `session.Closed` 时不再 `plan`；`carrier.Close()` 前再 drain 一次，
把已 `Joined` 但无人接管的 link 关闭，避免 socket 泄漏。

### 14.5 为什么按 id 集合而不是按计数

`Paths.Count + PathOpsPending + pending.Count` 在三处有 ±1 的瞬时偏差（出队与
`AddPath` 之间、`AddPath` 与队列应用之间、事件与 `pending` 之间），且无法区分
「同一 id 重建」与「另一个 id 缺失」——而 incarnation 必须按 id 单调。按 id 集合比较
消除偏差；`PathOpsPending` 只用于决定何时清空 `applying`，不参与判缺。

### 14.6 失败隔离与资源上界

- 补建失败只记日志，不关闭会话、不移除现有路径。
- `runBounded` 用 `TaskCreationOptions.LongRunning`，不占线程池；每轮最多
  `concurrency`（默认 32，上限 64）条。
- 退避 base 100 ms、cap 2 s、attempt 上限 8；255 条缺失在 8 轮 × 50 ms 内全部启动。
- 单条路径的最坏补建时延 = `socketTimeoutMs` × `maxAttempts` + 退避
  （默认 400 ms/30% 时为 31.4 s × 8）。因此 CASE-13 的 30 s 门槛只对
  「restart 后 socket 立即 RST」的常态路径成立；半开路径见 14.7。
- 内存：`inFlight ≤ concurrency`、`pending ≤ target`、事件队列 ≤ target。

### 14.7 半开路径（P3，不在第一版）

restart 后旧 socket 可能在客户端仍是 ESTAB；数量判据看不到它，但它不承载帧。
判据：某 path 的 `pathStep` 30 s 不前进且 `carrier.FramesIn` 同期不增长 → 主动
`Close()`，由现有 prune 移除后 PathPool 补建。必须先有数量补建与 CASE-13 基线，
否则无法区分「半开」与「本来就没有流量」。

### 14.8 CASE 映射与门禁

| CASE | 机制 | 证据 |
|---|---|---|
| CASE-13 路径池自愈 | PathPool 补建 + incarnation 单调 + 退避 | 3 次 restart 的 paths 时间序列 |
| CASE-4 256 路 | 目标数不变，补建不改变稳态 | 双端连接数、回环四批 |
| CASE-1 下行中位 | 只有路径数回到 256 后，中位才可比较 | 停摆率与中位分列 |
| CASE-6/10/12 | 路径数不变时逐字节同今天 | 矩阵、回环、停摆轮 |

门禁：CASE-13 通过前不部署 `--pace`；CASE-13 未通过不得称为“生产 A/B”。

### 14.9 风险

1. **重连风暴**：服务端反复重启时每轮 32 条；退避把单 id 限制到 2 s 一次。缓解是
   每轮启动上限 `concurrency` 与 attempt 上限后的 2 s 静默，不额外加全局锁。
2. **sync 模式无 `rxChannel`**：唤醒靠 50 ms 等待上限，只在 `inFlight > 0` 时生效，
   不改变空闲 5 s 语义。
3. **`applying` 的准确性**：`PathOpsPending` 是跨线程原子量，读取到 0 后清空集合；
   若下一轮又变为非 0，id 会重新进入 `applying`，不会误判缺。
4. **单测不覆盖 socket 时序**：PathPool 单测只覆盖 P-1…P-5；真实重启由回环床
   （`test_async_lifecycle.py` 增加 3 次服务端 restart）覆盖。

### 14.10 实施顺序

P1 `PathPool` + 单测（P-1…P-5，含 incarnation 连续两次重建为 1、2）→
P2 `Client` 装配 + 回环 3 次 restart（30 s 内 ≥250、60 s 内 256）→
P3 半开检测（可选，需 P2 基线）→ P4 生产只换本机客户端 →
P5 `--pace` 单变量（CASE-13 通过后）。

## 15. 第十三代：path 0 也必须补建（2026-11-20 13:40）

### 15.1 事实与断点

第十二代已实现半开检测（`PathLink.CheckAlive`：`SelectRead && Available = 0` 判 EOF，
`SessionCarrier.Service` 每秒抽检一次）并在回环上验证：`ss -K` 后客户端 `paths`
从 256 变 255 并稳定，说明检测与移除都生效。但 60 s 内没有新 JOIN。

原始日志给出唯一解释：`/tmp/tbt-g12-loop/client2.log` 与 `server.log` 在该状态下的
`path_tx` 列表从 1 开始、不含 0，两端 `paths=255`。被杀的正是握手路径 path 0。
`PathPool.Request` 的循环是 `let mutable id = 1`，且 `Joined`/`Failed` 用 `i > 0`
守卫，因此 id 0 从不进入补建面；会话只要还剩一条路径就不退出（
`Alive = paths.Count > 0 || pathOps 非空`），于是永久停在 255。

### 15.2 契约变更

`PathPool` 的补建面从 `1 .. target-1` 扩大到 `0 .. target-1`。

- `Request(now, present)` 从 `id = 0` 开始扫描；`present` 含 0 时不发。
- `Joined 0us` / `Failed(0us, now)` 与其它 id 同样记账：清退避或按
  `base <<< (attempt-1)` 退避，cap 2 s。
- `incarnation.[0]` 初值 0，首次补建发 `stride`；服务端 `JoinAdmission.Decide`
  只拒绝 `r.Incarnation = 0u || r.Incarnation <= highest.[p]`，而 `highest.[0]` 初值为 0，
  故 `inc ≥ 1` 被接受；`p = 0 < idLimit`、`activePaths = 255 < maxPaths = 256`，
  不触发 `InvalidRequest` / `Capacity`。服务端对同 id 的高 incarnation 先
  `RemovePath` 再 `AddPath`（`Server.fs:164-166`），因此半开旧连接被替换而不是并存。

### 15.3 启动窗口的重复建 0

`path 0` 在装配阶段由 `carrier.AddPath link0` 加入，而 `presentIds()` 只读
`carrier.Paths`、`pending`、`applying`。async 模式 `AddPath` 是排队，
`Paths` 在队列排空前为空，首轮 `plan()` 会误判 0 缺失并重建。判据改为：

```
path0Held = carrier.PathOpsPending > 0 || carrier.Paths 中存在 PathId = 0
```

`PathOpsPending > 0` 最多多抑制一个服务周期（毫秒级）；`prune` 移除路径 0 是
会话线程内联操作，此时 `Paths` 已不含 0 且 `PathOpsPending` 通常为 0，下一轮即可补建。
该判据只作用于 id 0，其它 id 仍按 `paths ∪ pending ∪ applying` 判缺。

### 15.4 失败隔离与回滚面

- 补建 id 0 失败只记日志与退避，不关闭会话、不移除其它路径；`Failed` 的
  指数退避把同一 id 限制到 2 s 一次，不会形成重连风暴。
- 若服务端始终拒绝（例如 `highest.[0]` 已被更高 incarnation 占据而池的回退值更低），
  客户端保持在 255 路并只记录日志；此时 `Exit 75` 仍是最终兜底：把 `paths=0`
  的全灭判据扩展为「0 缺失且连续 3 次补建失败」时退出重启，由 systemd 重建会话。
  本代先实现补建，兜底留作 15.5 的观测项，不与补建同批。
- 回滚是 `install` 上一版客户端二进制 + `systemctl restart tbtun-hnd0`，
  `<60 s`；协议、编码、信用、路径数、`rho`、GF(16) 全部不动。

### 15.5 CASE 映射与门禁

| CASE | 机制 | 判据 |
|---|---|---|
| CASE-13(b) 杀 path 0 | PathPool 覆盖 0 + 启动窗口抑制 | 60 s 内 256，日志出现 `path 0 joined` |
| CASE-13(a) 杀非 0 | 既有补建面不回归 | 60 s 内 256，日志出现对应 id |
| CASE-13 restart 3 次 | 会话重建仍走 `Exit 75` | 30 s 内 ≥250、60 s 内 256 |
| CASE-4 256 路 | 补建面扩大但目标数不变 | 回环四批仍 256、字节一致 |

门禁：CASE-13(b) 未通过不得宣称「路径池自愈」；生产只换本机客户端，HND0 不动。

### 15.6 吞吐面的诚实结论（不在本代修复面）

第十二代的三分账（`RxDelivered` 121.4 MB 对 curl 47.7 MB）与第七代 SIGQUIT
（`smux.(*stream).Read → waitRead`、18443 `Send-Q=0`、`bytes_acked` 增长）指向
本地代理的 smux 投递层。用户已限定本地代理只作上传通道、禁止作为实验对象，
因此本代不新增该层用例、不改其配置；tbtun 侧不再为吞吐做无证据的改动。
本代对下行的唯一承诺是路径数不退化，停摆与中位按 CASE-12/1 如实记录。

### 15.7 执行结果（2026-11-20 13:52 UTC）

- 实现与单测：`PathPool` 扫 `0 .. target-1`；`Client.presentIds` 加
  `PathOpsPending > 0` 抑制；单测 8 + 153 + 240 = 401 全过，构建 0 warning /
  0 error。
- 回环（sync）：杀非 0 后 `path 252 joined, paths=256`；杀 path 0 后
  `path 0 joined, paths=256`；restart 后 `Exit 75` 与新握手重建正常。
- 生产：候选 `1547a466`（3,992,936 B）安装，`tbtun.bak-g13` = `7c48936e`；
  restart 后 9 s 到 256；杀非 0 0.52 s、杀 path 0 1.55 s 回到 256。
- 遗留：默认 async 下 `--paths 2` 启动即退出（小池、无兜底），
  `--paths 256` 稳定；与本改动无关，已在 result §13.5 记录。

## 16. 第十五代：发送侧供给与 GatePass2 的证伪（2026-11-20 14:00）

### 16.1 用生产计数而非猜测排除 GatePass2

`AsyncPathLink.admitsWith pass2Limit pass notSent` 的拒绝计数在服务端 stats 中以
`gate2=pass/deny` 打印。生产运行 8 分钟的读数是 `gate2=392304/0`：pass 39 万、
deny 0。若 262144 是限制，deny 不可能恒为 0。与此同时 `beginfail=8818`，
其代码路径是 `admitted && link.TryBeginFrame` 失败，而 `BeginFrame` 的条件是
`txHead - txTail < slotCount`（16 个槽 × 2 KiB）。两个计数把“写门控上限”与
“用户态发送槽上限”分开，后者才是应用写入被挡的地方。

这条判据应写进 CASE-3：`beginfail` 与 `qhigh` 的窗口增量必须与 `gate2` 的
deny 数一起读，否则 `queued=0` 会掩盖内核与用户态的排队。

### 16.2 发送侧供给机制

下行数据的发送方是 HND0 服务端：`FlowBridge.Read` 从 target 读到源，编码后
交给 256 条承载 TCP。每条承载 socket 由 `SocketTuning.path` 配置，其中
`TCP_NOTSENT_LOWAT = 16384` 让应用最多领先内核 16 KiB；`AsyncPathLink` 的
16 个槽再给出一层 32 KiB 的应用内上限。

在 RTT 314 ms、每路约 1 Mbit/s 的生产链路上，每路 BDP 约 39 KB。
16 KiB 的领先量只够 BDP 的 0.4×，BBR 的 delivery-rate 采样长期落在
app-limited 区间，`bw` 估计停在 16–138 kbps，`pacing_rate` 随之下探，
表现为承载 `t=0 / w=0 / bl=0`、`app_limited`、`lastsnd` 数分钟。
这不是接收窗口、信用、FEC 或解码前沿的问题：本机 `rcv_wnd` 73–270 KB、
服务端 `queued=0`、`rho` 与 `cell_loss` 在该窗口内没有同步恶化。

### 16.3 参数化契约

| 模块 | 契约 |
|---|---|
| `SocketTuning.pathWith (socket, clamp, notsentLowat)` | 设 NoDelay、BBR、`TCP_NOTSENT_LOWAT=notsentLowat`、`TCP_WINDOW_CLAMP=clamp`；`path` 保持旧签名并委托 `NotSentLimit` |
| `SessionCarrier(..., ?txSlots)` | `txSlots` 缺省为 16；只影响 `PathLink`/`AsyncPathLink` 的槽数 |
| `AsyncPathLink` | 槽数组长度 = `slots`；`BeginFrame` 条件 `txHead - txTail < slots` 不变 |
| CLI | `--notsent-lowat`（4096–262144，默认 16384）、`--tx-slots`（16–64，默认 16） |

默认路径逐字节不变：缺省值就是旧常量。协议帧、路径编号、信用账目、`rho`
控制器、GF(16) 上行全部不进入本改动。

### 16.4 代价模型与禁止面

每路排队时间 ≈ `notsentLowat / 每路速率`。32 KiB 在 1 Mbit/s 下约 262 ms，
是 BDP 的 0.82×；64 KiB 约 524 ms，逼近 CASE-3 的 `srtt ≤ 1.6×minRTT` 线。
因此参数实验只允许 16384/32768/65536 三档，且必须同时读 `srtt`、`beginfail`
与承载重传率；任一恶化即回滚。槽数的内存代价是 `paths × slots × 2 KiB`，
64 槽 × 256 路 = 32 MiB/端，可接受，但不作为默认。

### 16.5 必须先修的客户端会话丢失判据

服务端重启后，客户端可能不退出（`Exit 75`）而停在 `paths=1`，对残留路径
反复 `writeBatch`：`beginfail` 单调增长、RSS 单调增长（实测 2.6 GB → 22 GB），
进程进入不可中断状态后 `kill -9` 与 `systemctl stop` 都无效，只能
`ss -K` 销毁承载 socket 迫使它退出。

这是吞吐实验的前置缺陷，不是发送侧参数的一部分。修复方向（下一代的入口）：
1. 会话丢失判据从“`paths=0`”扩展为“`paths=0`，或唯一存活路径连续 N 轮
   `beginfail` 且 `rx_ack` 不前进”；触发 `Exit 75`。
2. `AsyncPathLink` 的发送槽在会话关闭时必须有内存上界；`beginfail` 不应
   伴随无界分配。
3. 恢复路径保留 `ss -K` 作为带外手段，但不得作为常规操作。

### 16.6 CASE 映射

| CASE | 本代关系 |
|---|---|
| CASE-3 | 新增 `beginfail`/`qhigh`/`gate2` deny 的窗口增量；GatePass2 已由 deny=0 排除 |
| CASE-14 | 衰减判据改为好轮序列首两轮 vs 末两轮，停摆轮归 CASE-12 |
| CASE-12 | 停摆 5/10 复现，与历史一致 |
| CASE-1 | 本代无提速声明；基线好轮中位 0.40 MB/s |
| CASE-13 | 修复方向 16.5 是新的门禁项；服务端重启 A/B 之前必须先通过 |

## 17. 第十六代：半开路径与会话丢失判据（2026-11-20 14:30）

### 17.1 缺陷的精确形状

§16.5 把“服务端重启后客户端停在 `paths=1`”记为待修项。逐行核对后，它不是一处
判据写错，而是四处都在用“数量”代替“进展”：

| 位置 | 现行判据 | 半开时的结果 |
|---|---|---|
| `SessionCarrier.Alive` | `paths.Count > 0 \|\| pathOps 非空 \|\| KeepAlive()` | 1 条死路径即真 |
| `Client` 的 `KeepAlive` 谓词 | `carrier.Paths.Count < pool.Target` | 永远为真：补建失败也活着 |
| `Client.housekeeping` 退出条件 | `PathOpsPending = 0 && Paths.Count = 0` | 计数为 1，永不 `Exit 75` |
| `PathLink.CheckAlive` / `AsyncPathLink` | `Poll(0, SelectRead) && Available = 0` | FIN/RST 未达时为假，`alive` 保持 |

平台层只有 `TCP_NOTSENT_LOWAT` 与 `TCP_WINDOW_CLAMP` 两项能力
（`Platform.fs` 的 `PlatformCapabilities`）。没有 `TCP_USER_TIMEOUT`，所以内核
不会因为“有未 ACK 数据而 30 s 收不到确认”主动报错。于是半开路径的内核发送缓冲
被填满后：`PathLink.TryBeginFrame` 因 `TxIdle = false` 返回 false，
`AsyncPathLink.BeginFrame` 因 `txHead - txTail = slotCount` 返回 false，
`SessionCarrier.writeBatch` 对每帧 `beginFail + 1`（`SessionCarrier.fs:229`），
而路径既不移除、会话也不退出。这就是 `beginfail` 1.22 亿与 RSS 22 GB 的来源。

### 17.2 选定的修复面

**在内核层给每条承载 socket 设 `TCP_USER_TIMEOUT`，而不是在应用层加轮询判据。**

理由：

1. 它计时的对象正是故障条件——“有已发未 ACK 的数据，且确认迟到”。空闲路径没有
   未 ACK 数据，不会被误杀；这一条用应用层“N 轮无进展”难以表达，因为空闲会话
   同样无进展。
2. 超时后内核让该 socket 的下一次读写返回 `ETIMEDOUT`，既有代码已经有正确的
   反应：`PathLink.Send` 返回 `Closed` → `Close()` → `prune()` 移除 →
   `Paths.Count` 归零 → 既有 `Exit 75` 生效；`AsyncPathLink.PumpTx` 直接
   `Volatile.Write(&alive, 0)`。
3. 零轮询成本，不改会话线程的时序。

**不改退出判据，不改 `KeepAlive` 谓词。** 它们本身没有错：只要“死路径最终会变成
`Alive = false`”，`Paths.Count = 0` 就是正确的退出条件。加第二套应用层判据会
让“何时算死”出现两个真理来源。

### 17.3 契约

| 单元 | 契约 |
|---|---|
| `Platform.HasTcpUserTimeout` | Linux `setsockopt(IPPROTO_TCP, 18)` 被接受时为真。探测与既有 `trySetOption` 同模式：在临时 socket 上试设 30000 ms，失败即假 |
| `SocketTuning.UserTimeoutMs` | 产品常量 30000。0 表示不设置 |
| `SocketTuning.userTimeoutMsFor` | 把请求夹到 `[0, 300000]`；0 保持 0（关闭），其余下界 1000 |
| `SocketTuning.pathWith` | 新增 `userTimeoutMs` 参数；`HasTcpUserTimeout` 为真且值 > 0 时才设置 |
| `SocketTuning.path` | 委托 `pathWith (socket, clamp, NotSentLimit, UserTimeoutMs)`，产品默认从此带上保护 |

作用面：`pathWith`/`path` 只用于承载 path socket（客户端 `bootstrap`、`joinOnce`，
服务端路径接受）。`SocketTuning.local` 是 flow socket，不在此列，行为不变。

### 17.4 30 s 的依据与代价

生产 RTT 314 ms、承载丢包 18–27%。内核 RTO 起点约 200 ms，指数退避下 5–6 次
重传约 25–40 s。30 s 未 ACK 一个字节，该路径在数据面已经死了。

误杀的代价是可逆的：路径被 `prune` 后由 `PathPool` 重 JOIN，第十三代生产实测
0.52 s（非 path 0）与 1.55 s（path 0）回到 256 路。误杀一条健康路径的最坏
结果是一次 1.5 s 的重建，而不是 22 GB 的内存增长。

不做 BDP 自适应：`TCP_USER_TIMEOUT` 是“确认迟到上限”，不是队列长度，与带宽
无关；按线路写常数正是 AGENTS.md 禁止的。

### 17.5 失败隔离与回滚

| 情形 | 行为 |
|---|---|
| 非 Linux 或内核对 opt 18 返回 `ENOPROTOOPT` | `HasTcpUserTimeout = false`，不设置，行为与现在逐字节相同 |
| `--user-timeout-ms 0` | 不设置，等于旧行为；参数级回滚，不需要换二进制 |
| 超时误杀 | 路径与 `PathPool` 自愈；若会话真的没了，`Exit 75` 后 systemd 拉起 |
| 会话关闭 | 不变：`carrier.Close()` 逐条 `Close()` |

不碰：协议、路径数、8 MiB 信用、`rho` 语义、GF(16) 上行、`TCP_NOTSENT_LOWAT`
默认值、`GatePass2Limit`、FEC 自适应默认。

### 17.6 CASE 映射

| CASE | 本代关系 |
|---|---|
| CASE-13 | 新增“服务端 FIN/RST 被丢弃”的半开场景：旧版应停在 `paths=1` 且 `beginfail` 增长（对照），新版应在超时 + 60 s 内退出并重建 256 路。原有 3 次 restart 复用同一判据 |
| CASE-3 | 有载 `srtt` 不应改变：USER_TIMEOUT 不参与发送节奏，只在死路径上触发 |
| CASE-2 | 承载重传率不应改变；本代仍不合并第四代与第六代的冲突数字 |
| CASE-1 | 本代不声明提速；它是后续发送侧 A/B 的前提 |

### 17.7 验证顺序

1. 进程级确定性实验（不经隧道）：一对 TCP，`setsockopt` 后冻结对端（`SIGSTOP`），
   写满发送缓冲，本端 `send` 必须在超时 + 2 s 内报 `ETIMEDOUT`；同一代码在
   `--user-timeout-ms 0` 下必须一直是 `EAGAIN`。这是修复有效性的直接证据。
2. 回环 256 路：服务端 `kill -9` 后客户端应 `Exit 75`（既有行为不回归）。
3. 回环半开：netns 内丢弃服务端方向的 FIN/RST（或对客户端侧入向 `dport 39444`
   的全部包做 `DROP` 后杀服务端），旧版停在 `paths=256` 并 `beginfail` 增长，
   新版在 30 s + 余量内退出。
4. 生产：换本机客户端，做 3 次服务端 restart，记录 `first250`/`first256` 与
   `paths=1` 是否出现；RSS 必须始终在百 MB 量级。

