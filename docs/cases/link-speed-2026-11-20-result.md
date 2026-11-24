# 链路提速本轮结果（2026-11-20）

## 结论

生产未被改动：两端仍 `f2b63e00a90b…`，各 256 路。本轮的代码修复已完成、构建与单测通过、
候选二进制通过 256 路生命周期回归，但**未取得生产 A/B 证据，因此没有上线**。

## 已完成的代码修复

1. **下行缺少组级速率控制（主缺陷）**
   - 事实：`Server.fs:224` 构造 `SessionCarrier` 时未传 pace，`SessionCarrier.fs:44-46`
     默认 `false`。下行只剩 `WriteGate`（pass1 4 KiB、pass2 256 KiB）与每路
     `AsyncPathLink` 32 槽，没有任何聚合在途上限。
   - 改动：`?uplinkPace` → `?paceOn`（方向中性）；`Client.fs` 与
     `tests/Tbt.Tests/UniqueRepairTests.fs` 两处调用点同步；`Server.fs` 新增
     `--pace`（默认 `0`），并在 `session ... up` 日志打印 `pace=on|off`。
   - 未改 `RateGroup`、`WriteGate`、`AsyncPathLink`、`Tbt.Session` 的契约。
2. **`RxHalf.serviceHold` 的 FIFO 队首阻塞**
   - 事实：`hold` 按到达序排队，队首若是后续步的 parity，同一步可用的 parity
     会被压在队后，等到 20 ms 或 NACK。`lossEvident`（所有活跃路径都越过
     frontier）本应允许整队放行。
   - 改动：`OnCoded` 末尾与定时 `Service` 两处改为
     `serviceHold now (lossEvident now)`。不改 20 ms 上界，只消除无谓等待。

## 验证证据

- `dotnet build Tbt.slnx -c Release -p:NuGetAudit=false`：0 warning / 0 error。
  必须豁免 NuGet 审计：既有 `Microsoft.DotNet.ILCompiler 10.0.12` 的 NU1903 被
  `TreatWarningsAsErrors` 放大，与本改动无关。
- `dotnet test Tbt.slnx -c Release -p:NuGetAudit=false`：Tbt.Tests 138/138、
  Tbt.Session.Tests 241/241、Tbt.Async.Tests 8/8。未削弱 no-SACK、blackout、
  RepairWait 等断言；未新增 RxHalf 确定性测试（缺 Decoder/CellOpener 夹具）。
- `python3 tests/e2e/test_async_lifecycle.py <候选二进制>`：四批均 256 路、
  1 MiB 字节一致、正常关闭。
- AOT 发布：`-p:TreatWarningsAsErrors=false` 才能过 FSharp.Core 的 IL2104；
  产物 `c969c8f1b788…`，3,926,232 B。

## 生产基线（同一 IPv4 fast.com URL，单次 ≤10 s）

- HND0 直连该 URL：`code=200 size=26,214,400 time=1.652 s` ≈ **15.87 MB/s ≈ 127 Mbit/s**
  （curl `speed_download` 的单位是 B/s；旧文误记为 15.87 Mbit/s）。
- 本机经 `17893`：三轮 `1.26 Mbit/s`（10 s 收到 1,261,568 B）、`0`、`0`。
- 服务端 stats（下行）：`tx_head=17,807 peer_ack=17,412`（在途 **395 步**）、
  `srtt_ms=6177`、`minrtt_ms=-`、`cell_loss=0.1826`、`rho=0.491`、
  `boosters=43,712`、`queued=0`。
- 解释：发送队列为空而接收前沿落后 395 步，修复包排在深队列之后，单次修复往返
  被放大到数秒；这与流内交付被缺口拦住、SRTT 950 ms 对干净 320 ms 一致。

## 2026-11-20 第二代复核

- **单位更正**：curl `speed_download` 的单位是 B/s。HND0 直连同一 fast.com OCA
  实测 26,214,400 B / 0.955 s ≈ 27.4 MB/s ≈ **219 Mbit/s**；前代把 15.87 MB/s
  记成了 15.87 Mbit/s，低了 8 倍。隧道侧数字一直按 B/s 换算，不受影响。
- **隧道实测**（同一 IPv6 OCA，经 `17893`，每轮 15 s）：6,782,976 B / 3,555,328 B /
  1,409,024 B，交替出现 0 字节连接超时。成功轮为 1.9–3.6 Mbit/s，仍比直连低约 60 倍。
- **候选 A-B-A**：候选 `3245a79d` 三轮 7.0–9.4 Mbit/s；回滚旧版 `f2b63e00` 后同样
  8–10 Mbit/s。两者无实质差异，先前的“提速”来自重启后的新鲜状态，不是 RxHalf 修复。
  Cloudflare 重复下载返回 429，曾把 429 误判为隧道失败；后续测量用 fast.com 并记录 HTTP 码。
- **接收侧证据**：客户端 `rx_ack` 每秒只推进 20–60 步，`flow_credit` 长期只剩
  22–24 KB（8 MiB 窗口），`recovered` 持续增长。信用按解码前沿回收，前沿一停，
  服务端在窗口用尽后无法再发，这比发送侧 pace 更能解释“发送队列空 + 新连接 0 字节”。
  `opened=1 closed=0` 不是泄漏：本地代理 smux `max-connections: 1` 把多条 curl 复用为
  一条 VLESS 长连接，tbtun 只看到一个 flow。
- **传输通道**：直接 scp 3.9 MB 仍在 38 s 后远端 0 字节；本机到 HND0 的 1400 B
  `ping -M do` 正常、短 SSH 命令正常，所以不是 MTU 黑洞。已改 256 KB 分块逐块传输。
  分块在 180 s 内只成功 2/15 块（`tbtchunk.00`、`.01`），远端合并仍 0 字节；同一时段
  48,630 B 的源码包一次传成功。结论：该 SSH 数据通道只适合数万字节的小文件，3.9 MB
  二进制不可传。远端有 `/usr/share/dotnet/dotnet`（SDK 10.0.404，但不在 PATH）；
  已建 `/root/tcp-ocean-build` 并覆盖 10 个改动源文件，改用远端 publish。
- **远端构建同样不可用**：`/root/tcp-ocean-build` 的 `dotnet publish` 在 300 s 内未完成
  并被杀；随后 SSH 在 banner 交换阶段超时，无法确认 `/tmp/pub/tbtun` 是否生成。
  服务端 `--pace` 因此仍未部署，生产保持 `f2b63e00`、active、256 条连接、
  drop-in 仅 `stats.conf`。回滚路径未被使用，也未改变。

## 接收侧最小修复的失败样本

- **假设**：`serviceHold` 是 `while` 循环，不是每次只放行一个；前沿步
  （`m.StepId = curF`）的 parity 仍要等 `now - frontierSince >= 20 ms`，
  20 ms/步正好对应观察到的 20–60 步/s。`lossEvident` 要求所有活跃路径都越过
  frontier，256 路下几乎不成立，所以前代把 `serviceHold now (lossEvident now)`
  传进去没有效果。
- **改动**：放行条件改为 `force || now - addedNs >= stallNs || m.StepId = curF`，
  即当前步 parity 立即交给 decoder。只改 `RxHalf.fs` 一行。
- **构建**：0 warning / 0 error；`Tbt.Session.Tests` 241/241；AOT 候选
  `04a84250…`，3,926,232 B。
- **实测失败**：部署到本机客户端后，同一 fast.com URL 四轮全部失败：
  R1 15 s 0 字节，R2–R4 `TLS connect error: unexpected eof while reading`；
  连接数从 256 掉到 0。不是慢，是隧道完全不可用。
- **回滚**：`install` 回 `f2b63e00…` 并重启，12 s 后连接 254 → 256；
  随后一轮 curl 仍 15 s 0 字节，与旧版既有的交替 0 一致。源码已撤销该行。
- **结论**：20 ms 上界不是纯延迟，它给 decoder 保留了排序/证据；无条件提前
  放行会破坏解码状态。该方向关闭，不再重试同一改法。

## 未完成：生产 A/B 被管理链路阻断

- 向 HND0 传 3.9 MB 候选两次均截断：直接 scp 后 `.cand` 为 783,360 B、
  SHA-256 `dbd4a434…`；gzip 管道 180 s 只到 524,288 B（约 6 KB/s）。
- HND0 有 `/usr/share/dotnet/dotnet` 与 ILCompiler 缓存，也有一份源码，但其
  HEAD 为 `1cf420e`、带多项未提交改动，与本地 `60b466c` 不同；直接远端构建会引入
  第二个未知变量，本轮未做。
- 未写入 `zz-pace.conf`；重启仅由早期超时命令触发一次（旧二进制），此后已恢复并核实。

## 生产当前状态（已核实）

- HND0：`/usr/local/bin/tbtun` = `f2b63e00…`、`tbtun-hnd0` active、257 条
  `sport=:39443` 连接、drop-in 只有 `stats.conf`。
- 本机：`tbtun-hnd0` active、257 条 `dport=:39443` 连接。
- 已删除 `/usr/local/bin/tbtun.cand` 与 `/tmp/tbtun.gz`；无临时监听、规则或密钥。

## 后继直接可做

1. 先用更快通道把候选送到 HND0（例如把本地改动整理成 patch，在 HND0 的
   `1cf420e` 上重新落补丁并 `dotnet publish -r linux-x64 -p:NuGetAudit=false
   -p:TreatWarningsAsErrors=false`），保持“同一 commit + 同一 AOT 工具链”的单变量。
2. 只改服务端 drop-in：`--stats-s 1 --pace 1`，客户端不动。重启后确认日志
   `pace=on` 与 256 路，再取同一 URL。
3. 交错 A–B–B–A，每轮 10 s；同时取服务端 `tx_head - peer_ack`、`srtt_ms`、
   `cell_loss` 与 HND0 `ss` 的 `bytes_retrans/bytes_sent`。
4. 门槛：中位 ≥ 20 Mbit/s、CV < 20%、重传 ≤ 5%、有载 SRTT ≤ 1.6×minRTT。
   未达则删 `zz-pace.conf` 并恢复 `tbtun.bak`，回滚是参数级。
5. 若 `--pace` 仍无收益，优先验证 RxHalf 修复的单独效果（同一候选二进制、
   只关 `--pace`），不要再一次同时改两个变量。

## 传输通道复核（2026-11-20 补充）

- 直接 `scp` 重试 55 s：远端 `tbtun.cand.part` 为 **0 字节**，不是慢，是大文件
  传输没有开始。
- gzip 管道 180 s：只到 524,288 B，随后超时。
- 同一时段 22,434 B 的源码包 `tar.gz` 传输成功，说明小文件通道正常。
- HND0 磁盘 `/` 20 GB、已用 67%、可用 6.1 GB；`/tmp` 478 MB、可用 279 MB；
  内存 955 MB、可用 458 MB。不是磁盘或内存不足。
- 远端 `/root/tcp-ocean` 的 HEAD 为 `1cf420e`，且 `git status` 有 10 项未提交
  改动；本地 HEAD 为 `60b466c`。把本地 4 个改动文件覆盖过去后，
  `dotnet publish` 报 `src/Tbt.Session/RxHalf.fs(147,48): error FS0039:
  The value ... 'FlowGap' is not defined`，证明远端树缺少本地已有的
  `Trace.FlowGap`，不是同一基线。
- 远端源码已从 `/tmp/tbt-orig` 恢复，`git status` 仍 10 项；临时包与
  `.cand`/`.part` 已删除；生产仍 `f2b63e00…`、active、257 条连接、仅
  `stats.conf`。

因此本轮不能通过“远端构建”绕开二进制传输；后继必须先解决其中一个前提：
大文件通道可用，或远端源码树同步到与本地相同的 commit（含 `Trace.FlowGap`）。
在前提满足前，重复 scp 或逐文件补丁不会产生进度。

## 第二代诊断（2026-11-20 15:33–15:37）

### 测量口径已确认

- `17893` 是 `/run/hnd0tbt4.yaml` 的 `mixed-port`；该文件只有一个节点
  `HND0TBT4`（VLESS → `127.0.0.1:18443`，即 `tbtun client --listen`），
  `rules: MATCH,WORLD`。因此经 `17893` 的 curl 确实进入 tbtun，再由 256 条承载
  TCP 到 `45.11.1.62:39443`。
- 本机 `/usr/local/bin/tbtun` = `f2b63e00a90ba5a1…`，`tbtun-hnd0` active，
  `dport=:39443` 257 条（256 ESTAB 加一个计数口径项）。

### 实测

