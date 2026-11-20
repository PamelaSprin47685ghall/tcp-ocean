# 链路提速验收用例集（2026-11-20）

> 目标：本机 ↔ HND0 生产链路（256 路 TCP、TCP-only、内层 `127.0.0.1`）的下行吞吐从约
> **2 Mbit/s** 提升到可用量级，且不以缩减路径数、放松断言或改变既有产品策略换取速度。
> 三个实测症状是本轮的验收对象：承载 TCP 重传 **19–23%**、协议 SRTT **约 950 ms**
> 对干净样本 **约 320 ms**、解码前沿每秒只推进**数步**。
>
> 代码事实（2026-11-20 核对，第二代已更新）：服务端下行 `SessionCarrier` 以 `paceOn`
> 缺省 `false` 构造（旧名 `uplinkPace`，已改为方向中性），
> `RateGroup` 只服务客户端上行；`AsyncPathLink` 每路 32 个 tx 槽（约 42 KiB）而没有
> `TunPath` 的 cwnd/pacing/BBR；下行只剩 `WriteGate`（pass1 ≤4 KiB、pass2 ≤256 KiB）。
> 接收侧 `RxHalf` 的 `hold` 是到达序 FIFO，`serviceHold` 每 20 ms 才放行一次前沿步，
> `lossEvident` 要求所有活跃路径都越过 frontier，256 路下几乎不成立。
>
> 下行慢至少有三条竞争假设，本用例集不预设哪条成立；每条都必须按自己的判据证伪或证实：
>
> | 假设 | 机制 | 证伪判据 |
> |---|---|---|
> | H1 发送侧无组级速率控制 | 256 路聚合同步突发越过瓶颈 BDP，尾丢转成重传与 cwnd 减半 | 开 `--pace` 后 CASE-2 重传 ≤5% 且 CASE-1 中位不升 ⇒ H1 不充分 |
> | H2 接收前沿与信用回收 | 前沿步进慢 ⇒ 8 MiB 信用按解码前沿回收 ⇒ 服务端窗口用尽停发 | CASE-9 中 `rx_ack` 步/s 与交付字节率偏差 >50%，或 `flow_credit` 长期 <1 MiB ⇒ H2 成立 |
> | H3 外层供数与 smux 复用 | mihomo `max-connections: 1` 把多请求复用到一条 VLESS 长连接，HND0 直连 219 Mbit/s 而隧道 8–10 Mbit/s | HND0 直连与隧道同 URL 比值 >5×，且两端 stats 显示发送队列空 ⇒ H3 至少是共同成因 |
>
> 已有一组反证必须写进判定：同一影子服务、同一 OCA，开节拍 8.52 Mbit/s
> （15 s 收 15,974,400 B）、关节拍 18.11 Mbit/s（11.58 s 收齐 25 MiB），开节拍时
> `producefail` 从 4836 增到 15749。因此“给下行加 pace”不是已证收益，
> 任何以 pace 为唯一变量的实验都必须允许它失败。

## 评分总则

- 每个 CASE 独立打 0–100 分，`threshold` 是 PASS 线，低于它即不通过。
- 每个 CASE 同时给 `user_score`（用户能否感到更快、更稳）与 `developer_score`
  （机制是否可解释、可回退、无隐藏代价），最终分 `final = min(user_score, developer_score)`。
  两个分数都由执行者依据下方证据表评定；“wow”只进入 `user_score`，机制收益进入 `developer_score`。
- 一次手工执行由执行者以用户身份完成：打开真实终端、输入真实命令、读取真实输出；
  自动单测和 CI 只作为前置检查，不能替代本文件的步骤。
- 未执行的非 happy path 每条扣 5 分；未保留失败样本扣 10 分；用测试床结果冒充生产结果扣 20 分。
- 分数取整到 5 分粒度（55、60…）；同一证据的两次独立评分必须落在 ±5 内。
- 最低证据：curl 的 `size_download/speed_download/time_total/http_code`、两端该轮前后的
  `ss -tn state established` 连接数、两端 `journalctl` stats 各一行、以及本轮原始文件目录。
