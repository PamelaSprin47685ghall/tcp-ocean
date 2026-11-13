# Uplink standing queue: per-path receive window 600 000 B (spec ch. 9)

Before: TunPath advertised 4 MiB per path. The client kernel (cubic, loss hidden by the swallow) kept
up to 4 x 4 MiB in flight, and a standing queue built at the uplink bottleneck: the client TBT SRTT
during bulk up was 280-335 ms (median), with a max of 350-430 ms.

After (c7fa6b9): TunPath advertises 600 000 B, and client path sockets set TCP_WINDOW_CLAMP 600 000.
A/B vs main e4711ce, interleaved, clock_jumps=0; uplink SRTT = client TBT srtt in the bulk-up seconds:

| scenario | runs | bulk up (median, values) | uplink SRTT median (max) | down | under-bulk-down p99 | CPU |
|---|---|---|---|---|---|---|
| 0 % | 5 vs 5 | 142.0 [136.6 134.2 144.8 144.0 142.0] vs 144.6 [144.7 144.7 144.0 136.2 144.6] | 118-119 (max 137, once 219) vs 300-335 (428-434) | 142.5 vs 142.7 | 53 vs 53 | 2.17 vs 2.17 |
| steady 5 % | 3 vs 3 | 151.4 vs 151.4 | 97-112 (110-122) vs 282-315 (396-408) | 135.2 vs 135.3 | 115 vs 116 | 2.28 vs 2.33 |
| outage | 4 vs 4 | 115.1 vs 115.6 (main min 88.2, this min 111.3) | 85-108 (129-138) vs 186-227 (238-287) | 108.7 vs 109.1 | 532 vs 537 | 2.75 vs 2.80 |

0 % bulk up is bimodal on both sides (about 136 or 144). The 0 % uplink median is
142.0 vs 144.6 Mbit/s, about 1.8 % lower; that alone does not decide noise versus
a real decline. Queue delay on the uplink falls about 3×.

Conclusion, kept deliberately short of a "no regression" proof: queueing clearly
improved; no consistent throughput decline is shown. A further 0 % pairing of the
current 600 kB window against the old 4 MiB value would need a temporary
parameter (main has no env/CLI override for `SocketTuning.WindowClamp`, and
reverting the commit is the wrong way to compare). That re-run was not done
here: it would have held the shared netns lock for a multi-pair campaign, which
is not cheap on this box.
