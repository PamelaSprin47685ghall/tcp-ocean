# Loss x paths matrix (main 16e28d6; 20 ms OWD, 200 Mbit, tun carrier, 1 run per cell, clock_jumps=0)

| loss | paths | bulk up | bulk down (2 runs) | idle ping p99 | under-bulk p50 / p99 | echo8 after bulk |
|---|---|---|---|---|---|---|
| 5 % | 1 | 151.1 | 131.2 / 134.1 | 81.7 | 72.0 / 125.7 | 150.1 |
| 5 % | 2 | 145.4 | 133.4 / 135.4 | 81.7 | 41.3 / 140.2 | 88.5 |
| 5 % | 4 | 151.4 | 135.7 (A/B median) | 42.0 | 41.3 / 126 | - |
| 5 % | 8 | 151.4 | 136.6 / 134.3 | 42.0 | 41.4 / 153.5 | 128.4 |
| 0 % | 1 | 144.8 | 142.4 / 143.4 | 71.1 | 47.6 / 53.9 | 172.7 |
| 0 % | 8 | 136.3 | 143.3 / 142.7 | 41.9 | 47.1 / 54.7 | 76.8 |

## Coding and encapsulation budget (why 143 / 200 is not a mystery)

Main still uses the default `Rho = 0.25`. In the following conditions:

- `S = 1344`
- net application bytes per source symbol = `S - 16` = 1328 B
- one frame is one TCP segment, no TCP options; IP-billed length = 1455 B
  (`L = S + 71 = 1415`, plus 20 B IP and 20 B TCP)
- every step is full (16 sources + 4 parity); no extra control, repair, or short steps

the 200 Mbit link's net goodput ceiling is:

    200 × 1328 / 1455 / 1.25 ≈ 146.03 Mbit/s

143 Mbit is about 98 % of that ceiling. Most of the "missing" 57 Mbit is the
current coding and encapsulation budget, not an idle CPU or a stalled reader.
That does not prove the CPU and the scheduler are already optimal. It does
mean the next measurement should split the bytes actually sent: necessary
parity, extra repair, short steps, and control; and how much timely recovery
each of those bought. Lowering `rho` is a candidate, but only if recovery,
tail latency, and survival stay at least as good.

An adaptive-`rho` branch has already reached about 169 Mbit/s at 0 % loss
(expected once `rho` follows the 5.11 table, 0.04–0.05 at 0–1 % loss). It is
**not merged**: under the periodic-outage load the uplink sometimes collapsed,
and that regression is still open.

## Default window versus the original 400 ms scene

CLI defaults are `--window=1024` on both sides. The receiver keeps
`CodeGraph.HistorySpan + 64` = 320 steps of history (`RxHalf`). In the ideal
case (full steps, continuous advance, no other blocking) about 704 steps are
left for in flight:

    704 × 16 × 1328 × 8 / 0.4 ≈ 299.17 Mbit/s

at 400 ms RTT. Interactive reserve and other stalls tighten this further.
`--window` is already configurable; the 400 ms scene needs the window and the
memory budget frozen before any campaign, and 4096-step options have to count
both directions of source retention, decoder rows, per-flow credit, and
resident memory together.

The server default is `--max-paths=64`. A client `--paths=256` still
negotiates at most 64 unless `MAX_PATHS` is raised. `tests/e2e/run.sh` now
stamps the requested and negotiated path counts and fails if they differ.

## This matrix does not cover the original planning scene

These cells were taken at 20 ms one-way delay and a shared 200 Mbit bottleneck,
with no per-5-tuple 4 Mbit token buckets. They do not stand in for the original
scene (400 ms RTT, 4 Mbit/s per 5-tuple, up to 256 paths, and a related-loss
case). A 400 ms profile and an optional per-tuple bucket now exist in
`tests/e2e` (`PROFILE=rtt400`, `tuple_mbit=`). A short plain-TCP calibration
(no tbtun, no campaign) measured RTT 400.1 ms, one flow 3.744 Mbit/s, and two
flows 3.744+3.744 Mbit/s; see `docs/cases/wanem-profile.md`.

Findings so far (in the 20 ms / 200 Mbit scene):

- No throughput cliff over paths at 5 %: up and down are flat across 1-8 paths.
- Lossless ceiling: down tops out at ~143 of 200 Mbit even at 0 % loss, which
  is ~98 % of the `rho = 0.25` budget above.
- 1 path, 5 %: idle ping p99 82 ms (two RTTs: one repair round) and under-bulk p50 72 ms.
  One path has no spread to hide a loss behind. 2 paths still show the 82 ms p99.
- Under-bulk p99 rises with paths at 5 % (126 -> 153 ms at 8 paths). These p99
  figures predate the pingbulk change that reports only probes fully inside load.
- echo8 after bulk varies 77-173 Mbit with no pattern. It needs repeats before any conclusion.

To do: the 1 % and 3 % rows, 2 and 4 paths at 0 %, and repeats for the cells that look off,
after the 400 ms / per-tuple scene is the one being measured.
