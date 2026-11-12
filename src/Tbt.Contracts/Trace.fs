/// Opt-in latency trace of small (interactive) steps (env TBT_TRACE=<file prefix>).
/// Off: one bool test per call site, no allocation. On: fixed arrays, written out at each
/// stats tick. Times are Stopwatch ns (CLOCK_MONOTONIC on Linux), so the ends of a netns
/// test on one host share one time base.
module Tbt.Contracts.Trace

open System
open System.Diagnostics
open System.IO

/// tx: an interactive step is sealed (aux = k).
[<Literal>]
let Seal = 1
/// tx: a cell of an interactive step goes to a path (aux = 0 data, 1 parity, 2 booster).
[<Literal>]
let Send = 2
/// rx: a cell of a small step arrives (aux = 0 data, 1 parity, 2 booster).
[<Literal>]
let Arrive = 3
/// rx: a source of a small step is known (aux = 1 when recovered by parity).
[<Literal>]
let Known = 4

let mutable enabled = false
let private capacity = 1 <<< 22
let mutable private kinds: byte[] = [||]
let mutable private steps: uint64[] = [||]
let mutable private paths: uint16[] = [||]
let mutable private auxes: int[] = [||]
let mutable private times: int64[] = [||]
let mutable private count = 0
let mutable private written = 0
let mutable private writer: StreamWriter | null = null
let private factor = 1_000_000_000.0 / float Stopwatch.Frequency

let now () = int64 (float (Stopwatch.GetTimestamp()) * factor)

/// Start tracing into file (one per process).
let start (file: string) =
    kinds <- Array.zeroCreate capacity
    steps <- Array.zeroCreate capacity
    paths <- Array.zeroCreate capacity
    auxes <- Array.zeroCreate capacity
    times <- Array.zeroCreate capacity
    writer <- new StreamWriter(file, false)
    enabled <- true

let record (kind: int) (step: uint64) (path: uint16) (aux: int) =
    if enabled && count < capacity then
        let i = count
        kinds.[i] <- byte kind
        steps.[i] <- step
        paths.[i] <- path
        auxes.[i] <- aux
        times.[i] <- now ()
        count <- i + 1

/// Write out the entries since the last flush (not on the hot path).
let flush () =
    match writer with
    | null -> ()
    | w ->
        let n = count
        for i in written .. n - 1 do
            w.Write(int kinds.[i]); w.Write ' '; w.Write steps.[i]; w.Write ' '
            w.Write(int paths.[i]); w.Write ' '; w.Write auxes.[i]; w.Write ' '; w.WriteLine times.[i]
        written <- n
        w.Flush()
