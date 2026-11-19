# 下行 RTT 采样含发送端排队导致速率组自锁

## 现象（2026-11-19 生产）

本地经隧道下载 proof.ovh.net 的 100 MB 文件，连续三次 30 s 只得到
1.9 Mbit/s、连接失败、2.9 Mbit/s；同一时段本地上行方向正常
（minrtt 311 ms、beginfail 0）。HND0 服务端下行 stats 显示
`minrtt 4.77 s → 13.0 s → 16.4 s`、`srtt 19.7 s`、`beginfail 49/秒`。

## 根因

`TxHalf.seal` 在 cell 入队时记录 `sealNs`；Karn 采样取
`now - sealAt`。256 路下行时 cell 在发送端自己的 pacing 队列里等待
数秒，这段排队被计入路径 RTT。最小 RTT 窗口每 10 秒轮换，原始样本
直接写入窗口，只有 SRTT 被钳制到 3 倍观测最小值；于是每个窗口
最小值最多可涨 3 倍：453 ms → 1.36 s → 4.08 s → 12.2 s。

最小值达到十几秒后，速率组的采样间隔 `max(rtt, 5 ms)`、cwnd 与
pacing 全部按十几秒缩放，发送队列进一步变长，形成正反馈自锁。

## 修复

1. `TxHalf` 新增 `firstSendNs`：首个 DATA cell 首次渲染到路径时记录
   时间，Karn 样本改为 `now - firstSendNs`（缺失时回退 `sealNs`）。
   修复期望到达时间仍用 `sealNs`。
2. 防线：已建立最小值 `m` 后，`sample > 3 * m` 视为发送端排队或停顿，
   不写入最小窗口（窗口轮换照常执行）。`rttSkipped` 仍只表示 Karn
   拒绝。

## 单测

`tests/Tbt.Tests/SessionTestsD.fs`
`a step queued before send is not counted as path RTT`：400 ms 链路上
连续六轮「入队后排队 3 s → 发送 → 400 ms → ACK」，断言
`MinRttNs = 400 ms`、`SrttNs < 600 ms`。旧实现在同一序列下 SRTT 达
841 ms、最小窗口达 3.4 s。

## 生产验证（fbb1afc6）

- 30 s 下载三次：20.2、11.5、9.9 Mbit/s（修复前 1.9、0、2.9）。
- 30 MiB 上传 11.7 s ≈ 21.5 Mbit/s，服务端计数逐字节一致。
- HND0 下行 `minrtt 331 ms`、`srtt 753 ms`，不再出现秒级最小值。
- 两端 `paths=256`；本地上行 `minrtt 313 ms`。

## 官方矩阵

`TBT_BIN` 旧（94da397b）与新（fbb1afc6）各跑一组
`PROFILE=rtt400 HANDSHAKE_S=60 tests/e2e/run.sh 200 1000 0 8 10`，
两组均因 `negotiated(mux): paths=1 != requested 8` 判 `status=1`。
新旧行为一致，说明是测试床路径协商问题而非本次改动；该项尚未取得
有效对照样本。
