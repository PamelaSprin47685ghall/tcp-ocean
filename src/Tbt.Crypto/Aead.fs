/// ChaCha20-Poly1305 AEAD (RFC 8439 section 2.8) on the own ChaCha20 core.
/// Open never throws on a bad tag: it checks the tag in constant time before it decrypts,
/// so a forged frame costs one key-stream block and one MAC pass, no exception and no
/// allocation. One instance per key; single owner thread.
namespace Tbt.Crypto

open System
open System.Buffers.Binary
open System.Security.Cryptography

module private Wide =
    /// (hi, lo) + a * b as a 128-bit pair.
    let inline mulAdd (hi: uint64) (lo: uint64) (a: uint64) (b: uint64) =
        let mutable t = 0UL
        let h = Math.BigMul(a, b, &t)
        let l = lo + t
        struct (hi + h + (if l < t then 1UL else 0UL), l)

/// Poly1305 one-time authenticator (RFC 8439 section 2.5), 44/44/42-bit limbs.
[<Sealed>]
type Poly1305() =
    static let m44 = 0xfffffffffffUL
    static let m42 = 0x3ffffffffffUL
    let mutable r0 = 0UL
    let mutable r1 = 0UL
    let mutable r2 = 0UL
    let mutable h0 = 0UL
    let mutable h1 = 0UL
    let mutable h2 = 0UL
    let mutable pad0 = 0UL
    let mutable pad1 = 0UL

    /// Start a MAC with the 32-byte one-time key.
    member _.Init(key: ReadOnlySpan<byte>) =
        if key.Length <> 32 then invalidArg "key" "Poly1305 key is 32 bytes"
        let t0 = BinaryPrimitives.ReadUInt64LittleEndian key
        let t1 = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice 8)
        r0 <- t0 &&& 0xffc0fffffffUL
        r1 <- ((t0 >>> 44) ||| (t1 <<< 20)) &&& 0xfffffc0ffffUL
        r2 <- (t1 >>> 24) &&& 0x00ffffffc0fUL
        h0 <- 0UL
        h1 <- 0UL
        h2 <- 0UL
        pad0 <- BinaryPrimitives.ReadUInt64LittleEndian(key.Slice 16)
        pad1 <- BinaryPrimitives.ReadUInt64LittleEndian(key.Slice 24)

    member inline private _.Block(t0: uint64, t1: uint64, hibit: uint64) =
        h0 <- h0 + (t0 &&& m44)
        h1 <- h1 + (((t0 >>> 44) ||| (t1 <<< 20)) &&& m44)
        h2 <- h2 + ((((t1 >>> 24)) &&& m42) ||| hibit)
        let s1 = r1 * 20UL
        let s2 = r2 * 20UL
        // Each product is below 2^90; sums of three stay below 2^92. Keep (hi, lo) pairs.
        let struct (d0h, l0) = Wide.mulAdd 0UL 0UL h0 r0
        let struct (d0h, l0) = Wide.mulAdd d0h l0 h1 s2
        let struct (d0h, l0) = Wide.mulAdd d0h l0 h2 s1
        let struct (d1h, l1) = Wide.mulAdd 0UL 0UL h0 r1
        let struct (d1h, l1) = Wide.mulAdd d1h l1 h1 r0
        let struct (d1h, l1) = Wide.mulAdd d1h l1 h2 s2
        let struct (d2h, l2) = Wide.mulAdd 0UL 0UL h0 r2
        let struct (d2h, l2) = Wide.mulAdd d2h l2 h1 r1
        let struct (d2h, l2) = Wide.mulAdd d2h l2 h2 r0
        let c = (l0 >>> 44) ||| (d0h <<< 20)
        h0 <- l0 &&& m44
        let l1c = l1 + c
        let d1h = d1h + (if l1c < c then 1UL else 0UL)
        let c = (l1c >>> 44) ||| (d1h <<< 20)
        h1 <- l1c &&& m44
        let l2c = l2 + c
        let d2h = d2h + (if l2c < c then 1UL else 0UL)
        let c = (l2c >>> 42) ||| (d2h <<< 22)
        h2 <- l2c &&& m42
        h0 <- h0 + c * 5UL
        let c = h0 >>> 44
        h0 <- h0 &&& m44
        h1 <- h1 + c

    /// Absorb data. Every 16-byte chunk is one block; a short last chunk is zero padded to a
    /// full block when padded (AEAD rule), else it gets the 0x01 end marker (plain MAC rule).
    member this.Update(data: ReadOnlySpan<byte>, padded: bool) =
        let mutable d = data
        while d.Length >= 16 do
            this.Block(BinaryPrimitives.ReadUInt64LittleEndian d, BinaryPrimitives.ReadUInt64LittleEndian(d.Slice 8), 1UL <<< 40)
            d <- d.Slice 16
        if not d.IsEmpty then
            let mutable t0 = 0UL
            let mutable t1 = 0UL
            for i in 0 .. d.Length - 1 do
                if i < 8 then t0 <- t0 ||| (uint64 d.[i] <<< (8 * i))
                else t1 <- t1 ||| (uint64 d.[i] <<< (8 * (i - 8)))
            if padded then this.Block(t0, t1, 1UL <<< 40)
            else
                let n = d.Length
                if n < 8 then t0 <- t0 ||| (1UL <<< (8 * n)) else t1 <- t1 ||| (1UL <<< (8 * (n - 8)))
                this.Block(t0, t1, 0UL)

    /// Absorb one block made of two little-endian 64-bit words.
    member this.UpdateWords(w0: uint64, w1: uint64) = this.Block(w0, w1, 1UL <<< 40)

    /// Write the 16-byte tag.
    member _.Finish(tag: Span<byte>) =
        let mutable c = h1 >>> 44
        h1 <- h1 &&& m44
        h2 <- h2 + c
        c <- h2 >>> 42
        h2 <- h2 &&& m42
        h0 <- h0 + c * 5UL
        c <- h0 >>> 44
        h0 <- h0 &&& m44
        h1 <- h1 + c
        c <- h1 >>> 44
        h1 <- h1 &&& m44
        h2 <- h2 + c
        c <- h2 >>> 42
        h2 <- h2 &&& m42
        h0 <- h0 + c * 5UL
        c <- h0 >>> 44
        h0 <- h0 &&& m44
        h1 <- h1 + c
        // h + -p, then select h or h - p without a branch.
        let mutable g0 = h0 + 5UL
        c <- g0 >>> 44
        g0 <- g0 &&& m44
        let mutable g1 = h1 + c
        c <- g1 >>> 44
        g1 <- g1 &&& m44
        let mutable g2 = h2 + c - (1UL <<< 42)
        let keep = (g2 >>> 63) - 1UL
        g0 <- g0 &&& keep
        g1 <- g1 &&& keep
        g2 <- g2 &&& keep
        let drop = ~~~keep
        h0 <- (h0 &&& drop) ||| g0
        h1 <- (h1 &&& drop) ||| g1
        h2 <- (h2 &&& drop) ||| g2
        h0 <- h0 + (pad0 &&& m44)
        c <- h0 >>> 44
        h0 <- h0 &&& m44
        h1 <- h1 + ((((pad0 >>> 44) ||| (pad1 <<< 20))) &&& m44) + c
        c <- h1 >>> 44
        h1 <- h1 &&& m44
        h2 <- (h2 + ((pad1 >>> 24) &&& m42) + c) &&& m42
        BinaryPrimitives.WriteUInt64LittleEndian(tag, h0 ||| (h1 <<< 44))
        BinaryPrimitives.WriteUInt64LittleEndian(tag.Slice 8, (h1 >>> 20) ||| (h2 <<< 24))

