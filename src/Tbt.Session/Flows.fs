/// Logical flows of one session (spec 5.3, 6.9.1, 6.10b.3 rule 6). Up to 256 flow slots;
/// a slot carries one generation at a time. The flow initiator (client) picks slot and gen.
namespace Tbt.Session

open System
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
[<Sealed>]
type ByteQueue(cap: int) =
    let mutable buf: byte[] = Array.Empty<byte>()
    let mutable head = 0
    let mutable count = 0

    member _.Count = count
    member _.Cap = cap
    member _.Free = cap - count

    /// The oldest contiguous bytes.
    member _.Peek() : ReadOnlySpan<byte> =
        if count = 0 then ReadOnlySpan.Empty
        else ReadOnlySpan(buf, head, min count (buf.Length - head))

    member _.Skip(n: int) =
        count <- count - n
        head <- if count = 0 then 0 else (head + n) % buf.Length
        if count = 0 && buf.Length > 65536 then buf <- Array.Empty<byte>()

    /// Append all of data. The caller checks Free first.
    member _.Write(data: ReadOnlySpan<byte>) =
        if count + data.Length > buf.Length then
            let mutable size = max 4096 buf.Length
            while size < count + data.Length do size <- size * 2
            let next = Array.zeroCreate<byte> (min size (max cap (count + data.Length)))
            if count > 0 then
                let first = min count (buf.Length - head)
                Buffer.BlockCopy(buf, head, next, 0, first)
                if first < count then Buffer.BlockCopy(buf, 0, next, first, count - first)
            buf <- next
            head <- 0
        let tail = (head + count) % buf.Length
        let first = min data.Length (buf.Length - tail)
        data.Slice(0, first).CopyTo(Span(buf, tail, first))
        if first < data.Length then data.Slice(first).CopyTo(Span(buf, 0, data.Length - first))
        count <- count + data.Length

    member _.Clear() =
        count <- 0
        head <- 0
        buf <- Array.Empty<byte>()

/// Per-flow byte credit (FLOW_STATE format 2, spec 6.10b.4).
[<Struct>]
type CreditPolicy =
    { /// Both ends negotiated caps bit8 + bit9: tx obeys peer limits, rx reports its own.
      Enabled: bool
      /// Initial credit per slot and direction (C0).
      Initial: int
      /// Largest window one flow gets.
      FlowWindowMax: int
      /// Receive budget for reserve bytes plus promised credit, shared by the open flows.
      PoolBytes: int }

module CreditPolicy =
    let C0 = 65536
    let defaults = { Enabled = true; Initial = C0; FlowWindowMax = 8 <<< 20; PoolBytes = 32 <<< 20 }

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
    member val RxDone = false with get, set
    /// The peer's FIN is taken but the reserve still holds bytes: finish when it drains.
    member val RxFinPending = false with get, set
    member val Pending = PriorityQueue<PendingSource, FlowKey>()
    /// Peer's absolute byte limit for this end's tx (C0 until the first report).
    member val TxLimit = 0UL with get, set
    member val TxCreditEpoch = 0u with get, set
    member val TxCreditSeq = 0u with get, set
    /// Absolute byte limit this end promised the peer for its rx.
    member val RxLimit = 0UL with get, set
    /// Reports that still carry this flow (a change goes out in more than one report).
    member val ReportsDue = 0 with get, set
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
        f.RxDone <- false
        f.RxFinPending <- false
        f.TxLimit <- if credit.Enabled then uint64 credit.Initial else UInt64.MaxValue
        f.TxCreditEpoch <- 0u
        f.TxCreditSeq <- 0u
        f.RxLimit <- uint64 credit.Initial
        f.ReportsDue <- 0
        if f.Reserve.Cap <> credit.FlowWindowMax then f.Reserve <- ByteQueue(credit.FlowWindowMax)
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
type FlowTable(port: IFlowPort, store: ISourceStore, stepUndelivered: Action<uint64, int>, credit: CreditPolicy) =
    static let slotCount = 256
    let flows = Array.init slotCount Flow
    let closing = List<int>()
    /// Client side: next slot to try, so that a freed slot rests as long as possible.
    let mutable nextSlot = 0
    let counters = FlowCounters()
    /// Flows with an open rx side: they share the credit pool.
    let mutable rxActive = 0
    let mutable reportsDue = 0

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
            f.Gen <- 0u
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

    let openRx (f: Flow) =
        f.RxOpened <- true
        rxActive <- rxActive + 1
        port.Open(f.Index, f.Gen)

    /// Grow the rx limit when the local connection took bytes (spec 6.10b.4: a limit only
    /// grows). The window per flow is the pool split over the open flows, at least C0.
    let refreshCredit (f: Flow) =
        if credit.Enabled && f.Active && f.RxOpened && not f.RxDone then
            let window = max credit.Initial (min credit.FlowWindowMax (credit.PoolBytes / max 1 rxActive))
            let taken = f.RxDelivered - uint64 f.Reserve.Count
            let limit = taken + uint64 window
            // Report when an eighth of the window is free again, not on every write.
            if limit >= f.RxLimit + uint64 (window / 8) then
                f.RxLimit <- limit
                if f.ReportsDue = 0 then reportsDue <- reportsDue + 1
                f.ReportsDue <- 2

    /// Bytes the local connection of f takes from data; the rest goes to the reserve when
    /// it fits. Returns the bytes taken out of the decoder window, or -1 on failure.
    let deliver (f: Flow) (data: ReadOnlySpan<byte>) =
        let written = if f.Reserve.Count > 0 then 0 else port.Write(f.Index, data)
        if written < 0 then -1
        elif written = data.Length then written
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
                if n < seg.Length then go <- false
        if f.Reserve.Count = 0 && f.RxFinPending then
            f.RxFinPending <- false
            port.Finish f.Index
        f.Reserve.Count = 0

    member _.Counters = counters
    member _.Credit = credit
    /// Flows whose rx limit changed and waits for a FLOW_STATE report.
    member _.ReportsDue = reportsDue
    member _.RxActive = rxActive

    /// Bytes slot may still commit under the peer's limit.
    member _.TxRoom(slot: int) =
        let f = flows.[slot]
        if f.TxLimit = UInt64.MaxValue then Int32.MaxValue
        elif f.TxLimit <= f.TxOffset then 0
        else int (min (f.TxLimit - f.TxOffset) (uint64 Int32.MaxValue))

    /// A FLOW record from the peer about the bytes this end sends (spec 6.10b.2/3): a newer
    /// version per (slot, gen) may only grow the limit.
    member _.OnPeerCredit(epoch: uint32, seq: uint32, r: FlowRecord) =
        let f = flows.[r.FlowSlot]
        if credit.Enabled && f.Active && f.Gen = r.FlowGeneration
           && (epoch > f.TxCreditEpoch || (epoch = f.TxCreditEpoch && seq > f.TxCreditSeq)) then
            f.TxCreditEpoch <- epoch
            f.TxCreditSeq <- seq
            let limit = r.DeliveredOffset + uint64 r.CreditBytes
            if limit > f.TxLimit then f.TxLimit <- limit

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
