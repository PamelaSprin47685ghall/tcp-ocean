# Wave9 官方 5% 丢包矩阵（2026-11-26）

范围：干净树 `3e1d663` 上单次官方 `tests/e2e/run.sh 20 200 0.05 4 10` 复验。本文不写主机名、地址、端口或代理实现名。

## 样本身份

| 项 | 值 |
|---|---|
| script commit | `3e1d663adf7db415a3165f3815a383e66de982e6` |
| 服务端 AOT | sha256 `5390aa1bdffb46806880364dfc3933682fde3cacfa41524dda60632346a7a3a1` |
| 客户端 JIT dll | sha256 `8e223e7e0b36a00f70315c67d7d241d61607f3b6a25b3c3340dc7e0826b565a5` |
| client bundle | sha256 `d4806295df54a4d84e71fe9de5bcaa91bfa72fe477464c28d5ff40fcb641b98e` |
| 参数 | owd 20 ms，200 Mbit/s，loss 0.05，paths 4，seconds 10，window 1024，window_clamp 600000，seed 7，server_max_paths 64，carrier tun，uplink_pace 0，new_repair 0 |
| host_gate | quiet；MemAvailable 约 55.9 GiB；无 pause、无 balloon |

运行开始时工作树为空、锁空闲、无残留命名空间。运行结束后 `flock -n /tmp/tbtun-netns.lock true` **立即成功**，无 MSBuild 锁泄漏；命名空间列表为空。

## 结果

脚本退出码 1。mux 会话成功产出 7 段有效分类；bulk 会话启动服务端时崩溃，日志为 `cannot open TUN device tbt0`。这与 wave1 0% 场、wave8 5% 截断场曾见到的 TUN 重开失败同类；`TunStack.Close()` 的既有修复没有在这次重开中彻底闭环。iperf 段因此没有生成。

| 段 | 有效性 | 原因 | 指标 |
|---|---|---|---|
| bulk_down1 | valid | none | 78.5 Mbit/s，10.7 s，105447424 B |
| bulk_up | valid | none | 16.2 Mbit/s，22.7 s，46006272 B |
| echo1 | unknown | product_clock_catchup:server-mux.log | 39.8 Mbit/s，3.22 s，8/8 |
| echo2 | valid | none | 39.1 Mbit/s，3.27 s，8/8 |
| echo_small | valid | none | 67.1 Mbit/s，0.31 s，64/64 |
| ping | valid | none | ontime_rate=1.000，p50/p95/p99/max=40.8/41.1/41.4/42.1 ms，n=200 |
| pingbulk | valid | none | 143.3 Mbit/s，24.1 s；ontime_rate=1.000，p50/p95/p99/max=93.8/144.9/426.8/441.2 ms |

有效下载 78.5 Mbit/s，高于 wave1 5% 场的 88.3 是否可比需谨慎：wave1 的 pingbulk 伴随下行 126.8、iperf down 21.7；本次没有 iperf。有效上行 16.2，与 wave1 的 15.9、wave8 stdout 观察的 14.8 同量级，地板未改变。负载下 ping p99=426.8，明显高于 wave1 的 191.4，也高于 wave8 截断观察的 175.9；单种子，不可直接宣布退化。

`cell_loss` 日志：客户端 mux 0.0002–0.1273，服务端 mux 0.0133–0.128；服务端 mux RSS 峰值 79688 KiB，客户端 mux RSS 峰值 130612 KiB。服务端 `svc_gap_max_ms=1001`，无 freeze/host_pause。bulk 服务端崩溃前有 `cannot open TUN`，bulk 日志没有可分类指标。

## 结论

- 官方 5% 场仍不能完整通过：mux 有效分类完整，但第二会话因 TUN 打不开 abort。
- 锁修复有效：运行结束后锁立即可用，MSBuild 常驻进程不再继承锁 fd。
- 上行 16.2 Mbit/s 地板与历次 5% 样本一致，仍是最主要方向短板。
- 这不是产品验收通过；TUN 重开与上行地板均需后续定位与复验。
