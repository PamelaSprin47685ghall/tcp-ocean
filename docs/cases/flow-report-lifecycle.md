# FLOW_STATE pending-report lifecycle

Base: `701e228`. This change fixes report bookkeeping, not credit policy, ACK scheduling, rate estimation, write gating, or terminal-state protocol completion.

## Mechanism

`FlowTable.ReportsDue` counts flows with a nonzero local `Flow.ReportsDue`. The local value counts remaining repetitions (normally two). These are different quantities.

A flow could close with its local mark still set. Before a later `TakeReport` visit cleared that inactive mark, a new generation could reuse the slot. `Flow.Start` reset the local mark to zero without removing the previous global contribution. Opening the new generation then added another contribution. Even after all real reports were sent, the global count remained positive.

`Session.Tick` consults that global count. The resulting empty incremental FLOW_STATE frames did not remove the lost contribution: no local mark remained to debit. Periodic snapshots did not repair the count either.

## Controlled reproduction

The regression uses ordinary Session APIs and authenticated frames. The client allocates and resets slots 0 through 255, with each real priority WIN_ACK allowing local closure. Lower-priority report production/service is deferred. Slot 0 is then reused at generation 2. This is a controlled legal schedule, not a measurement of how often it occurs in deployment.

After draining the legitimate snapshot and two nonempty incremental reports, the unmodified baseline retains `ReportsDue=1`. Four subsequent 20 ms rounds each produce an empty incremental FLOW_STATE. The test checks the actual snapshot flag, so a legitimate empty snapshot is not confused with the defect. With the fix, no such residual reports remain.

## Minimal change and invariant

At the existing successful `tryClose` transition, retire a nonzero local report mark and subtract exactly one global contribution, before invoking `port.Close`. Do not subtract the remaining repeat count. Leave the close predicate and all other state machines unchanged.

At public operation boundaries the intended invariant is:

```
ReportsDue = number of flow slots whose local ReportsDue is nonzero
```

Credit refresh creates a contribution only on zero-to-nonzero transition. `TakeReport` removes one only when the local mark becomes zero. Actual close now performs the same transition before either local allocation or a pending remote OPEN can reuse the slot. Later report scans cannot double-decrement. This argument concerns normal FlowTable lifecycle operations, not arbitrary external mutation of public Flow fields.

Existing scheduled output or frozen snapshots are not cancelled. A previously scheduled empty increment may still be emitted once; the fix prevents persistent phantom scheduling, not every empty page.

## Regression coverage

Tests preserve the two incremental repetitions of a live flow; distinguish snapshots; exercise remaining marks 0, 1 and 2 at close; isolate other live slots; cover generation reuse; and retain marks during half-close, reserve drain and custody wait. A frozen snapshot survives closure unchanged. Existing assertions are not weakened.

## Explicit limits

Specification 6.10b.3 rule 6 requires terminal state to enter at least one full snapshot. Current `Reportable` includes only active receive flows and inactive `TakeReport` already discards marks. This patch does not add terminal tombstones or solve that pre-existing conformance gap. It does not remove an otherwise reportable closed record, and does not change records already frozen into a snapshot.

No throughput improvement, official matrix acceptance, or NativeAOT validation of this patch is claimed by these deterministic regression tests. The official valid-sample comparisons remain separate.
