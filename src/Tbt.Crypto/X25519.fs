/// X25519 (RFC 7748 sections 5 and 6.1). Handshake only: IKM = PSK || shared
/// when caps.bit1 is negotiated (spec 6.5.8). Follows the RFC ladder; the
/// arithmetic uses BigInteger and is not offered as a constant-time primitive.
module Tbt.Crypto.X25519

open System
open System.Numerics

[<Literal>]
let KeyLength = 32

let BasePoint: byte[] = Array.init KeyLength (fun i -> if i = 0 then 9uy else 0uy)

let private P = (1I <<< 255) - 19I
let private A24 = 121665I

let private modulus (x: bigint) =
    let r = x % P
    if r < 0I then r + P else r

let private decodeLe (b: ReadOnlySpan<byte>) (bits: int) =
    let n = (bits + 7) / 8
    let mutable v = 0I
    for i = n - 1 downto 0 do
        v <- (v <<< 8) + bigint (int b.[i])
    if bits % 8 <> 0 then v <- v &&& ((1I <<< bits) - 1I)
    v

let private encodeLe (u: bigint) (dst: Span<byte>) =
    dst.Clear()
    let bytes = (modulus u).ToByteArray(isUnsigned = true, isBigEndian = false)
    let n = min bytes.Length dst.Length
    if n > 0 then Span(bytes, 0, n).CopyTo(dst.Slice(0, n))
    dst.[31] <- dst.[31] &&& 127uy

let private decodeU (u: ReadOnlySpan<byte>) =
    let tmp = u.ToArray()
    tmp.[31] <- tmp.[31] &&& 127uy
    decodeLe (ReadOnlySpan tmp) 255

let private decodeScalar (k: ReadOnlySpan<byte>) =
    let tmp = k.ToArray()
    tmp.[0] <- tmp.[0] &&& 248uy
    tmp.[31] <- (tmp.[31] &&& 127uy) ||| 64uy
    decodeLe (ReadOnlySpan tmp) 255

let private bit (k: bigint) (t: int) = int ((k >>> t) &&& 1I)

let isZero (x: ReadOnlySpan<byte>) =
    let mutable acc = 0uy
    for i in 0 .. x.Length - 1 do acc <- acc ||| x.[i]
    acc = 0uy

/// X25519(k, u) into output. False when the shared u-coordinate is all zeros
/// (a low-order input; RFC 7748 section 6.1 says the check MAY abort).
let compute (scalar: ReadOnlySpan<byte>) (u: ReadOnlySpan<byte>) (output: Span<byte>) =
    if scalar.Length <> KeyLength || u.Length <> KeyLength || output.Length <> KeyLength then
        invalidArg "scalar" "X25519 inputs and output are 32 bytes"
    let k = decodeScalar scalar
    let x1 = decodeU u
    let mutable x2 = 1I
    let mutable z2 = 0I
    let mutable x3 = x1
    let mutable z3 = 1I
    let mutable swap = 0
    for t = 254 downto 0 do
        let kt = bit k t
        swap <- swap ^^^ kt
        if swap <> 0 then
            let tx, tz = x2, z2
            x2 <- x3
            z2 <- z3
            x3 <- tx
            z3 <- tz
        swap <- kt
        let a = modulus (x2 + z2)
        let aa = modulus (a * a)
        let b = modulus (x2 - z2)
        let bb = modulus (b * b)
        let e = modulus (aa - bb)
        let c = modulus (x3 + z3)
        let d = modulus (x3 - z3)
        let da = modulus (d * a)
        let cb = modulus (c * b)
        x3 <- modulus ((da + cb) * (da + cb))
        z3 <- modulus (x1 * modulus ((da - cb) * (da - cb)))
        x2 <- modulus (aa * bb)
        z2 <- modulus (e * (aa + A24 * e))
    if swap <> 0 then
        let tx, tz = x2, z2
        x2 <- x3
        z2 <- z3
        x3 <- tx
        z3 <- tz
    let inv = BigInteger.ModPow(z2, P - 2I, P)
    encodeLe (modulus (x2 * inv)) output
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
