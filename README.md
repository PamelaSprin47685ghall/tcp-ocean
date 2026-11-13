# TBTun

TBTun v2.2 协议的 F# 实现。规范见 `docs/spec/TBTun-主文档-v2.2.md`（唯一权威来源）。
公开仓库：[tcp-ocean](https://github.com/PamelaSprin47685ghall/tcp-ocean)。

## 环境

- .NET SDK 10.0（见 `global.json`）。
- 服务器目标：Linux，NativeAOT。客户端目标：Linux / Windows / macOS，JIT。

## 构建与测试

```sh
dotnet build Tbt.slnx -c Release
dotnet test Tbt.slnx -c Release
```

测试工程是 xunit.v3 可执行文件，也可直接运行：

```sh
tests/Tbt.Tests/bin/Release/net10.0/Tbt.Tests
```

如果 `dotnet test` 报 “.NET location: Not found”，请设置 `DOTNET_ROOT` 为 SDK 安装目录。

重新生成独立握手向量（需要 Python 3 和 `cryptography`）：

```sh
python3 tests/vectors/make_handshake_vectors.py > tests/vectors/handshake-vectors.json
```

端到端脚本的离线测试（不建命名空间）：

```sh
python3 tests/e2e/test_tb_guard.py
python3 tests/e2e/test_echo.py
```

## 模块

依赖是平的：每个模块只依赖 `Tbt.Contracts` 和下表左侧更底层的模块。

| 模块 | 内容 | 规范章节 |
|---|---|---|
| `Tbt.Contracts` | 帧布局常量、方向、cell 类型、时钟与熵接口 | 4, 6.2 |
| `Tbt.Codec` | TBT-SC2：GF(16)/GF(2) 运算、SplitMix64 图、编码器、增量 ML 消元解码器 | 5 |
| `Tbt.Wire` | 帧长与 MSS、序号扩展、源信封、meta、WIN_ACK、CTRL、FLOW_STATE 格式 2 | 4, 6.6–6.12 |
| `Tbt.Crypto` | ChaCha20 头部保护、重放窗口、cell AEAD、CLIENT_INIT/SERVER_INIT、密钥派生、JOIN/JOIN_ACK2 | 6.3–6.5 |
| `Tbt.Session` | 会话：流槽与代、发送/接收半边、许可与确认、校验暂存、NACK/BOOSTER/HEARTBEAT、握手、CTRL 可靠日志与 SESSION_CLOSE、入队、就绪集 | 5.7–5.9, 6.5, 6.9–6.11 |
| `Tbt.Carrier` | 载体：内核 TCP 路径（成帧、NOTSENT 门控）；服务器 TUN + 用户态 TCP（吞洞、借壳下行修复、RACK、RTO 兜底、D-SACK、简化 BBR、速率组）；本地流桥；Linux 默认 epoll（亦可 poll） | 4, 6.8, 7 |
| `Tbt.Cli` | `tbtun client` / `tbtun server` 命令 | — |

热路径（编码、渲染校验、封装、解封）不分配托管内存，由测试检查。

## 运行

```sh
# 服务器（NativeAOT）
dotnet publish src/Tbt.Cli -c Release -r linux-x64 -o out
out/tbtun server --listen 0.0.0.0:8443 --target 127.0.0.1:7890 --psk-file psk
# 或者：服务器在 TUN 上运行用户态 TCP（设备需事先创建并归该用户，例如
#   ip tuntap add tbt0 mode tun user $USER && ip addr add 10.0.0.1/24 dev tbt0 && ip link set tbt0 up）
out/tbtun server --tun tbt0 --tun-listen 10.0.0.2:8443 --target 127.0.0.1:7890 --psk-file psk
# 客户端（JIT）
dotnet run --project src/Tbt.Cli -c Release -- client --server HOST:8443 --listen 127.0.0.1:1080 --psk-file psk --paths 4
```

PSK 文件每行一个 64 位十六进制密钥。客户端入站是普通 TCP 监听（不是 TUN），每个本地连接是一条流；
服务器把它连到 `--target`。这是规范第 4 章的拓扑：TBTun 承载上层代理的不透明 TCP 流。
客户端始终使用未修改的内核 TCP，只有服务器端可以选 TUN 载体。

服务端 `--max-paths` 默认是 64。客户端 `--paths` 若大于该值，实际建立的路径数以协商值为准；
`tests/e2e/run.sh` 会核对二者一致，多于 64 条路径时需同时设 `MAX_PATHS`。

`--window-clamp`（默认 600000）同时设置客户端路径套接字的 TCP_WINDOW_CLAMP 和 TUN 服务端通告的每路径接收窗口。对照见 `docs/cases/uplink-window.md`。

## 端到端测试

`tests/e2e/run.sh [owd_ms] [rate_mbit] [loss] [paths] [seconds]` 建立三个网络命名空间，
用 `wanem` 模拟延迟、带宽和分段丢失，然后跑逐字节校验的 echo 和 iperf3 上下行，
并报告服务器峰值 RSS 与 CPU 时间。需要 sudo、gcc、python3、iperf3。
可选环境变量：`WINDOW`（默认 1024）、`MAX_PATHS`（默认 64）、`SEED`（默认 7）、
`CARRIER=tun|kernel`、`PROFILE=rtt400`（默认改为单向 200 ms、链路 1000 Mbit/s，并打开每五元组 4 Mbit/s 令牌桶）。

整次运行会持有 `/tmp/tbtun-netns.lock`，并发的测试床会在锁上排队。
`netns.sh up` 不会无条件删除固定名称的命名空间；清理只删除本轮记录过、且身份仍匹配的资源。
每次结果会打上提交、参数、种子、协商路径数和窗口。`pingbulk` 会让批量传输一直跑到全部探测结束，
p99 只统计收发都落在满载区间内的探测，并给出超时/失败的分母。

`CARRIER=tun`（默认）让服务器在远端命名空间的 `tbt0` 上运行用户态 TCP；`CARRIER=kernel` 使用内核 TCP 基线。

短校准（不启动 tbtun，只检查 400 ms RTT 与每五元组 4 Mbit/s）：`tests/e2e/calibrate_profile.sh`。

### 测量说明

0% 丢包、共享 200 Mbit/s 链路、固定 `ρ = 0.25` 时，净吞吐上限约为

`200 × 1328 / 1455 / 1.25 ≈ 146.0 Mbit/s`

（`S = 1344`，每源符号净荷 1328 B，每帧 IP 计费 1455 B）。主线上约 143 Mbit/s 已经是这条预算的约 98%，
并不是“还差 57 Mbit/s 没找到去处”。自适应 `ρ` 在分支上 0% 丢包大约到 169 Mbit/s，尚未合入。
完整算术和窗口预算见 `docs/cases/matrix.md`。

下表是 2026-11-12 的早期对照（单向 20 ms，200 Mbit/s，4 条路径，每例 10 s），用来说明 TUN 载体相对内核载体在丢包下的差异。之后主线的稳态数字以 `docs/cases/` 为准。

iperf3 经隧道（Mbit/s），普通 TCP 不走隧道作参考：

| 丢包 | 普通 TCP 1 流 / 4 流 | 内核载体 上行 / 下行 | TUN 载体 上行 / 下行 |
|---|---|---|---|
| 0% | 188 / 190 | 142 / 125 | 143 / 140（`-R -P4` 141） |
| 1% | — | 8.2 / 5.6 | 145 / 134 |
| 3% | 2.3 / 7.3 | 3.0 / 2.1 | 148 / 137 |
| 5% | 1.5 / 4.3 | 1.7 / 1.5 | 149 / 134 |

上表中的“—”表示该项未测。逐字节校验的 echo 在 TUN 载体下 4 档丢包全部正确。
空闲会话的 echo 延迟大约等于 RTT。服务器 RSS 约 60–62 MB。
这些格子是 20 ms / 共享 200 Mbit/s、没有每五元组 4 Mbit/s 限速时测的，不能代替原来的 400 ms RTT、256 路场景。

## 当前状态

以主线为准。与规范的长期偏差见 `docs/DECISIONS.md`。分项进度见 `docs/t9u-status.md` 和 `docs/cases/`。

**已实现**

- 上表全部模块；只实现套件 0x02（ChaCha20-Poly1305），握手中只通告这一种。
- FLOW_STATE 格式 2，每流信用与交付预留；许可按单条报告自己的确认计算（迟到报告不再放大）；会话级信用总承诺不超过 32 MiB 池（全部槽位的 C0，已占用和已承诺都计入，不收回）。
- 服务器 TUN + 用户态 TCP 载体：吞洞、借壳修复、RACK、与 SACK 无关的 RTO 兜底、合规 D-SACK、每路径接收窗口 600 000 B。
- 速率组：稳态在途上限约 1.25 倍带宽时延积，巡航增益 1.05，修补计入节拍。
- 会话 SRTT 的 Karn 规则；断网时路径保活（全组停住不当成死路径）。
- CTRL 可靠日志与 SESSION_CLOSE；Linux 默认使用 epoll，亦可退回 poll。
- 就绪集调度（RS-1…3）与有界批次（Q-3）。
- 会话级最后手段：对端矛盾的 k、或路径入口异常，只关闭该会话并计数。
- netns 端到端（echo、批量、丢包，两种载体）；测试床有全局锁和所有权记录。

**只有阶段数据，未合入主线**

- 自适应 `ρ`：0% 丢包上下行约 169 Mbit/s，CPU 更省；因断网压测中上行有时塌到约 60 Mbit/s，未合入。
- 浅队列单独上线的 A/B 更差，未合入（见 `docs/cases/shallow-queue-ab.md`）。
- 交互信用 CR-2 仍在分支。
- 合规 D-SACK 已合入，但 `DSACKUndo = 0`：它不能撤销断网时真实丢包造成的内核降窗（见 `docs/cases/uplink-ssthresh.md`）。

**仍开放**

- 交互延迟放到最后处理。空闲时 p99 约一个 RTT；满载时还有约一个 RTT 的队头阻塞。`urgent` 队列已有优先调度，但校验渲染仍传 `urgent=false`，不能当成原设计的紧急 GF(16) 路径已经接通。
- 规范里让客户端内核始终以为工况很好：预算回路（CONGESTION_WARN、`rx_win_grant`）和断网尾部丢包隐藏，仍在进行。
- 评审指出、尚未在主线收完的计量与许可问题：JOIN 生命周期、修补节拍与“已确认送达”的分计、D-SACK 依据真实接收历史。
- 默认 `--window=1024`、接收历史 320 步，在 400 ms RTT 的理想在途预算约 299 Mbit/s；进入原场景前要先冻结窗口和内存。
- 400 ms RTT、每五元组 4 Mbit/s、256 路及 30% 丢包的原验收场景：测试床刚提供配置和短校准，还没有大战役数据。
- CTRL 换钥、关路径；t9u 其余项（HHF 保持定时器、晚绑定、浅队列需与就绪集和交互信用一起再试，等）。

**已知限制**

- 吞洞让客户端内核 TCP 看不到上行丢包。每路径 600 000 B 的窗口已经把上行排队从约 300 ms 降到约 110 ms；排队明显改善，吞吐未见一致的下降趋势，但还不宜写成已经证明完全不退（见 `docs/cases/uplink-window.md`）。
- 测量环境是多个代理共用的一台机器，别的进程会抢 CPU，整机也出现过数分钟的暂停。单轮结果有噪声，结论以多轮、同一时段交替对比为准。

`Tbt.Contracts` 仍被各模块使用（布局常量、时钟、熵接口），保留。
