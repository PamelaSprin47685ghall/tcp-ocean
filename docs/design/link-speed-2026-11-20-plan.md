# 链路提速：编排计划（2026-11-20，第三代修订）

> 第二代把关键路径定为“本机客户端单变量 A-B-A”，前提是生产链路可测量。第三代实测推翻了
> 这个前提：HND0 的 sshd 与 tbtun server 都在 TCP 握手后不读（22 与 39443 均是 0.31 s
> 握手、3 s 零字节），`ping` 仍 2/3 到达、RTT 329 ms 稳定。生产客户端 `rx_ack=1`、
> `tx_head = peer_ack`、`flow_credit` 只用 8433 B / 8 MiB，说明接收侧没有数据可解、
> 发送侧没有数据可发。与此同时，`tbtun --version` 证明生产 `f2b63e00` 不含工作树的
> `paceOn`/`--pace`、`roundCap 512`、`GatePass2Limit 32 KiB`。
>
> 因此第三代的关键路径不再以 HND0 为前提：先用本机回环复现 256 路下行，分离“主机
> 卡死”与“产品缺陷”，再决定是否值得改代码；HND0 恢复作为可并行的独立分支，它的
> 失败不阻塞前一条。
>
> **第四代更正（2026-11-20）。** “HND0 卡死”前提已过期：`45.11.1.62:22` 在 2.4 s
> 内返回 SSH banner，`:39443` 已建 256 路。候选 `661ad970` 已部署 HND0 并测得生产
> 中位 11.8 Mbit/s（旧版 3.5，3.4×）。下文 P5 与“独立分支”按已恢复读。

## 0. 切片、输入、输出、停止条件

| 切片 | 输入 | 输出 | 停止条件 |
|---|---|---|---|
| P0 状态固定 | 两端 hash、`tbtun --version`、单元与 `ss`、工作树 diff | design §0 的“哪段代码在跑” | 生产二进制被证明不含候选改动 |
| P1 回环基线（已完成） | HEAD 构建、`unshare -rn` + netem | 无 netem 757 Mbit/s、netem 12 Mbit/s 的对照 | 已出数，见 result.md 第三代 |
| P2 内存与卡死机制（已完成） | P1 的 RSS | server RSS 104–111 MB | 未见无界增长 |
| P3 pace 单变量（已完成，证伪） | 同构建 `--pace 0/1` | 22.3896 s 对 22.3877 s | 无收益 |
| P4 前沿定量（已完成，证伪） | `stallNs` 20→2 ms、等待 1 ms | 11.305 s 对 11.310 s、11.308 s | 无收益 |
| P4b 根因修复（已完成） | flow socket `SO_RCVBUF` | 11.31 s→2.573 s；4 路/256 路 4.8× | 无 netem 不退化、单测 387 全过 |
| P5 HND0 恢复（已完成） | 跳板、备用端口、云 API | 22 端口 2.4 s 返回 banner；直接 scp 实测 11 KB/s | 已转 CASE-0；大文件改 gzip + `ssh cat` + sha 校验 |
| P6 工作树收口（已完成） | 21 项 diff | 补丁存 `/tmp/tbt-narrow-1795161697`；工作树退回 HEAD 后只留 rcvbuf 修复、启动日志、三个单测 | 候选单变量；敏感默认与 pace 不进入候选 |
| P7 生产切换（已完成） | 候选 `661ad970` + HND0 `rmem_max=16777216` | 换二进制、5 轮测量、重传与前沿指标 | 3.4× 且无 0 轮；CASE-2 18.4% 未达 5%，保留并标条件性上线 |
| P8 文档与证据 | P1–P7 原始输出 | cases 的 H4/主机健康/本地度量 CASE、result、本文件 | 证据表逐行有原始目录 |

关键路径：P0 → P1 → {P2, P3, P4}（同一测试床互斥，串行）→ P6 → P7 → P8。
P5 与 P1–P8 无共享可变状态，随时可并行；它只受外部主机可达性约束。

## 1. 状态与转移

```
phase     ∈ {fixed, loopback, diaged, candidate, matrix, shipped, rolled-back}
binary    = sha256(/usr/local/bin/tbtun)
candidate = sha256(候选 AOT)          # 仅 candidate 之后存在
run       = (slice, binary, paths, rate_mbit, loss, owd_ms, bytes, secs,
             rss_kib, svc_us_max, rx_ack, delivered_bytes, verdict)
verdict   ∈ {undecided, h1, h2, h3, h4}
```

- `fixed → loopback`：design §0 落盘，生产 hash 与工作树分类已记录。
- `loopback → diaged`：P1 四批 ≥254 路、P2 给出 RSS 上界或增长行号、P3/P4 出数；
  守卫：单轮 ≤60 s，测试床 `flock` 获取成功。
- `diaged → candidate`：至少一条假设被判真，且修复落在 `SessionCarrier`/`Server`/`RxHalf`/
  `SocketTuning`（含 `FlowBridge` 的 socket 设置）之一；否则 `diaged → rolled-back`，只写证伪。
  第四代的真实修复在 `SocketTuning.local`，本条在第四代修订时补入，否则按字面读该转移非法。
- `candidate → matrix`：构建 0 错、全量单测全绿、回环四批 256 路。
- `matrix → shipped`：CASE-6 矩阵中位不降、无断言放宽；本机 `install` 与回滚各 <60 s。
- `matrix → rolled-back`：30 s 内无 256 路，或 CASE-6 中位下降 >3%。

## 2. 安全不变式

- **S-1** 生产二进制在 CASE-6 通过前不动；任何 `install` 前必须已有同名 `.bak` 且 hash 已记录。
- **S-2** 一次只改一个变量：`roundCap`/`GatePass2Limit` 不与 `paceOn` 同批；两者都不与协议面改动同批。
- **S-3** 测试床由 `run.sh` 自己 `flock /tmp/tbtun-netns.lock`；不套外层锁，不删他人 netns，不杀非本会话进程。
- **S-4** 单次测量 ≤60 s，`curl --max-time` ≤15；不把多段长步骤拼进一条命令。
- **S-5** 协议面不动：256 路、`:39443`、TCP-only、8 MiB 单流信用、`rho=0.25`、GF(16)。
- **S-6** HND0 传二进制用 `gzip -9` + `ssh cat`（实测 11 KB/s，1.96 MB 约 7 分钟）；传后必须核 sha256 与解压后字节数，不等即拒绝 `install`。直接 scp 3.9 MB 在 5 分钟内只到 261 KB，不得再作为通道；不再使用远端 publish（远端源码树基线不一致）。