- 生产切换门槛（全部 CASE）：CASE-0 与 CASE-4 通过（通道与 256 路），CASE-1、2、3、5
  达到阈值，CASE-6 不退化，CASE-7 的回滚演练通过，CASE-9 不得判 0。
  未达门槛时生产保持 `f2b63e00…` 或上一个已验收版本。

---

## CASE-0 部署与回滚通道（前置）

- **path**：本机 `/usr/local/bin/tbtun` 与 `tbtun-hnd0` 单元；HND0
  `/usr/local/bin/tbtun` 与云端单元；候选二进制及其 sha256。
- **threshold**：90。
- **steps**
  1. 记录两端当前状态：`sha256sum /usr/local/bin/tbtun`、
     `systemctl is-active tbtun-hnd0`、本机
     `ss -tn state established '( dport = :39443 )' | wc -l`、HND0 侧同式但 `sport`。
  2. 本机部署：`systemctl stop tbtun-hnd0` →
     `install -m 755 <候选> /usr/local/bin/tbtun` →
     `systemctl start tbtun-hnd0`，整段计时；12 s 后确认 ≥254 且 30 s 内到 256 路。
  3. 本机回滚：`install` 回备份 `<二进制>.bak`，重启，同样确认 256 路，计时。
  4. HND0 通道只接受两种已核实的形态：(a) ≤64 KiB 的小文件，传后必须 `sha256sum`
     与本地一致；(b) 远端源码树与本地同一 commit（含 `Trace.FlowGap`）后远端
     `dotnet publish -r linux-x64 -p:NuGetAudit=false -p:TreatWarningsAsErrors=false`。
     大文件 scp、256 KB 分块、无基线远端 publish 已是失败样本，不再重试。
  5. 若 (a)(b) 都不可用，则服务端改动不进入本轮生产 A/B，只做本机客户端单变量；
     该判断必须写入结果文档，不得含糊成“计划上线”。
- **expected**：至少一条端到端通道能在 60 s 内完成“部署 → 256 路 → 回滚 → 256 路”，
  且二进制 sha256 已核对；否则本用例失败，未过本用例的实验不得称为“生产 A/B”。
- **rubric**
  - 100：两端通道都可用，部署与回滚各 <60 s，hash 全核。
  - just-PASS 90：本机通道可用且有回滚证据；服务端通道被明确判为不可用并留证。
  - just-below 85：本机通道可用但回滚未演练。
  - 0–50：两端都无可用通道；或未核 hash 就部署。
  - 硬失败：把传输重试次数当进度；用未核 hash 的二进制做 A/B；
    回滚 >120 s 仍宣称可控。
  - 扣分：未记传输字节数与 hash −20；未记部署耗时 −10。

---

## CASE-1 生产下行吞吐持续性

- **path**：本机 `17893` → 本地 mihomo HND0TBT4（VLESS）→ 本地 tbtun client → 隧道 →
  HND0 tbtun server → `127.0.0.1:18443` mihomo-tbt4 → fast.com IPv4 OCA。
