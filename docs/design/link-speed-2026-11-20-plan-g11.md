# 第十一代编排：会话年龄退化（2026-11-20）

前接 `plan.md` 第十代节。本代不重做路径池，不碰本地代理，不改协议面。
账本见 `todo`；本文件只写过程、状态与不变式。

## 1. 已固定的状态

| 项 | 值 | 来源 |
|---|---|---|
| 生产客户端 | `/usr/local/bin/tbtun` 第十代候选（3,992,920 B） | 交接报告 |
| 生产服务端 | HND0 `/usr/local/bin/tbtun`（`3f05c4a0`，pace off） | 交接报告 |
| 回滚备份 | 本机 `tbtun.bak-g10`、HND0 `tbtun.bak-g9` | 交接报告 |
| FEC 默认 | `FecAdaptive=false`、`RhoGain=3.0`、`RhoMax=0.50`、`RhoFloor=0.02` | `Session.fs:68` |
| rho 步长 | 升 0.02/次、降 0.005/次 | `TxHalf.fs:91-92` |
| 实测病征 | `rho` 0.03→0.494、`cell_loss` 0.0002→0.1704、`beginfail` 0→43722，随会话年龄 | 第九代 stats |

## 2. 切片与状态

进程状态：

```text
phase ∈ {fixed, building, probing, attributed, patched, matrix, shipped, measured, documented}
verdict ∈ {undecided, rho-ratchet, path-health, write-side, external}
```

| 切片 | 动作 | 输入 | 输出 | 停止条件 |
|---|---|---|---|---|
| T1 | `dotnet build Tbt.slnx -c Release`（含工作树第十代改动），记二进制 sha256 | 工作树 | 服务端/客户端可执行 | 0 warning / 0 error |
| T2 | `unshare -rn` 建 netem 床：`lo` 上 `delay 20ms rate 200mbit limit 50000`，256 路 | T1 | 床可起停 | 一次 16 MiB 下载成功 |
| T3 | 长跑：60 s × 10 轮连续下载（独立进程），每 30 s 采服务端 stats 一行 | T2 | `rho/erasure/beginfail/boosters/吞吐` 时间序列 | 10 轮完成或 12 分钟 |
| T4 | 判据：rho 单调升且吞吐退化 ≥2× ⇒ `rho-ratchet`；beginfail 升而 rho 平 ⇒ `write-side`；两者皆平 ⇒ `external` | T3 | `verdict` | 一个判定落盘 |
| T5 | 黑洞实验：6/256 路径入站单向丢弃，重跑 T3 的 3 轮 | T2 | `erasure` 是否跳到 ~0.023 并被 rho 放大 | `verdict` 细化 `path-health` |
| T6 | 按 `verdict` 改一处：rho 控制器（对称步长 + 滞回）或路径发送权重账本 | T4/T5 | 源码 diff、单行回滚点 | 构建 0 warning |
| T7 | 确定性单测：控制器步长/滞回、权重账本；全量 396+ 单测 | T6 | 测试通过 | 全绿 |
| T8 | 回环矩阵：4 路、16 路 × 0% / 5% 丢包，改进前后同 commit 构建对照 | T6 | 两侧吞吐与交互 | 中位不降 |
| T9 | AOT 发布并记 sha256；经 17893 隧道上传 HND0（`python3` 接收器，POST，sha256 校验） | T8 | 两端候选 | hash 相符 |
| T10 | 生产 CASE-13：HND0 连续 3 次 restart，2 s 粒度记录首次 ≥250 / =256；单路 `ss -K` 后 60 s | T9 | 时间序列 | 3/3 达 250 或失败即回滚 |
| T11 | 生产 CASE-12 + CASE-1：新构建与备份各 10 轮 × ≤12 s；停摆率与中位分列 | T10 | 原始目录 | 判定落盘 |
| T12 | 文档：修 CASE-13 证据行、新增会话衰减 CASE、result 记机制与残余 | T1–T11 | 三份文档 | 无静默空白 |

## 3. 安全不变式

- S-40：T2–T8 只在无特权 `unshare -rn` 床内；不启停生产单元，不改 `/run/hnd0tbt4.yaml`。
- S-41：单次下载 ≤60 s；单条命令 ≤60 s；长跑由独立轮次串成，不拼接。
- S-42：任何源码改动前先记 `git diff` 快照到 `/tmp`；回滚为 `git checkout -- <file>` 或单行反向补丁。
- S-43：协议面不动——GF(16)、信用窗口、路径数、`rho` 语义（自适应关闭时 0.25）、`RhoMax 0.50` 保持。
- S-44：部署前备份并核 sha256；回滚 <60 s；回滚后 30 s 内须 ≥200 路且一次 HTTPS 成功。
- S-45：敏感默认（startup/drain/满管、信用会计、testbed 判定）不与控制器改动同批。

## 4. 活性与判定

- L-40：每轮实验产出独立原始目录，失败样本保留。
- L-41：T3 若在 12 分钟内无法完成 10 轮，用已完成的 ≥5 轮判定，并记录截断。
- L-42：T4 判定为 `external` 时，T6–T11 跳过，直接 T12 记录；不以猜测补位。
- L-43：生产测量每轮立即落盘，判定不等全部结束。
- L-44：T10 失败即 T10b 回滚，不以重试掩盖。

## 5. 关键路径

串行段：T1 构建 → T2 床 → T3 长跑（10 分钟）→ T4 判定。
并行段：T5 黑洞实验与 T3 共用床，可在 T3 后立即接；T12 文档骨架可与 T3 并行起草。
可压缩点：T3 的 12 分钟是唯一长段；T8 矩阵用 4 路单枪先出方向，16 路仅在 4 路无差异时补。

## 6. 工人

单人执行。共享可变状态只有工作树构建产物、`/tmp` 床、生产二进制；
同一时刻只有一个写者，不并行改源码与测量。
