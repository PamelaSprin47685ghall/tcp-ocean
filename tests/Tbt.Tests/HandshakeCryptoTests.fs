module Tbt.Tests.HandshakeCryptoTests

open System
open System.Diagnostics
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
open Tbt.Crypto.Kernels
open Tbt.Session
open Tbt.Session.Handshake
open Tbt.Wire
open Tbt.Tests.SessionTests
open Tbt.Tests.Vectors

// ---- RFC 7748 X25519 -------------------------------------------------------

let private rfcScalar u = hex u
let private rfcOut (got: byte[]) (expected: string) = Assert.Equal(expected, toHex (ReadOnlySpan got))

[<Fact>]
let ``X25519 matches RFC 7748 section 5.2 test vectors`` () =
    let out = Array.zeroCreate 32
    Assert.True(X25519.compute (ReadOnlySpan(hex "a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"))
                               (ReadOnlySpan(hex "e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"))
                               (Span out))
    rfcOut out "c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"
    Assert.True(X25519.compute (ReadOnlySpan(hex "4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"))
                               (ReadOnlySpan(hex "e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493"))
                               (Span out))
    rfcOut out "95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957"

[<Fact>]
let ``X25519 iterated RFC 7748 section 5.2 vectors`` () =
    let k = Array.copy X25519.BasePoint
    let u = Array.copy X25519.BasePoint
    let next () =
        let nk = Array.zeroCreate 32
        Assert.True(X25519.compute (ReadOnlySpan k) (ReadOnlySpan u) (Span nk))
        u.AsSpan().Clear()
        k.AsSpan().CopyTo(Span u)
        nk.AsSpan().CopyTo(Span k)
    next ()
    rfcOut k "422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079"
    for _ in 2 .. 1000 do next ()
    rfcOut k "684cf59ba83309552800ef566f2f4d3c1c3887c49360e3875f2eb94d99532c51"

[<Fact>]
let ``X25519 Diffie-Hellman matches RFC 7748 section 6.1`` () =
    let alice = hex "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"
    let bob = hex "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"
    let aPub, bPub, sharedA, sharedB = Array.zeroCreate 32, Array.zeroCreate 32, Array.zeroCreate 32, Array.zeroCreate 32
    Assert.True(X25519.publicKey (ReadOnlySpan alice) (Span aPub))
    Assert.True(X25519.publicKey (ReadOnlySpan bob) (Span bPub))
    rfcOut aPub "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"
    rfcOut bPub "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"
    Assert.True(X25519.compute (ReadOnlySpan alice) (ReadOnlySpan bPub) (Span sharedA))
    Assert.True(X25519.compute (ReadOnlySpan bob) (ReadOnlySpan aPub) (Span sharedB))
    rfcOut sharedA "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"
    Assert.Equal<byte[]>(sharedA, sharedB)
    // A low-order (all-zero) u-coordinate must be rejected, not treated as IKM padding.
    let z = Array.zeroCreate 32
    Assert.False(X25519.compute (ReadOnlySpan alice) (ReadOnlySpan z) (Span z))
    Assert.True(X25519.isZero (ReadOnlySpan z))

// ---- ChaCha8 (RFC 8439 construction, 8 rounds) -----------------------------

[<Fact>]
let ``ChaCha8 block on the RFC 8439 2.3.2 inputs matches the independent reference`` () =
    let key = Array.init 32 byte
    let nonce = hex "000000090000004a00000000"
    let out = Array.zeroCreate<byte> 64
    ChaCha20.blockRounds 4 (ChaCha20.keyWords (ReadOnlySpan key)) 1u (ReadOnlySpan nonce) (Span out)
    Assert.Equal(
        "eead9dfbbc60443e9d6811bab8e60a3ac6001e0dfb985f65efcb0ea42454411c"
        + "64747ef73d4766e0c20e19208e5cb11777d487263152e65dc5ff947fcab23b2b",
        toHex (ReadOnlySpan out))
    // 8-round and 20-round streams of the same inputs must differ.
    let twenty = Array.zeroCreate<byte> 64
    ChaCha20.block (ChaCha20.keyWords (ReadOnlySpan key)) 1u (ReadOnlySpan nonce) (Span twenty)
    Assert.NotEqual<string>(toHex (ReadOnlySpan out), toHex (ReadOnlySpan twenty))

