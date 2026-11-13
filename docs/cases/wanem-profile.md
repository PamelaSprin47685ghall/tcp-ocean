# 400 ms RTT profile and per-5-tuple 4 Mbit/s buckets

`tests/e2e/wanem.c` now accepts `tuple_mbit` and `tuple_bucket_bytes` (0 disables
the per-tuple shaper). Each IPv4 TCP/UDP 5-tuple gets its own leaky bucket,
applied independently of the shared directional `rate_mbit` FIFO: a packet
waiting on its own bucket does not occupy the shared link for other tuples.

`PROFILE=rtt400 tests/e2e/run.sh` changes the defaults to 200 ms one-way, a
1000 Mbit/s shared link, and `tuple_mbit=4` with a 32 kB burst
(`tests/e2e/profiles/rtt400.conf`). This is not a campaign.

## Short calibration (plain TCP, no tbtun)

`tests/e2e/calibrate_profile.sh` against main's testbed, one run, clock not
observed to jump. iperf3 omits 4 s of slow start, then measures 8 s.

| check | target | measured |
|---|---|---|
| ping RTT, 20 packets | 400 ± 10 ms | 400.1 ms (min 400.089, max 400.291) |
| one flow | about 4.0 Mbit/s (accept 3.5–4.3) | 3.744 Mbit/s |
| two flows | about 4+4=8 (accept 7.0–8.6), not a shared 4 Mbit pipe | 3.744 + 3.744 = 7.488 Mbit/s |

The one-flow result is a little under 4.0 because the shaper accounts IP-packet
bytes and iperf3 reports TCP payload. Two flows each match the one-flow rate,
so the cap is per 5-tuple. No larger path-count or loss campaign was run.
