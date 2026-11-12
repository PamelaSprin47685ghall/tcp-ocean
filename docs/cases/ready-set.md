# 就绪集 RS-1…3 + 有界批次 Q-3(分支 ready-set)

## 第 1 步:ReadySet 类型(Tbt.Contracts)
- 256 位,4 个 u64;Count 0..256(满与空可区分);NextAfter 带回绕、至多 5 次字探测;NextUp 不回绕,用于按槽号一遍遍历。
- 测试 6 项:增删查与计数、回绕与空集、满集遍历、与参照集 2 万次随机操作一致、非法输入先失败、1 万轮操作 0 B 分配。

## Step 2 (RS-2/RS-3): bridge sets and the credit event

- FlowBridge keeps four ReadySets: `opened`, `armed` (connected, still sending, credit > 0),
  `writers` (connecting or short write pending), and `failures`.
- IFlowPort.Credit(slot) is raised by Flows.OnPeerCredit when the peer's limit grows; a flow
  that ran out of credit leaves `armed` and comes back only on that event (no polling).
- SessionCarrier.Watch/Service iterate `armed`/`writers` with NextUp instead of 0..255 (three
  passes). `SlotVisits` counts the visits.
- Tests (BridgeTests): credit-blocked flow leaves `armed` and returns on a newer FLOW record
  (an old one does nothing); 40 open flows, 39 half-closed: 10 rounds cost 30 visits
  (before: 256 x 3 x 10 = 7680).
- Caveat: an idle armed flow is still watched each round, so cost grows with armed flows,
  not strictly with readable flows.

## Step 3 (Q-3): bounded bulk batches

- writeRound sends in batches: each batch gives each path at most 4 frames (WriteBatch), the
  round budget stays 256. Between batches the interactive slots that were readable this round
  (`hot` ReadySet) are read again, so new interactive input goes before the rest of the bulk.
- Tests: 2 counting paths, 40+ queued cells: one batch = 4 + 4 frames, a 3-frame budget = 3;
  a Service round sends 9..256. The warm Watch/Wait/Service loop allocates 0 B (100 rounds).

## E-T9c A/B, first 3 + 3 (run.sh 20 ms, 200 Mbit, 5%, 4 paths, 10 s, SKIP_IPERF=1)

A = main a1ac6a1, B = ready-set 5019192, interleaved B, A.

| run | ping p99 | ping under bulk p99 | bulk up | bulk down 1 / 2 | note |
|---|---|---|---|---|---|
| B1 | 42.4 | 140.4 | 152.2 | 131.7 / 135.5 | clock_jumps=0 |
| A1 | 41.8 | 117.6 | 142.9 | 136.0 / 132.9 | clock_jumps=0 |
| B2 | 41.9 | 100.4 | 151.5 | 132.7 / 134.8 | clock_jumps=0 |
| A2 | 41.9 | 145.4 | 135.9 | 132.3 / 133.0 | clock_jumps=0 |
| B3 | 43.0 | 225.8 | 142.3 | 135.7 / 134.4 | clock_jumps=0 |
| A3 | 41.6 | 175.1 | 9.5 | 13.1 / 133.0 | VM stall (clock_jumps 4/8, up 170 s): void, rerun |

Ping under bulk p99 spreads 100–226 ms in both arms; more pairs follow.

### More pairs (A, B, A, B, A), same settings

| run | ping p99 | under bulk p99 | bulk up | bulk down 1 / 2 | note |
|---|---|---|---|---|---|
| A3' | 41.7 | 103.6 | 151.8 | 134.3 / 133.8 | clock_jumps=0 (A3 rerun) |
| B4 | 1827.6 | 164.5 | 148.0 | 134.2 / 132.3 | clock_jumps=4: void |
| A4 | 73.1 | 202.4 | 141.3 | 134.8 / 133.2 | clock_jumps=0 |
| B5 | 41.6 | 114.0 | 151.6 | 133.9 / 133.4 | clock_jumps=0 |
| A5 | 41.8 | 108.5 | 151.8 | 134.3 / 134.2 | clock_jumps=0 |

## Result (valid runs: B n=4, A n=5): merge

| metric | B ready-set median (values) | A main median (values) |
|---|---|---|
| ping p99 ms | 42.1 (42.4 41.9 43.0 41.6) | 41.8 (41.8 41.9 41.7 73.1 41.8) |
| under bulk p50 ms | 41.3 | 41.3 |
| under bulk p99 ms | 127.2 (140.4 100.4 225.8 114.0) | 117.6 (117.6 145.4 103.6 202.4 108.5) |
| bulk up Mbit | 151.6 (152.2 151.5 142.3 151.6) | 142.9 (142.9 135.9 151.8 141.3 151.8) |
| bulk down Mbit (mean of 2) | 133.7 | 134.1 |
| server peak RSS KiB | 61674 | 63792 |
| server cpu_s | 10.5 | 10.5 |

Under bulk p99: Mann-Whitney U = 10 of 20 (exactly even): no difference shows; the spread
(100-226 ms) is the same in both arms. Bulk up is higher in 3 of 4 B runs. No metric
regresses, so ready-set goes to main. This is not a full E-T9c pass (no SF/S30 scenarios,
no 95% intervals with n=4/5); the scheduler-cost slope is shown by BridgeTests instead.