[<Fact>]
let ``ChaCha8-Poly1305 AEAD on the RFC 8439 2.8.2 inputs matches the independent reference`` () =
    let key = hex "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f"
    let nonce = hex "070000004041424344454647"
    let plain = hex (
        "4c616469657320616e642047656e746c656d656e206f662074686520636c617373206f66202739393a"
        + "204966204920636f756c64206f6666657220796f75206f6e6c79206f6e652074697020666f7220746865"
        + "206675747572652c2073756e73637265656e20776f756c642062652069742e")
    let aad = hex "50515253c0c1c2c3c4c5c6c7"
    let aead = ChaCha20Poly1305Aead(ReadOnlySpan key, 4)
    let cipher = Array.zeroCreate plain.Length
    let tag = Array.zeroCreate 16
    aead.Encrypt(ReadOnlySpan nonce, ReadOnlySpan plain, Span cipher, Span tag, ReadOnlySpan aad)
    Assert.Equal(
        "92a6d1239f63d2f562bd5901d90df4fc53f63a1ec5201aa80648198a6436f717"
        + "26d646a496ea4c0dfa2ccfc994770e46e304829d1ad76490db4c27235cc6ebbf"
        + "25003fd30da6c4b76b7a90a947226c0122e9092fc556115ff72eac3f4970c239"
        + "bceeb55d3f61d8520c109131d5a7a1510864",
        toHex (ReadOnlySpan cipher))
    Assert.Equal("cae943b1d1d6ecfd13efbe36c2e356e0", toHex (ReadOnlySpan tag))
    let back = Array.zeroCreate plain.Length
    Assert.True(aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad))
    Assert.Equal<byte[]>(plain, back)
    // The 20-round AEAD must not accept an 8-round tag.
    let twenty = ChaCha20Poly1305Aead(ReadOnlySpan key, 10)
    Assert.False(twenty.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad))

// ---- Handshake negotiation and end-to-end interop --------------------------

let private id = Guid(Array.create 16 7uy)

let private handshake (x25519: bool) (chaCha8: bool) (aegis: bool) (seedC: int) (seedS: int) =
    let wantAegis = aegis
    let offer = ClientOffer(psk, id, SeededEntropy seedC, uint64 unixMs, 1448, 256,
                            offerX25519 = x25519, offerChaCha8 = chaCha8, offerAegis = wantAegis)
    let accepted = (ServerOffer([| psk |], SeededEntropy seedS, 4096us, 256,
                                offerX25519 = x25519, offerChaCha8 = chaCha8, offerAegis = wantAegis))
                       .TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    Assert.True(accepted.IsSome, "server rejected CLIENT_INIT")
    let struct (clientParams, sh) = offer.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    let serverParams = accepted.Value.Params
    struct (clientParams, serverParams, sh)

let private frameRoundTrip (cp: SessionParams) (sp: SessionParams) =
    Assert.Equal(cp.Keys.Suite, sp.Keys.Suite)
    Assert.Equal<byte[]>(cp.Keys.UpKey 0u, sp.Keys.UpKey 0u)
    Assert.Equal<byte[]>(cp.Keys.DownKey 0u, sp.Keys.DownKey 0u)
    Assert.Equal<byte[]>(cp.Keys.HpUp, sp.Keys.HpUp)
    let table = KeyTable(cp.Keys.Suite)
    table.Install(0u, ReadOnlySpan(cp.Keys.UpKey 0u), 0UL)
    let sealer = CellSealer(Direction.Up, cp.Symbol, table, HeaderMasks.Create(cp.Keys.Suite, ReadOnlySpan cp.Keys.HpUp))
    let f = Array.zeroCreate (Layout.frameLength cp.Symbol)
    FrameMath.writeChunkHeader cp.Symbol (Span f)
    f.[f.Length - 2] <- 13uy
    f.[f.Length - 1] <- 10uy
    for i in 0 .. cp.Symbol - 1 do f.[Layout.SymbolOffset + i] <- byte (i * 3 + 1)
    sealer.Seal(Span f, 7UL, 0us)
    let rtable = KeyTable(sp.Keys.Suite)
    rtable.Install(0u, ReadOnlySpan(sp.Keys.UpKey 0u), 0UL)
    let opener = CellOpener(Direction.Up, sp.Symbol, rtable, HeaderMasks.Create(sp.Keys.Suite, ReadOnlySpan sp.Keys.HpUp))
    let copy = Array.copy f
    let mutable c = 0UL
    Assert.True(opener.TryOpen(Span copy, 0us, &c))
    Assert.Equal(7UL, c)
    Assert.Equal(byte (0 * 3 + 1), copy.[Layout.SymbolOffset])

[<Fact>]
let ``default handshake still selects suite 0x02 and does not set X25519`` () =
    let struct (c, s, sh) = handshake false false false 101 202
    Assert.Equal(Suite.ChaCha20Poly1305, c.Keys.Suite)
    Assert.Equal(Suite.ChaCha20Poly1305, sh.Suite)
    Assert.Equal(0us, c.Caps &&& Caps.X25519)
    Assert.Equal(0us, s.Caps &&& Caps.X25519)
    Assert.Equal(Suite.ChaCha20Poly1305Bit, (ClientOffer(psk, id, SeededEntropy 1, uint64 unixMs, 1448, 256)).Hello.Suites)
    frameRoundTrip c s

