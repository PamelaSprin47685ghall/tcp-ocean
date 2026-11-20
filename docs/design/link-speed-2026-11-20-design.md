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
`8 MB × 活跃流数`。实测生产 server RSS 70 MB，因为 mihomo `max-connections: 1` 把
多条 curl 复用成一条 VLESS/smux 长连接，tbtun 只看到 1 条 flow。若并发流数上升，
按此顺序收边界：把 `FlowRecvBuf` 收到 1.25 BDP、按流数缩容、或只在接收侧设 rcvbuf
（`local` 只用于 flow socket，不用于 256 条承载路径）。这条上界由 CASE-11 验证。

## 2. 目标与非目标

目标：把 flow socket（target 侧与本地应用侧）的接收窗口由内核自动调优改为显式
`SO_RCVBUF`，让单流吞吐脱离 `rcvbuf / RTT` 的 64–128 KB 量级，使生产下行在 329 ms
RTT 下达到基线 2 倍以上（实测 3.4×），同时不让 SRTT 自致倍增、不让承载重传高于旧版。
验收以 `docs/cases/link-speed-2026-11-20-cases.md` 为准；CASE-2 的 5% 重传线当前未达，
见 §6 与本轮 result 的“条件性上线”。

非目标：
- 不改 256 路、`:39443`、TCP-only、TFO/BBR、mihomo `max-connections/min-streams`。
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
