# Clean-break WAN 改造交接（2026-11-25）

本文件交接一轮针对真实广域网（高 RTT、持续丢包）的 clean-break 改造：根因、改动清单、
已验证事实、开放项与后继动作。产品面向的部署链路在本地代理之后，链路特征以实测为准，
本文不写具体上游实现名。

## 一、根因

按影响排序，全部由源码 + 实测确认：

1. **及时擦除失真与速率控制死锁/过冲**（头号吞吐瓶颈）。
   - `src/Tbt.Session/RxHalf.fs`：早期只统计永久擦除，24% 物理丢包报成 `cell_loss≈0.0001`，
     `rho` 被钉在 0.027，几乎每步都等一次 TCP 重传卡住前沿。
   - 改为截止期及时擦除后，10 ms 稀疏小桶（1–2 个 due cell）把 `ErasureP95Q16` 顶成 1.0；
     `src/Tbt.Session/TxHalf.fs` 的 `e_eff` 曾给 `peerP95` 0.65 权重，令 `rho` 冲到 0.406
     （+40% 校验税），在饱和链路上反向加深排队，形成自拥塞环。
   - `deadlineNs` 原上界 200 ms、下界 90 ms，低于实测 112 ms 物理 RTT，正常 TCP 重传尾部
     必被判迟到，进一步放大擦除读数。
2. **解码器行与列索引泄漏**：`src/Tbt.Codec/Decoder.fs` 在 `Count=0` 时不回收行，
   `Release` 未清理仍引用旧步的列索引，长跑后 `rowsAllocated` 撞上 `maxRows`，FEC 静默失效
   （实测远端 RSS 101→132 MiB，+31 MiB 与 16384 行 × 1.95 KB 吻合）。
3. **异步路径唤醒与接收排空**：`src/Tbt.Carrier/AsyncPaths.fs` 的 `CommitFrame` 在写
   `txHead` 之前判断 `idle`，与 IO 线程竞态会丢唤醒（最高白卡 1000 ms）；`RxChannel` 排空
   无上限，实测造成 9.42 s / 12.48 s 的整段统计空白并饿死 `WIN_ACK` / `FLOW_STATE`。
4. **用户态发送环过小**：默认 `txSlots=16`（约 23 KiB）远小于高丢包单路 BDP，实测
   `beginfail` 达 2.58×10⁶，且 `UnsentBytes` 只看内核 `SIOCOUTQNSD`，不含用户态挂起。
5. **调度与控制面**：`SessionCarrier` 的写批外层只守 `session.WantsToSend`，会把内部 Phase 0
   的 `WIN_ACK`/`FLOW_STATE` 一起跳过（实测 7–8 s 发不出 1 帧）；把 `TCP_CA_Recovery`
   （24% 丢包下的常态）当病态跳过（`hskip` 达 2.8×10⁷）；把一次 `TCP_CA_Loss` 当成工作集
   饱和，在“收缩需要 `Tx.Queued=0`”的前提下使活跃路径集只增不减；`Describe()` 漏统计
   `AsyncPathLink`，生产日志长期把内核载体误报为 `send_control=tun` 且指标归零。
6. **空闲抖动与运行时**：`PathRefresher` 空闲时每秒重建 8 条连接（≈0.91 MiB/s 分配、
   大量半关闭 socket）；`Tbt.slnx` 漏列 `Tbt.Async.fsproj`，`-c Release` 下该工程按未优化
   Debug IL 编译；`Tbt.Cli.fsproj` 关闭并发 GC，2 vCPU 主机上出现 50–120 ms 满载停顿
   （实测 `iter_us_max` 最高 114 ms）。
7. **流控/速率/用户态 TCP**：`Flows.ByteQueue` 单块倍增复制把 8 MiB 数组推上 LOH；信用仅在
   消耗 `window/8` 后补发，前沿卡住时整秒停发；`RateGroup` 有 `Cwnd` 溢出风险与每帧
   O(paths) 扫描；`TunPath` 与 `RateGroup` 双拥塞控制器冲突、RACK 重排窗口硬编码 0、
   修复副本 2–4 倍盲放大。

## 二、改动清单

运行时与并发
- `Tbt.slnx`、`src/Tbt.Cli/Tbt.Cli.fsproj`：纳入 `Tbt.Async`，开启并发 GC。
- `src/Tbt.Carrier/AsyncPaths.fs`：Dekker 唤醒屏障；`txSlots` 上限 256（默认 64）；
  `UnsentBytes` 计入用户态挂起；`txFullWait` 半环释放唤醒；`PumpTx` 优先排空；
  `gather` 缓冲移出实例。