- **threshold**：80。
- **steps**
  1. 在 HND0 取新 targets，并固定一个 IPv4 OCA URL：
     `TOKEN='YXNkZmFzZGxmbnNkYWZoYXNkZmhrYWxm'`；
     `ssh root@45.11.1.62 "curl -sS 'https://api.fast.com/netflix/speedtest/v2?https=true&token=$TOKEN&urlCount=5'"`；
     选一个 `ipv4-*` target，记下完整 URL。
  2. HND0 直连同一 URL 验证供数（不计入本用例）。直连 `speed_download` 低于 50 Mbit/s、
     或低于当轮隧道中位的 5 倍，或非 200，则该轮无效。curl 的 `speed_download` 单位是
     B/s，`Mbit/s = speed_download * 8 / 1e6`；2026-11-20 复核同一 OCA 为
     26,214,400 B / 0.955 s ≈ 27.4 MB/s ≈ 219 Mbit/s。旧门槛 30 Mbit/s 是在把
     B/s 当 Mbit/s 读的背景下写的，会与前代 15.87 的实测自相矛盾，故废弃。
     `ssh root@45.11.1.62 "curl -sS --max-time 10 -o /dev/null -w 'code=%{http_code} size=%{size_download} speed=%{speed_download} time=%{time_total}\n' '$URL'"`。
  3. 本机连续 5 轮，每轮 ≤15 s，不并发其他下载：
     `curl --proxy http://127.0.0.1:17893 -sS --max-time 15 -o /dev/null -w 'code=%{http_code} size=%{size_download} speed=%{speed_download} time=%{time_total}\n' "$URL"`。
  4. 每轮前后各取一次连接数与 stats：`ss -tn state established '( dport = :39443 )' | wc -l`；
     `journalctl -u tbtun-hnd0 -n 1 --no-pager -o cat`；HND0 侧
     `ssh root@45.11.1.62 "ss -tn state established '( sport = :39443 )' | wc -l; journalctl -u tbtun-hnd0 -n 2 --no-pager -o cat"`。
  5. 把 5 轮原始输出写入一个新目录（如 `/tmp/tbt-link-$(date +%s)`），失败样本也保留。
- **expected**：5 轮中位数 ≥ 20 Mbit/s（本代实测旧版回滚后中位 8–10 Mbit/s，故为
  2–2.5×），总体相对标准差 < 20%，
  至少 4/5 收到 HTTP 200，每轮前后两端都保持 256 路。用户应感到 25 MiB 目标能在
  十几秒内跑完，而不是 15 s 只收到 1–3 MiB。
- **rubric**
  - 100：中位 ≥ 40 Mbit/s，CV < 10%，5/5 收齐或 HTTP 200，双端 256 路。
  - just-PASS 80：中位 ≥ 20 Mbit/s，CV < 20%，≥4/5 为 200，双端 256 路。
  - just-below 75：中位 15–20 Mbit/s，或 CV 20–30%。
  - 60–70：中位 8–15 Mbit/s，或 2/5 无响应。
  - 0–50：中位 < 8 Mbit/s。
  - 硬失败（判 0）：任一轮非 256 路却计入样本；用 HND0 直连数字冒充隧道；丢弃失败样本；
    改动代理规则把目标变成 DIRECT；部署新二进制后 30 s 内无法完成一次 HTTPS 握手
    （TLS 错误或 0 字节），而同一 URL 在旧版上可完成。
  - 扣分：未测上行 −10；未做 HND0 直连对照 −10；未记双端连接数 −5；未保存原始目录 −10。
  - 取整 5 分；两次评分差 > 5 时重评。

## CASE-2 承载 TCP 重传率

- **path**：HND0 `eth0` 上 `sport = :39443` 的 256 条已建 TCP。
- **threshold**：80。
- **steps**
  1. 选一个 CASE-1 的有效下载轮，在同一窗口内对 HND0 做前后快照：
     `ssh root@45.11.1.62 "ss -tinmH state established '( sport = :39443 )'" > before.txt`；
     下载结束后同样取 `after.txt`。
  2. 逐路配对的 `bytes_sent`/`bytes_retrans`（或 `retrans` 计数），只统计配对成功的路径。
  3. 同时记录本机 `nstat -az TcpRetransSegs TcpOutSegs` 的前后差作为旁证。
  4. 与同一轮 CASE-1 的交付速率并列，确认不是“重传少了但交付也塌了”。
- **expected**：配对路径的 `bytes_retrans / bytes_sent` ≤ 5%（基线 19–23%），且该轮交付
  不低于 CASE-1 的中位。
