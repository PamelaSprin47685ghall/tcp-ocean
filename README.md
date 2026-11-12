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
| `Tbt.Carrier` | 内核 TCP 载体：路径成帧、NOTSENT 门控、本地流桥、poll 等待、单会话驱动 | 4, 6.8 |
| `Tbt.Cli` | `tbtun client` / `tbtun server` 命令 | — |

热路径（编码、渲染校验、封装、解封）不分配托管内存，由测试检查。

## 运行

```sh
# 服务器（NativeAOT）
dotnet publish src/Tbt.Cli -c Release -r linux-x64 -o out
out/tbtun server --listen 0.0.0.0:8443 --target 127.0.0.1:7890 --psk-file psk
# 客户端（JIT）
dotnet run --project src/Tbt.Cli -c Release -- client --server HOST:8443 --listen 127.0.0.1:1080 --psk-file psk --paths 4
```

PSK 文件每行一个 64 位十六进制密钥。客户端入站是普通 TCP 监听：每个本地连接是一条流，
服务器把它连到 `--target`（规范第 4 章拓扑：TBTun 承载 mihomo 的不透明 TCP 流）。

## 端到端测试

`tests/e2e/run.sh [owd_ms] [rate_mbit] [loss] [paths] [seconds]` 建立三个网络命名空间，
用 `wanem` 模拟延迟、带宽和分段丢失，然后跑逐字节校验的 echo 和 iperf3 上下行，
并报告服务器峰值 RSS 与 CPU 时间。需要 sudo、gcc、python3、iperf3。

## 当前状态

- 已完成：上表全部模块；内核 TCP 基线的客户端与服务器；netns 端到端（echo、批量、丢包）。
- 只实现了 suite 0x02（ChaCha20-Poly1305），握手中只通告这一种。
- 未完成：TUN + 用户态 TCP 载体（吞洞）、FLOW_STATE 每流信用、CTRL（换钥、关路径）、epoll 等待。
  内核 TCP 基线下，分段丢失由各路径的内核 TCP 重传处理，吞吐受内核 TCP 拥塞控制限制。
