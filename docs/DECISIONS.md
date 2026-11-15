# 规范性决定

规范 v2.2 没有写死、但两端必须一致的几处细节。本实现按下面的规则收发；第二个实现照此即可互通。
每条给出规范章节、决定和验证它的测试。

## D1 引导帧加密体中的整数为小端（spec 6.5.2）

CLIENT_INIT 的 1008 B 明文与 SERVER_INIT 的 1032 B 明文中，所有 u16 / u64 字段（caps、unix_time_ms、
mss_client、max_paths、S、path_id、rx_win_grant0、echo_time_ms）按小端写。这与 meta（6.2）、
JOIN/JOIN_ACK 明文（6.5.7）一致。字段按表中顺序紧密排列，无对齐填充。
session_id、client_id 为 16 个原样字节（不是 GUID 的混合字节序）。

测试：`CryptoTests` 中的 CLIENT_INIT / SERVER_INIT 字节比对，参考值由
`tests/vectors/make_handshake_vectors.py` 独立生成。

## D2 k_hist 的位序（spec 6.2，meta 偏移 32，3 B）

把 3 字节按小端读成 24 位整数 h。第 i 个 4 位组 `(h >> 4i) & 0xF`（i = 0…5）存第 t − stride_i 步的
k − 1，stride = 1, 2, 4, 16, 64, 256。即 i = 0 是 t − 1 步，在第一个字节的低 4 位。

测试：`WireTests` 中的 meta 往返与 k_hist 用例。

## D3 块头十六进制的大小写（spec 6.1.1）

发送端写小写（例如 S = 1152 时为 `4c0\r\n`）。接收端大小写都接受。
块头在 AAD 中按线上原样字节参与认证，所以发送端选定后不会改变。

测试：`WireTests` 中的块头用例。

## D4 默认只通告套件 0x02（spec 6.4.1）

默认 CLIENT_INIT 的 suites 只置 bit1，SERVER_INIT 选 0x02（ChaCha20-Poly1305）。现有握手向量与此一致。
AEGIS-128L（bit0）已实现，但默认仍不通告也不选择，以免改变既有握手向量；双方显式 `offerAegis` 且本机有 AES 硬件时才协商，见 D12。ChaCha8（0x03）在双方显式提供时可以协商，见 D11。

## D5 FLOWSTATE 只与 FLOWSTATE2 一起协商（spec 6.10b.1）

caps bit8 只在 bit9 同时置位时发出；收到只有 bit8 的对端时视为两者都不启用。
因此本实现只使用 FLOW_STATE 格式 2。

## D6 服务端在收到第一个认证通过的客户端信元前不发下行步

规范允许服务端在 SERVER_INIT 之后立即发送下行数据。本实现的服务端等到第一个认证通过的
上行信元（DATA、PARITY 或 WIN_ACK）后才发下行步。这样 SERVER_INIT 丢失或被重放时，
服务端不会把加密数据发给一个没有会话密钥的对端。客户端在握手完成后立即发 WIN_ACK，
所以代价最多一个往返。

## D7 全部丢失的步：k 由 BOOSTER 解出的内容推出（spec 5.8 尾部规则）

一个步的 DATA 和 PARITY 全部丢失、后面又没有新步时，接收端从 HEARTBEAT 知道这个步存在，
但 k 不在任何 Cell 里（k_hist 只描述更早的步）。规范的 BOOSTER 覆盖全部 16 个槽，
编码时 s ≥ k 的槽按零参与组合（5.6）。所以 16 个槽全部解出后，接收端取
k = 1 + 最后一个非零符号的槽号。真实源符号不会全零：它的 16 字节信封含非零的
flow_generation（或 PAD 标志）。线上格式不变。

测试：`SessionTests` 中"全部丢失的尾部由 HEARTBEAT 发现、由 BOOSTER 修复"。

## D8 每流信用与交付预留的实现取舍（spec 6.10b）