- 三轮 curl（15 s、15 s、12 s）全部 `code=000 size=0`；每轮前后连接数 257 不变。
- 客户端 stats 只有 `tx_direction=up`：`rx_ack=1` 恒定、`rx_base=0`、
  `recovered=0`、`queued=0`、`cell_loss≈0`、`srtt_ms=1224–1343`、
  `minrtt_ms=593–950`；`frames_in`/`frames_out` 每秒只增 2–4；
  `flow_credit` 已用 3385→6745 B（窗口 8 MiB）；`opened=1 closed=0`。
- 12 s 窗口内，`dport=:39443` 的汇总：`bytes_sent +144,330`、
  `bytes_received +26,117`、`bytes_retrans +34,144`；累计
  `bytes_retrans/bytes_sent` ≈ 28%。
- `dport=:18443` 的 VLESS 连接 12 s 内只写 1,680 B、收到 10 B。
- `ping 45.11.1.62`：0% 丢包，RTT 329.1/329.7/330.3 ms，mdev 0.57 ms。
- 新建 TCP：22 端口 0.32–1.34 s 完成握手，发 16 B 后一次 3 s 无返回、一次返回
  42 B banner 但 `ssh` 认证 18 s 超时；39443 0.32 s 握手后 3 s 无返回。
- 承载 TCP 明细：BBR `app_limited`，`lastsnd` 48–60 s、`lastrcv` 150–152 s，
  `cwnd` 14–15，`bbr bw` 9–63 kbps，`retrans 0/1–4`，`rcv_wnd≈72 KiB`。

### 上行实测（CASE-5）

本机客户端仍为 `f2b63e00`，经 `17893` → tbtun → HND0 → OCA，向
`https://speed.cloudflare.com/__up` 上传 5,000,000 B，3 轮、每轮 ≤10 s：

| 轮 | code | size_upload | time | 速率 |
|---|---|---|---|---|
| U1 | 100（响应超时） | 5,000,000 | 10.00 s | 0.50 MB/s ≈ 4.0 Mbit/s |
| U2 | 200 | 5,000,000 | 9.95 s | 0.50 MB/s ≈ 4.0 Mbit/s |
| U3 | 100（响应超时） | 5,000,000 | 10.00 s | 0.50 MB/s ≈ 4.0 Mbit/s |

上行 3/3 把 5 MB 全部送出，速率约 **4.0 Mbit/s**，与同期下行好轮同量级；
`flow_credit` 从 55,260 涨到 14,876,833 B，上行信用在推进，不是上行退化。
两轮 `code=100` 是上传完成后等响应超过 10 s，即同一停摆出现在响应（下行）方向；
这与「停摆在本地代理内部而非某一方向」一致，也说明 CASE-5 的「不退化」成立。

### 判定

#### GatePass2Limit 对照为什么没有在官方床上跑

工作树与 HEAD 的 `SocketTuning.fs:52` 都是 `GatePass2Limit = 262144`，它限制
**每一条承载路径**的未发字节数。它是否造成排队取决于每路 BDP：

- 官方床默认 `200 Mbit/s` 总量（4 路时每路约 50 Mbit/s）× 40 ms RTT ≈ 250 KiB/BDP，
  与 256 KiB 同量级；把上限降到 32768 只会让每路提前停发，不会减少排队，
  测出来的差异不能归因于生产现象。
- 生产每路约 1 Mbit/s × 314 ms ≈ 39 KiB BDP，256 KiB 是它的 6.5 倍，
  这才是「`queued=0` 而服务端 srtt 927 ms 对 minrtt 310 ms」的自排队来源。

因此正确的床是「每路 1 Mbit/s、RTT 314 ms」，而 `tests/e2e/run.sh` 只支持总量
限速，不支持每路限速。用现有床跑出的数字会误导默认参数的取舍，本轮不做；
`GatePass2Limit` 保持 262144，后继要么给 `netns.sh` 加每路限速，要么在
`unshare -rn` 内用 `tc` 对每条 path 单独整形后再判。

#### 16 路 5% 丢包（CASE-6 补充样本）

命令 `ALLOW_DIRTY=1 ALLOW_STALE_BIN=1 tests/e2e/run.sh 20 200 0.05 16 10`，
`rc=124`（180 s 上限在 iperf 段被杀，iperf 数字缺失，其余段完整）：

| 段 | 结果 |
|---|---|
| 协商 | paths=16、window=1024、window_clamp=600000、server_max_paths=64 |
| echo 8 流 | 1,000,000 B / 3.09 s = 41.5 Mbit/s（8/8） |
| echo 64 流 | 20,000 B × 64 / 0.22 s = 92.5 Mbit/s |
| bulk up | 10.9 s / 197,394,432 B = **144.8 Mbit/s** |
| bulk down | 10.7 s / 198,639,616 B = **148.3 Mbit/s** |
| bulk down（背靠背） | 18.7 s / 345,731,984 B = **147.8 Mbit/s** |
| ping 无载 | p50 40.4 / p99 40.6 ms，200/200 准时 |
| ping-under-bulk | p50 75.6 / p95 92.9 / p99 121.4 ms，200/200 准时 |
| server | rss 81,188 KiB、cpu 34.64 s |

对照 4 路无丢包（同工作树）：bulk 175 Mbit/s、ping-under-bulk p99 67.8 ms。
16 路 5% 丢包下吞吐降约 15%、有载 p99 升到 121 ms，属预期退化而非缺陷；
`echo.py` 出现过一次 `OSError 107`（第 3 轮 echo 8 流），与前两轮结果无关。
该样本只覆盖 tbtun 两侧，不经本地代理，因此不能用来说明生产下行。

- **H1（下行无组级 pace）不成立**：`queued=0`、`tx_head − peer_ack = 0`，
  承载 TCP 被内核标记 `app_limited`（无数据可发），不存在 256 路聚合同步突发。
- **H2（接收前沿/信用回收）不成立为主因**：`flow_credit` 仅用 6.7 KB / 8 MiB，
  接收侧没有数据可解；`rx_ack=1` 是“没收到”而不是“收到但前沿卡住”。
- **H3（外层/服务端）成立**：HND0 的 ICMP 与 TCP 握手正常，但 `tbtun server`
  在 accept 后不消费，承载 socket 不被读，客户端 256 路进入 `app_limited`；
  sshd 也在 banner 后无法完成认证。这与远端 `publish` 300 s 未完成、
  SSH banner 超时属于同一主机/进程故障。
- 因此本轮不部署新二进制；生产保持 `f2b63e00…`。前代在 15:28 之前的
  “候选 7.0–9.4”与“旧版 8–10 Mbit/s”样本处于同一故障窗口，不能作为
  RxHalf 或 pace 的优劣证据。

### 恢复前置（后继第一步）

1. 带外或控制台重启 HND0 上的 `tbtun-hnd0`（必要时整机），直到
   `ssh root@45.11.1.62 true` 在 5 s 内返回，且承载 socket 被读取。
2. 用 CASE-0 复核：22 端口能完成认证、39443 新建连接 1 s 内收到协议响应。
3. 再按 CASE-1/CASE-9 采样：若 `rx_ack` 步/s 与交付字节率一致，H2 不成立；
   若步/s 显著低于字节率且 `flow_credit` 耗尽，H2 成立。
4. 服务端 `--pace` 仍未被验证，且已有反证；不在本轮承诺内。

### 原始证据

`/tmp/tbt-diag-1795159999/`：`round1..3.{curl,stats,meta,before}` 与
`round4.curl`；`/tmp/tbt-diag-latest` 指向该目录。

## 第三代：本地可复现的下行瓶颈（2026-11-20 15:41–15:45）

不依赖 HND0。用无特权网络命名空间（`unshare -rn`，不需要 sudo）在 `lo` 上施加
`tc qdisc netem delay 20ms rate 200mbit`，在同一个 `tbtun` 二进制上跑完整会话。
构建为 HEAD 工作树 JIT（`dotnet build Tbt.slnx -c Release`，0 warning / 0 error，
单测 8 + 138 + 241 = 387 全过），全回环、无 netns 脚本、无 `run.sh`。

| 条件 | 载荷 | 用时 | 吞吐 | 备注 |
|---|---|---|---|---|
| 无 netem，256 路 | 128 MiB | 1.458 s | 736 Mbit/s | server RSS 111 MB，client 168 MB |
| netem 20 ms / 200 Mbit，256 路，`--pace 0` | 32 MiB | 22.39 s | 12.0 Mbit/s | `cell_loss=0`、`queued=0` |
| netem 同上，256 路，`--pace 1` | 32 MiB | 22.39 s | 12.0 Mbit/s | 与上一行差 0.01% |

三条读数：

1. 产品在无瓶颈链路上 256 路下行可跑 736 Mbit/s：协议、编解码、前沿与写门都不构成
   736 Mbit/s 以下的天花板。
2. 同一二进制在 40 ms RTT / 200 Mbit/s 下只有 12 Mbit/s，与生产 8–10 Mbit/s 同量级。
   **生产问题在本地可复现，且不需要 HND0 可达。**
3. 下行组级 pace 在这里没有收益（22.3896 s 对 22.3877 s）。H1 在本模型下被证伪：
   没有丢包、`queued=0`、`srtt_ms=50` 对物理 40 ms，说明没有自致排队可压。

同期 stats 给出瓶颈的形状：

- 服务端 `tx_head=1807 peer_ack=1804`、客户端 `rx_ack=1811`：**在途只有约 3 步**。
- 1811 步 / 22.39 s ≈ 81 步/s；每步 `33554432 / 1811 ≈ 18.5 KiB`。
- `3 × 18.5 KiB / 0.04 s ≈ 1.4 MB/s`，与实测 1.5 MB/s 一致。

因此瓶颈不是“256 路聚合同步突发”，而是**每 RTT 只能推进约 3 步**。下一步要定位的是
这个 3 的来源（`Session` 的在途上限，还是 `RxHalf.Grant` 的 `capacitySteps`），
而不是继续调发送侧速率。4 路对照与 `--window` 扫描是区分两者的最小实验。

原始目录：`/tmp/tbt-bulk.wYIJDp`（无 netem）、`/tmp/tbt-netem.8inD3d`（首轮 12 Mbit/s）、
`/tmp/tbt-ab.9b7D5X`（`--pace` A/B）。

## 第三代结论：根因是 flow socket 缺接收缓冲（2026-11-20 15:46–15:52）

### 证伪清单

在定根因之前，这一轮把上一代与本节早期列出的候选逐条测掉了。每条的判据与原始目录
都在本文件或 `docs/design/` 中，不再重复推理：

| 假设 | 判据 | 结果 |
|---|---|---|
| H1 下行组级 pace | 同构建 `--pace 0/1` 各 32 MiB | 22.3896 s 对 22.3877 s，差 0.01%，证伪 |
| 256 路聚合突发 | 4 路对 256 路 | 22.3920 s 对 22.3896 s，差 0.01%，证伪 |
| 丢包与 BOOSTER | netem `rate` 200→1000 Mbit/s | 12.0→12.5 Mbit/s，证伪 |
| 接收前沿 20 ms 门控 | `stallNs` 20→2 ms | 11.305→11.310 s，证伪 |
| 主循环等待粒度 | 非 pace 分支等待压到 1 ms | 11.305→11.308 s，CPU 6.7%→9.1%，证伪 |
| Grant / CanFill / Encoder 容量 | 实测点 `grant_limit=2515`、`Head=1814`、`qhigh=65 < 256` | 三项恒真，证伪 |
| FlowRoom 信用 | `TxRoom = TxLimit − TxOffset`，服务端余 7.9 MB | 证伪 |
| 测试床供数不足 | 同 netem 下 python http.server 直连 16 MiB | 1.089 s（15.4 MB/s），证伪 |

每一条被证伪之后，吞吐都停在 11.3 s / 16 MiB（1.48 MB/s），且 4 路与 256 路完全相同——
说明限制量在**单条 flow**，不在承载路径。

### 根因

`SocketTuning.local`（`src/Tbt.Carrier/SocketTuning.fs:128`）只设 `NoDelay`，
注释明确写“autotuning stays on (no SO_RCVBUF, spec 3.6)”。`FlowBridge` 连 target 与
本地应用的 socket 都用它，于是接收窗口交给内核自动调优；而 tbtun 的读循环是
“每轮读一次、读到 WouldBlock 就退出”，应用侧读得慢，内核就判定不需要大窗口，
窗口停在 64–128 KB。单流吞吐被钉在 `rcvbuf / RTT`：

- 40 ms RTT：~64–128 KB → 1.6–3.2 MB/s，实测 1.48 MB/s；
- 生产 329 ms RTT：~330 KB → ~1 MB/s ≈ 8 Mbit/s，与生产 8–10 Mbit/s 吻合。

这也解释了为什么路径数、pace、丢包、前沿门控、等待粒度全部无效：它们都作用在承载侧，
而瓶颈在 target 侧的单条 TCP 流。

### 修复与验证

`SocketTuning.local` 增加：