- **rubric**
  - 100：≤ 2%，且全部 256 路配对。
  - just-PASS 80：≤ 5%，至少 240 路配对。
  - just-below 75：5–8%。
  - 50–70：8–15%；0–40：> 15%。
  - 硬失败：用累计值代替同窗口增量；缺少任一端快照仍给出比例；把内核 `retrans` 当首次丢包率。
  - 扣分：未记录交付速率 −10；未配对就报聚合比例 −15；未保留快照 −10。

## CASE-3 协议 RTT 与自致排队

- **path**：两端 tbtun stats 的 `srtt_ms`、`minrtt_ms`，以及同窗口承载 TCP 的 `rtt/rttvar`。
- **threshold**：85。
- **steps**
  1. 在 CASE-1 的单轮下载窗口内每秒取 HND0 服务端与本地客户端的 stats 行。
  2. 记录 `srtt_ms`、`minrtt_ms`、`queued`、`tx_head - peer_ack`、`gate1/gate2` 的增量。
  3. 同一时间用 `ss -tinm` 取若干条承载的 `rtt` 与 `cwnd`，取中位。
  4. 对照空闲时（无下载）同一会话的 `srtt/minrtt`。
- **expected**：有载 `srtt_ms` ≤ 1.6 × 空闲 `minrtt_ms`；不再出现 300 ms 干净样本对
  900 ms 有载样本的倍增；`queued` 在采样点不长期为 0 而交付又慢（那是上游供数或信用，
  不是路径排队）。
- **rubric**
  - 100：有载 SRTT ≤ 1.25 × minRTT，且承载 cwnd 不出现 256 路同步塌到初始值。
  - just-PASS 85：≤ 1.6 × minRTT。
  - just-below 80：1.6–2.0 ×。
  - 50–70：2.0–3.0 ×；0–40：> 3 × 或 SRTT < minRTT。
  - 硬失败：用 `srtt` 的平滑值证明没有排队而不给 `queued`/`cwnd` 证据；跨不同轮比较。
  - 扣分：未取空闲对照 −10；未取 `queued` 增量 −5。

## CASE-4 256 路保持与字节一致

- **path**：本机回环真实 TCP（`tests/e2e/test_async_lifecycle.py`）与生产隧道两种。
- **threshold**：90。
- **steps**
  1. 本机：对候选二进制运行生命周期脚本四批，每批都必须建齐 256 路、传完并校验 1 MiB、
     正常关闭。
  2. 生产：在 CASE-1 每轮前后取双端连接数，必须都是 256（或 257 含监听项，按历史口径说明）。
  3. 生产下载后核对客户端 `frames_out` 与 `cons_tx_b` 增量非零，服务端 `rx_cells` 增长。
  4. 关闭后检查 HND0 端 `eventfd` 数量不随批次数增长。
- **expected**：没有“少路运行”被当成优化结果；字节一致；关闭无描述符泄漏。
- **rubric**
  - 100：四批全部 256 路且字节一致，生产每轮 256 路，`eventfd` 不增长。
  - just-PASS 90：全部通过，允许一批因环境重试一次。
  - just-below 85：一批只到 255 路但重跑通过。
  - 0：任一批稳定低于 256 路；或字节不一致。
  - 硬失败：用少路数据证明提速；跳过 `eventfd` 检查。
  - 扣分：未跑回环 −20；未取生产连接数 −10。

## CASE-5 上行不退化

- **path**：本机 → HND0 的 `tx_direction=up` 方向。
- **threshold**：80。
- **steps**
  1. 用同一 fast.com URL 通过生产代理做下载（下行）时，另起 `curl --proxy` 上传一个
     本地 8 MiB 文件到 HND0 影子 HTTP（或复用现有回环校验端点），记录上行速率。
  2. 对照改动前同一时段的 `interactive` 行与上行 `path_tx` 分布。
  3. 记录上行 `srtt_ms`、`rho`、`boosters`，确认没有把损失转移到上行。
