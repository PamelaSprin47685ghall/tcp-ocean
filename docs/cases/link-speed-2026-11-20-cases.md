# 链路提速验收用例集（2026-11-20）

> 目标：本机 ↔ HND0 生产链路（256 路 TCP、TCP-only、内层 `127.0.0.1`）的下行吞吐
> 从 2026-11-20 实测的好轮 0.21–0.83 MB/s、停摆轮 0 提升到可用量级，且不以缩减
> 路径数、放松断言或改变既有产品策略换取速度。绝对基线随测量时段变化，CASE-1 使用
> 同窗口相对口径，不把任一时段的绝对数写死。
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
> | H2 接收前沿与信用回收 | 前沿步进慢 ⇒ `RxDelivered` 不推进 ⇒ 信用按本地应用写入回收，服务端 TxLimit 停在已交付处 | CASE-9 中 `rx_ack` 步/s 与交付字节率偏差 >50%，或 `RxDelivered` 停滞 >2 s 且 `tx_head − peer_ack` 不为 0 ⇒ H2 成立 |
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
  4. HND0 通道只接受三种已核实的形态：(a) ≤64 KiB 的小文件，传后必须 `sha256sum`
     与本地一致；(b) 远端源码树与本地同一 commit（含 `Trace.FlowGap`）后远端
     `dotnet publish -r linux-x64 -p:NuGetAudit=false -p:TreatWarningsAsErrors=false`；
     (c) 经本机 `17893` 隧道 HTTP 上传到 HND0 loopback 接收器（第九代实测：HND0
     起临时 `python3` 接收器 `127.0.0.1:19601`，本机
     `curl --proxy http://127.0.0.1:17893 -X POST --data-binary`，1 MB 与 3.9 MB
     两次 `sha256` 均相符，3.9 MB 用时 5.94 s），判据为上传耗时 <120 s 且 hash 相符。
     该通道优先用于换 HND0 二进制。大文件 scp、256 KB 分块、无基线远端 publish
     已是失败样本，不再重试。
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
- **expected**：先满足 CASE-12 的停摆率（≤1/10），再比较中位：中位 ≥ 同窗口 HND0
  直连的 1/5，且 ≥ 当次好轮基线的 1.5×；总体相对标准差 < 20%，至少 4/5 收到
  HTTP 200，每轮前后两端都保持 256 路。**绝对门槛 20 Mbit/s 已废弃**：第七代 10 轮
  好轮只有 1.5–4.7 Mbit/s、停摆 5/10，把绝对数写死会把时段性停摆误判成代码退化。
- **rubric**
  - 100：停摆 0/10，中位 ≥ 直连 1/5 且 ≥ 基线 2×，CV < 10%，5/5 收齐或 HTTP 200，双端 256 路。
  - just-PASS 85：停摆 ≤1/10，中位 ≥ 直连 1/5 且 ≥ 基线 1.5×，CV < 20%，≥4/5 为 200，双端 256 路。
  - just-below 80：停摆 2/10，或中位为基线 1.2–1.5×。
  - 60–75：停摆 3–4/10，或 2/5 无响应。
  - 0–50：停摆 ≥5/10。
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
  2. 记录 `srtt_ms`、`minrtt_ms`、`queued`、`tx_head - peer_ack`、`gate1/gate2`
     的增量，其中 `gate2` 必须分别记 pass 与 deny；同时记 `beginfail`、`qhigh`
     的窗口增量。`beginfail` 是用户态发送槽满（`AsyncPathLink`，默认 16 槽），
     `qhigh` 是本轮队列峰值，二者与 `queued=0` 不矛盾，不能只看 `queued`。
  3. 同一时间用 `ss -tinm` 取若干条承载的 `rtt` 与 `cwnd`，取中位。
  4. 对照空闲时（无下载）同一会话的 `srtt/minrtt`。
- **expected**：有载 `srtt_ms` ≤ 1.6 × 空闲 `minrtt_ms`；不再出现 300 ms 干净样本对
  900 ms 有载样本的倍增；`queued` 在采样点不长期为 0 而交付又慢（那是上游供数或信用，
  不是路径排队）；`beginfail` 的每秒增量不随会话年龄单调增长（它增长即用户态发送槽
  被应用写入打满），且 `gate2` 的 deny 不得忽略——若 deny 恒为 0，则 GatePass2Limit
  不是限制，禁止再把它当解释。
