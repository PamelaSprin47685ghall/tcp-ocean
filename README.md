# TBTun

TBTun v2.2 协议的 F# 实现。规范见 `docs/spec/TBTun-主文档-v2.2.md`（唯一权威来源）。

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

## 模块

依赖是平的：每个模块只依赖 `Tbt.Contracts` 和下表左侧更底层的模块。

| 模块 | 内容 | 规范章节 |
|---|---|---|
| `Tbt.Contracts` | 帧布局常量、方向、cell 类型、时钟与熵接口 | 4, 6.2 |
| `Tbt.Codec` | TBT-SC2：GF(16)/GF(2) 运算、SplitMix64 图、编码器、增量 ML 消元解码器 | 5 |
| `Tbt.Wire` | 帧长与 MSS、序号扩展、源信封、meta、WIN_ACK、CTRL、FLOW_STATE 格式 2 | 4, 6.6–6.12 |
| `Tbt.Crypto` | ChaCha20 头部保护、重放窗口、cell AEAD、CLIENT_INIT/SERVER_INIT、密钥派生、JOIN/JOIN_ACK2 | 6.3–6.5 |
| `Tbt.Session` | 会话：流槽与代、发送/接收半边、许可与确认、校验暂存、NACK/BOOSTER/HEARTBEAT、握手与入队 | 5.7–5.9, 6.5, 6.9–6.11 |
| `Tbt.Carrier` | 载体：内核 TCP 路径（成帧、NOTSENT 门控）；服务器 TUN + 用户态 TCP（吞洞、借壳下行修复、RACK、简化 BBR）；本地流桥、poll 等待、单会话驱动 | 4, 6.8, 7 |
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
服务器把它连到 `--target`。这是规范第 4 章的拓扑：TBTun 承载 mihomo 的不透明 TCP 流。
客户端始终使用未修改的内核 TCP，只有服务器端可以选 TUN 载体。

## 端到端测试

`tests/e2e/run.sh [owd_ms] [rate_mbit] [loss] [paths] [seconds]` 建立三个网络命名空间，
用 `wanem` 模拟延迟、带宽和分段丢失，然后跑逐字节校验的 echo 和 iperf3 上下行，
并报告服务器峰值 RSS 与 CPU 时间。需要 sudo、gcc、python3、iperf3。
`CARRIER=tun`（默认）让服务器在远端命名空间的 `tbt0` 上运行用户态 TCP；`CARRIER=kernel` 使用内核 TCP 基线。

### 测量（2026-11-12，单向 20 ms，200 Mbit/s，4 条路径，每例 10 s）

iperf3 经隧道（Mbit/s），普通 TCP 不走隧道作参考：

| 丢包 | 普通 TCP 1 流 / 4 流 | 内核载体 上行 / 下行 | TUN 载体 上行 / 下行 |
|---|---|---|---|
| 0% | 188 / 190 | 142 / 125 | 143 / 140（`-R -P4` 141） |
| 1% | — | 8.2 / 5.6 | 145 / 134 |
| 3% | 2.3 / 7.3 | 3.0 / 2.1 | 148 / 137 |
| 5% | 1.5 / 4.3 | 1.7 / 1.5 | 149 / 134 |

上表中的“—”表示该项未测。逐字节校验的 echo 在 TUN 载体下 4 档丢包全部正确：8×1 MB 用时 0.89–1.21 s。
内核载体在 3% 丢包时同一用例用时 29 s，在 5% 丢包时用时 51 s。
空闲会话的 echo 延迟 p50 / p99 为 40.6 / 43 ms（5% 丢包时），等于 RTT。服务器 RSS 约 61 MB。
运行方式：`CARRIER=tun|kernel tests/e2e/run.sh 20 200 <loss> 4 10`。

## 当前状态

- 已完成：上表全部模块；FLOW_STATE 格式 2 每流信用与交付预留；服务器 TUN + 用户态 TCP 载体；
  netns 端到端（echo、批量、丢包，两种载体）。
- 只实现了 suite 0x02（ChaCha20-Poly1305），握手中只通告这一种。
- 未完成：CTRL（换钥、关路径）、epoll 等待、HHF 保持定时器与交互流候选。与规范的偏差见 `docs/DECISIONS.md` D9–D10。
- 已知问题：同一会话里有批量下载时，交互流的延迟尾部很大（0% 丢包时 p99 约 1.3 s，内核载体 p95 约 0.45 s）。
  原因是发送端只有一条先进先出的 cell 队列，HHF 交互优先尚未实现。另外，吞洞后客户端内核 TCP 看不到上行丢包，上行拥塞控制不会因丢包收缩。
- 测量环境：测试机是多个代理共用的一台机器，别的进程会抢 CPU，整机也出现过数分钟的暂停（客户端与服务端统计同时中断）。
  所以单轮结果有噪声，结论以多轮、同一时段交替对比为准；`tests/e2e/run.sh` 的预检只检查本测试自己的资源（`tbe_*` 命名空间、e2e 工作目录里起的进程），不管机器上的其他进程。
- `Tbt.Contracts` 仍被各模块使用（布局常量、时钟、熵接口），保留。
