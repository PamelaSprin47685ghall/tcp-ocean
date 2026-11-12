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
