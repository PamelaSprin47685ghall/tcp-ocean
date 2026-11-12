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
    let mutable head = 0
    let mutable count = 0

    member _.Count = count
    member _.IsFull = count = capacity

    /// Keep a copy of a parity cell. The caller makes room first (IsFull).
    member _.Add(m: Meta, symbol: ReadOnlySpan<byte>) =
        let i = (head + count) % capacity
        metas.[i] <- m
        symbol.CopyTo(Span(data, i * symbolSize, symbolSize))
        count <- count + 1

    /// The oldest parity; valid until the next Add.
    member _.TryPeek(m: byref<Meta>, symbol: byref<ReadOnlySpan<byte>>) =
        if count = 0 then false
        else
            m <- metas.[head]
            symbol <- ReadOnlySpan(data, head * symbolSize, symbolSize)
            true

    member _.Drop() =
        head <- (head + 1) % capacity
        count <- count - 1
