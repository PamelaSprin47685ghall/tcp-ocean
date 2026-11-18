# 自适应 FEC（2026-11-18）

固定 `rho = 0.25` 换成按实测擦除率调整。两端的发送方向各自独立决策：上行只看服务端
观测到的上行丢失，下行只看客户端观测到的下行丢失，不跨方向拷贝。

## 控制律

```
peerErasure ← EWMA(对端上报的擦除率, 1 / FecWindowCells)
target      = clamp(RhoMin, RhoMax, RhoGain × peerErasure / (1 − peerErasure))
rho         ← 向 target 靠拢：升 +0.02/帧，降 −0.005/帧，死区 0.01
```

`RhoGain × e/(1−e)` 是覆盖该擦除率所需的最小 parity/source 比再乘安全系数。参数：

| 参数 | 值 | 含义 |
|---|---|---|
| `RhoMin` / `RhoMax` | 0.01 / 0.50 | 比例上下界 |
| `RhoFloor` | 0.02 | 无丢包时保留的 2 % 冗余（BOOSTER 也需要一点余量） |
| `RhoGain` | 3.0 | 安全系数；4.0 实测无收益，见下 |
| `FecWindowCells` | 64 | 对端擦除率的 EWMA 时间常数（帧） |
| `TBTUN_FEC_ADAPTIVE` | 默认开 | 置 `0` 回到固定 0.25，用于对照 |

`rho` 同时作为 BOOSTER 数量的擦除率输入，修复包数量随实测丢包一起缩放。

## 信号：必须用接收端的 source 擦除率

第一版用 NACK 缺口估丢包，是**有偏**信号：NACK 只报 FEC 修复失败后仍缺的 cell，rho 越高
报得越少，控制环把“修得好”当成“没丢包”。实测 5 % 丢包会话里 `rho` 一路降到下限而
`recovered / frames_in = 4.9 %`。

正确信号在 `RxHalf` 里早已存在：

```
erasure = 1 − received / expected        // 每 step，含被 FEC 修好的 cell
        → Meta.ErasureQ16（40 B 头偏移 35，每个出站帧携带）
        → TxHalf.OnPeerErasure
```

`expected` 的语义必须精确：

- 只统计 **source cell**，因此 `expected = Meta.StepUsed`（发送端填 `byte k`，
  见 `TxHalf.fs:763/821`），**不加 `Aux`**（`Aux` 是 parity 数）；
- `CellType.Parity` 不调用 `count`；
- 每个环形槽位带 `expectedStep` 标签。环形容量远小于在途 step 数，没有标签时迟到的
  cell 会覆盖同槽位另一个 step 的计数，零丢包链路被稳定算成 1/3 丢失；
- 折叠线取「所有活跃路径 `pathStep` 的最小值」。TCP 在同一路径上保序，低于这条线的
  step 不可能再有 cell 到达；固定 32 步的余量在 4 路分片下不够。

## 根因教训：两端跑的是不同二进制

`tests/e2e/run.sh` 服务端用 `$work/aot/tbtun`（NativeAOT），客户端用 `$work/jit/tbtun`
（JIT）。只 `dotnet publish` AOT 时，客户端一直跑旧 dll，所有 `RxHalf` 修复在客户端
不生效——表现为零丢包下行 `cell_loss=0.3316` 恒定，而服务端自己的估计器 `elast=12/12`
干净，客户端 stats 行缺少新加的 `efloor=` 字段。

`run.sh` 现在在 `TBT_BIN` 模式下比较两个二进制的 mtime，相差超过 300 s 直接拒绝启动
（`ALLOW_STALE_BIN=1` 可越过）。**任何只重建一端的测量都无效。**

## 实测（4 路，20 ms OWD，200 Mbit/s shaper，二进制 sha256 `46684cab…`）

| 场景 | 客户端 rho | 服务端 rho | iperf up | iperf -R | -R -P 4 |
|---|---|---|---|---|---|
| loss=0 | 0.025–0.030 | 0.030 | 170.0 / 169.4 | 173.0 | 174.5 |
| loss=0.05 | 0.189 | 0.162 | 155.6 | 108.7 | 146.2 |
| loss=0.05（RhoGain=4） | 0.245 | 0.220 | 142.6–151.0 | 104.1 | 140.8 |
| loss=0.05（固定 0.25 对照） | — | 0.250 | 55.3 | 134.0 | 139.6 |

- 零丢包：`cell_loss=0.0000`，冗余从 25 % 降到 3 %，上下行各提升约 20 %。
- 5 % 丢包：两端 rho 与 `RhoGain × e/(1−e)` 吻合（0.189 ≈ 3×0.0581/0.9419，
  0.162 ≈ 3×0.0493/0.9507）；上行从 55.3 提到 155.6，因为上行本身不丢包，冗余自动撤掉。
- 5 % 丢包单流下行 108.7，低于固定 0.25 的 134.0。把 `RhoGain` 提到 4.0 后 rho 升到
  0.22，`-R` 反而 104.1：**瓶颈是丢包下的 TCP 拥塞控制，不是 FEC 冗余**。因此保持
  `RhoGain = 3.0`，避免无谓带宽开销；多流 `-R -P 4` 为 146.2，高于固定值。

## 回归

- `dotnet test tests/Tbt.Session.Tests`：239/239（多次）。
- 4 路完整 e2e（echo/ping/bulk，loss=0）：`status=0`，echo 8 流 143.8 Mbit/s、
  64 流 100.7 Mbit/s、ping 200/200、ping-under-bulk 200/200，全部 segment valid。

## 复跑

```sh
# 两个二进制必须同时重建；run.sh 会拒绝 mtime 相差 300 s 以上的组合
dotnet publish src/Tbt.Cli -c Release -r linux-x64 -o /tmp/tbt-bin/aot \
  -p:NuGetAudit=false -p:SuppressTrimAnalysisWarnings=true
dotnet build src/Tbt.Cli -c Release -o /tmp/tbt-bin/jit -p:PublishAot=false -p:NuGetAudit=false

TBTUN_FEC_ADAPTIVE=1 TBT_BIN=/tmp/tbt-bin TBTUN_ASYNC_MODE=true \
  ALLOW_DIRTY=1 HOST_QUIET_BYPASS=1 tests/e2e/run.sh 20 200 0 4 10
```

`Describe` 输出中的 `rho=`、`cell_loss=`、`efloor=`、`efolds=`、`elast=<expected>/<received>@<step>`
用于核对估计器：`efolds` 应随运行持续增长，`elast` 在干净链路应为 `k/k`。