## 3. 活性不变式

- **L-1** 每次测试床启动 30 s 内必须见 256 路；否则判定该轮无效，不重试同一参数。
- **L-2** 任一 P 切片失败即落盘原因并转下一个无依赖切片，不空等外部主机。
- **L-3** 每个切片的原始输出写入独立目录并保留，失败样本同样保留。
- **L-4** 不设第二批意见：一个切片的数落盘即出结论，缺口标“未测”。

## 4. 串行段与缩短

串行链 P0→P1→P2/P3/P4→P6→P7→P8 的串行根因是**单一测试床**而非逻辑依赖。缩短手段：

1. P2/P3/P4 共用同一次测试床启动，减少建网与 256 路建立（约 15–25 s）的重复开销。
2. P5 与本地链路完全解耦，立即并行；它的成败不参与 P1–P4 的判定。
3. P6 的 diff 分离在读代码时静态完成，不占测试床时间。
4. P7 前先做 CASE-6 矩阵（可离线），把生产窗口压缩到一次 `install` + 一次回滚演练。

## 5. 停止条件与交付证据

- 任一守卫失败：停在该切片，写原始输出，不推进。
- 全部通过：更新 `docs/cases/link-speed-2026-11-20-result.md` 与证据表，保留 `/tmp` 目录路径。
- 清理：临时二进制、`/tmp` 目录、影子监听；复核两端 hash 与连接数后写入结果。

---

# 附录：第二代计划（已被上文取代，仅作历史）

> 第一代计划的关键路径是“服务端换二进制 + `--pace 1`”。本代已证伪该前提的可用性：
> 3.9 MB 二进制三次传不到 HND0（scp 两次截断、256 KB 分块 180 s 只成功 2/15、
> 远端 publish 300 s 未完成），远端源码树与本地不同 commit。因此本代把关键路径改为
> **本机客户端单变量 A-B-A**：下行接收重组走 `RxHalf`，换本机二进制即可验证接收侧，
> 部署与回滚各 <60 s，不需要向 HND0 传任何文件。服务端 `--pace` 只在 CASE-0 的
> 小文件或同基线通道成立后才进入计划。

## 1. 切片与责任

| 切片 | 输入 | 输出 | 停止条件 |
|---|---|---|---|
| T0 通道核验 | 两端 hash、单元状态、`ss` 连接数 | CASE-0 评分与可用通道结论 | 本机通道可用即通过；HND0 通道不可用则明确排除服务端变量 |
| T1 基线采样 | 同一 URL、现有 `f2b63e00` 二进制 | 5 轮 ≤15 s 的 curl + 每秒客户端 stats + 首尾服务端 stats | 原始目录写入 `/tmp/tbt-diag-*` |
| T2 判定 | T1 数据 | H1/H2/H3 的证据与置信度 | 至少一条命题被判真，或全部证伪 |
| T3 修复 | 判定结果 | 最小代码改动，或“不改”的结论 | 构建 0 错、单测全绿、回环四批 256 路 |
| T4 本机 A-B-A | 候选二进制 + 备份 | 交错 A-B-A 各 3 轮 | 中位 ≥1.5× 基线且无 0 轮，或 60 s 内回滚 |
| T5 收尾 | 全部原始输出 | result 文档与证据表 | 生产 hash 与连接数复核 |

## 2. 状态与转移

```
phase    ∈ {idle, diag, judged, built, loopback, ab, shipped, rolled-back}
binary   = sha256(/usr/local/bin/tbtun)   # 当前生产二进制
candidate= sha256(候选)                   # 仅在 built 后存在
samples  = [ (t, size, time, code, rx_ack, rx_base, credit, held, srtt, cell_loss) ]
verdict  ∈ {undecided, h1, h2, h3, none}
```

转移：

- `idle → diag`：CASE-0 本机通道可用，记录两端 hash 与 256 路。守卫：未在跑 mux；
  无并发下载。
- `diag → judged`：5 轮原始输出落盘；每轮 ≤15 s；轮前轮后连接数都 ≥254。
- `judged → built`：判定给出可本地验证的代码缺陷；否则 `judged → rolled-back`，
  不改代码，只记录证伪。
- `built → loopback`：`dotnet build Tbt.slnx -c Release -p:NuGetAudit=false` 0 错，
  全量单测全绿，`tests/e2e/test_async_lifecycle.py` 四批 256 路。
- `loopback → ab`：`stop → install → start` 全程 <60 s，30 s 内见 256 路。
- `ab → shipped`：交错 A-B-A 的 B 段中位 ≥1.5× A 段，且无 0 字节轮。
- `ab → rolled-back`：任一 B 轮 30 s 内无 HTTPS 握手，或连接数 <254；
  `install` 回 `<candidate>.bak` 并复验 256 路。

## 3. 安全不变式

- **S-1 回滚先于部署**：每次 `install` 前必须存在同名 `.bak`，且其 sha256 已记录。
- **S-2 单变量**：一次只换一端二进制；服务端 `--pace` 不与客户端改动同轮。
- **S-3 测量隔离**：同一 URL、同一时段、交错 A-B；宿主暂停或日志缺口段作废。
- **S-4 通道纪律**：HND0 只接受 ≤64 KiB 小文件（传后核 sha256）或同基线远端 publish；
  大文件 scp 与 256 KB 分块不再重试。
- **S-5 协议面不动**：256 路、`:39443`、TCP-only、8 MiB 单流信用、`rho=0.25`、
  GF(16) 上行保持。
- **S-6 随时可停**：任一守卫失败停在该阶段，保留原始输出，不推进。

## 4. 活性不变式

- **L-1** 每次部署后 30 s 内必须见 256 路；否则立即回滚。
- **L-2** 回滚是文件替换加重启，不依赖状态迁移，<60 s。
- **L-3** 每轮测量是独立进程，`--max-time 15`；单条命令不拼接多个长步骤。
- **L-4** 判定不等第二批意见：T1 数据落盘即出结论，缺口以“未测”记录。