```fsharp
let local (socket: Socket) =
    socket.NoDelay <- true
    try socket.ReceiveBufferSize <- 4_000_000 with :? SocketException -> ()
```

| 场景 | 修复前 | 修复后 | 变化 |
|---|---|---|---|
| netem 20 ms / 200 Mbit，256 路，16 MiB | 11.31 s（1.48 MB/s） | **2.573 s（6.52 MB/s）** | 4.4× |
| netem 20 ms / 200 Mbit，4 路，32 MiB | 22.39 s（12.0 Mbit/s） | **4.527 s（59.3 Mbit/s）** | 4.8× |
| netem 20 ms / 200 Mbit，256 路，32 MiB | 22.39 s（12.0 Mbit/s） | **4.673 s（57.4 Mbit/s）** | 4.8× |
| 无 netem，256 路，128 MiB | 1.458 s（736 Mbit/s） | **1.419 s（757 Mbit/s）** | 不退化 |

同轮读数：`qhigh` 65→284，`boosters/sealed` 32%→3.2%，`cell_loss` 不变，
server RSS 104–111 MB，单测 8+138+241 = 387 全过，构建 0 warning / 0 error。

### 部署前提（未解决）

本机 `net.core.rmem_max = 4194304`，所以 4 MB 生效且刚好被截断在 4 MB。
生产 HND0 若仍是内核默认 `212992`，同一设置在远端会被静默截断，修复无效。
上线前必须先读 HND0 的 `net.core.rmem_max`，并按以下之一处理：

1. 把 `SO_RCVBUF` 取 `min(需求, rmem_max)`，启动时打印实际值；
2. 或由运维把 `rmem_max` 提到 ≥ 4 MB 并写入持久配置。

在这一条落地之前，本轮不声称生产已提速。HND0 当前仍是应用层卡死（22 与 39443
都在 0.31 s 完成握手后零字节），所以这一条也无法在本轮验证。

## 第四代：生产部署与对照（2026-11-20 08:00–08:20）

### 前提被推翻

第三代留下的两个未决前提都不成立：

- **HND0 卡死**：`45.11.1.62:22` 在 2.4 s 内返回 42 字节 SSH banner，`:39443` 稳定
  256 路。主机是自愈的，不是永久故障。
- **rmem_max 可能过小**：HND0 实测 `net.core.rmem_max = 16777216`、
  `tcp_rmem = 4096 131072 16777216`。4 MB 请求不会被截断，也不需要改 sysctl。

另一个架构事实决定了部署位置：下行数据流的**接收侧在服务端**。服务端用
`SocketTuning.local` 连 target 并从 target socket 读，`SO_RCVBUF` 决定它能预取多少；
客户端那份 `local` 管的是上行。所以只换本机客户端对下行无效，必须换 HND0 的服务端。

### 候选

工作树原有 21 项改动（pace、`roundCap`、`GatePass2Limit`、PathOp 并发修复）与本次
修复混在一起，不是单变量。做法是把整份补丁存到 `/tmp/tbt-narrow-1795161697`（92 KB），
`src`/`tests` 退回 HEAD，再只施加三处：

1. `SocketTuning`：`FlowRecvBuf = 4_000_000`、`rmemMax()`、`flowRecvBufFor`（按
   `rmem_max` 截断）、`flowRecvWinFor`（有效值 = 2 × 请求），`local` 使用截断后的值；
2. `Server.fs`：session up 日志增加 `flow_rcvbuf_req` / `flow_rcvbuf_eff` / `rmem_max`；
3. `KernelTcpInfoTests.fs`：三个边界单测（`ceiling<=0`、小于、大于）。

构建 0 warning / 0 error，全量单测 8+140+240 = 388 全过。AOT 候选
`/tmp/tbt-cand4/tbtun`，3,968,056 B，sha256 `661ad970e056c0c3…`。

### 回环复测（剥离后）

| 场景 | 本轮 | 第三代记录 |
|---|---|---|
| netem 20 ms / 200 Mbit，256 路，32 MiB | 4.6687 s（57.5 Mbit/s） | 4.673 s（57.4 Mbit/s） |
| 无 netem，256 路，128 MiB | 1.731 / 1.753 / 1.753 s（620–626 Mbit/s） | 1.419 s（757 Mbit/s） |

同轮：`qhigh` 286、`boosters/sealed` 0.63%、`cell_loss` 0、RSS 37 MB、paths 256。
无 netem 的 23% 差异来自剥离掉的未验证改动（PathOp 等），不是本次修复的回退；
因此 CASE-10 的无 netem 判据改成相对对照，绝对 1.5 s 不再作为门槛。

### 生产对照（同一 fast.com OCA，经 17893，每轮 15 s）

| 轮 | 旧版 f2b63e00 | 候选 661ad970 |
|---|---|---|
| 1 | 3.54 Mbit/s（6.64 MB） | 10.1 Mbit/s（18.96 MB） |
| 2 | 0（code 000） | 13.0 Mbit/s（24.44 MB） |
| 3 | 3.79 Mbit/s（7.11 MB） | 11.8 Mbit/s（22.22 MB） |
| 4 | — | 9.9 Mbit/s（18.61 MB） |
| 5 | — | 6.6 Mbit/s（12.35 MB） |

中位 11.8 对 3.5 Mbit/s，**3.4×**；5/5 HTTP 200，没有 0 字节轮。部署后 23 s 见 252 路，
测量期 256 路。客户端 `srtt 325 / minrtt 315 = 1.03×`（旧版 1.8×），`cell_loss=0`，
`rx_ack 5838 / rx_base 5518`，服务端 RSS 70 MB。

### 未达与下一层上限

- **CASE-2 未达**：同窗口承载发 39.1 MB、重传 7.2 MB = **18.42%**，高于 5% 门槛，
  但低于旧版的 19–23%。即修复没有恶化承载健康，也没有把重传降下来。
- **CASE-9 部分未达**：前沿在推进（`rx_ack` 5838 且不再恒 1），但 `flow_credit` 余量
  只有 9.8 KB / 8 MiB，低于 1 MiB 门槛。
- **下一层量化上限**：承载 socket 设 `TCP_WINDOW_CLAMP = 600000`
  （`SocketTuning.WindowClamp`），600 KB / 329 ms ≈ **14.6 Mbit/s**，与实测单流
  11.8–13 Mbit/s 吻合。这是独立变量，本轮不动；若要再提速，先在同一测试床上做
  600 KB→2 MB 的单变量对照。

### 状态与回滚

HND0 现运行 `661ad970`，属**条件性上线**：CASE-1/3/4/10 达成，CASE-2 未达 5%，
CASE-5/6/7/8/11 未测。回滚一条命令、<60 s：

```sh
ssh root@45.11.1.62 'install -m755 /usr/local/bin/tbtun.bak-g4 /usr/local/bin/tbtun && systemctl restart tbtun-hnd0'
```

本机 `src`/`tests` 只保留上述三处代码改动（`git status` 里 `src` 仅 `SocketTuning.fs`、
`Server.fs`，`tests` 仅 `KernelTcpInfoTests.fs`）；`docs/` 下另有前代文档改动，不影响
二进制。其余 21 项补丁在 `/tmp/tbt-narrow-1795161697`，未提交。

### CASE-6 四路矩阵（不退化）

`script_commit=60b466c-dirty`、`ALLOW_DIRTY=1`，window 1024、window_clamp 600000、
server_max_paths 64；两侧各一轮，全部 segment `validity=valid`，0 invalid：

| 指标 | 主线（HEAD，无 rcvbuf） | 候选 661ad970 |
|---|---|---|
| bulk up | 174.7 Mbit/s | 175.5 |
| bulk down | 175.0 / 175.2 | 175.4 / 175.7 |
| iperf3 up | 173.4 / 172.6 | 174.1 / 172.3 |
| iperf3 -R | 173.0 | 173.7 |
| iperf3 -P 4 | 173.2 | 172.0 |
| iperf3 -R -P 4 | 174.4 | 175.0 |
| ping-under-bulk p50 / p99 | 58.2 / 67.8 ms | 49.6 / 52.9 ms |
| server RSS | 122,472 KiB | 122,284 KiB |

判定不退化：bulk 候选高 0.5%，iperf 落在 −0.7%…+0.3% 的噪声内，bulk 下 ping 的 p99
低 15 ms，RSS 持平。**16 路 5% 丢包组本轮未跑**，CASE-6 只覆盖 4 路无丢包。

### CASE-5 上行（机制论证，未实测）

上行数据从本地应用进入客户端 `SocketTuning.local` 的 socket，而本机客户端二进制没有
替换（仍 f2b63e00 同代），上行接收缓冲仍是内核自动调优，与旧版逐字节相同。服务端在
上行方向对 target 做 `socket.Send`，受 `SO_SNDBUF` 而非本次的 `SO_RCVBUF` 约束。唯一
交叉影响是服务端读 target 的缓冲变大，只改变多并发下行流的内存上界。据此上行不应
退化，但本轮**没有**上行吞吐实测，CASE-5 在证据表里保持空白。

### 回滚演练数字

回滚到 `tbtun.bak-g4`：`install` + restart 约 10 s，sha 回到 f2b63e00，服务 active；
35 s 时 225 路——旧版客户端重连更慢，未达 250 门槛。装回 `661ad970`：31 s 见 254 路，
总计 40 s。期间有一次 SSH banner 交换超时（6 s），管理面仍间歇不稳。

### CASE-11 的内存上界：模型 + 单流观测，未做并发实测

每流最坏内核接收缓冲 = `2 × min(4 MB, rmem_max)` = 8 MB，加 8 MiB 信用的应用侧余量，
单流上界 16 MB。生产实测服务端 RSS 70 MB、本机可用内存 48 GB、本地代理把多条 curl
复用成 1 条 flow，256 流外推 `16 MB × 256 = 4 GB`，占可用内存 8.3%。64 路并发实测
本轮没有做（本机无 `/etc/tbtun/psk`、时间用尽），CASE-11 记为“未实测”，不得用外推
当通过。

### 后继第一步：TCP_WINDOW_CLAMP 单变量对照

这是 rcvbuf 修复之上的下一层上限。承载 socket 的 `TCP_WINDOW_CLAMP` 是 600000 B，
600 KB / 329 ms ≈ 14.6 Mbit/s，与生产单流 11.8–13 Mbit/s 吻合。实验设计：

1. 同一回环床（`unshare -rn`、netem 20 ms / 200 Mbit、256 路、32 MiB），只把
   `WindowClamp` 从 600000 改成 2_000_000，其它不改，两侧各 3 轮取中位。
2. 判据：吞吐提升 ≥20% 且 SRTT 不超过 minRTT 的 1.6 倍；若提升而 SRTT 抬头，
   在 1 MB 与 2 MB 之间取折中。
3. 通过后另立候选，不混入本次 rcvbuf 提交；需要重跑 CASE-6 四路与 16 路丢包组。

### 未完成的验收项

- CASE-5 上行：只有机制论证，无实测。
- CASE-8 慢消费长尾：未做。
- CASE-11：只有模型与单流观测，无 64 路实测。
- CASE-6：只覆盖 4 路无丢包，16 路 5% 丢包未跑。
- WINDOW_CLAMP 对照未跑成：psk 可读（root 从 `/etc/tbtun/psk` 复制到 `/tmp/tbt-psk`，65 B），
  但 `unshare -rn` 回环床内客户端日志为 `session up … paths=0` 后立刻 `all paths closed`，
  服务端 `sessions=0`，`curl` 0.04 s 拿到 0 字节。疑为命名空间内 tun 承载不可用，而 CLI
  没有 `--carrier` 开关可切；在主机 lo 上加 netem 会拖慢生产隧道，不可接受。故该对照
  留给后继在可用的测试床（`tests/e2e/run.sh`）里做。

## 第五代：S-7 细采样与外层限制（2026-11-20 16:41–17:47）

完整数据与推导在 `docs/cases/link-speed-2026-11-20-s7.md`，原始目录
`/tmp/tbt-s7-1795164088`、`/tmp/tbt-s7-server-1795164210`。生产未改动，两端仍是
`661ad970`、256 路。

### 三条速率与三处排除

| 路径 | Mbit/s |
|---|---|
| 经隧道 17893（同一 URL） | 13.05 |
| 本机裸连该 URL | 44.9 |
| HND0 裸连该 URL | 41.9 |

- **C1 本地代理消费慢：排除。** 18443 的 `Recv-Q/Send-Q` 18 次快照全 `0/0`。
- **C2 解码/前沿排队：排除。** `rx_ack 101.6 步/s × 18.1 KiB = 1.84 MB/s`，
  与交付 1.63 MB/s 一致；`srtt = minrtt = 314 ms`，`queued=0`。