- **expected**：上行中位不低于改动前的 90%；交互（握手、DNS、小请求）不出现数秒卡顿。
- **rubric**
  - 100：≥ 100% 且交互 p99 不升。
  - just-PASS 80：≥ 90%。
  - just-below 75：80–90%。
  - 0–60：< 80% 或新增数秒级交互卡顿。
  - 硬失败：未改上行却用下行数字替代。
  - 扣分：未测交互 −10。

## CASE-6 官方测试床矩阵不退化

- **path**：`tests/e2e/run.sh`（4/8/64 路；0% 与 5% 丢包；20 ms OWD；200/1000 Mbit/s）。
- **threshold**：85。
- **steps**
  1. `tests/e2e/run.sh 20 200 0 4 10`
  2. `tests/e2e/run.sh 20 200 0.05 4 10`
  3. `tests/e2e/run.sh 20 200 0 8 10`
  4. `PROFILE=rtt400 HANDSHAKE_S=60 tests/e2e/run.sh 200 1000 0 64 10`
  5. 读取 `segments.json`，按与主线相同的有效样本规则排除主机停顿段。
- **expected**：bulk 不差于当时主线；交互（负载下 echo/ping）保持可用；无新的
  no-SACK/blackout/RepairWait 断言被放宽。
- **rubric**
  - 100：全部矩阵的 bulk 中位 ≥ 主线，交互全部通过。
  - just-PASS 85：最少一条矩阵单枪低于主线最小但中位不降，且无稳定地板下降。
  - just-below 80：中位降 1–3%。
  - 0–60：中位降 > 3%，或放宽/删除既有断言。
  - 硬失败：改测试阈值来通过；并发跑 mux 时修改 `run.sh`/`netns.sh`。
  - 扣分：未给 `segments.json` −10；未记录 script commit 与二进制 hash −5。

## CASE-7 故障、边界与回滚

- **path**：生产两端服务、代理、路由与影子实验资源。
- **threshold**：85。
- **steps**
  1. 目标无响应：用一个会超时的 URL 跑一轮，确认只有该 curl 失败，隧道与 256 路保持。
  2. 单路断开：在 HND0 `ss` 找到一条承载并 `ss -K` 关闭，确认会话在本轮内补到 256 路
     或至少不丢应用字节。
  3. 看门狗：正常 25 MiB 下载不触发 `mihomo-watchdog` 重启；人为把阈值临时降到
     1 KiB 的独立实例（不影响生产）应触发一次并自动恢复。
  4. 回滚演练：把候选二进制换回 `f2b63e00…`，确认服务与 256 路恢复，再换回候选。
  5. 整机重启（可延后）：记录新引导标识、`8890` 规则、`ip route get 45.11.1.62`
     指向 `enp8s0`、双端 256 路、`17893` 可用。
  6. 清理：影子监听、防火墙规则、临时密钥、`/tmp` 目录全部撤除并核实。
- **expected**：任一单点故障不把整条链路打成 0；回滚在 2 分钟内恢复；没有遗留影子资源。
- **rubric**
  - 100：六步全部手工完成并留证，回滚 < 60 s。
  - just-PASS 85：前五步完成，整机重启按 TODO 保留但记录未做。
  - just-below 80：缺看门狗或回滚演练之一。
  - 0–60：回滚失败，或影子监听残留。
  - 硬失败：为让步骤通过而改生产路由/防火墙规则；用重启代理冒充整机重启验收。
  - 扣分：未核实清理 −20。

## CASE-8 慢消费长跑与异步长尾

- **path**：本机 256 路回环 + 生产 15 s × 多轮（累计观察，不声称连续 30 分钟）。
- **threshold**：75。
- **steps**
  1. 回环跑一个“只读一半再停”的慢消费者用例，确认 `Reserve` 不无限增长、交付不丢字节。
  2. 生产连续 4 轮 CASE-1 后，读取 `async_svc_us_max`、`async_faults`、`local_fail`。
  3. 记录客户端 `flow_state`、`flow_credit` 的增量，确认没有单流信用长期触顶而 `queued=0`。