## 5. 关键路径与缩短

串行链：T0 → T1 → T2 → T3 → T4 → T5。缩短手段：

1. T0 与 T1 共用一次两端 hash 与连接数采集。
2. T1 的 15 s curl 窗口内不编译；窗口外并行读代码，构建与采样交错。
3. 不设“先部署服务端再测客户端”的波次；服务端变量在 CASE-0 通过前不存在。
4. T4 用 A-B-A 三段一次跑完，每段 3 轮 ×15 s，段间 5 s，不往返多轮。
5. 失败即回滚，不做“再试一次看是不是偶然”；重复测量只在 A-B-A 结构内。

## 6. 停止条件与交付证据

- 任一守卫失败：停在该阶段，记录原始输出，不推进。
- 全部通过：更新 `docs/cases/link-speed-2026-11-20-result.md` 与证据表，
  保留 `/tmp` 原始目录路径。
- 清理：临时二进制、`/tmp` 目录、任何影子监听；生产两端 hash 与连接数复核后
  写入结果。

---

# 第五代：S-7 生产细采样与瓶颈归属（2026-11-20 16:40）

P0–P8 已完成，生产为 661ad970、256 路、下行中位 11.8 Mbit/s。第五代不改已完成项，
只补一个尚未做的判定：这 11.8 的限制量在哪一段。依据是 design §9：信用按「写入
本地应用 socket 的字节」回收，8 MiB 信用周转 5.4 s 而 RTT 只有 329 ms，差额只能
落在解码 → Reserve → 本地 socket 这一段。三条候选与判据见 design §9.4。

## S-7 生产细采样（唯一的测量切片）

| | |
|---|---|
| 输入 | 生产两端 661ad970、256 路；一个固定的 fast.com OCA URL；`/tmp/tbt-s7-<epoch>/` |
| 动作 | 一轮 `curl --proxy 127.0.0.1:17893 --max-time 15`；同一窗口每秒取客户端 stats 一行；每秒取 `ss -tinm '( sport = :18443 )'` 的 `send-q`、`bytes_acked`；窗口首尾各一次服务端 stats |
| 输出 | `curl.txt`、`client.NN.txt`、`sock.NN.txt`、`server.before.txt`、`server.after.txt`、`verdict.txt` |
| 停止条件 | 单次命令 ≤60 s；原始文件全部落盘即停，不重试同一参数 |

## 状态与转移

```
s7.phase   ∈ {sampling, judged}
s7.verdict ∈ {undecided, c1, c2, c3}

sampling → judged   ：curl 与两端 stats 落盘；轮内双端 256 路
judged → c1         ：18443 的 send-q 中位 > 256 KB 且 bytes_acked 增量 ≈ 交付量
judged → c2         ：send-q 低位，且 rx_ack 步/s × 每步字节 > 实际交付字节率
judged → c3         ：send-q ≈ 0、tbtun Write 不阻塞，而本地代理出站仍低于目标
```

每一条判定都必须指向具体原始文件；单点读数不算判定，取中位。

## 安全不变式

- **S-7.1** S-7 不改任何生产状态，不部署、不重启、不改本地代理配置。
- **S-7.2** 判定为 C1 时，先只改本地代理参数并做同 URL 交错对照；tbtun 二进制不动。
- **S-7.3** 判定为 C2 时，先在回环床复现；复现不了则不改生产代码，只记录未复现。
- **S-7.4** 任何部署前已有 `.bak` 且 sha256 已记录；回滚 <60 s。
- **S-7.5** 协议面不动：256 路、`:39443`、TCP-only、8 MiB 信用、`rho=0.25`、GF(16)。

## 活性不变式

- **L-5** 采样窗口内若双端路数 <256，该轮作废并记录，不用少路数据判定。
- **L-6** 每次测量是独立进程，`--max-time 15`；不把多个长步骤拼进一条命令。
- **L-7** 判定不等第二批意见；落盘即出结论，缺口记“未测”。

## 关键路径与串行段

S-7 是唯一的串行前置，其余全部依赖它：C1 分支改本地代理参数、C2 分支回环复现、
C3 分支查 smux。串行根因是「判定必须先于修复」这一逻辑依赖，不是测试床；采样
本身 15 s，判定读文件即可，不需要第二波。

---

# 第七代：停摆优先的计划（2026-11-20 17:06）

> 第六代结束时生产是「已回滚、无候选上线」：`/run/hnd0tbt4.yaml` = `fe5dab8b`、
> 本机 `tbtun` = `f2b63e00`、服务端 `661ad970`、256 路 ESTAB。第七代不继承
> 「13 Mbit/s 是稳态」的前提：同一服务端二进制在两个独立源上都出现 0 字节轮
> （design §11.2）。本文件上文各代的关键路径与状态机只保留为历史。

## 0. 判据顺序

1. 先量化停摆：10 轮 × ≤12 s，记录 0 字节轮数与停摆后的恢复 ttfb（CASE-12）。
2. 在**同一停摆窗口**内取增量，判定 S1/S2/S3（design §11.4）。
3. 只对判真的那一条做最小修复；回环床能复现才改生产默认。
4. 停摆率达标后才做稳态 A/B（CASE-1）。

## 1. 切片

| 切片 | 输入 | 输出 | 停止条件 |
|---|---|---|---|
| T0 状态固定 | 两端 hash、yaml sha、单元、`ss` | 本文 §2 的 state | 与第七代交接一致 |
| T1 停摆率基线 | 17893 + 固定源 | 10 轮 curl、每秒客户端 stats、双端连接数 | 原始目录落盘；单轮 ≤12 s |
| T2 停摆捕获 | T1 中至少一个 0 字节轮 | 停摆前后各一行客户端 stats、18443 `ss`、服务端 stats（SSH 可用时） | 增量可取；服务端缺失则标注 |
| T3 归因 | T2 增量 | S1/S2/S3 之一，或「不可判」 | 命中 design §11.4 的一行 |
| T4 回环复现与修复 | T3 结论 | 回环复现（能/不能）、最小补丁或「不改」 | 构建 0 错、单测全绿 |
| T5 生产单变量 | T4 候选 + 备份 | install/restart、CASE-0 计时、CASE-12 复测 | 停摆率不劣化，否则回滚 |
| T6 稳态与文档 | T5 数据 | CASE-1/2/3/4/5/9 证据行、result、cases | 证据表无空白或明确标「未测」 |

