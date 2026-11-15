/// Bootstrap frames of the handshake (spec 6.5.2, 6.5.5, 6.5.7). Cold path: these run once
/// per session or carrier, so they may allocate. All bodies are frozen once rendered.
module Tbt.Crypto.Bootstrap

open System
open System.Buffers.Binary
open System.Security.Cryptography
open Tbt.Contracts

let private aeadNonce: byte[] = Array.zeroCreate 12

/// "438\r\n" (0x438 = 1080).
let Header: byte[] = Kdf.ascii "438\r\n"

let Trailer: byte[] = Kdf.ascii "\r\n"

/// Wrap 1080 content bytes into the 1087-byte bootstrap frame.
let frame (content: ReadOnlySpan<byte>) =
    if content.Length <> Layout.BootBodyLength then invalidArg "content" "bootstrap content is 1080 bytes"
    let f = Array.zeroCreate<byte> Layout.BootFrameLength
    Header.CopyTo(f, 0)
    content.CopyTo(Span(f, 5, Layout.BootBodyLength))
    Trailer.CopyTo(f, 5 + Layout.BootBodyLength)
    f

/// Content of a bootstrap frame, or empty when the wrapper bytes are wrong.
let content (frame: ReadOnlySpan<byte>) =
    if frame.Length <> Layout.BootFrameLength || not (frame.Slice(0, 5).SequenceEqual(ReadOnlySpan Header))
       || not (frame.Slice(1085).SequenceEqual(ReadOnlySpan Trailer)) then
        ReadOnlySpan<byte>.Empty
    else frame.Slice(5, Layout.BootBodyLength)

let private seal (key: byte[]) (plain: ReadOnlySpan<byte>) (aad: ReadOnlySpan<byte>) (cipher: Span<byte>) (tag: Span<byte>) =
    ChaCha20Poly1305Aead(ReadOnlySpan key).Encrypt(ReadOnlySpan aeadNonce, plain, cipher, tag, aad)

let private tryOpen (key: byte[]) (cipher: ReadOnlySpan<byte>) (tag: ReadOnlySpan<byte>) (aad: ReadOnlySpan<byte>) =
    let plain = Array.zeroCreate<byte> cipher.Length
    if ChaCha20Poly1305Aead(ReadOnlySpan key).TryDecrypt(ReadOnlySpan aeadNonce, cipher, tag, Span plain, aad) then ValueSome plain
    else ValueNone

/// Capability bits (spec 3.16).
module Caps =
    [<Literal>]
    let HpMask = 0x0001us

    [<Literal>]
    let X25519 = 0x0002us

    [<Literal>]
    let StraddleOk = 0x0004us

    [<Literal>]
    let Lane = 0x0008us

    [<Literal>]
    let MemTierMask = 0x0030us

    [<Literal>]
    let UserRx = 0x0040us

    [<Literal>]
    let Dl16 = 0x0080us

    [<Literal>]
    let FlowState = 0x0100us

    [<Literal>]
    let FlowState2 = 0x0200us

    [<Literal>]
    let JoinAck2 = 0x0400us

    [<Literal>]
    let Reserved = 0xF800us

