# Uplink after outages: client kernel ssthresh ratchets down (D-SACK branch notes)

Setup: netns, 4 paths, 20 ms OWD, 200 Mbit, 5 % loss, 300 ms outage every 3 s, tun carrier.
Probes in the client namespace: `ss -tin` every 0.5 s (cwnd, ssthresh of the :8443 path sockets)
and `nstat -az` at tunnel start and end (deltas: tunnel only).

| run | bulk up | (cwnd, ssthresh) per path at bulk-up start | DSACKUndo | LossUndo | SpuriousRTOs | Timeouts | DSACKRecv |
|---|---|---|---|---|---|---|---|
| D-SACK 513d1dd | 121.3 | (8,6) (12,154) (9,22) (15,15) | 0 | 16 | 16 | 32 | 29 |
| D-SACK (absolute counters) | 98.2 | (10,5) (11,10) (61,55) (117,105) | | | | | |
| main (absolute counters) | 132.5 | (163,191) (49,49) (25,25) (7,6) | | | | | |

Mechanism: before bulk up the paths are app-limited. Each outage costs the kernel an RTO on
really lost tail segments: ssthresh = 0.7 x cwnd, and cwnd cannot grow back while app-limited. So
ssthresh ratchets down (132 -> 71 -> 28 -> 13 -> 7 in one trace). Bulk up then starts in
congestion avoidance on most paths, and the uplink rate follows the one or two paths whose ssthresh
survived. That is the about-1-in-3 collapse.

The honest D-SACK (bytes that arrive again after being acked, swallowed or held) is sent and
received, but gives no undo (DSACKUndo = 0). Linux undoes only when every retransmission of the
episode is D-SACKed, and the outage-tail retransmissions are real losses. Spurious RTOs where only ACKs
were lost are already undone by F-RTO (LossUndo/SpuriousRTOs 16-26 per run). TCP timestamps would
give Eifel detection for the same spurious class only, so they do not help here; cost 12 B/segment (0.8 %).
