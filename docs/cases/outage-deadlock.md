# Downlink deadlock after an outage: every path demoted, no segment ever sent again

## Symptom (netns, 5 % loss plus a 300 ms outage every 3 s, about 1 run in 10)
A ping under bulk timed out. Server `demotions=4`, `seg_sent` and `tail_repairs` frozen for
minutes, every path's inflight unchanged, the client received nothing. It was seen on branch rho
and once on karn (already on main), so it predates both.

## Cause (TunPath)
- RACK repairs only segments below the highest SACK of the ACK it is processing.
- The tail timer repaired only segments sent after `rackSentNs`.
- When a repaired low segment is delivered, `rackSentNs` jumps to its repair time. Higher
  segments sent before that repair are neither below the highest SACK nor sent after
  `rackSentNs`. If an outage then silences every ACK, no SACK ever covers them, so neither
  mechanism repairs them.
- After max(1 s, 3 RTO) every path is demoted. Demoted paths take no new frames, so nothing is
  delivered and nothing un-demotes.

## Fix
1. Tail timer (it fires only after a whole timer with no delivery): any unsacked segment older
   than the timer is a candidate, oldest first, at most 4 per timer. TCP-level recovery never
   stops, demoted or not.
2. Demote a path only while another path of its group delivered within the demotion time
   (`RateGroup.AnyDeliveredSince`). All paths stalling together is an outage, not a dead path.
3. A demoted path still repairs, and its first delivery restores it (unchanged).

## Tests (both fail on main)
- `segments sent before a delivered repair are still re-sent when an outage silences every
  ACK`: on main nothing is re-sent in 2 s.
- `a blackout on every path does not leave them demoted and dead` (2 paths, model receiver
  with SACK, 1.3 s blackout): main demotes a path (1 demotion); the fix has 0, and both paths
  deliver after the blackout.
- Also seen: with cumulative-only ACKs (no SACK) the same blackout left a path with no progress
  after it. Linux always sends SACK, so this is not pursued here.

## Follow-up: peers without SACK (branch rto)
A peer that disables SACK, or a middlebox that strips it, sends cumulative ACKs only. RACK
never runs, and the tail timer cannot tell where the hole is. In the model blackout test main
sent 108 segments after the blackout and made no progress for 2.2 s, and srtt inflated to
1389 ms.
- RTO fallback, independent of SACK and RACK: if snd.una has not moved for max(2 x tail timer,
  200 ms), doubled per episode up to 3.2 s, every unsacked segment sent before the timer is
  lost and goes again from snd.una (go-back-N), 4 per service call. The backoff resets when
  snd.una moves. Counted as `rtos`, and shifted on clock jumps like every other timer.
- RTT samples: when a cumulative advance is released by a repaired segment, the segments
  behind it waited for the hole, so they give no sample.
Test: `a peer without SACK still gets every hole re-sent after a blackout (RTO fallback)`
fails on main and passes with the fix (srtt < 200 ms).

### RTO fallback: netns sanity (one run each, 4 paths, 20 ms OWD, 200 Mbit, 5 % loss, tun carrier)

| scenario | bulk up | bulk down | under-bulk p99 | rtos | demotions | main 447a5c2 reference |
|---|---|---|---|---|---|---|
| steady 5 % | 142.2 | 136.0 / 134.6 | 111.7 ms | 3 | 0 | up 126-151, down ~133, p99 ~110-160 |
| 300 ms outage every 3 s | 108.6 | 106.1 / 112.8 | 536.8 ms | 69 | 0 | up 101-130, down 111-114, p99 496-535 |

No timeouts, clock_jumps=0. With SACK peers the fallback fires rarely (3 times in steady loss) and the
outage case stays within the main range.
