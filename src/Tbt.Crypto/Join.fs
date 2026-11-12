/// Path join: JOIN request and JOIN_ACK with JOINACK2 (spec 6.5.7). Cold path.
/// Both frames are 1087 bytes and frozen once rendered; TCP retransmission resends the
/// same bytes, and a new attempt needs a new connection, join_nonce and incarnation.
module Tbt.Crypto.Join

open System
open System.Buffers.Binary
open System.Security.Cryptography
open Tbt.Contracts

let private zeroNonce: byte[] = Array.zeroCreate 12

[<Literal>]
let PlainLength = 1040

type JoinRole =
    | Bulk = 0uy
    | Lane = 1uy

type JoinStatus =
    | Accept = 0uy
    | InvalidRequest = 1uy
    | Capacity = 2uy
    | MssUnsupported = 3uy
    | SessionClosing = 4uy
    | ResourceBusy = 5uy

[<Struct>]
type JoinRequest =
    { PathId: uint16
      Incarnation: uint32
      MssClient: uint16
      Role: JoinRole }

[<Struct>]
type JoinAck =
    { Status: JoinStatus
      Role: JoinRole
      Suite: byte
      SessionId: Guid
      PathId: uint16
      Incarnation: uint32
      Symbol: uint16
      MaxPaths: uint16
      Caps: uint16
      MssClient: uint16
      MssServer: uint16 }

let private joinKey (kPath: ReadOnlySpan<byte>) (joinNonce: ReadOnlySpan<byte>) =
    Kdf.derive32 joinNonce kPath (ReadOnlySpan(Kdf.ascii "tbtun v2 join"))

/// join_tag = HMAC-SHA256(K_path, "join" | join_nonce)[0..8].
let joinTag (kPath: ReadOnlySpan<byte>) (joinNonce: ReadOnlySpan<byte>) =
    (Kdf.hmac kPath (ReadOnlySpan(Array.append (Kdf.ascii "join") (joinNonce.ToArray())))).[.. 7]

/// Render the JOIN frame. padding is 1031 bytes from the CSPRNG.
let buildRequest (kPath: ReadOnlySpan<byte>) (joinNonce: ReadOnlySpan<byte>) (r: JoinRequest) (padding: ReadOnlySpan<byte>) =
    if joinNonce.Length <> 16 || padding.Length <> PlainLength - 9 then invalidArg "padding" "bad length"
    let plain = Array.zeroCreate<byte> PlainLength
    let p = Span plain
    BinaryPrimitives.WriteUInt16LittleEndian(p, r.PathId)
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice 2, r.Incarnation)
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice 6, r.MssClient)
    p.[8] <- byte r.Role
    padding.CopyTo(p.Slice 9)
    let content = Array.zeroCreate<byte> Layout.BootBodyLength
    let c = Span content
    joinNonce.CopyTo c
    (joinTag kPath joinNonce).CopyTo(c.Slice 16)
    ChaCha20Poly1305Aead(ReadOnlySpan(joinKey kPath joinNonce)).Encrypt(ReadOnlySpan zeroNonce, ReadOnlySpan plain, c.Slice(24, PlainLength), c.Slice(1064, 16), ReadOnlySpan<byte>.Empty)
    Bootstrap.frame (ReadOnlySpan content)

/// Open a JOIN frame with one K_path. The join_tag only selects the key; the full tag decides.
let tryOpenRequest (kPath: ReadOnlySpan<byte>) (wire: ReadOnlySpan<byte>) =
    let c = Bootstrap.content wire
    if c.IsEmpty then ValueNone
    else
        let joinNonce = c.Slice(0, 16)
        if not (c.Slice(16, 8).SequenceEqual(ReadOnlySpan(joinTag kPath joinNonce))) then ValueNone
        else
            let plain = Array.zeroCreate<byte> PlainLength
            let aead = ChaCha20Poly1305Aead(ReadOnlySpan(joinKey kPath joinNonce))
            if not (aead.TryDecrypt(ReadOnlySpan zeroNonce, c.Slice(24, PlainLength), c.Slice(1064, 16), Span plain, ReadOnlySpan<byte>.Empty)) then
                ValueNone
            else
                let p = ReadOnlySpan plain
                ValueSome
                    { PathId = BinaryPrimitives.ReadUInt16LittleEndian p
                      Incarnation = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice 2)
                      MssClient = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice 6)
                      Role = LanguagePrimitives.EnumOfValue<byte, JoinRole> p.[8] }

/// JTH = SHA256(JOIN_WIRE).
let transcriptHash (joinWire: ReadOnlySpan<byte>) = Kdf.sha256 joinWire

let private ackKey (kPath: ReadOnlySpan<byte>) (joinNonce: ReadOnlySpan<byte>) (jth: ReadOnlySpan<byte>) =
    let info = Array.concat [ Kdf.ascii "tbtun v2 join ack s2c"; [| 0uy |]; jth.ToArray() ]
    Kdf.derive32 joinNonce kPath (ReadOnlySpan info)