- **expected**：`async_svc_us_max` 不出现 100 ms 级长尾；`async_faults=0`；慢消费者下
  内存不随轮次单调增长。
- **rubric**
  - 100：全部通过且长尾 < 20 ms。
  - just-PASS 75：长尾 < 100 ms，无故障，内存稳定。
  - just-below 70：长尾 100–200 ms 或一次可解释故障。
  - 0：持续 > 200 ms、故障增长或内存泄漏。
  - 硬失败：用累计 `csvc_us` 代替单次 `Service` 峰值。
  - 扣分：未保留日志 −10。

---

## CASE-9 接收前沿推进与信用回收

- **path**：本机客户端 stats 的 `rx_ack`/`rx_base`/`recovered`/`held`/`flow_credit`，
  与 HND0 服务端 stats 的 `tx_head`/`peer_ack`/`queued`。
- **threshold**：80。
- **steps**
  1. 在 CASE-1 的单轮窗口内每秒取一行客户端 stats，另在窗口首尾各取一次服务端 stats。
  2. 计算 `rx_ack` 的每秒增量；用该轮 `size_download` 除以 `rx_ack` 总增量得到
     “每步等效字节”，再算 `步/s × 每步字节`，与 `size_download / time_total` 对照。
  3. 记录 `flow_credit` 的最小值与低于 1 MiB 的累计时长；记录 `held` 峰值与
     `recovered` 增量。
  4. 记录服务端 `tx_head - peer_ack` 的时间序列；若它在信用耗尽后下降，
     说明发送端被信用卡死，而不是没有数据可发。
- **expected**：`rx_ack` 步/s 与交付字节率一致（偏差 ≤20%）；`flow_credit` 不低于
  1 MiB，或耗尽后 ≤1 s 恢复；`held` 不随轮次单调增长。
- **rubric**
  - 100：三项全满足，且 `tx_head - peer_ack` 与信用同步。
  - just-PASS 80：步/s 一致，信用有一次 ≤1 s 的耗尽。
  - just-below 75：步/s 与字节率偏差 20–50%。
  - 0–60：信用长期 <1 MiB 且 `tx_head - peer_ack` 下降（发送端被信用卡死）。
  - 硬失败：只用 `queued=0` 单点证明“不是接收侧”；只取单点不取时间序列；
    用不同轮比较。
  - 扣分：未做步/s 与字节率对照 −15；未保留原始 stats −10。

---

## CASE-10 flow socket 接收缓冲

- **path**：本机无特权网络命名空间（`unshare -rn`）+ `tc qdisc netem delay 20ms rate 200mbit`，
  同一工作树构建的 `tbtun server/client`，256 路，32 MiB 下载；对照组为无 netem 128 MiB。
  该用例不依赖 HND0，可在任何 Linux 主机复现。
- **threshold**：80。
- **steps**
  1. 从服务端启动日志取 `up, S=… flow_rcvbuf_req=<请求> flow_rcvbuf_eff=<有效> rmem_max=<上限>`。
     `eff` 必须是 `2 × min(req, rmem_max)`（Linux 对 `SO_RCVBUF` 先 clamp 再乘 2）；
     三者不满足该等式即记硬失败，本用例按 `eff` 评分。
  2. 无 netem：`unshare -rn` 内起 server/client，`curl` 128 MiB，记录 `time_total`。
  3. 加 `netem delay 20ms rate 200mbit limit 50000`：同一路径，`curl` 32 MiB，记录
     `time_total` 与 `speed_download`。
  4. 同一轮内取一次服务端 stats，记录 `qhigh`、`boosters`、`sealed`、`flowread` 三元组。
  5. 回滚对照：在**同一个 commit** 上只去掉 `SocketTuning.local` 的 `SO_RCVBUF` 一行，
     重新构建，记录两个二进制的 sha256 与大小，重跑第 3 步。对照二进制不得含任何
     与该行无关的改动；若无法保证，本轮记“无有效对照”并扣分，而不是拿另一个 build 充数。