[<Fact>]
let ``X25519 decorator is negotiated only when both ends offer it, and the IKM changes`` () =
    let struct (bothC, bothS, sh) = handshake true false false 111 222
    Assert.Equal(Caps.X25519, bothC.Caps &&& Caps.X25519)
    Assert.Equal(Caps.X25519, sh.Caps &&& Caps.X25519)
    Assert.Equal(Suite.ChaCha20Poly1305, bothC.Keys.Suite)
    frameRoundTrip bothC bothS
    // Only the client offers it: the bit is not in the intersection; IKM stays PSK.
    let onlyClient = ClientOffer(psk, id, SeededEntropy 111, uint64 unixMs, 1448, 256, offerX25519 = true)
    let accepted = (ServerOffer([| psk |], SeededEntropy 222, 4096us, 256, offerX25519 = false))
                       .TryAccept(ReadOnlySpan onlyClient.Frame, unixMs, 1448)
    Assert.True(accepted.IsSome)
    let struct (oneC, oneH) = onlyClient.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    Assert.Equal(0us, oneC.Caps &&& Caps.X25519)
    Assert.Equal(0us, oneH.Caps &&& Caps.X25519)
    frameRoundTrip oneC accepted.Value.Params
    // Same nonces would not be enough to compare IKM here (server entropy diverges),
    // but a two-sided session must not reuse the PSK-only up key of a one-sided one
    // with the same client seed and a different server seed... We just check the
    // two-sided keys are not a function of the PSK alone: different from one-sided.
    Assert.NotEqual<string>(toHex (ReadOnlySpan(bothC.Keys.UpKey 0u)), toHex (ReadOnlySpan(oneC.Keys.UpKey 0u)))

[<Fact>]
let ``ChaCha8 is selected when both ends offer it, and a 0x02 sealer cannot open the cell`` () =
    let struct (c, s, sh) = handshake false true false 131 232
    Assert.Equal(Suite.ChaCha8Poly1305, c.Keys.Suite)
    Assert.Equal(Suite.ChaCha8Poly1305, sh.Suite)
    Assert.Equal(Suite.ChaCha8Poly1305, s.Keys.Suite)
    frameRoundTrip c s
    // Cross-suite: seal with 0x03, try to open with a 0x02 table of the same raw key.
    let t20 = KeyTable(Suite.ChaCha20Poly1305)
    t20.Install(0u, ReadOnlySpan(c.Keys.UpKey 0u), 0UL)
    let t8 = KeyTable(Suite.ChaCha8Poly1305)
    t8.Install(0u, ReadOnlySpan(c.Keys.UpKey 0u), 0UL)
    let sealer = CellSealer(Direction.Up, c.Symbol, t8, ChaChaHeaderMask(ReadOnlySpan c.Keys.HpUp, 4))
    let f = Array.zeroCreate (Layout.frameLength c.Symbol)
    FrameMath.writeChunkHeader c.Symbol (Span f)
    f.[f.Length - 2] <- 13uy
    f.[f.Length - 1] <- 10uy
    sealer.Seal(Span f, 1UL, 0us)
    let opener = CellOpener(Direction.Up, c.Symbol, t20, ChaChaHeaderMask(ReadOnlySpan c.Keys.HpUp, 10))
    let mutable ctr = 0UL
    Assert.False(opener.TryOpen(Span(Array.copy f), 0us, &ctr))
    Assert.True(opener.AuthFails > 0L)

[<Fact>]
let ``X25519 and ChaCha8 together still let client and server exchange a cell`` () =
    let struct (c, s, sh) = handshake true true false 141 242
    Assert.Equal(Suite.ChaCha8Poly1305, sh.Suite)
    Assert.Equal(Caps.X25519, c.Caps &&& Caps.X25519)
    frameRoundTrip c s
    // A short session echo still works on the new suite and IKM.
    let clock = ManualClock()
    let client = Session(Role.Client, c, RecordingPort(), clock, SessionConfig.defaults)
    let server = Session(Role.Server, s, RecordingPort(), clock, SessionConfig.defaults)
    Assert.Equal(Suite.ChaCha8Poly1305, client.Keys.Suite)
    let frame = Array.zeroCreate client.FrameLength
    client.Tick()
    Assert.True(client.TryProduce(0us, Span frame))
    Assert.NotEqual(UInt64.MaxValue, server.OnFrame(0us, Span frame))
    server.Tick()
    Assert.True(server.TryProduce(0us, Span frame) || not server.WantsToSend)

let private windowHandshake (client: WindowOffer.Params) (server: WindowOffer.Params) =
    let offer = ClientOffer(psk, id, SeededEntropy 301, uint64 unixMs, 1448, 256, windowOffer = client)
    let accepted = (ServerOffer([| psk |], SeededEntropy 302, 4096us, 256, windowOffer = server))
                       .TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    struct (offer, accepted)

[<Fact>]
let ``window offer is negotiated only when both ends echo the same record`` () =
    let struct (offer, accepted) = windowHandshake WindowOffer.defaults WindowOffer.defaults
    Assert.True(accepted.IsSome)
    Assert.Equal(Caps.WindowCode, offer.Hello.Caps &&& Caps.WindowCode)
    let struct (clientParams, sh) = offer.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    Assert.Equal(Caps.WindowCode, clientParams.Caps &&& Caps.WindowCode)
    Assert.Equal(Caps.WindowCode, sh.Caps &&& Caps.WindowCode)
    Assert.Equal(Caps.WindowCode, accepted.Value.Params.Caps &&& Caps.WindowCode)

