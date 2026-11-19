# 默认上行的观测与散度判别

基线：`2bc29689b5fa1a77df57648d7d4d2d10c9c12a99`。
64 路上行有效样本的 93–121 Mbit/s 散度仍待官方 A/B 验收。
本文和新增日志提供定位所需的证据，不把确定性单测当成吞吐结果。

## 先确认真正控制发送的对象

默认 `UPLINK_PACE=0` 时，`SessionCarrier` 不给上行 RateGroup 加成员，
不调用它的 `Service`、`TryTake` 或 `OnSent`。此时由各条未修改的内核 TCP 连接
控制发送，`PathLink` 仍执行原有的未发送字节门控。服务器的 RateGroup 只控制下行。

因此不能把默认上行散度直接归因于组的 startup/fullCount，也不能拿服务器下行
的带宽残留给客户端上行分类。`UPLINK_PACE=1` 是另一种产品配置，必须另列比较。
本次不改变此默认值，不给未启用的控制器制造看似有效的带宽样本。

会话日志增加 `tx_direction=up|down` 和 `send_control=kernel|group|tun`。
客户端默认日志应显示 `up` 与 `kernel`；启用组 pacing 才显示 `group` 和 `pace_*`。
`pace_phase` 现在明确区分 startup、drain、probe，不再把 drain 写成 probe。

## 每秒只读一次内核状态

已有 `cons_ack_b` 为每条路径读取一次 Linux `TCP_INFO`。本次复用这一次系统调用
取得其余字段；不增加发送循环中的系统调用，不设置 socket 选项，不更改 TCP 算法、
窗口、重传计时器、发送次序或修复预算。只有调用 `Describe()` 时生成这些诊断。
日志额外格式化的代价仍须包含在正式性能比较中。

| 字段 | 含义与使用限制 |
| --- | --- |
| `kernel_paths` | 本次观察的内核 socket 数；TUN 路径不混入 |
| `kernel_ack_mbit` | 同一连接集合相邻日志间累计 TCP ACK 字节的增量速率；首次、换路径或计数回退为 `-` |
| `kernel_cwnd_b` | 各路径 `snd_cwnd * snd_mss` 之和 |
| `kernel_unacked_b` | 各路径 `unacked * snd_mss` 之和；是分段单位换算，不是精确的线上在途字节 |
| `kernel_notsent_b` | 内核仍未发出的字节总量 |
| `kernel_delivery_mbit` | 各路径最近 delivery-rate 样本的和；样本不同时，不能冒充整个瓶颈的带宽估计 |
| `kernel_pacing_mbit` | 各路径内核 pacing rate 的和；不是产品设定的线路速率 |
| `kernel_retrans` | 各存活连接累计 `tcpi_total_retrans` 的和；含建立连接阶段，按同一连接身份做段内增量 |
| `kernel_rto` | 支持此字段的内核提供的累计 `tcpi_total_rto` 之和；旧内核为 `-`，不得写成零 |
| `kernel_busy_us` | 各路径累计忙于发送的微秒数 |
| `kernel_rwnd_us` | 各路径累计受对端接收窗口限制的微秒数 |
| `kernel_sndbuf_us` | 各路径累计受本地发送缓冲限制的微秒数 |
| `kernel_send_window_b` | 对端当前通告的、已缩放窗口字节数之和 |
| `kernel_app_limited` | 最近 delivery-rate 样本带有 app-limited 标记的路径数；不代表此刻应用是否有积压 |
| `kernel_generation` | 路径集合增、删或淘汰时递增；变化两侧不直接相减累计计数 |

这些是本地 TCP 发送方向的状态。内核 ACK 可能包含已被服务端吞洞的字节；
`kernel_ack_mbit` 和 `kernel_delivery_mbit` 都不能代替真实到达、解码交付或
应用 goodput。日志不会把它们送进另一个方向的控制器。

`kernel_path` 保留每条连接，格式为逗号分隔的：

```text
path_id@incarnation:cwnd_b/ssthresh_b/unacked_b/send_window_b/notsent_b/rtt_us/rto_us/retrans/rto/ca_state/app_limited/delivery_Bps/pacing_Bps
```

`rto_us` 是当前 RTO 时长，不是距离下一次超时还剩多久。`rto` 才是累计事件数。
连接代际由 `incarnation` 与会话及 `kernel_generation` 共同界定；某些服务器内核
模式下 incarnation 为零，仍必须检查集合代际。路径消失后不能把其旧累计值继续
加入新路径的计数。短版本 `TCP_INFO` 的各字段逐项检查长度；任何成员缺值，相应
总量为 `-`，不报告会漏掉路径的“部分总量”。

