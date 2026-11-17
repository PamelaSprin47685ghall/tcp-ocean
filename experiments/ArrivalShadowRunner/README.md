# Arrival shadow API runner (experimental, NOT FOR MERGE)

Uses the observation-only B1 sampler. This separate executable references product libraries, not the CLI or NativeLib. No production CLI switch, runtime observer hook, ACK/gate/credit/recovery or controller modification is introduced. One session, one path; no reconnect/JOIN, server session farm, or production option compatibility. It is a narrow diagnostic harness, not the official e2e benchmark or a production replacement.

## Modes (use the SAME mode at both ends)

- `legacy-direct`: old negotiated format, direct product path, no observer.
- `extension-direct`: opt-in arrival feedback, direct product path, no observer.
- `extension-forward`: opt-in feedback, forwarding-only decorator, no sampler/buffer.
- `extension-shadow`: opt-in feedback, decorator plus shadow and bounded capture.

Each mode uses identical carrier and TUN service-loop code. Only the negotiated capability and explicit observer selection differ. Retain separate comparisons: legacy→extension protocol cost; direct→forward decorator cost; forward→shadow sampling/capture cost. Never attribute all three to the estimator.

## Entry points (isolated guest only)

Use an externally prepared synthetic 32-byte hexadecimal PSK file. Do not use real account credentials. An external driver owns the official netns guard/lock, `netns.sh up/down`, echo workload, process lifetimes and artifact export. This runner does not configure host routing or namespaces.

Example server in `tbe_far`:

```
arrival-shadow-runner --role server --mode extension-shadow --psk-file /work/test.psk --tun tbt0 --tun-listen 10.200.3.2:8443 --target 127.0.0.1:9001 --seconds 10 --output /work/shadow-server
```

Example JIT client in `tbe_near`:

```
dotnet arrival-shadow-runner.dll --role client --mode extension-shadow --psk-file /work/test.psk --server 10.200.3.2:8443 --listen 127.0.0.1:9000 --seconds 10 --uplink-pace 0 --output /work/shadow-client
```

The external echo client connects to port 9000 after `ready role=client`. The target echo server must already listen on far port 9001. Client initiates SESSION_CLOSE after the active duration, permitting two seconds to complete it; server permits five seconds beyond its duration. Exit 2 means failure, missing close completion, or incomplete shadow capture. Workload byte correctness/completion must be checked externally; exit 0 alone is not an echo success assertion. Defaults are a 10-second active window and at most 16384 records. Duration 1..120 and capacity 1..16384 only.

Server uses real TunStack/TunPath, requiring `/dev/net/tun`, CAP_NET_ADMIN and existing isolated routes; client is real kernel TCP via Socket/PathLink. The simplified bounded drain loop is a runner-specific schedule, NOT a claim to exactly reproduce the production adaptive drain loop. Do not compare runner rates with historical official QEMU throughput as if interchangeable.

## Authentication and transparency boundary

The IPath decorator is supplied on the first and only SessionCarrier.AddPath/Attach. It must not be installed by attaching again after an existing TunPath enters data phase. Each original owner callback completes first. Only an increase in the original Session Rx accepted-cell counter permits the observer to read the still-live plaintext frame. The product has already authenticated, replay-filtered and validated Meta. WIN_ACK schema/bounds are then checked by the existing codec and shadow helper. No second decrypt or copied key state is used. All original callback returns and control methods are forwarded; diagnostic exceptions are contained and mark capture invalid.

The decorator hides the PathLink runtime type from SessionCarrier.Describe. Accordingly this runner NEVER presents its Describe output as standard `kernel_path` evidence. It retains the actual PathLink/TunPath object and reads raw boundary TCP_INFO or Tun counters once at each end of the measured interval. These are explicitly named `raw_path_start/end`, with path 0/incarnation 1 in the per-process summary. Queue coverage is boundary-only in this minimal version: no Tx.Queued or TunPath.Inflight peak is collected, so a small terminal value does not exclude intermediate session/TUN queuing. Client unsent bytes are not sampled per loop: PathLink.UnsentBytes invokes a probe syscall. Its raw boundary TCP_INFO snapshots cannot exclude an intermediate kernel-unsent peak. One unit test demonstrates the Describe classification discrepancy; a separate real TunPath packet fixture verifies unchanged first attachment and packets with forwarding-only wrapping. This alone does not establish OS-level transparency; guest evidence and limitations are tracked in the [results summary](../../docs/experiments/arrival-evidence-results.md).

## Bounded capture and interpretation

Only extension-shadow allocates the fixed record array. Per accepted WIN_ACK: local observation time, receiver timestamp, cumulative coded bytes, local generated bound, outcome/status, and sample coverage/rate are recorded. No payload, PSK, key, command line, or arbitrary exception message is logged. No per-packet file writes or formatting. CSV is serialized after the loop; summary follows. Output/serialization failure returns a failure exit, not success.

