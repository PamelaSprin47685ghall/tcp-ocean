# One bulk upload stalls the whole session (TBT4 / 256 kernel-TCP paths)

## Symptom

A 4-worker 32 MiB `Content-Length` POST through the client stalled every flow, including
new TLS handshakes. `send()` on the application side filled the kernel buffer, then timed
out. `curl` through the same listener timed out until the client process was restarted.

Observed on the HND0TBT4 deployment: client `127.0.0.1:18443`, server
`45.11.1.62:39443`, both kernel-TCP carriers, 256 paths.

## Evidence

With `--stats-s 1` on both ends:

- Client sent one flow's full 8 MiB credit burst in about one second
  (`tx_head=409`, `grant_limit=1024`), then `tx_head` stayed near 410 for tens of seconds
  while `peer_ack` crawled (13 → 17).
- Server `csvc_us` (time inside one `c.Service poller` call) reached **33,199,557 µs**
  (33 s). `iter_us_max` reached 32,309,324 µs. During that call it consumed 8,964 frames
  (~12.6 MB), sent no WIN_ACK, and did not return to `tryAccept`.
- The server was not blocked in a syscall: every path socket was `O_NONBLOCK`, the
  service thread was runnable, and `/proc/<pid>/wchan` showed `ep_poll` between iterations.
- The server had ~430 MiB free; not memory pressure.

## Cause

Two independent defects compound.

### 1. Parities were fed to the decoder regardless of need

`RxHalf.OnCoded` sent a parity straight to the decoder whenever `decoder.PendingRows > 0`,
and `serviceHold` dumped **every** held parity as soon as the frontier stalled for 20 ms.
On a 256-path carrier, one late DATA cell creates a pending row; every subsequent parity
on every path then enters Gauss-Jordan elimination. With TCP jitter, thousands of parities
for future steps arrive before their sources. The decoder held up to 16,384 pending rows
and, for each newly known source, scanned every row (`substitute`), and for each pivot
scanned every row again (`addPivot`). On HND0's QEMU CPU (no AVX, scalar GF(16) path),
one `Service` call spent 33 s inside that elimination, so `tryAccept`, path reads and
WIN_ACK all stopped.

This is also why the client could not get credit: the server decoded nothing new, sent no
WIN_ACK, and the client's step grant stayed frozen. A new flow could be accepted, but its
first source could not be sealed (`encoder.Head = grantLimit`), so its TLS bytes never left
the client.

### 2. `Service` drained every readable path with an unbounded frame count

Each `PathLink.Receive` could process a full 64 KiB socket buffer (about 45 frames), and
`Service` visited all 256 paths before returning. A single burst therefore kept the accept
loop away for the whole elimination pass. Even with a bounded decoder this would delay
`tryAccept` and WIN_ACK behind a large burst.

## Fix

- `Decoder`: keep an inverted index `column -> rows` and update it as rows are reduced or
  their coefficients cancel. `addPivot` and `substitute` now visit only rows that contain
  the affected column instead of scanning every pending row. Pivot rows remain fully
  reduced; row storage, payloads and results are unchanged.
- `ParityHold`: store the arrival time of each parity. `serviceHold` offers a parity only
  when it is forced by NACK, when its own step has waited at least 20 ms, or when it is the
  frontier step and the frontier has stalled / loss is evident. Parities for later steps no
  longer flood the decoder because one earlier step is waiting. When the hold is full, the
  oldest parity is released or dropped (counted unused) instead of being replaced by an
  unbounded decoder row.
- `PathLink.ReceiveBudgeted` + `SessionCarrier.Service`: drain at most 256 frames per
  Service turn, round-robin across paths. Leftover buffered frames set `HasPendingRx`, so
  the client waits 0 ms and the server polls immediately; `tryAccept`, WIN_ACK and the
  local-flow reads run between slices.
- The per-flow credit path is unchanged: a slow target still stops only its own flow's
  intake once the sender exhausts that flow's limit.

## Verification

- Same 4-worker 32 MiB reproduction: server `csvc_us` peaks around 124 ms (was 33 s);
  path sets stay at 256; `tx_head`/`peer_ack` keep advancing; no restart needed.
- During 4 × 4 MiB and 4 × 16 MiB acknowledged uploads, parallel
  `curl https://1.1.1.1/cdn-cgi/trace` through the tunnel returned `ip=45.11.1.62`.
- After the upload, `flow_credit` shows `reserve=0`, `tx_head == peer_ack`, and the grant
  stays ahead of the head: the session drains and accepts new work without a restart.
