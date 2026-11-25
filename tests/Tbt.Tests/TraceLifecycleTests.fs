// These tests drive the MODULE-level lifecycle (`Trace.startWithCapacity` / `Trace.stop`), so they
// touch the same process-global switch and instance that `BridgeTests` uses.

// The attribute must sit on the MODULE, as in `BridgeTests` and `FlowReportLifecycleTests`. F#
// compiles a module's `[<Fact>]` functions into a class named after the module, and xunit reads
// `Collection` from the class that OWNS the tests. An earlier version declared
// `[<Xunit.Collection(...)>] type SerialTraceLifecycle() = class end` — an attribute on an empty
// class marks nothing, so these facts ran outside the serial collection and only passed whenever
// the scheduler happened not to interleave them with `BridgeTests`. `the serial collection is
// bound to the classes that own the tests` below fails if that mistake comes back.
[<Xunit.Collection("BridgeTests serial")>]
module Tbt.Tests.TraceLifecycleTests

open System
open System.IO
open System.Threading
open Xunit
open Tbt.Contracts

let private freshPath () =
    let dir = Path.Combine(Path.GetTempPath(), "tbt-tl-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    Path.Combine(dir, "events.trace")

let private cleanup (file: string) =
    try File.Delete file with _ -> ()
    try Directory.Delete(Path.GetDirectoryName file, true) with _ -> ()

let private lines (file: string) =
    if File.Exists file then File.ReadAllLines file |> Array.filter (fun l -> l.Trim().Length > 0)
    else [||]

let private fields (line: string) = line.Split(' ')

/// Every row must be one complete record: five numeric columns, an event kind, a path id in the
/// slot range, and the checksum carried in `aux`. A blended or half-written row fails here.
let private intact (line: string) =
    let f = fields line
    if f.Length <> 5 then false
    else
        match Int32.TryParse f.[0], UInt64.TryParse f.[1], Int32.TryParse f.[2], Int32.TryParse f.[3] with
        | (true, kind), (true, step), (true, path), (true, aux) ->
            Int64.TryParse f.[4] |> fst
            && kind >= 1 && kind <= 13 && path >= 0 && path < 64
            && aux = int (step % 1000UL)
        | _ -> false

[<Fact>]
let ``restarting onto the same path replaces the file instead of failing on it`` () =
    // The previous implementation opened the new writer FIRST, so restarting onto the path the
    // old run still owned hit a sharing violation (StreamWriter opens with FileShare.Read) and,
    // worse, FileMode.Create would truncate while the old writer held its own offset.
    let file = freshPath ()
    try
        Trace.startWithCapacity file 16
        for i in 1 .. 3 do Trace.record Trace.Arrive (uint64 i) 1us (int (uint64 i % 1000UL))
        // Same path again: must succeed, and the file must contain only the new run.
        Trace.startWithCapacity file 16
        for i in 100 .. 101 do Trace.record Trace.Send (uint64 i) 2us (int (uint64 i % 1000UL))
        Trace.flush ()
        let ls = lines file
        Assert.Equal(2, ls.Length)
        Assert.All(ls, fun l -> Assert.True(intact l, "not a complete row: " + l))
        Assert.Equal<string[]>(ls |> Array.map (fun l -> (fields l).[1]), [| "100"; "101" |])
        // Exactly one writer owns the file afterwards: it is still usable, not closed by the swap.
        Trace.record Trace.Send 102UL 2us 102
        Trace.flush ()
        Assert.Equal(3, (lines file).Length)
    finally
        Trace.stop ()
        cleanup file

[<Fact>]
let ``a failed open leaves tracing off rather than half-configured`` () =
    let missing = Path.Combine(Path.GetTempPath(), "tbt-no-such-" + Guid.NewGuid().ToString("N"), "e.trace")
    try
        Assert.ThrowsAny<Exception>(fun () -> Trace.startWithCapacity missing 8) |> ignore
        // The switch must not be left armed against a run that does not exist: every later
        // record would be dropped while callers believed the trace was live.
        Assert.False TraceGate.On
        for i in 1 .. 5 do Trace.record Trace.Arrive (uint64 i) 0us 0
        Assert.Equal(0L, Trace.droppedCount ())
        Assert.False(File.Exists missing)
    finally
        Trace.stop ()

[<Fact>]
let ``records after stop are refused, and are explicitly not counted`` () =
    // Honest bookkeeping: the file is already flushed and closed by `Stop`, so a refusal after it
    // could never be reported. Refused-but-uncounted is the contract; this pins it so nobody
    // later "fixes" the count into a number no reader can ever see.
    let file = freshPath ()
    try
        Trace.startWithCapacity file 32
        Trace.record Trace.Arrive 1UL 0us 1
        Trace.stop ()
        Assert.Equal(1, (lines file).Length)
        for i in 2 .. 10 do Trace.record Trace.Arrive (uint64 i) 0us (int (uint64 i % 1000UL))
        Trace.flush ()   // no writer any more: must be a no-op, not a write to a closed handle
        Assert.Equal(1, (lines file).Length)
        Assert.Equal(0L, Trace.droppedCount ())
        Assert.False TraceGate.On
        Trace.stop ()    // idempotent
        Assert.Equal(1, (lines file).Length)
    finally
        Trace.stop ()
        cleanup file

[<Fact>]
let ``starting again after stop works and writes its own run only`` () =
    let first = freshPath ()
    let second = freshPath ()
    try
        Trace.startWithCapacity first 8
        Trace.record Trace.Arrive 1UL 0us 1
        Trace.stop ()
        Assert.Equal(1, (lines first).Length)
        Trace.startWithCapacity second 8
        Assert.True TraceGate.On
        Trace.record Trace.Arrive 2UL 3us 2
        Trace.record Trace.Arrive 3UL 3us 3
        Trace.flush ()
        Assert.Equal(2, (lines second).Length)
        Assert.All(lines second, fun l -> Assert.True(intact l))
        Assert.Equal(1, (lines first).Length) // the retired run was not appended to
    finally
        Trace.stop ()
        cleanup first
        cleanup second

[<Fact>]
let ``stop racing writers leaves every published row complete and never throws`` () =
    // The only two legal outcomes for a record that races `Stop` are "already in the flushed
    // prefix" and "refused". What must never happen is a torn row, an exception escaping the
    // writer, or a row appended after the handle was closed.
    let file = freshPath ()
    let writers = 4
    let perWriter = 300
    try
        Trace.startWithCapacity file (writers * perWriter)
        use start = new ManualResetEventSlim(false)
        let errors = System.Collections.Concurrent.ConcurrentQueue<exn>()
        let threads =
            [| for w in 0 .. writers - 1 ->
                   let t =
                       Thread(fun () ->
                           start.Wait()
                           try
                               for i in 1 .. perWriter do
                                   let step = uint64 (w * perWriter + i)
                                   Trace.record Trace.Send step (uint16 w) (int (step % 1000UL))
                           with e -> errors.Enqueue e)
                   t.IsBackground <- true
                   t.Start()
                   t |]
        start.Set()
        Thread.Sleep 2          // let writers get going, then stop in the middle of the burst
        Trace.stop ()
        for t in threads do Assert.True(t.Join 30000, "writer did not finish")
        Assert.Empty(errors)
        let ls = lines file
        Assert.All(ls, fun l -> Assert.True(intact l, "torn or post-close row: " + l))
        Assert.True(ls.Length <= writers * perWriter)
        // Each recorded step appears at most once: the ring appends, it never rewrites a slot.
        let steps = ls |> Array.map (fun l -> (fields l).[1])
        Assert.Equal(steps.Length, (steps |> Array.distinct).Length)
    finally
        Trace.stop ()
        cleanup file

// ---- the isolation itself is a testable claim ----

[<Fact>]
let ``no Collection attribute sits on a class that owns no tests`` () =
    // The mistake this catches: `[<Xunit.Collection("BridgeTests serial")>] type SomeMarker() =
    // class end`. xunit reads `Collection` from the class that owns the test methods; an attribute
    // on a marker class marks nothing, so the facts silently run in parallel. That is not a style
    // point — it is how the previous trace tests passed in a solution run and failed alone.
    // A `CollectionDefinition` is a *different* attribute, so every class carrying `Collection`
    // must own at least one fact.
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    let owners =
        asm.GetTypes()
        |> Array.filter (fun t ->
            t.GetCustomAttributes(typeof<Xunit.CollectionAttribute>, false).Length > 0
            && not (
                t.GetMethods()
                |> Array.exists (fun m -> m.GetCustomAttributes(typeof<Xunit.FactAttribute>, false).Length > 0)))
        |> Array.map (fun t -> t.FullName)
    Assert.Empty owners

[<Fact>]
let ``the modules that drive the process-wide trace are in the serial collection`` () =
    // Pin the three owners that touch the process-global trace. If a new module starts using it,
    // adding it here is the deliberate step that keeps it out of parallel execution.
    let asm = System.Reflection.Assembly.GetExecutingAssembly()
    for name in [ "Tbt.Tests.BridgeTests"; "Tbt.Tests.FlowReportLifecycleTests"; "Tbt.Tests.TraceLifecycleTests" ] do
        let t = asm.GetType name
        Assert.NotNull t
        let attrs = t.GetCustomAttributes(typeof<Xunit.CollectionAttribute>, false)
        Assert.True(attrs.Length = 1, name + " must carry exactly one [<Xunit.Collection>] on the module")
