/// HKDF-SHA256 (RFC 5869) and HMAC-SHA256 helpers. Strings are ASCII without terminator.
module Tbt.Crypto.Kdf

open System
open System.Security.Cryptography
open System.Text

let ascii (s: string) = Encoding.ASCII.GetBytes s

let hmac (key: ReadOnlySpan<byte>) (data: ReadOnlySpan<byte>) =
    let out = Array.zeroCreate<byte> 32
    HMACSHA256.HashData(key, data, Span out) |> ignore
    out

let extract (salt: ReadOnlySpan<byte>) (ikm: ReadOnlySpan<byte>) =
    let prk = Array.zeroCreate<byte> 32
    HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt, Span prk) |> ignore
    prk

let expand (prk: ReadOnlySpan<byte>) (info: ReadOnlySpan<byte>) (length: int) =
    let out = Array.zeroCreate<byte> length
    HKDF.Expand(HashAlgorithmName.SHA256, prk, Span out, info)
    out

/// HKDF-SHA256 Extract then Expand to 32 bytes.
let derive32 (salt: ReadOnlySpan<byte>) (ikm: ReadOnlySpan<byte>) (info: ReadOnlySpan<byte>) =
    expand (ReadOnlySpan(extract salt ikm)) info 32

let sha256 (data: ReadOnlySpan<byte>) = SHA256.HashData data
