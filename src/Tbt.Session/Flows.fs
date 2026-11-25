/// Logical flows of one session (spec 5.3, 6.9.1, 6.10b.3 rule 6). Up to 256 flow slots;
/// a slot carries one generation at a time. The flow initiator (client) picks slot and gen.
namespace Tbt.Session

open System
open System.Buffers
open System.Collections.Generic
open Tbt.Wire

/// The local end of the flows: the host owns the sockets. Every call is non-blocking and
/// comes from the session thread.
type IFlowPort =
    /// The peer opened flow (slot, gen). Server: connect to the target. Client: no action.
    abstract Open: slot: int * gen: uint32 -> unit
    /// Write delivered bytes to the local connection. Return the count accepted (0 when it
    /// would block), or -1 when the local connection failed.
    abstract Write: slot: int * data: ReadOnlySpan<byte> -> int
    /// The peer ended its byte stream: shut down the local send side.
    abstract Finish: slot: int -> unit
    /// The peer reset the flow: abort the local connection.
    abstract Reset: slot: int -> unit
    /// Both directions of the flow are done: close the local connection and free the slot.
    abstract Close: slot: int -> unit
    /// The peer's byte limit for this end's tx on slot grew (spec 16.3.3 RS-2: the event
    /// that brings a credit-blocked flow back to reading, instead of polling it).
    abstract Credit: slot: int -> unit

/// One received source that waits for delivery. Its bytes stay in the decoder window.
[<Struct>]
type PendingSource =
    { Step: uint64
      Slot: byte
      Offset: uint64
      Length: int
      Flags: StreamFlags }

/// Delivery order inside a slot: generation, stream offset, then the OPEN source first.
/// The zero-length OPEN and the first data source share offset 0; when the data source is
/// decoded first (later steps can overtake), it must still sort after the OPEN.
[<Struct; CustomComparison; CustomEquality>]
type FlowKey =
    { Gen: uint32
      Offset: uint64
      /// 0 for an OPEN source, 1 otherwise.
      Rank: byte }

    override k.Equals(o: obj) =
        match o with
        | :? FlowKey as q -> k.Gen = q.Gen && k.Offset = q.Offset && k.Rank = q.Rank
        | _ -> false

    override k.GetHashCode() = HashCode.Combine(k.Gen, k.Offset, k.Rank)

    interface IComparable<FlowKey> with
        member k.CompareTo(q) =
            let c = k.Gen.CompareTo q.Gen
            if c <> 0 then c
            else
                let c = k.Offset.CompareTo q.Offset
                if c <> 0 then c else k.Rank.CompareTo q.Rank

    interface IComparable with
        member k.CompareTo(o) =
            match o with
            | :? FlowKey as q -> (k :> IComparable<FlowKey>).CompareTo q
            | _ -> invalidArg "o" "not a FlowKey"

/// Read access to source symbols that wait in the decoder window.
type ISourceStore =
    abstract Source: step: uint64 * slot: int -> ReadOnlySpan<byte>

