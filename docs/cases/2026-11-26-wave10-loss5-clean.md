# Wave10 官方 5% 丢包净树终验（2026-11-26）

范围：干净树 `05ec1eb` 上单次官方 `tests/e2e/run.sh 20 200 0.05 4 10`。本文不写主机名、地址、端口、凭据或外部代理实现名。

## 样本身份

| 项 | 值 |
|---|---|
| script commit | `05ec1ebabbd21f41f0b50d4b9c0d9911fe1e9cd9` |
| server AOT | sha256 `3f7a533be89d775d038bba988ba77a831b5c1a46ea1838410114c7c2c05a24bb` |
| client dll | sha256 `dee29acbdad0938dd3e5699b78afd8da32bf25d7051c6b0047be358b5138f750` |
| client bundle | sha256 `349cc243aaf5883e669493c1c70ed928889b2ba5e5d4cc74e3f6de2caa750d8b` |
| parameters | owd=20ms, rate=200Mbit/s, loss=0.05, paths=4, seconds=10, window=1024, window_clamp=600000, seed=7, max_paths=64, carrier=tun, uplink_pace=0, new_repair=0 |
| host_gate | quiet, no fork/balloon, no discarded events |

## 核心结论

脚本退出码 **0**。两个会话都完成了握手、全部测试段和正常关闭；日志中没有 `cannot open TUN device tbt0`。运行结束后 `flock -n /tmp/tbtun-netns.lock true` 立即可获取，`ip netns list` 为空。此前 wave1、wave9 在第二会话启动时的 TUN 抢占/重开失败在这次干净样本中没有再出现。一次样本还不是统计意义上的“永久修复”，但本轮的关键验收项全部通过。

## 有效段

| 段 | 有效性 | 指标 |
|---|---|---|
| bulk_down1 | valid | 122.7 Mbit/s, 10.8 s, 166068224 B |
| bulk_up | valid | 15.9 Mbit/s, 22.7 s, 44957696 B |
| echo_small | valid | 89.7 Mbit/s, 0.23 s, 64/64 |
| ping | valid | ontime_rate=1.000, p50/p95/p99=40.8/41.1/41.8 ms, max=50.3 ms, n=200 |
| pingbulk | valid | 144.3 Mbit/s, 25.2 s; ontime_rate=1.000, p50/p95/p99=93.7/135.1/202.4 ms, max=1696.9 ms |
| iperf_1 | valid | 16.4 Mbit/s (up) |
| iperf_2 | valid | 31.8 Mbit/s (down) |
| iperf_4 | valid | 113.5 Mbit/s (down, 4 flows) |

未知段：echo1 / echo2 / iperf_3 / iperf_5，原因均为 `product_clock_catchup`，不参与吞吐结论。全部 12 段均 rc=0，无 freeze 事件，freeze 影响为 no。

## 对照与残余

裸 TCP 参考：1 流 167.5 Mbit/s、4 流 178.7 Mbit/s；裸 ping ontime_rate=1.000。相对 wave1 5% 场已分类数据（bulk_down1=88.3、bulk_up=15.9、pingbulk 伴随下行 126.8、iperf down=21.7）：本次有效下行更高、bulk_up 地板不变、iperf 4 流下行更高；不同批次公网条件有差异，不能视为严格 A/B。5% 丢包下 `bulk_up` 仍约 15.9 Mbit/s，是未解决的方向短板。

日志：`cell_loss` 范围客户端 mux 0.0002–0.445、服务端 mux 0.0123–0.148、客户端 bulk 0–0.1304、服务端 bulk 0–0.1495；服务端 `svc_gap_max_ms=1001`，无 host pause。服务端峰值 RSS：mux 78168 KiB、bulk 75896 KiB；CPU：mux 37.27 s、bulk 18.39 s。客户端峰值 RSS：mux 132264 KiB、bulk 123564 KiB。路径建立日志：客户端 mux 5 次、bulk 6 次 join。

## 判定

本轮是一次完整的官方有效样本：**exit 0、两会话全通、无 TUN 崩溃、无锁泄漏、无命名空间残留、无 freeze、host 安静**。 内核 TCP/TUN 载体路径的 TUN 重开问题在此样本中闭环。上行地板与非零 `cell_loss` 仍是后续优化目标。
