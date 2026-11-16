# Dot investigation handoff — 2026-11-16

## Published code

- First-copy accounting fix is on main: a6b8ede5ed5c07f95a44b9310e10efa3593f3fd0. It avoids counting DATA repair fallback as a second unique cell while retaining wire charging. The recorded CI run passed, including NativeAOT.
- PR #2 (`d17d7f2`): offline uplink segment evidence tooling.
- PR #3 (`0b9f6bd`): experimental, default-off negotiated coded-cell feedback and shadow estimator. **Not for merge as-is:** tentative protocol bit 0x0800 and WIN_ACK bytes 80–95 require coordination; no controller integration or throughput validation.
- PR #4 (`76889915`): fixes FlowTable ReportsDue lifecycle leak on slot close/reuse.
- PR #5 (`a5b9f04`): fixes interleaved valid NACK ranges losing repair wait/seed progress. Lazy table costs approximately 384 KiB per active default-repair session at C4096; evaluate memory/GC at scale.

The PRs remain drafts; see their descriptions and current GitHub checks for exact scope and current status. Do not combine them blindly with concurrent ACK, write-gate, CTRL or startup work.

## Validation and limits

All four PR heads had successful build-test and aot-server checks when reviewed for this handoff. Local integration tests on then-current main passed for #3 (414 tests), #4 and #5 (355 each); these are not a substitute for CI on a subsequently changed main or an end-to-end performance gate.

PR #4 QEMU A/B was repeated with source-stamped binaries. A7 and B7 passed validity gates. B9 reverse iperf was invalidated by the existing `clock_jump` gate after a service gap around 393 ms; the run was stopped before A9, so all 13 comparison groups had insufficient valid samples. The event is a service-loop gap, not proof of a wall-clock adjustment or a regression in #4. An earlier A/B used an invalid provenance label for B and cannot be accepted. Neither result establishes a throughput gain or regression.

The guest was Alpine Linux under QEMU TCG without KVM. NativeAOT and netns/TUN were exercised, but TCG scheduling and host contention limit extrapolation to real hardware. Preserve invalid samples; do not weaken validity gates. Obtain at least two valid seeds per side and segment for the agreed scenarios before claiming the formal throughput gate.

## Other findings

- In a controlled iperf 3.20 experiment, 64 flows each read 64 KiB but 128 KiB-block receiver accounting reported zero at TEST_END; a 16 KiB control reported the full 4 MiB. Historical zero-flow counters alone therefore do not establish that no bytes arrived. This does not reconstruct historical byte totals.
- Receiver erasure feedback can vary with lawful frame ordering even for the same 192 loss-free authenticated frames and clock. The receiver lacks a true generated-frame denominator/deadline, so the signal must not be treated as precise loss without additional protocol evidence.
- A manually separated write-batch timing scenario showed a stale cached inflight value could shift a measured sample; a more realistic full Carrier.Service run did not reproduce a clock refresh between those frames. Production frequency and throughput impact remain unproven. No startup code change is proposed.
- A candidate receive-window scan cache was tested against directed, exhaustive and random models; no real hotspot or benefit under dirty churn was established. No product change is proposed.

The original detailed private test artifacts were not present in the current execution workspace at handoff. The numerical notes above are investigation summaries, not replacements for raw traces. Do not infer performance acceptance from them.