关键路径：T0 → T1 → T2 → T3 → T4 → T5 → T6。T2 的服务端增量依赖 SSH；
SSH 不可用时不阻塞 T3，只用客户端侧判据，并在证据里标注「服务端窗口缺失」。

## 2. 状态

```
phase         ∈ {fixed, sampled, stalled, attributed, built, shipped, rolled-back}
binary_local  = sha256(/usr/local/bin/tbtun)
binary_remote = sha256(HND0 /usr/local/bin/tbtun)
本地代理_cfg    = sha256(/run/hnd0tbt4.yaml)
stall         = (t_start, t_end, src, size, code,
                 rx_ack_before, rx_ack_after,
                 flowread_before, flowread_after,
                 tx_head, peer_ack, queued, srtt, minrtt)
verdict       ∈ {undecided, s1, s2, s3, inconclusive}
```

转移：

- `fixed → sampled`：10 轮原始输出落盘，双端 256 路每轮前后不变。
- `sampled → stalled`：至少一轮满足 0 字节定义；否则直接 `sampled → shipped`
  （无可复现停摆，转稳态验证）。
- `stalled → attributed`：T2 增量齐全（客户端必需、服务端可选），命中 design §11.4 一条。
- `stalled → inconclusive`：三条判据都不命中；写入「不可判」并停，不再猜。
- `attributed → built`：T4 给出补丁，或明确「回环不复现、不改生产」。
- `built → shipped`：CASE-0 通道可用，`install` 后 30 s 见 256 路。
- `shipped → rolled-back`：停摆率劣化，或 30 s 内无法完成一次 HTTPS 握手。

## 3. 安全不变式

- **S-10 归因先在窗口内**：S1/S2/S3 只用同一停摆窗口内的增量；累计值只作跨轮对照。
- **S-11 停摆轮单列**：0 字节轮不进入 CASE-1 中位数，必须单列计数与恢复时间。
- **S-12 单变量**：`GatePass2Limit`、`max-stream-receive-window`、`FlowBridge` 读预算、
  客户端 rcvbuf 不得同批上线。
- **S-13 回滚先于部署**：`install` 前同名 `.bak` 与 sha256；
  HND0 侧 `tbtun.bak-g4` 与 `/run/hnd0tbt4.yaml.bak-g6` 保留。
- **S-14 测量隔离**：同一源同一时段交错；不并发下载；宿主暂停或日志缺口段作废。
- **S-15 协议面不动**：256 路、`:39443`、TCP-only、8 MiB 信用、`rho=0.25`、GF(16)。

## 4. 活性不变式

- **L-10** 每轮测量 30 s 内必须见 256 路，否则该轮无效。
- **L-11** SSH 不可用不阻塞客户端侧判定；服务端字段缺失只标注，不用累计值补。
- **L-12** 每个切片原始输出独立目录，失败样本保留。
- **L-13** 一次测量一个进程，`--max-time ≤12`；单条命令 ≤60 s。
- **L-14** 判定不等第二批意见，落盘即结论，缺口记「未测」。

## 5. 关键路径与串行分数

T1（10 × 12 s ≈ 120 s）与 T4（回环 3+3 轮）是仅有的两段长时间串行；其余是读文件
与单次部署。串行根因是「同一时刻只能有一个下载测量占满隧道」，不是逻辑依赖。
缩短：

1. T1 固定源与固定目录，轮间不加人为间隔；不重试失败轮。
2. T2 的服务端快照与 T1 的最后一轮合用同一窗口，不单独起下载。
3. T4 的回环两侧共用一次 `run.sh` 启动，用 `TBT_BIN` 指向两个构建。
4. T5 的 CASE-12 复测复用 T1 的结构，不另写脚本。

## 6. 工人分工（单板，无并发写者）

| 角色 | 责任 | 输入 | 输出 | 停止条件 |
|---|---|---|---|---|
| 测量员 | T1/T2/T5 | 生产状态、固定源 | 原始目录 | 每轮 ≤12 s、256 路 |
| 代码员 | T4 | T3 结论 | 补丁或「不改」 | 构建 0 错、单测全绿 |
| 文档员 | T6 | 全部原始输出 | result/cases 行 | 无静默空白 |

生产隧道与本地代理配置是共享可变状态，**不设并行写者**；只读测量可以并行，
写操作串行。角色之间只通过原始目录交接，通信量 O(1)。

---

# 第八代：smux stream 投递停摆的单变量对照（2026-11-20 17:25）

第七代把停摆定位到本地代理内部；第八代用 goroutine dump 钉到
`smux.(*stream).waitRead`，并给出唯一未试过的配置候选 `min-streams: 0`（design §12）。

## 0. 判据顺序

1. 隔离实例三路交错：生产 17893（`min-streams=1`）、隔离 A 17901（`min-streams=0`）、
   隔离 B 17902（`min-streams=1` 同构对照）。
2. 每路 5 轮 × ≤10 s，轮间切换，不并发下载。
3. A 的停摆轮数显著低于 B，且 B 与生产同形，才判 H7 成立。
4. 成立后只改生产 yaml 的 `min-streams`，复测 CASE-12 10 轮 + CASE-1。

## 1. 切片

| 切片 | 输入 | 输出 | 停止条件 |
|---|---|---|---|
| P1 隔离实例 | 生产 yaml 副本、端口 17901/17902、独立 `-d` 目录 | 两个本地代理进程 + 端口 LISTEN | `本地代理 -t` 通过；17893 不受影响 |
| P2 三路交错 | P1 + 固定 URL | 每路 5 轮的 code/size/speed | 每轮 ≤10 s；原始目录落盘 |
| P3 判定 | P2 | H7 成立/不成立 | 按 §0.3 |
| P4 生产单变量 | P3 成立 | yaml 改 `min-streams: 0` + sha256 + 重启计时 | 30 s 内 256 路；否则回滚 |
| P5 复测 | P4 | CASE-12 10 轮、CASE-1 中位、短请求 ttfb | 停摆率不劣化 |
| P6 文档 | 全部原始输出 | result/cases/design/plan | 无静默空白 |

