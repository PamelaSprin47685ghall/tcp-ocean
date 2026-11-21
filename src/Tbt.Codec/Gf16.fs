/// GF(16) with polynomial x^4 + x + 1 (0x13). One byte holds two field values:
/// the low nibble and the high nibble. The same coefficient multiplies both (spec 5.4).
/// GF(2) is the subset {0, 1}; its addition is the same XOR.
module Tbt.Codec.Gf16

open System
open System.Numerics
open System.Runtime.InteropServices
open System.Runtime.Intrinsics
open System.Runtime.Intrinsics.X86
open System.Runtime.Intrinsics.Arm

[<Literal>]
let Poly = 0x13

let private mulNibble (a0: int) (b0: int) =
    let mutable a = a0
    let mutable b = b0
    let mutable r = 0
    for _ in 0..3 do
        if b &&& 1 <> 0 then r <- r ^^^ a
        b <- b >>> 1
        a <- a <<< 1
        if a &&& 0x10 <> 0 then a <- a ^^^ Poly
    r

/// Mul.[a * 16 + b] = a * b.
let Mul: byte[] = Array.init 256 (fun i -> byte (mulNibble (i >>> 4) (i &&& 15)))

/// Inv.[a] = a^-1 for a <> 0; Inv.[0] = 0.
let Inv: byte[] =
    Array.init 16 (fun a ->
        if a = 0 then 0uy
        else byte (Array.findIndex (fun b -> Mul.[a * 16 + b] = 1uy) [| 0..15 |]))

let inline mul (a: byte) (b: byte) = Mul.[int a * 16 + int b]

let inline inv (a: byte) = Inv.[int a]

/// MulByte.[c * 256 + x] = c * x for both nibbles of byte x.
let MulByte: byte[] =
    Array.init (16 * 256) (fun i ->
        let c = i >>> 8
        let x = i &&& 255
        Mul.[c * 16 + (x &&& 15)] ||| (Mul.[c * 16 + (x >>> 4)] <<< 4))

// Per-coefficient 16-byte tables for the nibble-shuffle method: low nibble and high nibble.
let private lowTables: Vector128<byte>[] =
    Array.init 16 (fun c -> Vector128.Create<byte>(Array.init 16 (fun x -> Mul.[c * 16 + x])))

let private highTables: Vector128<byte>[] =
    Array.init 16 (fun c -> Vector128.Create<byte>(Array.init 16 (fun x -> Mul.[c * 16 + x] <<< 4)))

/// dst <- dst XOR src. Lengths must be equal.
let xorInto (src: ReadOnlySpan<byte>) (dst: Span<byte>) =
    if src.Length <> dst.Length then invalidArg "src" "length mismatch"
    let sv = MemoryMarshal.Cast<byte, Vector<byte>>(src)
    let dv = MemoryMarshal.Cast<byte, Vector<byte>>(dst)
    for j in 0 .. sv.Length - 1 do
        dv.[j] <- dv.[j] ^^^ sv.[j]
    for i in sv.Length * Vector<byte>.Count .. src.Length - 1 do
        dst.[i] <- dst.[i] ^^^ src.[i]

let private mulAddScalar (c: int) (src: ReadOnlySpan<byte>) (dst: Span<byte>) (start: int) =
    let row = c * 256
    for i in start .. src.Length - 1 do
        dst.[i] <- dst.[i] ^^^ MulByte.[row + int src.[i]]

/// Multiply each nibble by x in GF(16), polynomial x^4 + x + 1. SSE2 has no
/// byte shuffle, so this is the shift-and-reduce, not a weaker field.
let private sse2MulX (n: Vector128<byte>) =
    let shift = Sse2.ShiftLeftLogical(n.AsInt16(), 1uy).AsByte() &&& Vector128.Create(0x1Euy)
    let top = shift &&& Vector128.Create(0x10uy)
    let red =
        Sse2.ShiftRightLogical(top.AsInt16(), 4uy).AsByte()
        ^^^ Sse2.ShiftRightLogical(top.AsInt16(), 3uy).AsByte()
        ^^^ top
    (shift ^^^ red) &&& Vector128.Create(0x0Fuy)

/// Each byte of n is a nibble. Result is c * nibble, still in the low nibble.
let private sse2MulNibbles (c: int) (n: Vector128<byte>) =
    let mutable p = n
    let mutable acc = Vector128<byte>.Zero
    if c &&& 1 <> 0 then acc <- p
    if c > 1 then
        p <- sse2MulX p
        if c &&& 2 <> 0 then acc <- acc ^^^ p
    if c > 3 then
        p <- sse2MulX p
        if c &&& 4 <> 0 then acc <- acc ^^^ p
    if c > 7 then
        p <- sse2MulX p
        if c &&& 8 <> 0 then acc <- acc ^^^ p
    acc