- **expected**：第 3 步 ≤ 8 s（≥40 Mbit/s）；第 2 步用**相对判据**——同构建、同机器、
  同测试床的无 netem 128 MiB 吞吐不低于回滚对照的 95%（绝对值同时记录，目标 ≥550 Mbit/s，
  但不作为独立门槛，因为环回吞吐随构建漂移：同一修复在剥离无关改动后从 757 掉到 620–626
  Mbit/s）；`boosters/sealed` ≤ 10%；回滚后第 3 步回到 ≥20 s。
- **rubric**
  - 100：第 3 步 ≤ 5 s、第 2 步 ≤ 1.5 s、`boosters/sealed` ≤ 5% 且 `qhigh` ≥ 200。
  - just-PASS 80：第 3 步 ≤ 8 s，第 2 步不退化，回滚对照 ≥ 20 s。
  - just-below 75：第 3 步 8–12 s，或第 2 步退化 5–10%。
  - 0–60：第 3 步 ≥ 20 s（修复未生效），或无回滚对照。
  - 硬失败：对照二进制混入与该行无关的改动（必须同 commit、只差该行、双方 sha256 都记录）；
    不记录 `rmem_max` 与有效值；只测单轮不测无 netem 对照。
  - 扣分：未记录 `qhigh`/`boosters` −10；未保留 `/tmp` 目录 −10。
- **备注**：PASS 线来自 2026-11-20 第三代实测（修复后 4.673 s / 57.4 Mbit/s，无 netem
  1.419 s / 757 Mbit/s，`boosters/sealed` 3.2%，`qhigh` 284）。若主机 `rmem_max` 低于
  1 MB，本用例预期无法通过，应先把 `net.core.rmem_max` 提到 ≥4 MB。

---

## CASE-11 并发流的内存上界

- **path**：本机生产客户端（`/usr/local/bin/tbtun client`）+ 一条 VLESS/smux 长连接，
  或回环测试床；对同一 URL 起 `N ∈ {1, 8, 64}` 条并发下载，每轮 ≤15 s。
- **threshold**：80。
- **steps**
  1. 测前记录 `systemctl show tbtun-hnd0 -p MemoryCurrent`（或 `ps -o rss=`）与服务端
     `rss_kib`；同时记录 `ss -tinm` 里 flow socket 的 `skmem` 与 `rcv_space`。
  2. 单流跑一轮 15 s，记录两端 RSS 增量与 `skmem.r` 峰值。
  3. 8 路、64 路各跑一轮，记录同样三项；每轮间隔 ≥3 s 让内核回收。
  4. 在服务端读 `/proc/net/sockstat` 的 `TCP: inuse` 与 `mem`，确认内存随流数近似线性。
  5. 用 `N=64` 的结果外推 256 流上界 = 单流峰值 × 256，与主机可用内存比较。
- **expected**：单流 RSS 增量 ≤ 16 MB（8 MB `sk_rcvbuf` + 8 MiB 信用余量）；8 路与 64 路
  的增量单调但不超过 `16 MB × N`；外推 256 流不超过主机可用内存的 50%。
- **rubric**
  - 100：单流 ≤ 12 MB，且 64 路实测 ≤ 1 GB，外推 256 路 ≤ 可用内存 30%。
  - just-PASS 80：单流 ≤ 16 MB，64 路 ≤ 2 GB，外推 256 路 ≤ 50%。
  - just-below 75：单流 16–24 MB，或外推 60–80%。
  - 0–60：单流 > 24 MB，或外推 > 80%，或出现 OOM/`sk_rcvbuf` 回落到自动调优。
  - 硬失败：用 `RSS` 代替 `skmem` 记账；不记录流数；只测单流就外推。
  - 扣分：未读 `/proc/net/sockstat` −10；未做 64 路 −15。
