/// X25519 (RFC 7748 sections 5 and 6.1). Handshake only: IKM = PSK || shared
/// when caps.bit1 is negotiated (spec 6.5.8).
///
/// Constant-time Montgomery ladder over 5×51-bit limbs (the public-domain
/// curve25519-donna-c64 field arithmetic). Every cswap is a mask, field
/// operations have a fixed instruction sequence, and compute / publicKey
/// allocate nothing: no secret-dependent branch, table lookup, or heap
/// allocation on the ladder or the invert.
module Tbt.Crypto.X25519

open System
open System.Buffers.Binary
open System.Runtime.CompilerServices

[<Literal>]
let KeyLength = 32

let BasePoint: byte[] = Array.init KeyLength (fun i -> if i = 0 then 9uy else 0uy)

[<Literal>]
let private Mask51 = 0x7ffffffffffffUL

[<Struct>]
type private Fe =
    val mutable X0: uint64
    val mutable X1: uint64
    val mutable X2: uint64
    val mutable X3: uint64
    val mutable X4: uint64

module private Field =
    let inline one () =
        let mutable f = Fe()
        f.X0 <- 1UL
        f

    let inline cswap (a: byref<Fe>) (b: byref<Fe>) (bit: uint64) =
        let mask = 0UL - bit
        let d0 = mask &&& (a.X0 ^^^ b.X0)
        a.X0 <- a.X0 ^^^ d0
        b.X0 <- b.X0 ^^^ d0
        let d1 = mask &&& (a.X1 ^^^ b.X1)
        a.X1 <- a.X1 ^^^ d1
        b.X1 <- b.X1 ^^^ d1
        let d2 = mask &&& (a.X2 ^^^ b.X2)
        a.X2 <- a.X2 ^^^ d2
        b.X2 <- b.X2 ^^^ d2
        let d3 = mask &&& (a.X3 ^^^ b.X3)
        a.X3 <- a.X3 ^^^ d3
        b.X3 <- b.X3 ^^^ d3
        let d4 = mask &&& (a.X4 ^^^ b.X4)
        a.X4 <- a.X4 ^^^ d4
        b.X4 <- b.X4 ^^^ d4

    let inline add (out: byref<Fe>) (a: inref<Fe>) (b: inref<Fe>) =
        out.X0 <- a.X0 + b.X0
        out.X1 <- a.X1 + b.X1
        out.X2 <- a.X2 + b.X2
        out.X3 <- a.X3 + b.X3
        out.X4 <- a.X4 + b.X4

    /// out = a - b, with the 2^54 bias from donna so every limb stays non-negative.
    let inline sub (out: byref<Fe>) (a: inref<Fe>) (b: inref<Fe>) =
        out.X0 <- a.X0 + (((1UL <<< 54) - 152UL) - b.X0)
        out.X1 <- a.X1 + (((1UL <<< 54) - 8UL) - b.X1)
        out.X2 <- a.X2 + (((1UL <<< 54) - 8UL) - b.X2)
        out.X3 <- a.X3 + (((1UL <<< 54) - 8UL) - b.X3)
        out.X4 <- a.X4 + (((1UL <<< 54) - 8UL) - b.X4)

    let inline private umul (a: uint64) (b: uint64) = UInt128(0UL, a) * UInt128(0UL, b)

    let inline private carry (t0: byref<UInt128>) (t1: byref<UInt128>) (t2: byref<UInt128>)
                             (t3: byref<UInt128>) (t4: byref<UInt128>) (r: byref<Fe>) =
        let mask = UInt128(0UL, Mask51)
        let mutable r0 = uint64 (t0 &&& mask)
        let mutable c = uint64 (t0 >>> 51)
        t1 <- t1 + UInt128(0UL, c)
        let mutable r1 = uint64 (t1 &&& mask)
        c <- uint64 (t1 >>> 51)
        t2 <- t2 + UInt128(0UL, c)
        let mutable r2 = uint64 (t2 &&& mask)
        c <- uint64 (t2 >>> 51)
        t3 <- t3 + UInt128(0UL, c)
        let mutable r3 = uint64 (t3 &&& mask)
        c <- uint64 (t3 >>> 51)
        t4 <- t4 + UInt128(0UL, c)
        let mutable r4 = uint64 (t4 &&& mask)
        c <- uint64 (t4 >>> 51)
        r0 <- r0 + c * 19UL
        c <- r0 >>> 51
        r0 <- r0 &&& Mask51
        r1 <- r1 + c
        c <- r1 >>> 51
        r1 <- r1 &&& Mask51
        r2 <- r2 + c
        r.X0 <- r0
        r.X1 <- r1
        r.X2 <- r2
        r.X3 <- r3
        r.X4 <- r4

    /// out = in2 * in. out must be a distinct Fe from both inputs.
    let mul (out: byref<Fe>) (in2: inref<Fe>) (in1: inref<Fe>) =
        let r0, r1, r2, r3, r4 = in1.X0, in1.X1, in1.X2, in1.X3, in1.X4
        let s0, s1, s2, s3, s4 = in2.X0, in2.X1, in2.X2, in2.X3, in2.X4
        let mutable t0 = umul r0 s0
        let mutable t1 = umul r0 s1 + umul r1 s0
        let mutable t2 = umul r0 s2 + umul r2 s0 + umul r1 s1
        let mutable t3 = umul r0 s3 + umul r3 s0 + umul r1 s2 + umul r2 s1
        let mutable t4 = umul r0 s4 + umul r4 s0 + umul r3 s1 + umul r1 s3 + umul r2 s2
        let r4' = r4 * 19UL
        let r1' = r1 * 19UL
        let r2' = r2 * 19UL
        let r3' = r3 * 19UL
        t0 <- t0 + umul r4' s1 + umul r1' s4 + umul r2' s3 + umul r3' s2
        t1 <- t1 + umul r4' s2 + umul r2' s4 + umul r3' s3
        t2 <- t2 + umul r4' s3 + umul r3' s4
        t3 <- t3 + umul r4' s4
        carry &t0 &t1 &t2 &t3 &t4 &out

    let squareTimes (out: byref<Fe>) (input: inref<Fe>) (count: int) =
        let mutable r0, r1, r2, r3, r4 = input.X0, input.X1, input.X2, input.X3, input.X4
        for _ = 1 to count do
            let d0 = r0 * 2UL
            let d1 = r1 * 2UL
            let d2 = r2 * 2UL * 19UL
            let d419 = r4 * 19UL
            let d4 = d419 * 2UL
            let mutable t0 = umul r0 r0 + umul d4 r1 + umul d2 r3
            let mutable t1 = umul d0 r1 + umul d4 r2 + umul r3 (r3 * 19UL)
            let mutable t2 = umul d0 r2 + umul r1 r1 + umul d4 r3
            let mutable t3 = umul d0 r3 + umul d1 r2 + umul r4 d419
            let mutable t4 = umul d0 r4 + umul d1 r3 + umul r2 r2
            let mask = UInt128(0UL, Mask51)
            r0 <- uint64 (t0 &&& mask)
            let mutable c = uint64 (t0 >>> 51)
            t1 <- t1 + UInt128(0UL, c)
            r1 <- uint64 (t1 &&& mask)
            c <- uint64 (t1 >>> 51)
            t2 <- t2 + UInt128(0UL, c)
            r2 <- uint64 (t2 &&& mask)
            c <- uint64 (t2 >>> 51)
            t3 <- t3 + UInt128(0UL, c)
            r3 <- uint64 (t3 &&& mask)
            c <- uint64 (t3 >>> 51)
            t4 <- t4 + UInt128(0UL, c)
            r4 <- uint64 (t4 &&& mask)
            c <- uint64 (t4 >>> 51)
            r0 <- r0 + c * 19UL
            c <- r0 >>> 51
            r0 <- r0 &&& Mask51
            r1 <- r1 + c
            c <- r1 >>> 51
            r1 <- r1 &&& Mask51
            r2 <- r2 + c
        out.X0 <- r0
        out.X1 <- r1
        out.X2 <- r2
        out.X3 <- r3
        out.X4 <- r4

    /// out = in * scalar (121665 for a24).
    let scale (out: byref<Fe>) (input: inref<Fe>) (scalar: uint64) =
        let mutable a = umul input.X0 scalar
        out.X0 <- uint64 a &&& Mask51
        a <- umul input.X1 scalar + UInt128(0UL, uint64 (a >>> 51))
        out.X1 <- uint64 a &&& Mask51
        a <- umul input.X2 scalar + UInt128(0UL, uint64 (a >>> 51))
        out.X2 <- uint64 a &&& Mask51
        a <- umul input.X3 scalar + UInt128(0UL, uint64 (a >>> 51))
        out.X3 <- uint64 a &&& Mask51
        a <- umul input.X4 scalar + UInt128(0UL, uint64 (a >>> 51))
        out.X4 <- uint64 a &&& Mask51
        out.X0 <- out.X0 + uint64 (a >>> 51) * 19UL

    let expand (out: byref<Fe>) (input: ReadOnlySpan<byte>) =
        out.X0 <- BinaryPrimitives.ReadUInt64LittleEndian input &&& Mask51
        out.X1 <- (BinaryPrimitives.ReadUInt64LittleEndian(input.Slice 6) >>> 3) &&& Mask51
        out.X2 <- (BinaryPrimitives.ReadUInt64LittleEndian(input.Slice 12) >>> 6) &&& Mask51
        out.X3 <- (BinaryPrimitives.ReadUInt64LittleEndian(input.Slice 19) >>> 1) &&& Mask51
        out.X4 <- (BinaryPrimitives.ReadUInt64LittleEndian(input.Slice 24) >>> 12) &&& Mask51

    let contract (output: Span<byte>) (input: inref<Fe>) =
        let mask = UInt128(0UL, Mask51)
        let mutable t0 = UInt128(0UL, input.X0)
        let mutable t1 = UInt128(0UL, input.X1)
        let mutable t2 = UInt128(0UL, input.X2)
        let mutable t3 = UInt128(0UL, input.X3)
        let mutable t4 = UInt128(0UL, input.X4)
        let carryOnce () =
            t1 <- t1 + (t0 >>> 51); t0 <- t0 &&& mask
            t2 <- t2 + (t1 >>> 51); t1 <- t1 &&& mask
            t3 <- t3 + (t2 >>> 51); t2 <- t2 &&& mask
            t4 <- t4 + (t3 >>> 51); t3 <- t3 &&& mask
            t0 <- t0 + UInt128(0UL, 19UL) * (t4 >>> 51); t4 <- t4 &&& mask
        carryOnce ()
        carryOnce ()
        t0 <- t0 + UInt128(0UL, 19UL)
        carryOnce ()
        t0 <- t0 + UInt128(0UL, 0x8000000000000UL - 19UL)
        t1 <- t1 + UInt128(0UL, 0x8000000000000UL - 1UL)
        t2 <- t2 + UInt128(0UL, 0x8000000000000UL - 1UL)
        t3 <- t3 + UInt128(0UL, 0x8000000000000UL - 1UL)
        t4 <- t4 + UInt128(0UL, 0x8000000000000UL - 1UL)
        t1 <- t1 + (t0 >>> 51); t0 <- t0 &&& mask
        t2 <- t2 + (t1 >>> 51); t1 <- t1 &&& mask
        t3 <- t3 + (t2 >>> 51); t2 <- t2 &&& mask
        t4 <- t4 + (t3 >>> 51); t3 <- t3 &&& mask
        t4 <- t4 &&& mask
        BinaryPrimitives.WriteUInt64LittleEndian(output, uint64 (t0 ||| (t1 <<< 51)))
        BinaryPrimitives.WriteUInt64LittleEndian(output.Slice 8, uint64 ((t1 >>> 13) ||| (t2 <<< 38)))
        BinaryPrimitives.WriteUInt64LittleEndian(output.Slice 16, uint64 ((t2 >>> 26) ||| (t3 <<< 25)))
        BinaryPrimitives.WriteUInt64LittleEndian(output.Slice 24, uint64 ((t3 >>> 39) ||| (t4 <<< 12)))

    /// out = z^(2^255 - 21), donna/djb addition chain, fixed sequence.
    let invert (out: byref<Fe>) (z: inref<Fe>) =
        let mutable a = Fe()
        let mutable t0 = Fe()
        let mutable b = Fe()
        let mutable c = Fe()
        squareTimes &a &z 1
        squareTimes &t0 &a 2
        mul &b &t0 &z
        mul &a &b &a
        squareTimes &t0 &a 1
        mul &b &t0 &b
        squareTimes &t0 &b 5
        mul &b &t0 &b
        squareTimes &t0 &b 10
        mul &c &t0 &b
        squareTimes &t0 &c 20
        mul &t0 &t0 &c
        squareTimes &t0 &t0 10
        mul &b &t0 &b
        squareTimes &t0 &b 50
        mul &c &t0 &b
        squareTimes &t0 &c 100
        mul &t0 &t0 &c
        squareTimes &t0 &t0 50
        mul &t0 &t0 &b
        squareTimes &t0 &t0 5
        mul &out &t0 &a