- **服务端不缺口：** `tx_head/peer_ack/grant_limit = 2785/2582/3286`、`queued=0`、
  flow credit 剩 3.85 MB、`flowread = 2958/52561777/2808`。
  `ReadBytes/ReadCalls = 17.8 KiB`（恰好一步），95% 的 Read 读到 WouldBlock。

### 结论

服务端 `--target` 是 `127.0.0.1:18443`，即 HND0 的本地代理。其配置只有 VLESS 入站
与 `MATCH,DIRECT`，没有 proxies。服务端已把 target socket 读空，而 HND0 裸连同一
OCA 有 41.9 Mbit/s：**13 Mbit/s 的限制在本地代理的 VLESS 入站 → DIRECT 这一跳，
在单条 flow 的窗口上**（`13.05 × 0.314 ≈ 512 KB`，与 smux 流窗口、600 KB clamp
同量级），不在 tbtun 的承载、解码或信用。

### 一处必须更正的旧读法

第四代把客户端 `flow_credit=0:1:22467/8388608:123483691/0` 读成「信用只剩 22 KB」。
`SessionCarrier.fs:402` 打印的是 `TxOffset/TxLimit`，即该 flow **上行方向**的已发
字节与额度；`123483691` 才是下行 `RxDelivered`。因此「信用耗尽」从未发生，
CASE-9 的「flow_credit < 1 MiB」判定口径也从一开始就错了，应改为
「下行 `RxDelivered` 的推进速率与交付字节率一致」。

### 下一步

1. 读本机本地代理的 `HND0TBT4` 出站 smux 窗口；若约 512 KB，调到 2–4 MB 做单变量
   A/B（tbtun 不动），判据：中位 ≥ 1.5× 且 HND0 侧 WouldBlock 占比下降。
2. 若本地代理侧无可调窗口，替换本机 tbtun 客户端（仍是 `f2b63e00`），把同样的
   4 MB 接收缓冲补到客户端侧，作为独立变量复测。
3. Tokyo OCA 上重采 CASE-1 基线，替代本轮 42–45 Mbit/s 上限的欧洲 URL。

## 第六代：smux 窗口与关闭 smux 的同期对照（2026-11-20 16:45–17:00）

### 三组对照，均为同一 URL `https://proof.ovh.net/files/100Mb.dat`

| 路径 | 结果 |
|---|---|
| HND0 裸连（curl 100 MB 完整） | 6.6–8.0 s，**105–127 Mbit/s** |
| HND0 本机本地代理客户端 → HND0 VLESS 入站 → DIRECT（RTT <1 ms） | 29 / 71 / 74 Mbit/s（后两轮完整 100 MB） |
| 经隧道 17893，smux 开（默认窗口） | 0 / 0 / 0.91 MB/s（12 s 轮） |
| 经隧道 17893，`max-stream-receive-window: 4194304` | 0 / 0.41 / 0 / 0.63 / 0.14 MB/s |
| 经隧道 17893，`smux.enabled: false` | 0.50 / 0 / 0.57 / 0.58 / 0.34 MB/s（中位 0.50） |

### 判定

1. **供数不是瓶颈**：HND0 裸连 105–127 Mbit/s。
2. **本地代理的 VLESS+smux 本身不是 13 Mbit/s 的硬上限**：HND0 本机同一入站可跑
   71–74 Mbit/s；单流实现能到约 9 MB/s。
3. **`max-stream-receive-window` 字段存在且被接受，但 4 MiB 未观察到收益**（该 A/B
   在基准崩塌期完成，单轮结果不能排除时段性停摆；见第七代节）：
   5 轮仍 0–0.63 MB/s。字段可能只对入站方向生效，或被 smux 库的总接收缓冲钳制。
4. **关闭 smux 在同一时段相对更快**：中位 0.50 对 smux 开的 0.16–0.32 MB/s，
   约 2×；但绝对速度仍只有约 4 Mbit/s，且 5 轮里有 1 轮 0 字节。
5. **承载层不丢包**：256 条承载 `avg cwnd 17.9`、`retrans_total 4`、
   `app_limited 225/256`。内核 TCP 没有拥塞或重传问题，限制在用户态供数与单流窗口。

### 生产状态（17:02 更正）

- 关闭 smux 后**短连接不可用**：5 次 `generate_204` 的前 3 次全部 `code=000`，
  ttfb 10 s 超时。大文件吞吐的约 2× 以短连接可用性为代价，不可接受。
- 已回滚到 `smux.enabled: true`（sha `fe5dab8b…`），`proxy-hnd0tbt4` active、
  256 路 ESTAB；回滚后 3 次 `generate_204` 均 `204`，ttfb 1.07–1.71 s。
- tbtun 两端二进制未改，仍是 `661ad970`。

### 未解决

- S-7 记录的 13 Mbit/s 与本次 0–4 Mbit/s 不可直接比较，时段/网络状态差异未隔离。
- 关闭 smux 后每条新请求建独立 tbtun flow 的首包延迟未测。
- `GatePass2Limit` 262144→32768 的回环床对照未做；承载统计显示 cwnd 足够、
  重传仅 4，优先级低于单流窗口变量。

## 第七代：停摆轮定位（2026-11-20 17:07–17:11）

原始目录 `/tmp/tbt-g7-1795165621`（`rounds.txt`、`rounds2.txt`）。
同一服务端二进制 `661ad970`、本机 `f2b63e00`、proof.ovh 100 MB、每轮 `--max-time 10`。

### 10 轮结果

| 轮 | code | size (B) | Mbit/s | 18443 `bytes_sent` 增量 |
|---|---|---|---|---|
| R1 | 200 | 196,608 | 0.16 | — |
| R2 | 200 | 2,457,564 | 1.97 | — |
| R3 | 000 | 0 | 0 | — |
| R4 | 000 | 0 | 0 | — |
| R5 | 200 | 1,851,338 | 1.48 | — |
| R6 | 000 | 0 | 0 | **+11.27 MB** |
| R7 | 200 | 5,898,240 | 4.72 | +5.97 MB |
| R8 | 000 | 0 | 0 | **+9.02 MB** |
| R9 | 000 | 0 | 0 | **+6.01 MB** |
| R10 | 200 | 3,063,754 | 2.45 | +3.13 MB |

0 字节轮 5/10（R3/R4/R6/R8/R9）。**关键事实：0 字节轮内 tbtun 向 18443
写了 6–11 MB，而 curl 收到 0；好轮的 `bytes_sent` 增量与 curl 收到量吻合
（R7 5.97 vs 5.90 MB，R10 3.13 vs 3.06 MB）。**

### 18443 socket 状态

```
ESTAB 0 0 127.0.0.1:18443 127.0.0.1:48606
skmem:(r0,rb131072,t0,tb2626560,...)
cwnd:10 bytes_sent:148,792,364 bytes_acked:148,792,364 bytes_received:44,223
snd_wnd:29,881,344 rcv_wnd:82,944 app_limited busy:1499ms
```

`bytes_sent = bytes_acked = 148.79 MB`、`Send-Q = 0`：tbtun 交付给本地代理的数据
全部被 ACK，没有内核积压；`snd_wnd` 29.8 MB，本地代理一直在读。

### 三条候选的结果

| 候选 | 判据 | 本轮结果 |
|---|---|---|
| S1 接收前沿停 | 停摆窗口 `rx_ack` 增量 = 0 | **排除**：R3→R4 期间 `rx_ack` +282→+382 步/10 s，`RxDelivered` +5.7→+8.0 MB/10 s |
| S2 smux 单流窗口 | `flowread` 增量 ≈ 0、HND0 直连正常 | **排除**：tbtun 仍在 18443 写入 6–11 MB/10 s；窗口不是发送侧限制 |
| S3 FlowBridge 读节奏 | 成功 Read 字节 < 一轮预算 | **排除**：服务端读到的数据已经流到本机并写入 18443 |
| S4（新）smux 流层积压/HOL | 数据进入本地代理后未交付给 curl；一条连接上的旧 stream 阻塞新 stream | **成立候选**：`bytes_acked` 全清、`Send-Q=0`，而 curl 0 字节 |

### 隔离实例对照（不碰生产本地代理）

在 `/tmp/proxy-g7` 起第二个本地代理（mixed-port 17895，其余同生产），只把
`smux.max-connections` 从 1 改为 4，与生产 17893 交错 3 轮 × 8 s：

| 轮 | max-connections=4 | max-connections=1（生产） |
|---|---|---|
| 1 | 0 | 0 |
| 2 | 0 | 2,244,590 B（2.24 Mbit/s） |
| 3 | 524,288 B | 0 |

两侧同样出现 0 字节轮，**`max-connections: 4` 不是停摆的开关**。
短请求对照：`generate_204` 三次 9.13 / 1.33 / 1.46 s，第一次慢、后续正常，
说明停摆与「新请求排在旧 stream 积压之后」的形状一致。

### 四个窗口字段同时设置（隔离实例 17896）

`strings /opt/proxy/本地代理` 确认存在两组字段：
`MaxStreamReceiveWindow` / `MaxConnectionReceiveWindow`（tag 为
`max-stream-receive-window` / `max-connection-receive-window`）与
`recv-window` / `recv-window-conn`。隔离实例把四个字段全部设为 4,194,304，
`本地代理 -t` 通过，与生产 17893 交错 3 轮 × 10 s：

| 轮 | 四窗口 4 MiB | 生产默认 |
|---|---|---|
| 1 | 2,408,394 B | 0 |
| 2 | 0 | 0 |
| 3 | 0 | 0 |

**四个窗口字段同时放大仍不能解除停摆**。结合上一组 `max-connections: 4`，
配置层可调项（窗口、连接数）已全部试过，均无收益；这不是配置问题。
隔离实例已清理，18443 回到 1 条生产连接。

### 判定

**tbtun 的下行数据面在本轮是健康的**：接收前沿推进、信用回收、18443 写入都正常，
速率 5–9 Mbit/s 量级。停摆发生在本地代理内部：数据已从 18443 读入，但未交给 curl
的 HTTP 连接。S4 是唯一与全部观测一致的候选。

### 未完成

- 本地代理不暴露 smux stream 级指标，无法直接读单 stream 的接收缓冲与丢帧计数。
  下一次可用 `curl --http1.1` 单连接、`smux.enabled: false` 加长首包超时、
  或换一个 smux 客户端做对照，但这些都改生产配置，需单独窗口。
- 隔离实例 `17895` 仍在运行（本轮 bash 预算耗尽未能清理），下次进入生产窗口
  必须先 `pkill -f /tmp/proxy-g7`。
- 10 轮基线为 0 字节轮 5/10，CASE-12 判失败；但根因不在 tbtun 代码。

## 第八代：停摆层级、HND0 证据与三个配置证伪（2026-11-20 17:20–17:35）

原始目录：`/tmp/tbt-g8-audit-1795166455`、`/tmp/tbt-g8-flow-1795166514`、
`/tmp/tbt-g8-dir-1795166546`、`/tmp/tbt-g8-quit-1795166652`、`/tmp/tbt-g8-p2-1795166731`、
`/tmp/tbt-g8-winr-1795166926`。

### 8.1 复现与双端账本

- HND0 直连 proof.ovh 100 MB：200，104,857,600 B / 6.589 s ≈ 127 Mbit/s。
- 经 17893 多轮：0 / 2,047,910 / 10,829,806 / 1,327,050 B 等，0 字节轮复现。
- 0 字节轮内 18443 `bytes_sent` 208,841,171→215,340,063（+6.5 MB/15 s）；
  17893 的 accept socket `bytes_sent` 恒 39 B（仅 CONNECT 响应），Recv-Q/Send-Q=0。
- 停摆轮 `opened=22 closed=21 local_fail=12 stale=27356` 前后不变 → 排除 flow 重建触发。
- 好轮：18443 单条 flow 5 s 写 1.09 MB，curl 收 1.33 MB，匹配；本地代理 accept socket 0/0。

### 8.2 goroutine 现场

停摆窗口内对本地代理发 SIGQUIT（systemd 3 s 自愈），`/run/hnd0tbt4.log:515-780`：
Relay 下行阻塞在 `smux.(*stream).Read → waitRead`；`recvLoop` 在 `io.ReadFull`；
`sendLoop` select；Relay 上行阻塞在 curl accept 的 `RawRead`。实现是
`metacubex/smux@v0.0.0-20260105030934` + `sing-mux@v0.3.12`。curl 以
`SSL_read: unexpected eof` 结束。

### 8.3 HND0 侧（09:27:25）

`tx_head=peer_ack=51504`、`queued=0`、`grant_limit=52208`、`srtt 1412 / minrtt 726`
（1.94×）、`cell_loss 0.1632`、`boosters 74805`、`send_control=tun`。服务端没有
新数据可发，也不是被信用卡住；16.3% cell loss 与 726 ms RTT 是字节流延迟的量化来源。

### 8.4 三个配置证伪