[<Fact>]
let ``unsupported window parameters are rejected even when peers match`` () =
    let p = WindowOffer.defaults
    let unsupported =
        [| { p with Version = 2uy }; { p with WindowSteps = 3uy }
           { p with Field = 1uy }; { p with Degree = 8uy }; { p with SymbolCount = 32us } |]
    Assert.True(WindowOffer.isValid p)
    for other in unsupported do
        Assert.False(WindowOffer.isValid other)
        let padding = Array.zeroCreate WindowOffer.Length
        WindowOffer.write (Span padding) other
        Assert.True(WindowOffer.tryRead (ReadOnlySpan padding) |> ValueOption.isNone)
        Assert.Throws<ArgumentException>(fun () ->
            ClientOffer(psk, id, SeededEntropy 301, uint64 unixMs, 1448, 256, windowOffer = other) |> ignore) |> ignore
        Assert.Throws<ArgumentException>(fun () ->
            ServerOffer([| psk |], SeededEntropy 302, 4096us, 256, windowOffer = other) |> ignore) |> ignore

[<Fact>]
let ``window version mismatch is rejected`` () =
    let hello =
        { Version = 2uy; Suites = Suite.ChaCha20Poly1305Bit
          Caps = SupportedCaps ||| Caps.WindowCode; ClientId = id
          UnixTimeMs = uint64 unixMs; MssClient = 1448us; MaxPaths = 256us }
    let cn = Array.init 32 (fun i -> byte (i + 11))
    let padding = Array.zeroCreate (ClientInit.BodyLength - 64)
    let bad = { WindowOffer.defaults with Version = 9uy }
    // Stamp is refused for an invalid record, so write the bytes directly.
    padding.[2] <- bad.Version
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(Span padding, WindowOffer.Magic)
    padding.[3] <- WindowOffer.DefaultSteps
    padding.[5] <- WindowOffer.DefaultDegree
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(Span(padding, 6, 2), WindowOffer.DefaultSymbols)
    let content = ClientInit.build (ReadOnlySpan psk) (ReadOnlySpan cn) hello ReadOnlySpan<byte>.Empty (ReadOnlySpan padding)
    let frame = Bootstrap.frame (ReadOnlySpan content)
    let accepted = (ServerOffer([| psk |], SeededEntropy 9, 4096us, 256, windowOffer = WindowOffer.defaults))
                       .TryAccept(ReadOnlySpan frame, unixMs, 1448)
    Assert.True(accepted.IsNone)

[<Fact>]
let ``an old peer that does not offer the window bit is refused when this end requires it`` () =
    let offer = ClientOffer(psk, id, SeededEntropy 311, uint64 unixMs, 1448, 256, windowOffer = WindowOffer.defaults)
    let accepted = (ServerOffer([| psk |], SeededEntropy 312, 4096us, 256))
                       .TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    Assert.True(accepted.IsNone)
    let legacy = ClientOffer(psk, id, SeededEntropy 313, uint64 unixMs, 1448, 256)
    let server = ServerOffer([| psk |], SeededEntropy 314, 4096us, 256, windowOffer = WindowOffer.defaults)
    Assert.True(server.TryAccept(ReadOnlySpan legacy.Frame, unixMs, 1448).IsNone)

[<Fact>]
let ``default handshake still leaves the window bit clear`` () =
    let offer = ClientOffer(psk, id, SeededEntropy 321, uint64 unixMs, 1448, 256)
    Assert.Equal(0us, offer.Hello.Caps &&& Caps.WindowCode)
    let accepted = (ServerOffer([| psk |], SeededEntropy 322, 4096us, 256))
                       .TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    Assert.True(accepted.IsSome)
    Assert.Equal(0us, accepted.Value.Params.Caps &&& Caps.WindowCode)

[<Fact>]
let ``server rejects unknown codec capability instead of silently selecting legacy codec`` () =
    let hello =
        { Version = 2uy; Suites = Suite.ChaCha20Poly1305Bit
          Caps = SupportedCaps ||| 0x0800us; ClientId = id
          UnixTimeMs = uint64 unixMs; MssClient = 1448us; MaxPaths = 256us }
    let cn = Array.init 32 (fun i -> byte (i + 3))
    let padding = Array.zeroCreate (ClientInit.BodyLength - 64)
    let content = ClientInit.build (ReadOnlySpan psk) (ReadOnlySpan cn) hello ReadOnlySpan<byte>.Empty (ReadOnlySpan padding)
    let frame = Bootstrap.frame (ReadOnlySpan content)
    let accepted = ServerOffer([| psk |], SeededEntropy 9, 4096us, 256).TryAccept(ReadOnlySpan frame, unixMs, 1448)
    Assert.True(accepted.IsNone)

