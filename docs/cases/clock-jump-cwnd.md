# 案例:整机暂停后下行永久停摆(负 RTT → 组 cwnd 为负)

修复:1009576(回归测试 67b52dd)。

## 现象(/tmp/tbtun-e2e.GVA9jQ,base 5941568)

整机暂停 81 s(`clock_jumps=4`,客户端日志 00:01:23 → 00:02:47)。之后服务端下行再未恢复:
`seg_sent` 停在 22267,会话 queued 14 → 42 持续增长,客户端 `frames_in` 停在 13631 共 7 分钟,
纯 ACK 包仍在往返。

## 哪道闸门挡住(server-mux.log)

路径 1–3:`srtt_ms=-778.9 min_rtt_ms=-7079.3`;组 `bw_mbit=347.8 inflight=0`;`rq=0`、`demotions=0`。
所以不是 repairQ、不是令牌、不是降级探测,而是组 cwnd:
`RateGroup.minRtt()` 取成员最小值 → 负数 → `Cwnd = g·bw·minRtt + 4l` 为负 → `TryTake` 永远拒绝。

## 根因

`TunPath.catchUp`(停顿裁剪:把环上所有发送/交付/定时器时间戳后移 `gap − 1 ms`)只在
`Service`/`OnSegment` 里调用,发送入口(`TryBeginFrame`、`SendReliable`)不调。
恢复后会话先要帧、路径定时器还没跑时发出的段,时间戳已是恢复后的真实时间,随后
`catchUp` 又把它们后移一整个停顿 → 发送时间落在未来 → 其 ACK 给出负 RTT → `minRttNs` 为负
(10 s 窗口内不会被正样本替换,因为负数永远更小)。

## 修复

1. 发送入口先 `catchUp now`:时间戳写入环之前完成停顿裁剪,环上不会有"未来"的发送时间。
2. RTT 采样只在 `now > SentNs` 时取(防御)。
3. `RateGroup.minRtt` 忽略 ≤ 0 的成员值(防御:一个坏成员不能把整组 cwnd 打到负)。

## 测试

`TunTests`:4 路径共享一个 RateGroup,稳态发送(40 ms RTT),ManualClock 跳 81 s(对端也停,期间无 ACK),
恢复后先 `TryBeginFrame` 再 `Service`,40 ms 后 ACK。旧代码:`path 0 min RTT -80959000000 ns`;
修复后各路径 min RTT/SRTT 为正、组 cwnd 正常、300 ms 内继续发送。

## e2e 复现

`PAUSE=5/12 tests/e2e/run.sh 20 200 0 4 10`:会话建立后每 12 s 同时 SIGSTOP 服务端和客户端 5 s
(两侧时钟都跳,日志 `pause.log`)。

- 旧代码(894194e,/tmp/tbtun-e2e.1eAgSv):2 条路径 `srtt_ms=-372 min_rtt_ms=-4968`,`seg_sent` 冻结在 14753,
  bulk up/down、pingbulk 全部超时。
- 修复(1009576,/tmp/tbtun-e2e.fXYulR):无负 RTT,`clock_jumps=20`(5 次 × 4 路径),会话不断,
  每次暂停后下行回到约 195 Mbit/s(17k 段/s)。

暂停 5 s 超过 echo.py 探测超时,所以 ping 有 timeout/fail,这是测法本身的代价(旧代码同样),不是回归。
SIGSTOP 只停进程、内核照常运行(客户端内核继续 ACK);真实挂起时内核也停,但两者都会让服务端时钟跳变,
根因相同。