- `src/Tbt.Async/AsyncSession.fs`：单一生命期阶段状态机；非阻塞 `RequestClose`；
  有界接收排空；退出后才释放资源。
- `src/Tbt.Carrier/KernelTcpInfo.fs`：280 B `[<ThreadStatic>]` 零分配缓冲，补齐
  `min_rtt`、`bytes_sent/bytes_retrans`、`segs_out`、`lost/sacked/retrans`、
  `dsack_dups/reord_seen` 等字段，提供 `PathHealth`。

FEC 与交付
- `src/Tbt.Codec/Decoder.fs`：回收空行、清理过期列索引、按 `warmRows` 缩容。
- `src/Tbt.Session/RxHalf.fs`、`ParityHold.fs`：截止期及时擦除账本；10 ms 窗口真 P95；
  稀疏突发不被吞；非队头阻塞的校验提取；按活跃路径数扫描。
- `src/Tbt.Session/TxHalf.fs`、`Session.fs`：`DecodedBitmap0..3`、`ErasureP95Q16` 全链路；
  消费端截尾融合 `e_eff = max mean (0.70·mean + 0.30·min burst (mean+0.25))`；
  `RhoMax=0.38`、`RhoGain=1.05`；有冷却的位图证据微修复；`repairCapacity=1024`、
  `TxQueueCells=512`；`deadlineNs` 上界 260 ms（下界 90 ms 不变）。
- `src/Tbt.Session/Handshake.fs`：`JoinAdmission.Decide` 对未认证帧返回 `Close`（API 边界契约）。

流控、调度与承载
- `src/Tbt.Session/Flows.fs`：64 KiB `ArrayPool` 分段环 `ByteQueue`；开流即扩窗；
  信用刷新阈值降到 `min(window/16, 262144)`。
- `src/Tbt.Carrier/FlowBridge.fs`：共享暂存缓冲；零信用时不再逐帧 1 字节 peek。
- `src/Tbt.Carrier/SessionCarrier.fs`：Phase 0 控制帧无条件放行；健康分层只把 `TCP_CA_Loss`
  视为病态；Tier-2 跳过不再计入饱和；活跃路径工作集下界 16、饱和时弹性扩张；
  自适应第二遍写门控按 `Cwnd` 夹取；`Describe()` 统计 `AsyncPathLink`。
- `src/Tbt.Carrier/PathRefresher.fs`、`PathPool.fs`、`Client.fs`、`Server.fs`：
  零空闲 churn；按健康度定向刷新；客户端对齐协商路径数；异步并行握手；
  非阻塞会话回收与退役 JOIN 决策提取（`Server.joinMustReject` / `retireExpired`）。
- `src/Tbt.Carrier/RateGroup.fs`、`TunPath.fs`、`TunStack.fs`、`SocketTuning.fs`：
  `Cwnd` 钳位；O(1) 缓存；`NoteLimited()`；无窗口缩放时 65535 通告；RFC 8985 自适应 RACK
  重排窗口；去掉 4× 修复放大并公平配比；初始化去重采样。
- `src/Tbt.Cli/Program.fs`、`Common.fs`、`AsyncMode.fs`、`Native.fs`、`Path.fs`、
  `PathLink.fs`、`PathPool.fs`、`Trace.fs`、`ParityHold.fs`：用法错误边界、路径健康接口、
  跟踪器并发与截断可见性等配套。

测试与工具（不改变既有敏感断言）
- 新增：`tests/Tbt.Tests/AsyncSessionTerminationTests.fs`、`JoinRefusalRetryTests.fs`、
  `ServerJoinRealTests.fs`、`ServerJoinTests.fs`、`TraceLifecycleTests.fs`、`TraceLogTests.fs`、
  `TunJoinRejectTests.fs`、`tests/e2e/test_bulk_phases.py`。
- 修改：`BridgeTests.fs`、`ControlTests.fs`、`JoinTests.fs`、`CodecTests.fs`、
  `KernelTcpInfoTests.fs`、`PollerTests.fs`、`RateGroupTests.fs`、`SessionTestsC.fs`、
  `TunTestsC.fs`、`TunTestsD.fs`、`PathPoolTests.fs`、`FlowReportLifecycleTests.fs`、
  `CliTests.fs`、`tests/e2e/echo.py`、`tests/e2e/test_echo.py`。
- 设计记录：`.agent/skills/tbtun-clean-break-design/SKILL.md`。

## 三、已验证事实

构建与单测（最新工作区）
- `dotnet build Tbt.slnx -c Release -p:NuGetAudit=false`：0 Warning / 0 Error；
  `src/Tbt.Async/bin/Release/net10.0/Tbt.Async.dll` 正常产出。