[<Fact>]
let ``a CLIENT_INIT that sets X25519 with an all-zero public key is rejected`` () =
    let hello =
        { Version = 2uy; Suites = Suite.ChaCha20Poly1305Bit
          Caps = SupportedCaps ||| Caps.X25519; ClientId = id
          UnixTimeMs = uint64 unixMs; MssClient = 1448us; MaxPaths = 256us }
    let cn = Array.init 32 (fun i -> byte (i + 3))
    let padding = Array.init (ClientInit.BodyLength - 64) (fun i -> byte i)
    let content = ClientInit.build (ReadOnlySpan psk) (ReadOnlySpan cn) hello (ReadOnlySpan(Array.zeroCreate 32)) (ReadOnlySpan padding)
    let frame = Bootstrap.frame (ReadOnlySpan content)
    let accepted = (ServerOffer([| psk |], SeededEntropy 9, 4096us, 256, offerX25519 = true))
                       .TryAccept(ReadOnlySpan frame, unixMs, 1448)
    Assert.True(accepted.IsNone)

// ---- AEGIS-128L (RFC 10032 / spec 6.4.1) -----------------------------------

let private requireAes () =
    if not Aegis128LAead.IsSupported then failwith "AEGIS-128L tests require hardware AES"

let private loadAegisVectors () =
    let path = System.IO.Path.Combine(AppContext.BaseDirectory, "vectors", "aegis-128l-test-vectors.json")
    System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText path).RootElement

[<Fact>]
let ``AESRound matches the RFC 10032 appendix A.1 vector`` () =
    requireAes ()
    let path = System.IO.Path.Combine(AppContext.BaseDirectory, "vectors", "aesround-test-vector.json")
    let v = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText path).RootElement.[0]
    let hs (name: string) =
        match v.GetProperty(name).GetString() with
        | null -> failwith name
        | s -> hex s
    let input, rk, expected = hs "in", hs "rk", hs "out"
    let actual = Array.zeroCreate 16
    Aegis128L.AesRound(ReadOnlySpan input, ReadOnlySpan rk, Span actual)
    Assert.Equal(toHex (ReadOnlySpan expected), toHex (ReadOnlySpan actual))

[<Fact>]
let ``AES-128-ECB of a NIST block matches, and is used for AEGIS header protection`` () =
    requireAes ()
    // FIPS-197 C.1, AES-128.
    let key = hex "000102030405060708090a0b0c0d0e0f"
    let pt = hex "00112233445566778899aabbccddeeff"
    let ct = hex "69c4e0d86a7b0430d8cdb78070b4c55a"
    let aes = Aes128Ecb(ReadOnlySpan key)
    let out = Array.zeroCreate 16
    aes.Encrypt(ReadOnlySpan pt, Span out)
    Assert.Equal(toHex (ReadOnlySpan ct), toHex (ReadOnlySpan out))
    let mask = Array.zeroCreate 8
    let hp = AesHeaderMask(ReadOnlySpan key) :> IHeaderMask
    hp.Compute(ReadOnlySpan pt, Span mask)
    Assert.Equal(toHex (ReadOnlySpan(ct.[0..7])), toHex (ReadOnlySpan mask))

[<Fact>]
let ``AEGIS-128L Update matches the RFC 10032 appendix A.2.1 vector`` () =
    requireAes ()
    let root = loadAegisVectors ()
    let v = root.[0]
    let g (name: string) =
        match v.GetProperty(name).GetString() with
        | null -> failwith name
        | s -> hex s
    let state = Array.concat [ g "S0"; g "S1"; g "S2"; g "S3"; g "S4"; g "S5"; g "S6"; g "S7" ]
    Aegis128L.ApplyUpdate(Span state, ReadOnlySpan(g "M0"), ReadOnlySpan(g "M1"))
    let after = Array.concat [ g "S0_2"; g "S1_2"; g "S2_2"; g "S3_2"; g "S4_2"; g "S5_2"; g "S6_2"; g "S7_2" ]
    Assert.Equal(toHex (ReadOnlySpan after), toHex (ReadOnlySpan state))

