/// AEGIS-128L AEAD wrapper (RFC 10032). Suite 0x01; 16-byte key, 16-byte nonce,
/// 16-byte tag. Hardware AES only; construction throws if it is missing.
namespace Tbt.Crypto

open System
open Tbt.Crypto.Kernels

[<Sealed>]
type Aegis128LAead(key: ReadOnlySpan<byte>) =
    let core = Aegis128L(key)

    static member IsSupported = Aegis128L.IsSupported

    member _.Wipe() = core.Wipe()

    member _.Encrypt(nonce: ReadOnlySpan<byte>, plain: ReadOnlySpan<byte>, cipher: Span<byte>, tag: Span<byte>, aad: ReadOnlySpan<byte>) =
        core.Encrypt(nonce, plain, cipher, tag, aad)

    member _.TryDecrypt(nonce: ReadOnlySpan<byte>, cipher: ReadOnlySpan<byte>, tag: ReadOnlySpan<byte>, plain: Span<byte>, aad: ReadOnlySpan<byte>) =
        core.TryDecrypt(nonce, cipher, tag, plain, aad)

    interface IAead with
        member this.Encrypt(n, p, c, t, a) = this.Encrypt(n, p, c, t, a)
        member this.TryDecrypt(n, c, t, p, a) = this.TryDecrypt(n, c, t, p, a)
        member this.Wipe() = this.Wipe()