- `dotnet test Tbt.slnx -c Release --no-build`：**566/566** 通过
  （`Tbt.Async.Tests` 8、`Tbt.Tests` 195、`Tbt.Session.Tests` 363）。
- 离线脚本：`test_bulk_phases.py` 18/18、`test_echo.py` 19/19、
  `test_tb_guard.py` 11/11、`test_wanem.sh` 通过。

真实链路短实验（本机 WiFi IPv6 ↔ 远端，同一时间窗，每次传输 ≤ 10 s）
- 物理链路：`ping6` 112.8 ms RTT、25% 丢包（同日另一窗口为 112.8 ms / 16%）。
- 裸 TCP 下行：1 流 33.6 Mbps，16 流 43.5 Mbps（同期 50.8 Mbps 窗口亦出现）。
- 纯隧道 32 路径（弹性下界 16）：握手就绪 0.80 s；`check` 4/4 字节一致；
  `pingbulk` 窗口内均值 39.4 Mbps（占裸 16 流的 90.6%）；全程含排空均值 19.0 Mbps；
  服务端 `rho=0.104`、`iter_us_max` 82–150 µs、`beginfail=0`、`dec_stale=0`；
  负载探针 12/12 准时，p50 773.6 ms、max 1396.1 ms。
- 本地代理（单连接复用前端）端到端：4×`generate_204` 全 204，TTFB 0.242–0.405 s；
  单流 8 MiB 30.8 Mbps 且哈希一致；4 并发 8 s 四流全 200（5.69/6.11/5.69/6.19 MB），
  合计 23.7 Mbps，零流饿死；4 MB 上行 200，1.63 s。
- 对照改造前：服务端 `rho` 0.406→0.104，`iter_us_max` 9891→27–150 µs，
  窗内吞吐 29.1→39.4 Mbps，空闲 `hskip` 2.8×10⁷→0，空闲分配 ~0.91 MiB/s→~0。

## 四、开放项

1. **官方矩阵未跑完**：定稿控制律（`RhoMax=0.38`、`minActivePaths=16`、截尾 `e_eff`）
   尚未与干净基线 `bac3c61` 在同一 Testing 批次做 NativeAOT 服务端 + JIT 客户端的
   官方 `run.sh` 全矩阵对照（`20/200/0`、`20/200/0.05`、`CARRIER=kernel`、
   `PROFILE=rtt400` 8/64 路径）。这是合入主线的必要前置。
2. **饱和下行仍有排队**：25% 丢包下纯隧道 32 路径满载时，服务端 `srtt` 268–778 ms vs
   `minrtt` 269 ms，负载探针 p50 ~774 ms；`rho` 已降到 0.10–0.13，故剩余延迟来自发送侧
   队列深度而非校验税。
3. **`cell_loss` 仍偏高**：实测 0.28–0.36，高于物理丢包；`deadlineNs` 与 TCP 重传尾延迟的
   关系仍需单独标定，否则 `rho` 的输入长期偏高。
4. **偶发 `JoinTests` 失败**：同树曾出现一次 `ArgumentOutOfRangeException`
   （`Handshake.fs` 的 nonce 切片）、以及一次误判 `Close`，均未复现；测试宿主日志同名覆盖，
   需按轮保留 `TestResults` 才能取证。
5. **TUN 载体拒绝 ACK 单发**：`SendReliable` 后紧接 `Abort`，该段不重传
   （`state=Established` 守卫）。记录为 `[OPEN]`；修法是 flush-then-close，
   必须保住 `TunTestsC` 无 SACK RTO 与 1.8 s 黑洞断言。
6. **测试床产物命名**：部分历史目录中 `aot/` 与 `jit/` 为同一 apphost，不代表真正的
   NativeAOT 对照；后续必须公布构建日志与逐文件清单。

## 五、后继动作

1. 以当前工作区为源固定一份快照（源文件内容哈希清单），重跑官方全矩阵，与 `bac3c61`
   同批对照；服务端 AOT、客户端 JIT 分开标注。
2. 针对第 2、3 项做单变量实验：同窗口裸 TCP 对照 + 不同路径数（16/32）对照，
   再改 `deadlineNs` 与写门控；一次只动一个变量。
3. 为 `JoinTests` 增加按轮唯一的 `--results-directory`，捕获下一次失败。
4. TUN flush-then-close 单独立项，先补可控丢包的双端测试，再动关闭状态机。
5. 生产部署前保留回滚点；部署后以 6 秒级短样本复测，确认 `rho`、`srtt`、探针分位数不退化。
