# P1 验收结果（2026-11-18，四路径修复后）

## 决定性修复：IO 线程池共享队列丢弃路径

`AsyncPollerGroup` 原实现：

```fsharp
let pending = ConcurrentQueue<IPathIo>()
...
while pending.TryDequeue(&path) do
    if path.ThreadIndex = index && not path.Closed then local.Add path
```

每个 IO 线程都从同一个 `pending` 队列取路径，但只接受 `ThreadIndex` 等于自己的路径；其余路径被**丢弃且不重新入队**。`ThreadFor pathId = int pathId % width`，因此每条路径只有 1/4 概率被正确线程取到。4 条路径通常只剩 1 条被驱动。

修复：每线程一个 `ConcurrentQueue<IPathIo>`，`Add` 按 `ThreadIndex` 入对应队列，`Loop` 只从自己的队列取。

## 二进制

- `/tmp/tbt-bin/aot/tbtun` sha256 `c8e3bf96199ab2cb26860123e4e6217aeb28ffcabb653dfdf69f90bd982fa354`
- `/tmp/tbt-bin/jit/tbtun.dll` 同步构建（`-p:PublishAot=false`）

## 4 路 e2e（`/tmp/tbtun-e2e.zHvZaS`）

命令：`TBT_BIN=/tmp/tbt-bin TBTUN_ASYNC_MODE=true ALLOW_DIRTY=1 HOST_QUIET_BYPASS=1 tests/e2e/run.sh 20 200 0 4 10`

| 项 | 修复前 | 修复后 |
|---|---|---|
| 握手 | 5.30 s | 5.53 s |
| echo 8 流 | 89.8 | 126.1 |
| echo 64 流 | 63.1 | 91.9 |
| ping p50 / p99 | 40.8 / 41.4 | 40.4 / 41.0 |
| iperf up | 85.3 | 142.3 |
| iperf -R | 136.0 | 142.7 |
| iperf -P 4 | 84.8 | 140.6 |
| iperf -R -P 4 | 136.0 | 143.1 |

路径分布：

```
client-mux : path_tx=0:70215130,1:69988730,3:70087780,2:70097685
client-bulk: path_tx=0:186805470,1:187132335,2:186778585,3:187040360
```

修复前 mux 只有 path 1（184 MB），bulk 只有 path 3（456 MB）。

## P1-5 单流稳定性（5 轮 × 15 s，`paths=1`）

up 样本（每轮两个）：86.0, 86.4, 86.0, 86.2, 85.9, 86.4, 86.0, 86.4, 86.0, 86.3

- n=10，均值 86.16 Mbit/s，总体标准差 0.191，相对 **0.22 %**

down 样本：136.4, 136.7, 137.4, 137.3, 136.6

- n=5，均值 136.88 Mbit/s，标准差 0.397，相对 **0.29 %**

