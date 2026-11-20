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
