/// Client path-bootstrap timing and bounded JOIN retries.
/// Socket timeouts cover enough TCP RTO doublings that a JOIN can retransmit
/// inside one attempt at the design loss; exhausted attempts never throw.
module Tbt.Session.JoinBootstrap

open System

/// Design point used when the caller does not pass a measurement: the original
/// rtt400 scenario (400 ms RTT, 30% segment loss).
[<Literal>]
let DefaultRttMs = 400

[<Literal>]
let DefaultLoss = 0.30

[<Literal>]
let private Epsilon = 1e-3

[<Literal>]
let MinTimeoutMs = 10_000

[<Literal>]
let MaxTimeoutMs = 60_000

/// Linux-like initial RTO: at least 1 s, and at least two RTTs.
let initialRtoMs (rttMs: int) = max 1000 (2 * max 0 rttMs)

/// Transmissions (original + retransmits) so that losing every one at `loss`
/// has probability ≤ epsilon. Clamped to [4, 8].
let transmissionsFor (loss: float) =
    let p = min 0.9 (max 0.0 loss)
    if p <= 0.0 then 4
    else
        let n = int (Math.Ceiling(Math.Log Epsilon / Math.Log p))
        min 8 (max 4 n)

/// Connect / Send / ReceiveTimeout for one bootstrap attempt: the wait to
/// exhaust `transmissionsFor loss - 1` doubling RTOs, plus one RTT, clamped
/// to [10 s, 60 s]. At 400 ms and 30% this is 31.4 s (five doublings from 1 s).
let socketTimeoutMs (rttMs: int) (loss: float) =
    let rto0 = initialRtoMs rttMs
    let k = transmissionsFor loss - 1
    let wait = rto0 * ((1 <<< k) - 1) + max 0 rttMs
    min MaxTimeoutMs (max MinTimeoutMs wait)

/// How many bootstrap attempts (the first plus retries) for one path.
let maxAttempts (loss: float) =
    if loss >= 0.2 then 5 elif loss >= 0.05 then 4 else 3

/// Backoff before retry `attempt` (0-based, after the first failure).
let backoffMs (rttMs: int) (attempt: int) =
    let r = max 200 (max 0 rttMs)
    min 5_000 (r * (1 <<< min 4 (max 0 attempt)))

[<Struct>]
type JoinAttempt =
    | Joined
    | GaveUp of string

/// Run `tryOnce` up to `attempts` times. `tryOnce` may throw; each throw is a
/// failed attempt. `sleepMs` is called with the backoff before a retry, not
/// after the last attempt. The attempt index passed to `tryOnce` is 0-based
/// (use `uint32 (i + 1)` as the JOIN incarnation). Never throws.
let tryRepeated (attempts: int) (backoffMs: int -> int) (sleepMs: int -> unit) (tryOnce: int -> unit) =
    let n = max 1 attempts
    let rec loop i =
        try
            tryOnce i
            Joined
        with e ->
            if i + 1 >= n then GaveUp e.Message
            else
                sleepMs (backoffMs i)
                loop (i + 1)
    loop 0

[<Literal>]
let DefaultConcurrency = 16

/// In-flight JOIN cap used by the client (at least 1, at most 16).
let clampConcurrency (n: int) = min 16 (max 1 n)

/// Waves needed to join `remaining` paths at this concurrency.
let waves (remaining: int) (concurrency: int) =
    let c = clampConcurrency concurrency
    let n = max 0 remaining
    if n = 0 then 0 else (n + c - 1) / c

/// Run `work` for each item with at most `concurrency` in flight. Blocks until
/// every item has finished. Exceptions from `work` are collected and rethrown
/// as AggregateException after the others finish; a well-behaved `work` does
/// not throw (see `tryRepeated`).
let runBounded (concurrency: int) (items: 'T[]) (work: 'T -> unit) =
    let c = clampConcurrency concurrency
    use sem = new Threading.SemaphoreSlim(c)
    let tasks =
        [| for item in items do
               let captured = item
               Threading.Tasks.Task.Run(fun () ->
                   sem.Wait()
                   try work captured
                   finally sem.Release() |> ignore) |]
    if tasks.Length > 0 then Threading.Tasks.Task.WaitAll(tasks)

