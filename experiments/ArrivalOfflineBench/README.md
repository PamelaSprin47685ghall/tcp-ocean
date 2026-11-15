# Equal-input offline post-authentication decomposition

Independent experiment only. No controller, production default, Session, Collector, CLI or existing runner Program modifications. This is a managed host microbenchmark, not a QEMU benchmark or native AOT performance result.

## Inputs and reconstructed boundary

The p1-shadow/p2-shadow client/server CSVs contain numeric report evidence, not original ciphertext, keys, complete WIN_ACK headers or the original session state. For each row, this harness creates a **canonical synthetic WIN_ACK**, populating the recorded receiver timestamp and coded count and filling unavailable base-header fields with fixed valid values. A public synthetic PSK handshake establishes fixture keys. Each canonical frame is sealed/opened once with the actual crypto implementation **outside all measurement windows**, then its accepted plaintext is reused across stages. No original network packet, authentication timing, replay timing, encryption-suite overhead or protocol-extension crypto cost is measured.

All four input files use a fixed synthetic Client/Up identity, including files named `server.csv`. This intentionally validates direction-independent arithmetic only. It is NOT a real session/direction identity, JOIN/rekey lifecycle, path-group coverage, or end-to-end traffic replay. The CSV local/receiver timestamps, counts and per-row generated upper bounds are nevertheless fed unchanged to the sampling arithmetic. Frame length is taken from the fixture's negotiated 1448 MSS (1415-byte frame), and every input count must be a complete frame multiple.

## Three measured stages

- `parse-direct`: actual `WinAck.tryReadHeadWithArrival` through a direct `IPathOwner` parser adapter, with a semantic checksum.
- `parse-forward`: the identical parser owner via the actual `ObservedPath` forwarding-only decorator and a synthetic FeedPath dispatch seam. It has identical plaintext input and parse checksum. The differential includes that seam; do not name it a perfectly isolated single-method cost.
- `parse-sample`: actual parser plus a newly initialized `ArrivalShadow.Sampler` for each full replay, per-row `Snapshot`, and richer sample-result checksum. Count/time sequence is identical. The differential includes snapshot/checksum bookkeeping and per-pass construction; it is NOT pure arithmetic-only cost, actual Collector cost, full wrapper+authentication cost, or a controller change.

P/F semantic checksums must match. S hashes outcome, status, delta bytes, coverage interval and exact rate double bits against the captured CSV. Every timed batch's checksum is checked after measurement. Reconstructed plaintext SHA256 is also required identical across separate processes for the same source. These checks do not make omitted base-header fields or session identities authentic originals.

## Untimed real Collector validation

After all timing windows, a separate real Session's generated counter is advanced row-by-row using successful synthetic DATA seals until it equals the capture's local generated bound, then the actual Collector observes the canonical already-authenticated plaintext with the original local timestamp. Its exported rows must exactly equal the input CSV, not just the final cumulative total.

This setup uses approximately 4–5 thousand seals per capture. It only materializes the required upper-bound counter. It does not reconstruct original DATA/PARITY/BOOSTER choices, source slots, TCP submission, custody, decoded state, repairs or queue history. These synthetic seals and the Collector replay/export are OUTSIDE all reported component timing windows. Collector replay is a semantic test, not a cost measurement.

## Measurement contract

A new subprocess is launched for each input/stage/order combination. `first-pass-after-setup` is the stage's first invocation after fixture creation and synthetic authentication; it is NOT process startup, OS cold cache or cold crypto. Warm measurements follow 20 untimed replay passes. The supplied driver fixes `DOTNET_TieredCompilation=0` and records it. This avoids mixing tiering transitions with warm stage differences, but is not the default runtime configuration of the online runner.

CPU is process cumulative CPU from boundary `Process.TotalProcessorTime` reads, including runtime/background CPU. Wall time is Stopwatch elapsed around replay loops. Per-thread allocated bytes and process GC counts are recorded. There are no per-row wall-clock or CPU probes; captured clocks are data, never sleeps. Semantic checksums are inside the loop and part of measured work. Setup, final validation, file output and subprocess startup/exit are excluded from the inner stage window, but the driver separately records whole-process wall time.

Warm batches can be very short, so scheduling, CPU frequency, JIT aftermath and timer resolution can dominate tiny differences. Preserve raw samples and report ranges, not just ratios of medians. An observed negative wrapper differential is noise/measurement-boundary evidence, not a claim that wrapping accelerates parsing.

## Running without shared-build interference

Build the existing dependency projects first. To reuse already-built dependencies, use `-p:BuildProjectReferences=false` for this project, and do not restore shared projects concurrently. The benchmark directory receives its own copied dependency DLLs, whose SHA256 values are recorded by the driver. Do not substitute a current source revision for those binary hashes.

```
dotnet restore experiments/ArrivalOfflineBench -m:1 --disable-build-servers
dotnet build experiments/ArrivalOfflineBench -c Release --no-restore -p:BuildProjectReferences=false -m:1 --disable-build-servers
python3 experiments/ArrivalOfflineBench/run_equal_input.py --dotnet /path/to/dotnet --bench /path/to/arrival-offline-bench.dll --input-root /path/to/shadow-results --output /path/to/results --passes 1000 --trials 5
```

The driver launches four capture files × three stages, then reverses that complete order. It retains 24 first-pass results and 120 warm-batch results, stdout/error logs, Collector replay CSVs, file/binary hashes, invocation order and timestamps. A nonzero child exit or checksum discrepancy aborts instead of yielding a successful summary.

## Validated host run and narrow conclusion

Host build passed with 0 warnings/errors. All 24 child runs succeeded: P/F checksums matched, S matched capture semantics, and untimed actual Collector rows matched each capture exactly. Measurements used the copied runner DLL SHA256 `c400df812020759997c48299c5fde2955d57eec279d153fb84366fb07bbfa14d`, not a claim about a later runner build. No concurrent build or active QEMU workload was scheduled during the measurement window; an older idle VM remained, and host frequency/scheduling were not controlled.

The captured traces have 180/318/240/262 reports. Median process CPU ns/report (10 warm samples per cell):

| Capture | Direct parser | Forward path | Parser + sampler/snapshot/checksum |
|---|---:|---:|---:|
| p1 client | 13.18 | 17.93 | 155.81 |
| p1 server | 21.64 | 23.42 | 111.89 |
| p2 client | 21.68 | 16.78 | 100.85 |
| p2 server | 16.37 | 24.65 | 136.84 |

Direct/forward measured zero current-thread allocation in replay loops. The sample stage measured about 390–404 allocated bytes/report, including snapshot/outcome/checksum path construction; this is a candidate for separate allocation profiling, not attribution to a single method. Across all captures/trials CPU ranges were 12.67–59.53 ns/report direct, 15.10–51.58 forward, and 93.04–425.91 sample-stage. The forwarding differential changes sign between captures, so the current short batches do NOT establish a stable wrapper cost. First-pass wall ranges were 4.17–33.99 ms direct, 4.43–11.80 forward, and 10.66–27.51 sample-stage; these are first-call-after-setup results, not cold application latency.

The supported conclusion is semantic reproduction of these numeric traces and measurable allocation/work in the sampling path under this host experiment. These numbers do not explain QEMU application slowdown, bound online collector overhead, or establish production capacity/throughput. See [consolidated results and limitations](../../docs/experiments/arrival-evidence-results.md) for the experiment summary, including failed original guest pilots and later workload differences. Raw manifests, absolute workspace paths and captured artifacts are not included in this draft PR. The driver can generate `summary.json`, per-run JSON and a provenance manifest for an independently supplied input capture.
