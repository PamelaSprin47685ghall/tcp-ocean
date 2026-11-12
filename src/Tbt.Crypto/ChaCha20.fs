/// ChaCha20 (RFC 8439 sections 2.3 and 2.4): the block function for header protection and
/// the key stream for the AEAD. No allocation per call.
module Tbt.Crypto.ChaCha20

open System
open System.Buffers.Binary
open System.Numerics
open System.Runtime.InteropServices
open System.Runtime.Intrinsics

let inline private qr (a: byref<uint32>) (b: byref<uint32>) (c: byref<uint32>) (d: byref<uint32>) =
    a <- a + b
    d <- BitOperations.RotateLeft(d ^^^ a, 16)
    c <- c + d
    b <- BitOperations.RotateLeft(b ^^^ c, 12)
    a <- a + b
    d <- BitOperations.RotateLeft(d ^^^ a, 8)
    c <- c + d
    b <- BitOperations.RotateLeft(b ^^^ c, 7)

/// Write (or XOR, when mix) word i of a block into output, cut at output.Length.
let private put (mix: bool) (output: Span<byte>) (i: int) (w: uint32) =
    let at = 4 * i
    if at < output.Length then
        for j in 0 .. min 4 (output.Length - at) - 1 do
            let k = byte (w >>> (8 * j))
            output.[at + j] <- if mix then output.[at + j] ^^^ k else k

/// The 8 little-endian words of a 32-byte key.
let keyWords (key: ReadOnlySpan<byte>) =
    if key.Length <> 32 then invalidArg "key" "ChaCha20 key is 32 bytes"
    let w = Array.zeroCreate<uint32> 8
    for i in 0 .. 7 do
        w.[i] <- BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4 * i))
    w

let private blockCore (mix: bool) (key: uint32[]) (counter: uint32) (nonce: ReadOnlySpan<byte>) (output: Span<byte>) =
    if key.Length <> 8 || nonce.Length <> 12 || output.Length > 64 then invalidArg "nonce" "bad key, nonce or output length"
    let n0 = BinaryPrimitives.ReadUInt32LittleEndian nonce
    let n1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice 4)
    let n2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice 8)
    let mutable x0 = 0x61707865u
    let mutable x1 = 0x3320646eu
    let mutable x2 = 0x79622d32u
    let mutable x3 = 0x6b206574u
    let mutable x4 = key.[0]
    let mutable x5 = key.[1]
    let mutable x6 = key.[2]
    let mutable x7 = key.[3]
    let mutable x8 = key.[4]
    let mutable x9 = key.[5]
    let mutable x10 = key.[6]
    let mutable x11 = key.[7]
    let mutable x12 = counter
    let mutable x13 = n0
    let mutable x14 = n1
    let mutable x15 = n2
    for _ in 1 .. 10 do
        qr &x0 &x4 &x8 &x12
        qr &x1 &x5 &x9 &x13
        qr &x2 &x6 &x10 &x14
        qr &x3 &x7 &x11 &x15
        qr &x0 &x5 &x10 &x15
        qr &x1 &x6 &x11 &x12
        qr &x2 &x7 &x8 &x13
        qr &x3 &x4 &x9 &x14
    put mix output 0 (x0 + 0x61707865u)
    put mix output 1 (x1 + 0x3320646eu)
    put mix output 2 (x2 + 0x79622d32u)
    put mix output 3 (x3 + 0x6b206574u)
    put mix output 4 (x4 + key.[0])
    put mix output 5 (x5 + key.[1])
    put mix output 6 (x6 + key.[2])
    put mix output 7 (x7 + key.[3])
    put mix output 8 (x8 + key.[4])
    put mix output 9 (x9 + key.[5])
    put mix output 10 (x10 + key.[6])
    put mix output 11 (x11 + key.[7])
    put mix output 12 (x12 + counter)
    put mix output 13 (x13 + n0)
    put mix output 14 (x14 + n1)
    put mix output 15 (x15 + n2)

/// Write the first output.Length (at most 64) key stream bytes of one block.
let block key counter nonce output = blockCore false key counter nonce output

let inline private rotl (v: Vector128<uint32>) (n: int) =
    Vector128.ShiftLeft(v, n) ||| Vector128.ShiftRightLogical(v, 32 - n)

