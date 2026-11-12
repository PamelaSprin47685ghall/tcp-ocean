# Loss x paths matrix (main 16e28d6; 20 ms OWD, 200 Mbit, tun carrier, 1 run per cell, clock_jumps=0)

| loss | paths | bulk up | bulk down (2 runs) | idle ping p99 | under-bulk p50 / p99 | echo8 after bulk |
|---|---|---|---|---|---|---|
| 5 % | 1 | 151.1 | 131.2 / 134.1 | 81.7 | 72.0 / 125.7 | 150.1 |
| 5 % | 2 | 145.4 | 133.4 / 135.4 | 81.7 | 41.3 / 140.2 | 88.5 |
| 5 % | 4 | 151.4 | 135.7 (A/B median) | 42.0 | 41.3 / 126 | - |
| 5 % | 8 | 151.4 | 136.6 / 134.3 | 42.0 | 41.4 / 153.5 | 128.4 |
| 0 % | 1 | 144.8 | 142.4 / 143.4 | 71.1 | 47.6 / 53.9 | 172.7 |
| 0 % | 8 | 136.3 | 143.3 / 142.7 | 41.9 | 47.1 / 54.7 | 76.8 |

Findings so far:
- No throughput cliff over paths at 5 %: up and down are flat across 1-8 paths.
- Lossless ceiling: down tops out at ~143 of 200 Mbit even at 0 % loss. The loss is not the limit,
  so look at FEC overhead (rho), the rate group, and the client 64 KB read. Next to investigate.
- 1 path, 5 %: idle ping p99 82 ms (two RTTs: one repair round) and under-bulk p50 72 ms.
  One path has no spread to hide a loss behind. 2 paths still show the 82 ms p99.
- Under-bulk p99 rises with paths at 5 % (126 -> 153 ms at 8 paths).
- echo8 after bulk varies 77-173 Mbit with no pattern. It needs repeats before any conclusion.
To do: the 1 % and 3 % rows, 2 and 4 paths at 0 %, and repeats for the cells that look off.