单路径测试床 `< 20 %` → **达标**。生产 256 路不以此验收；同一 fast.com 目标五轮各 15 秒，三轮无 HTTP 响应，失败计作 0 Mbit/s 后总体相对标准差 **124.9%**，仍未达标。见[生产测量](../cases/production-fastcom-2026-11-19.md#双端包头复核)。

## P1-6 多流（`paths=3`，`/tmp/tbtun-e2e.vywIFM`）

up 143.6、-R 143.6、-P 3 143.4、-R -P 3 144.0、up 143.5

旧倍率 `143.5 / 86.16 = 1.67×` 的分母来自单承载测试，不是同一组内的单流结果。保持三条承载，上行三流／单流为 `143.4 / 143.6 ≈ 1.00×`；下行为 `144.0 / 143.6 ≈ 1.00×`。原定倍率未验证，不据此要求改变调度。

生产口径（256 路）：3 并发流合计 ≈ 单流，倍率 ≈ 1.0×。

**2026-11-19 口径更正。** 这条不构成缺陷，也不能用来推瓶颈。tbtun 的职责就是
把一条本地 TCP 流分布到 256 条公网路径。「单流」只存在于本地 loopback 到
tbtun 之间；增加本地流数不会增加公网承载数。这次倍率约 1.0×，不能据此判定
承载已经跑满，更不能归因于所有流共用一条公网 TCP 拥塞窗口。测试床跨承载数的
旧比较也不能用来推断 256 路生产瓶颈。

## P1-7

- 不变式 `SrttNs >= MinRttNs`：修复后 client-bulk `srtt_ms=110 minrtt_ms=40`、client-mux `srtt_ms=120 minrtt_ms=40`，均成立。
- `rtt_skipped` 修复前：bulk 2377（约 25 s，≈ 95/秒）、mux 161（≈ 1.6/秒）。
- 根因：main Karn 分支只检查 `ack - 1` 一步；该步被一次 BOOSTER 覆盖就整次 ack 放弃采样。四路径 bulk 下 BOOSTER 频繁，采样几乎停止。
- 修复（`TxHalf.fs` main 分支）：与 `newRepair` 分支一致，从 `ack` 向 `peerAck` 回退，取最新的 `lastRepairNs = 0` 的步作为样本；只有整个 ack 跳跃内的步都被修复过才计一次 skip。
- 第一次修复（无按步标记，`/tmp/tbtun-e2e.hncZfv`）：mux/bulk `rtt_skipped` 均为 0，但那是回退无界、采到跨黑障旧步的结果；SRTT 被高估到 110–120 ms。
- 第二次修复（`/tmp/tbtun-e2e.CdrbJA`）：main 分支对 NACK 的 `span` 内每一步各发 BOOSTER 并写 `lastRepairNs`，Karn 只跳过“最新 8 步全部被修复”的 ack。mux 14（0.47/秒）、bulk 85（约 1.4/秒），mux 达标、bulk 略超。
- 最终修复（深度 16，`e8d7ce09…`，`/tmp/tbtun-e2e.y4k2F2`）：
  - mux `rtt_skipped=13`（约 30 s，0.43/秒），`srtt 120 / minrtt 40`。
  - bulk `rtt_skipped=41`（约 60 s，0.68/秒），`srtt 117 / minrtt 43`。
  - 四路径 `path_tx` 均匀，吞吐 up 141.1 / -R 142.7 / -P 4 141.1 / -R -P 4 143.1 / up 141.8。
- 验收 `rtt_skipped` 增速 < 1/秒 → **达标**（mux 0.43/秒、bulk 0.68/秒）。

上述是四路径测试床。独立复算 `/tmp/tbt-flow-public.vo5u90` 与 `/tmp/tbt-flow-public.b9UFzB` 中 HND0 的 256 路诊断会话：第一批在 75.287 秒内增加 40 次（0.53 次/秒），第二批在 75.153 秒内增加 92 次（1.22 次/秒）。两批双方的采样点均有 `srtt >= minrtt`，但第二批 HND0 超过 `< 1/秒`，生产口径不能关闭 P1-7。这里用的是会话统计窗口，不是三轮下载各自的速率。

## P1-8

`recovered / frames_in`：mux `392/411031 = 0.095 %`，bulk `196/334623 = 0.059 %` → **达标**（< 10 %）。

两批 256 路诊断会话的本机下行窗口增量分别为 `2357/60238 = 3.91%`、`2376/57737 = 4.12%`。两批均低于阈值；填补阻塞流交付的缺口是否频繁依赖方程恢复，是另一个分母不同的问题。

## P1-9

`csvc_us` 是**每秒累计的 Service 时间**，不是单次延迟；`iter_us_max` 才是单次迭代峰值。

19.6 分钟连续样本（`/tmp/tbtun-e2e.G7UgQi/server-bulk.log`，n=1177 秒）按 147 秒分桶：

| 桶 | 阶段 | csvc_us 均值 | max |
|---|---|---|---|
| 0–146 | echo/ping | 18 883 | 28 185 |
| 147–293 | iperf up 前段 | 20 358 | 20 893 |
| 294–440 | 过渡 | 87 787 | 171 276 |
| 441–587 | bulk | 172 799 | 251 887 |
| 588–734 | bulk | 161 241 | 198 715 |
| 735–881 | -R 轻载 | 50 579 | 57 272 |
| 882–1028 | -R 轻载 | 51 568 | 52 688 |
| 1029–1175 | -P 4 | 120 634 | 224 790 |

- 单次迭代：`iter_us_max` p99 = 1 911 µs、max = 5 479 µs；迭代间隔 `svc_gap_us_max` p99 = 10 161 µs、max = 100 117 µs。
- 结论一：**无长尾**。单次最坏 5.5 ms，远低于 100 ms。
- 结论二：`csvc_us` 随吞吐线性增长。142 Mbit/s bulk 下 172 ms/秒 ≈ 17.9 % CPU（每秒 9 169 次迭代、19.5 µs/次）；旧 20 Mbit/s 基线下同一实现为 20–24 ms，与 TODO 的「现状」一致。
- 因此 TODO 字面的「csvc_us p99 < 100 ms」只在旧吞吐下成立；在当前约 7 倍吞吐下不成立（p99 ≈ 180–200 ms，`/tmp/tbtun-e2e.y4k2F2`、`xlplNK`、`K7LhNf` 三份最终主线日志一致）。要把它压到 100 ms 以下，需要把 AEAD seal/open 分片到 ComputePool（counter 顺序分配、并行计算），属于 P1 的下一个结构性改动。

### P1-9 生产口径与指标纠错（2026-11-19，异步已默认）

`csvc_us` 只由 `Server.run` 主循环统计 `carrier.Service`。异步模式下每个会话的 Service
在各自的 `AsyncSessionCoordinator` 线程上，该字段在生产 stats 里恒为 0，**不能用作验收**。
异步会话的真实延迟指标是协调器每轮 `Service` 的耗时，现由
`AsyncSessionStats.DescribeWindow()` 输出到 stats 行：

| 字段 | 含义 |
|---|---|
| `async_cycles` | 本统计窗口内完成的服务轮数 |
| `async_svc_us` | 本窗口 Service 累计微秒（CPU 占用，随吞吐线性增长） |
| `async_svc_us_max` | 本窗口单次 `SessionCarrier.Service` 执行耗时峰值 |
| `async_wait_us` | 本窗口 Poller 等待累计微秒 |
| `async_faults` | 累计会话故障数（不随窗口重置） |

实测（本地客户端，`--stats-s 1`，生产 256 路，2026-11-19 01:16，30 s 下载
`down_Bps=2 388 765` = 19.11 Mbit/s）：

| 指标 | 空闲 | 19.11 Mbit/s 下载中 |
|---|---|---|
| `async_svc_us_max` | 405 µs | **5 111 µs**（12 个窗口的峰值） |
| `async_cycles` 每秒 | 21 | 约 133 |
| `async_svc_us` 每秒 | 514 | 22 340（2.2 % CPU） |
| `srtt_ms` / `minrtt_ms` | — | 313 / 310 |
| `recovered / frames_in` | — | 1 704 / 39 401 = 4.3 % |

单次 Service 峰值 5.1 ms，**远低于 100 ms**。`async_svc_us` 是 CPU 占用率，不是延迟；
这只覆盖 `SessionCarrier.Service`；协调器在计时前从 IO 通道取帧并解码，故该字段不包含这段时间，也不能替代端到端延迟或完整轮次的峰值。短测的已采窗口低于 100 ms，但尚未覆盖原待办的 30 分钟连续有载观察。不能用这一短测或不同口径的 19.6 分钟测试床日志关闭长时观察项。原指标若按字面继续使用，会把「服务了更多流量」误判为「延迟变差」。

## 复测（深度 16，2026-11-18）

- 单流 4 轮 × 15 s（每轮两个 up 样本）：n=8，均值 86.162 Mbit/s，标准差 0.1847，
  相对 **0.214 %**（验收 <20 %）。
- 三流 15 s：up 143.5 / 143.4，-R 143.4，-R -P 3 144.0；倍率 143.5 / 86.0 = **1.67×**。
- `dotnet test tests/Tbt.Session.Tests` → **237 / 237**，含 karnCase 的
  `RttSkipped > 0` 与 `r < 6000`（并行补发把 6112 ms 压到阈值内）。

## 收尾状态

测试床结果不等于生产验收。P1-5 生产单流稳定性未达标；P1-9 长时延迟、P2-10 HND0 的 SSE2 专项对照、P2-12 整机重启后路由尚缺验收。P2-10 见
`docs/cases/slow-consumer-cpu-stall.md` 的全硬件指令 A/B 一节；P2-11 见
`docs/cases/test-build-latency-2026-11-18.md` 的 nullness 结论；P2-12/13 见
`docs/cases/deployment-route-and-cleanup-2026-11-18.md`。

## 吞吐参数 A/B：窗口大小（2026-11-18，best-effort）

窗口不是吞吐杠杆。单流 `paths=1`、20 ms OWD、200 Mbit/s shaper、15 s，
同一 AOT 二进制，只改 `WINDOW`：

| WINDOW | up | -R | -P 1 | -R -P 1 | up |
|---|---|---|---|---|---|
| 1024 | 86.0 | 136.0 | 86.0 | 136.0 | 86.4 |
| 2048 | 86.0 | 136.6 | 86.4 | 137.8 | 86.3 |
| 4096 | 86.0 | 137.4 | 86.4 | 139.1 | 86.2 |

- 三档在噪声内相同，保持默认 1024。
- 四路 142 Mbit/s 已接近 `rho=0.25` 的理论上限：142 × 1.25 = 177.5，
  对 plain TCP 4 流基线 189.6 为 94%。再提吞吐只能改 FEC 比率，而
  `rho=0.25` 是既定产品策略，不动。
- 单流 up 86 Mbit/s（106 Mbit/s 含 FEC）是多路径配置下的非典型场景；
  生产配置走多路径，四路/256 路是相关口径。

## 256 路（生产配置）吞吐（2026-11-18，best-effort）

`MAX_PATHS=256 HANDSHAKE_S=120 tests/e2e/run.sh 20 200 0 256 10`，256 条路径全部协商：

| 运行 | session_up | iperf up | iperf -R | 末轮 up | 服务端 RSS |
|---|---|---|---|---|---|
| 原始写轮预算 256 | 6.21 s | 141.9 | 143.5 | 142.8 | 114 MB |
| 预算改为 `paths*4` | 7.56 s | 142.0 | 143.1 | **38.0** | 183 MB |
| 回滚后复测 | 8.38 s | 142.8 | 134.1 | 141.7 | 219 MB |

- 256 路 bulk 上传/下载稳定在 142 Mbit/s 左右，与 4 路相同；路径数不影响 bulk 吞吐。
- 把 `writeRound` 预算从固定 256 改为 `paths*4`（256 路时 1024）会提升 echo 64 流，
  但最后一轮 bulk up 从 142.8 崩到 38.0，已回滚。
- `iperf3` 并行流上限 128：`tests/e2e/run.sh` 新增 `iperf_par=min(paths,128)`，
  256 路会话用 128 个 iperf 流测试。`-P 128` 在 256 路下只有 5–6 Mbit/s，
  是 128 个并行流经 256 条路径的固有开销，不影响单流/少量流的 bulk 吞吐。

## 上传吞吐：端点限速与链路能力的区分（2026-11-19）

同一 30 MiB 载荷、同一部署（AOT `c882b2ba`）、每次不超过 30 s，只换上传目标：

| 路径 | 结果 | 完整性 |
|---|---|---|
| tele2 `upload.php` #1 | 8.39 Mbit/s | 30 MiB 完整 |
| tele2 `upload.php` #2 | 8.39 Mbit/s | 30 MiB 完整 |
| tele2 `upload.php` #3 | **2.00 Mbit/s** | 仅 7.5 MiB，30 s 截止 |
| **纯隧道 #1** | **21.62 Mbit/s**（2 701 940 B/s，11.64 s） | 服务端计数 31 457 280 |
| **纯隧道 #2** | **17.89 Mbit/s**（2 235 857 B/s，14.07 s） | 累计 62 914 560 |

纯隧道指本地 `curl --proxy 127.0.0.1:17893` → tbtun → HND0 上一个临时
`python3` PUT 接收端（端口 18081，测完即停并删除）。HND0 本地代理的路由是
`MATCH,DIRECT`，因此这条路径除 tbtun 与本地代理中转外没有第三方参与。

- tbtun 链路本身的上传能力是 **17.89–21.62 Mbit/s**，为 9 Mbit/s 目标的
  **199–240 %**。服务端按字节计数确认两次都收到完整的 30 MiB，没有截断。
- tele2 免费端点限速且不稳定（8.39 → 2.00 Mbit/s，第三次只收下 7.5 MiB），
  这是第三方服务的限制，不是 tbtun 的缺陷。
- 测量期间两端 `paths=256`、`queued=0`，本地 `cell_loss ≤ 0.0007`，
  `async_svc_us_max = 300–349 µs`；HND0 侧 `recovered / frames_in =
  3 340 / 109 481 = 3.1 %`。
- 结论：上传验收以纯隧道数据为准，第三方测速端点只能作旁证。用 tele2 的
  数字判断 tbtun 会把远端限速误记成产品缺陷。
