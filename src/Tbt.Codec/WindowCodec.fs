/// Experimental, path-independent systematic GF(2) code for one bounded window.
/// Not a wire protocol: the caller owns window identifiers, acknowledgements and
/// source retention. Each window has at most 64 symbols and is reset as a unit.
module Tbt.Codec.WindowCodec

open System
open System.Collections.Generic
open System.Numerics
open System.Runtime.InteropServices
open System.Runtime.Intrinsics
open System.Runtime.Intrinsics.Arm
open System.Runtime.Intrinsics.X86

let private validMask count =
    if count < 1 || count > 64 then invalidArg "count" "must be in 1..64"
    if count = 64 then UInt64.MaxValue else (1UL <<< count) - 1UL

/// Deterministically choose exactly degree distinct active slots without replacement.
let coefficientsFromMask (activeMask: uint64) (degree: int) (seed: uint64) =
    let count = int (BitOperations.PopCount activeMask)
    if count = 0 then invalidArg "activeMask" "must not be empty"
    if degree < 1 || degree > count then invalidArg "degree" "must be in 1..active slot count"
    let mutable rng = SplitMix64.Rng(seed)
    let mutable result = 0UL
    let mutable remaining = degree
    let mutable available = activeMask
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

/// Deterministically choose exactly degree distinct symbols without replacement.
let coefficients (count: int) (degree: int) (seed: uint64) =
    coefficientsFromMask (validMask count) degree seed

/// GF(2) add. AVX2 covers 32 bytes, AdvSimd 16, Vector the host width, then a scalar tail.
let xorInto (src: ReadOnlySpan<byte>) (dst: Span<byte>) =
    if src.Length <> dst.Length then invalidArg (nameof src) "length mismatch"
    let mutable i = 0
    if Avx2.IsSupported then
        let n = src.Length / 32
        let sv = MemoryMarshal.Cast<byte, Vector256<byte>>(src.Slice(0, n * 32))
        let dv = MemoryMarshal.Cast<byte, Vector256<byte>>(dst.Slice(0, n * 32))
        for j = 0 to n - 1 do dv.[j] <- dv.[j] ^^^ sv.[j]
        i <- n * 32
    elif AdvSimd.IsSupported then
        let n = src.Length / 16
        let sv = MemoryMarshal.Cast<byte, Vector128<byte>>(src.Slice(0, n * 16))
        let dv = MemoryMarshal.Cast<byte, Vector128<byte>>(dst.Slice(0, n * 16))
        for j = 0 to n - 1 do dv.[j] <- dv.[j] ^^^ sv.[j]
        i <- n * 16
    let sv = MemoryMarshal.Cast<byte, Vector<byte>>(src.Slice i)
    let dv = MemoryMarshal.Cast<byte, Vector<byte>>(dst.Slice i)
    for j = 0 to sv.Length - 1 do dv.[j] <- dv.[j] ^^^ sv.[j]
    for k = i + sv.Length * Vector<byte>.Count to src.Length - 1 do
        dst.[k] <- dst.[k] ^^^ src.[k]

/// Same bytes as xorInto, forced scalar. Tests use this as the equivalence oracle.
let xorIntoScalar (src: ReadOnlySpan<byte>) (dst: Span<byte>) =
    if src.Length <> dst.Length then invalidArg (nameof src) "length mismatch"
    for i = 0 to src.Length - 1 do dst.[i] <- dst.[i] ^^^ src.[i]

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
        if mask &&& (1UL <<< i) <> 0UL then xorInto (ReadOnlySpan sources.[i]) (Span output)
    output

/// Outcome of adding one equation, reported as a struct so the receive path
/// stays allocation free. Invalid never mutates stored rows; OutOfCapacity means
/// no pivot can be assigned because all rows are already committed.
[<Struct>]
type AddOutcome =
    | Innovative
    | Duplicate
    | Invalid
    | OutOfCapacity

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
        xorInto (ReadOnlySpan(payload, srcOffset, symbolSize)) (Span(dst, dstOffset, symbolSize))
    let checkIndex index =
        if index < 0 || index >= count then invalidArg "index" "outside window"
    /// Structural, atomic add. Scratch is the only line touched before the row
    /// is known to be independent; a contradiction leaves masks/payload/rank intact.
    member this.TryAdd(mask: uint64, data: ReadOnlySpan<byte>) : AddOutcome =
        if mask = 0UL || mask &&& ~~~allowed <> 0UL then Invalid
        elif data.Length <> symbolSize then Invalid
        else
            data.CopyTo(Span scratch)
            let mutable bits = mask
            for i = 0 to count - 1 do
                if bits &&& (1UL <<< i) <> 0UL && masks.[i] <> 0UL then
                    bits <- bits ^^^ masks.[i]
                    xorRow scratch 0 (i * symbolSize)
            if bits = 0UL then
                let mutable nonzero = false
                let mutable j = 0
                while not nonzero && j < symbolSize do
                    if scratch.[j] <> 0uy then nonzero <- true
                    j <- j + 1
                if nonzero then Invalid else Duplicate
            elif rank >= count then
                OutOfCapacity
            else
                let pivot = BitOperations.TrailingZeroCount bits
                masks.[pivot] <- bits
                Array.Copy(scratch, 0, payload, pivot * symbolSize, symbolSize)
                for i = 0 to count - 1 do
                    if i <> pivot && masks.[i] &&& (1UL <<< pivot) <> 0UL then
                        masks.[i] <- masks.[i] ^^^ bits
                        xorRow payload (i * symbolSize) (pivot * symbolSize)
                rank <- rank + 1
                Innovative
    /// Legacy signature. True when a new row was committed, false for a
    /// consistent dependent equation. Contradictions throw without state change.
    member this.Add(mask: uint64, data: byte[]) =
        if isNull (box data) then nullArg "data"
        match this.TryAdd(mask, ReadOnlySpan data) with
        | Innovative -> true
        | Duplicate -> false
        | Invalid -> invalidOp "invalid or contradictory equation"
        | OutOfCapacity -> invalidOp "decoder row capacity exhausted"
    /// Borrow a solved source row without allocating. The caller must keep this
    /// window alive and must not call Release while the span is in use. Adding
    /// equations leaves solved unit rows unchanged; the decoder is owner-thread only.
    /// An empty span means that the index is not yet solved.
    member _.Source(index: int) : ReadOnlySpan<byte> =
        checkIndex index
        if masks.[index] = (1UL <<< index) then
            ReadOnlySpan(payload, index * symbolSize, symbolSize)
        else ReadOnlySpan<byte>.Empty
    member _.TryCopyKnown(index: int, dst: Span<byte>) =
        checkIndex index
        if dst.Length <> symbolSize then invalidArg "dst" "wrong symbol size"
        if masks.[index] = (1UL <<< index) then
            ReadOnlySpan(payload, index * symbolSize, symbolSize).CopyTo dst
            true
        else false
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

