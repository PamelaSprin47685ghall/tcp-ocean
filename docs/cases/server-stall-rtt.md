# 案例:仅服务端进程停顿后 RTT 塌缩 → 修复风暴(64 路 -R -P64 爬行 35 s)

修复:见本分支 `carrier: no RTT or rate sample from segments that crossed a clock jump`。

## 现象

PR #5 seed 22 的 64 路短测中,服务端事件循环停顿 454 ms 后:`seg_lost` 800 → 20000,
`rtos` 64 → 870,ACK 爬行 35 s。复现:`SERVER_PAUSE=iperf_4:4:450`(只 SIGSTOP 服务端
450 ms,客户端和 wanem 不停),main 5234d97 同样出现(/tmp/tbtun-e2e.GQXdbx):
`seg_lost` 553 → 23706,`rtos` 18 → 388,`tail_repairs` 510 → 5669,`-R -P64` 和其后的
上行 iperf 均超时;wanem 丢包 0,近端内核 `TCPTimeouts=0`。

## 哪一环出错(server-bulk.log 的 group / path 行)

恢复后 1 s 内:路径 `min_rtt_ms` 400 → 74,`srtt_ms` 83(真实 RTT ≥ 400 ms);
`repair_kib` 1.5 MB → 76 MB;组 `bw_mbit` 先被拷贝估计抬到 799,再降到 182 → 1.5 → 0.5,
startup 判满管退到 drain,`pacing_mbit=0.2`,修复大量推迟(`defer` 538k)。

## 根因

`catchUp` 把环上所有发送时间戳后移整个停顿。这只在对端也停了(整机暂停)时成立。
只有服务端进程停顿时,对端的 ACK 照常到达、在 TUN 队列里排队,恢复后才读到:
相对已后移的发送时间,它们给出 1–400 ms 的 RTT(= 停顿开始 − 发送时刻)。
于是 `minRttNs`/`srttNs` 塌缩 → 尾部定时器(1.15·SRTT)远小于真实 RTT → 几乎每段都被
修复(每次 2–4 份拷贝)→ Karn(`Retx > 0` / `heldBehindRepair`)挡住之后所有 RTT 样本,
SRTT 再也回不来;拷贝洪流又污染组带宽样本,组速率塌到 0.2 Mbit。

## 修复

`catchUp` 记下 `jumpNs`;`onDelivered` 对发送早于最近一次跳变的段不取 RTT 样本、不取路径
速率样本(计数 `jump_skips`)。整机暂停的情形不变(那时本来也不该有样本)。

回归测试:`downlink: a stall of this process alone does not collapse SRTT and min RTT`
(未修复时 min RTT 40 ms → 1 ms)。

## 未改的隐患

SRTT 若因别的原因被低估,全部段都被修复后 Karn 同样会挡住所有纠正样本,无退避。
本次触发已消除;若再出现,应考虑尾部定时器在连续无交付时退避,或用时间戳选项取样。