/// Suite numbers and the suite bitmap of CLIENT_INIT (spec 6.4.1).
/// suites bit2 (0x04) announces 0x03; the spec reserved the number but did not
/// assign a bitmap bit (see docs/DECISIONS.md D11).
module Suite =
    /// AEGIS-128L (RFC 10032). 128-bit key, 128-bit nonce, 128-bit tag (spec 6.4.1).
    [<Literal>]
    let Aegis128L = 0x01uy

    /// CLIENT_INIT bitmap bit0 for suite 0x01.
    [<Literal>]
    let Aegis128LBit = 0x01uy

    [<Literal>]
    let ChaCha20Poly1305 = 0x02uy

    /// Bitmap bit for suite 0x02.
    [<Literal>]
    let ChaCha20Poly1305Bit = 0x02uy

    /// TBT-ChaCha8-Poly1305: RFC 8439 section 2.8 with 8 ChaCha rounds (4 double-rounds).
    [<Literal>]
    let ChaCha8Poly1305 = 0x03uy

    /// CLIENT_INIT bitmap bit2 for suite 0x03.
    [<Literal>]
    let ChaCha8Poly1305Bit = 0x04uy

    /// True when AES-NI or ARMv8 Crypto is present. AEGIS is never advertised without it.
    let hardwareAes = Kernels.Aegis128L.IsSupported

    let isImplemented (s: byte) =
        s = ChaCha20Poly1305 || s = ChaCha8Poly1305 || (s = Aegis128L && hardwareAes)

    let offers (bitmap: byte) (s: byte) =
        if s = Aegis128L then bitmap &&& Aegis128LBit <> 0uy
        elif s = ChaCha20Poly1305 then bitmap &&& ChaCha20Poly1305Bit <> 0uy
        elif s = ChaCha8Poly1305 then bitmap &&& ChaCha8Poly1305Bit <> 0uy
        else false

    let name (s: byte) =
        if s = Aegis128L then "aegis"
        elif s = ChaCha20Poly1305 then "chacha20"
        elif s = ChaCha8Poly1305 then "chacha8"
        else "0x" + s.ToString("x2")

    let tryParseName (text: string) : ValueOption<byte> =
        match text.Trim().ToLowerInvariant() with
        | "aegis" | "aegis-128l" | "0x01" | "1" -> ValueSome Aegis128L
        | "chacha20" | "chacha20-poly1305" | "0x02" | "2" -> ValueSome ChaCha20Poly1305
        | "chacha8" | "chacha8-poly1305" | "0x03" | "3" -> ValueSome ChaCha8Poly1305
        | _ -> ValueNone

    /// Parse a comma-separated offer list (CLI --suites). Empty is the default: 0x02 only.
    /// Returns offerAegis, offerChaCha8, offerChaCha20. Unknown names fail.
    let parseOfferList (text: string) =
        let t = text.Trim()
        if t.Length = 0 then struct (false, false, true)
        else
            let mutable aegis, chacha8, chacha20 = false, false, false
            for part in t.Split ',' do
                let p = part.Trim()
                if p.Length > 0 then
                    match tryParseName p with
                    | ValueSome s when s = Aegis128L -> aegis <- true
                    | ValueSome s when s = ChaCha8Poly1305 -> chacha8 <- true
                    | ValueSome s when s = ChaCha20Poly1305 -> chacha20 <- true
                    | _ -> invalidArg "suites" ("unknown suite: " + p)
            if not aegis && not chacha8 && not chacha20 then
                invalidArg "suites" "at least one suite is required"
            struct (aegis, chacha8, chacha20)

    /// Advertise 0x02 by default; 0x03 when offerChaCha8 is set; 0x01 only when
    /// offerAegis is set and hardware AES is present. Pass offerChaCha20=false
    /// to omit 0x02. Pass true for the historical default that always offers 0x02.
    let advertisedBits (offerChaCha8: bool) (offerAegis: bool) (offerChaCha20: bool) =
        let mutable b = 0uy
        if offerChaCha20 then b <- b ||| ChaCha20Poly1305Bit
        if offerChaCha8 then b <- b ||| ChaCha8Poly1305Bit
        if offerAegis && hardwareAes then b <- b ||| Aegis128LBit
        b

    /// Prefer 0x01 when both offered it and hardware AES is present, then 0x03
    /// if preferChaCha8, else 0x02 when the server still offers it (default);
    /// 0 if the intersection is empty (see D12).
    let select (bitmap: byte) (preferAegis: bool) (preferChaCha8: bool) (offerChaCha20: bool) =
        if preferAegis && hardwareAes && offers bitmap Aegis128L then Aegis128L
        elif preferChaCha8 && offers bitmap ChaCha8Poly1305 then ChaCha8Poly1305
        elif offerChaCha20 && offers bitmap ChaCha20Poly1305 then ChaCha20Poly1305
        else 0uy

    let keyLength (s: byte) = if s = Aegis128L then 16 else 32

    let nonceLength (s: byte) = if s = Aegis128L then 16 else 12

    let doubleRounds (s: byte) = if s = ChaCha8Poly1305 then 4 else 10

/// Fields of the CLIENT_INIT encrypted body (spec 6.5.2). Integers are little endian.
[<Struct>]
type ClientHello =
    { Version: byte
      Suites: byte
      Caps: uint16
      ClientId: Guid
      UnixTimeMs: uint64
      MssClient: uint16
      MaxPaths: uint16 }

/// Fields of the SERVER_INIT encrypted body. Integers are little endian.
[<Struct>]
type ServerHello =
    { Status: byte
      Suite: byte
      Caps: uint16
      SessionId: Guid
      Symbol: uint16
      PathId: uint16
      MaxPaths: uint16
      RxWinGrant0: uint16
      EchoTimeMs: uint64
      Cookie: UInt128 }

module ServerStatus =
    [<Literal>]
    let Ok = 0uy

    [<Literal>]
    let Retry = 1uy

    [<Literal>]
    let Reject = 2uy

let private guidBytes (g: Guid) (dst: Span<byte>) = g.TryWriteBytes(dst, true) |> ignore
let private readGuid (src: ReadOnlySpan<byte>) = Guid(src.Slice(0, 16), true)

/// key_hint = HMAC-SHA256(PSK, "hint" | client_nonce)[0..8].
let keyHint (psk: ReadOnlySpan<byte>) (clientNonce: ReadOnlySpan<byte>) =
    (Kdf.hmac psk (ReadOnlySpan(Array.append (Kdf.ascii "hint") (clientNonce.ToArray())))).[.. 7]

