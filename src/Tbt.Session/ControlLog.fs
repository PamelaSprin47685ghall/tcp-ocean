/// Reliable control (CTRL, spec 6.10a): the send log with retries and the receive window
/// with duplicate suppression. No I/O and no clock of its own: the session renders the cells,
/// carries the acknowledgement in WIN_ACK (ctrl_ack_hi, ctrl_ack_bitmap) and gives the time.
namespace Tbt.Session

open System
open Tbt.Wire

/// What the receive window says about an incoming control_seq.
type ControlArrival =
    /// Not seen before: execute it, then call Executed.
    | Fresh = 0
    /// Already executed: only the acknowledgement is sent again.
    | Duplicate = 1
    /// Outside [hi, hi + 64): refused, the current acknowledgement is sent again.
    | OutOfWindow = 2

[<Sealed>]
type ControlLog(retryLimitNs: int64) =
    /// Log size (rule 5): 64 entries, at most 48 the local side starts, 16 kept for replies.
    static let capacity = 64
    static let ownLimit = 48
    static let maxBody = 16
    let seqs = Array.zeroCreate<uint32> capacity
    let ops = Array.zeroCreate<CtrlOp> capacity
    let bodies = Array.zeroCreate<byte> (capacity * maxBody)
    let lengths = Array.zeroCreate<int> capacity
    let replies = Array.zeroCreate<bool> capacity
    let firstNs = Array.zeroCreate<int64> capacity
    let sentNs = Array.zeroCreate<int64> capacity
    let used = Array.zeroCreate<bool> capacity
    let mutable count = 0
    let mutable own = 0
    let mutable nextSeq = 1u
    let mutable failed = false
    // Receive window: all < hi executed; bit i: hi + i executed.
    let mutable hi = 1u
    let mutable bitmap = 0UL

    let add (op: CtrlOp) (body: ReadOnlySpan<byte>) (reply: bool) =
        let n = Ctrl.bodyLength op
        if n < 0 || body.Length <> n then invalidArg "body" "body length does not match the operation"
        if count = capacity || (not reply && own = ownLimit) || nextSeq = UInt32.MaxValue then 0u
        else
            let i = Array.IndexOf(used, false)
            used.[i] <- true
            seqs.[i] <- nextSeq
            ops.[i] <- op
            body.CopyTo(Span(bodies, i * maxBody, maxBody))
            lengths.[i] <- n
            replies.[i] <- reply
            firstNs.[i] <- -1L
            sentNs.[i] <- -1L
            count <- count + 1
            if not reply then own <- own + 1
            nextSeq <- nextSeq + 1u
            seqs.[i]

    let remove (i: int) =
        used.[i] <- false
        count <- count - 1
        if not replies.[i] then own <- own - 1

    new() = ControlLog(30_000_000_000L)

    /// Start an operation. Returns its control_seq, or 0 when the log has no room.
    member _.Start(op: CtrlOp, body: ReadOnlySpan<byte>) = add op body false
    /// Queue the reply to an executed request (uses the reserve).
    member _.Reply(op: CtrlOp, body: ReadOnlySpan<byte>) = add op body true
    /// Room for one more reply: a request that needs a reply is accepted only then.
    member _.CanReply = count < capacity
    member _.Pending = count
    /// An entry passed the retry limit without an acknowledgement (rule 6): a hard failure.
    member _.Failed = failed

    /// True when an entry is due (never sent, or unacknowledged for 1.05 x SRTT) and not
    /// past the retry limit. No side effects: TryRenderDue does the sending.
    member _.IsDue(now: int64, srttNs: int64) =
        let wait = srttNs + srttNs / 20L
        let mutable due = false
        let mutable i = 0
        while not due && i < capacity do
            if used.[i] && (sentNs.[i] < 0L || now - sentNs.[i] >= wait)
               && not (firstNs.[i] >= 0L && now - firstNs.[i] > retryLimitNs) then
                due <- true
            i <- i + 1
        due

    /// Mark the log failed when an entry passed the retry limit (rule 6); returns Failed.
    member _.CheckLimit(now: int64) =
        for i in 0 .. capacity - 1 do
            if used.[i] && firstNs.[i] >= 0L && now - firstNs.[i] > retryLimitNs then failed <- true
        failed

    /// True while the entry with this control_seq waits for its acknowledgement.
    member _.Contains(seq: uint32) =
        let mutable found = false
        for i in 0 .. capacity - 1 do
            if used.[i] && seqs.[i] = seq then found <- true
        found

    /// The entry to send now: never sent, or unacknowledged for 1.05 x SRTT (rule 3).
    /// Writes the CTRL symbol into dst and returns true, or false when nothing is due.
    member _.TryRenderDue(now: int64, srttNs: int64, dst: Span<byte>) =
        let wait = srttNs + srttNs / 20L
        let mutable best = -1
        for i in 0 .. capacity - 1 do
            if used.[i] && (sentNs.[i] < 0L || now - sentNs.[i] >= wait) then
                if firstNs.[i] >= 0L && now - firstNs.[i] > retryLimitNs then failed <- true
                elif best < 0 || seqs.[i] < seqs.[best] then best <- i
        if best < 0 then false
        else
            let body = Ctrl.write seqs.[best] ops.[best] dst
            ReadOnlySpan(bodies, best * maxBody, lengths.[best]).CopyTo body
            if firstNs.[best] < 0L then firstNs.[best] <- now
            sentNs.[best] <- now
            true

    /// The peer's acknowledgement from WIN_ACK: entries it executed leave the log.
    member _.OnAck(ackHi: uint32, ackBitmap: uint64) =
        for i in 0 .. capacity - 1 do
            if used.[i] then
                let s = seqs.[i]
                if s < ackHi || (s - ackHi < 64u && (ackBitmap >>> int (s - ackHi)) &&& 1UL <> 0UL) then
                    remove i

    /// Classify an incoming control_seq (rule 2: window [hi, hi + 64), no wrap guessing).
    member _.Arrive(seq: uint32) =
        if seq < hi then ControlArrival.Duplicate
        elif uint64 seq >= uint64 hi + 64UL then ControlArrival.OutOfWindow
        elif (bitmap >>> int (seq - hi)) &&& 1UL <> 0UL then ControlArrival.Duplicate
        else ControlArrival.Fresh

    /// The operation seq was executed (or accepted with its reply queued): acknowledge it.
    member _.Executed(seq: uint32) =
        if seq >= hi && uint64 seq < uint64 hi + 64UL then
            bitmap <- bitmap ||| (1UL <<< int (seq - hi))
            while bitmap &&& 1UL <> 0UL do
                bitmap <- bitmap >>> 1
                hi <- hi + 1u

    /// For WIN_ACK.
    member _.AckHi = hi
    member _.AckBitmap = bitmap
