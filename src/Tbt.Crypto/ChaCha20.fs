/// ChaCha20 (RFC 8439 sections 2.3 and 2.4): the block function for header protection and
/// the key stream for the AEAD. No allocation per call. The core lives in Tbt.Kernels
/// (AVX2 8-block, Vector128 4-block / 1-block, scalar tail) and is shared by the 20-round
/// and 8-round suites. Suite 0x02 is also cross-checked against the platform
/// System.Security.Cryptography.ChaCha20Poly1305 (OpenSSL on Linux).
module Tbt.Crypto.ChaCha20

open System
open System.Buffers.Binary

/// The 8 little-endian words of a 32-byte key.
let keyWords (key: ReadOnlySpan<byte>) =
    if key.Length <> 32 then invalidArg "key" "ChaCha20 key is 32 bytes"
    let w = Array.zeroCreate<uint32> 8
    for i in 0 .. 7 do
        w.[i] <- BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4 * i))
    w

/// Write the first output.Length (at most 64) key stream bytes of one 20-round block.
let block key counter nonce output = Kernels.ChaCha.Block(10, key, counter, nonce, output)

/// Same as block, with an explicit double-round count (10 = ChaCha20, 4 = ChaCha8).
let blockRounds (doubleRounds: int) key counter nonce output =
    Kernels.ChaCha.Block(doubleRounds, key, counter, nonce, output)

/// XOR src with a ChaCha key stream of the given double-round count into dst.
/// dst may be the same memory as src. scratch is 64 words owned by the caller
/// (kept for the API; the SIMD core does not use it).
let xorStreamRounds (doubleRounds: int) (key: uint32[]) (counter: uint32) (nonce: ReadOnlySpan<byte>) (src: ReadOnlySpan<byte>) (dst: Span<byte>) (scratch: Span<uint32>) =
    if doubleRounds < 1 || key.Length <> 8 || nonce.Length <> 12 || dst.Length <> src.Length || scratch.Length < 64 then
        invalidArg "dst" "bad key, nonce, length or scratch"
    Kernels.ChaCha.XorStream(doubleRounds, key, counter, nonce, src, dst)

/// RFC 8439 section 2.4: 20-round ChaCha.
let xorStream key counter nonce src dst scratch =
    xorStreamRounds 10 key counter nonce src dst scratch
