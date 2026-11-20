# S-7 生产细采样与瓶颈归属（第五代，2026-11-20 16:41，17:44 更正）

原始目录：`/tmp/tbt-s7-1795164088`（软链 `/tmp/tbt-s7-latest`）。
本轮只做测量，不改任何生产状态：两端仍是 `661ad970`、服务、257 连接。

> **更正声明（17:44）**：本文初版把客户端 stats 里的
> `flow_credit=0:1:2011/8388661:25868083/0` 读成「剩余信用 2011 B」，
> 并据此推出「信用耗尽 / 账目不一致」。这是误读。`SessionCarrier.fs:402`
> 打印的是 `f.TxOffset` / `f.TxLimit`，即**该 flow 上行方向**的已发字节与额度；
> `25868083` 才是下行方向的 `RxDelivered`。按正确读法，上行额度还剩 8.38 MB，
> 下行已交付 25.87 MB 且仍在推进——**不存在信用耗尽**。第四代文档里
> 「flow_credit 只剩 9.8 KB」是同一个误读，不应再作为证据引用。

## 一、三条速率

同一 URL（`ipv4-c002-bud001-retn-isp.1.oca.nflxvideo.net/speedtest`，25 MiB）：

| 路径 | size (B) | time (s) | speed (B/s) | Mbit/s |
|---|---|---|---|---|
| 经隧道 `127.0.0.1:17893` | 24,477,696 | 15.000 | 1,631,819 | **13.05** |
| 本机直连该 URL | 26,214,400 | 4.667 | 5,616,458 | **44.9** |
| HND0 直连该 URL | 26,214,400 | 5.010 | 5,232,878 | **41.9** |

隧道拿到直连的 29–31%。这个 URL 的供数上限只有约 42–45 Mbit/s（不是 219 Mbit/s
的 Tokyo OCA），所以它不能判「隧道天花板是多少」，但能判「瓶颈在哪一段」：
同 URL 直连 42 Mbit/s 而隧道 13 Mbit/s。

## 二、客户端 stats 序列（每秒一行，下行口径）

| t (s) | rx_ack | rx_base | RxDelivered (B) | queued | srtt/minrtt |
|---|---|---|---|---|---|
| 3 | 0 | 0 | 0 | 0 | 314 / 314 |
| 9 | 641 | 321 | 9,306,455 | 0 | 314 / 314 |
| 14 | 1,128 | 808 | 19,580,003 | 0 | 314 / 314 |
| 18 | 1,426 | 1,106 | 25,868,083 | 0 | 314 / 314 |

- 步率 `(1426 − 4) / 14 ≈ 101.6 步/s`；每步 `25,868,083 / 1426 ≈ 18.1 KiB`；
  `101.6 × 18.1 KiB ≈ 1.84 MB/s` 对交付 1.63 MB/s（首尾截断解释差额）。
  **解码前沿的步率与交付字节率一致，解码不是瓶颈。**
- `queued=0` 全程，`srtt = minrtt = 314 ms`（1.00×）。承载无排队、无自致延迟。
- `rx_base` 落后 `rx_ack` 320 步 ≈ 5.8 MB，即 decoder 窗口内保留的在途步。
  `Grant = BaseStep + capacitySteps − Frontier = 1106 + 1024 − 1426 = 704 步`，
  约 12.7 MB，不是当前限制。

## 三、18443 socket

`sport = :18443` 每秒快照 18 次，`Recv-Q/Send-Q` **全程 `0/0`**。
tbtun 写 mihomo 从不阻塞，mihomo 立即读走。C1（mihomo 消费慢）因此被排除。

## 四、判定（更正后）

| 候选 | 判据 | 结果 |
|---|---|---|
| C1 mihomo 消费慢 | 18443 `send-q` 长期 > 256 KB | **排除**：18 次快照全 0 |
| C2 解码/前沿排队 | `rx_ack` 步/s × 每步字节 > 交付率 | **排除**：两者相等（1.84 vs 1.63 MB/s） |
| C3 smux 单连接限速 | 18443 双向空、tbtun Write 不阻塞但仍慢 | **未排除**：socket 层无积压只能说明 tbtun 交付不被反压，不能说明 mihomo 的 VLESS/smux 出站不慢 |

真正被本轮钉住的事实是步率本身：`101.6 步/s × 18.1 KiB ≈ 1.8 MB/s`。把它与
第四代的两个回环读数并排：

| 场景 | RTT | 路径数 | 步/s | 每步字节 | 吞吐 |
|---|---|---|---|---|---|
| 回环 netem | 40 ms | 256 | 81 | 18.5 KiB | 12.0 Mbit/s |
| 回环 netem | 40 ms | 4 | 81 | 18.5 KiB | 12.0 Mbit/s |
| 生产隧道 | 314 ms | 256 | 101.6 | 18.1 KiB | 13.05 Mbit/s |

三种完全不同的 RTT 与路径数下，步率都在 **80–102 步/s**，每步字节都在 **18 KiB**。
这是固定节拍的形状（约 10 ms 一轮、每轮约 18 帧），不是网络参数决定的。
`SessionCarrier.WaitUs` 在 `lastRoundSent < 256` 时返回 `NextTimerUs()`（winAck 周期
10 ms），与该形状吻合；第四代「把 `>= 256` 改成 `> 0` 无收益」的实验只说明
轮次加密不改变每轮帧数，没有排除节拍本身。

## 五、决定性数据（17:45，抓到服务端 stats）