/// A sealed tail is a window whose last k sources (1..64) are known. Parity masks
/// are regenerated from (windowId, parity index, k); coefficients never travel on the wire.
module WindowSessionCodec =
    [<Literal>]
    let MaxSymbols = 64

    [<Literal>]
    let MaxWindows = 160

    /// 16 MiB hard cap on decoder row storage across live windows.
    [<Literal>]
    let MaxDecoderBytes = 16L <<< 20

    let private checkK k =
        if k < 1 || k > MaxSymbols then invalidArg (nameof k) "tail length must be in 1..64"

    /// Degree stays in 1..min(8, k). A tail of 1 or 2 cannot take a degree of 3.
    let degree (k: int) =
        checkK k
        min k (min 8 (max 3 (k / 8)))

    /// Stateless seed: both ends derive the same mask without exchanging coefficients.
    let paritySeed (windowId: uint64) (parityIdx: int) (k: int) =
        checkK k
        if parityIdx < 0 then invalidArg (nameof parityIdx) "negative"
        SplitMix64.seedOf4 SplitMix64.Golden windowId (uint64 parityIdx) (uint64 k) SplitMix64.DomParity

    let parityMask (windowId: uint64) (parityIdx: int) (k: int) =
        let deg = degree k
        coefficients k deg (paritySeed windowId parityIdx k)

    /// One parity symbol for a sealed tail of exactly k sources.
    let encodeParity (windowId: uint64) (parityIdx: int) (sources: byte[][]) =
        if isNull (box sources) then nullArg (nameof sources)
        encode sources (parityMask windowId parityIdx sources.Length)

/// Live windows, each sealed at its own k. Release returns a window's row storage
/// as soon as the caller has delivered it (ack frontier).
[<Sealed>]
type WindowCodecPool(symbolSize: int, maxWindows: int, maxBytes: int64) =
    let windows = Dictionary<uint64, Decoder>()
    let mutable used = 0L
    do
        if symbolSize < 1 then invalidArg (nameof symbolSize) "must be positive"
        if maxWindows < 1 || maxWindows > WindowSessionCodec.MaxWindows then
            invalidArg (nameof maxWindows) "must be in 1..160"
        if maxBytes < 1L || maxBytes > WindowSessionCodec.MaxDecoderBytes then
            invalidArg (nameof maxBytes) "must be in 1..16MiB"

    let decoderBytes k = int64 k * 8L + (int64 k + 1L) * int64 symbolSize

    member _.Count = windows.Count
    member _.AllocatedBytes = used
    member _.MaxBytes = maxBytes

    /// Open a sealed window of k sources. False when the window cap or byte cap would be exceeded.
    member _.TryOpen(windowId: uint64, k: int) =
        if k < 1 || k > WindowSessionCodec.MaxSymbols then invalidArg (nameof k) "tail length must be in 1..64"
        if windows.ContainsKey windowId then false
        elif windows.Count >= maxWindows then false
        else
            let bytes = decoderBytes k
            if used > maxBytes - bytes then false
            else
                windows.Add(windowId, Decoder(k, symbolSize, bytes))
                used <- used + bytes
                true

    member _.TryGet(windowId: uint64) =
        match windows.TryGetValue windowId with
        | true, decoder -> ValueSome decoder
        | _ -> ValueNone

    /// Remove one window and return its exact byte budget. Used to roll back a
    /// freshly opened window when a later validation/add step fails. Idempotent.
    member _.Remove(windowId: uint64) =
        match windows.TryGetValue windowId with
        | true, decoder ->
            used <- used - decoder.AllocatedBytes
            decoder.Release()
            windows.Remove windowId |> ignore
            true
        | _ -> false

    /// Drop every window whose id is at or below the acknowledged frontier.
    member _.ReleaseThrough(ackWindow: uint64) =
        let drop = ResizeArray<uint64>()
        for KeyValue(id, _) in windows do
            if id <= ackWindow then drop.Add id
        for id in drop do
            match windows.TryGetValue id with
            | true, decoder ->
                used <- used - decoder.AllocatedBytes
                decoder.Release()
                windows.Remove id |> ignore
            | _ -> ()
        if windows.Count = 0 then used <- 0L
