/// Parities that wait until the decoder needs them. Spec 5.9 fixes only the result of
/// decoding (maximum-likelihood erasure decoding), not the algorithm. On TCP paths most
/// cells that are missing are late, not lost: a parity that arrives before its sources
/// only costs elimination work. So parities wait here, and go to the decoder when there
/// is evidence of loss; a parity whose step the frontier passed is dropped unused.
namespace Tbt.Session

open System
open Tbt.Wire

[<Sealed>]
type ParityHold(symbolSize: int, capacity: int) =
    let data: byte[] = GC.AllocateUninitializedArray(capacity * symbolSize)
    let metas: Meta[] = Array.zeroCreate capacity
    let addedNs: int64[] = Array.zeroCreate capacity
    /// A slot taken out of the middle leaves a tombstone; the fifo head skips them.
    let alive: bool[] = Array.zeroCreate capacity
    let mutable head = 0
    /// Physical slots in use, tombstones included.
    let mutable occupied = 0
    /// Parities still available (tombstones excluded).
    let mutable live = 0

    let trim () =
        while occupied > 0 && not alive.[head] do
            head <- (head + 1) % capacity
            occupied <- occupied - 1

    member _.Count = live
    /// Physical slots in use, including tombstones: the bound for an offset scan.
    member _.Occupied = occupied
    member _.IsFull = occupied = capacity

    /// Keep a copy of a parity cell. The caller makes room first (IsFull).
    member _.Add(m: Meta, nowNs: int64, symbol: ReadOnlySpan<byte>) =
        let i = (head + occupied) % capacity
        metas.[i] <- m
        addedNs.[i] <- nowNs
        symbol.CopyTo(Span(data, i * symbolSize, symbolSize))
        alive.[i] <- true
        occupied <- occupied + 1
        live <- live + 1

    /// The oldest live parity; valid until the next Add.
    member this.TryPeek(m: byref<Meta>, nowNs: byref<int64>, symbol: byref<ReadOnlySpan<byte>>) =
        trim ()
        if occupied = 0 then false
        else
            m <- metas.[head]
            nowNs <- addedNs.[head]
            symbol <- ReadOnlySpan(data, head * symbolSize, symbolSize)
            true

    /// The parity `offset` places behind the oldest slot. False on a tombstone or beyond
    /// the occupied range. A step the whole session waits on may sit behind parities of
    /// later steps, so the caller must be able to look past the head without removing
    /// anything it may still need.
    member _.TryPeekAt(offset: int, m: byref<Meta>, nowNs: byref<int64>, symbol: byref<ReadOnlySpan<byte>>) =
        if offset < 0 || offset >= occupied then false
        else
            let i = (head + offset) % capacity
            if not alive.[i] then false
            else
                m <- metas.[i]
                nowNs <- addedNs.[i]
                symbol <- ReadOnlySpan(data, i * symbolSize, symbolSize)
                true

    /// Take the parity at `offset` out of the window without disturbing the order of the
    /// others: the slot becomes a tombstone that `trim` collects at the head.
    member _.Kill(offset: int) =
        if offset >= 0 && offset < occupied then
            let i = (head + offset) % capacity
            if alive.[i] then
                alive.[i] <- false
                live <- live - 1

    member _.Drop() =
        trim ()
        if occupied > 0 then
            alive.[head] <- false
            head <- (head + 1) % capacity
            occupied <- occupied - 1
            live <- live - 1