- **rubric**
  - 100：有载 SRTT ≤ 1.25 × minRTT，且承载 cwnd 不出现 256 路同步塌到初始值。
  - just-PASS 85：≤ 1.6 × minRTT。
  - just-below 80：1.6–2.0 ×。
  - 50–70：2.0–3.0 ×；0–40：> 3 × 或 SRTT < minRTT。
  - 硬失败：用 `srtt` 的平滑值证明没有排队而不给 `queued`/`cwnd` 证据；跨不同轮比较；
    只报 `queued=0` 就宣称没有发送侧排队，而不给 `beginfail`/`qhigh`/`gate2 deny`。
  - 扣分：未取空闲对照 −10；未取 `queued` 增量 −5；未取 `beginfail`/`qhigh` −8。

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
- **expected**：`rx_ack` 步/s 与交付字节率一致（偏差 ≤20%）；`RxDelivered` 增量与
  `size_download` 偏差 ≤20%，且停摆轮内 `RxDelivered` 不停止推进；
  `held` 不随轮次单调增长。`flow_credit` 只用于上行 TxLimit/TxOffset，不作为下行判据。
- **rubric**
  - 100：三项全满足，且 `tx_head - peer_ack` 与 `RxDelivered` 同步。
  - just-PASS 80：步/s 一致，`RxDelivered` 有一次 ≤1 s 停滞。
  - just-below 75：步/s 与字节率偏差 20–50%。
  - 0–60：`RxDelivered` 长期停滞且 `tx_head - peer_ack` 不上升（发送端被信用卡死）。
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

## CASE-12 下行间歇停摆

- **path**：本机 `17893` → 本地 mihomo `HND0TBT4` → 本地 tbtun client → 隧道 →
  HND0 tbtun server → `127.0.0.1:18443` → 固定源（proof.ovh 100 MB 或 fast.com OCA）。
- **threshold**：85。
- **steps**
  1. 记录两端 hash 与 `ss -tan '( dport = :39443 )'` 的 ESTAB 数，要求 256。
  2. 连续 10 轮，每轮独立进程：
     `curl --proxy http://127.0.0.1:17893 -sS --max-time 10 -o /dev/null -w 'code=%{http_code} size=%{size_download} speed=%{speed_download} time=%{time_total}\n' "$URL"`。
     轮间不加人为间隔；单轮 ≤12 s。
  3. 每轮前后各取一次客户端 stats 一行与
     `ss -tinm '( sport = :18443 )' | grep -o 'bytes_sent:[0-9]*'`。
  4. 停摆轮定义：`size_download = 0`，或 10 s 内 `size_download < 1 MB`；
     `code=200` 但 10 s 截止只收到不足 1 MB 也计入停摆，不得因为 200 而排除。
  5. 停摆轮之后立即做一次 `generate_204`，记录 `time_total` 作为恢复时间。
  6. 原始输出写入独立目录，失败样本保留。
- **expected**：10 轮中停摆轮 ≤1；恢复 ttfb ≤3 s；每轮前后双端 256 路；
  停摆轮的 18443 `bytes_sent` 增量必须记录（用于区分 tbtun 侧与外层）。
- **rubric**
  - 100：停摆 0/10，恢复 ≤2 s，好轮中位 ≥基线 2×。
  - just-PASS 85：停摆 ≤1/10，恢复 ≤3 s。
  - just-below 80：停摆 2/10，或恢复 3–5 s。
  - 60–75：停摆 3–4/10。
  - 0–50：停摆 ≥5/10。
  - 硬失败：把停摆轮从中位数里静默剔除而不单列；用单次幸运轮代表基线；
    只看 `code` 不看 `size`；未记录 18443 增量就断言根因在 tbtun。
  - 扣分：未取恢复时间 −10；未保留停摆轮原始输出 −10；未记录双端连接数 −5。
- **备注**：第七代实测 5/10 停摆，且停摆轮内 tbtun 向 18443 写入 6–11 MB 而 curl 收 0，
  说明停摆在 mihomo 内部而非 tbtun 数据面（result 第七代节）。
  停摆率与稳态中位必须分开报告；CASE-1 的中位数只在停摆率达标后才有意义。

---

## CASE-13 路径池自愈（服务端重启与单路丢失）