- 接收端把本地连接一时收不下的已交付字节移入该流的交付预留（字节环，按需增长，上限为单流窗口），
  使慢读者不再占住解码窗口里的步。`delivered_offset` 含预留字节，`limit = 已被本地读走 + 单流窗口`，只增不减。
- 会话级预算：池 32 MiB 计入一切尚未被本地读者取走的承诺——预留中已到未读的字节与已承诺未到的信用都算
  （每流 = `limit - 已被本地读走`）。初始信用 C0 在池内：256 个槽的 C0（16 MiB）先行预留，因为对端随时可在
  任一空槽以 C0 开流；其余 16 MiB 为共享部分。新信用先从共享部分取预算，取不到就不发；已发出的上界不收回，
  预算只因本地读走或流关闭而释放。单流窗口 = clamp(C0 + 共享部分 / 打开的接收流数, C0, 单流上限 8 MiB)。
  代价：同时打开 3 个以上接收流时单流窗口小于旧值（4 流：约 4.06 MiB 对旧 8 MiB）。FIN/RST 之后承诺只剩预留字节。
  可检查的总量 `FlowTable.CommittedBytes`（每活动槽 max(C0, 未读承诺)，每空槽 C0）恒 ≤ 池。
  旧算法（窗口 = 池 / 流数，已发上界不收回）顺序打开 256 个不读的流时可承诺约 161 MiB；测试见
  `256 flows opened one after another ... stay within the 32 MiB pool`（旧算法 41.7 MiB > 32 MiB 即失败）。
- 每流记录独立比较 (epoch, seq)，只取更大的上界；快照不做组装——缺席不是释放证明（6.10b.3 第 5 条），
  所以单条记录单独生效即可，与组装后提交等价。增量中每个变化重复 2 份报告，另有每 1 s 冻结快照兜底。
- 未协商 bit8+bit9 时发送端不受每流上界约束，预留仍然生效，但慢读者超过预留上限后会退回旧行为（占住窗口）。

## D9 TUN 载体：一段一帧（spec 4、6.8、7）

- 服务器用户态 TCP 在协商 MSS 不小于帧长 L 时才发数据，每个 TCP 段正好是一帧。
  于是下行整帧洞可以用借壳填充，不必重建描述符。L 由会话握手给出，客户端用未修改的内核 TCP。
- 上行按帧边界切分接收字节，乱序到达的完整帧立刻交给会话。片段槽 32 个，洞超过 16 个就提前退休最老的洞。
- 吞洞：每个洞记录其后第一个认证通过的 cell 的 carrier_wm。Frontier 越过该值就立即吞，否则 15 ms 宽限期满后吞。
  租约只在数据阶段有效，且要求路径已挂接、未关闭、对端未发 FIN。宽限期内不发重复 ACK 和 SACK。
- 下行修复使用 RACK，重排窗口为 0（HOLE-1）。第 j 次修复发 min(2 + j, 4) 份（HOLE-2）。
  修复的内容是新的：优先 BOOSTER，其次最老未确认步的下一个额外校验，最后是 PAD。修复帧带 FILL 标志，cell_seq 也是新的。
  HTTP 头和引导回复按原字节重传。

## D10 TUN 载体与规范的偏差（待补）

- 速率控制用简化 BBR（交付速率采样、startup 增益 2.885、drain、8 相 ProbeBW、10 轮最大值滤波）。
  速率组在途上限在启动之后是 1.25×BDP（不再是 2×BDP），巡航增益 1.05，修补副本计入节拍；
  速率样本以发送速率为上界。会话积压少于 16 cell 的轮次算应用受限，这些轮次沿用上一轮的带宽估计；
  连续 3 轮以上受限后重新进入 startup。app-limited 或在途不足 1 BDP 的轮次会清掉
  过期的满管计数，以免方向切换后在残留带宽上提前结束启动；满管按在途 ≥ 1 BDP
  计，不要求 TryTake 拒绝，连续 3 轮无增长再退出（`2bc2968`）。
