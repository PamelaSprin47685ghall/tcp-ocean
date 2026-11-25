/// Opt-in latency trace of small (interactive) steps (env TBT_TRACE=<file prefix>).
/// Off: one bool test per call site, no allocation. On: fixed arrays, written out at each
/// stats tick. Times are Stopwatch ns (CLOCK_MONOTONIC on Linux), so the ends of a netns
/// test on one host share one time base.
namespace Tbt.Contracts

open System
open System.Diagnostics
open System.IO

/// The trace switch alone: a plain static field with no type initializer, so the hot-path
/// test is one load (no class-init check under NativeAOT).
[<AbstractClass; Sealed>]
type TraceGate =
    [<DefaultValue>]
    static val mutable private on: bool
    static member On
        with get () = TraceGate.on
        and set (v: bool) = TraceGate.on <- v

module Trace =

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

    /// path: an RTT sample of a first-copy segment (step = path inflight bytes, aux = RTT us).
    [<Literal>]
    let Rtt = 5

    /// path: bytes the kernel holds readable before a read (step = bytes).
    [<Literal>]
    let Readable = 6

    /// rx: the delivery frontier advanced (spec 6.9.1 `ack_step_lo`). `step` = the new
    /// frontier, `aux` = how many steps it crossed in this call.
    ///
    /// Seal/Send/Arrive/Known already describe the two ends of one interactive step; what
    /// they cannot say is whether the frontier moved past it. A probe that is late because
    /// its own step never decoded looks the same, in those four records, as one whose step
    /// decoded but stayed behind an earlier hole — the first needs parity, the second needs
    /// the frontier to unblock. This record separates them, and it is written once per call
    /// that crosses something rather than once per step, so a jump of hundreds of steps
    /// costs one line.
    [<Literal>]
    let Ack = 7

    /// rx: a flow's CUSTODY frontier advanced — bytes left the decoder window.
    ///
    /// This is `Flow.RxDelivered`, and it is not "the application received them". `deliver`
    /// hands the local socket what it accepts and parks the remainder in the flow's reserve,
    /// returning the FULL length when the reserve had room; `RxDelivered` is then advanced by
    /// that full length, so reserve bytes are counted here too. The module's own credit
    /// arithmetic proves it: `consumed f = f.RxDelivered - uint64 f.Reserve.Count`.
    /// `step` = cumulative custody bytes, `path` = flow slot, `aux` = generation as int32
    /// (wraps above 2^31; generations are small). Use `Write` for the socket-accepted part.
    ///
    /// `Ack` cannot answer even this: `ack_step_lo` is a STEP frontier, while a flow releases
    /// its bytes by STREAM OFFSET, and one step carries bytes belonging to several flows.
    [<Literal>]
    let Deliver = 8

    /// rx: bytes the local PORT took responsibility for (cumulative per generation).
    /// `step` = cumulative bytes returned by `IFlowPort.Write`, `path` = flow slot, `aux` =
    /// generation as int32. Delta between two records of the same (slot, generation) is the
    /// bytes of one call, the interval its stall.
    ///
    /// This is NOT kernel acceptance. `FlowBridge.Write` copies into its 64 KiB per-slot stage
    /// and returns the length; the `socket.Send` happens later in `flushSlot` (or immediately
    /// only when the incoming chunk alone exceeds the stage). So `Stage` means "the bridge owns
    /// these bytes", and `Deliver − Stage` is the reserve backlog in the flow's own ring.
    /// Kernel acceptance is the separate `Sent` record; the far end's `read` is unobservable
    /// here by construction.
    [<Literal>]
    let Stage = 9

    /// tx-local: bytes actually handed to the kernel by `FlowBridge.flushSlot` (cumulative
    /// per flow-slot lifetime — the counter resets when the slot is dropped, so a reused slot
    /// starts from zero). `step` = cumulative bytes `socket.Send` accepted, `path` = flow slot,
    /// `aux` = -1, meaning "generation not tracked at this layer" (the bridge keys staging by
    /// slot only).
    ///
    /// `Sent` is what makes the local pipeline separable: `Stage − Sent` is the bytes sitting
    /// in the bridge's stage because the socket refused them (EAGAIN), while `Sent` itself only
    /// means the kernel took the bytes — not that the peer application read them. Written from
    /// `flushSlot`, which runs on the session thread, the same thread that owns every other
    /// record, so the ring keeps its single-writer invariant.
    [<Literal>]
    let Sent = 10

    /// rx: the STEP that produced the bytes now leaving the decoder window for a flow.
    /// `step` = the step id (uint64, exact — never truncated), `path` = flow slot, `aux` =
    /// flow generation as int32 (generations are small; same convention as `Deliver`).
    ///
    /// This is the missing link between the two other timelines. `Seal`/`Send`/`Arrive`/
    /// `Known` know only the step; `Deliver`/`Stage`/`Sent` know only the flow and its
    /// cumulative bytes. Without this record a probe could be followed by step or by flow, but
    /// not both, so "the probe's bytes waited in step 4211" was not answerable at all.
    /// It is written immediately BEFORE the `Deliver` record for the same bytes, on the same
    /// thread, so a reader pairs them positionally: the `Bind` preceding a `Deliver` delta
    /// names the step whose bytes that delta carried. When one source needs several deltas
    /// (a reserve-limited reader), only the first carries a new `Bind`; the last `Bind` for
    /// the flow stays in force until the next one.
    [<Literal>]
    let Bind = 11

    /// tx: a source was COMMITTED into encoding step `step`. `path` = flow slot, `aux` = flow
    /// generation as int32 (generations are small; same convention as `Deliver`).
    ///
    /// This is the send-side link the trace was missing. `Seal` is emitted only for
    /// urgent/interactive steps, so a bulk step that carried, say, a probe's echo had no
    /// send-side record at all — "which step took the target's bytes, and when" was
    /// unanswerable, and it is the one question left after the receive side was decomposed
    /// (delivery coincided with the step's last arrival to the microsecond, so nothing waits
    /// there). Emitted once per committed source from `Session.CommitSource`, which is the
    /// single funnel for every send path (OPEN, read, close).
    [<Literal>]
    let Fill = 12

    /// tx: the flow's cumulative stream offset AFTER the source named by the preceding `Fill`.
    /// The `step` field carries the BYTE OFFSET (uint64), `path` = flow slot, `aux` = generation.
    ///
    /// Same convention as `Deliver`, whose `step` field also carries cumulative bytes. Pairing
    /// is positional and ordered: the `Fill` immediately before a `Put` names the step, and the
    /// difference between that `Put` and the flow's previous `Put` is exactly the byte range
    /// that step took. One thread, one call, no new field, no second clock.
    [<Literal>]
    let Put = 13

    let private capacity = 1 <<< 22

    /// The one line kind that is not an event: the ring ran out of room and `step` is how many
    /// records were dropped (cumulative for this run). All event kinds are >= 1, so a reader
    /// that dispatches on known kinds ignores this line; a reader that walks every line still
    /// sees five whitespace-separated numbers, so the format does not change shape.
    ///
    /// Why a marker in the file rather than only a stats field: a truncated trace is the one
    /// failure mode that quietly invalidates the conclusion drawn from it — an absent tail looks
    /// exactly like "nothing happened late in the run". Recording the loss turns that silence
    /// into an observation.
    [<Literal>]
    let Truncated = 0

    let private factor = 1_000_000_000.0 / float Stopwatch.Frequency

    let now () = int64 (float (Stopwatch.GetTimestamp()) * factor)

    /// One trace run: its own ring, writer, cursors and drop counters.
    ///
    /// This is a type rather than module-level mutable state for one concrete reason: a test can
    /// own a private instance. The process-wide `TraceGate` and the single production instance
    /// below are then untouched by tests, so a test can never leave the global switch on and
    /// pollute another test's records (which is exactly what made the earlier trace tests pass in
    /// a full solution run and fail when their project ran alone).
    ///
    /// Invariants (what makes the file trustworthy):
    ///   R1  `count` grows only under `ringLock`, and only after the five words of that slot are
    ///       written. Therefore any index `< count` names a complete record.
    ///   R2  Slots are never rewritten: there is no wrap, only append-then-drop. Therefore a
    ///       record's bytes cannot change after `count` has passed it.
    ///   R3  `flush` reads `count` under `ringLock` and then writes `[written, count)` outside it.
    ///       By R1 every one of those slots is complete; by R2 they stay complete; by R3 being
    ///       the only writer of `written`, two flushes cannot overlap ranges (they also hold
    ///       `outputLock`).
    ///   R4  When the ring is full, `dropped` grows under the same lock, so it is an EXACT count
    ///       of refused records — not a lower bound — and `Truncated` reports it.
    type TraceLog(file: string, ringCapacity: int) =
        do
            if ringCapacity <= 0 then
                invalidArg "ringCapacity" "the trace ring needs room for one record"

        let kinds = Array.zeroCreate<byte> ringCapacity
        let steps = Array.zeroCreate<uint64> ringCapacity
        let paths = Array.zeroCreate<uint16> ringCapacity
        let auxes = Array.zeroCreate<int> ringCapacity
        let times = Array.zeroCreate<int64> ringCapacity

        /// Guards the ring only: `count`, `dropped` and the slots below `count`.
        ///
        /// Why a lock instead of a lock-free reservation: one record is five words, and `flush`
        /// must never see a partially written one. Reserving an index with `Interlocked` is not
        /// enough — a writer can be preempted between the reservation and its last store, so a
        /// flush would emit a row with one writer's `kind` and another's `step`; and two writers
        /// that both read the same `count` would overwrite one row into a mixture. A monitor
        /// makes R1-R4 local facts that need no memory-model argument.
        ///
        /// The cost is one uncontended enter/exit per record (~15-20 ns) and it is only paid when
        /// tracing is switched on. That is the deliberate trade: a diagnostic that silently loses
        /// or blends rows destroys the conclusion it is collected for, and `TBT_TRACE` is not a
        /// production mode.
        let ringLock = obj ()

        /// Serialises `flush`/`stop`, so a stats tick and `ProcessExit` cannot write the same
        /// range or close the writer twice.
        let outputLock = obj ()

        let mutable count = 0
        let mutable written = 0
        let mutable dropped = 0L
        let mutable markedDropped = 0L
        let mutable stopped = false
        let mutable writer: StreamWriter | null = new StreamWriter(file, false)

        /// Records refused because the ring was full; 0 until it fills. Exact (R4), read under
        /// the ring lock so a stats line cannot report a value the ring has already passed.
        ///
        /// It does NOT include a record refused because the run was stopped: `Stop` has already
        /// flushed and closed the file by then, so a count taken afterwards could never reach a
        /// reader, and the run those records belonged to is over. They are refused, not counted,
        /// and no later reader should claim otherwise.
        member _.Dropped = lock ringLock (fun () -> dropped)

        member _.Capacity = ringCapacity

        member _.Record(kind: int, step: uint64, path: uint16, aux: int) =
            // Once `Stop` has run, a record is refused and is NOT counted: the file is already
            // flushed and closed, so the count could never be reported. `Stop` means the run is
            // over — whoever keeps recording after it loses those rows, and the loss shows up
            // only as the missing tail it is. Nothing here pretends otherwise.
            lock ringLock (fun () ->
                if not stopped then
                    if count < ringCapacity then
                        kinds.[count] <- byte kind
                        steps.[count] <- step
                        paths.[count] <- path
                        auxes.[count] <- aux
                        times.[count] <- now ()
                        count <- count + 1
                    else dropped <- dropped + 1L)

        /// Write out the entries since the last flush, then one `Truncated` line if anything was
        /// refused since the previous one. Same five whitespace-separated columns as any event,
        /// so no reader has to learn a new shape; kind 0 is not a valid event.
        member _.Flush() =
            lock outputLock (fun () ->
                let n, d = lock ringLock (fun () -> count, dropped)
                match writer with
                | null -> ()
                | w ->
                    for i in written .. n - 1 do
                        w.Write(int kinds.[i]); w.Write ' '; w.Write steps.[i]; w.Write ' '
                        w.Write(int paths.[i]); w.Write ' '; w.Write auxes.[i]; w.Write ' '; w.WriteLine times.[i]
                    written <- n
                    if d > markedDropped then
                        w.Write(Truncated); w.Write ' '; w.Write d; w.Write ' '
                        w.Write 0; w.Write ' '; w.Write 0; w.Write ' '; w.WriteLine(now ())
                        markedDropped <- d
                    w.Flush())

        /// Stop recording, flush what is buffered, and close the writer.
        ///
        /// Order matters: `stopped` is raised FIRST (under the ring lock, so a writer already in
        /// `Record` either finishes before it or is refused after it), then the buffered records
        /// are written, then the handle is released. A record that raced with `Stop` is either in
        /// the file or counted as refused by the "stopped" branch — it is never silently dropped
        /// between the two.
        member this.Stop() =
            lock ringLock (fun () -> stopped <- true)
            this.Flush()
            lock outputLock (fun () ->
                match writer with
                | null -> ()
                | w ->
                    writer <- null
                    w.Dispose())

    /// The one run production uses. Tests construct `TraceLog` directly instead of setting this.
    let mutable private current: TraceLog | null = null

    /// Serialises `start`/`stop`. These are process-lifecycle operations, not throttle points:
    /// production calls `start` once — from `Program.main`, before any thread that records
    /// exists — and never calls `stop`. Re-targeting in one process is supported for tests, and
    /// this lock is what makes it a defined transition instead of two runs racing one path.
    let private lifecycleLock = obj ()

    /// Start tracing into `file` with a ring of `ringCapacity` records.
    ///
    /// Order, and why: the previous run is retired FIRST, then the new one is opened. Opening
    /// first is not safe when `file` is the path the previous run still owns — `StreamWriter`
    /// opens with `FileShare.Read`, so the second writer gets a sharing violation; and even if it
    /// did not, `FileMode.Create` would truncate the file while the old writer still holds its
    /// own offset, so the old tail would land in the new file at the wrong place. Retiring first
    /// makes "start the same path again" mean what it says: one writer, one file, fresh content.
    ///
    /// A failed open (missing directory, permissions) therefore leaves tracing OFF rather than
    /// half-configured: `current` stays null and `TraceGate.On` stays false. That is a deliberate
    /// loss of the old destination in exchange for never writing two runs into one file — a trace
    /// that mixes two runs is worse than a trace that is absent.
    let startWithCapacity (file: string) (ringCapacity: int) =
        lock lifecycleLock (fun () ->
            // Clear the switch first, so call sites that test it stop producing new records
            // before the old run is closed. Sites that call `record` unconditionally still take
            // the `On` check inside it and drop, which is the bounded, documented price of a
            // re-target: a record that races the swap may be lost, and is not counted.
            TraceGate.On <- false
            match current with
            | null -> ()
            | p ->
                current <- null
                p.Stop()
            let fresh = TraceLog(file, ringCapacity)   // may throw; tracing stays off
            current <- fresh
            TraceGate.On <- true)

    /// Start tracing into file (one per process).
    let start (file: string) = startWithCapacity file capacity

    let record (kind: int) (step: uint64) (path: uint16) (aux: int) =
        // Off: exactly one static read, no object touch, no allocation.
        if TraceGate.On then
            match current with
            | null -> ()
            | log -> log.Record(kind, step, path, aux)

    let flush () =
        match current with
        | null -> ()
        | log -> log.Flush()

    /// Records dropped so far in this run; 0 until the ring fills. Same value the `Truncated`
    /// line carries, for a stats line or a test that must not parse the file.
    let droppedCount () =
        match current with
        | null -> 0L
        | log -> log.Dropped

    /// Flush, close, and switch recording off. Safe to call twice, and safe while writers run.
    ///
    /// The switch is cleared BEFORE the run is stopped, so the only writer that can still reach
    /// the log is one that already read `On = true`; `TraceLog.Stop` then decides it — its row is
    /// either in the flushed prefix or refused as post-stop. `current` is deliberately left in
    /// place: a late `record` that read the switch early finds a log that refuses it, instead of
    /// a null whose meaning it would have to guess.
    let stop () =
        lock lifecycleLock (fun () ->
            TraceGate.On <- false
            match current with
            | null -> ()
            | log -> log.Stop())
