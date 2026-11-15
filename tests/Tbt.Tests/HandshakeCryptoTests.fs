module Tbt.Tests.HandshakeCryptoTests

open System
open System.Diagnostics
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
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

let private handshake (x25519: bool) (chaCha8: bool) (seedC: int) (seedS: int) =
    let offer = ClientOffer(psk, id, SeededEntropy seedC, uint64 unixMs, 1448, 256,
                            offerX25519 = x25519, offerChaCha8 = chaCha8)
    let accepted = (ServerOffer([| psk |], SeededEntropy seedS, 4096us, 256,
                                offerX25519 = x25519, offerChaCha8 = chaCha8))
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
    let sealer = CellSealer(Direction.Up, cp.Symbol, table, ChaChaHeaderMask(ReadOnlySpan cp.Keys.HpUp, Suite.doubleRounds cp.Keys.Suite))
    let f = Array.zeroCreate (Layout.frameLength cp.Symbol)
    FrameMath.writeChunkHeader cp.Symbol (Span f)
    f.[f.Length - 2] <- 13uy
    f.[f.Length - 1] <- 10uy
    for i in 0 .. cp.Symbol - 1 do f.[Layout.SymbolOffset + i] <- byte (i * 3 + 1)
    sealer.Seal(Span f, 7UL, 0us)
    let rtable = KeyTable(sp.Keys.Suite)
    rtable.Install(0u, ReadOnlySpan(sp.Keys.UpKey 0u), 0UL)
    let opener = CellOpener(Direction.Up, sp.Symbol, rtable, ChaChaHeaderMask(ReadOnlySpan sp.Keys.HpUp, Suite.doubleRounds sp.Keys.Suite))
    let copy = Array.copy f
    let mutable c = 0UL
    Assert.True(opener.TryOpen(Span copy, 0us, &c))
    Assert.Equal(7UL, c)
    Assert.Equal(byte (0 * 3 + 1), copy.[Layout.SymbolOffset])

[<Fact>]
let ``default handshake still selects suite 0x02 and does not set X25519`` () =
    let struct (c, s, sh) = handshake false false 101 202
    Assert.Equal(Suite.ChaCha20Poly1305, c.Keys.Suite)
    Assert.Equal(Suite.ChaCha20Poly1305, sh.Suite)
    Assert.Equal(0us, c.Caps &&& Caps.X25519)
    Assert.Equal(0us, s.Caps &&& Caps.X25519)
    Assert.Equal(Suite.ChaCha20Poly1305Bit, (ClientOffer(psk, id, SeededEntropy 1, uint64 unixMs, 1448, 256)).Hello.Suites)
    frameRoundTrip c s

[<Fact>]
let ``X25519 decorator is negotiated only when both ends offer it, and the IKM changes`` () =
    let struct (bothC, bothS, sh) = handshake true false 111 222
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
    let struct (c, s, sh) = handshake false true 131 232
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
    let struct (c, s, sh) = handshake true true 141 242
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

// ---- Microbenchmark (hot path: per-cell AEAD) ------------------------------

[<Fact>]
let ``microbench: ChaCha8 vs ChaCha20 AEAD on a data-cell body`` () =
    let key = Array.init 32 (fun i -> byte (i * 5 + 1))
    let nonce = Array.init 12 (fun i -> byte i)
    let aad = Array.init 18 (fun i -> byte (i + 3))
    // 40-byte meta + 1344-byte symbol, the plaintext of one S=1344 cell.
    let plain = Array.init 1384 (fun i -> byte i)
    let run (rounds: int) (iters: int) =
        let aead = ChaCha20Poly1305Aead(ReadOnlySpan key, rounds)
        let cipher = Array.zeroCreate plain.Length
        let tag = Array.zeroCreate 16
        let back = Array.zeroCreate plain.Length
        aead.Encrypt(ReadOnlySpan nonce, ReadOnlySpan plain, Span cipher, Span tag, ReadOnlySpan aad)
        Assert.True(aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad))
        let sw = Stopwatch.StartNew()
        for _ in 1 .. iters do
            aead.Encrypt(ReadOnlySpan nonce, ReadOnlySpan plain, Span cipher, Span tag, ReadOnlySpan aad)
            if not (aead.TryDecrypt(ReadOnlySpan nonce, ReadOnlySpan cipher, ReadOnlySpan tag, Span back, ReadOnlySpan aad)) then
                failwith "bench decrypt failed"
        sw.Stop()
        sw.Elapsed.TotalNanoseconds / float iters
    // Warm both paths, then measure.
    run 10 64 |> ignore
    run 4 64 |> ignore
    let ns20 = run 10 8000
    let ns8 = run 4 8000
    Assert.True(ns8 > 0.0 && ns20 > 0.0)
    // Recorded for the report; we do not fail the suite on the ratio.
    eprintfn "cell AEAD ns/op ChaCha20=%.0f ChaCha8=%.0f ratio=%.2f" ns20 ns8 (ns20 / ns8)
