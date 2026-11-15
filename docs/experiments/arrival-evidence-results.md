# Experimental arrival evidence: review status and measured limits

**Draft, not ready to merge. No throughput fix is claimed.**

Reviewed base: `496d511a45053253970f78509803ddad5877c955`. This work adds default-off, bilaterally negotiated arrival evidence, an observation-only shadow sampler, and independent diagnostic runners. It does not connect shadow estimates to RateGroup, change pacing defaults, replace copy-estimate telemetry, redesign ledger credit, or modify protected ACK/write-gate/recovery behavior.

Capability `0x0800` and WIN_ACK bytes 80–95 are **tentative experimental allocations, not globally reserved**. Maintainers must coordinate protocol allocation and unpublished parallel work before any integration. The visible open uplink-evidence diagnostic PR does not assign these fields; that is not proof of global availability.

## Verification layers

- Latest test-project suite: **400 passed, 0 failed, 0 skipped**. New lifecycle tests preserve existing assertions. Successful single-process builds: zero warnings/errors.
- Actual legacy/new assembly interoperability was checked in both directions, plus deterministic default handshake/DATA/WIN_ACK byte equivalence. The in-process TunPath packet fixture uses a simulated TCP peer; it is not an OS end-to-end test.
- The independent runner was separately published with NativeAOT inside Alpine Linux and exercised actual client kernel TCP ↔ server TunPath/TUN. Client execution was JIT; server execution was NativeAOT.
- **The complete solution is not green:** clean base also has `Tbt.NativeLib/ClientHost.cs:188` CS7036 concerning `offerChaCha20`. This unrelated baseline/parallel API issue was not patched or suppressed. No audit/warning checks or protected assertions were disabled.
- Testbed guard tests: 11 passed. Echo test assertions passed, but existing shutdown `OSError(107)` diagnostics were observed; this is not an exception-free claim.

## Original real-Linux four-mode pilot: retain failures

One session/path; QEMU TCG, 4 vCPU/6 GB; 20 ms one-way delay, 10 Mbps shaper, zero configured loss, seed 7; identical deterministic 4 MiB echo, fixed 15-second client session lifetime. Order was legacy → extension-direct → extension-forward → extension-shadow, then reverse. An older idle VM remained a platform confound. This is not the official e2e matrix or 64-path validation.

| Mode | Complete application checks | Received bytes in incomplete cases |
|---|---:|---|
| legacy-direct | 2/2 | none |
| extension-direct | 0/2 | 4,034,848; 4,150,436 |
| extension-forward | 1/2 | 2,274,544 |
| extension-shadow | 0/2 | 3,479,536; 3,513,536 |

All five failures sent 4,194,304 bytes but received short echo then EOF. Endpoint exit codes were zero: **clean session close is not application completion**. These cases remain failed, regardless of valid feedback traces.

Both shadow cases produced complete, independently checked CSVs: client samples 170/212, server samples 289/242, with zero dropped/error/rejected/invalid reports. Counters were monotone, frame-aligned, no greater than generated evidence; sample deltas, coverage and rates recomputed exactly. This establishes observation over real Linux paths, not suitability for control.

Collector elapsed wall time was client 101/106 ms, server 25/24 ms. It can include preemption and is neither CPU cost nor the whole wrapper cost. CSV export was separately measured outside the service window. Large service-loop gaps and incomplete unequal work preclude a reliable overhead percentage. Do not discard failures to claim cheap observation.

## Lifecycle investigation: useful evidence, unresolved original failures

The five original short EOFs appeared approximately 15.094–15.405 seconds after client-ready, near the runner's fixed session deadline. The runner requests SESSION_CLOSE at that deadline without waiting for all flows to drain, then eventually calls `carrier.Close()` / `FlowBridge.CloseAll()`. Correlation did not prove this caused those particular failures.

The optional lifecycle recorder distinguishes flow Finish/Reset/Close from runner close-request/export/CloseAll. Two actual loopback tests show that an exact prefix followed by direct CloseAll produces EOF without Flow Finish; Flow Finish itself produces a half-close while the socket remains active. EOF alone therefore cannot certify intended work completion.

A subsequent instrumented three-mode forward/reverse batch (legacy, extension-direct, shadow) kept the 15-second deadline, 4 MiB and seed 7. **All 6 completed**, with full target/application byte counts and recorded flow Finish/Close before the session deadline. It did **not reproduce the original short echoes**. It also used a synchronous single-connection counter-bearing echo target instead of the original threaded echo server, and added lifecycle accounting. Those scheduling/instrumentation differences prevent calling it an identical rerun, a fix, or proof of the earlier cause. No further trials were added to conceal that uncertainty.

Before/after guest probes supported a common CLOCK_MONOTONIC basis for Python and the JIT runtime's Stopwatch conversion. They were environment probes, not in-process runner calibration or native-AOT cross-process precision guarantees. Within-process callback order is the stronger evidence. Target successful-send counts mean local socket acceptance, not peer consumption.

## Equal-input offline decomposition

See [ArrivalOfflineBench](../../experiments/ArrivalOfflineBench/README.md). Four captured numeric traces were reconstructed as canonical synthetic authenticated plaintext, not original ciphertext or session identity. Across 24 processes, parser/forward semantic checksums matched, sample results matched the captures, and untimed actual Collector replay CSVs matched byte-for-byte.

There were 24 first-pass and 120 warm measurements. Warm median CPU was roughly 13–22 ns/report for direct parsing, 17–25 for forwarding, and 101–156 for parser + sampler/snapshot/checksum work. Forwarding differences changed sign between inputs. Sampling-stage allocation was about 390–404 bytes/report, including snapshot/checksum/construction work. These are measured component workloads, **not pure method attribution, complete Collector cost, or QEMU/production performance**. Authentication, setup and Collector verification were outside those timing windows.

## Review gates and reproducibility

1. Coordinate the tentative capability/extension allocation and review the trust model (authenticated peer reports are not physical capacity proof).
2. Keep defaults off and shadow disconnected from control. Preserve recovery/repair and old interoperability gates.
3. Investigate the original application failures with unchanged test endpoints and explicit lifecycle evidence; do not relax timeouts or treat the later instrumented passes as replacements.
4. Re-run the official validity matrix before any capacity/throughput claim. No such claim or merge approval is implied here.

Source tools and tests are included; VM images, private raw archives, local absolute paths and credentials are not. The numeric-only captures under `experiments/ArrivalOfflineBench/fixtures/` are sanitized reproduction inputs, not production traffic. The build/test and pilot summaries above intentionally preserve unsuccessful results and measurement limits.