## 2. 状态

```
phase      ∈ {prepared, sampled, judged, shipped, rolled-back}
instances  = {prod:17893, min0:17901, min1:17902}
samples[r] = list of (code, size, time)
verdict    ∈ {undecided, h7, not-h7}
```

## 3. 安全不变式

- **S-20** 隔离实例用独立 `-d` 目录与端口，不碰 17893/18443；`本地代理 -t` 失败不启动。
- **S-21** 生产改动前 `.bak-g8` + sha256；回滚 <60 s。
- **S-22** 单变量：一次只改 `min-streams`；不与 tbtun 二进制、其他 smux 字段同批。
- **S-23** 不并发下载；同一 URL 同一时段交错；每轮 ≤10 s、单命令 ≤60 s。
- **S-24** 协议面不动：256 路、`:39443`、TCP-only、8 MiB 信用、`rho=0.25`、GF(16)。

## 4. 活性不变式

- **L-20** 每次生产重启后 30 s 内见 256 路，否则该轮无效并回滚。
- **L-21** 隔离实例结束即 kill，确认无 `/tmp/g8-proxy*` 残留。
- **L-22** 原始输出独立目录，失败样本保留。
- **L-23** 判定不等人，落盘即结论。

## 5. 关键路径与串行分数

P2 的 10 轮 × 10 s 是唯一长串行段，串行根因是单隧道不能并发测。缩短：三路交错在
同一命令内完成；P5 复用 P2 的轮结构。P1/P4 是单次部署，可与只读工作并行。

## 6. 工人分工

| 角色 | 责任 | 输入 | 输出 | 停止条件 |
|---|---|---|---|---|
| 测量员 | P2/P5 | 三路端口、固定 URL | 原始目录 | 每轮 ≤10 s |
| 配置员 | P1/P4 | yaml 副本、备份 | 进程/端口/yaml sha | 预检通过、256 路 |
| 文档员 | P6 | 原始输出 | 四份文档行 | 无静默空白 |

生产隧道与本地代理配置是共享可变状态，不设并行写者；隔离实例的写与生产写串行。

## 7. 第九代开工建议（第八代结束时，2026-11-20 17:35）

第八代已证伪 `min-streams=0` 与 `initial`+`max` 窗口 4 MiB，并拿到 HND0
`cell_loss 16.3%`、`srtt 1412/minrtt 726`、`tx_head=peer_ack`、`send_control=tun`。
修复面回到 tbtun 承载。按序：

1. **先建丢包床**：回环 256 路 + `netem delay 20ms rate 200mbit loss 1%`，记录
   `cell_loss`、`boosters/sealed` 与 bulk 基线。官方床无 loss，pace 反证
   （22.3896 vs 22.3877 s）不能代表生产。
2. **在丢包床上重实现服务端下行组级 pace**：`SessionCarrier` 的 `paceOn` +
   `Server`/`Cli` 的 `--pace`（默认 0），构建 0 warning、单测全绿；单变量对照
   `send_control=tun` 对 `group`，判据 `cell_loss` 与 `boosters/sealed` 下降且 bulk 不降。
3. **若 pace 无效，查 FEC 自适应**：`rho=0.495` 与 `cell_loss 16.3%` 的循环；rho
   上限、修复包与数据包的调度优先级。属敏感默认，需机制说明 + 单测 + 矩阵。
4. **生产 A/B 只在好时段做**：先跑 3 轮 HND0 直连，中位 <50 Mbit/s 或隧道 3 轮全
   0 字节则不做 A/B，只记录。
5. **生产切换**：备份 `tbtun.bak-g4` 与 `/run/hnd0tbt4.yaml.bak-g6`；换 HND0 二进制
   后加 `--pace 1`；复测 CASE-12 10 轮 + CASE-1 + CASE-3；无效则 `install` 备份并
   `systemctl restart`，<60 s。

## 第九代：路径补建与退化时间序列

1. **P1 路径补建**：按 design §13.2 在 `Client.fs` 的 `housekeeping()` 比较
   `carrier.Paths.Count + PathOpsPending + pending.Count` 与目标路径数，不足时后台
   补建缺失 id。新增确定性测试：把 carrier 从 256 条降到 1 条，断言 30 s 内回到 256，
   且每个 id 的 `Incarnation` 严格递增。
2. **P2 回环床**：256 路、netem 20 ms/200 Mbit，主动 restart 服务端 3 次，每次
   30 s 内 `paths` 必须 ≥250；任一失败即修，不进入生产。
3. **P3 部署**：经 17893 上传（3.9 MB / 5.9 s）替换本机客户端，备份
   `/usr/local/bin/tbtun.bak-g9c`；服务端保持 `3f05c4a0`、pace off。
4. **P4 退化时间序列**：同一会话连续 10 分钟，每 10 s 记 `paths`、`rx_ack`、
   `RxDelivered`、`boosters`、`cell_loss`、`rho`，定位 9.55→6.28 MB/s 的拐点。
5. **P5 约束**：不改本地代理配置、不做本地代理层实验；`--pace` 的对照必须等路径
   补建上线后，且两侧都从 restart 后新会话开始。

## 第十代：路径池补建的可执行编排（2026-11-20 12:24）

### 1. 状态与数据结构

```
phase        ∈ {planned, pool-built, client-built, loopback-ok,
                deployed, verified, rolled-back}
pool_tests   ∈ {none, pass, fail}
paths_t[r]   = 第 r 次 restart 后首次 ≥250 的秒数（r = 1..3，回环与生产各一组）
verdict      ∈ {undecided, pass, fail}
```

每次 restart 的观测记录固定为五元组：
`(r, paths_at_10s, paths_at_30s, paths_at_60s, first_250_s)`。
生产另加 `(http_code, ttfb_s, client_sha, server_sha)`。

### 2. 切片

