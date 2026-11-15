# 按测量段判定有效性

## 为什么要改

`2bc2968` 的脚本保存了二进制戳和整场主机事件，但没有保存每个测量命令的起止时间。
因此，一次较晚的 balloon 会使已结束的有效段难以保留；反过来，mux 日志的停顿也可能被误用于否定独立的 iperf 会话。
这次改动补齐时间窗和数据来源。它不改网络模型、测试流量、产品断言或吞吐验收标准。

## 记录与判定

`run.sh` 在每个 echo、ping、bulk 和 iperf 命令前后保存：

- 会话名 `mux` / `bulk`、方向、命令参数；
- 带时区的本机时间、Unix 纳秒、单调钟纳秒；
- 命令退出码、原始输出；iperf 的 JSON 和 stderr 分开保存。

记录在 `segments/*.segment.json`，原始输出在同名 `.out`。这是命令的完整运行窗口，
包括连接、进程启动和尾部确认，比只取 payload 发送期略宽。采用这个保守窗口，
不会把受污染的连接建立或收尾排除在测量外。

清理结束时，`measure_segments.py` 写出 `segments.json`。每段有独立的 `validity` 和 `product_pass`：

| 字段 | 含义 |
| --- | --- |
| `validity=valid` | 主机证据可读，两个端点的统计覆盖该段，且没有相交的 host event、stats gap 或 clock jump |
| `validity=invalid` | 有明确的相交事件，报告中保留事件时间、日志名与原因 |
| `validity=unknown` | 段未完成、日志缺失、统计覆盖不足，或主机事件时间无法判定 |
| `product_pass` / `returncode` | 原命令的结果；测量无效不会把非零退出码改成成功 |

`mux` 只读 `client-mux.log` / `server-mux.log`，`bulk` 只读对应的 bulk 日志。
默认 stats gap 阈值是 2.5 秒，来自 `STATS_GAP_S`，并写入结果。`clock_jumps` 使用相邻计数的增量；
已经发生过一次跳时不会否定此后所有段。计数重置的不确定性也只作用于相交的采样区间。
GC 次数、事件循环迭代数、单次服务耗时都不单独构成作废理由。统计间隙本身会被记录，
但脚本不会据此宣称根因一定来自主机。

dmesg 以 `LC_ALL=C` 采集，事件时间按记录中的测试床时区解释；不使用查看报告者的时区。
日志只有 `HH:mm:ss.fff`，首行选择最接近测量起始时刻的日期，随后处理跨午夜。
一秒精度的 dmesg 事件按 `[秒开始, 下一秒)` 判断相交；没有凭空加上一分钟污染窗口。
事件本身不能说明暂停持续多久，较长中断由同会话 stats gap 补充覆盖。
无法解析的 host event 会明确留下 unknown，不能反过来当成“没有事件”。

旧的 `HOST_PAUSE_DISCARD` / `run-invalid` 仍可标记整场存在主机事件，供已有脚本识别。
它不是各段吞吐的判决。性能比较必须读取 `segments.json`，不能用整场退出码代替。

## 开跑前的安静门

编译完成、创建 namespace 之前，脚本重新采集 dmesg 并检查：

- 最近一次 fork / balloon 已过去至少 `HOST_QUIET_S=60` 秒；
- `MemAvailable` 至少为 `HOST_MIN_MEM_MIB=2560`。

结果写入 `host-gate.json`。dmesg 不可读、事件时间不明或内存信息缺失时，状态是 unknown，
不是 quiet。`HOST_QUIET_BYPASS=1` 可以显式越过开跑门，但该选择会留在结果中，
也不会关闭事后的分段有效性检查。

原有全轮 `flock` 和资源所有权检查保持不变。不要在 `run.sh` 外再包一层锁。
命令行 loss 及 `WANEM_EXTRA` 合成后的 loss 都要求有限数且位于 `[0, 1]`；
`5`、`NaN`、`Inf` 会在网络测试前被拒绝，5% 应写 `0.05`。

## 方向切换与残留

每段保留切换前最近 2.5 秒内的上、下行观测，以及开始后 3 秒内的轨迹。
没有新鲜观测时记 unavailable，不从相反方向借用。

