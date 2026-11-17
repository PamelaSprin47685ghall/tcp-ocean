# TODO

State as of 2026-11-17: `main` = aae7b18 (server-stall RTT guard on top of 5234d97).
Merge rule: bulk/iperf on the testbed must be no worse than `main`; echo/ping only need to stay usable.
Testbed noise note: a comment-only rebuild of `main` moves 20/200 5% bulk down by -0.9..+5.6 Mbit and
up(2) by -9..+13 Mbit per seed, so small freeze-free samples cannot settle a 1-2% difference.

## Open, ordered by value

1. **64-path `-R -P4` after a6b8ede (repair identity accounting).** A/B suggests about -8..-9 Mbit
   (61 vs 70 Mbit mean, n=5..7), still inside the older main's range. Mechanism not found. Every build caps
   near 4 MB inflight after ~5 s on this test; look there first.
2. **PR #5 (repair range history).** Rebased with the per-session 384 KiB table replaced by a small
   dictionary keyed by (tLo, span), pruned below the custody frontier; 386 tests pass on top of aae7b18.
   Held: 20/200 5% bulk down averages -1.3 Mbit (won 2 of 7 seeds), `-P4` -2.0. Needs more seeds or a cause.
3. **AEGIS-128L as default when hardware AES is present.** Wire format works, saves server CPU, fine on
   64-path, but 20/200 5% downlink loses 1.5-2.5% on every seed. Find out why before enabling by default.
4. **PR #3 (arrival evidence / shadow estimator), draft.** Cap bit 0x0800 (Reserved now 0xF000), evidence in
   WIN_ACK bytes 80-95 (decision D14), wire-identity digest test proves default-off is byte-identical to main.
   No controller yet. Shadow traces: the cumulative counter is accurate; the per-sample rate overestimates
   downlink by 5-80% under loss. A controller should use the counter's delta over a fixed window.
5. **Tail-repair timer has no backoff.** If RTT is ever underestimated for another reason, Karn's rule can
   lock it in (the stall fix only covers clock jumps). Prototype "stuck path -> double timer up to 16x"
   works in unit tests but fires in normal 64-path runs and gave no gain; needs a narrower trigger.
6. **Spurious repairs in 64-path upload phases.** ~600 per run on main, almost all unneeded: client ACKs
   queue behind its own upload (up to 1.86 s late in `-P64`). Prototype (original-copy RTT samples +
   RTT+4*RTTVAR floor) halves them but costs `-P64` upload -8 Mbit. No measurable cost today; low priority.
   Real fix needs reverse-direction queueing awareness.
7. **8-path slow-class ACK queueing** (fast-echo down1 slightly low after direction switch). Deferred.
8. **Android.** `android` branch builds a bionic NativeAOT `tbt.so` + VpnService APK; host e2e ok, never run
   on a device.
9. **Interactive latency** (deadline scheduling, lanes). Deferred to the end by design.

## Not planned
Windows/macOS ports, CAPACITY_CHANGE auto-trigger, ChaCha8/X25519 as defaults, chasing <1% gaps on 20/200.

## Testbed tips
- Short mode for 64-path: `SKIP_ECHO=1 SKIP_REF=1` (~104 s instead of ~410 s). Keep 20/200 runs full.
- The VM freezes for 100-120 ms now and then; wanem records them and runs are tagged `freeze_affected`.
  Compare freeze-free segments, but beware the small sample.
- One testbed run at a time (shared netns lock).