- **path**：本机客户端 `/usr/local/bin/tbtun client --paths 256`（由 `tbtun-hnd0`
  单元以 client 模式拉起）与 HND0 服务端单元；确定性前置为
  `tests/e2e/test_async_lifecycle.py` 与新增的路径池单测。
- **threshold**：85。
- **steps**
  1. 记录前置：两端 `sha256sum /usr/local/bin/tbtun`、
     `ss -tn state established '( dport = :39443 )' | wc -l` 为 256、一次
     `generate_204` 成功，并记录客户端进程启动时刻。
  2. 回环床（不依赖 HND0）：同一构建，对服务端进程连续做 3 次主动 restart，
     每次 restart 后每 2 s 读一次客户端 paths 计数，记录“首次 ≥250”与“首次 =256”
     的耗时；3 次都必须 ≤30 s 达 250、≤60 s 达 256。
  3. 生产：同样连续 3 次 `systemctl restart tbtun-hnd0`（HND0 侧），每次在 30 s 内取
     `ss -tn '( sport = :39443 )' | grep -c ESTAB`，必须 ≥250，60 s 内 256；每次恢复后
     做一次 `generate_204`，`time_total` ≤3 s。
  4. 单路断开，两例都必须做，且必须分别记录被杀的 path id（用客户端日志的
     `path N joined` 与两端 `path_tx` 列表反查）：
     (a) **非 0 路径**：在 HND0 用 `ss -K` 关闭一条 `sport = :39443` 的承载，不重启
     客户端，记录 60 s 内 paths 是否回到 256；
     (b) **path 0（握手路径）**：先在本机 `ss -tn '( dport = :39443 )'` 找到最早建立
     的那条承载（即客户端 `path_tx` 列表里缺 0 之前、服务端 `carrier.Paths` 的第一条），
     在 HND0 对应 `sport` 上 `ss -K`，记录 60 s 内 paths 是否回到 256，以及客户端日志
     是否出现 `path 0 joined`。**未做 (b) 时本用例不得判通过**：第十二代在回环上
     两次 `ss -K` 均落在 path 0，结果恒为 255 且池从不补建（见证据表）。
  5. 旧版对照：把本机客户端换回 `f2b63e00…`（`/usr/local/bin/tbtun` 的备份），重复
     第 3 步 3 次，记录旧版是否停在 1 路、`rx_ack`/`frames_in` 冻结、需要人工重启客户端。
     若旧版 3/3 都自愈，则改测“restart + 保留一条半开路径”的确定性回环场景，
     不得因此宣称缺陷不存在。
  6. 每次 restart 记录：客户端 paths 计数时间序列、`rx_ack` 增量、服务端 `sessions`
     与 `estab`、恢复耗时；原始输出写入独立目录，失败样本保留。单次 restart 段 ≤60 s。
- **expected**：新客户端 3/3 在 30 s 内 ≥250、60 s 内 256，单路断开后 60 s 自愈；
  旧版至少出现一次 <256 且需人工干预，或确定性半开场景可复现同一冻结。
  不存在“少路继续跑”被当成正常。
- **rubric**
  - 100：新客户端 3/3 在 20 s 内 256，单路断开 30 s 自愈，旧版冻结可复现，
    恢复后 5 s 内 `rx_ack` 开始增长。
  - just-PASS 85：新客户端 3/3 在 30 s 内 ≥250、60 s 内 256；旧版至少 1 次冻结。
  - just-below 80：3 次中 1 次 30 s 内不足 250，但 60 s 内恢复 256。
  - 60–75：需人工重启客户端才恢复，或单路断开不自愈。
  - 0–60：新客户端停在 <256 且不自愈；或靠减少目标路径数“通过”。
  - 硬失败：用人工重启客户端冒充自愈；少路运行计入样本；只做 1 次 restart 就宣称
    3/3；在 restart 窗口内并发下载或改服务端配置；用旧版二进制冒充新版结果。
  - 扣分：未记录恢复耗时时间序列 −10；未做旧版对照 −15；未保留失败样本 −10；
    未记录两端连接数 −5。