Overflow increments `dropped` without resizing. Internal collector errors and wrapper failures invalidate capture; `capture_complete=false` is not a successful trace. `capture_complete` measures storage/collector integrity, not sampler validity. Explicit `samples`, `invalid_reports`, `rejected_reports`, `shadow_status` (same codes as CSV) and `shadow_usable` distinguish feedback capture from usable shadow samples. A complete trace can faithfully record an invalid shadow state; no reports/no samples, any rejected evidence/local-time report, or invalid shadow state sets shadow_usable=false and exit 2. Normal repeated/reordered reports do not invalidate quality. A rejected local-time row retains the last known status without calling Snapshot with the bad time; its outcome code 2 explicitly identifies the rejection. It does not imply valid capacity evidence. Buffers/errors do not alter the original protocol return or fail the real session. CSV outcome codes: 0 disabled, 1 rejected evidence, 2 rejected local time, 3 reordered, 4 repeat, 5 baseline, 6 feedback-only, 7 deferred, 8 sample, 9 invalidated. Status codes: 0 unknown, 1 fresh feedback, 2 stale feedback, 3 overflow, 4 invalid generated bound, 5 timestamp regression, 6 timestamp range.

Local timestamps are observations immediately AFTER the original owner finishes, not kernel arrival or physical wire timestamps. Receiver RxTimeUs remains peer-reported. All B1 clock/coverage/trust limits apply. No estimated value is consumed by a controller.

## Build / AOT boundary

Normal managed restore/build and unit tests run on the host. For the Linux-musl guest, restore/build with `-r linux-musl-x64 -p:PublishAot=true`; actual native linking is a separate `dotnet publish ... -p:PublishAot=true` in the prepared guest/toolchain. A managed cross-RID build alone does not prove native linking; the separate guest build and run status is reported in the [results summary](../../docs/experiments/arrival-evidence-results.md). The isolated project bypasses the unrelated baseline NativeLib constructor error.

## Low-disturbance pilot and comparisons

First perform short bounded single-path runs with byte-exact echo verification, ensuring all modes close cleanly and shadow rows do not drop. Repeat the four modes in forward and reversed order with identical fixed workload, seed/profile and artifacts. Every mode also records bounded scalar loop iteration count and maximum existing CachedClock.Tick-to-Tick gap, without additional per-loop clock or /proc reads. These are wall-clock intervals including readiness waits, scheduling and GC, not thread CPU. No histogram allocation or periodic output occurs. Process cumulative CPU is read only at start/end of the post-bootstrap service interval, not via the rejected expensive process/thread observer. Session wall/CPU measurement excludes bootstrap and final serialization. Separate export_wall_ms/export_cpu_ms measure CSV serialization and file close using boundary-only Process.TotalProcessorTime; summary serialization and total process startup/exit remain covered only by the external driver. Raw socket boundary probes occur outside that interval. Per-report collector ticks are elapsed wall-clock ticks (including possible GC/scheduling), NOT thread CPU, and exclude decorator dispatch cost; compare modes for that cost. Driver wall/end-to-end time must also be retained to expose output/exit overhead.

Before interpreting rates, compare same-role CPU, wall duration, completion and fixed byte totals across modes. Report absolute overhead and variability; do not silently accept a budget chosen after seeing results. If pilot overhead is material, retain traces as instrumented diagnosis only, stop performance extrapolation, and revert to existing passive evidence. Building this project alone is not end-to-end validation.

## Historical host validation snapshot

Managed Release test build: 0 warnings, 0 errors. Full suite: **395 passed, 0 failed, 0 skipped**, 28.126 s (387 prior cases plus 8 runner cases). Cross-RID `linux-musl-x64` restore and build with explicit `PublishAot=true`: 0 warnings, 0 errors. This historical snapshot records the AOT-configured managed build only, not native linking or the later guest experiments.

JIT output is `bin/Release/net10.0/`; musl output is `bin/Release/net10.0/linux-musl-x64/`. See [consolidated results and limitations](../../docs/experiments/arrival-evidence-results.md) for later builds, tests and guest runs. The original pilot had application-completion failures even when both runner processes exited zero. Later successful cases used a different synchronous counting-echo workload: they are not an identical rerun, do not establish a fix for the original failure, and do not establish throughput improvement.

### Optional lifecycle evidence (diagnostic only)
`--lifecycle 1` wraps the application `IFlowPort` with a bounded, single-event-loop
recorder. Default is off. It records actual successful write byte totals and lifecycle
callbacks plus runner close/exit/cleanup boundaries; no per-write clock or I/O is used.
`OUTPUT.lifecycle.csv` is exported after the unchanged carrier shutdown. This does not
wait for application drain or extend the fixed session lifetime. In particular a clean
SESSION_CLOSE is not a byte-integrity verdict. Compare application timestamps with
`session_close_before`, `summary_export_*`, and `carrier_close_*`, and distinguish real flow
`finish_*`/`close_*` callbacks from runner `CloseAll` (which bypasses IFlowPort.Close).
Diagnostic overflow/export failure is explicit and makes an opted-in run fail. This
additional observer has cost and must not be substituted for the original cost pilot.
Client-side local `TryAdd` bypasses `IFlowPort.Open`: lifecycle generation `0` therefore
means **not observed/unknown**, not a claim that the actual flow generation is zero.
Associate the single-flow pilot with its session summary and slot; do not use this
recorder as a general multi-generation identity source without additional evidence.