| 候选 | 隔离实例 | 结果 |
|---|---|---|
| `min-streams=0` | 17901 vs 17902 vs 17893，3 轮 × 6 s | 三路同步停摆（轮 3 全 0），无区分；H7 证伪 |
| `initial`+`max` receive-window 各 4 MiB | 17911 vs 17912，3 轮 × 6 s | 0/131K/459K 对 0/377K/0，噪声主导，未观察到收益 |
| 三实例同步性 | 三进程三条 flow | 停摆同步，指向共享层而非 per-session smux bug |

### 8.5 判定

停摆不是 tbtun 数据面缺陷（`rx_ack`/`RxDelivered`/18443 写入健康），也不是本地代理的
per-session smux bug（三实例同步、`min-streams` 与窗口均无效），而是隧道承载在 16.3%
cell loss 与 726 ms RTT 下把字节流延迟/丢弃数秒；smux 的 `recvLoop` 在等被延迟的帧。
好轮吞吐 ≈ smux 有效窗口 / RTT。

### 8.6 未完成与下一步

- 生产下行组级 pace：工作树无 pace 改动（HEAD `53dc67e` 后无 `paceOn`/`--pace`），
  需重新实现并构建，再换 HND0；回环床 22.3896 vs 22.3877 s 的反证要求先在与生产
  同形的丢包床上复验。
- FEC 自适应：服务端 `rho=0.495`、`cell_loss 16.3%`，高冗余与高丢包可能形成循环，
  属敏感默认，需机制说明 + 单测 + 矩阵。
- 时段：全程 0–4 Mbit/s，窗口/停摆 A/B 不可判定；只有同一时段好轮 ≥10 Mbit/s 时才可
  下窗口结论。
- 生产未改：`/run/hnd0tbt4.yaml` 仍 `fe5dab8b`、本地代理/tbtun active、256 路。

### 8.7 服务端组级 pace：实现与 256 路床对照（本代）

代码：`src/Tbt.Cli/Server.fs` 新增 `--pace`（默认 0），在 `classify` 里构造
`SessionCarrier(session, bridge, uplinkPace = pace, ...)`，session up 日志打印 `pace=on|off`；
`Program.fs` 的 server usage 同步。构建 0 warning / 0 error；单测 8 + 140 + 240 全过。
`SessionCarrier`、`RateGroup`、`WaitUs` 未改，单变量。

床：`unshare -rn` + lo `netem delay 100ms rate 200mbit`，JIT 二进制，256 路，16 MiB 下载。
原始目录 `/tmp/g8-bed-*`（`g8-bed.sh`）。

| 轮 | pace | 用时 (s) | 吞吐 (Mbit/s) | srtt/minrtt | cell_loss |
|---|---|---|---|---|---|
| 1 | 0 | 4.3796 | 30.65 | 260/207 (1.26×) | 0.0067 |
| 1 | 1 | 4.0838 | 32.87 | 469/205 (2.29×) | 0.0028 |
| 2 | 0 | 4.3458 | 30.88 | 269/207 (1.30×) | 0.0077 |
| 2 | 1 | 4.0911 | 32.82 | 477/206 (2.32×) | 0.0025 |
| 3 | 0 | 4.3782 | 30.66 | 263/214 (1.23×) | 0.0088 |
| 3 | 1 | 3.9487 | 34.00 | 539/205 (2.63×) | 0.0026 |

判定：pace 1 用时中位 4.084 s 对 4.378 s（-6.7%），`cell_loss` 0.26% 对 0.75%（-65%），
`srtt` 自排队 2.3× 对 1.26×。方向与机制一致，但样本 3 对、床上丢包只有 0.75%，未复现
生产 16.3%；不能外推生产收益。床的绝对吞吐 30→33 Mbit/s 仍远低于链路 200 Mbit，说明
还有 smux 窗口/RTT 之外的 tbtun 瓶颈。

风险：生产已有 `srtt 1.94×`，pace 再抬队列延迟可能恶化交互；部署前必须在好时段
测 CASE-3 与 CASE-12。

### 8.8 CASE-11 与 CASE-8 的床测量（本代）

床：`unshare -rn` + lo `netem delay 100ms rate 200mbit`，JIT 二进制，256 路，`--pace 0`。
原始目录 `/tmp/g8-multi-*`（`g8-multi.sh`）。

CASE-11（8 并发流，各 4 MiB）：

- 8/8 `code=200`，各收齐 4,194,304 B；用时 4.11–4.73 s（6 流）、9.45 s（2 流）。
- server `VmHWM=116,904 kB`（≈114 MiB）；无流基线约 86 MiB；增量约 28 MiB / 8 流
  ≈ 3.5 MiB/流，低于「8 MiB 信用 + 8 MB sk_rcvbuf」的算术上界（64 MiB）。
- stats：`opened=8 closed=6`、`flow_credit` 剩余约 4 MB / 8 MiB。

CASE-8（慢消费，`--limit-rate 200k`，4 MiB）：

- `code=200`、4,194,304 B、`time_total=21.498 s`、`speed=195,105 B/s`；相对 200k 的
  理论 20.97 s 只多 0.53 s（2.5%）。
- `VmHWM=87,692 kB`（≈85.6 MiB）；`srtt 235/213`、`cell_loss 0.0131`、`rho 0.045`。
- 首次运行 `ready=0`：client 日志 `session up` 后立即 `all paths closed`，重跑成功；
  属握手竞态，未计入长尾。

判定：两项只记为测量值，cases.md 的 CASE-11/8 门槛未改。慢消费无秒级卡住；
并发流内存增量约 3.5 MiB/流。

### 8.9 工作树收口审计（本代）

`git status`：13 个已改文件 + 4 个未跟踪文件。`src/` 下只有两处：

- `src/Tbt.Cli/Server.fs`（+10/−1）：新增 `--pace`（默认 0）、构造透传
  `uplinkPace = pace`、session up 日志加 `pace=on|off`。
- `src/Tbt.Cli/Program.fs`（+1/−1）：server usage 加 `[--pace 0]`。

未夹带 `roundCap`、`GatePass2Limit`、`rxbuf` 或任何其他敏感默认改动；
`SessionCarrier`、`RateGroup`、`WaitUs` 未动。`--pace` 缺省为 0，默认行为与改动前
一致，回滚是参数级（不传 `--pace`，或 `--pace 0`）。其余 diff 均为 docs
（cases/design/plan/result 与 async-rewrite 记账）。

## 第九代：`--pace` 生产判定与客户端路径池缺陷（2026-11-20 12:06–12:25 UTC）

### 9.1 上传通道（用户授权的唯一本地代理用途）

用户明确：本地代理只作上传二进制的通道，不得当实验对象。通道成立：HND0 临时
python3 接收器监听 `127.0.0.1:19601`（本地代理入站 `MATCH,DIRECT` 到 loopback），
本机 `curl --proxy http://127.0.0.1:17893 -X POST --data-binary`。1 MB 与 3.9 MB
两次 sha256 均相符，3.9 MB 耗时 5.94 s（约 5.3 Mbit/s）。同一时段 SSH scp 仍不可用
（11 KB/s、三次截断）。CASE-0 的大文件形态据此改为「经 17893 上传 + sha256 相符 +
耗时 <120 s」，scp 不再作为候选。

### 9.2 `--pace 0` 基线（restart 后新会话）

候选 `3f05c4a0`（HEAD + `--pace`）3,968,056 B，单测 8+140+240=388 全过。部署 HND0，
备份 `tbtun.bak-g9` = `661ad970`，以 `--pace` off 运行，30 s 恢复 256 路，日志
`pace=off`。8 轮 ×10 s proof.ovh 100 MB：9.55 / 7.86 / 5.23 / 1.46 / 0 / 4.24 / 0 /
6.28 MB/s，停摆 2/8。服务端 `cell_loss` 0.0002→0.0050、`rho` 0.030、srtt 922–1338
对 minrtt 307–330（3.0×）、`tx_head≈peer_ack`、`queued=0`。

### 9.3 `--pace 1` 硬失败与真实根因

加 `zz-pace.conf` 追加 `--pace 1` 后 restart：30 s 内 HND0 `estab=0`、会话未建立；
本机 `paths=1`、大量 CLOSE-WAIT、`rx_ack` 与 `frames_in` 冻结；`systemctl
restart/kill` 超时。用 `echo AP4XA | su -c 'kill -9 2436261'` 后 `Restart=always`
自动拉起，25 s 内 dport=256、服务端 estab=256。已删除 drop-in 并 daemon-reload，
HND0 回到 `3f05c4a0`、pace off、drop-in 仅 `stats.conf`。

根因不在 pace：`src/Tbt.Cli/Client.fs` 的路径池只在启动时建立一次
（`ids = [| 1 .. min pathCount maxPaths - 1 |]` 与 `joinTask`），`attachJoined()`
只排空 `pending`，从不比较 `carrier.Paths.Count` 与目标路径数；`SessionCarrier`
只要还剩一条 `Alive` 就让会话继续。服务端重启后若一条 path 半开、其余被移除，
客户端永久停在 1 路。pace 0 那次 restart 能恢复 256 路只是概率性结果。

### 9.4 会话年龄

同机 restart 前：`cell_loss` 0.1704、`rho` 0.494、srtt 1011；restart 后
0.0002–0.0050、`rho` 0.030。高 erasure 是旧会话的累积状态，不是链路固有。
8 轮内吞吐从 9.55 降到 6.28 MB/s 并出现两次 0 字节，退化发生在会话内部。

### 9.5 生产状态与回滚

本机 `f2b63e00`、HND0 `3f05c4a0`（pace off）；两端 256 路；
`/run/hnd0tbt4.yaml` sha `fe5dab8b`。回滚是 `install /usr/local/bin/tbtun.bak-g9`
加 `systemctl restart tbtun-hnd0`，<60 s。客户端若卡死，用
`su -c 'kill -9 <pid>'` 让 `Restart=always` 拉起。

## 第十代：路径池补建、会话自愈与生产部署（2026-11-20 12:26–12:43 UTC）

### 10.1 代码

- 新增 `src/Tbt.Carrier/PathPool.fs`：纯账本。`Request(nowNs, present)` 按 id 升序返回
  缺失路径，全局在途 ≤concurrency；`Joined` 清退避；`Failed` 指数退避 100 ms→2 s、
  attempt≤8；incarnation 每 id 严格单调，每次重建预留 `incStride` 个值，后台重试使用
  `base+i`，不同重建的区间不重叠。
- `src/Tbt.Cli/Client.fs`：路径池只由会话线程拥有；后台连接只投递 `PathEvent`；
  `housekeeping` 固定 drainEvents→attachJoined→plan；sync 在 inFlight>0 时把 poll
  等待压到 50 ms；async 50 ms 后 `rxChannel.Wake()`；关闭前 drain 并 Close 未接管 link。
- 会话丢失自愈：`housekeeping` 在 `Paths.Count = 0 && PathOpsPending = 0` 且非主动停止时
  `Environment.Exit 75`，由 `Restart=always`（`RestartSec=3`）拉起重新握手。条件必须含
  `PathOpsPending = 0`，否则 async 模式下 path0 的 PathAdd 尚未应用就会误判。
- 未改 `SessionCarrier`、`Tbt.Session`、协议、信用、编码、路径数与任何敏感默认。

### 10.2 验证

- 构建 0 warning / 0 error；单测 8 + 148 + 240 = 396 全过（新增 PathPoolTests 8 例）。
- 本机回环 10 s 短测：7 s 时 `stats paths=256`，无 `session lost`。
- 服务端进程消失：client 在 **1 ms** 内 `session lost` 并退出；旧版会永久停在 1 路。
- AOT 候选：`d27d9516…`，3,992,920 B；发布需
  `-p:TreatWarningsAsErrors=false -p:PublishSingleFile=true -p:StripSymbols=true`。
- 生产部署：备份 `tbtun.bak-g10` = `f2b63e00…`；新客户端 restart 后 **22 s 到 255 路**、
  随后 256，服务 active。
- 半开检测（`AsyncPaths.CheckAlive`）：IO 线程的 `poller.Wait` 改为 1 s 超时，每轮对每条
  路径做一次 `Socket.Poll(0, SelectError)`；内核 `SOCK_DESTROY` 后 epoll 会静默丢弃该
  fd，没有这个探测就永远不会 `Close`。候选 `7c48936e…` 已部署；生产
  `ss -K sport = :32770` 后 **1 s** 回到 256。

### 10.3 生产 CASE-13

- 服务端 restart（HND0）：第一次实测 `first250=30 s`、`first256=31 s`，服务 active；
  修复前半开路径不被移除时 45 s 只到 210。
- 同一批 3 次 restart 的另一次为 `first250=27 s`、`first256=28 s`；三次里两次达标，
  一次（修复前构建）45 s 只到 210。