/// Delivery reserve of one flow (spec 6.10b.2 delivered_offset "may hold reserve bytes"):
/// a byte ring that takes delivered bytes the local connection cannot take now, so their
/// decoder step can go. It grows on demand up to its cap and gives its memory back when empty.
///
/// The ring is a sequence of fixed chunks rented from the shared ArrayPool, never one
/// doubling array: a reserve may reach 8 MiB, and a single array of that size is a Large
/// Object Heap allocation whose copies run on the session thread that also decodes. A chunk
/// is 64 KiB, below the 85 000-byte LOH threshold, and goes back to the pool as soon as it
/// drains, so an idle or closed flow holds no buffer at all.
[<Sealed>]
type ByteQueue(cap: int) =
    static let chunkSize = 65536
    /// Chunk slots in logical order starting at first; ring.Length is a power of two.
    let mutable ring: (byte[] | null)[] = Array.Empty<byte[] | null>()
    let mutable first = 0
    let mutable chunks = 0
    /// Offset of the first byte inside the oldest chunk.
    let mutable head = 0
    let mutable count = 0

    let slot (i: int) = (first + i) % ring.Length

    let giveBack (from: int) =
        for i in from .. chunks - 1 do
            let s = slot i
            match ring.[s] with
            | null -> ()
            | c ->
                ArrayPool<byte>.Shared.Return c
                ring.[s] <- null
        chunks <- min chunks from

    /// Make room for one more chunk, preserving logical order.
    let grow () =
        if chunks = ring.Length then
            let next = Array.zeroCreate<byte[] | null> (max 4 (ring.Length * 2))
            for i in 0 .. chunks - 1 do next.[i] <- ring.[slot i]
            // The old array's chunks are now referenced by next; empty the old slots so a
            // later Clear cannot return the same chunk twice.
            for i in 0 .. chunks - 1 do ring.[slot i] <- null
            ring <- next
            first <- 0

    /// Bytes held by the first chunk (never more than its tail after `head`).
    let bytesInFirst () = if chunks = 0 then 0 else min count (chunkSize - head)
    /// Write offset inside the last chunk: after the first chunk's tail, then full chunks.
    let writeOffsetInLast () =
        if chunks = 0 then 0
        elif chunks = 1 then head + count
        else count - bytesInFirst () - (chunks - 2) * chunkSize

    member _.Count = count
    /// Bytes requested from the shared pool. ArrayPool may hand back a larger slab;
    /// this is the reserved chunk size, not a guess of the process RSS.
    member _.Rented = chunks * chunkSize
    member _.Cap = cap

    member _.Free = cap - count

    /// The oldest contiguous bytes: at most to the end of the first chunk.
    member _.Peek() : ReadOnlySpan<byte> =
        if count = 0 then ReadOnlySpan.Empty
        else
            match ring.[first] with
            | null -> ReadOnlySpan.Empty
            | c -> ReadOnlySpan(c, head, min count (chunkSize - head))

    member _.Skip(n: int) =
        let mutable left = min n count
        while left > 0 && chunks > 0 do
            let room = chunkSize - head
            if left >= room then
                left <- left - room
                let s = slot 0
                match ring.[s] with
                | null -> ()
                | c ->
                    ring.[s] <- null
                    ArrayPool<byte>.Shared.Return c
                first <- (first + 1) % ring.Length
                chunks <- chunks - 1
                head <- 0
            else
                head <- head + left
                left <- 0
        count <- max 0 (count - n)
        if count = 0 then
            // Nothing is left to mirror an offset: hand every chunk back so an idle slot holds
            // no memory. Resetting `first` without this left the last chunk rented and, worse,
            // pointed `first` at a slot a later Write would find null.
            giveBack 0
            head <- 0

    /// Append all of data. The caller checks Free first.
    member _.Write(data: ReadOnlySpan<byte>) =
        let mutable rest = data
        while rest.Length > 0 do
            // Full chunks after the first one. A negative value means the first chunk still
            // has room, so the last chunk keeps taking bytes.
            let queuedAfterFirst = if chunks = 0 then 0 else count - (chunkSize - head)
            if chunks = 0 || queuedAfterFirst >= (chunks - 1) * chunkSize then
                // The last chunk is full: take another one.
                grow ()
                let s = slot chunks
                match ring.[s] with
                | null -> ring.[s] <- ArrayPool<byte>.Shared.Rent chunkSize
                | _ -> ()
                chunks <- chunks + 1
            let last = chunks - 1
            let tail = writeOffsetInLast ()
            let room = chunkSize - tail
            let take = min room rest.Length
            let dst =
                match ring.[slot last] with
                | null ->
                    let fresh = ArrayPool<byte>.Shared.Rent chunkSize
                    ring.[slot last] <- fresh
                    fresh
                | c -> c
            rest.Slice(0, take).CopyTo(Span(dst, tail, take))
            rest <- rest.Slice take
            count <- count + take

    /// Drop every byte and give all rented chunks back to the pool.
    member _.Clear() =
        giveBack 0
        first <- 0
        head <- 0
        count <- 0

/// Per-flow byte credit (FLOW_STATE format 2, spec 6.10b.4).
[<Struct>]
type CreditPolicy =
    { /// Both ends negotiated caps bit8 + bit9: tx obeys peer limits, rx reports its own.
      Enabled: bool
      /// Initial credit per slot and direction (C0).
      Initial: int
      /// Largest window one flow gets.
      FlowWindowMax: int
      /// Receive budget of the session: every promise not yet consumed by the local reader
      /// (bytes held in reserves plus credit not yet arrived) counts. C0 of all 256 slots is
      /// inside it (the peer may open any slot with C0 at any time); the rest is shared.
      PoolBytes: int }

module CreditPolicy =
    let C0 = 65536
    /// Default single-flow receive window. A 45-50 MB/s flow needs about 20 MB in flight
    /// at 0.4 s BDP; 24 MiB leaves headroom without allocating it up front.
    let DefaultFlowWindowMax = 24 <<< 20
    /// 256 slots still reserve 16 MiB of C0. The remaining 48 MiB is shared, enough for
    /// two large flows or many small ones; the pool only charges promises, not RSS.
    let DefaultPoolBytes = 64 <<< 20
    let defaults =
        { Enabled = true
          Initial = C0
          FlowWindowMax = DefaultFlowWindowMax
          PoolBytes = DefaultPoolBytes }

