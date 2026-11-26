# Wave8 官方矩阵（2026-11-26）

范围：干净树 `6a115c1` 上复验 wave1 记录的 TUN 重开回归，并对照 0% / 5% 两场 4 路样本。本文不写主机名、地址或端口。

## 样本身份

| 项 | 值 |
|---|---|
| script commit | `6a115c1`（完整 `6a115c15e142112f0511e155f171456cf651c64e`） |
| 服务端 AOT | sha256 `561fa7f5dcac4bf9448a74fb2004d723564a2f0d0492025e42f6435fabc1fd68` |
| 客户端 JIT dll | sha256 `181c805e962dcdc731587197f91733ec51fa756e79b156f728f409028f88529e` |
| client bundle | sha256 `66ffa21a42d7948522895742c3a98f7c32919e760303708d50834da80d9ca1c7` |
| 参数 | owd 20 ms，200 Mbit/s，paths 4，seconds 10，window 1024，window_clamp 600000，seed 7，server_max_paths 64，carrier tun，UPLINK_PACE=0，new_repair=0 |
| host_gate | 两场均为 quiet |

## 第一场：20 / 200 / 0 / 4 / 10

脚本退出码 **0**。mux 与 bulk 两会话握手都成功（5.63 s / 5.47 s），两会话都正常关闭。**没有** `cannot open TUN device tbt0`。wave1 在 `5b22237` 上复现的“优雅退出后第二会话打不开 TUN 并 abort”这一次没有复现。

裸 TCP：1 流 188.1 Mbit/s，4 流 189.6 Mbit/s。裸 ping ontime_rate=1.000，p50/p99=40.5/41.7 ms。

| 段 | 有效性 | 结果 |
|---|---|---|
| echo1 | valid | 141.2 Mbit/s，0.91 s，8/8 |
| echo_small | valid | 118.5 Mbit/s，0.17 s，64/64 |
| ping | valid | ontime_rate=1.000，p50/p99=40.8/41.3 ms |
| bulk_up | valid | 173.2 Mbit/s，10.6 s |
| bulk_down1 | valid | 175.8 Mbit/s，10.6 s |
| pingbulk | valid | ontime_rate=1.000，p50/p99=49.8/116.5 ms；伴随下行 175.4 Mbit/s |
| echo2 | unknown，`product_clock_catchup` | 196.5 Mbit/s，0.65 s，8/8 |
| iperf -R -P 4 | valid | 176.0 Mbit/s |
| 其余 iperf 四段 | unknown，`product_clock_catchup` | 174.6 / 174.2 / 174.3 / 152.0 |

host_pause events=0。服务端 mux RSS 69828 KiB，CPU 28.31 s；bulk RSS 71196 KiB，CPU 29.63 s。`cell_loss` 日志：客户端 mux 0–0.0626，服务端 mux 0–0.0484，客户端 bulk 0–0.1053，服务端 bulk 0–0.0484。低于 wave1 同场客户端 0.013–0.085 的上沿附近，也远低于历史 0.28–0.36。负载下 ping p99=116.5 ms，高于 wave1 同场 53.9 ms，仍不是数百毫秒饱和排队。客户端 `path joined`：mux 3 次、bulk 6 次，不再是 wave1 那种几乎每秒一条。

## 第二场：20 / 200 / 0.05 / 4 / 10

这场**没有正常结束**。脚本先被空闲的构建节点占着锁卡住约 10 分钟（第一场 `dotnet publish` 的 MSBuild `nodeReuse` 继承了锁文件描述符），清掉这些空闲节点后才真正开跑。mux 与 bulk 握手都成功（5.85 s / 4.97 s），再次没有 TUN abort。stdout 已打出吞吐，但工具 900 s 时限在收尾前杀掉了脚本：没有 `segments.json`，没有分类后的 validity，netns 由本记录的操作者随后清掉。下面数字是 stdout 观察，**不是**已分类的有效样本。

裸 TCP：1 流 167.3，4 流 185.7 Mbit/s。裸 ping ontime=1.000，p99=394.3 ms。

| 观察 | 数字 |
|---|---|
| echo 8×1 MiB / 64×20 KiB / 再 8×1 MiB | 55.7 / 88.0 / 27.1 Mbit/s，字节检查都过 |
| ping | ontime_rate=1.000，p50/p99=40.8/41.3 ms |
| bulk_up / bulk_down | 14.8 Mbit/s（23.1 s）/ 25.6 Mbit/s（15.5 s） |
| pingbulk | ontime_rate=1.000，p50/p99=94.2/175.9 ms；伴随下行 145.8 Mbit/s |
| iperf up / down / P4 up / P4 down / 再 up | 15.0 / 19.4 / 15.3 / 32.9 / 27.4 |

服务端 mux RSS 72780 KiB，CPU 38.14 s；bulk RSS 76092 KiB，CPU 24.47 s（bulk 段被截断，CPU 不含完整收尾）。`cell_loss` 日志：客户端 mux 0–0.2106，服务端 mux 0.008–0.1347，客户端 bulk 0–0.7784，服务端 bulk 0–0.0902。客户端 bulk 的 0.7784 出现在被截断的会话里，不能和 wave1 已分类段的 0.3588 直接比。

相对 wave1 已分类的 5% 场（bulk_up 15.9、bulk_down 88.3、iperf down 21.7）：本场上行地板同量级（14.8 / 15.0），下行 stdout 25.6 明显更低，但 pingbulk 伴随下行 145.8。未分类、单次、且被杀，不能据此说下行变差或变好。

## 结论

- TUN 重开：0% 场退出码 0，第二会话握手成功并正常关闭。5% 场两会话握手也都成功。wave1 的 `cannot open TUN device tbt0` abort 在这两次里没有出现。一次完整场加一次被截断的场，不是“已修”的官方结论，只是这次没有再撞上。
- 0% 吞吐与 wave1 同场接近或更好：bulk_up 173.2（wave1 104.5）、bulk_down 175.8（wave1 174.9）。单种子。
- `product_clock_catchup` 把 0% 场的 echo2 和四段 iperf 标成 unknown，不再像 wave1 那样把 echo 标成 invalid `clock_jump`。分类规则变了，不能把 unknown 段拿去比吞吐。
- 5% 场没有有效性分类。锁被构建节点描述符拖住，是测试床操作问题，不是产品回归。
- 没有跑 64 路。