原始文件 `/tmp/tbt-s7-server-1795164210/server.txt`，同一轮 curl 收 19,316,736 B
（12.9 Mbit/s）。服务端下行字段：

| 字段 | 值 | 含义 |
|---|---|---|
| `tx_direction` | down | 这是服务端下行统计 |
| `tx_head / peer_ack / grant_limit` | 2785 / 2582 / 3286 | 在途 203 步；grant 还允许 501 步 |
| `queued` | 0 | 没有待发数据 |
| `flow_credit` | `0:1:52561777/56415304:3993/0` | TxOffset 52.56 MB、TxLimit 56.42 MB，剩余 **3.85 MB** |
| `flowread` | `2958/52561777/2808` | 2958 次 Read，共 52.56 MB，2808 次 WouldBlock |
| `srtt / minrtt` | 927 / 310 ms | 2.99× |
| `cell_loss` | 0.0039 | 0.39% |
| `boosters / sealed` | 2290 / 42743 | 5.4% |

三条读数：

1. `ReadBytes / ReadCalls = 17,769 B`，恰好一步；第 2958 次里 2808 次（**95%**）
   读到 WouldBlock。每次 Read 都把 target socket 读空，不是「每轮只调一次 Read」。
2. 发送端额度全部充足：`queued=0`、grant 剩 501 步（≈9 MB）、flow credit 剩 3.85 MB。
   它不缺窗口，缺数据。
3. 服务端 `--target 127.0.0.1:18443` 是 **HND0 本机 mihomo 的入站**，不是 OCA。
   所以「服务端从哪读」的答案是 mihomo，不是网站。

## 六、结论（17:47 修正）

HND0 的 `/etc/mihomo-tbt4/config.yaml` 只有一条 `listeners`（`type: vless`、
`listen: 127.0.0.1`、`port: 18443`、`allow-insecure: true`），`rules:` 只有
`MATCH,DIRECT`，**没有任何 `proxies`**。所以 18443 之后不是海外节点，而是 mihomo
自己解 VLESS 再以本机网络 DIRECT 出站。

于是 13 对 42 的差距被夹在 mihomo 的 VLESS 入站 → DIRECT 之间：

- 同一 URL，HND0 上 `curl` 裸连 41.9 Mbit/s；
- 服务端从 `127.0.0.1:18443` 读只有 13 Mbit/s（`flowread` 每次 17.8 KiB、95% WouldBlock）。

`13.05 Mbit/s × 0.314 s ≈ 512 KB` 的在途量，与 smux 单流窗口、以及
`TCP_WINDOW_CLAMP = 600000` 同一量级。第四代「600 KB / 329 ms ≈ 14.6 Mbit/s」的数字
在这里反而对上了，但它约束的是**一条 flow 的在途**，不是 256 条承载的聚合。

结论：**tbtun 的 256 路承载与解码都不是当前限制；限制在单条 flow 的窗口，
而这条 flow 的窗口由客户端 mihomo 的 smux 出站与 HND0 的 VLESS 入站共同决定。**

第四代的 rcvbuf 修复仍是下行 3.5→11.8 Mbit/s 的真实原因：它消掉了 tbtun 从 target
读入的瓶颈，瓶颈现在移到了 target 后面那一跳。

## 七、下一步

1. **[第七代更正]** 本机 mihomo 的 `HND0TBT4` 出站已读：`type: vless`、
   `network: tcp`、`tls: false`、`smux.enabled: true`、`protocol: smux`、
   `max-connections: 1`、`min-streams: 1`。v1.19.33 **接受**
   `smux.max-stream-receive-window`（`mihomo -t` 通过），所以「窗口不可调」
   不成立；但 4 MiB 在基准崩塌期未观察到收益（见文末第六代更正）。可执行的替代是
   `max-connections: 1 → 4` 或 `smux.enabled: false`。第七代已用隔离实例实测
   `max-connections: 4`：3 轮 0 / 0 / 0.5 MB，与生产 `max-connections: 1` 同样
   出现 0 字节轮，故该参数不是停摆的开关。
2. 改后同一 URL 经 17893 做 A/B（每轮 ≤15 s × 5）。判据：中位 ≥ 1.5×、
   HND0 侧 `flowread` 的 WouldBlock 占比下降、SRTT ≤ 1.6 × minRTT。
3. 若 mihomo 侧无窗口可调，再查 tbtun 客户端：本机仍是旧版 `f2b63e00`，
   `SocketTuning.local` 的 4 MB 接收缓冲只装在服务端，客户端那一侧还是自动调优。
4. 本 URL 上限 42–45 Mbit/s，CASE-1 基线仍需 Tokyo OCA 重采。

### 第六代更正（17:02）

- 「mihomo 不暴露流缓冲、窗口不可调」不成立：v1.19.33 接受
  `smux.max-stream-receive-window`，`mihomo -t` 通过。
- 但 4 MiB 实测**无收益**：5 轮 0 / 0.41 / 0 / 0.63 / 0.14 MB/s，已回滚。
- 关闭 smux 使大文件单流中位升到 0.43–0.50 MB/s、0 字节轮从 2/3 降到 1/8，
  但短请求 3/3 `code=000`、ttfb 10 s 超时，不可用，已回滚到 smux 开。
- HND0 本机同一 VLESS 入站可跑 71–74 Mbit/s；256 条承载 `avg cwnd 17.9`、
  `retrans_total 4`、`app_limited 225/256`。所以限制不在 mihomo 的实现能力、
  不在内核 TCP 拥塞或重传，仍在隧道单流在 314 ms RTT 下的有效吞吐。