let private initClientKey (psk: ReadOnlySpan<byte>) (clientNonce: ReadOnlySpan<byte>) =
    Kdf.derive32 clientNonce psk (ReadOnlySpan(Kdf.ascii "tbtun v2 init c"))

let private initServerKey (psk: ReadOnlySpan<byte>) (clientNonce: ReadOnlySpan<byte>) (serverNonce: ReadOnlySpan<byte>) =
    let salt = Array.append (clientNonce.ToArray()) (serverNonce.ToArray())
    Kdf.derive32 (ReadOnlySpan salt) psk (ReadOnlySpan(Kdf.ascii "tbtun v2 init s"))

/// CLIENT_INIT content: client_nonce 32 | key_hint 8 | cookie 16 | body 1008 | tag 16.
module ClientInit =
    [<Literal>]
    let BodyLength = 1008

    /// Render the content once. padding fills body bytes 64..1007 (944 bytes, from the CSPRNG).
    let build (psk: ReadOnlySpan<byte>) (clientNonce: ReadOnlySpan<byte>) (hello: ClientHello)
              (x25519Pub: ReadOnlySpan<byte>) (padding: ReadOnlySpan<byte>) =
        if clientNonce.Length <> 32 || padding.Length <> BodyLength - 64 then invalidArg "padding" "bad length"
        let body = Array.zeroCreate<byte> BodyLength
        let b = Span body
        b.[0] <- hello.Version
        b.[1] <- hello.Suites
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 2, hello.Caps)
        guidBytes hello.ClientId (b.Slice 4)
        BinaryPrimitives.WriteUInt64LittleEndian(b.Slice 20, hello.UnixTimeMs)
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 28, hello.MssClient)
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 30, hello.MaxPaths)
        if not x25519Pub.IsEmpty then x25519Pub.CopyTo(b.Slice(32, 32))
        padding.CopyTo(b.Slice 64)
        let c = Array.zeroCreate<byte> Layout.BootBodyLength
        let cs = Span c
        clientNonce.CopyTo cs
        (keyHint psk clientNonce).CopyTo(cs.Slice 32)
        seal (initClientKey psk clientNonce) (ReadOnlySpan body) ReadOnlySpan<byte>.Empty (cs.Slice(56, BodyLength)) (cs.Slice(1064, 16))
        c

    /// Same frozen content with the clear cookie field set (spec 6.5.2: retry keeps the
    /// ciphertext and tag).
    let withCookie (content: ReadOnlySpan<byte>) (cookie: ReadOnlySpan<byte>) =
        let c = content.ToArray()
        cookie.CopyTo(Span(c, 40, 16))
        c

    let clientNonce (content: ReadOnlySpan<byte>) = content.Slice(0, 32)
    let hint (content: ReadOnlySpan<byte>) = content.Slice(32, 8)
    let cookie (content: ReadOnlySpan<byte>) = content.Slice(40, 16)

    /// Open the body with one PSK. ValueNone on a tag failure or a bad version.
    let tryOpen (psk: ReadOnlySpan<byte>) (content: ReadOnlySpan<byte>) =
        let cn = clientNonce content
        match tryOpen (initClientKey psk cn) (content.Slice(56, BodyLength)) (content.Slice(1064, 16)) ReadOnlySpan<byte>.Empty with
        | ValueNone -> ValueNone
        | ValueSome body ->
            let b = ReadOnlySpan body
            if b.[0] <> 2uy then ValueNone
            else
                ValueSome(
                    { Version = b.[0]
                      Suites = b.[1]
                      Caps = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 2)
                      ClientId = readGuid (b.Slice 4)
                      UnixTimeMs = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice 20)
                      MssClient = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 28)
                      MaxPaths = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 30) },
                    body.[32 .. 63])

