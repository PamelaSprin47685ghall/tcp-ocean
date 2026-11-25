# Wave1 官方矩阵（2026-11-26）

范围：新控制面首次进官方 netns 矩阵。本文不写私人主机名、地址或端口。未改产品代码。

## 样本身份

| 项 | 值 |
|---|---|
| script commit | `5b22237`（完整 `5b22237bcfb608394c6c1926d21b8a12a45758c1`） |
| 服务端 AOT | sha256 `ca31a2d8437c4566234cf6ef17717d4658f1ac0c049d6f605cd1e0cdd4b50017`，build_commit 同上 |
| 客户端 JIT dll | sha256 `746d60b0bb4b8b6146f1f8f17a6946beeaffd9ffa0b780ccbb2c94e8064fd838` |
| client bundle | sha256 `a6ea065ebb185389148aa13238b1cc4de717d2e439ff5ab19956d615bb46505f` |
| 参数 | owd 20 ms，200 Mbit/s，loss 0，paths 4，seconds 10，window 1024，window_clamp 600000，seed 7，server_max_paths 64，carrier tun，UPLINK_PACE=0，new_repair=0 |
| host_gate | quiet，MemAvailable 约 54.6 GiB，无 fork/balloon |

第二场（loss 0.05）与第三场（64 路）在本文写入时尚未完成，见文末。

## 第一场：20 / 200 / 0 / 4 / 10

裸 TCP 对照：1 流 188.1 Mbit/s，4 流 188.9 Mbit/s。裸 ping ontime_rate=1.000，p50/p99=40.5/42.0 ms。

| 段 | 有效性 | 结果 |
|---|---|---|
| echo1（8 流 × 1 MiB） | invalid，`clock_jump` | rc=0，144.6 Mbit/s，0.89 s，8/8 字节正确 |
| echo_small（64 流 × 20 KiB） | valid | 118.4 Mbit/s，0.17 s，64/64 |
| ping | valid | n=200，ontime_rate=1.000，p50/p95/p99/max=40.9/41.7/42.1/42.2 ms |
| bulk_up | valid | 104.5 Mbit/s，12.4 s，161480704 B |
| bulk_down1 | valid | 174.9 Mbit/s，10.6 s，231669760 B |
| pingbulk（负载下 ping + down2） | valid | ontime_rate=1.000，p50/p99=50.4/53.9 ms；伴随下行 175.7 Mbit/s |
| echo2 | invalid，`clock_jump` | rc=0，192.7 Mbit/s，0.66 s，8/8 |

host_pause：events=0，整场未因主机暂停作废。服务端 mux 峰值 RSS 72848 KiB，CPU 27.53 s。协商 paths=4。

### 与历史对照

- 0% 丢包、200 Mbit/s 对照预算约 146 Mbit/s（主线文档公式）。本场有效下行 174.9 / 175.7，已高于该预算口径，但不能据此宣称物理满速：载体是 tun，速率组仍报 `phase=startup`，且只这一颗种子。
- `cell_loss`：客户端 mux 日志在有效负载段大约 0.013–0.085，收尾降到 0.000 附近后又因空闲计时爬到 0.009；服务端收尾 `cell_loss=0.0008`。低于历史 0.28–0.36。这是单场日志读数，不是分段中位数。
- 排队：负载下 ping p99=53.9 ms，相对空载 p99=42.1 ms，没有出现历史饱和下行约 500 ms 的交互卡住。服务端 `svc_gap_max_ms=880` 出现在会话已空、stats 1 s 心跳之后，不是负载段排队。
- 路径刷新很勤：客户端日志几乎每秒 `path N joined`。空闲会话仍在重建五元组。这与“空闲不刷”的设计不一致，是观察不是修复。

### 回归：第二会话打不开 TUN

整场退出码 1。mux 正常关闭后，bulk 会话在 15.09 s 才起来，10 s 握手失败。服务端：

```
Aborted
Unhandled exception. System.Exception: cannot open TUN device tbt0
```

`Server.fs` 的 SIGTERM 路径现在关 listener 并打印 `draining`/`stopped`，但 `TunStack` 没有 Close，注释写明 fd 留给进程退出回收。脚本在前一进程仍占着 `tbt0` 时启动下一会话，于是第二会话直接 abort。iperf 半场因此没有样本。按本轮约束未改代码。

## 第二场：20 / 200 / 0.05 / 4 / 10

同一二进制，`ALLOW_DIRTY=1` 因为结果文档已在树里。脚本戳记 `script_commit=5b22237-dirty`；服务端与客户端 sha256 与第一场相同，build_commit 仍是 `5b22237bcfb608394c6c1926d21b8a12a45758c1`。seed 7。host_gate quiet。退出码 0。两会话握手都成功，没有再撞上 `tbt0`。

裸 TCP：1 流 165.1，4 流 185.6 Mbit/s。裸 ping ontime=1.000，但 p99=536.5 ms（5% 丢包下的内核重传尾）。

| 段 | 有效性 | 结果 |
|---|---|---|
| echo1 | invalid，`clock_jump` | rc=0，39.2 Mbit/s，8/8 |
| echo_small | valid | 40.0 Mbit/s，64/64 |
| ping | valid | ontime_rate=1.000，p50/p99=40.9/41.9 ms |
| bulk_up | valid | 15.9 Mbit/s，22.8 s |
| bulk_down1 | valid | 88.3 Mbit/s，11.0 s |
| pingbulk | valid | ontime_rate=1.000，p50/p99=94.2/191.4 ms，max 248.6；伴随下行 126.8 Mbit/s |
| echo2 | invalid，`clock_jump` | rc=0，43.8 Mbit/s，8/8 |
| iperf up / 再 up | valid / invalid | 14.3；第二次 4.7 因 `clock_jump` 作废 |
| iperf down | valid | 21.7 |
| iperf -P 4 up / down | valid | 15.9 / 20.2 |

服务端 mux RSS 76220 KiB，CPU 36.19 s；bulk 峰值 RSS 82008 KiB。`cell_loss` 日志：客户端 0–0.3588，服务端 0.0128–0.2241。这覆盖了历史 0.28–0.36 的区间，不能说 cell_loss 已压下去。负载下 ping p99=191 ms，不是 500 ms 卡住，也不是空载 42 ms。上行有效地板 15.9（bulk）/ 14.3（iperf），相对裸 4 流 185.6 只有约 8–9%。下行有效 88.3，pingbulk 伴随 126.8，约为裸 4 流的 48–68%。

wanem 实丢：to_far 42962/860067≈5.0%，to_near 37649/748587≈5.0%，与设定一致。host_pause events=0。

## 未跑

`run.sh 20 200 0.05 64 10` 未开。两场已给出 4 路在 0% 与 5% 下的方向差，64 路需要另一颗完整运行，不能用这两场外推。

## 结论

- 单种子。0% 场第二会话失败，iperf 半场缺失。5% 场完整。
- 交互：0% 负载下 p99=54 ms；5% 负载下 p99=191 ms。没有复现历史约 500 ms 饱和排队，也没有“无数秒卡住”。
- 吞吐：0% 下行接近对照线；5% 上行明显低于裸链，是这批样本里的地板。
- 回归已复现一次：优雅退出不关 TUN，下一会话 `cannot open TUN device tbt0` 并 abort。5% 场没再撞上，不代表修好。
- `clock_jump` 在两场的 echo1/echo2 上都作废了段。产品日志自己在记 jump，不是主机 pause。
- 客户端在 0% 场几乎每秒重建一条路径。空闲刷新仍在。