- 尾部定时器会修复所有超时未确认的段（不只是最后一次交付之后发出的段）；另有与 SACK/RACK
  无关的 RTO 兜底。令牌桶深度为 2 ms 速率（至少 4L）。
- 宽限期固定 15 ms，未按 RTT 调整。HHF 的保持定时器（hold）为 0，交互流候选尚未实现。
- 每路径接收窗口按规范第 9 章通告 600 000 B。吞洞仍让客户端内核 TCP 看不到上行丢包；
  窗口收紧后上行排队明显缩短（见 `docs/cases/uplink-window.md`），但断网造成的内核降窗
  还没有被完全隐藏（见 `docs/cases/uplink-ssthresh.md`）。

## D11 X25519 装饰器与 TBT-ChaCha8-Poly1305（spec 6.5.8、6.4.1）

X25519（RFC 7748）按规范 6.5.8：两端都置 caps.bit1 时，CLIENT_INIT 与 SERVER_INIT 各带 32 字节临时公钥，
IKM = PSK ‖ X25519(本端私钥, 对端公钥)。共享秘密为全零（低阶点）或 bit1 已置但公钥全零时，握手失败。
只一方通告该位时，交集不含 bit1，IKM 仍是 PSK。默认不通告 bit1，以免改变既有握手向量；
`ClientOffer` / `ServerOffer` 的 `offerX25519` 打开后才置位。引导帧本身仍用 ChaCha20-Poly1305（6.5.2）。

套件 0x03 是规范登记的 TBT-ChaCha8-Poly1305。归档没有完整算法说明和协商位。本实现将其定义为
RFC 8439 第 2.8 节的 AEAD，只把 ChaCha 从 20 轮改为 8 轮（4 个双轮）；Nonce、AAD、Poly1305 与 klen=32
与 0x02 相同；头部遮蔽用同样布局的 ChaCha8。CLIENT_INIT 的 suites 位图 bit2（0x04）通告 0x03
（规范只写了 bit0/bit1）。默认仍只通告 0x02；双方 `offerChaCha8` 时服务端才选 0x03，不会把 0x02 悄悄换成 8 轮。
JOIN / JOIN_ACK 的引导 AEAD 仍是 ChaCha20-Poly1305；ACK 里的 suite 字段回显本会话数据套件。

测试：`HandshakeCryptoTests` 中的 RFC 7748 向量（含 1 次与 1000 次迭代）、独立脚本 `tests/vectors/chacha8_ietf.py` 的 8 轮 KATs、
以及客户端与服务端在 X25519 / 0x03 / 两者同时开启时的互通。

X25519 使用 5×51-bit limb 的恒定时间 Montgomery ladder（cswap、固定指令序列、ladder 与求逆不分配）。
每 cell 的 AEAD 零分配：ChaCha 在 AVX2 上走 8-block Vector256 核，否则 Vector128；Poly1305 是 donna-64（UInt128 乘法，预计算 r²..r⁴，一次吸收 4 块）。
套件 0x02 也可与平台 `ChaCha20Poly1305`（Linux 上为 OpenSSL）交叉验证。TryDecrypt 不抛异常，伪造帧也不分配。

## D12 AEGIS-128L（spec 6.4.1、RFC 10032）

套件 0x01 按规范：128 比特密钥、128 比特随机数、128 比特标签；klen=16。数据帧随机数是常用的 96 比特
`[dir | BE16 path_id | 0 | BE64 cell_seq]` 再补 32 个零比特。头部遮蔽为 AES-128-ECB(K_hp, sample)
的前 8 字节。CLIENT_INIT 的 suites 位图 bit0（0x01）通告本套件，与规范一致。

