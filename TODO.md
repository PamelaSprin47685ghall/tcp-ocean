# TODO — 剩余问题清单

> **状态：P1-5 生产单流稳定性、P1-7 HND0 跳过 RTT 采样的速率未达标；P1-9 长时延迟、P2-10 HND0 的 SSE2 专项对照、P2-12 整机重启后的转发和路由仍缺验收证据。P1-6 的历史倍率也未达到原阈值。**
>
> 逐项结论：
> - **P0-1～P0-4**：`Tbt.Async` 独立构建 0 警告/0 错误；生产 TCP 会话默认进入异步数据面；SPSC 100 万次、WorkStealing 20 万件、ComputePool、BufferPool 四类单测齐全，`Tbt.Async.Tests` 8/8。256 路异步回环双向逐字节校验 4 MiB，客户端、服务端分别验证 3,284、3,928 帧，均为 0 次认证失败；不以 `sessions_failed=0` 代替帧认证计数。独立复跑抓到 JOIN 准入跨线程读取路径列表导致进程崩溃；修复归属后，连续 16 次回环会话各建齐 256 路并正常关闭。此修复与服务端等待单位修复均未部署。TUN 仍走共享单线程，JOIN 客户端引导仍用有时限的阻塞 socket；生产 TCP 数据面默认异步，不等于所有模式和阶段都已无轮询。
> - **P1-5**：测试床单路径结果不能代替生产验收。同一 fast.com 目标的五轮生产测试各 15 s，两轮只收到部分数据，三轮无 HTTP 响应；失败计作 0 Mbit/s，五轮总体相对标准差 **124.9%**（门槛 < 20%）。两组新建的 256 路公网影子会话对照中，异步生命周期候选的速率优势在交换端口后未复现。启动、关闭、通知回收和发送唤醒各有本机回归证据，不算生产提速。独立审计发现接收缺段与单流补信用等待同时存在；未修改生产信用或连接配置。详见 [生产下行独立审计](docs/cases/production-fastcom-2026-11-19.md#独立审计与回归补充)。
> - **P1-6**：原测试床 1.67× 混用了三条承载与一条承载，不能作多流验收。保持三条承载时，记录中的上行单流与三流分别为 143.6／143.4 Mbit/s；生产 256 路的历史三流合计 9.92 Mbit/s，约等于当时单流。没有实测达到原定 1.5×。但一条本地 TCP 流已经分布到 256 条公网承载，增加本地应用流不会增加承载数；约 1.0× 本身既不是缺陷，也不能证明单流已占满承载。不为追求倍率而改变调度策略。
> - **P1-7**：两批 256 路诊断会话的已采样行均有 `srtt >= minrtt`；HND0 `rtt_skipped` 增量分别为 0.53、1.22 次/秒。第二批超过原门槛 1 次/秒，不能按测试床结果关闭，不为通过计数而放宽 Karn 采样条件。
> - **P1-8**：两批 256 路诊断会话的本机下行窗口 `Δrecovered / Δframes_in` 为 3.91%、4.12%，均低于 10%。这是该指标的通过样本，不代表流内停顿已经消失。
> - **P1-9**：改用异步会话单次 `Service` 峰值 `async_svc_us_max`。19.11 Mbit/s 的生产短测中，已采窗口的峰值为 5.1 ms；19.6 分钟的历史测试床记录不是同一异步生产指标。没有 30 分钟的同口径证据，不关闭长时观察项。累计 `csvc_us` 不能作延迟验收。
> - **P2-10**：测试床记录比较的是开启与关闭**全部**硬件指令，且未记录启用组实际进入 SSE2 专用分支；不能把差值归到 HND0 的 SSE2 GF(16)。真实上行的同条件专项对照仍未证实。
> - **P2-11 / P2-13 / P2-14**：按构建、清理和文档证据记录。
> - **P2-12**：规则当前生效（`ip route get 45.11.1.62` → `enp8s0`，`8890` 规则存在）。专用代理从 `/run` 的 `tmpfs` 读取配置，在已检查的启动机制中未找到生成者；同次引导内重启过代理，不能证明整机重启后该配置和路由仍可用。**整机重启尚未实测验收。**
>
> 证据索引：
> - P0-1～P0-4：`docs/async-rewrite/06-verification-log.md`、`tests/Tbt.Async.Tests`（8/8）
> - P1-5～P1-9（含 2026-11-19 生产口径与指标纠错）：`docs/async-rewrite/10-p1-acceptance.md`
> - P2-10（SSE2 A/B）：`docs/cases/slow-consumer-cpu-stall.md`
> - P2-11（测试编译）：`docs/cases/test-build-latency-2026-11-18.md`
> - P2-12/13（路由与清理）：`docs/cases/deployment-route-and-cleanup-2026-11-18.md`
> - 异步默认、事件驱动与本地代理诊断：`docs/async-rewrite/12-event-driven-default.md`
> - 生产看门狗：`deploy/README.md`
>
> 约束均未改：256 路、TCP-only、`:39443` 静默、无 TLS/REALITY、TFO/BBR、smux 配置；
> `rho=0.25` 语义保留在 `TBTUN_FEC_ADAPTIVE=0`，默认自适应；GF(16) 上行未改。

> **2026-11-21 生产轮（prod-hnd0 合入）**：会话老化下行衰减定位在载波 TCP 连接状态（长连接 BBR 退出 STARTUP 后带宽估计在约 20 % 丢包下持续缩小）。
> 修复与改进：HasPath 跨线程 NRE（路径 id 快照）、minRTT 窗口不再棘轮、客户端滚动路径刷新（空闲优先）、服务端 `--cc-nudge-s` 滚动重置拥塞控制、
> 大面积断路后会话保活（`--no-path-grace-s`）、初始 JOIN 未完成时不退出、新会话首条路径入队前不被判死。部署：两端 ChaCha8（`--suites chacha8,chacha20`）+ `--cc-nudge-s 30`。
> 未解决：刷新年龄抖动（50 %）实测更慢，默认关闭；重排序相关 sysctl 无明确收益，已恢复。

更新时间：2026-11-21
项目路径：`~/tcp-ocean`（本地）、`/root/tcp-ocean`（HND0）

## P0：异步架构（方案 B）未落地

### 1. Tbt.Async 编译不通过
现象：`dotnet build src/Tbt.Async/Tbt.Async.fsproj` 约 20 个错误。
位置与原因：
- `AsyncCodec.fs`：`Decoder.OfferData` 不存在（现有 API 是 `OfferSource` / `OfferParity`）；`CodeField` 未导入；`OfferParity` 返回 `EquationResult` 而非 `bool`。
- `FrameAdapter.fs`：`Cell` / `Meta` 是占位解析，未接 `Tbt.Wire` 的真实帧格式与 Meta 布局。
- `IOThread.fs`：`handleReceive` / `handleSend` 递归引用的作用域问题；`Thread` 可空性（F# nullness）。
- `ComputePool.fs` / `AsyncSession.fs`：`WorkerStats` / `IOThreadStats` / `AsyncSessionStats` 的 struct 字段可变性。
- `SPSCQueue.fs`：`CachePadding` 已改为 class，但 `Volatile` 读写与泛型约束仍需复查。
验收：`dotnet build src/Tbt.Async/Tbt.Async.fsproj -p:PublishAot=false` 零错误。

### 2. 异步路径未接入生产入口
现象：`AsyncMode.fs` 与 `AsyncSessionCoordinator` 未被 `Server.fs` / `Client.fs` 调用；`TBTUN_ASYNC_MODE` 环境变量目前没有效果。
待办：
- 在 `Server.run` / `Client.run` 中按特性开关创建 `IOThreadPool` + `ComputePool` + `AsyncSessionCoordinator`。
- 用协调器替换 `carrier.Service poller` 主循环；保留同步路径作为回退。
- 将 `Tbt.Async` 加入 `Tbt.Cli.fsproj` 的 ProjectReference。
验收：设置 `TBTUN_ASYNC_MODE=true` 时日志出现 async 启动行，且能建立 256 路会话。

### 3. FrameAdapter 需要真实帧解析
待办：
- 按 `Tbt.Wire` 的 40B Meta 布局解析 `CellType`、`StepId`、`Slot`、`StepUsed`、`Flags`。
- 接入 `CellOpener` 做认证；坏帧计数。
- 覆盖 DATA / PARITY / BOOSTER / WIN_ACK / CTRL / FLOW_STATE 分派。
验收：异步模式下收发 1000 帧无认证错误。

### 4. 无锁结构与线程池无单元测试
待办：
- SPSCQueue：单生产单消费 100 万次，顺序与计数一致。
- WorkStealingQueue：多窃取者下不丢任务、不重复执行。
- ComputePool：高/普通/低优先级顺序；工作窃取生效。
- BufferPool：租借/归还无泄漏。
验收：测试可重复通过，含并发压力用例。

## P1：生产速度仍未达标

### 5. 单流速率不稳
现状：短测 19.06 Mbit/s，连续 3 次 8.57 → 7.71 → 3.29 Mbit/s；重启后 8.52 Mbit/s。
独立复核：普通 TCP 的部分样本确认 20 秒内交付至少 25.91 MB，但整文件未收齐，不用于估算隧道开销。256 KiB 补信用候选同目标交错对照中位数为 3.64 Mbit/s，原阈值为 4.88 Mbit/s，未改生产默认。会话描述符回收、异步发送唤醒和首次尾部 NACK 的截止修复有本机回归证据，没有单变量公网提速证据。[公网跟踪](docs/cases/production-fastcom-2026-11-19.md#缺口最终由什么填补)确认已解出的后续数据被流内缺口拦住；181 次实际缺口填补中有 161 次由编码方程恢复。尚未量化修复各阶段的延迟，也未验证可重复提速。生产未切换。

发布边界：[当前工作树](docs/cases/production-fastcom-2026-11-19.md#工作树与已测候选的区别)另有 32 KiB 写闸门、软件发送队列记账、32 个每路发送槽及 512 帧的 256 路发送轮预算。这四项不在此前的生命周期隔离候选里，未通过官方矩阵和单变量公网对照，不能随生命周期修复一起上线。
待查：
- 按同一解码步对齐缺口出现、NACK、修复包发出与解码完成，区分修复触发、排队和传输时间。
- 状态累积点（hold 队列、decoder 前沿、encoder 历史、未释放 reserve）。
- 记录前一轮的尾部是否清空；已有两批样本中，前一轮积压接近清空后，下一轮才收到 HTTP 首字节。
- 长连接下 `HeldParities` / `rx_base` / `recovered` 的增长曲线。
验收：保持事件驱动、256 路 TCP-only、8 MiB 单流信用、32 MiB 池和既有生产路由。对 HND0 直连验证过的同一 IPv4 fast.com 目标，在现有公网路径交错测试，单次不超过 60 秒。须取得可重复的吞吐提升，且连续 5 次 15 秒本地单流测试的相对标准差（标准差除以均值）< 20%。保留失败样本，未达标不切换生产。

### 6. 多流并发反而变慢
现状：3 流并行总吞吐 7.72 Mbit/s，低于单流 19.06 Mbit/s。
待查：
- 当前本地代理 `max-connections: 1` 下，多流是否被 smux 串行化。
- `FlowBridge.Read` 在多个 slot 间是否公平；单轮是否只读一遍。
- 会话级窗口与 per-flow 信用是否互相挤压。
验收：3 流总吞吐 ≥ 单流 1.5 倍。

### 7. SRTT < minRTT 不变式违反
现象：服务端 `srtt_ms` 152–242，`minrtt_ms` 310–318；`rtt_skipped` 高达 140。
待查：
- `TxHalf.OnPeerAck` 的 seal-custody 采样与 10 秒窗口重置时序。
- 高 BOOSTER 场景下“找不到干净步骤”导致 SRTT 长期不更新。
- 时钟源一致性（`CachedClock` / `FreshNs`）。
验收：任意时刻 `SrttNs >= MinRttNs`，`rtt_skipped` 增速 < 1/秒。

### 8. FEC 恢复率偏高
现象：客户端某些秒 `recovered / frames_in` 达 15–47%；服务端 BOOSTER 1574。
待查：
- 是真的丢包，还是 20 ms `stallNs` 把 330 ms RTT 下的乱序误判为丢包。
- 256 路中是否存在少数慢路径拖累整体。
- `rho=0.25` 与实际丢包是否匹配（产品策略保持 0.25，不改码）。
验收：稳态下行 `recovered / frames_in < 10%`。

### 9. 服务循环峰值需持续观察
现状：Read budget 16→4096 后 `csvc_us` 从 978 ms 降到 20–24 ms。
待办：在 30 分钟持续传输下采样 `csvc_us`、`iter_us_max`，确认无长尾。
验收：观察异步会话单次 Service 与完整有载轮次的长尾，核对 100 ms 阈值；`async_svc_us_max` 不含计时前的异步接收和解码。每次测试不超过 60 秒；一组短测可以累计观察时间，但不能声称覆盖了连续 30 分钟有载运行。

## P2：环境与部署

### 10. HND0 SSE2 GF(16) 收益未验证
现状：`Gf16.sse2MulX` 已实现并部署；HND0 只有 SSE2、无 SSSE3。
待办：在真实上行负载下对比启用/禁用 SSE2 路径的 `csvc_us` 与吞吐。
验收：记录 HND0 实际选择的 GF(16) 分支，在相同 256 路上行负载下只改变该分支，给出收益或无收益的证据。测试床全硬件指令开关的结果只作旁证。

### 11. 测试工程编译异常慢
现象：`Tbt.Tests` 的 fsc 跑 8 分钟以上未完成（1.6 GB RSS），已多次超时。
待办：
- 拆分 `SessionTests.fs` / `TunTests.fs` 的大文件。
- 检查是否有病态类型推断（大数组 + inline + 泛型）。
- 或为 CI 单独建最小测试工程。
验收：测试工程 5 分钟内完成编译。

### 12. 路由绕过规则的健壮性
现状：本地单元 `ExecStartPre` 尝试重建规则 `8890`，使承载查主路由表。
本轮只读核查：规则 `8890` 存在，IPv4 承载目标的路由查询指向 `enp8s0`，当前 256 路已建立。服务的本轮启动晚于当前整机引导，不能代替新一次整机重启验收。
待办：
- 先确认专用代理所用 `/run/hnd0tbt4.yaml` 能在开机时恢复。它当前位于 `tmpfs`，其 systemd 单元没有生成步骤；不要把该文件当作可删除的临时文件。
- 在受控的本地代理重启和新一次整机引导后分别复查规则。`ip rule add` 重复添加会报错，`ExecStartPre` 的 `-` 前缀只允许服务忽略错误，不保证规则已生效。
验收：记录新的引导标识、`8890` 规则、`ip route get 45.11.1.62` 仍指向 `enp8s0`、双端 256 条承载 TCP，以及专用代理 `17893` 实际可用。

### 13. 临时文件与备份清理
待办：
- 确认 `/run/hnd0tbt4.yaml`、`/run/hnd0tbt4.pid`、`/run/hnd0tbt4.log` 是否还需保留。
- 保留 `/opt/proxy/config.yaml.bak-smux1` 作为回滚点，记录保留期限。
- HND0 上 `/root/tcp-ocean` 与本地 `~/tcp-ocean` 的差异需明确（Async 代码是否同步）。

### 14. 文档补齐
待办：
- `docs/cases/slow-consumer-cpu-stall.md` 增补 SSE2 路径验证结果。
- `docs/async-rewrite/` 的集成状态与真实编译状态对齐（当前标注 80%，实际编译未过）。
- 记录 `-p:PublishAot=false` 的既有约束（NU1903 + TreatWarningsAsErrors）。

## 已知约束（不要改）

- TBT4 = IPv4，路径数 256；不要降路径数来换速度。
- 承载只走 TCP；UDP 必须留在 TCP 隧道内。
- 公网面只暴露 tbtun，`:39443` 对探测保持静默；内层 `127.0.0.1`。
- 不引入 TLS/REALITY 到 `:18443`。
- 保留 TFO；保留 BBR 与 `/etc/sysctl.d/99-bbr-tfo.conf`。
- smux 永久 `max-connections: 1`、`min-streams: 1`；不关闭 smux。
- 不改变既定产品策略（`rho=0.25`、256 路、GF(16) 上行）。