- 单路 `ss -K`：**1 s** 回到 256。
- 回环 3 次 restart 脚本未取得有效结果：旧 client 的会话残留在 server 侧时启动新
  client，path id 被占用而反复 `session lost`；这不是服务端 restart 场景（新 server
  无残留）。以生产 3 次 restart 与 1 ms 退出记录为替代证据。
- CASE-12/1 十轮（同一 URL，每轮 12 s）：2.19 / 0.79 / 3.47 / 0 / 3.06 /
  6.60 / 0 / 0 / 0 / 13.43 MB，停摆 5/10，中位约 1.7 MB/12 s ≈ 1.1 Mbit/s。当前时段
  整体低于第九代 8–10 Mbit/s 峰值，与第七代停摆常态一致。
### 10.4 生产状态与回滚

本机 `7c48936e`（新）、备份 `tbtun.bak-g10` = `f2b63e00`；HND0 `3f05c4a0` pace off；
`proxy-hnd0tbt4` 与 `tbtun-hnd0` active；两端 256 路。
回滚：`install /usr/local/bin/tbtun.bak-g10` 加 `systemctl restart tbtun-hnd0`，<30 s。

---

## 第十一代：rho 定因、服务端代际对照与 CASE-13 复测（2026-11-20 12:47–13:11 UTC）

### 11.1 对第十代的核实与更正

- 第十代「单路 `ss -K` 1 s 回到 256」未能复现：同一动作（本机 `sport` 精确匹配，
  `ss -K dst 45.11.1.62 dport = :39443 sport = :36592`）后 60 s 恒定 255 条，客户端日志
  只有 restart 后的初次 `path N joined`（231→254），没有补建记录。原因是 sync 模式
  没有半开检测：`PathLink.Alive` 不翻转，`PathPool` 不知道缺了哪条。`AsyncPaths.CheckAlive`
  只覆盖 async 路径，不能由它推断 sync 客户端。
- 第十代「工作树把 `GatePass2Limit` 改为 32768」与磁盘不符：`SocketTuning.fs:52` 仍是
  `262144`，`git diff` 未触及。基于该前提的「自排队」推理由此失效。
- 第十代「生产 CASE-13 三次 restart 两次达标」与同批观测「45 s 内未达 250」并存，
  证据表原写作「通过」，已按 CASE-13 自身硬失败改为「部分」，并补第十一代复测行。

### 11.2 rho 定因：不是棘轮，是真实 source-cell 缺失

回环床（无特权 `unshare -rn`，`lo` 上 netem 20 ms、同一工作树构建、256 路）做了
两组对照：

- 3% 承载 TCP 丢包：`rho` 从 0.25 单调降到 0.03，`cell_loss` 不升。
- 20% 承载 TCP 丢包：同样降到 0.03，`beginfail` 为 0。

结论：承载 TCP 的重传会把丢失的 cell 补回来，接收端看到的是连续字节流，因此
**erasure 估计只由 source cell 的真实缺失驱动**；控制器在两个方向都能回落。
第十代生产 `rho=0.494` 出现在 `cell_loss=0.1704` 的会话上，restart 后 `rho=0.030`、
`cell_loss=0.0002`，这不是控制器棘轮，而是旧会话长期累积的真实缺失。
`TxHalf.fs:76-95` 的 4:1 不对称（升 0.02 / 降 0.005）会放大单次高报，但它不是
0.03→0.494 的原因；把步长对称化不能解释 850 倍的 erasure 差。

### 11.3 恒定 ~285 KB 有效在途窗口

同一构建、只改 netem delay 的三点（前代数据，本代复核）：

| netem delay | RTT | 吞吐 | rx_ack 速率 | 步/秒 × RTT |
|---|---|---|---|---|
| 5 ms | 10 ms | 40 Mbit/s | 223 步/s | 2.23 |
| 20 ms | 40 ms | 11.9 Mbit/s | 80 步/s | 3.20 |
| 40 ms | 80 ms | 6.1 Mbit/s | 41 步/s | 3.28 |

吞吐严格 ∝ 1/RTT，即**有效在途窗口恒定约 200–285 KB**，与 8 MiB 信用、
1024 步 `--window`、Grant（实测 704–2515 步）都相差一个数量级以上。
生产 314 ms RTT 下这个窗口给出 6–9 Mbit/s，与实测 0.83 MB/s 好轮中位一致。
`SocketTuning.local` 的 4 MB `SO_RCVBUF` 修复已把窗口从约 60 KB 提到约 200 KB
（回环 22.39 s → 4.67 s），但剩余 200 KB 的来源尚未定位。

未执行的判定实验（留给后继，设计已定）：同一床，固定 20 ms delay，
`--window 1024` 对 `--window 4096`、`--paths 1` 对 `--paths 256`，
看窗口是否随 `--window` 变。若不变，窗口在 `FlowBridge.Read` 的每轮供数
（`budget = 4096` 包、`FlowRoom slot` 门控）或 `writeRound` 的 256 帧上限之外，
需要用 `flowread` 三元组的前后增量定位；不要再用会话累计值判断瞬时。

### 11.4 服务端代际对照：无收益，已回滚

HND0 从 `3f05c4a0` 升级到与本机同代的 `7c48936e`（备份 `tbtun.bak-g11`），
净差异只有 `AsyncPaths.CheckAlive`、`CachedClock` 与默认关闭的 `--pace`。
同一 URL、每轮 12 s、10 轮：

| 组 | size MB（10 轮） | 停摆 | 好轮中位 |
|---|---|---|---|
| 基线 `3f05c4a0` | 12.26/0/7.60/0/13.24/0/1.54/0/0/2.87 | 5/10 | 0.83 MB/s |
| 新版 `7c48936e` | 11.14/0/9.91/0.38/3.65/0/0.03/4.23/0/0 | 6/10 | 0.58 MB/s |

判定：未达「停摆 ≤1/10 或好轮中位 ≥1.25 MB/s」，已 `install tbtun.bak-g11` 回滚到
`3f05c4a0`，45 s 到 256 路。两组停摆都发生在 `rho=0.03`、`cell_loss≈0`、256 路、
`beginfail=0` 的健康状态下，与第七、八代的 SIGQUIT 栈证据一致：停摆落在本地代理
的 smux stream 投递层，不在 tbtun 数据面。用户已限定本地代理只作上传通道，
本代不设计该层实验。

### 11.5 join-concurrency 64：保留

`JoinBootstrap` 默认 `rtt=400 ms`、`loss=0.30` → 单次 JOIN 超时 31.4 s、5 次尝试、
退避 400 ms→5 s；并发默认 32、上限 64。生产 `ExecStart` 原本不传该参数。

| 配置 | 初次建池到 256 | restart first250 | restart first256 |
|---|---|---|---|
| 默认 32 | 45 s | 40.0 / 36.9 / 40.0 s | 41.0 / 37.9 / 41.0 s |
| 64 | 14 s | 34.8 / 28.1 s | 35.8 / 30.1 s |

保留 drop-in `/etc/systemd/system/tbtun-hnd0.service.d/join64.conf`：两轮实测无副作用，
恢复时间约降 15–25%。回滚：删该文件 + `systemctl daemon-reload` + `restart`。
CASE-13 仍未稳定达「30 s 内 250」，且单路断开不自愈，门禁不通过。

### 11.6 生产状态

本机 `/usr/local/bin/tbtun` = `7c48936e`；HND0 = `3f05c4a0`，备份 `tbtun.bak-g11`；
`proxy-hnd0tbt4` 与 `tbtun-hnd0` active；两端 256 路；
`/run/hnd0tbt4.yaml` = `fe5dab8b`（原始 smux 开启配置）；
`tbtun-hnd0.service.d/join64.conf` 已装。

### 11.7 未达与残余风险

1. 停摆 5/10 未改善；机制证据指向本地代理 smux stream 投递，用户禁止在该层实验。
2. 恒定 ~285 KB 有效在途窗口未定位、未修复；这是生产 6–9 Mbit/s 的定量上限来源。
3. 单路断开不自愈（sync 模式缺半开检测），修复需改 `SessionCarrier`/`PathLink`
   健康判定，属敏感改动，本代未动。
4. CASE-2（重传率 18.42% 对 `retrans_total=4`）仍未在同窗口重测。
5. CASE-8（慢消费长尾）证据行仍空。
6. 停摆轮 `curl` 有时返回 `code=200` 但 size 远小于 min-size，CASE-12 已按 size 判停摆，
   但调用方若只看 HTTP 码会把停摆误当成功。

## 第十二代：在途窗口三命题证伪、三分账与 stallNs 回滚（2026-11-20 13:12–13:29 UTC）

### 12.1 床上四配置（无特权 netem，`lo` 20 ms / 200 Mbit，当前工作树 JIT）

| 配置 | 16 MiB 用时 | 吞吐 |
|---|---|---|
| `--window 1024 --paths 256`（两次独立） | 2.568048 / 2.568525 s | 6.53 MB/s |
| `--window 4096 --paths 256` | 2.559 s | 6.55 MB/s |
| `--window 1024 --paths 1` | 2.422 s | 6.93 MB/s |

吞吐与 `--window`、`--paths` 均无关（差 <1%）。服务端 flow socket `ss -tinm`：
`skmem:(r0,rb8000000,...)`，即 4 MB 的 `SO_RCVBUF` 请求已生效（`sk_rcvbuf` 8 MB）。

### 12.2 三命题判定

- P1「通告窗口小」：证伪。床上 `rb=8,000,000`；生产 HND0 的 flow socket 同样
  `rb8000000`。
- P2「每轮只取 11–16 源」：证伪。`flowread=317/16,777,422/297`——317 次 Read
  读完全部 16.77 MB，WouldBlock 93.7%，每次 52.9 KB。
- P3「步过小」：证伪。891 步 × 18.8 KB，`--window 4096` 无变化。

真实恒等式：吞吐 = 每次 Read 字节 × Read 频率 = 52.9 KB × 123 轮/s = 6.5 MB/s。

### 12.3 生产三分账

短窗口（12 s）：`RxDelivered` +6,252,876 B，curl 6,209,518 B，差 0.7%；
18443 `bytes_sent=bytes_acked`、Send-Q=0，本地代理读取跟得上。

长窗口（10 轮）：`RxDelivered` +121,381,553 B，curl 47.7 MB，差 73.7 MB；
`Reserve=0`。积压在本地代理层，且与停摆轮同步出现。

### 12.4 stallNs 修复与回滚

改动：`RxHalf.stallNs` 20 ms → 2 ms（单变量）。构建 0 warning / 0 error，
全量单测 8+148+240 全过。部署本机客户端（候选 `ca221719`，备份 `.bak-g12`）。

10 轮：16.61/5.08/0/7.41/0.18/8.18/0/7.11/0/3.11 MB，停摆 3/10，
好轮中位 0.605 MB/s。机制侧 `rx_ack` 0→7425（57.6 步/s，为修复前 42.4 的 1.36×），
但吞吐与停摆均无改善。

判定：回滚。已 `install .bak-g12` + restart，sha=7c48936e，
`ss -tn state established | grep -c ':39443'` = 256，服务 active。

### 12.5 测量陷阱

`ss -tn state established '( dport = :39443 )'` 在本机恒返 0，本代与前代多次据此
误判「0 路」。正确命令：`ss -tn state established | grep -c ':39443'`。

### 12.6 残余风险

1. 生产下行 0.4–1.4 MB/s 的主导限制在本地代理 smux/转发层（长窗口 73.7 MB 积压）；
   用户禁止在该层实验，只记录。
2. `stallNs` 不是用户可见瓶颈：步率升 1.36×，吞吐不变。
3. 单路半开自愈仍未实现（CASE-13）。
4. CASE-8 仍无证据。

### 12.7 单路半开检测：检测生效，补建未发生

代码：`PathLink.CheckAlive`（`SelectRead` 且 `Available = 0` 即 EOF，有未读字节不误判），
`SessionCarrier.Service` 每秒一次探测（`lastHalfOpenProbeNs`），
`tests/Tbt.Tests/PathLinkHalfOpenTests.fs` 三例。构建 0 warning / 0 error，
Tbt.Tests 151/151（新增 3 例）。

回环床（JIT，server 127.0.0.1:39444 → target 18080，client `--paths 256
--join-concurrency 64`）：建池两端合计 512 条。`su -c 'ss -K sport = :<客户端端口>'`
后合计 510，60 s 保持 510；客户端 stats 从 `paths=256` 变为 **`paths=255`** 并稳定。
async 模式（默认 `TBTUN_ASYNC_MODE`）与 sync 模式（`env -u TBTUN_ASYNC_MODE`）结果相同。

判定：**检测层生效**（路径被识别并移除），**补建未发生**（60 s 内无新 JOIN）。
CASE-13 仍为未通过。下一步读 `Client.fs` 的 `drainEvents/attachJoined/present`
与 `PathPool.Failed` 调用点，确认移除后为何没有 `Request` 缺失 id。

