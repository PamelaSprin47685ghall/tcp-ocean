# 宽限期内不可发送的 ACK 不应独自中断 TUN 接收

基线：`2bc29689b5fa1a77df57648d7d4d2d10c9c12a99`。

## 可由代码和单测确认的机制

默认 adaptive 接收在每读入一个包之后检查是否需要把执行权交给 Service。
原来的 `TunStack.AckDue` 使用 `TunPath.AckPending`：只要某一路 `ackDue=true`，
就提前结束本次接收。

但“有待处理的 ACK”不总等于“Service 现在可以发 ACK”。租约内出现上行缺口时，
`ackDue` 保持为真；15 ms 宽限期内，`FlushAck` 必须压住会暴露缺口的重复 ACK
和 SACK。只有低于累计 ACK 的合法 D-SACK 可以先发送。

因此，某一路的缺口尚不能处理时，它仍会使整个 TUN 的接收在一个包后返回。
随后再次遍历会话和各条路径。只收到纯 ACK、或继续收到宽限期内的数据时，
这次提前返回可能没有任何可执行的 ACK 工作。

这是接收调度的确定性问题；它对 64 路上行吞吐散度的贡献尚无有效 A/B 结果。
普通在序数据仍会产生可以立即发送的 ACK，本补丁保留这种情况下的及时返回。
不能把它表述为“所有上行接收现在都能合批”。

## 改动

保留 `AckPending` 原有的“有待处理 ACK”含义。新增只读查询
`AckNeedsService(now)`，供 `TunStack.AckDue` 判断是否值得提前返回。

| 当前情况 | 是否要求提前 Service | Service / FlushAck 的行为 |
| --- | --- | --- |
| 普通 ACK 已就绪 | 是 | 按原逻辑发送 ACK |
| 租约缺口仍在宽限期内 | 否 | 继续接收，仍受原时间与包数预算限制 |
| 宽限期内，D-SACK 在累计 ACK 之下 | 是 | 只发合法 D-SACK，缺口继续等待 |
| 宽限期内，D-SACK 位于累计 ACK 之上 | 否 | 继续压住会暴露缺口的确认 |
| 缺口恰好达到 15 ms | 是 | Service 按原规则吞洞，随后发送 ACK |
| 解码前沿已有足够证据 | 是 | Service 按原规则提前吞洞 |
| 租约尚未建立或已因 FIN 失效 | 是，只要有 ACK 等待 | 按原有可靠 TCP / SACK 规则处理 |
| 路径已关闭 | 否 | 不发送 ACK |

`canFlushAck()` 同时供查询和 `FlushAck` 使用；`firstGapAction(now)` 同时供
查询和 Service 的吞洞循环使用，避免两份条件日后分叉。检查租约和连接状态之后
才访问 owner 的解码前沿。`TunStack` 每次查询只读取一次时钟。

查询不发包、不吞洞、不清 ACK 标记，也不改任何时间戳。时钟跳变的处理仍在
Service 中：跳变后的首次查询可以要求提前 Service，但 Service 先顺延宽限期，
不会因此提早向客户端确认缺失字节。即使先前的下行数据已捎带 ACK、清掉
`ackDue`，到期的上行缺口仍会要求 Service。

本补丁没有修改服务器 callback、15 ms 宽限期、2 ms 接收时限、128 包预算、
吞洞条件、修复份数或默认开关。

## 回归覆盖

[TunAckSchedulingTests.fs](../../tests/Tbt.Tests/TunAckSchedulingTests.fs) 新增
12 个展开用例：普通 ACK；宽限前与恰好到期；累计 ACK 之下和之上的 D-SACK；
未协商 SACK；bootstrap 未完整和完整但未 Attach；FIN 使租约失效；
解码前沿提前满足；捎带 ACK 后的到期缺口；400 ms 时钟跳变；关闭路径。

测试直接检查生成的 ACK 序号、SACK 块、发包数量、虚拟 ACK 和 D-SACK 计数。
未来时刻的就绪查询不能改变当前状态；反复查询不能产生发包。
既有 no-SACK、blackout、RepairWait 等测试的断言不变。

## 吞吐验收与下一步证据

这是等待官方 A/B 的候选修复，不是已验证的上行吞吐改善。需要在当时 main 与
候选分支之间，用至少两个有效种子比较 64 路上行，以及 8 路、20/200 0%/5%、
非对称 profile 的 bulk / iperf 单流与并行、pingbulk 准时率。

每段仍按主机停顿和所属会话的 gap / clock jump 判断有效性。方向切换前记录
残留状态；8 路 down1 保持快/慢残留分类。不能把事件循环自身的 CPU 或 GC 停顿
作为无效样本删除。

诊断首先看同一有效时间窗内的 `up_holes`、`recv_yielded`、
`recv_pkts / recv_calls`、`svc_us`、`csvc_us` 和 `iter_n`。
若宽限期内的无效提前返回是实质瓶颈，应看到相应接收批次扩大、重复 Service
成本下降；同时客户端的内核累计 ACK 增量和实际应用吞吐应改善。
仅看到循环次数减少，或某次吞吐高点，不足以作合入判断。