let isZero (x: ReadOnlySpan<byte>) =
    let mutable acc = 0uy
    for i in 0 .. x.Length - 1 do acc <- acc ||| x.[i]
    acc = 0uy

/// X25519(k, u) into output. False when the shared u-coordinate is all zeros
/// (a low-order input; RFC 7748 section 6.1 says the check MAY abort).
[<MethodImpl(MethodImplOptions.NoInlining)>]
let compute (scalar: ReadOnlySpan<byte>) (u: ReadOnlySpan<byte>) (output: Span<byte>) =
    if scalar.Length <> KeyLength || u.Length <> KeyLength || output.Length <> KeyLength then
        invalidArg "scalar" "X25519 inputs and output are 32 bytes"
    let k0 = BinaryPrimitives.ReadUInt64LittleEndian scalar &&& ~~~7UL
    let k1 = BinaryPrimitives.ReadUInt64LittleEndian(scalar.Slice 8)
    let k2 = BinaryPrimitives.ReadUInt64LittleEndian(scalar.Slice 16)
    let k3 = (BinaryPrimitives.ReadUInt64LittleEndian(scalar.Slice 24) &&& 0x7fffffffffffffffUL) ||| (1UL <<< 62)
    let mutable x1 = Fe()
    Field.expand &x1 u
    let mutable x2 = Field.one ()
    let mutable z2 = Fe()
    let mutable x3 = x1
    let mutable z3 = Field.one ()
    let mutable a = Fe()
    let mutable aa = Fe()
    let mutable b = Fe()
    let mutable bb = Fe()
    let mutable e = Fe()
    let mutable c = Fe()
    let mutable d = Fe()
    let mutable da = Fe()
    let mutable cb = Fe()
    let mutable t = Fe()
    let mutable swap = 0UL
    for ti = 254 downto 0 do
        let word = if ti >= 192 then k3 elif ti >= 128 then k2 elif ti >= 64 then k1 else k0
        let kt = (word >>> (ti &&& 63)) &&& 1UL
        swap <- swap ^^^ kt
        Field.cswap &x2 &x3 swap
        Field.cswap &z2 &z3 swap
        swap <- kt
        // RFC 7748 Montgomery ladder step; a24 = 121665.
        Field.add &a &x2 &z2
        Field.squareTimes &aa &a 1
        Field.sub &b &x2 &z2
        Field.squareTimes &bb &b 1
        Field.sub &e &aa &bb
        Field.add &c &x3 &z3
        Field.sub &d &x3 &z3
        Field.mul &da &d &a
        Field.mul &cb &c &b
        Field.add &t &da &cb
        Field.squareTimes &x3 &t 1
        Field.sub &t &da &cb
        Field.squareTimes &t &t 1
        Field.mul &z3 &x1 &t
        Field.mul &x2 &aa &bb
        Field.scale &t &e 121665UL
        Field.add &t &aa &t
        Field.mul &z2 &e &t
    Field.cswap &x2 &x3 swap
    Field.cswap &z2 &z3 swap
    let mutable inv = Fe()
    Field.invert &inv &z2
    let mutable x = Fe()
    Field.mul &x &x2 &inv
    Field.contract output &x
    not (isZero (Span<byte>.op_Implicit output))

/// X25519(k, 9): the public key for scalar k.
let publicKey (scalar: ReadOnlySpan<byte>) (output: Span<byte>) =
    compute scalar (ReadOnlySpan BasePoint) output

/// IKM = PSK || shared (spec 6.5.5).
let ikm (psk: ReadOnlySpan<byte>) (shared: ReadOnlySpan<byte>) =
    let b = Array.zeroCreate (psk.Length + shared.Length)
    psk.CopyTo(Span(b, 0, psk.Length))
    shared.CopyTo(Span(b, psk.Length, shared.Length))
    b
