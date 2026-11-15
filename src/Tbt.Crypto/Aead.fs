/// ChaCha20-Poly1305 AEAD (RFC 8439 section 2.8) on the own ChaCha core.
/// Pass doubleRounds=4 for TBT-ChaCha8-Poly1305 (suite 0x03): same construction, 8 rounds.
/// Open never throws on a bad tag: it checks the tag in constant time before it decrypts,
/// so a forged frame costs one key-stream block and one MAC pass, no exception and no
/// allocation. One instance per key; single owner thread.
namespace Tbt.Crypto

open System
open System.Security.Cryptography

/// Poly1305 one-time authenticator (RFC 8439 section 2.5). The arithmetic is
/// the donna-64 limb core in Tbt.Kernels (UInt128 products, r²..r⁴); one
/// instance is reused, no allocation per Update/Finish.
[<Sealed>]
type Poly1305() =
    let mutable core = Kernels.Poly1305()
    member _.Init(key: ReadOnlySpan<byte>) = core.Init key
    member _.Update(data: ReadOnlySpan<byte>, padded: bool) = core.Update(data, padded)
    member _.UpdateWords(w0: uint64, w1: uint64) = core.UpdateWords(w0, w1)
    member _.Finish(tag: Span<byte>) = core.Finish tag

[<Sealed>]
type ChaCha20Poly1305Aead(key: ReadOnlySpan<byte>, ?doubleRounds: int, ?platform: bool) =
    let rounds = defaultArg doubleRounds 10
    let wantPlatform = defaultArg platform true
    let words = ChaCha20.keyWords key
    let mac = Poly1305()
    let polyKey: byte[] = Array.zeroCreate 32
    let expected: byte[] = Array.zeroCreate 16
    let scratch: uint32[] = Array.zeroCreate 64
    // Suite 0x02 (20 rounds): use the platform AEAD (OpenSSL on Linux) when it
    // is available. TryDecrypt stays on the managed path so a bad tag never
    // throws or allocates.
    let native: ChaCha20Poly1305 | null =
        if wantPlatform && rounds = 10 && ChaCha20Poly1305.IsSupported then new ChaCha20Poly1305(key) else null

    member private _.Authenticate(nonce: ReadOnlySpan<byte>, aad: ReadOnlySpan<byte>, cipher: ReadOnlySpan<byte>, tag: Span<byte>) =
        ChaCha20.blockRounds rounds words 0u nonce (Span polyKey)
        mac.Init(ReadOnlySpan polyKey)
        mac.Update(aad, true)
        mac.Update(cipher, true)
        mac.UpdateWords(uint64 aad.Length, uint64 cipher.Length)
        mac.Finish tag

    /// Clear the key words when the key retires.
    member _.Wipe() =
        Array.Clear words
        match native with
        | null -> ()
        | n -> n.Dispose()

    /// Encrypt plain into cipher (the same memory is allowed) and write the tag.
    member this.Encrypt(nonce: ReadOnlySpan<byte>, plain: ReadOnlySpan<byte>, cipher: Span<byte>, tag: Span<byte>, aad: ReadOnlySpan<byte>) =
        if nonce.Length <> 12 || tag.Length <> 16 || cipher.Length <> plain.Length then invalidArg "nonce" "bad nonce, tag or length"
        match native with
        | null ->
            ChaCha20.xorStreamRounds rounds words 1u nonce plain cipher (Span scratch)
            this.Authenticate(nonce, aad, Span<byte>.op_Implicit cipher, tag)
        | n -> n.Encrypt(nonce, plain, cipher, tag, aad)

    /// Check the tag in constant time; when it matches, decrypt cipher into plain (the same
    /// memory is allowed) and return true. On false plain is not written.
    member this.TryDecrypt(nonce: ReadOnlySpan<byte>, cipher: ReadOnlySpan<byte>, tag: ReadOnlySpan<byte>, plain: Span<byte>, aad: ReadOnlySpan<byte>) =
        if nonce.Length <> 12 || tag.Length <> 16 || cipher.Length <> plain.Length then false
        else
            this.Authenticate(nonce, aad, cipher, Span expected)
            if CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, tag) then
                ChaCha20.xorStreamRounds rounds words 1u nonce cipher plain (Span scratch)
                true
            else false