let inline private columnRound (a: byref<Vector128<uint32>>) (b: byref<Vector128<uint32>>)
                               (c: byref<Vector128<uint32>>) (d: byref<Vector128<uint32>>) =
    a <- a + b
    d <- rotl (d ^^^ a) 16
    c <- c + d
    b <- rotl (b ^^^ c) 12
    a <- a + b
    d <- rotl (d ^^^ a) 8
    c <- c + d
    b <- rotl (b ^^^ c) 7

/// Quarter round on four lanes, by value so that the JIT keeps every word in a register.
let inline private quarter (a: Vector128<uint32>) (b: Vector128<uint32>) (c: Vector128<uint32>) (d: Vector128<uint32>) =
    let a = a + b
    let d = rotl (d ^^^ a) 16
    let c = c + d
    let b = rotl (b ^^^ c) 12
    let a = a + b
    let d = rotl (d ^^^ a) 8
    let c = c + d
    let b = rotl (b ^^^ c) 7
    struct (a, b, c, d)

/// Four blocks at once, one block per lane: x_i holds word i of blocks ctr .. ctr+3.
/// The 16 output vectors go to words (64 words, word i of block j at 4 i + j).
let private quadBlock (key: uint32[]) (ctr: uint32) (n0: uint32) (n1: uint32) (n2: uint32) (words: Span<uint32>) =
    let c0 = Vector128.Create 0x61707865u
    let c1 = Vector128.Create 0x3320646eu
    let c2 = Vector128.Create 0x79622d32u
    let c3 = Vector128.Create 0x6b206574u
    let k0 = Vector128.Create key.[0]
    let k1 = Vector128.Create key.[1]
    let k2 = Vector128.Create key.[2]
    let k3 = Vector128.Create key.[3]
    let k4 = Vector128.Create key.[4]
    let k5 = Vector128.Create key.[5]
    let k6 = Vector128.Create key.[6]
    let k7 = Vector128.Create key.[7]
    let cc = Vector128.Create(ctr, ctr + 1u, ctr + 2u, ctr + 3u)
    let m0 = Vector128.Create n0
    let m1 = Vector128.Create n1
    let m2 = Vector128.Create n2
    let mutable x0 = c0
    let mutable x1 = c1
    let mutable x2 = c2
    let mutable x3 = c3
    let mutable x4 = k0
    let mutable x5 = k1
    let mutable x6 = k2
    let mutable x7 = k3
    let mutable x8 = k4
    let mutable x9 = k5
    let mutable x10 = k6
    let mutable x11 = k7
    let mutable x12 = cc
    let mutable x13 = m0
    let mutable x14 = m1
    let mutable x15 = m2
    for _ in 1 .. 10 do
        let struct (x0', x4', x8', x12') = quarter x0 x4 x8 x12
        x0 <- x0'
        x4 <- x4'
        x8 <- x8'
        x12 <- x12'
        let struct (x1', x5', x9', x13') = quarter x1 x5 x9 x13
        x1 <- x1'
        x5 <- x5'
        x9 <- x9'
        x13 <- x13'
        let struct (x2', x6', x10', x14') = quarter x2 x6 x10 x14
        x2 <- x2'
        x6 <- x6'
        x10 <- x10'
        x14 <- x14'
        let struct (x3', x7', x11', x15') = quarter x3 x7 x11 x15
        x3 <- x3'
        x7 <- x7'
        x11 <- x11'
        x15 <- x15'
        let struct (x0', x5', x10', x15') = quarter x0 x5 x10 x15
        x0 <- x0'
        x5 <- x5'
        x10 <- x10'
        x15 <- x15'
        let struct (x1', x6', x11', x12') = quarter x1 x6 x11 x12
        x1 <- x1'
        x6 <- x6'
        x11 <- x11'
        x12 <- x12'
        let struct (x2', x7', x8', x13') = quarter x2 x7 x8 x13
        x2 <- x2'
        x7 <- x7'
        x8 <- x8'
        x13 <- x13'
        let struct (x3', x4', x9', x14') = quarter x3 x4 x9 x14
        x3 <- x3'
        x4 <- x4'
        x9 <- x9'
        x14 <- x14'
    let w = MemoryMarshal.Cast<uint32, Vector128<uint32>> words
    w.[0] <- x0 + c0
    w.[1] <- x1 + c1
    w.[2] <- x2 + c2
    w.[3] <- x3 + c3
    w.[4] <- x4 + k0
    w.[5] <- x5 + k1
    w.[6] <- x6 + k2
    w.[7] <- x7 + k3
    w.[8] <- x8 + k4
    w.[9] <- x9 + k5
    w.[10] <- x10 + k6
    w.[11] <- x11 + k7
    w.[12] <- x12 + cc
    w.[13] <- x13 + m0
    w.[14] <- x14 + m1
    w.[15] <- x15 + m2