| 切片 | 输入 | 输出 | 停止条件 |
|---|---|---|---|
| T1 PathPool | design §14.3 | `src/Tbt.Carrier/PathPool.fs` + fsproj 编译项 | API 与 P-1…P-5 一致 |
| T2 单测 | T1 | `tests/Tbt.Tests/PathPoolTests.fs` + fsproj | 覆盖 P-1…P-5、退避、incarnation 1→2 |
| T3 构建与单测 | T1/T2 | 0 warning；`dotnet test tests/Tbt.Tests` 全绿 | 任一失败即停 |
| T4 Client 装配 | T3 + `Client.fs` | housekeeping 四步、事件队列、wake、关闭 drain | 同步与 async 两条路径都编译 |
| T5 回环 restart | T4 + netem 床 | 3 次服务端 restart 的 `paths_t` | 3/3 在 30 s 内 ≥250、60 s 内 256 |
| T6 候选二进制 | T4 | AOT 客户端，sha256、大小 | hash 记录 |
| T7 部署 | T6 + 备份 | `/usr/local/bin/tbtun` 新 hash，`tbtun-hnd0` active | 30 s 内 256 路 |
| T8 CASE-13 生产 | T7 | 3 次 restart + 1 次 `ss -K` + 旧版对照 | 新客户端 3/3；旧版至少 1 次冻结 |
| T9 CASE-12/1 | T8 | 10 轮停摆率、5 轮中位 | 每轮 ≤15 s |
| T10 回滚 | T9 fail | `install` 备份 + restart | <60 s，256 路 |
| T11 文档 | 全部原始输出 | result/cases/design/plan | 无静默空白 |

T0（保持员，只读）与 T1–T5 并行：记录两端 hash、连接数、单元状态、备份清单，
起草部署与回滚命令；不改生产任何状态。

### 3. 安全不变式

- **S-30** 任何生产替换前先备份并记录 sha256；回滚是 `install` + restart，目标 <60 s。
- **S-31** PathPool 只由会话线程调用；跨线程只有 `ConcurrentQueue<PathEvent>`；
  后台 `joinOne` 不读不写 `carrier.Paths`。
- **S-32** 同一 id 的 incarnation 严格单调，失败也递增，不复用、不回退。
- **S-33** 每轮启动 ≤ `concurrency`（默认 32，上限 64）；退避 base 100 ms、cap 2 s、
  attempt ≤ 8，防止 restart 风暴。
- **S-34** 协议面不动：256 路、`:39443`、TCP-only、8 MiB 信用、`rho=0.25`、GF(16)；
  不改本地代理；`--pace` 在 CASE-13 通过前不部署。
- **S-35** 不与敏感默认同批：`roundCap`、`GatePass2Limit`、FEC 默认在本代全部不动。
- **S-36** 单次测量进程 ≤15 s、单条命令 ≤60 s；原始输出独立目录，失败样本保留。

### 4. 活性不变式

- **L-30** `Request` 在有限轮内覆盖所有 missing id：每个 id 每次 `Failed` 后
  `nextAt` 有限，`Request` 每轮按 id 升序取，不饥饿。
- **L-31** `inFlight` 最终归零：`joinOne` 的每个出口（成功、`GaveUp`、异常）都投递
  一个事件；`drainEvents` 无条件 `inFlight.Remove`。
- **L-32** 关闭时 `carrier.Close()` 前 drain 一次，`Joined` 但未接管的 link 被
  `Close()`，无 socket 泄漏。
- **L-33** 回环 3/3 未达 30 s/≥250 不进入生产；生产任一次未达即回滚。
- **L-34** 回滚后 30 s 内服务 active、≥200 路，60 s 内 256 路。

### 5. 关键路径与串行分数

关键路径：T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9。
串行段只有三处：两次构建（各 1–2 min）、回环 3 次 restart（每次 ≤60 s）、
生产 3 次 restart（每次 ≤60 s）。T9 的 10 轮 × 10 s 是唯一长测量，约 100 s。
缩短措施：
- T0 快照与 T1–T4 并行，不占关键路径。
- T5 的 3 次 restart 在同一进程会话内连续做，复用同一 netem 床，不重建环境。
- T8 与 T9 共用同一部署会话；每轮观测立即落盘，判定不等待全部结束。
- T11 的四个文档行可与 T10 并行起草，只留数字待填。

### 6. 工人分工

| 角色 | 切片 | 输入 | 输出 | 停止条件 |
|---|---|---|---|---|
| 代码员 W1 | T1–T4 | design §14、`Client.fs`、两个 fsproj | `PathPool.fs`、`PathPoolTests.fs`、装配 diff、0 warning | 单测全绿、两条模式都编译 |
| 回环员 W1b | T5 | T4 的二进制、`unshare -rn` + netem | `paths_t` 三组、原始日志 | 3/3 ≤30 s 到 250 |
| 保持员 W2 | T0 | 只读 `ssh`/`ss`/`systemctl` | 快照、部署与回滚脚本草稿 | 未改生产 |
| 测量员 W3 | T6–T10 | 候选、备份、固定 URL | sha256、三组 restart 记录、CASE-12/1 目录 | 每轮 ≤15 s |
| 文档员 W4 | T11 | 全部原始输出 | 四份文档更新 | 无静默空白 |

共享可变状态只有生产两端二进制与生产隧道，同一时刻只允许 W3 一个写者；
W1 与 W1b 的写面是工作树与 `/tmp` 床，与生产不共享。W2 只读。

### 7. 判定

- 进入生产：T3 全绿、T5 3/3 达成、T6 hash 记录。
- 生产通过：T8 新客户端 3/3 在 30 s 内 ≥250、60 s 内 256，单路断开 60 s 自愈；
  T9 停摆 ≤1/10 且中位 ≥旧版 1.5×。
- 任一失败：T10 回滚，`verdict=fail`，把失败样本与 hash 写入 result。
- 生产通过后才允许讨论 `--pace` 单变量；本代不执行。

## 第十三代：path 0 补建与单变量验证（2026-11-20 13:40）

### 1. 单变量

唯一改动是「补建面从 1..target-1 扩到 0..target-1」及其启动窗口抑制。
`RxHalf`、`SessionCarrier` 调度、`GatePass2Limit`、`roundCap`、FEC 默认、`--pace`
本代全部不动；协议面、路径数、8 MiB 信用、`rho`、GF(16) 不动。

