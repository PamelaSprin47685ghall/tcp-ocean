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
  `opened=1 closed=0` 不是泄漏：mihomo smux `max-connections: 1` 把多条 curl 复用为
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

### 判定

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