/// Reference form: split the nibbles, multiply each, merge (kept for the tests).
let sse2MulByteSplit (c: int) (x: Vector128<byte>) =
    let nib = Vector128.Create(0x0Fuy)
    let lo = sse2MulNibbles c (x &&& nib)
    let hi = sse2MulNibbles c (Sse2.ShiftRightLogical(x.AsInt16(), 4uy).AsByte() &&& nib)
    lo ^^^ Sse2.ShiftLeftLogical(hi.AsInt16(), 4uy).AsByte()

/// Multiply both nibbles of every byte by x at once (x^4 = x + 1 per nibble):
/// shift left inside each nibble, then fold each nibble's carried-out top bit back
/// in as 0b0011. Same field and same output as the split form, about half the ops:
/// the split form ran this whole chain twice plus the split and merge.
let private sse2MulX2 (x: Vector128<byte>) =
    let t = x &&& Vector128.Create(0x88uy)
    let s = Sse2.ShiftLeftLogical(x.AsInt16(), 1uy).AsByte() &&& Vector128.Create(0xEEuy)
    s ^^^ Sse2.ShiftRightLogical(t.AsInt16(), 3uy).AsByte() ^^^ Sse2.ShiftRightLogical(t.AsInt16(), 2uy).AsByte()

let sse2MulByte (c: int) (x: Vector128<byte>) =
    let mutable p = x
    let mutable acc = Vector128<byte>.Zero
    if c &&& 1 <> 0 then acc <- p
    if c > 1 then
        p <- sse2MulX2 p
        if c &&& 2 <> 0 then acc <- acc ^^^ p
    if c > 3 then
        p <- sse2MulX2 p
        if c &&& 4 <> 0 then acc <- acc ^^^ p
    if c > 7 then
        p <- sse2MulX2 p
        if c &&& 8 <> 0 then acc <- acc ^^^ p
    acc

let private sse2MulAdd (c: int) (src: ReadOnlySpan<byte>) (dst: Span<byte>) =
    let sv = MemoryMarshal.Cast<byte, Vector128<byte>>(src)
    let dv = MemoryMarshal.Cast<byte, Vector128<byte>>(dst)
    for j in 0 .. sv.Length - 1 do
        dv.[j] <- dv.[j] ^^^ sse2MulByte c sv.[j]
    mulAddScalar c src dst (sv.Length * 16)

let private sse2Scale (c: int) (dst: Span<byte>) =
    let dv = MemoryMarshal.Cast<byte, Vector128<byte>>(dst)
    for j in 0 .. dv.Length - 1 do
        dv.[j] <- sse2MulByte c dv.[j]
    let start = dv.Length * 16
    let row = c * 256
    for i in start .. dst.Length - 1 do
        dst.[i] <- MulByte.[row + int dst.[i]]

/// dst <- dst + c * src (GF(16) multiply-add on both nibbles). Lengths must be equal.
let mulAddInto (c: byte) (src: ReadOnlySpan<byte>) (dst: Span<byte>) =
    if src.Length <> dst.Length then invalidArg "src" "length mismatch"
    if c = 0uy then ()
    elif c = 1uy then xorInto src dst
    elif Ssse3.IsSupported || AdvSimd.Arm64.IsSupported then
        let lo = lowTables.[int c]
        let hi = highTables.[int c]
        let nib = Vector128.Create(0x0Fuy)
        let sv = MemoryMarshal.Cast<byte, Vector128<byte>>(src)
        let dv = MemoryMarshal.Cast<byte, Vector128<byte>>(dst)
        if Ssse3.IsSupported then
            for j in 0 .. sv.Length - 1 do
                let x = sv.[j]
                let l = Ssse3.Shuffle(lo, x &&& nib)
                let h = Ssse3.Shuffle(hi, Vector128.ShiftRightLogical(x, 4) &&& nib)
                dv.[j] <- dv.[j] ^^^ l ^^^ h
        else
            for j in 0 .. sv.Length - 1 do
                let x = sv.[j]
                let l = AdvSimd.Arm64.VectorTableLookup(lo, x &&& nib)
                let h = AdvSimd.Arm64.VectorTableLookup(hi, Vector128.ShiftRightLogical(x, 4) &&& nib)
                dv.[j] <- dv.[j] ^^^ l ^^^ h
        mulAddScalar (int c) src dst (sv.Length * 16)
    elif Sse2.IsSupported then sse2MulAdd (int c) src dst
    else
        mulAddScalar (int c) src dst 0

/// dst <- c * dst.
let scaleInPlace (c: byte) (dst: Span<byte>) =
    if c = 0uy then dst.Clear()
    elif c = 1uy then ()
    elif Sse2.IsSupported && not Ssse3.IsSupported && not AdvSimd.Arm64.IsSupported then
        sse2Scale (int c) dst
    elif c <> 1uy then
        let row = int c * 256
        for i in 0 .. dst.Length - 1 do
            dst.[i] <- MulByte.[row + int dst.[i]]
