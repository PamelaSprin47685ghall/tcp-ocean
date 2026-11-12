/// 256-bit set of local flow slots (spec 10.4.5, 16.3.3 RS-1…3). Every operation is
/// bounded and allocation free on valid inputs; invalid inputs fail before indexing.
namespace Tbt.Contracts

open System
open System.Numerics

[<Sealed; AllowNullLiteral>]
type ReadySet() =
    let words = Array.zeroCreate<uint64> 4
    let mutable count = 0
    static let checkSlot slot =
        if slot < 0 || slot > 255 then invalidArg "slot" "Slot must be in 0..255."

    /// Set slots, 0..256: full and empty differ (WIRE-2).
    member _.Count = count
    member _.IsEmpty = count = 0

    member _.Add(slot: int) =
        checkSlot slot
        let w = slot >>> 6
        let bit = 1UL <<< (slot &&& 63)
        if words.[w] &&& bit = 0UL then
            words.[w] <- words.[w] ||| bit
            count <- count + 1

    member _.Remove(slot: int) =
        checkSlot slot
        let w = slot >>> 6
        let bit = 1UL <<< (slot &&& 63)
        if words.[w] &&& bit <> 0UL then
            words.[w] <- words.[w] &&& ~~~bit
            count <- count - 1

    /// Add or remove slot.
    member this.Set(slot: int, on: bool) = if on then this.Add slot else this.Remove slot

    member _.Contains(slot: int) =
        checkSlot slot
        words.[slot >>> 6] &&& (1UL <<< (slot &&& 63)) <> 0UL

    /// The next set slot after after (-1..255), with wrap; -1 for an empty set.
    /// At most five word probes, the fifth being the wrap to the first word.
    member _.NextAfter(after: int) =
        if after < -1 || after > 255 then invalidArg "after" "After must be in -1..255."
        if count = 0 then -1
        else
            let start = (after + 1) &&& 255
            let mutable result = -1
            let mutable i = 0
            while result < 0 && i < 5 do
                let w = ((start >>> 6) + i) &&& 3
                let mutable m = words.[w]
                if i = 0 then m <- m &&& (UInt64.MaxValue <<< (start &&& 63))
                if m <> 0UL then result <- (w <<< 6) + BitOperations.TrailingZeroCount m
                i <- i + 1
            result

    /// The next set slot greater than after (-1..255), without wrap; -1 when none.
    /// For one pass over the set in slot order: s = NextUp -1, then s = NextUp s.
    member _.NextUp(after: int) =
        if after < -1 || after > 255 then invalidArg "after" "After must be in -1..255."
        if after = 255 || count = 0 then -1
        else
            let start = after + 1
            let mutable result = -1
            let mutable w = start >>> 6
            let mutable m = words.[w] &&& (UInt64.MaxValue <<< (start &&& 63))
            while result < 0 && w < 4 do
                if m <> 0UL then result <- (w <<< 6) + BitOperations.TrailingZeroCount m
                else
                    w <- w + 1
                    if w < 4 then m <- words.[w]
            result

    member _.Clear() =
        Array.Clear words
        count <- 0
