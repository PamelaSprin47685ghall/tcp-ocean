module Tbt.Tests.TraceLogTests

open System
open System.IO
open System.Threading
open Xunit
open Tbt.Contracts

// These tests own a private `TraceLog`. They never touch `TraceGate` or the module's production
// instance, so they cannot interfere with any other test in this process — that isolation is the
// reason `TraceLog` was extracted from module-level mutable state.
type private Log = Trace.TraceLog

let private scratch () =
    let dir = Path.Combine(Path.GetTempPath(), "tbt-trace-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, "events.trace")

let private cleanup (file: string) =
    try File.Delete file with _ -> ()
    try Directory.Delete(Path.GetDirectoryName file, true) with _ -> ()

let private lines (file: string) =
    if File.Exists file then
        File.ReadAllLines file |> Array.filter (fun l -> l.Trim().Length > 0)
    else [||]

let private fields (line: string) = line.Split(' ')

let private isMarker (line: string) = (fields line).[0] = "0"

/// Every event row must be internally consistent. Each test encodes a checksum in the row so a
/// torn or blended row (one writer's kind with another's step) fails instead of passing quietly.
let private intact (line: string) =
    let f = fields line
    if f.Length <> 5 then false
    else
        match Int32.TryParse f.[0] with
        | false, _ -> false
        | true, kind ->
            match UInt64.TryParse f.[1] with
            | false, _ -> false
            | true, step ->
                match Int32.TryParse f.[2] with
                | false, _ -> false
                | true, path ->
                    match Int32.TryParse f.[3] with
                    | false, _ -> false
                    | true, aux ->
                        Int64.TryParse f.[4] |> fst
                        // kind is in 1..13, path is a thread id < 64, aux is step-derived.
                        && kind >= 1 && kind <= 13 && path >= 0 && path < 64
                        && aux = int (step % 1000UL)

[<Fact>]
let ``a sequential run writes exactly its records and no marker`` () =
    let file = scratch ()
    try
        let log = Log(file, 64)
        for i in 1 .. 10 do log.Record(Trace.Arrive, uint64 i, 1us, int (uint64 i % 1000UL))
        log.Stop()
        let ls = lines file
        Assert.Equal(10, ls.Length)
        Assert.Empty(ls |> Array.filter isMarker)
        Assert.Equal(0L, log.Dropped)
        Assert.All(ls, fun l -> Assert.True(intact l, "not a complete row: " + l))
    finally cleanup file

[<Fact>]
let ``refused records past the capacity are counted exactly and reported once per change`` () =
    let file = scratch ()
    try
        let log = Log(file, 4)
        for i in 1 .. 10 do log.Record(Trace.Seal, uint64 i, 0us, int (uint64 i % 1000UL))
        // Exact, not a lower bound: six calls were refused, and every one is accounted for.
        Assert.Equal(6L, log.Dropped)
        log.Flush()
        Assert.Equal(5, (lines file).Length) // 4 records + 1 marker
        let markers () = lines file |> Array.filter isMarker
        let first = markers ()
        Assert.Equal(1, first.Length)
        Assert.Equal(5, (fields first.[0]).Length)   // same five columns as an event
        Assert.Equal("6", (fields first.[0]).[1])    // cumulative refused
        Assert.Equal("0", (fields first.[0]).[2])
        Assert.Equal("0", (fields first.[0]).[3])
        // Nothing new since the last flush: no duplicate marker.
        log.Flush()
        Assert.Equal(1, (markers ()).Length)
        // Dropping continues, and the next change is reported with the new total.
        for i in 11 .. 13 do log.Record(Trace.Seal, uint64 i, 0us, int (uint64 i % 1000UL))
        log.Flush()
        let after = markers ()
        Assert.Equal(2, after.Length)
        Assert.Equal("9", (fields after.[1]).[1])
        Assert.Equal(9L, log.Dropped)
        log.Stop()
    finally cleanup file

[<Fact>]
let ``parallel writers never overwrite or blend a row`` () =
    // Eight writers, 500 rows each. A lock-free counter that two writers could read at once would
    // give a duplicate index: the total would fall short and a blended row would fail `intact`.
    let file = scratch ()
    let writers = 8
    let perWriter = 500
    try
        let log = Log(file, writers * perWriter)
        let start = new ManualResetEventSlim(false)
        let threads =
            [| for w in 0 .. writers - 1 ->
                   let t =
                       Thread(fun () ->
                           start.Wait()
                           for i in 1 .. perWriter do
                               let step = uint64 (w * perWriter + i)
                               log.Record(Trace.Send, step, uint16 w, int (step % 1000UL)))
                   t.IsBackground <- true
                   t.Start()
                   t |]
        start.Set()
        for t in threads do Assert.True(t.Join 30000, "writer did not finish")
        log.Stop()
        let ls = lines file
        Assert.Equal(writers * perWriter, ls.Length)
        Assert.All(ls, fun l -> Assert.True(intact l, "torn or blended row: " + l))
        Assert.Equal(0L, log.Dropped)
        // Every step appears exactly once: no row lost, no row duplicated.
        let steps = ls |> Array.map (fun l -> (fields l).[1])
        Assert.Equal(writers * perWriter, (steps |> Array.distinct).Length)
    finally cleanup file

[<Fact>]
let ``flush never publishes a record that is still being written`` () =
    // A flusher runs continuously while four writers record. Each flushed range must be complete:
    // if `flush` could read a slot whose words are still landing, `intact` would fail on a row
    // with a zero or a mismatched checksum.
    let file = scratch ()
    let writers = 4
    let perWriter = 400
    try
        let log = Log(file, writers * perWriter)
        // CountdownEvent rather than a shared counter: it signals completion without a byref to a
        // ref cell, and the flusher can wait with a timeout so it keeps flushing until the last
        // writer is done.
        use finished = new CountdownEvent(writers)
        let start = new ManualResetEventSlim(false)
        let flusher =
            Thread(fun () ->
                start.Wait()
                while not (finished.Wait 1) do
                    log.Flush())
        flusher.IsBackground <- true
        flusher.Start()
        let threads =
            [| for w in 0 .. writers - 1 ->
                   let t =
                       Thread(fun () ->
                           start.Wait()
                           for i in 1 .. perWriter do
                               let step = uint64 (w * perWriter + i)
                               log.Record(Trace.Known, step, uint16 w, int (step % 1000UL))
                           finished.Signal() |> ignore)
                   t.IsBackground <- true
                   t.Start()
                   t |]
        start.Set()
        for t in threads do Assert.True(t.Join 30000, "writer did not finish")
        flusher.Join 30000 |> ignore
        log.Stop()
        let ls = lines file
        Assert.Equal(writers * perWriter, ls.Length)
        Assert.All(ls, fun l -> Assert.True(intact l, "flush observed an unpublished row: " + l))
        Assert.Equal(writers * perWriter, (ls |> Array.map (fun l -> (fields l).[1]) |> Array.distinct).Length)
    finally cleanup file

[<Fact>]
let ``stopping flushes the tail and restarting does not inherit the old run`` () =
    let first = scratch ()
    let second = scratch ()
    try
        let a = Log(first, 32)
        for i in 1 .. 5 do a.Record(Trace.Arrive, uint64 i, 2us, int (uint64 i % 1000UL))
        // No explicit Flush: Stop must publish the buffered tail before closing.
        a.Stop()
        Assert.Equal(5, (lines first).Length)
        Assert.All(lines first, fun l -> Assert.True(intact l))
        // A fresh run writes to its own file only, and starts with clean counters.
        let b = Log(second, 32)
        b.Record(Trace.Arrive, 1UL, 3us, 1)
        b.Stop()
        Assert.Equal(1, (lines second).Length)
        Assert.Equal(5, (lines first).Length) // the old file was not appended to
        Assert.Equal(0L, b.Dropped)
    finally
        cleanup first
        cleanup second

[<Fact>]
let ``a stopped log refuses further records instead of writing past its ring`` () =
    let file = scratch ()
    try
        let log = Log(file, 8)
        log.Record(Trace.Arrive, 1UL, 0us, 1)
        log.Stop()
        for i in 2 .. 20 do log.Record(Trace.Arrive, uint64 i, 0us, int (uint64 i % 1000UL))
        // Refused by the stopped branch, not silently written after the writer was closed.
        Assert.Equal(1, (lines file).Length)
        log.Stop() // idempotent
        Assert.Equal(1, (lines file).Length)
    finally cleanup file

[<Fact>]
let ``a zero-sized ring is rejected rather than silently dropping everything`` () =
    let file = scratch ()
    try
        Assert.Throws<ArgumentException>(fun () -> Log(file, 0) |> ignore) |> ignore
    finally cleanup file