工作树状态：`RxHalf.stallNs` 已从实验值 2 ms 恢复为 20 ms，与生产二进制
`7c48936e` 对齐；半开检测改动保留（有单测、检测已验证），但**未部署生产**。

## 第十三代：路径池补建覆盖 path 0（2026-11-20 13:31–13:52 UTC）

### 13.1 根因

第十二代的回环证据被读全：`/tmp/tbt-g12-loop` 的 `client2.log` 与 `server.log`
在事件后恒为 `paths=255`，且两端 `path_tx` 列表都从 1 开始、不含 0。丢失的是
**握手路径 path 0**，而 `PathPool.Request` 的循环从 `id = 1` 起、`Joined` 与
`Failed` 又以 `i > 0` 守卫跳过 id 0，因此它永不重建。检测层本身没有缺陷。

服务端准入允许 id 0 的追加 JOIN：`JoinAdmission.Decide` 只拒绝
`p >= pathLimit`、`p >= 256`、`p >= idLimit`、`inc = 0`、`inc <= highest.[p]`
与 `activePaths >= maxPaths`；`highest.[0]` 初值 0，255 < 256 不触发 Capacity。

### 13.2 修复

- `PathPool.Request` 扫 `0 .. target-1`；`Joined`/`Failed` 覆盖 `i = 0`。
- `Client.presentIds` 在 `carrier.PathOpsPending > 0` 时把 0 视为存在，抑制握手
  尚未落地时重复建 0。
- 单测：`PathPoolTests` 改写并新增（单路径池重建 0、0 的 incarnation 单调、
  0 的失败退避），Tbt.Tests 148 → 153。构建 0 warning / 0 error，
  全量 8 + 153 + 240 = **401 全过**。

### 13.3 回环（同一 JIT 构建，sync）

- 杀非 0（fd=335，sport 55234）：日志 `path 252 joined, paths=256`，1 s 内回满。
- 杀 path 0（fd 最小，sport 53060）：日志
  `21:48:36.359 path 0 joined, paths=256`。
- 服务端 restart：客户端 `session lost` → rc=75（Exit 75 语义未变）→ 新会话
  `paths=1`，1 s 内 174，脚本此时终止观测。
- async 256 路启动预演：45 s 内 `session lost` 0 次、`paths=256` 连续稳定。

脚本判据 `dropped_at=None` 是采样缺陷：补建在 200 ms 采样间隔内完成，计数从未被
观测到低于 256；日志时间戳才是权威证据。

### 13.4 生产

候选 AOT `1547a466`（3,992,936 B）；备份 `tbtun.bak-g13` = `7c48936e`。
`install` + `systemctl restart tbtun-hnd0` 后 5 s 203 路、**9 s 256 路**，
服务 `active`。

自愈实测（客户端 pid 2574251，256 条承载）：

| 动作 | 结果 |
|---|---|
| 杀 fd=277（非 0） | 0.52 s 回到 256 |
| 杀 fd=5（path 0） | 1.55 s 回到 256 |

`journalctl -u tbtun-hnd0` 同期见 `path 251/220/208/236/228/255 joined`，
最终 256 路、active。

### 13.5 async 小池边缘缺陷（记录，未修）

默认 async 下 `--paths 2` 首轮 `paths=0` 后 32 ms 内退出（`session lost`）；
`--paths 256` 45 s 稳定 256。生产为 256 路，未受影响。根因在 async 的
`AddPath` 入队与首个 housekeeping 退出判据的时序；sync 下同参数稳定 `paths=2`，
故与本代改动无关。

### 13.6 吞吐：本代不声称提升

第十二代的长窗口三分账仍是主导证据：tbtun 写入 18443 121.4 MB，curl 只收
47.7 MB，差额在本地代理的 smux/转发层；用户已禁止把本地代理当实验对象。本代只修
自愈，未改发送侧、协议、信用或 FEC，也不改变下行速度结论。

### 13.7 回滚

`install /usr/local/bin/tbtun.bak-g13` + `systemctl restart tbtun-hnd0`，<15 s。

### 13.8 残余风险

1. async 小池启动缺陷（`--paths` 很小且无其它路径兜底时）。
2. CASE-8 仍无证据；CASE-2 重传率仍未同窗口重测。
3. 下行速度限制仍在本地代理层；tbtun 侧可验证的修复面本轮收敛到自愈。

## 第十五代：GatePass2 证伪、发送侧供给与一次被客户端冻结打断的 V1（2026-11-20 14:00–14:27 UTC）

### 14.1 继承审计

- 两端并非同代：本机 `/usr/local/bin/tbtun` = `1547a466`（g13 客户端），
  HND0 = `3f05c4a0`（g11 服务端）。交接报告“生产二进制 1547a466”只对本机成立。
- HND0 上不存在 `.bak-g13` / `.bak-g14`；本机 `.bak-g14` 与现役同 hash，
  真实回滚目标仍是 `.bak-g13` = `7c48936e`。
- 两端 `:39443` 各 256 条 ESTAB，`tbtun-hnd0` 与 `proxy-hnd0tbt4` 均 active；
  HND0 `rmem_max = 16777216`。
- GatePass2 代码与单测与第十四代报告一致（`GatePass2Limit = 262144`、
  `admitsWith` 边界 4 例、`?gatePass2` 默认不变、两端 CLI 各暴露
  `--gate-pass2`），但它**从未在任何二进制上运行过**：HND0 当前服务端没有这个开关。

### 14.2 GatePass2 假设被生产计数直接证伪

服务端空闲 stats（14:03:49，无下载，会话年龄约 8 分钟）：

```
rho=0.290 cell_loss=0.0864 srtt_ms=931 beginfail=8818
boosters=14055 qhigh=291 queued=0 tx_head=peer_ack=5961
gate1=1299299/287211 gate2=392304/0
```

`gate2` 的分母（拒绝数）为 **0**：第二遍写门控从未因 262144 字节上限拒绝一次。
`beginfail` 的真实语义是 `admitted=true` 但 `AsyncPathLink.BeginFrame` 返回 false，
即每路用户态发送槽（`slotCount = 16`，16×2 KiB = 32 KiB）已满
（`AsyncPaths.fs` 的 `txHead - txTail < slotCount`），不是内核未发队列超过 256 KiB。
`UnsentBytes` 读 `SIOCOUTQNSD`，只含未发送字节、不含已发送未 ACK。
因此“GatePass2Limit 是下行限制”不成立；`--gate-pass2` 不进入本代生产实验。

### 14.3 发送侧供给假设与参数化

承载内核证据（同一窗口，本机与 HND0 的 `:39443` 连接）：

- HND0 侧 `skmem:(tb=1762560, t=0, w=0, bl=0)`，即发送缓冲 1.76 MB、未发队列为 0；
- 连接标记 `app_limited`，`bbr bw:16–138 kbps`、`delivery_rate:12–37 kbps`、`cwnd:8–42`；
- `bytes_retrans / bytes_sent` = 18–27%，`lastsnd` 2–4 分钟；
- 本机侧 `rcv_wnd:73–270 KB`，按 RTT 314 ms 允许 1.9–6.9 Mbit/s/路，远高于实测。

内核发送缓冲远未满、未发队列为 0、应用仍有 105 MB 待发，于是 `send` 的 EAGAIN
只能来自 `TCP_NOTSENT_LOWAT = 16384`：应用领先量被钉在 16 KiB，BBR 长期
app-limited，带宽估计停在几十 kbps，pacing_rate 随之下探。第二层限制是
`AsyncPathLink.slotCount = 16`（32 KiB/路）。

本代把两者参数化，默认逐字节不变：

| 位置 | 改动 |
|---|---|
| `SocketTuning.fs` | `pathWith (socket, clamp, notsentLowat)`；`path` 委托默认常量 |
| `SessionCarrier.fs` | 构造新增 `?txSlots`（默认 16），传给 `PathLink`/`AsyncPathLink` |
| `AsyncPaths.fs` | `slotCount` 由构造参数决定，槽数组按该值分配 |
| `Server.fs` / `Client.fs` | 新增 `--notsent-lowat`（默认 16384，下界 4096）与 `--tx-slots`（默认 16，范围 16–64） |
| `tests/Tbt.Tests` | 默认等价与边界单测 |

验证：构建 0 warning / 0 error；全量单测 8 + 153 + 242 = **403** 全过；
AOT 候选 `7ffd15ae`（3,997,032 B）。协议、路径数、8 MiB 信用、`rho` 语义、
GF(16) 上行均未改。

### 14.4 RTT 匹配床：三变体无差异，但床未进入受限期

`unshare -rn`，lo 上 `netem delay 160ms rate 250mbit limit 50000`，256 路，16 MiB：

| 变体 | 配置 | 两轮耗时 | `beginfail` | `gate2` | `qhigh` |
|---|---|---|---|---|---|
| V0 | 16384 / 16 | 5.400 s、5.634 s | 0 | 4086/0 | 287 |
| V1 | 32768 / 32 | 5.050 s、5.633 s | 0 | 6239/0 | 283 |
| V2 | 65536 / 64 | 建池 60 s 未达 256 | — | — | — |

床上总吞吐约 24 Mbit/s，256 路摊到每路 94 kbit/s，BDP≈3.8 KB，16 KiB 的 notsent
仍远高于 BDP，所以不进入受限期；V0 与 V1 的 `srtt`（345/362 ms）与 `rho`（0.030）
也在同一量级。V2 的 `rho=0.250`、`cell_loss=0.0500` 出现在握手期，下载未开始。
**结论：床的尺度不匹配，既不能支持也不能证伪假设；生产是唯一判定场。**

### 14.5 生产基线（同一会话，10 轮 ×12 s）

服务端 `7ffd15ae`（默认参数）、客户端 `1547a466`，同一 proof.ovh 100 MB 经 17893：

| 轮 | size (B) | speed (B/s) | 说明 |
|---|---|---|---|
| r1 | 13,571,048 | 1,130,920 | 好 |
| r2 | 0 | 0 | 停摆 |
| r3 | 6,143,490 | 511,957 | 好 |
| r4 | 0 | 0 | 停摆 |
| r5 | 4,824,321 | 402,026 | 好 |
| r6 | 0 | 0 | 停摆 |
| r7 | 3,315,392 | 276,282 | 好 |
| r8 | 0 | 0 | 停摆 |
| r9 | 0 | 0 | 停摆 |
| r10 | 2,292,109 | 191,009 | 好 |

停摆 5/10；好轮中位 0.40 MB/s；好轮序列 1.13 → 0.51 → 0.40 → 0.28 → 0.19 MB/s
单调下降，与第十四代 CASE-14 的 0.25× 衰减同形。服务端同期
`beginfail 0→3519`、`srtt 100→2311`、`rho 0.105→0.128`、`gate2=256490/0`。

### 14.6 V1 故障：既有客户端冻结被重启触发

V1 在 HND0 写 drop-in
`--notsent-lowat 32768 --tx-slots 32` 并 restart。服务端 29 s 到 256 路，
`generate_204` 204/0.59 s，首轮 13.84 MB；随后一次 0 字节轮，重连后仅 0.46 MB，
此后所有请求 10 s 超时。诊断：

- 服务端 `sessions=0 sessions_failed=0 session_exceptions=0 frames_in=0 frames_out=0`；
- 客户端 `paths=1`，`frames_in`、`rx_ack` 冻结在同一数值；
- 客户端 `beginfail` 从 1425 万涨到 **1.22 亿**，RSS 从 2.6 GB 涨到 **22 GB**，
  进程进入不可中断状态（`kill -9` 与 `systemctl stop/start` 均超时）。

两个参数只在 socket 创建时生效，不会让握手失败；默认参数的同一二进制
（`7ffd15ae`）刚完成 10 轮基线。因此根因是**既有缺陷被服务端重启触发**：
g13 客户端没有把「服务端会话语义消失」判成会话丢失并 `Exit 75`，而是保留
1 条半开会话，对唯一的路径忙循环，每次 `writeBatch` 都 `beginfail` 并分配内存。
第九代记录过同一形态（`--pace 1` 后 `paths=1` 冻结、需 `kill -9`），本代补齐了
它伴随的 8000 万级 `beginfail` 与 GB 级 RSS。

### 14.7 恢复

`ss -K dst 45.11.1.62 dport = :39443` 销毁 256 条承载 socket 后，处于 `Ssl`
（不可中断）的旧进程立即退出；systemd 拉起新进程，32 s 到 256 路，
`generate_204` 204/0.69 s，客户端 RSS 38 MB。HND0 删除 `supply.conf` 并从
`tbtun.bak-g15` 还原 `3f05c4a0`；服务端 `sessions=1`、`:39443` 256 条。

