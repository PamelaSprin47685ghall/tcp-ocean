# 方向切换候选：验证记录与合入门槛

日期：2026-11-15。状态：候选分支，尚未完成官方吞吐 A/B，不可据此合入数据路径改动。

## 基线

按请求从 `2bc29689b5fa1a77df57648d7d4d2d10c9c12a99` 开始。提交前远端 main
前进到 `546fb0a171a320bcb35af4cd4a391eaa30f9f143`，新增 CTRL 换钥与关路径处理。
此后主线又新增 rekey 集成测试和 MemoryGovernor / mem_tier，审查基线最终固定为
`571666aeb10c9a9cca459dddc8877ac8a2394211`。候选重放到该提交；每次基线变动后
分别对主线与候选做全量构建与单测。

README 中的历史吞吐数字仍属于 `2bc2968`，不能移作较新主线或候选的测量结果。
真正合入前还须确认届时 main，并用同一测量脚本比较两侧预编译产品。

## 本地结果

SDK 为 .NET 10.0.404，符合 `global.json` 的 `latestFeature` 策略；没有关闭
NuGet 审计、警告即错误或已有测试。采用 Release 构建和完整 xunit.v3 可执行测试入口。

| 代码 | Release 构建 | 全量单测 |
| --- | --- | --- |
| 原指定主线 `2bc2968` | 0 警告、0 错误 | 218 / 218 |
| 候选，尚未重放新主线 | 0 警告、0 错误 | 265 / 265 |
| 更新后主线 `546fb0a` | 0 警告、0 错误 | 226 / 226 |
| 候选，重放到 `546fb0a` 后 | 0 警告、0 错误 | 273 / 273 |
| 最终审查基线 `571666a` | 0 警告、0 错误 | 243 / 243 |
| 候选，重放到 `571666a` 后 | 0 警告、0 错误 | 290 / 290 |

新增 47 个展开用例：速率组 13、ACK 调度 12、内核状态观测 13、RTO 包络与恢复 9。
既有 no-SACK、blackout、RepairWait 断言保持不变，`NewRepair` 仍默认关闭。

离线 Python 测试合计 55 / 55：分段测量 39、原有资源所有权检查 11、原有 echo 检查 5。
`bash -n` 与 `git diff --check` 通过。来源门的回归包括占位 commit、错误 hash、缺失路径数，
以及把 `7` / `07` 误当两个种子的情况。

一次新的真空闲夹具曾把整数发送量与浮点名义速率混用，产生 2 B 的期望差。
修正只涉及该新夹具：保存空闲前真实窗口与节拍，检查空闲后完全相等；没有扩大容差，
也没有为通过测试改动产品算法或既有断言。

可复核的入口：

```sh
dotnet build Tbt.slnx -c Release -m:1 -nodeReuse:false
dotnet tests/Tbt.Tests/bin/Release/net10.0/Tbt.Tests.dll
python3 tests/e2e/test_measure_segments.py
python3 tests/e2e/test_tb_guard.py
python3 tests/e2e/test_echo.py
bash -n tests/e2e/run.sh
git diff --check
```

本地 NativeAOT 发布未完成：构建任务宿主所需的 Unix domain socket 在当前执行环境中
不可用。GitHub CI 单独执行 NativeAOT 发布和二进制启动检查，其结果在 PR 的检查中记录，
不把上述 Release 构建当成 NativeAOT 成功。

## 已证实与仍待证实

| 工作项 | 本轮已经固定的机制 | 尚缺的产品证据 |
| --- | --- | --- |
| 64 路上行散度 | 默认上行由内核控制；修复 ACK 尚不可执行时独自打断 TUN 接收的条件，增加同方向内核观测 | 有效上行中位、地板与散度的 A/B；不能声称 93–121 已收窄 |
| 真空闲后的启动 | 零发送、零交付、零在途会清除旧满管计数；中断仍有在途时保留旧行为 | 8 路快/慢残留各自不退、64 路 down1 保持约 137 档、低残留不新增一秒低速 |
| 丢包隐藏包络 | 分开定义无重传、无 RTO、无窗口收缩、应用限时恢复；固定服务端宽限、尾部可见性与包络外恢复 | 真实内核剩余计时器与反馈预算、四项结果的逐路径测量 |
| 分段有效性 | mux/bulk 隔离、事件相交、缺证据未知、来源与种子门、按残留类别比较 | 在正式测试床用新记录器产生至少两种子的有效对照 |

内核诊断复用已有 TCP_INFO 读取，不修改客户端 TCP、窗口、发送门控或 RTO。
额外日志格式化的成本仍须计入正式性能比较，不能只凭只读设计推定零开销。
速率组保持立即 `2.885` 启动、一个 BDP 满管判据及上下行独立估计；没有恢复被否决的
先 `1×` 填充策略，没有扫描乘数，没有引入 `ledger` 信用改造。

## 官方矩阵：本轮有效样本数为零

当前执行环境没有创建网络命名空间所需的权限，也没有 sudo，因此没有运行官方 netns
测试床。下表表示待验收项，绝不是通过记录。

| 场景 | 必须分开审查的结果 |
| --- | --- |
| 20/200，0%，4 路 | bulk 上/下、iperf 单流及并行、pingbulk 准时率 |
| 20/200，5%，4 路 | 同上；loss 参数必须为 `0.05` |
| rtt400，8 路 | down1 快 echo 与慢 echo 分组；其余 bulk、iperf、交互 |
| rtt400，64 路 | 优先看上行中位与散度，同时保持下行与 iperf 的门槛 |
| asym20_200 | 两方向分别测量，检查估计不互拷 |

每侧每段至少两个有效样本和两个不同种子。host pause、所属会话 stats gap 或 clock jump
相交的段保留原始结果但不进入吞吐池；unknown 也不能补足样本数。
`review_required` 只是样本数量足够评审，仍须检查失败、稳定地板和交互准时率。
脚本自己的锁覆盖整轮；开跑前固定二进制及其来源，运行 mux 时不编译、不修改测试脚本。

机制、命令和字段定义分别见 [上行观测](uplink-observation.md)、
[真空闲计数](rate-group-idle.md)、[ACK 调度](tun-ack-drain.md)、
[RTO 包络](rto-envelope.md) 和 [分段有效性](measurement-validity.md)。

## 新主线中独立的生命周期接线缺口

重放时的只读复核发现以下问题已存在于 `546fb0a`，在 `571666a` 仍然存在；
不是本候选引入，也未夹带进本轮吞吐修复。它们需要单独的接口修正和生命周期回归，
不能因本表单测通过而视为已经覆盖。

- `Session.ProduceFill` 在 PATH_CLOSE 或 rekey 保留区可直接返回，接口仍是 `unit`。
  `SessionCarrier` / `IPathOwner` 无法把“没有填充”传给 `TunPath.repair`，后者仍构包、发包并更新修复计数。
  TUN 共用的 Scratch 没有在该调用处清空，`Packet.build` 也只写头部，不替换 payload。
- `Server.tryJoinAny` 未接入 `Session.PathState` / `NotePathJoined`。同路径 id 的新 JOIN
  可以完成物理附着，但关闭状态没有复位，后续 `TryProduce` 仍可能拒绝；Closing 期间的
  JOIN admission 也没有按新事务状态拦住。

这两项限制长期 rekey / PATH_CLOSE / JOIN 的结论。官方吞吐样本即使足够，
也不能替代这些生命周期边界的验证。