- `:39443` still answers a garbage probe with zero payload; Xray `:8443`, Hysteria `:444`
  and warp-go are unchanged.

## Note on throughput

The HND0 uplink is about 2.5 Mbit/s. A 32 MiB upload takes minutes to drain even with a
healthy tunnel; new flows then wait behind that queue. The bug was the permanent stall and
the 33 s accept blackout, not the queue itself.

## Shared-pipe "fixes" reverted

A later pass treated this campus link as one shared pipe and throttled all 256 paths
together (8 KiB window clamp, 256 KiB aggregate unsent cap, erasure-shrunk full-step rho,
120 ms NACK timer, 8-cell BOOSTER burst). That was wrong: the carrier does not couple
distinct TCP 5-tuples, and those changes were removed. Window clamp is 600 KiB again,
rho is the configured 0.25, NACK stall is 20 ms.

HND0's QEMU CPU has SSE2 and not SSSE3, so GF(16) multiply-add was the scalar byte table.
`Gf16.sse2MulX` is the same field (x^4 + x + 1), shift-and-reduce, used only when SSSE3
is absent. It is not a weaker code.

## FlowBridge.Read budget defect (4096 fix)

Single-stream fast.com download measured 11 Mbit/s with 98.9% `kernel_rwnd_limited` on the
server target socket. The root cause: `FlowBridge.Read` had a budget of 16 sources per call
(~21.5 KB), and `SessionCarrier.Service` called Read once per cycle. With Service averaging
50 ms and peaking at 978 ms, the server read only ~430 KB/s from the target socket, far below
fast.com's 6-8 MB/s push rate. The target socket receive buffer filled, blocking 本地代理's
write, starving the sender, causing 3812 BOOSTER repairs and 20-38% FEC recovery rate.

**Fix:** Increased `FlowBridge.Read` budget from 16 to 4096 sources (~5.5 MB per Service cycle).

**Result:** `kernel_rwnd_limited` dropped from 98.9% to 0.017%, throughput rose from 11 to
12.43 Mbit/s, BOOSTER count fell from 3812 to 1574, and FEC recovery rate dropped to 15-47%.

The remaining 12 Mbit/s ceiling and sawtooth pattern (peaks at 65 Mbit/s, troughs near 0)
indicate丢包 recovery or other bottlenecks beyond the read starvation, but the primary
implementation defect (target socket starvation) is resolved.

## SSE2 GF(16) A/B on the async path (2026-11-18)

Same JIT binary for both sides (`/tmp/tbt-jit-both`, `tbtun.dll` sha256 `4bf70c4a…`),
4 paths, 20 ms OWD, 200 Mbit/s shaper, 10 s, `TBTUN_ASYNC_MODE=true`. The only
difference is `DOTNET_EnableHWIntrinsic=0`, which makes `Sse2.IsSupported` false and
forces the scalar GF(16) path.

| metric | SSE2 on | SSE2 off | delta |
|---|---|---|---|
| server-mux `csvc_us` peak | 167 831 µs | 328 484 µs | +96 % |
| server-bulk `csvc_us` peak | 183 216 µs | 300 562 µs | +64 % |
| server-mux `recv_us_max` | 9 991 µs | 15 209 µs | +52 % |
| server-bulk `recv_us_max` | 8 355 µs | 17 637 µs | +111 % |
| echo 64 flows | 97.0 Mbit/s | 46.2 Mbit/s | −52 % |
| echo 8 flows (first) | 125.0 Mbit/s | 103.2 Mbit/s | −17 % |
| iperf3 up (bulk) | 142.7 Mbit/s | 141.9 Mbit/s | −0.6 % |

- Logs: `/tmp/tbtun-e2e.PqboeA` (SSE2 on), `/tmp/tbtun-e2e.lkvWEr` (SSE2 off).
- `iter_us_max` is 41/42 ms with SSE2 and 42/33 ms without; the regression sits inside the
  receive/codec path, not in the poll loop.
- Bulk iperf is capped by the 200 Mbit/s shaper and hides the difference. The 64-flow echo
  mix is CPU-bound and halves.
- This is the HND0 scenario: HND0 has SSE2 but no SSSE3, so it takes `sse2MulAdd`. The SSE2
  path buys roughly 2× CPU headroom in the codec; disabling it costs about half the
  throughput on mixed small-flow load.
- The A/B requires a JIT binary because NativeAOT ignores `DOTNET_EnableHWIntrinsic`. The
  async e2e normally runs an AOT server, so this measurement used the same JIT build for
  both sides.
