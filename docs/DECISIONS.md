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

## D4 只通告套件 0x02（spec 6.4.1）

本实现只实现 ChaCha20-Poly1305。CLIENT_INIT 的 suites 只置 bit1；SERVER_INIT 只选 0x02。
AEGIS-128L（bit0）与 ChaCha8 的套件号保留，可在以后协商，但本实现不通告也不接受。

## D5 FLOWSTATE 只与 FLOWSTATE2 一起协商（spec 6.10b.1）

caps bit8 只在 bit9 同时置位时发出；收到只有 bit8 的对端时视为两者都不启用。
因此本实现只使用 FLOW_STATE 格式 2。

## D6 服务端在收到第一个认证通过的客户端信元前不发下行步

规范允许服务端在 SERVER_INIT 之后立即发送下行数据。本实现的服务端等到第一个认证通过的
上行信元（DATA、PARITY 或 WIN_ACK）后才发下行步。这样 SERVER_INIT 丢失或被重放时，
服务端不会把加密数据发给一个没有会话密钥的对端。客户端在握手完成后立即发 WIN_ACK，
所以代价最多一个往返。