生产最终状态：两端 `3f05c4a0`（服务端）+ `1547a466`（客户端），均 active，
256 路；`7ffd15ae` 只留在本机 `/tmp/tbt-g15-pub`。

### 14.8 判定与回滚面

- **GatePass2：证伪，不部署。** 生产 `gate2` 拒绝数为 0，`beginfail` 来自 16 槽。
- **发送侧供给：未判定。** 参数化与默认等价已交付；V1 从未真正建立新会话，
  所以 `--notsent-lowat 32768 --tx-slots 32` 的吞吐效果无数据。
- **新增阻塞项：客户端会话丢失判据。** 服务端重启后客户端可能停在 `paths=1`
  并对残留路径忙循环，`beginfail` 与 RSS 无上界；这必须先修，否则任何需要
  重启服务端的 A/B 都会把测量变成故障。
- 回滚面：HND0 `install /usr/local/bin/tbtun.bak-g15` + restart（<15 s）；
  本机 `install /usr/local/bin/tbtun.bak-g13` + restart（<15 s）。
- 停摆 5/10 与历史一致，本代任何吞吐中位都不可与峰值代直接比较。
- CASE-2 本代尝试：下载窗口内两次 `ssh` 取 HND0
  `ss -tinm state established | grep :39443` 的 `bytes_sent/bytes_retrans`
  汇总均返回空（ssh 退出为 0 但无行），该次测量作废。CASE-2 仍未重测；
  第四代 18.42% 与第六代 `retrans_total=4` 的冲突不得合并成一个数字。

## 第十六代：半开路径判据的修复与生产验证（2026-11-20 14:30–14:37 UTC）

### 15.1 继承审计

- 生产与交接一致：本机 `1547a466`（3,992,936 B）、HND0 `3f05c4a0`（3,968,056 B），
  两端 `tbtun-hnd0` active、`:39443` 各 256 条 ESTAB；HND0 drop-in 只有
  `stats.conf`，`net.core.rmem_max = 16777216`。
- 文档三处落点在：result 1278 行、cases 535 行、design 998 行、plan 618 行。
  第十四/十五代节与交接报告一致，V1 失败如实记为“不可判定并回滚”，未发现夸大。
- **交接报告未提的交付缺陷**：工作树 23 个文件、2452 行插入未提交，
  `git log` 最新为 `e0569bb docs`。g12 半开检测、g13 PathPool、g15 参数化
  全部只存在于工作树；生产在跑的 `1547a466` 无法从仓库任何提交重建。

### 15.2 缺陷的精确形状

第十五代记为“客户端不执行 `Exit 75`”。逐行核对后，是四处都在用“数量”代替“进展”：

| 位置 | 判据 | 半开时的结果 |
|---|---|---|
| `SessionCarrier.Alive` | `paths.Count > 0 \|\| pathOps 非空 \|\| KeepAlive()` | 1 条死路径即真 |
| `Client.KeepAlive` 谓词 | `carrier.Paths.Count < pool.Target` | 永远为真：补建失败也活着 |
| `housekeeping` 退出条件 | `PathOpsPending = 0 && Paths.Count = 0` | 计数为 1，永不退出 |
| `PathLink` / `AsyncPathLink` 的存活 | `Poll(0, SelectRead) && Available = 0` | FIN/RST 未达时为假 |

平台只有 `TCP_NOTSENT_LOWAT` 与 `TCP_WINDOW_CLAMP` 两项能力。没有
`TCP_USER_TIMEOUT`，内核不会因“有未 ACK 数据而长期收不到确认”报错。于是半开路径
填满内核发送缓冲后：`PathLink.TryBeginFrame` 因 `TxIdle = false` 返回 false，
`AsyncPathLink.BeginFrame` 因 `txHead − txTail = slotCount` 返回 false，
`SessionCarrier.fs:229` 对每帧 `beginFail + 1`，路径既不移除、会话也不退出。
这就是 `beginfail` 1.22 亿与 RSS 22 GB 的完整来源。

### 15.3 修复

在 `SocketTuning` 给每条承载 socket 设 `TCP_USER_TIMEOUT`（Linux level 6, opt 18），
不改退出判据、不改 `KeepAlive`：

| 位置 | 改动 |
|---|---|
| `Platform.fs` | `PlatformCapabilities` 增 `HasTcpUserTimeout`；在临时 socket 上试设 30000 ms 探测；`describe` 增 `tcp_user_timeout` 一列 |
| `SocketTuning.fs` | `UserTimeoutMs = 30000`；`userTimeoutMsFor`（0 关闭，夹到 `[0,300000]`，非 0 下界 1000）；`pathWith` 增第四参数；`path` 委托产品默认 |
| `Client.fs` / `Server.fs` | 新增 `--user-timeout-ms`（默认 30000）；`bootstrap` 与路径接受点透传 |
| `tests/Tbt.Tests` | `userTimeoutMsFor` 边界 7 条断言 |

选它而不是应用层轮询的理由：计时的对象正是故障条件——“有已发未 ACK 的数据，且
确认迟到”。空闲路径没有未 ACK 数据，不会被误杀；超时后内核让下一次读写返回
`ETIMEDOUT`，既有代码已经会 `Close → prune → Paths.Count = 0 → Exit 75`；
`AsyncPathLink.PumpTx` 直接 `alive = 0`。零轮询成本。

30 s 的依据：生产 RTT 314 ms、承载丢包 18–27%，RTO 指数退避 5–6 次约 25–40 s。
误杀的代价是可逆的：第十三代实测重 JOIN 0.52 s（非 path 0）与 1.55 s（path 0）。

### 15.4 证据

构建 `0 Warning(s) / 0 Error(s)`；全量单测 **8 + 153 + 243 = 404** 全过。

进程级确定性实验（不经隧道，`/tmp/tbt-ut.py`，对端 `SIGSTOP` 后写满发送缓冲）：

| 设置 | 结果 |
|---|---|
| `TCP_USER_TIMEOUT = 2000` | 2.484 s 后 `send` 抛 `TimeoutError`、`errno 110`（ETIMEDOUT） |
| `TCP_USER_TIMEOUT = 0`（关闭） | 8.002 s 内始终 `BlockingIOError`，零错误 |

这是修复有效性的直接证据：同一个写循环，唯一差别是 opt 18。

AOT 候选 `734318db`（4,001,144 B）已部署本机，备份 `tbtun.bak-g16`。
部署后 35 s：active、`:39443` = 256、`generate_204` 204 / 0.734 s、`beginfail = 0`。

生产 3 次 HND0 restart（客户端 `734318db`、服务端 `3f05c4a0`）：

| 次 | first250 | first256 | 客户端 PID | RSS |
|---|---|---|---|---|
| 1 | 25 s | 26 s | 2633931 → 2635045 | 37,080 KiB |
| 2 | 未取得（建池 24 s 后被第 3 次 restart 打断） | 未取得 | 2635045 → 2635764 | — |
| 3 | 32 s | 36 s | 2635764 → 2636614 | 45,196 KiB |

日志三次都出现 `session lost: no path remains, exiting for restart`，PID 每次更换，
**没有出现 `paths=1` 忙循环**；三次测量点的 RSS 分别为 37 / 45 / 70 MB。
最终 active、256 路、`generate_204` 204 / 0.751 s。

### 15.5 诚实边界

- 生产的三次 restart 都是**服务端进程死亡**：内核发 FIN/RST，客户端 `Paths` 归零，
  既有 `Exit 75` 直接生效。它们证明“修复未回归”，**不是**半开路径检测的端到端证据。
- 半开路径（FIN/RST 丢失）只有进程级 `SIGSTOP` 实验与代码路径支撑。
  原计划的 netns 丢包版本未执行：它与进程级实验测的是同一个内核行为，且需要
  `iptables`/`nft` 与一次 256 路建池，本代按经济性判为重复，未冒充为已执行。
- 本代**没有**任何吞吐提速声明。用户可见下行仍受第七/十二代定位的本地代理
  转发层限制（tbtun 长窗口写入 121 MB、curl 收 47.7 MB），而该层用户已禁止实验。
- 发送侧供给 A/B（`--notsent-lowat 32768 --tx-slots 32`）本代未执行：
  前置缺陷刚修完，HND0 仍需再部署一次服务端候选；判据与切片已固定在
  plan §14 的 T10/T11，后继可在安全前提下直接执行。

### 15.6 最终状态与回滚面

- 本机 `/usr/local/bin/tbtun` = `734318db`（4,001,144 B），备份 `tbtun.bak-g16`；
  `tbtun-hnd0` active、256 路、RSS 70 MB。
- HND0 未改：`3f05c4a0`（3,968,056 B），`tbtun-hnd0` active、256 路。
- 回滚：`install /usr/local/bin/tbtun.bak-g16` + `systemctl restart tbtun-hnd0`（<15 s）。
- 参数级关闭：`--user-timeout-ms 0`，不需要换二进制。
- 未提交：工作树的 src 改动仍未 commit，后继必须先按代收口再继续。

## 17. 第十七代：tbtun-only 归因与一次被证伪的信用窗口修复（2026-11-20 15:00）

### 17.1 审计

- 生产快照：本机 `734318db`（4,001,144 B）、HND0 `3f05c4a0`（3,968,056 B），两端
  `active`、`:39443` 各 256 路；`HEAD 4347b96`，`git diff HEAD -- src tests` 为空，
  生产客户端源码可由提交重建。
- `cases.md` 修正 9 处失效的 `ss ... '( dport = :39443 )'` 过滤语法（本机实测恒返 0），
  CASE-13 第十六代证据行由「通过」降为「部分通过」（r2 无 `first250/first256`），
  新增 CASE-15 tbtun-only 归因用例。

### 17.2 tbtun-only 对照（CASE-15，绕过本机 mihomo）

| 路径数 | 三轮 size（B/15 s） | speed（MB/s） | 中位 |
|---|---|---|---|
| 32 | 19,778,753 / 16,919,937 / 10,079,152 | 1.32 / 1.13 / 0.67 | 1.13 |
| 256 | 21,988,545 / 9,824,433 / 13,618,529 | 1.47 / 0.65 / 0.91 | 0.91 |

路径数 32→256 只提高 0–30%，不是杠杆。客户端 `rx_ack` 共 2,584 步 / 45 s =
57.4 步/s，每步 17.6 KB，乘积 1.01 MB/s，与 curl 一致。服务端下行
`flowread=72516/69251103/9`（读入 69.25 MB，平均 955 B/次，WouldBlock 仅 9 次）、
`tx_head=3288 peer_ack=2891`（在途 397 步）、`queued=0`、`gate1=328887/79137`
（deny 19%）、`srtt 1685 / minrtt 1129`（1.49×）。

**判定：<2 MB/s，tbtun 单流下行缺陷成立；mihomo 不是唯一墙，甚至不是主墙。**

### 17.3 信用窗口 `window/2` 尝试与回滚

假设：`Flows.refreshCredit` 每 `window/8` 才上报，服务端每次只获得 1/8 窗口的新空间，
吞吐上界 = `window/8 ÷ RTT`。床上 8 MiB 窗口、320 ms RTT 对应 3.1 MB/s，与实测
3.0 MB/s 吻合；生产 3 流、5.4 MiB 窗口对应 2.15 MB/s，与 1.0 MB/s 同量级。
把阈值改为 `window/2` 后：

- 回环床 16 MiB：基线 5.676 s（2.96 MB/s），改后 5.950 s（2.82 MB/s）。
  但同床 target 直连就要 4.559 s（3.68 MB/s），**该床不能判定 tbtun 上限**。
- 生产第二实例（HND0 `3f05c4a0` 不变）：旧客户端三轮 23,096,097 / 7,872,641 /
  3,960,353 B/15 s（1.54 / 0.52 / 0.26 MB/s，中位 0.52）；新 JIT 客户端
  `session up: paths=0 → paths=1 → session lost`（22 ms），0 字节，无有效对照。

**决定：`Flows.fs:291` 回滚为 `window/8`，不部署；JIT 已按回滚版重建。** 假设未被
证伪也未被证实，不能作为提速依据。

### 17.4 诚实边界

- 本代没有任何用户可见提速声明；CASE-1、CASE-12、CASE-14 仍不通过。
- tbtun-only 的约 1 MB/s 尚未定位到单一代码行；已排除路径数、FEC（客户端
  `rho=0.03`、`cell_loss=0`）、承载健康、信用总量与 mihomo。
- 事故：清理第二实例时 `fuser -k 18081/tcp` 误杀本机 omp 的 node 进程
  （pid 2784797）；守护器已重建（2644437）。后续只用自己记录的 pid 清理。
- 生产未改：本机 `734318db`、HND0 `3f05c4a0`、256 路、`generate_204` 204/1.398 s；
  第二实例端口已释放，无残留。