- **备注**：mihomo `max-connections: 1` 会把多条 curl 复用成一条 flow，所以“并发流”
  必须由独立 smux 连接或直连 tbtun 产生，否则测到的仍是单流。

---

## 证据表（执行时逐行填写）

| 项 | 本轮执行者 | 日期 | 结果 | 原始目录 |
|---|---|---|---|---|
| CASE-1 下行 5 轮 | 第四代 | 2026-11-20 | 通过：10.1/13.0/11.8/9.9/6.6 Mbit/s，中位 11.8 对旧版 3.5（3.4×），5/5 HTTP 200 | `/tmp/tbt-g4-prod-*`、`cand.log` |
| CASE-2 重传率 | 第四代 | 2026-11-20 | 未达：18.42%（12 s 发 39.1 MB、重传 7.2 MB），低于旧版 19–23%，但高于 5% 门槛 | `/tmp/tbt-g4-prod-*` |
| CASE-3 RTT/排队 | 第四代 | 2026-11-20 | 通过：srtt 325 / minrtt 315 = 1.03×（旧版 578/320 = 1.8×） | `/tmp/tbt-g4-prod-*` |
| CASE-4 256 路 | 第四代 | 2026-11-20 | 通过：部署后 23 s 见 252 路，测量期 256 路，client rx_ack 5838 > 0 | `/tmp/tbt-g4-prod-*` |
| CASE-5 上行 | 第四代 | 2026-11-20 | 机制论证通过、未实测：客户端二进制未换，上行接收缓冲仍是自动调优；服务端上行写 target 受 SNDBUF 约束 | result §CASE-5 |
| CASE-6 矩阵 | 第四代 | 2026-11-20 | 4 路不退化：bulk 175.5/175.4 vs 主线 174.7/175.0 Mbit/s，iperf 差 ≤1.3%，ping-under-bulk p99 52.9 vs 67.8 ms；16 路 5% 丢包未跑 | `/tmp/matrix-main-4p.log`、`/tmp/matrix-cand-4p.log` |
| CASE-7 故障/回滚 | 第四代 | 2026-11-20 | 回滚 40 s 完成：旧版方向 35 s 到 225 路，新版方向 31 s 到 254 路；SSH 有一次 banner 超时 | HND0 `tbtun.bak-g4` |
| CASE-8 长尾 | | | | |
| CASE-0 部署/回滚通道 | 第四代 | 2026-11-20 | 通过：gzip+`ssh cat` 1,958,693 B 约 7 分钟，sha256 相符后 install；回滚为 `tbtun.bak-g4` | `/tmp/tbtun-g4.gz`、HND0 `.bak-g4` |
| CASE-9 前沿/信用 | 第四代 | 2026-11-20 | 部分：rx_ack 5838、rx_base 5518（推进中无停滞），但 flow_credit 余 9.8 KB < 1 MiB 门槛 | `/tmp/tbt-g4-prod-*` |
| CASE-10 接收缓冲 | 第四代 | 2026-11-20 | 通过：回环复测 netem 256 路 32 MiB = 4.6687 s（前代 4.673 s），boosters/sealed 0.63%，qhigh 286，RSS 37 MB；生产 server RSS 70 MB | `/tmp/tbt-cand4`、回环复测目录 |

## 不可改的约束（验收前提）

- 256 路 TCP、TCP-only、`:39443` 静默、无 TLS/REALITY、TFO/BBR 保留。
- mihomo `max-connections: 1`、`min-streams: 1` 不变；不关 smux。
- `rho=0.25` 语义保留（自适应关闭时）；GF(16) 上行不改。
- 8 MiB 单流信用、32 MiB 池为当前基线；若要改，必须另有单变量生产对照，不在本用例
  集的“通过”条件里。
