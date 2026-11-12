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

/// Delivery order inside a slot: generation first, then stream offset.
[<Struct; CustomComparison; CustomEquality>]
type FlowKey =
    { Gen: uint32
      Offset: uint64 }

    override k.Equals(o: obj) =
        match o with
        | :? FlowKey as q -> k.Gen = q.Gen && k.Offset = q.Offset
        | _ -> false

    override k.GetHashCode() = HashCode.Combine(k.Gen, k.Offset)

    interface IComparable<FlowKey> with
        member k.CompareTo(q) =
            let c = k.Gen.CompareTo q.Gen
            if c <> 0 then c else k.Offset.CompareTo q.Offset

    interface IComparable with
        member k.CompareTo(o) =
            match o with
            | :? FlowKey as q -> (k :> IComparable<FlowKey>).CompareTo q
            | _ -> invalidArg "o" "not a FlowKey"

/// Read access to source symbols that wait in the decoder window.
type ISourceStore =
    abstract Source: step: uint64 * slot: int -> ReadOnlySpan<byte>

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
    member val Pending = PriorityQueue<PendingSource, FlowKey>()

    member f.Active = f.Gen <> 0u

    /// Prepare the slot for generation gen.
    member f.Start(gen: uint32) =
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
type FlowTable(port: IFlowPort, store: ISourceStore, stepUndelivered: Action<uint64, int>) =
    static let slotCount = 256
    let flows = Array.init slotCount Flow
    let closing = List<int>()
    /// Client side: next slot to try, so that a freed slot rests as long as possible.
    let mutable nextSlot = 0
    let counters = FlowCounters()

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
        if f.Active && f.RxDone && f.TxDone then
            dropGen f f.Gen
            f.Gen <- 0u
            counters.Closed <- counters.Closed + 1L
            port.Close f.Index
            true
        else false

    let localFailure (f: Flow) =
        counters.LocalFailures <- counters.LocalFailures + 1L
        dropGen f f.Gen
        f.RxDone <- true
        if not f.TxEndSent then f.TxResetPending <- true

    member _.Counters = counters
    member _.Item(slot: int) = flows.[slot]

    /// Client: allocate a slot and a new generation for a local connection.
    member _.TryAllocate(slot: byref<int>) =
        let mutable found = false
        let mutable i = 0
        while not found && i < slotCount do
            let s = (nextSlot + i) % slotCount
            let f = flows.[s]
            if not f.Active && f.Pending.Count = 0 && f.MaxGen < UInt32.MaxValue then
                f.Start(f.MaxGen + 1u)
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
    member _.OnPeerAck(ack: uint64) =
        let mutable i = closing.Count - 1
        while i >= 0 do
            let f = flows.[closing.[i]]
            if not f.Active || not f.TxEndSent then closing.RemoveAt i
            elif ack > f.TxEndStep then
                f.TxDone <- true
                closing.RemoveAt i
                tryClose f |> ignore
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
                              { Gen = e.FlowGeneration; Offset = e.StreamOffset })
            stepUndelivered.Invoke(step, 1)
            true

    /// Deliver what the local connection of slot can take now.
    member this.Pump(slot: int) =
        let f = flows.[slot]
        let mutable src = Unchecked.defaultof<PendingSource>
        let mutable key = Unchecked.defaultof<FlowKey>
        let mutable go = true
        while go && f.Pending.TryPeek(&src, &key) do
            let isOpen = src.Flags.HasFlag StreamFlags.Open && src.Offset = 0UL
            if key.Gen < f.MaxGen || (key.Gen = f.MaxGen && (not f.Active || f.RxDone)) then
                counters.StaleSources <- counters.StaleSources + 1L
                dropHead f
            elif key.Gen > f.MaxGen then
                // A new generation starts only on a free slot, with its OPEN source.
                if f.Active || not isOpen then go <- false
                else
                    f.Start key.Gen
                    counters.Opened <- counters.Opened + 1L
                    f.RxOpened <- true
                    port.Open(slot, key.Gen)
            elif not f.RxOpened then
                if isOpen then
                    f.RxOpened <- true
                    port.Open(slot, key.Gen)
                else go <- false
            elif src.Offset > f.RxDelivered then go <- false
            else
                let finish = src.Offset + uint64 src.Length
                let remaining = if finish > f.RxDelivered then int (finish - f.RxDelivered) else 0
                let mutable written = remaining
                if remaining > 0 then
                    let skip = src.Length - remaining
                    let data = store.Source(src.Step, int src.Slot).Slice(Tbt.Contracts.Layout.EnvelopeLength + skip, remaining)
                    written <- port.Write(slot, data)
                if written < 0 then
                    localFailure f
                    go <- false
                else
                    f.RxDelivered <- f.RxDelivered + uint64 written
                    if written < remaining then go <- false
                    else
                        dropHead f
                        if src.Flags.HasFlag StreamFlags.Rst then
                            f.RxDone <- true
                            f.TxDone <- true
                            port.Reset slot
                        elif src.Flags.HasFlag StreamFlags.Fin then
                            f.RxDone <- true
                            port.Finish slot
                        if f.RxDone then dropGen f f.Gen
        // A closed slot may already hold the next generation.
        if tryClose f && f.Pending.Count > 0 then this.Pump slot