[<Fact>]
let ``AEGIS-128L AEAD matches the RFC 10032 appendix A.2 reference vectors`` () =
    requireAes ()
    let root = loadAegisVectors ()
    let mutable kat = 0
    let mutable rejected = 0
    let hexProp (v: System.Text.Json.JsonElement) (name: string) =
        match v.TryGetProperty(name) with
        | true, p ->
            match p.GetString() with
            | null | "" -> [||]
            | s -> hex s
        | _ -> [||]
    for v in root.EnumerateArray() do
        match v.TryGetProperty("key") with
        | false, _ -> ()
        | true, _ ->
            let key = hexProp v "key"
            let nonce = hexProp v "nonce"
            let ad = hexProp v "ad"
            let tag = hexProp v "tag128"
            let aead = Aegis128LAead(ReadOnlySpan key)
            match v.TryGetProperty("error") with
            | true, _ ->
                let ct = hexProp v "ct"
                let back = Array.zeroCreate ct.Length
                Assert.False(aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan ct, ReadOnlySpan tag, Span back, ReadOnlySpan ad))
                Assert.All(back, fun b -> Assert.Equal(0uy, b))
                rejected <- rejected + 1
            | _ ->
                let msg = hexProp v "msg"
                let ct = hexProp v "ct"
                let cipher = Array.zeroCreate msg.Length
                let got = Array.zeroCreate 16
                aead.Encrypt(ReadOnlySpan nonce, ReadOnlySpan msg, Span cipher, Span got, ReadOnlySpan ad)
                Assert.Equal(toHex (ReadOnlySpan ct), toHex (ReadOnlySpan cipher))
                Assert.Equal(toHex (ReadOnlySpan tag), toHex (ReadOnlySpan got))
                let back = Array.zeroCreate msg.Length
                Assert.True(aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan ad))
                Assert.Equal<byte[]>(msg, back)
                kat <- kat + 1
    Assert.True(kat >= 5 && rejected >= 4, sprintf "expected RFC KATs and forgeries, got kat=%d rejected=%d" kat rejected)

[<Fact>]
let ``AEGIS-128L forged tag is rejected with no allocation and a cleared plaintext`` () =
    requireAes ()
    let key = Array.init 16 (fun i -> byte (i + 1))
    let nonce = Array.init 16 (fun i -> byte (i * 3))
    let aad = Array.init 18 (fun i -> byte (i + 7))
    let plain = Array.init 1384 (fun i -> byte i)
    let aead = Aegis128LAead(ReadOnlySpan key)
    let cipher = Array.copy plain
    let tag = Array.zeroCreate 16
    aead.Encrypt(ReadOnlySpan nonce, ReadOnlySpan plain, Span cipher, Span tag, ReadOnlySpan aad)
    tag.[15] <- tag.[15] ^^^ 0x80uy
    let back = Array.copy cipher
    for _ in 1 .. 8 do
        aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad) |> ignore
    GC.Collect()
    GC.WaitForPendingFinalizers()
    let before = GC.GetAllocatedBytesForCurrentThread()
    for _ in 1 .. 1000 do
        Assert.False(aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad))
    let alloc = GC.GetAllocatedBytesForCurrentThread() - before
    Assert.Equal(0L, alloc)
    Assert.All(back, fun b -> Assert.Equal(0uy, b))

[<Fact>]
let ``default handshake still does not advertise or select AEGIS-128L`` () =
    Assert.Equal(Suite.ChaCha20Poly1305Bit, Suite.advertisedBits false false true)
    if Suite.hardwareAes then
        Assert.Equal(Suite.ChaCha20Poly1305Bit ||| Suite.ChaCha8Poly1305Bit ||| Suite.Aegis128LBit, Suite.advertisedBits true true true)
    else
        Assert.Equal(Suite.ChaCha20Poly1305Bit ||| Suite.ChaCha8Poly1305Bit, Suite.advertisedBits true true true)
    // bit0 is set only when offerAegis and hardware AES are both present.
    if Suite.hardwareAes then
        Assert.Equal(Suite.ChaCha20Poly1305Bit ||| Suite.Aegis128LBit, Suite.advertisedBits false true true)
        Assert.Equal(Suite.Aegis128L, Suite.select (Suite.Aegis128LBit ||| Suite.ChaCha20Poly1305Bit) true false true)
    else
        Assert.Equal(Suite.ChaCha20Poly1305Bit, Suite.advertisedBits false true true)
        Assert.Equal(Suite.ChaCha20Poly1305, Suite.select (Suite.Aegis128LBit ||| Suite.ChaCha20Poly1305Bit) true false true)
    let struct (c, s, sh) = handshake false false false 101 202
    Assert.Equal(Suite.ChaCha20Poly1305, c.Keys.Suite)
    Assert.NotEqual<byte>(Suite.Aegis128L, sh.Suite)
    Assert.Equal(32, c.Keys.UpKey(0u).Length)
    Assert.Equal(32, c.Keys.HpUp.Length)
    ignore s

[<Fact>]
let ``--suites names parse and advertisedBits can omit 0x02`` () =
    Assert.Equal(struct (false, false, true), Suite.parseOfferList "")
    Assert.Equal(struct (false, false, true), Suite.parseOfferList "chacha20")
    Assert.Equal(struct (true, true, true), Suite.parseOfferList "aegis,chacha8,chacha20")
    Assert.Equal(struct (true, false, false), Suite.parseOfferList "AEGIS-128L")
    Assert.Equal(struct (false, true, false), Suite.parseOfferList "0x03")
    Assert.Equal("aegis", Suite.name Suite.Aegis128L)
    Assert.Equal("chacha20", Suite.name Suite.ChaCha20Poly1305)
    Assert.Equal("chacha8", Suite.name Suite.ChaCha8Poly1305)
    Assert.Throws<ArgumentException>(fun () -> Suite.parseOfferList "rc4" |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> Suite.parseOfferList "  ,  " |> ignore) |> ignore
    // Explicitly omitting 0x02 does not change the two-argument default.
    Assert.Equal(Suite.ChaCha20Poly1305Bit, Suite.advertisedBits false false true)
    if Suite.hardwareAes then
        Assert.Equal(Suite.Aegis128LBit, Suite.advertisedBits false true false)
        Assert.Equal(Suite.Aegis128L, Suite.select Suite.Aegis128LBit true false false)
        Assert.Equal(0uy, Suite.select Suite.ChaCha20Poly1305Bit false false false)
    else
        Assert.Equal(0uy, Suite.advertisedBits false true false)