### 2. 切片与次序

| 切片 | 动作 | 停止条件 |
|---|---|---|
| T1 | `PathPool.Request` 从 0 起扫；`Joined`/`Failed` 允许 `i = 0` | 既有 8 例仍绿 |
| T2 | `PathPoolTests`：Target=1 且 present 空 → 重建 `(0, stride)`；present={0} → 空；id 0 连续重建 inc 单调 | 全部断言通过 |
| T3 | `Client.presentIds` 增加 `path0Held` 判据 | 构建 0 warning / 0 error |
| T4 | `dotnet test Tbt.slnx -c Release` 全量 | 全绿，数字记入 result |
| T5 | 回环 JIT：杀 path 0；杀 id 7；3 次服务端 restart | 三例都 60 s 内 256（restart 30 s 内 ≥250） |
| T6 | AOT 发布 | sha256/大小记录 |
| T7 | 生产 `install` 本机客户端 + restart，备份 `tbtun.bak-g13` | 30 s 内 ≥250、60 s 内 256 |
| T8 | 生产杀一条非 0 承载，再杀一次 path 0 | 各自 60 s 内回到 256，日志有 `path N joined` |
| T9 | 文档：cases 证据表、result 第十三代节 | 无静默空白 |

### 3. 安全不变式

- **S-50** 生产替换前 `install` 备份并记 sha256；回滚 <60 s。
- **S-51** PathPool 仅会话线程调用；后台 JOIN 只投递事件。
- **S-52** id 0 的 incarnation 严格递增，失败也递增；不复用。
- **S-53** 启动窗口 `path0Held` 最多抑制一个服务周期，不改变稳态判缺。
- **S-54** 不与敏感默认同批；出现吞吐或路径数退化时先回滚再诊断。
- **S-55** 单次测量 ≤15 s、单条命令 ≤60 s；原始输出独立目录。

### 4. 活性不变式

- **L-50** 缺失 id 在有限轮内被 `Request` 覆盖，id 0 不再例外。
- **L-51** 每个 `joinOne` 出口都投递一个事件，`inFlight` 终将归零。
- **L-52** 回环三例任一失败即停，不进生产。
- **L-53** 生产任一例 60 s 未回 256 即回滚，并保留日志。
- **L-54** 回滚后 30 s 内 active、≥200 路，60 s 内 256。

### 5. 关键路径

T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9。串行分数集中在两处：
构建/单测（一次）与三次回环观测（每次 ≤90 s）。文档 T9 与 T8 并行起草。
没有共享可变状态需要协调：生产写者只有一人（本次执行者），回环床在
`unshare -rn` 内与生产隔离。

### 6. 判定

- 进入生产：T4 全绿且 T5 三例全达。
- 生产通过：T7 与 T8 全部达 60 s 内 256，且路径数在测量期间无其它下降。
- 任一失败：回滚 `tbtun.bak-g13`，把失败样本与 hash 写入 result，`verdict=fail`。
- 吞吐不因本改动承诺提升；停摆与中位只按 CASE-12/1 记录。

### 7. 执行结果

T1–T9 全部执行。T4 全量单测 401 全过、构建 0 warning / 0 error；T5 回环两例
均在 1 s 内回满 256（日志 `path 252 joined`、`path 0 joined`），restart 走
`Exit 75` 并完成新握手；T6 AOT `1547a466`（3,992,936 B）；T7 生产 restart 后
9 s 到 256；T8 生产杀非 0 0.52 s、杀 path 0 1.55 s 回到 256，无需回滚。
未按 T5 原文重复 3 次 restart：该路径本代未改，g11 三次 28–35 s 到 256 为
唯一 3 次证据，已在 result 标注。async `--paths 2` 启动退出为既有边缘缺陷，
不阻塞 256 路生产。

## 第十五代：发送侧供给参数化与生产判定（2026-11-20 14:00）

### 1. 切片

| 切片 | 内容 | 状态 |
|---|---|---|
| T0 | 两端 hash、256 路、stats、会话年龄只读快照 | 完成 |
| T1 | `SocketTuning.pathWith`、`SessionCarrier ?txSlots`、`AsyncPathLink` 槽数、两端 CLI | 完成 |
| T2 | 默认等价与边界单测（4096/262144、16/64） | 完成 |
| T3 | 构建 0 warning、全量单测 8+153+242=403 | 完成 |
| T4 | RTT 匹配床：delay 160ms、250 Mbit/s、256 路、16 MiB，V0/V1/V2 | 完成，尺度不匹配 |
| T5 | AOT `7ffd15ae`（3,997,032 B） | 完成 |
| T6 | 经 17893 上传、sha256、备份 `tbtun.bak-g15`、install、29 s 到 256 | 完成 |
| T7 | 默认参数基线 10×12 s | 完成，停摆 5/10 |
| T8 | `--notsent-lowat 32768 --tx-slots 32` 10 轮 | **中止**：触发客户端冻结 |
| T9 | 判定 | 不可判定，回滚 |
| T10 | 恢复：`ss -K` + 服务端还原 `3f05c4a0` | 完成，32 s 到 256 |
| T11 | 文档 | 本文件与 result §14、design §16、cases 证据表 |

### 2. 安全不变式

- **S-60** 生产部署前备份并核 sha256；回滚 <60 s。
- **S-61** 两个新参数的缺省值必须等于旧常量（16384 / 16），默认行为逐字节不变。
- **S-62** 协议、路径数、8 MiB 信用、`rho` 语义、GF(16) 上行不动。
- **S-63** 单轮测量 ≤12 s、单条命令 ≤60 s；床内实验不碰生产。
- **S-64** 只给服务端开参数，客户端保持默认，保证单变量。
- **S-65** 不修改本地代理配置，不把本地代理当实验对象。

### 3. 活性不变式

- **L-60** 每个变体保留原始目录与两端 stats。
- **L-61** 床不过不进生产；床未复现限制时只能记为“不可判定”，不能记“有效”。
- **L-62** 部署后 30 s 内见 256 路，否则回滚。
- **L-63** 停摆率 >5/10 时只用服务端机制字段判定，不据此宣布用户可见成败。
- **L-64** 失败即回滚，并复验 256 路与 `generate_204`。