算法遵循 RFC 10032（AEGIS v1.1 的 Finalize：长度以比特计，小端 LE64||LE64，再 Update(t,t) 七次；
128 比特标签是 S0..S6 的异或）。测试向量取自该 RFC 附录 A.1 / A.2 以及草稿仓库 commit d3ef9984
的 `aegis-128l-test-vectors.json` 与 `aesround-test-vector.json`。

实现只用硬件 AES：x86 上 `System.Runtime.Intrinsics.X86.Aes`（AES-NI），ARM 上
`System.Runtime.Intrinsics.Arm.Aes`（AESE+AESMC 合成与 AESENC 等价的一轮）。没有表驱动 AES，
也没有软件 AES 回退。本机没有硬件 AES 时，`advertisedBits` 即使 `offerAegis` 也不置 bit0，
`select` 不选 0x01，构造 `Aegis128LAead` / `AesHeaderMask` 会抛出；默认路径不受影响。

规范写明两端都有 AES 硬件时用 0x01，但没有写 0x01 与 0x03 同时出现在交集里时的优先级。
本实现在硬件 AES 可用且双方都通告 bit0 时优先选 0x01，然后再按 D11 考虑 0x03，否则 0x02。
默认 CLIENT_INIT 仍只通告 0x02。引导帧 CLIENT_INIT / SERVER_INIT 与 JOIN / JOIN_ACK 始终是
ChaCha20-Poly1305；0x01 只用于套件选定之后的数据帧 AEAD 和头部遮蔽。

测试：RFC 附录与 JSON 参考向量、伪造标签零分配、客户端与服务端在 `offerAegis` 时的互通，
以及只一方通告时仍落在 0x02。

## D13 客户端可移植性与 Android TUN fd 注入

规范要的是高兼容 JIT 客户端，并单独列出 Android。服务端仍是 Linux NativeAOT，其 epoll、
SIOCOUTQNSD、TUNSETIFF、TCP_NOTSENT_LOWAT、TCP_WINDOW_CLAMP、Linux TCP_INFO 快路径在探测成功时
与原先 `OperatingSystem.IsLinux()` 分支一致，默认 Linux 行为不变。

共享层把 Linux 假设改成启动时探测（`Platform.current`），失败则优雅回退，不当成“队列为零”：

| 能力 | 探测 | 缺失时的回退 |
| --- | --- | --- |
| epoll(7) | `epoll_create1` | POSIX `poll(2)`（macOS / 无 epoll 的 Unix），再不行 `Socket.Select`（Windows） |
| poll(2) | `poll(..., 0, 0)` | `Socket.Select`（只能等套接字，不能等原始 fd） |
| `/dev/net/tun` | 路径存在再 `open`/`TUNSETIFF` | `TunStack.Open` 返回 null，不调用 libc |
| 原始 TUN fd | libc 可解析 | `TunStack.Attach` 返回 null |
| SIOCOUTQNSD | 对 TCP 套接字 ioctl，成功才通告 | 改用 Linux 布局的 `TCP_INFO.tcpi_notsent_bytes`（Android：SELinux 拒绝该 ioctl） |
| Linux TCP_INFO | `getsockopt` 选项 11，optlen ≥ 104 / 148 | `KernelTcpInfo.tryRead` 为 none；写门控为“不可知”（-1），不是 0 |
| TCP_NOTSENT_LOWAT | 试 Linux 25，再试 Darwin 0x201 | 不设该选项 |
| TCP_WINDOW_CLAMP | 选项 10 | Linux/Android 不设 SO_RCVBUF（保持自动调谐）；Windows 把 SO_SNDBUF 限制在 384 KiB 作为粗门控 |
| SIGINT/SIGTERM | `PosixSignalRegistration` | Windows 上 `Console.CancelKeyPress` |
| RSS | `/proc/self/status` | `Process.WorkingSet64` |