[<Sealed>]
type ChaCha20Poly1305Aead(key: ReadOnlySpan<byte>) =
    let words = ChaCha20.keyWords key
    let mac = Poly1305()
    let polyKey: byte[] = Array.zeroCreate 32
    let expected: byte[] = Array.zeroCreate 16
    let scratch: uint32[] = Array.zeroCreate 64

    member private _.Authenticate(nonce: ReadOnlySpan<byte>, aad: ReadOnlySpan<byte>, cipher: ReadOnlySpan<byte>, tag: Span<byte>) =
        ChaCha20.block words 0u nonce (Span polyKey)
        mac.Init(ReadOnlySpan polyKey)
        mac.Update(aad, true)
        mac.Update(cipher, true)
        mac.UpdateWords(uint64 aad.Length, uint64 cipher.Length)
        mac.Finish tag

    /// Clear the key words when the key retires.
    member _.Wipe() = Array.Clear words

    /// Encrypt plain into cipher (the same memory is allowed) and write the tag.
    member this.Encrypt(nonce: ReadOnlySpan<byte>, plain: ReadOnlySpan<byte>, cipher: Span<byte>, tag: Span<byte>, aad: ReadOnlySpan<byte>) =
        if nonce.Length <> 12 || tag.Length <> 16 || cipher.Length <> plain.Length then invalidArg "nonce" "bad nonce, tag or length"
        ChaCha20.xorStream words 1u nonce plain cipher (Span scratch)
        this.Authenticate(nonce, aad, Span<byte>.op_Implicit cipher, tag)

    /// Check the tag in constant time; when it matches, decrypt cipher into plain (the same
    /// memory is allowed) and return true. On false plain is not written.
    member this.TryDecrypt(nonce: ReadOnlySpan<byte>, cipher: ReadOnlySpan<byte>, tag: ReadOnlySpan<byte>, plain: Span<byte>, aad: ReadOnlySpan<byte>) =
        if nonce.Length <> 12 || tag.Length <> 16 || cipher.Length <> plain.Length then false
        else
            this.Authenticate(nonce, aad, cipher, Span expected)
            if CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, tag) then
                ChaCha20.xorStream words 1u nonce cipher plain (Span scratch)
                true
            else false
