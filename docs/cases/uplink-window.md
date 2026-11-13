# Uplink standing queue: per-path receive window 600 000 B (spec ch. 9)

Before: TunPath advertised 4 MiB per path. The client kernel (cubic, loss hidden by the swallow) kept
up to 4 x 4 MiB in flight, and a standing queue built at the uplink bottleneck: the client TBT SRTT
during bulk up was 280-335 ms (median), with a max of 350-430 ms.

After (c7fa6b9): TunPath advertises 600 000 B, and client path sockets set TCP_WINDOW_CLAMP 600 000.

`--window-clamp BYTES` (client and server, default 600000) now makes the advertised window and the
client clamp configurable, so a 4 MiB comparison does not require reverting.

## Same-commit repeat, 0 % loss (0c77ad1)

Interleaved 6 vs 6, `WINDOW_CLAMP=600000` vs `4194304` (4 MiB) on both sides, 20 ms OWD, 200 Mbit,
4 paths, tun carrier, 10 s bulk, `SKIP_IPERF=1`, seed 7, clock_jumps=0 on every run. pingbulk p99
is over probes fully inside load. Uplink SRTT is the client session `srtt_ms` on stats lines where
`tx_head` was advancing (bulk up). Raw records: `docs/cases/data/window-clamp-0pct.json`.

| arm | bulk up | bulk down | uplink SRTT median (max) | idle p99 | under-bulk p99 | CPU s | peak RSS KiB |
|---|---|---|---|---|---|---|---|
| 600 kB | 144.1 [144.9, 143.7, 144.9, 144.5, 142.1, 136.1] | 142.7 [141.3, 143.6, 142.1, 142.9, 142.9, 142.4] | 119 [118–123] (max 119–175) | 43.0 [41.6–54.8] | 63.8 [51.9, 61.7, 54.8, 65.9, 65.9, 80.7] | 10.73 | 61254 |
| 4 MiB | 144.2 [144.7, 144.8, 144.4, 128.5, 144.0, 141.7] | 143.6 [144.2, 143.0, 144.3, 142.8, 143.8, 143.4] | 347 [322–388] (max 428–434) | 45.1 [41.8–48.5, once 127] | 67.2 [69.0, 65.4, 55.1, 132.3, 118.3, 61.6] | 10.56 | 61786 |

Medians of six values are the mean of the two middle ones. Queueing is clearly improved: uplink
SRTT stays around 119 ms with a 600 kB clamp, and climbs to ~350 ms (max ~430 ms) with 4 MiB,
about a 3× standing queue. Throughput does not show a consistent decline: uplink medians are
144.1 vs 144.2 Mbit/s; both sides still have a one-run low (136.1 and 128.5), the same bimodal
pattern as the earlier 5 vs 5. Downlink medians are 142.7 vs 143.6 Mbit/s (4 MiB slightly higher,
well inside run-to-run spread). One 4 MiB run (pair 4) was an outlier on several tails (up 128.5,
idle p99 127, under-bulk p99 132) but still had the ~430 ms uplink SRTT of the other 4 MiB runs.

Conclusion: queueing clearly improved; no consistent throughput decline is shown. This 0 %
same-commit pairing is enough to keep that wording. It does not by itself re-prove the 5 % or
outage rows below.

## Earlier A/B vs then-main e4711ce (c7fa6b9)

A/B vs main e4711ce, interleaved, clock_jumps=0; uplink SRTT = client TBT srtt in the bulk-up seconds:

| scenario | runs | bulk up (median, values) | uplink SRTT median (max) | down | under-bulk-down p99 | CPU |
|---|---|---|---|---|---|---|
| 0 % | 5 vs 5 | 142.0 [136.6 134.2 144.8 144.0 142.0] vs 144.6 [144.7 144.7 144.0 136.2 144.6] | 118-119 (max 137, once 219) vs 300-335 (428-434) | 142.5 vs 142.7 | 53 vs 53 | 2.17 vs 2.17 |
| steady 5 % | 3 vs 3 | 151.4 vs 151.4 | 97-112 (110-122) vs 282-315 (396-408) | 135.2 vs 135.3 | 115 vs 116 | 2.28 vs 2.33 |
| outage | 4 vs 4 | 115.1 vs 115.6 (main min 88.2, this min 111.3) | 85-108 (129-138) vs 186-227 (238-287) | 108.7 vs 109.1 | 532 vs 537 | 2.75 vs 2.80 |

The earlier 0 % uplink median was 142.0 vs 144.6 Mbit/s, about 1.8 % lower; that comparison was
across two commits, and both sides were bimodal (about 136 or 144). The same-commit repeat above
does not reproduce a throughput gap.