/// XOR src with the key stream from block counter (RFC 8439 section 2.4) into dst.
/// dst may be the same memory as src.
/// scratch is 64 words owned by the caller.
let xorStream (key: uint32[]) (counter: uint32) (nonce: ReadOnlySpan<byte>) (src: ReadOnlySpan<byte>) (dst: Span<byte>) (scratch: Span<uint32>) =
    if key.Length <> 8 || nonce.Length <> 12 || dst.Length <> src.Length || scratch.Length < 64 then
        invalidArg "dst" "bad key, nonce, length or scratch"
    let mutable at = 0
    let mutable ctr = counter
    if Vector128.IsHardwareAccelerated then
        let n0 = BinaryPrimitives.ReadUInt32LittleEndian nonce
        let n1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice 4)
        let n2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice 8)
        while src.Length - at >= 256 do
            quadBlock key ctr n0 n1 n2 scratch
            for j in 0 .. 3 do
                let s = MemoryMarshal.Cast<byte, uint32>(src.Slice(at + 64 * j, 64))
                let o = MemoryMarshal.Cast<byte, uint32>(dst.Slice(at + 64 * j, 64))
                for i in 0 .. 15 do
                    let k = scratch.[4 * i + j]
                    o.[i] <- s.[i] ^^^ (if BitConverter.IsLittleEndian then k else BinaryPrimitives.ReverseEndianness k)
            at <- at + 256
            ctr <- ctr + 4u
        // One block as four rows; diagonal rounds rotate rows b, c and d by lanes.
        let r0 = Vector128.Create(0x61707865u, 0x3320646eu, 0x79622d32u, 0x6b206574u)
        let r1 = Vector128.Create(key.[0], key.[1], key.[2], key.[3])
        let r2 = Vector128.Create(key.[4], key.[5], key.[6], key.[7])
        let n0 = BinaryPrimitives.ReadUInt32LittleEndian nonce
        let n1 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice 4)
        let n2 = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice 8)
        while src.Length - at >= 64 do
            let r3 = Vector128.Create(ctr, n0, n1, n2)
            let mutable a = r0
            let mutable b = r1
            let mutable c = r2
            let mutable d = r3
            for _ in 1 .. 10 do
                columnRound &a &b &c &d
                b <- Vector128.Shuffle(b, Vector128.Create(1u, 2u, 3u, 0u))
                c <- Vector128.Shuffle(c, Vector128.Create(2u, 3u, 0u, 1u))
                d <- Vector128.Shuffle(d, Vector128.Create(3u, 0u, 1u, 2u))
                columnRound &a &b &c &d
                b <- Vector128.Shuffle(b, Vector128.Create(3u, 0u, 1u, 2u))
                c <- Vector128.Shuffle(c, Vector128.Create(2u, 3u, 0u, 1u))
                d <- Vector128.Shuffle(d, Vector128.Create(1u, 2u, 3u, 0u))
            let s = src.Slice at
            let o = dst.Slice at
            (Vector128.Create<byte>(s.Slice(0, 16)) ^^^ (a + r0).AsByte()).CopyTo o
            (Vector128.Create<byte>(s.Slice(16, 16)) ^^^ (b + r1).AsByte()).CopyTo(o.Slice 16)
            (Vector128.Create<byte>(s.Slice(32, 16)) ^^^ (c + r2).AsByte()).CopyTo(o.Slice 32)
            (Vector128.Create<byte>(s.Slice(48, 16)) ^^^ (d + r3).AsByte()).CopyTo(o.Slice 48)
            at <- at + 64
            ctr <- ctr + 1u
    src.Slice(at).CopyTo(dst.Slice at)
    while at < src.Length do
        let n = min 64 (src.Length - at)
        blockCore true key ctr nonce (dst.Slice(at, n))
        at <- at + n
        ctr <- ctr + 1u