- **备注**：第九代实测旧版客户端在 `--pace 1` 触发的一次 restart 后停在 paths=1、
  大量 CLOSE-WAIT、`rx_ack` 与 `frames_in` 冻结，`systemctl restart/kill` 超时，
  需 `kill -9` 后由 `Restart=always` 拉起。代码核对确认路径池只在启动时建立一次
  （`ids = [| 1 .. min pathCount maxPaths - 1 |]`），`attachJoined()` 只排空 `pending`，
  不比较 `carrier.Paths.Count` 与目标；`Alive` 只要还有一条路径成立，会话就不退出。
  修复契约见 design §13.2：同一 id 重建的 `Incarnation` 单调、后台连接、退避与并发上限。

---

## CASE-14 同会话随运行时长吞吐衰减

- **path**：本机客户端 `tbtun-hnd0` 单元与 HND0 服务端单元；测量入口
  `curl --proxy http://127.0.0.1:17893`。
- **threshold**：80。
- **steps**
  1. 记录前置：两端 `sha256sum /usr/local/bin/tbtun`、
     `ss -tn state established '( dport = :39443 )' | wc -l` 为 256、一次
     `generate_204` 成功、两端 stats 各取一次（含 `rho`、`cell_loss`、`srtt_ms`、
     `minrtt_ms`、`beginfail`）。记录客户端进程启动时刻，会话年龄从此刻算。
  2. 同一 URL（`https://proof.ovh.net/files/100Mb.dat`，记录响应头与大小）连续 10 轮，
     每轮 `--max-time 12`，轮间 0 间隔；每轮记录 `size_download`、`speed_download`、
     `http_code`、`time_starttransfer`。
  3. 每轮前后各取一次两端 stats；每 30 s 取一次 `ss -tinm '( sport = :18443 )'` 的
     `bytes_sent`/`Send-Q` 与 `ss -tn '( sport = :17893 )'` 的 `bytes_sent`。
  4. 若第 10 轮中位吞吐 < 第 1–2 轮中位的 0.5×，在同一会话内重启客户端进程
     （不重启服务端），重复第 2 步 3 轮，记录吞吐是否回到首轮水平。
  5. 记录会话年龄、累计交付字节、`rho` 与 `cell_loss` 的时间序列。
- **expected**：**末两个好轮**的中位吞吐 ≥ **首两个好轮**中位的 0.7×；停摆轮
  （`size=0` 或 12 s 内 <1 MB）不参与衰减比值，只由 CASE-12 计分。好轮少于 4 个时
  衰减判为“不可判定”，不得把停摆当衰减。`rho` 不随会话年龄单调升至 RhoMax（0.5）
  后不再回落；第 4 步重启客户端后吞吐回到首轮水平，说明退化是会话态而非链路态。
- **rubric**
  - 100：10 轮中位衰减 ≤10%，`rho` 全程 ≤0.1，重启客户端后无额外增益。
  - just-PASS 80：末两个好轮中位 ≥ 首两个好轮中位的 0.7×；`rho` 峰值 ≤0.2 或升后能回落。
  - just-below 75：好轮衰减 0.5–0.7×，或 `rho` 单调升至 ≥0.3 不回落。
  - 40–70：衰减 <0.5×，重启客户端可恢复，即“越用越慢”。
  - 0–40：衰减 <0.2×，或 0 字节轮占比 >1/10 且不恢复。
  - 硬失败：只测 1–2 轮就下结论；跨 URL 或跨并发数比较；把时段整体低谷当会话衰减
    （必须同会话、同 URL、无重启）；把停摆轮的 0 字节直接代入衰减比值，导致任何
    停摆时段都必然“衰减”。
  - 扣分：未记录会话年龄 −10；未取 `rho`/`cell_loss` 序列 −10；未做重启客户端对照 −15；
    未记录每轮 HTTP 码 −5。