/// State of one slot. Tx: this end's bytes to the peer. Rx: the peer's bytes to this end.
[<Sealed>]
type Flow(index: int) =
    member _.Index = index
    /// Current generation; 0 = slot unused.
    member val Gen = 0u with get, set
    /// Highest generation seen or created on this slot.
    member val MaxGen = 0u with get, set
    member val TxOffset = 0UL with get, set
    member val TxOpenSent = false with get, set
    /// A FIN or RST source is committed; TxEndStep is its step.
    member val TxEndSent = false with get, set
    member val TxEndStep = 0UL with get, set
    /// The tx side needs no more peer action (end acknowledged, or the peer reset the flow).
    member val TxDone = false with get, set
    /// The local connection failed: send RST at the next commit.
    member val TxResetPending = false with get, set
    member val RxOpened = false with get, set
    member val RxDelivered = 0UL with get, set
    /// Bytes the local PORT took responsibility for in this generation. Always ≤ RxDelivered:
    /// `deliver` adds the whole length to RxDelivered when the remainder fits the reserve,
    /// while only the accepted part reaches here, so `RxDelivered - RxStaged` is the reserve
    /// backlog — bytes still in the flow's own ring, which is what a slow local reader makes a
    /// probe wait behind.
    ///
    /// "Accepted" stops at the port boundary: `FlowBridge.Write` copies into its 64 KiB stage
    /// and returns the length, so these bytes are staged, not yet in the kernel. Kernel
    /// acceptance is traced separately from `flushSlot` (`Trace.Sent`); the peer application's
    /// `read` is unobservable from this process.
    member val RxStaged = 0UL with get, set
    member val RxDone = false with get, set
    /// The peer's FIN is taken but the reserve still holds bytes: finish when it drains.
    member val RxFinPending = false with get, set
    member val Pending = PriorityQueue<PendingSource, FlowKey>()
    /// Peer's absolute byte limit for this end's tx (C0 until the first report).
    member val TxLimit = 0UL with get, set
    member val TxCreditEpoch = 0u with get, set
    member val TxCreditSeq = 0u with get, set
    /// The peer's `DeliveredOffset` from the last credit report this end ACCEPTED.
    ///
    /// It is the peer's rx CUSTODY frontier (`RxDelivered` on the far side), not its
    /// application read position: on the receiver those differ by the reserve ring. It is also
    /// a REPORTED number, so it lags the peer by one FLOW_STATE report and cannot be turned
    /// into an in-flight byte count by subtracting a local window — `TxLimit` is a monotone
    /// high-water mark against an absolute peer limit, and the local `FlowWindowMax` never
    /// crosses the wire. Observation only: nothing in the credit path reads it.
    member val TxDeliveredReported = 0UL with get, set
    /// Local monotonic ns at which that report was accepted; 0 = no report seen yet.
    /// Freshness is a LOCAL age, so it needs no clock agreement with the peer.
    member val TxReportNs = 0L with get, set
    /// Reports accepted (advanced epoch:seq), rejected as stale (≤ last epoch:seq, i.e. a
    /// duplicate or out-of-order page), and rejected as foreign (slot inactive or unused,
    /// generation mismatch, credit disabled). Counters only; the accept rule below is unchanged.
    member val TxReportsAccepted = 0L with get, set
    member val TxReportsStale = 0L with get, set
    member val TxReportsForeign = 0L with get, set
    /// Accepted reports whose DeliveredOffset went backwards. A peer's custody frontier is
    /// monotone, so this counts a peer invariant violation; the stored offset keeps the higher
    /// value instead of following the bad report.
    member val TxDeliveredRegressions = 0L with get, set
    /// Absolute byte limit this end promised the peer for its rx.
    member val RxLimit = 0UL with get, set
    /// Reports that still carry this flow (a change goes out in more than one report).
    member val ReportsDue = 0 with get, set
    /// Shared-pool bytes this flow holds beyond its C0 (spec 6.10b.4 budget).
    member val Charged = 0L with get, set
    member val Reserve: ByteQueue = ByteQueue(0) with get, set

    member f.Active = f.Gen <> 0u

    /// Prepare the slot for generation gen.
    member f.Start(gen: uint32, credit: CreditPolicy) =
        f.Gen <- gen
        if gen > f.MaxGen then f.MaxGen <- gen
        f.TxOffset <- 0UL
        f.TxOpenSent <- false
        f.TxEndSent <- false
        f.TxEndStep <- 0UL
        f.TxDone <- false
        f.TxResetPending <- false
        f.RxOpened <- false
        f.RxDelivered <- 0UL
        f.RxStaged <- 0UL
        f.RxDone <- false
        f.RxFinPending <- false
        f.TxLimit <- if credit.Enabled then uint64 credit.Initial else UInt64.MaxValue
        f.TxCreditEpoch <- 0u
        f.TxCreditSeq <- 0u
        // A new generation has its own report history; carrying the old numbers over would read
        // as a fresh report for bytes this generation never sent.
        f.TxDeliveredReported <- 0UL
        f.TxReportNs <- 0L
        f.TxReportsAccepted <- 0L
        f.TxReportsStale <- 0L
        f.TxReportsForeign <- 0L
        f.TxDeliveredRegressions <- 0L
        f.RxLimit <- uint64 credit.Initial
        f.ReportsDue <- 0
        if f.Reserve.Cap <> credit.FlowWindowMax then
            // Clear before replacing: the old ring must give its chunks back to the pool.
            f.Reserve.Clear()
            f.Reserve <- ByteQueue(credit.FlowWindowMax)
        else f.Reserve.Clear()

