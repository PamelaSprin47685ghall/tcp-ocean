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

热路径（编码、渲染校验、封装、解封）不分配托管内存，由测试检查。

## 当前状态

- 已完成：上表四个模块及其测试（规范向量、RFC 8439 / RFC 9001 向量、独立 Python 握手向量、随机丢包解码、零分配检查）。
- 未完成：会话与调度、客户端（SOCKS5/mixed 入站，256 条 TCP 子流）、服务器（内核 socket 基线，然后 TUN + 用户态 TCP）、网络命名空间端到端测试。
- 只实现了 suite 0x02（ChaCha20-Poly1305），握手中只通告这一种。
