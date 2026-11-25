/// Experimental, path-independent systematic GF(2) code for one bounded window.
/// Not a wire protocol: the caller owns window identifiers, acknowledgements and
/// source retention. Each window has at most 64 symbols and is reset as a unit.
module Tbt.Codec.WindowCodec

open System
open System.Numerics

let private validMask count =
    if count < 1 || count > 64 then invalidArg "count" "must be in 1..64"
    if count = 64 then UInt64.MaxValue else (1UL <<< count) - 1UL

/// Deterministically choose exactly degree distinct symbols without replacement.
let coefficients (count: int) (degree: int) (seed: uint64) =
    validMask count |> ignore
    if degree < 1 || degree > count then invalidArg "degree" "must be in 1..count"
    let mutable rng = SplitMix64.Rng(seed)
    let mutable result = 0UL
    let mutable remaining = degree
    // Partial Fisher-Yates via selection from the remaining bit set; bounded work.
    let mutable available = validMask count
    while remaining > 0 do
        let mutable n = int (rng.Next() % uint64 (BitOperations.PopCount available))
        let mutable bits = available
        while n > 0 do
            bits <- bits &&& (bits - 1UL)
            n <- n - 1
        let bit = bits &&& (~~~bits + 1UL)
        result <- result ||| bit
        available <- available ^^^ bit
        remaining <- remaining - 1
    result

/// XOR selected sources. Systematic symbols are sent directly by the caller.
let encode (sources: byte[][]) (mask: uint64) =
    if isNull (box sources) then nullArg "sources"
    let allowed = validMask sources.Length
    if mask = 0UL || mask &&& ~~~allowed <> 0UL then invalidArg "mask" "invalid coefficients"
    if isNull (box sources.[0]) then invalidArg "sources" "null source"
    let size = sources.[0].Length
    if size = 0 then invalidArg "sources" "empty symbol"
    let output = Array.zeroCreate<byte> size
    for i = 0 to sources.Length - 1 do
        if isNull (box sources.[i]) || sources.[i].Length <> size then invalidArg "sources" "symbol lengths differ"
        if mask &&& (1UL <<< i) <> 0UL then
            for j = 0 to size - 1 do output.[j] <- output.[j] ^^^ sources.[i].[j]
    output

/// Fixed-size reduced echelon matrix. AllocatedBytes counts array payloads,
/// not CLR object headers, transient output copies, or caller-owned sources.
[<Sealed>]
type Decoder(count: int, symbolSize: int, maxBytes: int64) =
    let allowed = validMask count
    do if symbolSize < 1 then invalidArg "symbolSize" "must be positive"
    let bytes = int64 count * 8L + (int64 count + 1L) * int64 symbolSize
    do if maxBytes < bytes || int64 count * int64 symbolSize > int64 Int32.MaxValue then invalidArg "maxBytes" "matrix exceeds budget"
    let masks = Array.zeroCreate<uint64> count
    let payload = Array.zeroCreate<byte> (count * symbolSize)
    let scratch = Array.zeroCreate<byte> symbolSize
    let mutable rank = 0
    let xorRow (dst: byte[]) dstOffset srcOffset =
        for j = 0 to symbolSize - 1 do dst.[dstOffset+j] <- dst.[dstOffset+j] ^^^ payload.[srcOffset+j]
    let checkIndex index =
        if index < 0 || index >= count then invalidArg "index" "outside window"
    /// True when rank increases; false for a consistent dependent equation.
    /// Contradictory dependent equations throw without modifying stored rows.
    member _.Add(mask: uint64, data: byte[]) =
        if mask = 0UL || mask &&& ~~~allowed <> 0UL then invalidArg "mask" "invalid coefficients"
        if isNull (box data) || data.Length <> symbolSize then invalidArg "data" "wrong symbol size"
        Array.Copy(data, scratch, symbolSize)
        let mutable bits = mask
        for i = 0 to count - 1 do
            if bits &&& (1UL <<< i) <> 0UL && masks.[i] <> 0UL then
                bits <- bits ^^^ masks.[i]
                xorRow scratch 0 (i * symbolSize)
        if bits = 0UL then
            if scratch |> Array.exists ((<>) 0uy) then invalidOp "contradictory equation"
            false
        else
            let pivot = BitOperations.TrailingZeroCount bits
            masks.[pivot] <- bits
            Array.Copy(scratch, 0, payload, pivot * symbolSize, symbolSize)
            for i = 0 to count - 1 do
                if i <> pivot && masks.[i] &&& (1UL <<< pivot) <> 0UL then
                    masks.[i] <- masks.[i] ^^^ bits
                    xorRow payload (i * symbolSize) (pivot * symbolSize)
            rank <- rank + 1
            true
    member _.TryGet(index: int) =
        checkIndex index
        if masks.[index] = (1UL <<< index) then
            let result = Array.zeroCreate<byte> symbolSize
            Array.Copy(payload, index * symbolSize, result, 0, symbolSize)
            Some result
        else None
    member _.Rank = rank
    member _.AllocatedBytes = bytes
    /// Reset the complete window. The caller must have delivered all needed data.
    member _.Release() =
        Array.Clear(masks)
        Array.Clear(payload)
        Array.Clear(scratch)
        rank <- 0