/// Counters for statistics.
[<Sealed>]
type FlowCounters() =
    member val StaleSources = 0L with get, set
    member val Opened = 0L with get, set
    member val Closed = 0L with get, set
    member val LocalFailures = 0L with get, set

/// The flow slots of one session. stepUndelivered(step, delta) counts sources per step
/// that wait for delivery; the decoder keeps a step while its count is not zero.
[<Sealed>]
type FlowTable(port: IFlowPort, store: ISourceStore, stepUndelivered: Action<uint64, int>, credit: CreditPolicy, ?newWindowCap: unit -> int) =
    static let slotCount = 256
    let mutable windowCap = defaultArg newWindowCap (fun () -> Int32.MaxValue)
    let flows = Array.init slotCount Flow
    let closing = List<int>()
    /// Client side: next slot to try, so that a freed slot rests as long as possible.
    let mutable nextSlot = 0
    let counters = FlowCounters()
    /// Flows with an open rx side: they share the credit pool.
    let mutable rxActive = 0
    let mutable reportsDue = 0
    // Pool (spec 6.10b.4, DECISIONS D8): C0 of every slot is reserved up front; the shared
    // rest is charged per flow as RxLimit - consumed - C0, and released only by consumption
    // or close (a promise is never taken back).
    let c0 = int64 credit.Initial
    let mutable budgetCap = int64 credit.PoolBytes
    let mutable sharedPool = max 0L (budgetCap - int64 slotCount * c0)
    let mutable poolUsed = 0L
    let consumed (f: Flow) = f.RxDelivered - uint64 f.Reserve.Count
    /// Bytes f may still make this end hold: after the peer's FIN or RST nothing more comes,
    /// so only the reserve; before it, the promise not yet consumed.
    let commitment (f: Flow) =
        if f.RxDone then int64 f.Reserve.Count else int64 (f.RxLimit - min f.RxLimit (consumed f))
    let recharge (f: Flow) =
        let charge = max 0L (commitment f - c0)
        poolUsed <- poolUsed + charge - f.Charged
        f.Charged <- charge
    let release (f: Flow) =
        poolUsed <- poolUsed - f.Charged
        f.Charged <- 0L

    let dropHead (f: Flow) =
        let mutable src = Unchecked.defaultof<PendingSource>
        let mutable key = Unchecked.defaultof<FlowKey>
        f.Pending.TryDequeue(&src, &key) |> ignore
        stepUndelivered.Invoke(src.Step, -1)

    /// Drop the pending sources of generation gen and lower.
    let dropGen (f: Flow) (gen: uint32) =
        let mutable src = Unchecked.defaultof<PendingSource>
        let mutable key = Unchecked.defaultof<FlowKey>
        while f.Pending.TryPeek(&src, &key) && key.Gen <= gen do
            dropHead f

    let tryClose (f: Flow) =
        if f.Active && f.RxDone && f.TxDone && not f.RxFinPending then
            dropGen f f.Gen
            f.Reserve.Clear()
            if f.RxOpened then rxActive <- rxActive - 1
            release f
            f.Gen <- 0u
            // reportsDue counts marked flows, not remaining repeat transmissions.
            // Retire this generation's mark before reuse or the Close callback.
            if f.ReportsDue > 0 then
                f.ReportsDue <- 0
                reportsDue <- reportsDue - 1
            counters.Closed <- counters.Closed + 1L
            port.Close f.Index
            true
        else false

    let localFailure (f: Flow) =
        counters.LocalFailures <- counters.LocalFailures + 1L
        dropGen f f.Gen
        f.Reserve.Clear()
        f.RxFinPending <- false
        f.RxDone <- true
        if not f.TxEndSent then f.TxResetPending <- true

    /// Grow the rx limit when the local connection took bytes (spec 6.10b.4: a limit only
    /// grows). The window per flow is C0 plus the shared pool split over the open flows, at
    /// most FlowWindowMax, and never more than the pool still has free.
    let refreshCredit (f: Flow) =
        if credit.Enabled && f.Active then recharge f
        if credit.Enabled && f.Active && f.RxOpened && not f.RxDone then
            // A pressure cap applies to NEW promises only. RxLimit never shrinks here, so
            // bytes already promised and in flight keep their right to arrive. The floor is
            // C0 because Start already promised it; retracting it would break custody.
            let cap = max c0 (int64 (windowCap ()))
            let window =
                int (max (int64 credit.Initial)
                         (min (min (int64 credit.FlowWindowMax) cap)
                              (c0 + sharedPool / int64 (max 1 rxActive))))
            let taken = consumed f
            let free = sharedPool - poolUsed
            let limit = min (taken + uint64 window) (taken + uint64 (c0 + f.Charged + free))
            // Report when an eighth of the window (capped at 256 KiB) is free again, floored
            // at C0/2. A 24 MiB window would be 3 MiB on the uncapped rule, too silent for
            // 400 Mbit/s; 256 KiB is about 5 ms at that rate. A report also goes out when
            // the flow's own room falls below half the window, so a fast drain cannot hit 0.
            let step = max (c0 / 2L) (int64 (min (window / 8) 262144))
            let room = f.RxLimit - min f.RxLimit taken
            if limit >= f.RxLimit + uint64 step || (limit > f.RxLimit && room < uint64 (window / 2)) then
                f.RxLimit <- limit
                recharge f
                if f.ReportsDue = 0 then reportsDue <- reportsDue + 1
                f.ReportsDue <- 2

    let openRx (f: Flow) =
        f.RxOpened <- true
        rxActive <- rxActive + 1
        // Announce the fair share now. Without this the peer starts at C0 (64 KiB) and the
        // first round trip after the handshake runs on a window far below the BDP of a long
        // path, which costs one RTT of slow start on every flow.
        refreshCredit f
        port.Open(f.Index, f.Gen)

    /// Bytes the local connection of f takes from data; the rest goes to the reserve when
    /// it fits. Returns the bytes taken out of the decoder window, or -1 on failure.
    ///
    /// The return value is CUSTODY, not socket acceptance: when the reserve has room for the
    /// remainder this returns `data.Length`, so `RxDelivered` counts bytes the local socket
    /// never took. `RxStaged` is the part the port took responsibility for — which for
    /// `FlowBridge` means copied into its stage buffer, not handed to the kernel; `flushSlot`
    /// reports kernel acceptance as the separate `Sent` record.
    let deliver (f: Flow) (data: ReadOnlySpan<byte>) =
        let written = if f.Reserve.Count > 0 then 0 else port.Write(f.Index, data)
        if written < 0 then -1
        else
            if written > 0 then
                f.RxStaged <- f.RxStaged + uint64 written
                if Tbt.Contracts.TraceGate.On then
                    Tbt.Contracts.Trace.record
                        Tbt.Contracts.Trace.Stage
                        f.RxStaged
                        (uint16 f.Index)
                        (int f.Gen)
            if written = data.Length then written
            else
                let rest = data.Slice written
                if rest.Length <= f.Reserve.Free then
                    f.Reserve.Write rest
                    data.Length
                else written

    /// Give the reserve to the local connection. False when it still holds bytes.
    let drainReserve (f: Flow) =
        let mutable go = true
        while go && f.Reserve.Count > 0 do
            let seg = f.Reserve.Peek()
            let n = port.Write(f.Index, seg)
            if n < 0 then
                localFailure f
                go <- false
            else
                f.Reserve.Skip n
                // Draining the reserve is the same kind of event as `deliver`'s accepted part:
                // the port took the bytes. For `FlowBridge` that still means "copied into the
                // stage", so it belongs on the `Stage` timeline — counting it as kernel
                // acceptance would overstate `Sent − Stage` by however much drained here.
                if n > 0 then
                    f.RxStaged <- f.RxStaged + uint64 n
                    if Tbt.Contracts.TraceGate.On then
                        Tbt.Contracts.Trace.record
                            Tbt.Contracts.Trace.Stage
                            f.RxStaged
                            (uint16 f.Index)
                            (int f.Gen)
                if n < seg.Length then go <- false
        if f.Reserve.Count = 0 && f.RxFinPending then
            f.RxFinPending <- false
            port.Finish f.Index
        f.Reserve.Count = 0

    member _.Counters = counters
    member _.Credit = credit
    /// Governor hook for ShrinkNewWindows. The cap is clamped to [C0, FlowWindowMax];
    /// lowering it stops future RxLimit growth but never revokes an issued promise.
    /// Passing Int32.MaxValue restores the configured ceiling.
    member this.SetNewWindowCap(cap: int) =
        let lower = max CreditPolicy.C0 cap
        windowCap <- fun () -> min credit.FlowWindowMax lower
    /// Flows whose rx limit changed and waits for a FLOW_STATE report.
    member _.ReportsDue = reportsDue
    member _.RxActive = rxActive
    /// Shared-pool bytes charged now (at most the current budget cap minus 256 x C0).
    /// This is a credit promise, not physical memory. Memory accounting uses ReserveBytes.
    member _.PoolUsed = poolUsed
    /// Delivery-reserve chunks currently rented. Zero while a flow has not had to queue
    /// bytes the local peer refused. Not the credit commitment.
    member _.ReserveBytes =
        let mutable n = 0L
        for f in flows do
            if f.Active then n <- n + int64 f.Reserve.Rented
        n
    /// Session rx budget cap in bytes (CTRL CAPACITY_CHANGE). Starts at PoolBytes.
    member _.BudgetCap = budgetCap
    /// Tighten or raise the rx budget cap. Already issued RxLimit values are kept.
    /// False when the cap cannot cover CommittedBytes (spec 6.10a rule 7).
    member this.TrySetBudgetCap(bytes: int64) =
        if bytes < this.CommittedBytes then false
        else
            budgetCap <- bytes
            sharedPool <- max 0L (budgetCap - int64 slotCount * c0)
            true
    /// Every byte this end may still have to hold: per active slot its commitment (at least
    /// C0), C0 per free slot. Never above PoolBytes when credit is on.
    member _.CommittedBytes =
        let mutable total = 0L
        for f in flows do
            total <- total + (if f.Active then max c0 (commitment f) else c0)
        total

    member this.Committed = this.CommittedBytes

    /// Bytes slot may still commit under the peer's limit.
    member _.TxRoom(slot: int) =
        let f = flows.[slot]
        if f.TxLimit = UInt64.MaxValue then Int32.MaxValue
        elif f.TxLimit <= f.TxOffset then 0
        else int (min (f.TxLimit - f.TxOffset) (uint64 Int32.MaxValue))

    /// Apply one FLOW_STATE page (spec 6.10b.2). Every FLOW record is checked against this end's
    /// slot space BEFORE any of them moves state, so a record outside it rejects the page whole
    /// ("拒收整份报告，禁止半份更新") instead of leaving half an update behind. The wire reader
    /// already enforces `flow_slot <= 255`; this is the application boundary doing the same
    /// check against its own `slotCount`, so the atomicity rule survives any future relaxation
    /// of the wire guard. Returns false when the page was refused and nothing was applied.
    member this.OnPeerPage(epoch: uint32, seq: uint32, page: ReadOnlySpan<byte>, recordCount: int, nowNs: int64) =
        let mutable ok = true
        let mutable i = 0
        while ok && i < recordCount do
            if FlowState.kindAt page i = byte FlowStateKind.Flow then
                let slot = (FlowState.readFlow page i).FlowSlot
                if slot < 0 || slot >= slotCount then ok <- false
            i <- i + 1
        if ok then
            for i in 0 .. recordCount - 1 do
                if FlowState.kindAt page i = byte FlowStateKind.Flow then
                    this.OnPeerCredit(epoch, seq, FlowState.readFlow page i, nowNs)
        ok

    /// A peer's slot space is this end's slot space; exposed so a caller can pre-check bounds.
    member _.SlotCount = slotCount

    /// A FLOW record from the peer about the bytes this end sends (spec 6.10b.2/3): a newer
    /// version per (slot, gen) may only grow the limit.
    member _.OnPeerCredit(epoch: uint32, seq: uint32, r: FlowRecord, ?nowNs: int64) =
        // Defense in depth: an out-of-range slot is a foreign report, never an index fault.
        if r.FlowSlot < 0 || r.FlowSlot >= slotCount then
            raise (System.ArgumentOutOfRangeException("r", "FlowSlot outside the slot space"))
        let f = flows.[r.FlowSlot]
        if credit.Enabled && f.Active && f.Gen = r.FlowGeneration then
            if epoch > f.TxCreditEpoch || (epoch = f.TxCreditEpoch && seq > f.TxCreditSeq) then
                f.TxCreditEpoch <- epoch
                f.TxCreditSeq <- seq
                // Observation only (a counter, a stored offset, a timestamp). The accept rule and
                // the limit arithmetic below are unchanged: this record must not move credit.
                f.TxReportsAccepted <- f.TxReportsAccepted + 1L
                f.TxReportNs <- defaultArg nowNs 0L
                if r.DeliveredOffset < f.TxDeliveredReported then
                    // A peer's custody frontier is monotone. Count the violation, keep the higher
                    // value, and still apply the limit below so a bad report cannot shrink credit.
                    f.TxDeliveredRegressions <- f.TxDeliveredRegressions + 1L
                else f.TxDeliveredReported <- r.DeliveredOffset
                let limit = r.DeliveredOffset + uint64 r.CreditBytes
                if limit > f.TxLimit then
                    f.TxLimit <- limit
                    port.Credit r.FlowSlot
            else
                // Duplicate or out-of-order page: benign repeats are normal (an unchanged flow
                // reappears in later pages), so this is a counter, not an error path.
                f.TxReportsStale <- f.TxReportsStale + 1L
        else f.TxReportsForeign <- f.TxReportsForeign + 1L

    /// The FLOW record this end reports for slot (its rx side).
    member _.Record(slot: int) =
        let f = flows.[slot]
        let state =
            if f.RxDone && f.TxEndSent then FlowLifecycle.Closed
            elif f.RxDone then FlowLifecycle.HalfClosedRemote
            elif f.TxEndSent then FlowLifecycle.HalfClosedLocal
            else FlowLifecycle.Open
        { State = state; FlowSlot = slot; FlowGeneration = f.Gen; DeliveredOffset = f.RxDelivered
          CreditBytes = uint32 (min (f.RxLimit - min f.RxLimit f.RxDelivered) (uint64 UInt32.MaxValue)) }

    /// True when slot is an open rx flow that a snapshot lists.
    member _.Reportable(slot: int) = flows.[slot].Active && flows.[slot].RxOpened

    /// Take the report mark of slot. True when it was set.
    member _.TakeReport(slot: int) =
        let f = flows.[slot]
        if f.ReportsDue > 0 && f.Active && f.RxOpened then
            f.ReportsDue <- f.ReportsDue - 1
            if f.ReportsDue = 0 then reportsDue <- reportsDue - 1
            true
        else
            if f.ReportsDue > 0 then
                f.ReportsDue <- 0
                reportsDue <- reportsDue - 1
            false
    member _.Item(slot: int) = flows.[slot]

    /// Client: allocate a slot and a new generation for a local connection.
    member _.TryAllocate(slot: byref<int>) =
        let mutable found = false
        let mutable i = 0
        while not found && i < slotCount do
            let s = (nextSlot + i) % slotCount
            let f = flows.[s]
            if not f.Active && f.Pending.Count = 0 && f.MaxGen < UInt32.MaxValue then
                f.Start(f.MaxGen + 1u, credit)
                counters.Opened <- counters.Opened + 1L
                slot <- s
                nextSlot <- (s + 1) % slotCount
                found <- true
            i <- i + 1
        found

    /// Envelope for the next tx source of slot: len user bytes; ending = FIN or RST.
    member _.NextEnvelope(slot: int, len: int, fin: bool) =
        let f = flows.[slot]
        if not f.Active || f.TxEndSent then invalidOp "flow is not open for sending"
        let rst = f.TxResetPending
        let flags =
            (if f.TxOpenSent then StreamFlags.None else StreamFlags.Open)
            ||| (if rst then StreamFlags.Rst elif fin then StreamFlags.Fin else StreamFlags.None)
        let e =
            { RealLength = (if rst then 0 else len); FlowSlot = byte slot; Flags = flags
              FlowGeneration = f.Gen; StreamOffset = f.TxOffset }
        f.TxOpenSent <- true
        f.TxOffset <- f.TxOffset + uint64 e.RealLength
        if rst || fin then f.TxEndSent <- true
        e

    /// The FIN or RST source of slot went into step t.
    member _.NoteEndStep(slot: int, t: uint64) =
        flows.[slot].TxEndStep <- t
        closing.Add slot

    /// The local connection of slot failed (or its target connect failed).
    member _.Fail(slot: int) =
        let f = flows.[slot]
        if f.Active then localFailure f

    /// Slots with an RST to send.
    member _.ResetPending(slot: int) = flows.[slot].Active && flows.[slot].TxResetPending && not flows.[slot].TxEndSent

    /// The peer has custody of every step below ack.
    member this.OnPeerAck(ack: uint64) =
        let mutable i = closing.Count - 1
        while i >= 0 do
            let f = flows.[closing.[i]]
            if not f.Active || not f.TxEndSent then closing.RemoveAt i
            elif ack > f.TxEndStep then
                f.TxDone <- true
                closing.RemoveAt i
                // The peer may already have opened the next generation on this slot.
                if tryClose f && f.Pending.Count > 0 then this.Pump f.Index
            i <- i - 1

    /// A received source with a valid envelope. Data waits in the decoder window until the
    /// local connection takes it. False when it is stale (old generation): no wait.
    member _.OnSource(step: uint64, slot: int, e: SourceEnvelope) =
        let f = flows.[int e.FlowSlot]
        let g = e.FlowGeneration
        if g = 0u || g < f.MaxGen || (g = f.MaxGen && (not f.Active || f.RxDone)) then
            counters.StaleSources <- counters.StaleSources + 1L
            false
        else
            f.Pending.Enqueue({ Step = step; Slot = byte slot; Offset = e.StreamOffset; Length = e.RealLength; Flags = e.Flags },
                              { Gen = e.FlowGeneration; Offset = e.StreamOffset
                                Rank = (if e.Flags.HasFlag StreamFlags.Open then 0uy else 1uy) })
            stepUndelivered.Invoke(step, 1)
            true

    /// Deliver what the local connection of slot can take now. Bytes it cannot take move to
    /// the flow's reserve, so one slow reader holds no decoder step (spec 6.10b).
    member this.Pump(slot: int) =
        let f = flows.[slot]
        let mutable src = Unchecked.defaultof<PendingSource>
        let mutable key = Unchecked.defaultof<FlowKey>
        let mutable go = true
        if f.Active && f.Reserve.Count > 0 then drainReserve f |> ignore
        while go && f.Pending.TryPeek(&src, &key) do
            let isOpen = src.Flags.HasFlag StreamFlags.Open && src.Offset = 0UL
            if key.Gen < f.MaxGen || (key.Gen = f.MaxGen && (not f.Active || f.RxDone)) then
                counters.StaleSources <- counters.StaleSources + 1L
                dropHead f
            elif key.Gen > f.MaxGen then
                // A new generation starts only on a free slot, with its OPEN source.
                if f.Active || not isOpen then go <- false
                else
                    f.Start(key.Gen, credit)
                    counters.Opened <- counters.Opened + 1L
                    openRx f
            elif not f.RxOpened then
                if isOpen then openRx f
                else go <- false
            elif src.Offset > f.RxDelivered then go <- false
            else
                let finish = src.Offset + uint64 src.Length
                let remaining = if finish > f.RxDelivered then int (finish - f.RxDelivered) else 0
                let mutable taken = remaining
                if remaining > 0 then
                    let skip = src.Length - remaining
                    let data = store.Source(src.Step, int src.Slot).Slice(Tbt.Contracts.Layout.EnvelopeLength + skip, remaining)
                    taken <- deliver f data
                if taken < 0 then
                    localFailure f
                    go <- false
                else
                    f.RxDelivered <- f.RxDelivered + uint64 taken
                    // Opt-in custody timeline (TraceGate off costs one load). `RxDelivered` is
                    // CUSTODY, not socket acceptance: `deliver` returns the full length when the
                    // remainder fits the reserve, so bytes the local socket never took are
                    // counted here (the module's own `consumed` subtracts `Reserve.Count` for
                    // exactly that reason). The port-accepted part is the separate `Stage`
                    // record and kernel acceptance is `Sent`; none of them is the peer
                    // application's read position.
                    if taken > 0 && Tbt.Contracts.TraceGate.On then
                        // `Bind` first, then `Deliver`: the reader pairs them positionally, so a
                        // `Deliver` delta is attributed to the step named by the `Bind` that
                        // immediately precedes it, without needing another field or another
                        // clock. `src.Step` is the step these bytes came out of, and the byte
                        // range is exactly the delta this `Deliver` records.
                        Tbt.Contracts.Trace.record
                            Tbt.Contracts.Trace.Bind
                            src.Step
                            (uint16 f.Index)
                            (int f.Gen)
                        Tbt.Contracts.Trace.record
                            Tbt.Contracts.Trace.Deliver
                            f.RxDelivered
                            (uint16 f.Index)
                            (int f.Gen)
                    if taken < remaining then go <- false
                    else
                        dropHead f
                        if src.Flags.HasFlag StreamFlags.Rst then
                            f.RxDone <- true
                            f.TxDone <- true
                            f.Reserve.Clear()
                            f.RxFinPending <- false
                            port.Reset slot
                        elif src.Flags.HasFlag StreamFlags.Fin then
                            f.RxDone <- true
                            if f.Reserve.Count = 0 then port.Finish slot
                            else f.RxFinPending <- true
                        if f.RxDone then dropGen f f.Gen
        refreshCredit f
        // A closed slot may already hold the next generation.
        if tryClose f && f.Pending.Count > 0 then this.Pump slot