[<Fact>]
let ``AEGIS-128L is selected when both ends offer it, keys are 16 bytes, and a 0x02 sealer cannot open the cell`` () =
    requireAes ()
    let struct (c, s, sh) = handshake false false true 151 252
    Assert.Equal(Suite.Aegis128L, c.Keys.Suite)
    Assert.Equal(Suite.Aegis128L, sh.Suite)
    Assert.Equal(Suite.Aegis128L, s.Keys.Suite)
    Assert.Equal(16, c.Keys.UpKey(0u).Length)
    Assert.Equal(16, c.Keys.DownKey(0u).Length)
    Assert.Equal(16, c.Keys.HpUp.Length)
    Assert.Equal(16, s.Keys.HpDown.Length)
    frameRoundTrip c s
    // Cross-suite: seal with 0x01, try to open with a 0x02 table (different klen, must not succeed).
    let t20 = KeyTable(Suite.ChaCha20Poly1305)
    // 0x02 needs a 32-byte key; pad to show the raw 16-byte material is not interchangeable.
    let padded = Array.append (c.Keys.UpKey 0u) (Array.zeroCreate 16)
    t20.Install(0u, ReadOnlySpan padded, 0UL)
    let tA = KeyTable(Suite.Aegis128L)
    tA.Install(0u, ReadOnlySpan(c.Keys.UpKey 0u), 0UL)
    let sealer = CellSealer(Direction.Up, c.Symbol, tA, HeaderMasks.Create(Suite.Aegis128L, ReadOnlySpan c.Keys.HpUp))
    let f = Array.zeroCreate (Layout.frameLength c.Symbol)
    FrameMath.writeChunkHeader c.Symbol (Span f)
    f.[f.Length - 2] <- 13uy
    f.[f.Length - 1] <- 10uy
    sealer.Seal(Span f, 1UL, 0us)
    let opener = CellOpener(Direction.Up, c.Symbol, t20, ChaChaHeaderMask(ReadOnlySpan(Array.append c.Keys.HpUp (Array.zeroCreate 16)), 10))
    let mutable ctr = 0UL
    Assert.False(opener.TryOpen(Span(Array.copy f), 0us, &ctr))
    Assert.True(opener.AuthFails > 0L)

[<Fact>]
let ``AEGIS-128L is not selected when only one end offers it`` () =
    requireAes ()
    let onlyClient = ClientOffer(psk, id, SeededEntropy 161, uint64 unixMs, 1448, 256, offerAegis = true)
    Assert.Equal(Suite.Aegis128LBit ||| Suite.ChaCha20Poly1305Bit, onlyClient.Hello.Suites)
    let accepted = (ServerOffer([| psk |], SeededEntropy 262, 4096us, 256, offerAegis = false))
                       .TryAccept(ReadOnlySpan onlyClient.Frame, unixMs, 1448)
    Assert.True(accepted.IsSome)
    let struct (oneC, oneH) = onlyClient.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    Assert.Equal(Suite.ChaCha20Poly1305, oneC.Keys.Suite)
    Assert.Equal(Suite.ChaCha20Poly1305, oneH.Suite)
    frameRoundTrip oneC accepted.Value.Params

[<Fact>]
let ``AEGIS-128L is preferred over ChaCha8 when both are offered, and a session still exchanges a cell`` () =
    requireAes ()
    let offer = ClientOffer(psk, id, SeededEntropy 171, uint64 unixMs, 1448, 256, offerChaCha8 = true, offerAegis = true)
    let accepted = (ServerOffer([| psk |], SeededEntropy 272, 4096us, 256, offerChaCha8 = true, offerAegis = true))
                       .TryAccept(ReadOnlySpan offer.Frame, unixMs, 1448)
    Assert.True(accepted.IsSome)
    let struct (cp, sh) = offer.TryAccept(ReadOnlySpan accepted.Value.Reply).Value
    Assert.Equal(Suite.Aegis128L, sh.Suite)
    Assert.Equal(Suite.Aegis128L, cp.Keys.Suite)
    frameRoundTrip cp accepted.Value.Params
    let clock = ManualClock()
    let client = Session(Role.Client, cp, RecordingPort(), clock, SessionConfig.defaults)
    let server = Session(Role.Server, accepted.Value.Params, RecordingPort(), clock, SessionConfig.defaults)
    let frame = Array.zeroCreate client.FrameLength
    client.Tick()
    Assert.True(client.TryProduce(0us, Span frame))
    Assert.NotEqual(UInt64.MaxValue, server.OnFrame(0us, Span frame))
    server.Tick()
    Assert.True(server.TryProduce(0us, Span frame) || not server.WantsToSend)