字段布局依据 Linux `include/uapi/linux/tcp.h`。开发时也用本机头文件的
`offsetof(struct tcp_info, ...)` 编译检查了偏移，`tcpi_total_rto` 位于 240。
目标内核是否提供该字段，仍由 `getsockopt` 返回的长度决定。

## 下一轮怎样判别 64 路散度

每个有效 `bulk_up` / iperf 上行段，保存切换前最后一份同向状态与开始后 3 秒轨迹。
默认上行残留类写 `kernel-unpaced`，同时保存每条路径状态；不能硬套下行的
14–18 / 36–40 Mbit/s 分组。

| 低档样本中的现象 | 要继续核验的机制 |
| --- | --- |
| 开始前 cwnd/ssthresh 较低，随后 retrans 或 rto 增加 | 尾洞不可见、反馈超预算或内核恢复；查同路径序号和 ACK 轨迹 |
| `rwnd_us` 段内增量较高，发送窗口或未发送队列形成持续门控 | 对端窗口、吞洞时机或发送端等待窗口更新 |
| `sndbuf_us` 增量较高 | 本地 socket 缓冲与写门控；同时检查每路径写入量，不能只看吞吐 |
| 内核仍可发送、queued/credit 却长期不给新源 | 会话信用或修复调度；单独核验，不能顺手合入 ledger |
| 路径少数繁忙、多数低窗口或不工作 | JOIN/连接代际和路径级限制；确认协商数与存活路径变化 |
| 样本窗口与 host_pause、所属会话 stats gap 或 clock jump 重叠 | 该段无效；保留原始结果，不据此判产品吞吐 |

以上是判别条件，不是已确认的根因。将同一脚本和相同生效开关用于当时 main 与
候选二进制，每侧至少两个有效种子，再看上行中位和散度，并保留 20/200 0% / 5%、
8 路、64 路下行及非对称矩阵的门槛。新日志不能替代这些有效样本。

另见 [`rate-group-idle.md`](rate-group-idle.md)、
[`measurement-validity.md`](measurement-validity.md) 与
[`rto-envelope.md`](rto-envelope.md)。

## 离线逐段取证（不改测量判定或产品默认）

对保留了 `stamp.txt`、`segments/`、两侧会话日志与主机证据的官方工作目录：

```sh
python3 tests/e2e/uplink_evidence.py /path/to/official-work > uplink-evidence.json
python3 tests/e2e/test_uplink_evidence.py
```

工具只读原始目录，复用 `measure_segments.analyze` 的有效性判定，不重写
`segments.json`。无效或未知段保留判定和原因，但不计算诊断增量；mux 与 bulk
各自读取所属客户端日志。默认 kernel 与可选 group 控制保留标签，不能混为一类。
输出保留应用 goodput、来源戳、协商路径数、开始前同向快照、段内前 3 秒轨迹
以及段内相邻快照的计数增量。没有快照不补零，不把服务端下行带宽拿来补上行。

`observed_window` 明确给出实际覆盖范围；增量只覆盖段内相邻观察点，通常不含
首尾不足一秒的部分，**不是全段总量**。开始前快照仅供比较初始 cwnd/ssthresh，
不并入段内计数。代际缺失/变化、路径身份或数量变化、控制模式切换、非正时间
或日志间隔超过 2.5 秒都阻止跨界相减。累计值缺失或下降（包括 16 位 total_rto
回绕）输出 null，绝不当成零事件。每路径 retrans/rto 也按 incarnation 独立计算。
`rwnd/sndbuf_per_busy` 是同一观察区间的累计受限时间/忙碌时间，不是墙钟占比。

使用方法：先按相同二进制、种子、profile、路径数与发送控制模式对齐官方有效
样本，再对照低档和高档的初始窗口、逐秒 retrans/rto、rwnd/sndbuf 增量和每路径
状态，沿上表验证候选机制。工具没有自动根因判决或吞吐通过标记；计数关联也不
证明因果。仍需两侧每段至少两个有效样本、两个不同种子及既定全矩阵；8 路下行
仍由原脚本按快/慢 echo 残留分类。本次只增加离线工具，不修改运行时日志或热路径。

顶层 `status=observed` 仅表示段内至少有两个快照，不保证相邻区间均可相减；
必须逐项检查 `intervals[].status` 及 null 字段。当前工具未解析逐路径写入量，
也不输出整段窗口轨迹，因此不能单凭该报告完成路径负载不均或信用阻塞的诊断；
这些仍须回看原始 `path_tx`、queued/credit 和后续窗口日志。
