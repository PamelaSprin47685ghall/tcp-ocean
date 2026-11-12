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