### 4. 判定与执行结果

- GatePass2：生产 `gate2` deny=0，**证伪**，不部署。
- 发送侧供给：V1 从未建立新会话（服务端 `sessions=0`、客户端 `paths=1`），
  **不可判定**；默认参数基线 0.40 MB/s 好轮中位、停摆 5/10。
- 故障：客户端对残留会话忙循环，`beginfail` 1.22 亿、RSS 22 GB、不可中断；
  `ss -K` 清承载后退出。该缺陷被列为下一代第一优先级，先于任何需要重启
  服务端的 A/B。
- 生产最终：HND0 `3f05c4a0` + 本机 `1547a466`，256 路、active、
  `generate_204` 204/0.69 s。

## 第十六代：半开路径判据的修复与生产验证（2026-11-20 14:30）

### 1. 状态机

```
state = { phase, binary, samples, verdict }
phase ∈ { designed, coded, unit_ok, process_ok, bed_ok, shipped, verified, rolled_back }
verdict ∈ { undecided, timeout_fixed, timeout_ineffective, blocked }
```

每次转移只允许一条证据：`coded → unit_ok` 要全量单测；`unit_ok → process_ok`
要进程级 `ETIMEDOUT` 实验；`process_ok → bed_ok` 要回环两例；`bed_ok → shipped`
要 AOT 哈希与备份；`shipped → verified` 要生产 3 次 restart 与 RSS 上界。

### 2. 切片

| 切片 | 内容 | 退出条件 |
|---|---|---|
| T1 | `Platform.HasTcpUserTimeout` + `describe` 增一列 | 能力探测在本机为真、在 `--user-timeout-ms 0` 路径上不被设置 |
| T2 | `SocketTuning.UserTimeoutMs`、`userTimeoutMsFor`、`pathWith` 参数、`path` 委托 | 构建通过；默认路径设置 30000 |
| T3 | 单测：`userTimeoutMsFor` 边界（0/999/1000/30000/300000/300001）、`pathWith` 不抛 | `dotnet test` 全绿 |
| T4 | 进程级实验 | 设 2000 ms 时 `SIGSTOP` 对端后 `send` 在 4 s 内 `ETIMEDOUT`；设 0 时 10 s 仍 `EAGAIN` |
| T5 | 回环 256 路 + 服务端 `kill -9` | 客户端 `Exit 75`，systemd 拉起后 30 s 内 256 路 |
| T6 | 回环半开（netns 内 DROP FIN/RST 后杀服务端） | 旧二进制停在 `paths=256` 且 `beginfail` 增长；新二进制 30 s + 60 s 内退出 |
| T7 | 工作树按代提交 | 三个独立提交，各带构建/单测证据；`git status` 无 src 未提交项 |
| T8 | AOT 发布 + 备份 `tbtun.bak-g16` + 部署本机 | 30 s 内 256 路，`generate_204` 204 |
| T9 | 生产 3 次服务端 restart | 每次 `first250`/`first256`；无 `paths=1` 忙循环；RSS < 500 MB |
| T10 | 发送侧单变量（仅当 T9 通过） | 默认 16384/16 与 32768/32 各 10 轮 ×12 s |
| T11 | 判定 | 好轮中位 ≥1.5×、`beginfail` 增量降 ≥50%、有载 `srtt ≤1.6×minrtt`、停摆不增 |
| T12 | 文档 | result §15、cases 证据表、design §17.7、plan 本文件 |

### 3. 工人与边界

只有两个角色，且互斥：

- **代码员**（我）：`Platform.fs`、`SocketTuning.fs`、`tests/Tbt.Tests`、CLI 参数。
  停止条件：T3 全绿。
- **测量员**（我，串行切换）：只在代码冻结后启动；输入是已落盘的二进制哈希与
  原始目录；输出是每轮 `{size,speed,code,time}` 与两端 stats 快照。不改代码。

没有并行写者。回环床与生产测量不并发（两者都碰 39443 与 17893）。

### 4. 安全不变式

- **S-70** 任何生产替换前 `cp` 备份并核 sha256；回滚 <60 s。
- **S-71** `--user-timeout-ms 0` 必须等于旧行为；默认值只能通过显式参数改变。
- **S-72** 协议、路径数、8 MiB 信用、`rho` 语义、GF(16) 上行、`GatePass2Limit`、
  FEC 自适应默认全部不动。
- **S-73** 单轮测量 ≤12 s；单条命令 ≤60 s；床内实验不碰生产。
- **S-74** 不修改本地代理配置；停摆轮只记录，不据此设计实验。
- **S-75** 工作树收口用按代提交，不把四代改动混进一个提交。
- **S-76** 回环 netns 用 `unshare -rn`（无需 sudo）；不删除他人 netns。

### 5. 活性不变式

- **L-70** 每个回环/生产测量有唯一原始目录，落盘即判定。
- **L-71** 部署后 30 s 内见 256 路，否则回滚。
- **L-72** 半开实验的旧/新对照必须同一天、同一 netns 参数、同一二进制来源。
- **L-73** `paths=1` 出现即该轮判失败，不等待“可能自愈”。
- **L-74** 停摆率 >5/10 时只用机制字段（`beginfail` 增量、RSS、`srtt`）判修复是否
  生效，不宣布用户可见提速。
- **L-75** 失败即回滚并复验 256 路与 `generate_204`。

### 6. 关键路径与串行段

串行：T1→T2→T3（构建）→T4（4 s + 10 s）→T5（≈60 s）→T6（≈120 s）→T7（提交）
→T8（AOT 约 90 s + 部署 40 s）→T9（3 × restart，每次 ≈60 s）→T10（可选，10 ×12 s
×2 组 ≈ 5 min）→T12。

可并行的窗口只有一个：T8 的 AOT 发布与本机备份可以同时准备；HND0 侧本轮不改，
因此没有远端传输依赖（与第十五代的 V1 不同）。

关键路径上唯一的“可能推翻设计”的节点是 T4。若 T4 显示 `ETIMEDOUT` 不来，立刻
转 `verdict = timeout_ineffective`，修改设计而不是在生产上试。

