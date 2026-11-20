# 第十二代编排：从判定到最小修复

> 目标只有一个：把下行有效在途窗口从 200–285 KB 提高到与 8 MiB 信用相称的量级，
> 且每一步都可单独回滚。本计划不重做已被证伪的方向（pace、RxHalf 20 ms、
> 等待粒度、承载丢包、Grant/CanFill），只执行设计文档 g12 §3 的三个命题判定。

## 1 状态与变量（过程本身的数据结构）

```
phase      : fixed | built | bed-up | measured | judged | patched | verified | shipped | rolled-back
verdict    : undecided | p1 | p2 | p3 | none
candidate  : { path, sha256, bytes, side: client|server|both }
samples    : []sample            // 每样本 { config, seconds, bytes, rx_ack, steps_per_s, read_calls, read_bytes, would_block, rcv_space, skmem_r, unacked }
raw_dir    : /tmp/tbt-g12-<epoch>   // 原始输出，不可覆盖
```

转移：

```
fixed    --build JIT & AOT--> built
built    --unshare -rn + tc netem--> bed-up
bed-up   --run matrix (4 configs)--> measured
measured --apply design g12 §3 判据--> judged
judged   --one-line patch--> patched
patched  --build + unit tests + 4-path matrix--> verified
verified --tunnel upload + sha256--> shipped
shipped  --10x12s production--> shipped | rolled-back
```

每一转移只允许一个变量改变。`verdict` 未定之前不得进入 `patched`。

## 2 工人工序（本代单写者，切片仍按不变量切）

| 工人 | 切片 | 输入 | 输出 | 停止条件 |
|---|---|---|---|---|
| 测量员 | T1 床、T2 四配置矩阵 | 工作树二进制、netem | `samples`、`raw_dir` | 四配置各 ≥2 次有效样本 |
| 分析员 | T3 判定 | `samples` | `verdict` + 证据 | 对一个命题给出 ≥95% 把握 |
| 代码员 | T4 补丁、T5 单测 | 设计 g12 §4 | patch + test | 0 warning / 0 error、全量测试通过 |
| 测量员 | T6 生产 A/B | candidate、URL | 10 轮样本 | 停摆 ≤1/10 或判回滚 |
| 记录员 | T7 文档 | 全部原始 | cases/result/design | 证据表每行有路径 |

## 3 安全不变式（Safety）

- **S-50** 床内实验：所有 netem 与回环实例只在 `unshare -rn` 命名空间内，不碰生产接口。
- **S-51** 单变量：一个 scope 内只改一处；补丁前 `git diff --stat` 必须只列预期文件。
- **S-52** 备份先行：任何 `/usr/local/bin/tbtun` 替换前必须存在 `.bak-g12` 且 sha256 已记录。
- **S-53** 协议面不动：不改 `Tbt.Session` 的帧格式、信用语义、编码参数、路径数、FEC 默认。
- **S-54** 单次测量 ≤15 s、单条命令 ≤60 s；原始目录不覆盖。
- **S-55** 部署后 30 s 内不见 256 路即回滚；回滚总时长 <60 s。
- **S-56** 停摆轮单列：停摆轮不进中位，也不被静默剔除。

## 4 活性不变式（Liveness）

- **L-50** 床起不来（无 `unshare` 或 `tc`）时，直接退到生产只读测量，不阻塞判定。
- **L-51** 四配置矩阵中任一配置 3 次无效样本即判该配置不可用，继续其余配置。
- **L-52** 判定期不超过两个 15 s 轮的分析时间；证据不足则 `verdict=none` 并记录缺口。
- **L-53** 生产时段停摆 >5/10 时，暂停生产 A/B，保留基线，转文档记录，不声称结论。

## 5 关键路径与串行分数

串行段：`build → bed-up → measured → judged → patched → verified → shipped`。
并行段：T7 文档可在 T3 判定后立即起草；AOT 构建可与 JIT 床内实验并行（不同输出目录）。
缩短串行：JIT 构建一次服务全部四配置；四配置在同一床内串行但每配置只跑 1 次 16 MiB
（约 12 s），总计 <2 分钟；生产 A/B 只在 `verified` 后执行一次。

## 6 判据汇总（与设计 g12 §3 一致）

| 命题 | 成立条件 | 证伪条件 |
|---|---|---|
| P1 | 服务端 flow socket `rcv_space` < 200 KB 而代码请求 4 MB | `rcv_space` ≥ 1 MB 且 `skmem r` ≈ 8 MB |
| P2 | `readCalls/秒 ÷ 步/秒` ≈ 1 且 `wouldBlock/readCalls` > 95% | `readCalls/秒` 远大于步/秒 |
| P3 | `--window 4096` 吞吐 ≈ `--window 1024`，且 `readBytes/readCalls` 恒定 | 吞吐随 `--window` 上升 |

## 7 生产验收（与 cases.md 一致）

- CASE-12：10 轮 ×12 s，0 字节轮 ≤1/10。
- CASE-1：好轮中位 ≥ 同窗口基线 1.5×（基线取本代部署前同 URL 10 轮）。
- CASE-3：有载 `srtt` ≤1.6× `minrtt`。
- CASE-6：4 路 bulk/iperf 不退化（只在补丁触及共享路径时跑）。
- 任一不达标：60 s 内回滚，`verdict` 保留，转入文档。

## 8 执行结果（第十二代）

T0–T5 已完成：四配置床、三命题判定（P1/P2/P3 全证伪）、`stallNs` 单变量生产 A/B
（10 轮，好轮中位 0.605 MB/s，停摆 3/10）、回滚（`install .bak-g12` + restart，
sha=7c48936e、256 路、active）。T6 文档完成。未执行：T7 单路半开自愈，
保留给后继，验收按 cases.md CASE-13 与修正后的路径计数命令。
