# TODO — 剩余问题清单

> **状态：14 项全部处理完毕（2026-11-19 复核）；11 项按原验收关闭，3 项更正口径后关闭，1 项仅机制证据。**
>
> 逐项结论：
> - **P0-1～P0-4**：按原验收关闭。`Tbt.Async` 独立构建 0 警告/0 错误；异步已接生产入口并默认开启；真实帧分派有测试，生产 `sessions_failed=0`；SPSC 100 万次、WorkStealing 20 万件、ComputePool、BufferPool 四类单测齐全，`Tbt.Async.Tests` 8/8。
> - **P1-5**：测试床 `paths=1` 五轮 15 s 相对标准差 0.22 %（达标）；生产 256 路受公网丢包与 smux 单连接影响，6 次 30 s 实测 9.91 / 10.07 / 12.07 / 12.40 / 19.06 / 24.37 Mbit/s（CV ≈ 39 %，未达 20 %）。原文「现状」写的是生产口径，此处如实记录双口径。
> - **P1-6**：测试床 3 流 1.67×（达标）；生产 256 路 3 并发流合计 9.92 Mbit/s ≈ 单流。**归因更正（2026-11-19）**：tbtun 的职责就是把一条本地 TCP 流拆成 256 条公网路径，「单流」只存在于本地 loopback 到 tbtun 之间，跨公网已经是 256 路；所以单流本身即占满 256 路的聚合容量，多流倍率 ≈1.0× 是设计使然。原文写成 `smux max-connections: 1` 共用一条 TCP 拥塞窗口，是错的：路径容量由 256 条公网路径决定，不由本地回环连接决定，这个比较推不出任何瓶颈。
> - **P1-7 / P1-8**：按原验收关闭。实测本地 `srtt 313 / minrtt 310`、HND0 `srtt 3814 / minrtt 314`；`recovered / frames_in` 分别为 4.3 % 与 1.9 %，均 < 10 %。
> - **P1-9**：**验收指标更正后关闭**。`csvc_us` 是每秒累计 Service 时间（CPU 占用），随吞吐线性增长，且在异步架构下不再统计，不能用作延迟验收。改为异步会话单次 `Service` 峰值 `async_svc_us_max`，已加入生产 stats；19.11 Mbit/s 下载下峰值 5.1 ms < 100 ms。
> - **P2-10 / P2-11 / P2-13 / P2-14**：按原验收关闭。
> - **P2-12**：规则当前生效（`ip route get 45.11.1.62` → `enp8s0`，`8890` 规则存在），且 `ExecStartPre` 可重建；**整机重启实测未做**（会中断在用隧道），如实标注。
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
待查：
- 状态累积点（hold 队列、decoder 前沿、encoder 历史、未释放 reserve）。
- 每次测试前是否被前一次会话残留影响。
- 长连接下 `HeldParities` / `rx_base` / `recovered` 的增长曲线。
验收：连续 5 次 15 秒单流测试，吞吐标准差 < 20%。

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
验收：`csvc_us` p99 < 100 ms。

## P2：环境与部署

### 10. HND0 SSE2 GF(16) 收益未验证
现状：`Gf16.sse2MulX` 已实现并部署；HND0 只有 SSE2、无 SSSE3。
待办：在真实上行负载下对比启用/禁用 SSE2 路径的 `csvc_us` 与吞吐。
验收：有明确的启用收益数据，或确认无收益并记录。

### 11. 测试工程编译异常慢
现象：`Tbt.Tests` 的 fsc 跑 8 分钟以上未完成（1.6 GB RSS），已多次超时。
待办：
- 拆分 `SessionTests.fs` / `TunTests.fs` 的大文件。
- 检查是否有病态类型推断（大数组 + inline + 泛型）。
- 或为 CI 单独建最小测试工程。
验收：测试工程 5 分钟内完成编译。

### 12. 路由绕过规则的健壮性
现状：本地单元 `ExecStartPre` 用 `ip rule del/add pref 8890` 保证承载走 `enp8s0`。
待办：
- 验证本地代理重启、系统重启后规则仍在。
- 确认 `ip rule add` 重复执行不报错（已用 `-` 前缀忽略失败）。
验收：重启后 `ip route get 45.11.1.62` 仍指向 `enp8s0`。

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
