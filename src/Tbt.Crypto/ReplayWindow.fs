/// Replay window of 2^20 counters per direction (spec 6.6). Check does not change state;
/// only Commit after a valid tag moves the window, so forged counters push nothing out.
namespace Tbt.Crypto

[<Sealed>]
type ReplayWindow() =
    static let bits = 1 <<< 20
    static let mask = uint64 (bits - 1)
    let words: uint64[] = Array.zeroCreate (bits / 64)
    let mutable right = 0UL
    let mutable any = false

    let test (c: uint64) = (words.[int ((c &&& mask) >>> 6)] >>> int (c &&& 63UL)) &&& 1UL <> 0UL
    let set (c: uint64) =
        let i = int ((c &&& mask) >>> 6)
        words.[i] <- words.[i] ||| (1UL <<< int (c &&& 63UL))
    let clear (c: uint64) =
        let i = int ((c &&& mask) >>> 6)
        words.[i] <- words.[i] &&& ~~~(1UL <<< int (c &&& 63UL))

    static member Size = bits

    /// True when counter c may be accepted: newer than the right edge, or inside the
    /// window and not seen yet.
    member _.Check(c: uint64) =
        if not any || c > right then true
        elif right - c >= uint64 bits then false
        else not (test c)

    /// Mark c as seen; move the right edge when c is newer.
    member _.Commit(c: uint64) =
        if not any then
            any <- true
            right <- c
        elif c > right then
            if c - right >= uint64 bits then System.Array.Clear words
            else
                let mutable x = right + 1UL
                while x < c do
                    clear x
                    x <- x + 1UL
                clear c
            right <- c
        set c

    member _.RightEdge = right