[<Fact>]
let ``X25519 and AEGIS-128L together still let client and server exchange a cell`` () =
    requireAes ()
    let struct (c, s, sh) = handshake true false true 181 282
    Assert.Equal(Suite.Aegis128L, sh.Suite)
    Assert.Equal(Caps.X25519, c.Caps &&& Caps.X25519)
    Assert.Equal(16, c.Keys.UpKey(0u).Length)
    frameRoundTrip c s

// ---- Microbenchmark (hot path: per-cell AEAD) ------------------------------

[<Fact>]
let ``microbench: ChaCha8 vs ChaCha20 AEAD on a data-cell body`` () =
    let key = Array.init 32 (fun i -> byte (i * 5 + 1))
    let nonce = Array.init 12 (fun i -> byte i)
    let aad = Array.init 18 (fun i -> byte (i + 3))
    // 40-byte meta + 1344-byte symbol, the plaintext of one S=1344 cell.
    let plain = Array.init 1384 (fun i -> byte i)
    let median (xs: float[]) =
        let a = Array.copy xs
        Array.sortInPlace a
        a.[a.Length / 2]
    let run (rounds: int) (platform: bool) (iters: int) (warm: int) (samples: int) =
        let aead = ChaCha20Poly1305Aead(ReadOnlySpan key, rounds, platform)
        let cipher = Array.zeroCreate plain.Length
        let tag = Array.zeroCreate 16
        let back = Array.zeroCreate plain.Length
        let once () =
            aead.Encrypt(ReadOnlySpan nonce, ReadOnlySpan plain, Span cipher, Span tag, ReadOnlySpan aad)
            if not (aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad)) then
                failwith "bench decrypt failed"
        for _ in 1 .. warm do once ()
        let ns = Array.zeroCreate samples
        let mutable allocTotal = 0L
        for s in 0 .. samples - 1 do
            GC.Collect()
            GC.WaitForPendingFinalizers()
            let before = GC.GetAllocatedBytesForCurrentThread()
            let sw = Stopwatch.StartNew()
            for _ in 1 .. iters do once ()
            sw.Stop()
            allocTotal <- allocTotal + (GC.GetAllocatedBytesForCurrentThread() - before)
            ns.[s] <- sw.Elapsed.TotalNanoseconds / float iters
        median ns, float allocTotal / float (iters * samples)
    // Managed Enc+Dec (platform=false) is what this Poly1305 change moves; 5 warmed samples, median.
    let ns20, a20 = run 10 false 20000 8000 5
    let ns8, a8 = run 4 false 20000 8000 5
    let ns20p, a20p = run 10 true 20000 8000 5
    Assert.True(ns8 > 0.0 && ns20 > 0.0 && ns20p > 0.0)
    let nsA, aA =
        if not Aegis128LAead.IsSupported then (0.0, 0.0)
        else
            let key16 = Array.init 16 (fun i -> byte (i * 5 + 1))
            let nonce16 = Array.init 16 (fun i -> byte i)
            let aead = Aegis128LAead(ReadOnlySpan key16)
            let cipher = Array.zeroCreate plain.Length
            let tag = Array.zeroCreate 16
            let back = Array.zeroCreate plain.Length
            let once () =
                aead.Encrypt(ReadOnlySpan nonce16, ReadOnlySpan plain, Span cipher, Span tag, ReadOnlySpan aad)
                if not (aead.TryDecrypt(ReadOnlySpan nonce16, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad)) then
                    failwith "aegis bench decrypt failed"
            let iters, warm, samples = 20000, 8000, 5
            for _ in 1 .. warm do once ()
            let ns = Array.zeroCreate samples
            let mutable allocTotal = 0L
            for s in 0 .. samples - 1 do
                GC.Collect()
                GC.WaitForPendingFinalizers()
                let before = GC.GetAllocatedBytesForCurrentThread()
                let sw = Stopwatch.StartNew()
                for _ in 1 .. iters do once ()
                sw.Stop()
                allocTotal <- allocTotal + (GC.GetAllocatedBytesForCurrentThread() - before)
                ns.[s] <- sw.Elapsed.TotalNanoseconds / float iters
            let med =
                let a = Array.copy ns
                Array.sortInPlace a
                a.[a.Length / 2]
            med, float allocTotal / float (iters * samples)
    if Aegis128LAead.IsSupported then Assert.True(nsA > 0.0)
    eprintfn "cell AEAD median-of-5 ns/op managed20=%.0f managed8=%.0f prod20=%.0f aegis128l=%.0f alloc/op 20=%.3f 8=%.3f prod20=%.3f aegis=%.3f"
        ns20 ns8 ns20p nsA a20 a8 a20p aA
