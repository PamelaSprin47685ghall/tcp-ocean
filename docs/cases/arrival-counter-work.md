# Incremental arrival-counter work (draft, not performance acceptance)

Base: `5234d971378fea534dccca8cd2c77893b3c7e385`. No changes to wire formats, inferred-arrival semantics, repair scheduling, rate control, flow credit, defaults, or official measurement validity rules. The FLOW_STATE fix is already in this base; this is independent of it and the repair-range-history draft.

## Mechanism and preserved semantics

`UniqueArrived` remains the existing **inference**, not a measurement of actual coded-cell arrival. It sums custody first copies plus first copies rendered for the steps at or below the peer's monotonic `t_hi`.

Previously every advancing `OnPeerSeen` and `OnPeerAck` rescanned `[peerAck, min(peerSeenHi, Head-1)]`. With a pinned custody frontier and increasing seen frontier, visits are quadratic in the window length. Later one-step custody advances repeatedly scan the shrinking remainder, even though no remaining count changed. This work also runs when uplink pacing is disabled, but its real share of CPU has not been measured.

The candidate keeps one `int64` live sum for the unacknowledged seen range:

- A newly rendered first DATA/PARITY identity adds one only when its step is in that range. BOOSTER, extra fill parity and repeated DATA remain excluded.
- Advancing `t_hi` adds only the previously uncovered range; empty encoders and future high-water reports retain the original API behavior.
- Custody subtracts covered entries before clearing them and moves the same amounts into the custody total, using the existing ACK loop.
- **Renders do not refresh public `UniqueArrived`.** Only the same advancing ACK/Seen events publish the sum. Same/old feedback and invalid custody preserve the old lazy snapshot.

The live invariant is `sum(uniqueCells[t], peerAck <= t <= min(peerSeenHi, Head-1))`. Absolute-step lifetimes and clearing still follow existing custody/encoder rules. No new table, heap, scan timer, or per-cell object is introduced; there is one additional scalar field. Object construction cost/layout was not measured. The first-render and custody loops gain a small conditional cost, so a component speedup cannot by itself establish end-to-end non-regression.

## Reproduction and component experiment

After a Release solution build:

```sh
DOTNET_TieredCompilation=0 dotnet fsi \
  --lib:tests/Tbt.Tests/bin/Release/net10.0 \
  tests/perf/arrival_counter.fsx 7 > arrival-counter.csv
```

Build baseline and candidate separately and keep their complete matching dependency directories. The script uses real public `TxHalf` APIs, seals one-source steps with 32-byte symbols, and checks exact rendered/arrival counts. Setup is outside timing. `seen` times advancing high-water feedback with custody pinned; `ack` first marks all steps seen, then times successive custody advances; `render` times all originals without advancing feedback. Its clock is constant, so the ACK fixture does **not** exercise positive RTT sample updates. It excludes network transport, authenticated feedback parsing, receiver decoding and complete service-loop scheduling.

A first two-process pilot used the default tiered JIT and displayed obvious warm-up dependence; its unedited rows are retained as `pilot-A.csv` and `pilot-B.csv`, not pooled into the comparison. The fixed follow-up uses tiered compilation disabled and process order **A1, B1, B2, A2**, each with an explicitly retained warm-up and seven measured repetitions for every size/mode. All rows are in `data/arrival-counter/`; none was selected out for a favorable result. This is a noisy shared Linux x64 managed environment, SDK 10.0.404 / runtime 10.0.15, not a QEMU or official network test.

Medians in microseconds for each complete component loop:

| Steps | Mode | A1 | B1 | B2 | A2 |
|---:|---|---:|---:|---:|---:|
| 128 | seen | 5.298 | 0.421 | 0.421 | 5.298 |
| 512 | seen | 75.010 | 1.513 | 1.472 | 74.589 |
| 2048 | seen | 1246.702 | 5.668 | 5.649 | 1306.120 |
| 4096 | seen | 4994.189 | 11.166 | 11.166 | 4917.166 |
| 128 | ack | 5.969 | 0.971 | 0.921 | 5.939 |
| 512 | ack | 77.804 | 4.467 | 4.236 | 102.842 |
| 2048 | ack | 1334.651 | 21.401 | 20.501 | 1218.771 |
| 4096 | ack | 5248.762 | 41.541 | 41.922 | 5201.404 |
| 128 | render | 740.518 | 934.083 | 749.621 | 783.231 |
| 512 | render | 3122.128 | 3220.222 | 3122.078 | 3352.106 |
| 2048 | render | 13268.367 | 13484.033 | 12158.296 | 13778.517 |
| 4096 | render | 30879.045 | 25110.138 | 26599.486 | 24088.957 |

This supports reduced counter-update work for the constructed gap scenario. **The render-only results fluctuate and do not establish a non-regression bound.** The FSI render loop itself allocates; allocation columns are retained, not represented as a zero-allocation end-to-end result. Timed allocation does not include construction.

Measured `Tbt.Session.dll` SHA-256 (other dependencies identical):

- A: `f63b2e9f328835e40a401917dd7ea3ee1e4728511a2477660b2cf2fbc8a5b1d0`
- B: `764d4a01a07b15068db31acf3ef79c9cdc07dd7f2c67a2b18b8301734ccf9be9`

## Semantic validation and limits

`ArrivalCounterTests` uses independently tracked absolute identities from authenticated output and a slow pending-step oracle. It checks after every event: lazy render snapshots, implicit step zero, future/duplicate/old seen, invalid/duplicate custody, partial render followed by custody, urgent out-of-order first renders, fill exclusion, DATA fallback, 512-slot encoder reuse, and 65536-slot first-copy counter reuse. The oracle models the old inference, not actual received bytes. Existing `UniqueRepairTests` additionally retain real bidirectional Session regression coverage.

Keep this candidate as a draft. Full correctness/CI checks are separate from official valid multi-seed bulk/iperf and interaction comparisons. There are **zero new official throughput samples**, no demonstrated 64-path root cause, no NativeAOT semantic benchmark, and no permission to weaken no-SACK, blackout, RepairWait or measurement rules.

Validation on this exact candidate and base:

- Both full Release solution builds: 0 warnings / 0 errors, with warnings-as-errors and NuGet audit retained.
- Baseline plus the same eight added oracle cases: **380 passed / 0 failed / 0 skipped**. Candidate: **380 passed / 0 failed / 0 skipped**. The baseline result is a semantic equivalence check, not an expected-red bug fix.
- Used the complete xunit executable/DLL without test filters. Standard `dotnet test` was attempted but its Microsoft.Testing.Platform named-pipe connection failed with `SocketException (13): Permission denied` before executing tests in this sandbox. It is not reported as a pass.
- Offline guard 11/11, echo 5/5, measurement 48/48; shell syntax and diff checks passed. Echo still logged the existing background shutdown `OSError(107)` despite successful assertions and exit 0; it was not changed or hidden.
- Earlier default parallel MSBuild and first-run/cache attempts hit sandbox read-only/IPC limits. The successful builds used writable CLI/NuGet caches and `-m:1 -nodeReuse:false`, not relaxed warnings or audit. An initial benchmark-script syntax error was corrected before collecting any rows.
- Independent code review found no blocking counter-equivalence issue. NativeAOT was not run locally (no clang); remote CI remains a separate check and does not replace native semantic or network acceptance.
