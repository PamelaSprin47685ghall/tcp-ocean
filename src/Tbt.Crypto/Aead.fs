/// ChaCha20-Poly1305 AEAD (RFC 8439 section 2.8) on the own ChaCha core.
/// Pass doubleRounds=4 for TBT-ChaCha8-Poly1305 (suite 0x03): same construction, 8 rounds.
/// Open never throws on a bad tag: it checks the tag in constant time before it decrypts,
/// so a forged frame costs one key-stream block and one MAC pass, no exception and no
/// allocation. One instance per key; single owner thread.
namespace Tbt.Crypto

open System
open System.Security.Cryptography

/// Shared AEAD surface for the key table (ChaCha suites and AEGIS-128L).
type IAead =
    abstract Encrypt: nonce: ReadOnlySpan<byte> * plain: ReadOnlySpan<byte> * cipher: Span<byte> * tag: Span<byte> * aad: ReadOnlySpan<byte> -> unit
    abstract TryDecrypt: nonce: ReadOnlySpan<byte> * cipher: ReadOnlySpan<byte> * tag: ReadOnlySpan<byte> * plain: Span<byte> * aad: ReadOnlySpan<byte> -> bool
    abstract Wipe: unit -> unit

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
    // The platform Decrypt refuses overlapping buffers. In-place opens decrypt
    // into this buffer and copy only after the tag matches. A mismatch throws
    // before that API writes, so the caller's buffer stays unchanged.
    let mutable decryptScratch: byte[] = Array.zeroCreate 2048

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
        CryptographicOperations.ZeroMemory(Span decryptScratch)
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

    member private _.EnsureScratch(n: int) =
        if decryptScratch.Length < n then
            CryptographicOperations.ZeroMemory(Span decryptScratch)
            decryptScratch <- Array.zeroCreate n

    /// Check the tag; when it matches, decrypt cipher into plain (the same memory is
    /// allowed) and return true. On false plain is not written.
    member this.TryDecrypt(nonce: ReadOnlySpan<byte>, cipher: ReadOnlySpan<byte>, tag: ReadOnlySpan<byte>, plain: Span<byte>, aad: ReadOnlySpan<byte>) =
        if nonce.Length <> 12 || tag.Length <> 16 || cipher.Length <> plain.Length then false
        else
            match native with
            | null ->
                this.Authenticate(nonce, aad, cipher, Span expected)
                if CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, tag) then
                    ChaCha20.xorStreamRounds rounds words 1u nonce cipher plain (Span scratch)
                    true
                else false
            | n ->
                // Reject first on the allocation-free managed MAC. A bad frame never reaches
                // the platform API, whose mismatch exception allocates. The platform call only
                // decrypts an already-accepted tag.
                this.Authenticate(nonce, aad, cipher, Span expected)
                if not (CryptographicOperations.FixedTimeEquals(ReadOnlySpan expected, tag)) then false
                else
                    try
                        this.EnsureScratch cipher.Length
                        let dst = Span<byte>(decryptScratch, 0, cipher.Length)
                        n.Decrypt(nonce, cipher, tag, dst, aad)
                        dst.CopyTo plain
                        true
                    with :? AuthenticationTagMismatchException -> false

    interface IAead with
        member this.Encrypt(n, p, c, tg, a) = this.Encrypt(n, p, c, tg, a)
        member this.TryDecrypt(n, c, tg, p, a) = this.TryDecrypt(n, c, tg, p, a)
        member this.Wipe() = this.Wipe()