`TunStack.Attach(fd, ...)` 是 Android VpnService 的注入入口：宿主用 `Builder.establish()` 拿到 fd
后交给用户态 TCP 栈，本进程不执行 TUNSETIFF / SIOCSIFTXQLEN，fd 仍由宿主持有。现有 `Open(name)`
只用于 Linux 命名设备，行为不变。当前 `tbtun client` 仍是内核 TCP 监听器，没有改默认数据路径。

**产品路径是 `linux-bionic-arm64` NativeAOT 共享库，而不是 `net10.0-android` / Mono。**
新增 `src/Tbt.NativeLib`（C#，`PublishAot=true`，`NativeLib=Shared`），通过
`UnmanagedCallersOnly` 导出 C ABI（`tbt.h`）：`tbt_start(fd, cfg)`、`tbt_stop()`、
`tbt_stats(buf, len)`、`tbt_set_protect(fn, ctx)`。`tbt_start` 用已有的 `TunStack.Attach`
接管宿主 fd（不关 fd、不做 TUNSETIFF），后台线程 poll/Receive/Service；`TBT_FLAG_SMOKE`
会在一只一次性套接字上调用 protect 回调。默认 `tbtun client` / 服务端 Linux 数据路径未改，
也没有动 RateGroup、startup、drain、ledger credit、deadline scheduling、lanes。

交叉与导出已在本环境验证（NDK r27c，`/workspace/tools/android-ndk-r27c`，clang 18.0.3，
Pkg.Revision 27.2.12479018；`PATH` 上 NDK clang 优先，并加上 `DisableUnsupportedError`、
`PublishAotUsingRuntimePack`、`-Wl,--undefined-version`）：

- `linux-bionic-arm64`：`tbt.so` 2.6 MiB，ELF64 AArch64，`NEEDED` 为 bionic 的
  `libdl.so` / `libm.so` / `libc.so`。`readelf`/`nm -D` 可见
  `tbt_start` / `tbt_stop` / `tbt_stats` / `tbt_set_protect`（`@@V1.0`）。当前镜像不依赖
  `libssl` / `libcrypto`（密码是托管实现）。发布目录另存一份 `libtbt.so`，供
  `System.loadLibrary("tbt")` 使用。
- `linux-x64` 宿主烟测（`tests/native/tbt_abi_smoke.c`）：同样四个导出，`socketpair`
  充当假 TUN，`tbt_start` + 写入 64 字节 + `tbt_stats` 见到 `pkts_in=1` 且
  `protect_calls=1`，然后 `tbt_stop`。探测结果仍是完整 Linux 快路径
  （epoll、SIOCOUTQNSD、tcp_info_notsent 等）。

`android/vpn/` 是规范第十四章的薄壳源码（不在本环境编 APK：没有 Android SDK / workload）：
Kotlin `TbtVpnService`（`Builder.establish()`、O_NONBLOCK、`foregroundServiceType=connectedDevice`
前台通知——通知不是存活保证）、`TbtNative`、手写 `tbt_jni.c`（`protect` thunk 在工作线程
`AttachCurrentThread`，不跨线程缓存 `JNIEnv`）。完整会话握手、真机加载仍待做。

仍待完成：

- 在带 Android SDK 的机器上编出 APK，并在真机/模拟器上加载 `libtbt.so`（AndroidSpike S1 的设备证据）。
- 把 `protect(fd)` 接到真实路径套接字的 `connect` 之前。回调和烟测已有；现有 `tbtun client`
  仍是内核 TCP 监听器，为保持默认 Linux 行为未挂钩。
- Doze 下的单调时钟推进与进程保活策略（前台通知本身不是 liveness）。
- 若日后 BCL 密码路径引入 OpenSSL，APK 需随包提供 `libssl`（当前 `.so` 的 `NEEDED` 里没有）。
- Windows `SIO_TCP_INFO` 写门控；macOS/iOS `utun` 设备（不只是注入 fd）。
- 把本地 VPN 捕获接到现有 `tbtun client` 会话（现在客户端只听本地 TCP，不读 TUN）。