/// ack_hint = HMAC-SHA256(K_path, "tbtun v2 join ack hint s2c" | 00 | join_nonce | JTH)[0..8].
let ackHint (kPath: ReadOnlySpan<byte>) (joinNonce: ReadOnlySpan<byte>) (jth: ReadOnlySpan<byte>) =
    let data = Array.concat [ Kdf.ascii "tbtun v2 join ack hint s2c"; [| 0uy |]; joinNonce.ToArray(); jth.ToArray() ]
    (Kdf.hmac kPath (ReadOnlySpan data)).[.. 7]

let private ackAad (joinNonce: ReadOnlySpan<byte>) (hint: byte[]) (jth: ReadOnlySpan<byte>) =
    Array.concat
        [ Kdf.ascii "tbtun v2 join ack aad s2c"; [| 0uy; byte Direction.Down |]; Bootstrap.Header
          [| 0x04uy; 0x3Fuy |]; joinNonce.ToArray(); hint; jth.ToArray(); Bootstrap.Trailer ]

/// Render the JOIN_ACK frame for the request with join_nonce and transcript hash jth.
/// padding is 968 bytes from the CSPRNG.
let buildAck (kPath: ReadOnlySpan<byte>) (joinNonce: ReadOnlySpan<byte>) (jth: ReadOnlySpan<byte>) (a: JoinAck) (padding: ReadOnlySpan<byte>) =
    if joinNonce.Length <> 16 || jth.Length <> 32 || padding.Length <> PlainLength - 72 then invalidArg "padding" "bad length"
    let plain = Array.zeroCreate<byte> PlainLength
    let p = Span plain
    p.[0] <- 2uy
    p.[1] <- byte a.Status
    p.[2] <- byte a.Role
    p.[3] <- a.Suite
    a.SessionId.TryWriteBytes(p.Slice(4, 16), true) |> ignore
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice 20, a.PathId)
    BinaryPrimitives.WriteUInt32LittleEndian(p.Slice 22, a.Incarnation)
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice 26, a.Symbol)
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice 28, a.MaxPaths)
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice 30, a.Caps)
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice 32, a.MssClient)
    BinaryPrimitives.WriteUInt16LittleEndian(p.Slice 34, a.MssServer)
    jth.CopyTo(p.Slice 36)
    padding.CopyTo(p.Slice 72)
    let hint = ackHint kPath joinNonce jth
    let content = Array.zeroCreate<byte> Layout.BootBodyLength
    let c = Span content
    joinNonce.CopyTo c
    hint.CopyTo(c.Slice 16)
    ChaCha20Poly1305Aead(ReadOnlySpan(ackKey kPath joinNonce jth)).Encrypt(ReadOnlySpan zeroNonce, ReadOnlySpan plain, c.Slice(24, PlainLength), c.Slice(1064, 16), ReadOnlySpan(ackAad joinNonce hint jth))
    Bootstrap.frame (ReadOnlySpan content)

/// Open and check a JOIN_ACK against the request the client sent (spec 6.5.7.3). ValueNone
/// on any wrapper, nonce, hint, tag, version, reserved-byte or JTH mismatch. The caller
/// still compares session, path, incarnation, role, S, max_paths, caps, suite and mss_client.
let tryOpenAck (kPath: ReadOnlySpan<byte>) (joinNonce: ReadOnlySpan<byte>) (jth: ReadOnlySpan<byte>) (wire: ReadOnlySpan<byte>) =
    let c = Bootstrap.content wire
    let hint = ackHint kPath joinNonce jth
    if c.IsEmpty || not (c.Slice(0, 16).SequenceEqual joinNonce) || not (c.Slice(16, 8).SequenceEqual(ReadOnlySpan hint)) then
        ValueNone
    else
        let plain = Array.zeroCreate<byte> PlainLength
        let aead = ChaCha20Poly1305Aead(ReadOnlySpan(ackKey kPath joinNonce jth))
        let opened = aead.TryDecrypt(ReadOnlySpan zeroNonce, c.Slice(24, PlainLength), c.Slice(1064, 16), Span plain, ReadOnlySpan(ackAad joinNonce hint jth))
        let p = ReadOnlySpan plain
        if not opened || p.[0] <> 2uy || p.[1] > 5uy || not (p.Slice(36, 32).SequenceEqual jth)
           || p.Slice(68, 4).IndexOfAnyExcept(0uy) >= 0 then
            ValueNone
        else
            ValueSome
                { Status = LanguagePrimitives.EnumOfValue<byte, JoinStatus> p.[1]
                  Role = LanguagePrimitives.EnumOfValue<byte, JoinRole> p.[2]
                  Suite = p.[3]
                  SessionId = Guid(p.Slice(4, 16), true)
                  PathId = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice 20)
                  Incarnation = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice 22)
                  Symbol = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice 26)
                  MaxPaths = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice 28)
                  Caps = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice 30)
                  MssClient = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice 32)
                  MssServer = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice 34) }