下行使用服务端 `group` 统计。上行 `UPLINK_PACE=1` 使用客户端 `pace_*` 统计；
默认 `UPLINK_PACE=0` 的发送控制属于内核，残留类标为 `kernel-unpaced`。
`kernel_*` 和原始客户端 stats 原样保存。`kernel_delivery_mbit` 是各路径不同时刻样本的和，
不能当作共享 RateGroup 带宽。`kernel_generation` 变动会单独标记，分析器不跨代际推算速率。

目前只有历史数据支持的 `rtt400 / 8 路 / bulk_down1` 使用快慢分类：

| 残留带宽 | 分类 |
| --- | --- |
| 14–18 Mbit/s | `slow_echo_14_18` |
| 35–41 Mbit/s | `fast_echo_35_41` |
| 其余或缺失 | `outside_reference_bands` / `unknown`，不混入已知类别 |

这些区间仅用于归档已有快慢样本，绝不进入产品速率控制。
其他配置仍保留原始残留数值和轨迹，不能把这里的区间套到其它线路。

`pingbulk` 的同一命令窗口同时包含计划探测准时率和伴随下行吞吐，后者即矩阵中的 down2。
比较器把它们输出为两个指标，主判决仍是 `ontime_rate`；p99 作为次要指标保留。

## 来源与比较

报告包含 script commit、服务端与客户端 build commit / SHA256、协商路径数、种子、
全部 stamp 行、原始 `wanem.conf` 和允许记录的环境选项。JIT 客户端另有
`client-bundle.sha256`，列出全部 DLL 与 JSON 配置；只哈希 `tbtun.dll` 不能识别 carrier 程序集变化。
wanem 版本使用退出时保存的实际 runtime 版本。`WANEM_EXTRA` 重复项按最后一个值比较。

使用同一份测量脚本和已编译的 `TBT_BIN` 对两侧运行官方矩阵。不要边跑 mux 边构建。
每侧每段至少两个有效样本、两个种子后，再查看同配置、同残留类别的结果：

```sh
python3 tests/e2e/compare_segments.py \
  --baseline /path/main-seed7 /path/main-seed9 \
  --candidate /path/change-seed7 /path/change-seed9 > comparison.json
```

比较器要求 build commit、hash、协商路径数和种子可核对。缺失来源信息的样本仍保存，
但不计入可比较样本数。一侧若混入不同产品二进制，会分成独立 cohort，不计算混合中位数。
SHA256 必须为 64 位十六进制；commit 必须为 7–40 位十六进制，script commit 可带现有的
`-dirty` 后缀。`unknown`、空串或其它占位值都不能满足来源门槛。
请求与协商路径数必须存在、为正整数且数值相同。

种子只接受明确的非负十进制整数，且不超过 `2^53 - 1`：当前 wanem 先用 `atof` 读取，
再转为 `uint64_t`，更大的数可能在浮点转换时合并成相同值。比较器按整数规范化种子，
`7` 与 `07` 只算一个种子；正常的 7/9 样本不受影响。有效种子字段已存在但内容未知时，
不能退回请求种子来掩盖这个缺口。这些检查只筛选比较输入，不改变网络模型。

输出包含中位、最小/最大值、跨度、IQR、全部有效样本、排除原因和观测到的非零退出码。
只有一个种子、只有一个有效样本、残留类未知或两侧配置不同，都不会生成充分证据的提示。

`review_required` 表示样本数量够评审，不表示准许合入。仍要检查中位数、稳定地板、
交互准时率和失败日志；单次低于 main 最小值不能独自定案。正式 A/B 尚需具备权限的测试床。
这些离线夹具验证的是记录与筛选逻辑，不能证明吞吐已提高。

## 离线回归

```sh
python3 tests/e2e/test_measure_segments.py
python3 tests/e2e/test_tb_guard.py
python3 tests/e2e/test_echo.py
```

覆盖会话隔离、窗口外 host event、clock counter 增量/重置、跨午夜、缺证据、CPU/GC 不免责、
原始退出码、快慢分类、不对称方向观测、最少种子、二进制来源、混合版本和误填 loss。
这些命令不启动 namespace 测试床。