/// SERVER_INIT content: server_nonce 32 | body 1032 | tag 16.
module ServerInit =
    [<Literal>]
    let BodyLength = 1032

    /// Render once. padding fills body bytes 84..1031 (948 bytes).
    let build (psk: ReadOnlySpan<byte>) (clientNonce: ReadOnlySpan<byte>) (serverNonce: ReadOnlySpan<byte>)
              (hello: ServerHello) (x25519Pub: ReadOnlySpan<byte>) (padding: ReadOnlySpan<byte>) =
        if serverNonce.Length <> 32 || padding.Length <> BodyLength - 84 then invalidArg "padding" "bad length"
        let body = Array.zeroCreate<byte> BodyLength
        let b = Span body
        b.[0] <- hello.Status
        b.[1] <- hello.Suite
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 2, hello.Caps)
        guidBytes hello.SessionId (b.Slice 4)
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 20, hello.Symbol)
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 22, hello.PathId)
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 24, hello.MaxPaths)
        BinaryPrimitives.WriteUInt16LittleEndian(b.Slice 26, hello.RxWinGrant0)
        BinaryPrimitives.WriteUInt64LittleEndian(b.Slice 28, hello.EchoTimeMs)
        if not x25519Pub.IsEmpty then x25519Pub.CopyTo(b.Slice(36, 32))
        BinaryPrimitives.WriteUInt128LittleEndian(b.Slice 68, hello.Cookie)
        padding.CopyTo(b.Slice 84)
        let c = Array.zeroCreate<byte> Layout.BootBodyLength
        let cs = Span c
        serverNonce.CopyTo cs
        seal (initServerKey psk clientNonce serverNonce) (ReadOnlySpan body) ReadOnlySpan<byte>.Empty (cs.Slice(32, BodyLength)) (cs.Slice(1064, 16))
        c

    let tryOpen (psk: ReadOnlySpan<byte>) (clientNonce: ReadOnlySpan<byte>) (content: ReadOnlySpan<byte>) =
        let sn = content.Slice(0, 32)
        match tryOpen (initServerKey psk clientNonce sn) (content.Slice(32, BodyLength)) (content.Slice(1064, 16)) ReadOnlySpan<byte>.Empty with
        | ValueNone -> ValueNone
        | ValueSome body ->
            let b = ReadOnlySpan body
            ValueSome(
                { Status = b.[0]
                  Suite = b.[1]
                  Caps = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 2)
                  SessionId = readGuid (b.Slice 4)
                  Symbol = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 20)
                  PathId = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 22)
                  MaxPaths = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 24)
                  RxWinGrant0 = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice 26)
                  EchoTimeMs = BinaryPrimitives.ReadUInt64LittleEndian(b.Slice 28)
                  Cookie = BinaryPrimitives.ReadUInt128LittleEndian(b.Slice 68) },
                body.[36 .. 67])

/// Session keys (spec 6.5.5). Generation g keys come from PRK_g; PRK_0 also gives the
/// fixed header-protection keys, graph seeds and K_path.
[<Sealed>]
type SessionKeys(ikm: byte[], clientNonce: byte[], serverNonce: byte[], sessionId: Guid, clientId: Guid,
                 suite: byte, frameLength: int, clientInit: byte[], serverInit: byte[]) =
    let prk0 = Kdf.extract (ReadOnlySpan(Array.append clientNonce serverNonce)) (ReadOnlySpan ikm)
    let th = Kdf.sha256 (ReadOnlySpan(Array.append clientInit serverInit))
    let ctx =
        let sid = Array.zeroCreate<byte> 16
        guidBytes sessionId (Span sid)
        let cid = Array.zeroCreate<byte> 16
        guidBytes clientId (Span cid)
        let l = Array.zeroCreate<byte> 2
        BinaryPrimitives.WriteUInt16BigEndian(Span l, uint16 frameLength)
        Array.concat [ Kdf.ascii "tbtun v2"; sid; (Kdf.sha256 (ReadOnlySpan cid)).[.. 7]; [| suite |]; l; th ]
    let klen = Suite.keyLength suite

    let be32 (g: uint32) =
        let b = Array.zeroCreate<byte> 4
        BinaryPrimitives.WriteUInt32BigEndian(Span b, g)
        b

    let info (label: string) (tail: byte[]) = ReadOnlySpan(Array.concat [ ctx; Kdf.ascii label; tail ])

    member _.SessionId = sessionId
    member _.Suite = suite
    member _.TranscriptHash = th
    member _.HpUp = Kdf.expand (ReadOnlySpan prk0) (info "up hp" [||]) klen
    member _.HpDown = Kdf.expand (ReadOnlySpan prk0) (info "down hp" [||]) klen
    member _.GraphSeedUp = BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(Kdf.expand (ReadOnlySpan prk0) (info "up graph" [||]) 8))
    member _.GraphSeedDown = BinaryPrimitives.ReadUInt64LittleEndian(ReadOnlySpan(Kdf.expand (ReadOnlySpan prk0) (info "down graph" [||]) 8))
    member _.PathKey = Kdf.expand (ReadOnlySpan prk0) (info "path" [||]) 32

    /// PRK of generation g (PRK_0 for g = 0).
    member this.Prk(g: uint32) =
        let mutable p = prk0
        for i in 1u .. g do
            p <- Kdf.expand (ReadOnlySpan p) (info "next" (be32 i)) 32
        p

    member this.UpKey(g: uint32) = Kdf.expand (ReadOnlySpan(this.Prk g)) (info "up key" (be32 g)) klen
    member this.DownKey(g: uint32) = Kdf.expand (ReadOnlySpan(this.Prk g)) (info "down key" (be32 g)) klen