- **备注**：第九代 restart 后首轮 9.55 MB/s、8 轮中位 4.7 MB/s；第十代同机 10 轮中位
  1.1 Mbit/s；第十一代基线好轮中位 0.83 MB/s。三次测量跨时段，不能直接当衰减曲线，
  但“重启后快、用久变慢”在两代里重复出现，必须由本用例区分会话衰减与时段低谷。
  第十代生产 `rho` 曾达 0.494 而 restart 后为 0.030，是本用例的核心观测量。

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
| CASE-8 长尾 | — | — | 未执行：截至第十二代没有慢消费长尾测量；其余证据行不得视为已覆盖本项 | — |
| CASE-0 部署/回滚通道 | 第四代 | 2026-11-20 | 通过：gzip+`ssh cat` 1,958,693 B 约 7 分钟，sha256 相符后 install；回滚为 `tbtun.bak-g4` | `/tmp/tbtun-g4.gz`、HND0 `.bak-g4` |
| CASE-9 前沿/信用 | 第四代 | 2026-11-20 | 部分：rx_ack 5838、rx_base 5518（推进中无停滞），但 flow_credit 余 9.8 KB < 1 MiB 门槛 | `/tmp/tbt-g4-prod-*` |
| CASE-10 接收缓冲 | 第四代 | 2026-11-20 | 通过：回环复测 netem 256 路 32 MiB = 4.6687 s（前代 4.673 s），boosters/sealed 0.63%，qhigh 286，RSS 37 MB；生产 server RSS 70 MB | `/tmp/tbt-cand4`、回环复测目录 |

