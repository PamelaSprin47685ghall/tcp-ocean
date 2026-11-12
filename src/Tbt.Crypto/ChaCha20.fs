/// ChaCha20 block function (RFC 8439 section 2.3). Used for header protection only;
/// the AEAD itself is the platform ChaCha20-Poly1305. No allocation per block.
module Tbt.Crypto.ChaCha20

open System
open System.Buffers.Binary
open System.Numerics

let inline private qr (a: byref<uint32>) (b: byref<uint32>) (c: byref<uint32>) (d: byref<uint32>) =
    a <- a + b
    d <- BitOperations.RotateLeft(d ^^^ a, 16)
    c <- c + d
    b <- BitOperations.RotateLeft(b ^^^ c, 12)
    a <- a + b
    d <- BitOperations.RotateLeft(d ^^^ a, 8)
    c <- c + d
    b <- BitOperations.RotateLeft(b ^^^ c, 7)

let private put (output: Span<byte>) (i: int) (w: uint32) =
    let at = 4 * i
    if at < output.Length then
        for j in 0 .. min 4 (output.Length - at) - 1 do
            output.[at + j] <- byte (w >>> (8 * j))

/// The 8 little-endian words of a 32-byte key.
let keyWords (key: ReadOnlySpan<byte>) =
    if key.Length <> 32 then invalidArg "key" "ChaCha20 key is 32 bytes"
    let w = Array.zeroCreate<uint32> 8
    for i in 0 .. 7 do
        w.[i] <- BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4 * i))
    w

/// Write the first output.Length (at most 64) key stream bytes of one block.
let block (key: uint32[]) (counter: uint32) (nonce: ReadOnlySpan<byte>) (output: Span<byte>) =
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
    put output 0 (x0 + 0x61707865u)
    put output 1 (x1 + 0x3320646eu)
    put output 2 (x2 + 0x79622d32u)
    put output 3 (x3 + 0x6b206574u)
    put output 4 (x4 + key.[0])
    put output 5 (x5 + key.[1])
    put output 6 (x6 + key.[2])
    put output 7 (x7 + key.[3])
    put output 8 (x8 + key.[4])
    put output 9 (x9 + key.[5])
    put output 10 (x10 + key.[6])
    put output 11 (x11 + key.[7])
    put output 12 (x12 + counter)
    put output 13 (x13 + n0)
    put output 14 (x14 + n1)
    put output 15 (x15 + n2)