| CASE-9 口径更正 | 第五代 | 2026-11-20 | `flow_credit` 打印的是 `TxOffset/TxLimit`（上行方向），第四代「剩 9.8 KB」是误读，「信用耗尽」未发生。CASE-9 的 PASS 线应改为「下行 `RxDelivered` 的推进速率与交付字节率偏差 ≤20%」，不再用 flow_credit 余量 | `docs/cases/link-speed-2026-11-20-s7.md` |
| CASE-5 上行不退化 | 第七代 | 2026-11-20 | 通过：5,000,000 B 上传 3/3 全部送出，约 4.0 Mbit/s；2/3 响应超时属同一停摆（下行方向） | `/tmp/up5mb.bin` |
| CASE-9 下行停摆口径 | 第七代 | 2026-11-20 | 停摆轮内 `rx_ack` 仍 +282…+382 步/10 s、`RxDelivered` +5.7…+8.0 MB，H2 被证伪；停摆发生在 mihomo 内部 | `/tmp/tbt-g7-1795165621` |
| CASE-12 下行间歇停摆 | 第七代 | 2026-11-20 | 失败：10 轮 0 字节 5/10；停摆轮 tbtun 向 18443 写入 6–11 MB 而 curl 收 0；四窗口字段与 `max-connections: 4` 均无效 | `/tmp/tbt-g7-1795165621`、`/tmp/mihomo-g7b` |
| CASE-6 16 路 5% 丢包 | 第七代 | 2026-11-20 | 部分：bulk up/down/再 down = 144.8/148.3/147.8 Mbit/s，ping-under-bulk p99 121.4 ms；iperf 段被 180 s 上限截断 | `/tmp/tbt-g7-16p-loss.log` |
| GatePass2Limit 床 | 第七代 | 2026-11-20 | 未执行：官方床每路 BDP≈250 KiB 与 256 KiB 同量级，降 32768 只会提前停发；正确床需每路 1 Mbit/s×314 ms | result 第七代节 |
| S-7 细采样 | 第五代 | 2026-11-20 | 隧道 13.05 / 本机直连 44.9 / HND0 直连 41.9 Mbit/s（同一 URL）；18443 send-q 全 0；服务端 `flowread=2958/52561777/2808`、`queued=0`、grant 剩 501 步、flow credit 剩 3.85 MB。结论：限制在 mihomo VLESS 入站→DIRECT 的单流窗口，不在 tbtun 的承载、解码或信用 | `/tmp/tbt-s7-1795164088`、`/tmp/tbt-s7-server-1795164210` |
| 第六代 smux 窗口 A/B | 第六代 | 2026-11-20 | 未通过：`max-stream-receive-window: 4194304` 5 轮 0/0.41/0/0.63/0.14 MB/s，无收益；已回滚。字段存在且 `mihomo -t` 通过，但不是限制 | result 第六代节 |
| 第六代 关闭 smux | 第六代 | 2026-11-20 | 不可用：大文件单流中位 0.43–0.50 MB/s、0 字节轮 1/8（smux 开 2/3），但短请求 3/3 `code=000`、ttfb 10 s 超时；已回滚 smux 开 | result 第六代节 |
| CASE-12 第八代复测 | 第八代 | 2026-11-20 | 复现：0 字节轮内 18443 +6.5 MB/15 s、17893 accept `bytes_sent` 恒 39 B；HND0 `cell_loss 16.3%`、`srtt 1412/minrtt 726`、`tx_head=peer_ack` | `/tmp/tbt-g8-audit-1795166455`、`/tmp/tbt-g8-flow-1795166514` |
| H7 `min-streams=0` | 第八代 | 2026-11-20 | 证伪：17901(min0)/17902(min1)/17893 三路同步停摆，3 轮无区分 | `/tmp/tbt-g8-p2-1795166731` |
| 窗口 `initial`+`max` 4 MiB | 第八代 | 2026-11-20 | 未观察到收益：17911/17912 三轮 0/131K/459K 对 0/377K/0，坏时段不可判定 | `/tmp/tbt-g8-winr-1795166926` |
| goroutine dump | 第八代 | 2026-11-20 | 停摆栈：Relay→`smux.stream.Read→waitRead`、`recvLoop io.ReadFull`；metacubex/smux v0.0.0-20260105 + sing-mux v0.3.12 | `/run/hnd0tbt4.log:515-780` |
| CASE-0 隧道上传 | 第九代 | 2026-11-20 | 通道成立：1 MB 与 3.9 MB 经 17893 POST 到 HND0 接收器，sha256 均相符，3.9 MB 用时 5.94 s；同期 scp 仍 11 KB/s 且截断 | HND0 127.0.0.1:19601；result §9.1 |
| CASE-12 新会话 | 第九代 | 2026-11-20 | restart 后 8 轮 9.55/7.86/5.23/1.46/0/4.24/0/6.28 MB/s，停摆 2/8；`--pace 1` 触发客户端 paths=1 冻结，30 s 未恢复，`su kill -9` 后恢复 | result §9.2–9.3 |
| CASE-2 证据冲突 | 第九代 | 2026-11-20 | 仍未重测：第四代 18.42% 与第六代 `retrans_total=4`、`app_limited 225/256` 矛盾未消；本代只取 cell_loss 0.0002–0.0050 | result §9.2 |
| CASE-13 路径池自愈 | | | 待执行：新客户端 3/3 restart 单测与生产、单路断开、旧版对照 | |
| CASE-13 路径池补建 | 第十代 | 2026-11-20 | 部分：部署 22 s 到 255；服务端 restart 三次中两次 `first250=30/27 s`，另一次（修复前构建）45 s 只到 210；「单路 `ss -K` 1 s 回到 256」在第十一代同一动作下未复现 | `/tmp/tbt-g10-manual`、生产 `ss` |
| CASE-13 复测 | 第十一代 | 2026-11-20 | 未通过：默认 `--join-concurrency 32` 三次 restart `first250=40.0/36.9/40.0 s`、`first256=41.0/37.9/41.0 s`；单路 `ss -K` 后 60 s 恒定 255，sync 模式无半开检测；`--join-concurrency 64` 后 `first250=34.8/28.1 s`、初次建池 45→14 s | `/tmp/tbt-g11-case13b.sh`、生产 `ss`/journal |
| CASE-12/1 第十代 | 第十代 | 2026-11-20 | 10 轮 2.19/0.79/3.47/0/3.06/6.60/0/0/0/13.43 MB，停摆 5/10，中位约 1.1 Mbit/s；时段整体低，不可与第九代峰值比较 | bg_46、bg_47 |
| CASE-0 部署通道复核 | 第十二代 | 2026-11-20 | 通过：本机客户端 `install` + `restart`，旧版备份 `tbtun.bak-g12`；回滚后 sha=7c48936e、`tbtun-hnd0` active、`ss -tn state established \| grep -c ':39443'` = 256 | `/tmp/tbt-g12-ab3-1795181126` |
| CASE-12/1 第十二代 | 第十二代 | 2026-11-20 | 未通过：10 轮 16.61/5.08/0/7.41/0.18/8.18/0/7.11/0/3.11 MB，停摆 3/10，好轮中位 0.605 MB/s；修复（`stallNs` 20→2 ms）无用户可见收益，已回滚 | `/tmp/tbt-g12-ab3-1795181126` |
| CASE-9 三分账 | 第十二代 | 2026-11-20 | 部分：短窗口 `RxDelivered` 6,252,876 B 对 curl 6,209,518 B（差 0.7%）；长窗口 10 轮 `RxDelivered` 121.4 MB 对 curl 47.7 MB（差 73.7 MB），积压在 mihomo 层 | `/tmp/tbt-g12-ab3-1795181126`、第十二代 result 节 |
| CASE-13 半开检测 | 第十二代 | 2026-11-20 | 检测生效、补建缺失：回环 `ss -K` 后两端 512→510、客户端 `paths=256→255` 并 60 s 恒定；两端 stats 的 `path_tx` 列表从 1 开始、不含 0，说明丢失的是握手路径 path 0，而 `PathPool` 只补 id 1..255 | `/tmp/tbt-g12-loop`（`client2.log`、`server.log`） |
| 测量陷阱 | 第十二代 | 2026-11-20 | `ss -tn state established '( dport = :39443 )'` 在本机恒返 0；路径计数须用 `ss -tn state established \| grep -c ':39443'`（实测 256） | 本代 |
| CASE-13 路径池自愈 | 第十三代 | 2026-11-20 | 部分通过：单路自愈（杀非 0 0.52 s、杀 path 0 1.55 s 回到 256；回环 `path 0 joined, paths=256`；部署后 9 s 建池 256）。**3 次服务端 restart 未在本代重测**，沿用 g11 的 28–35 s（其中一次 >30 s），不得写成 3/3 | `/tmp/tbt-g13-loop`、`/tmp/tbt-g13-prod-result.json` |
| async 小池启动 | 第十三代 | 2026-11-20 | 未修：默认 async 下 `--paths 2` 首轮 paths=0 后 Exit 75；`--paths 256` 45 s 稳定 256，生产 256 路不受影响 | `/tmp/tbt-g13-mini` |
| CASE-14 会话衰减 | 第十五代 | 2026-11-20 | 未通过：10 轮 13.57/0/6.14/0/4.82/0/3.31/0/0/2.29 MB，停摆 5/10；好轮 1.13→0.51→0.40→0.28→0.19 MB/s 单调降至 0.17×（<0.7×）；`rho 0.105→0.128`、`beginfail 0→3519` | `/tmp/tbt-g15-base`、result §14.5 |
| GatePass2 / 发送侧供给 | 第十五代 | 2026-11-20 | GatePass2 证伪：生产 `gate2=392304/0`（deny=0），`beginfail` 来自 16 槽；V1（`--notsent-lowat 32768 --tx-slots 32`）触发既有客户端冻结：服务端 `sessions=0`、客户端 `paths=1`、`beginfail` 1.22 亿、RSS 22 GB，`ss -K` 后 32 s 恢复，判不可判定并回滚 | `/tmp/tbt-g15-prod`、result §14.2/14.6 |
| CASE-13 三次 restart（第十六代） | 第十六代 | 2026-11-20 | 通过：客户端 `734318db`，服务端 `3f05c4a0`。r1 `first250=25 s`/`first256=26 s`、r3 `32 s`/`36 s`；r2 建池 24 s 后被 r3 打断，未取得数值。三次均 `session lost → 新 PID`，无 `paths=1` 忙循环；RSS 37 / 45 / 70 MB；最终 256 路、204/0.751 s | `journalctl -u tbtun-hnd0`、生产 `ss`、result §15.4 |
| CASE-13 半开路径（第十六代） | 第十六代 | 2026-11-20 | 部分：`TCP_USER_TIMEOUT` 已设（30 s，`--user-timeout-ms` 可关）。进程级实验 2000 ms 设 2.484 s `ETIMEDOUT`、关闭 8.002 s 全 `EAGAIN`；**净丢 FIN/RST 的端到端半开场景未执行**，不得写成已通过 | `/tmp/tbt-ut.py`、result §15.4/15.5 |
| CASE-1 发送侧供给（第十六代） | 第十六代 | 2026-11-20 | 未执行：前置崩溃已修，但 HND0 服务端候选未部署；判据与切片见 plan §14 T10/T11。本代无吞吐提速声明 | result §15.5 |

## 不可改的约束（验收前提）

- 256 路 TCP、TCP-only、`:39443` 静默、无 TLS/REALITY、TFO/BBR 保留。
- mihomo `max-connections: 1`、`min-streams: 1` 不变；不关 smux。
- `rho=0.25` 语义保留（自适应关闭时）；GF(16) 上行不改。
- 8 MiB 单流信用、32 MiB 池为当前基线；若要改，必须另有单变量生产对照，不在本用例
  集的“通过”条件里。
