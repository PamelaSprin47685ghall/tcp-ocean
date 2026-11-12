module Tbt.Tests.CryptoTests

open System
open System.Buffers.Binary
open Xunit
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
open Tbt.Wire
open Tbt.Tests.Vectors

[<Fact>]
let ``ChaCha20 block matches RFC 8439 section 2.3.2`` () =
    let key = Array.init 32 byte
    let nonce = hex "000000090000004a00000000"
    let out = Array.zeroCreate<byte> 64
    ChaCha20.block (ChaCha20.keyWords (ReadOnlySpan key)) 1u (ReadOnlySpan nonce) (Span out)
    Assert.Equal("10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e"
                 + "d2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e", toHex (ReadOnlySpan out))

[<Fact>]
let ``ChaCha20 header mask matches RFC 9001 appendix A.5`` () =
    let m = ChaChaHeaderMask(ReadOnlySpan(hex "25a282b9e82f06f21f488917a4fc8f1b73573685608597d0efcb076b0ab7a7a4")) :> IHeaderMask
    let out = Array.zeroCreate<byte> 8
    m.Compute(ReadOnlySpan(hex "5e5cd55c41f69080575d7999c25a5bfb"), Span out)
    Assert.Equal("aefefe7d03", toHex (ReadOnlySpan out.[.. 4]))

[<Fact>]
let ``replay window accepts once, keeps 2^20 history and only moves on commit`` () =
    let w = ReplayWindow()
    Assert.True(w.Check 10UL)
    w.Commit 10UL
    Assert.False(w.Check 10UL)
    Assert.True(w.Check 9UL)
    Assert.True(w.Check 5_000_000UL)   // check alone moves nothing
    Assert.True(w.Check 9UL)
    w.Commit(10UL + uint64 ReplayWindow.Size - 1UL)
    Assert.False(w.Check 9UL)          // left of the window: too old
    Assert.False(w.Check 10UL)
    Assert.True(w.Check 11UL)

let private hs = lazy handshake.Value
let private hx (k: string) = hex (str hs.Value k)

let private serverHello () =
    { Status = ServerStatus.Ok; Suite = Suite.ChaCha20Poly1305; Caps = 0x0705us
      SessionId = Guid(ReadOnlySpan(hx "session_id_hex"), true); Symbol = 1344us; PathId = 0us
      MaxPaths = 256us; RxWinGrant0 = 4096us; EchoTimeMs = 1_800_000_000_000UL; Cookie = UInt128.Zero }

let private clientHello () =
    { Version = 2uy; Suites = Suite.ChaCha20Poly1305Bit; Caps = 0x0705us
      ClientId = Guid(ReadOnlySpan(hx "client_id_hex"), true); UnixTimeMs = 1_800_000_000_000UL
      MssClient = 1448us; MaxPaths = 256us }

[<Fact>]
let ``CLIENT_INIT and SERVER_INIT match the independent reference`` () =
    let psk = ReadOnlySpan(hx "psk_hex")
    let cn = hx "client_nonce_hex"
    let ci = ClientInit.build psk (ReadOnlySpan cn) (clientHello ()) ReadOnlySpan<byte>.Empty (ReadOnlySpan(Array.init 944 (fun i -> byte (7 * i))))
    Assert.Equal(str hs.Value "client_init_hex", toHex (ReadOnlySpan ci))
    let si = ServerInit.build psk (ReadOnlySpan cn) (ReadOnlySpan(hx "server_nonce_hex")) (serverHello ()) ReadOnlySpan<byte>.Empty (ReadOnlySpan(Array.init 948 (fun i -> byte (11 * i))))
    Assert.Equal(str hs.Value "server_init_hex", toHex (ReadOnlySpan si))
    match ClientInit.tryOpen psk (ReadOnlySpan ci) with
    | ValueSome(hello, _) -> Assert.Equal(clientHello (), hello)
    | ValueNone -> failwith "CLIENT_INIT did not open"
    Assert.Equal(ValueSome(serverHello ()), ServerInit.tryOpen psk (ReadOnlySpan cn) (ReadOnlySpan si))
    // A wrong PSK or one flipped bit fails closed.
    Assert.True((ClientInit.tryOpen (ReadOnlySpan(Array.zeroCreate 32)) (ReadOnlySpan ci)).IsNone)
    ci.[700] <- ci.[700] ^^^ 1uy
    Assert.True((ClientInit.tryOpen psk (ReadOnlySpan ci)).IsNone)

[<Fact>]
let ``a cookie retry keeps the frozen ciphertext`` () =
    let psk = ReadOnlySpan(hx "psk_hex")
    let ci = hx "client_init_hex"
    let retry = ClientInit.withCookie (ReadOnlySpan ci) (ReadOnlySpan(Array.create 16 0xEEuy))
    Assert.True((ReadOnlySpan retry).Slice(56).SequenceEqual((ReadOnlySpan ci).Slice 56))
    Assert.True((ClientInit.tryOpen psk (ReadOnlySpan retry)).IsSome)
    Assert.Equal("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", toHex (ClientInit.cookie (ReadOnlySpan retry)))

let private keys () =
    SessionKeys(hx "psk_hex", hx "client_nonce_hex", hx "server_nonce_hex", Guid(ReadOnlySpan(hx "session_id_hex"), true),
                Guid(ReadOnlySpan(hx "client_id_hex"), true), Suite.ChaCha20Poly1305, 1415, hx "client_init_hex", hx "server_init_hex")

[<Fact>]
let ``session key schedule matches the independent reference`` () =
    let k = keys ()
    Assert.Equal(str hs.Value "th_hex", toHex (ReadOnlySpan k.TranscriptHash))
    Assert.Equal(str hs.Value "k_up0_hex", toHex (ReadOnlySpan(k.UpKey 0u)))
    Assert.Equal(str hs.Value "k_down0_hex", toHex (ReadOnlySpan(k.DownKey 0u)))
    Assert.Equal(str hs.Value "hp_up_hex", toHex (ReadOnlySpan k.HpUp))
    Assert.Equal(str hs.Value "hp_down_hex", toHex (ReadOnlySpan k.HpDown))
    Assert.Equal(str hs.Value "graph_seed_up", $"0x{k.GraphSeedUp:x16}")
    Assert.Equal(str hs.Value "graph_seed_down", $"0x{k.GraphSeedDown:x16}")
    Assert.Equal(str hs.Value "k_path_hex", toHex (ReadOnlySpan k.PathKey))
    Assert.Equal(str hs.Value "k_up1_hex", toHex (ReadOnlySpan(k.UpKey 1u)))

let private makeFrame () =
    let f = Array.zeroCreate<byte> 1415
    FrameMath.writeChunkHeader 1344 (Span f)
    (hex (str hs.Value "frame_meta_hex")).CopyTo(f, Layout.MetaOffset)
    for i in 0 .. 1343 do f.[Layout.SymbolOffset + i] <- byte ((i * 13 + 5) % 256)
    f.[1413] <- 13uy
    f.[1414] <- 10uy
    f

[<Fact>]
let ``a sealed data frame matches the independent reference and opens once`` () =
    let k = keys ()
    use table = new KeyTable()
    table.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    let sealer = CellSealer(Direction.Up, 1344, table, ChaChaHeaderMask(ReadOnlySpan k.HpUp))
    let f = makeFrame ()
    let counter = Convert.ToUInt64(str hs.Value "frame_counter", 16)
    sealer.Seal(Span f, counter, 7us)
    Assert.Equal(str hs.Value "frame_hex", toHex (ReadOnlySpan f))
    use rtable = new KeyTable()
    rtable.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    let opener = CellOpener(Direction.Up, 1344, rtable, ChaChaHeaderMask(ReadOnlySpan k.HpUp))
    let copy = Array.copy f
    let mutable c = 0UL
    Assert.True(opener.TryOpen(Span f, 7us, &c))
    Assert.Equal(counter, c)
    Assert.True((ReadOnlySpan f).Slice(Layout.MetaOffset, 40).SequenceEqual(ReadOnlySpan(hex (str hs.Value "frame_meta_hex"))))
    // The same bytes again are a replay; a frame moved to another path fails the tag.
    Assert.False(opener.TryOpen(Span(Array.copy copy), 7us, &c))
    Assert.Equal(1L, opener.ReplayDrops)

[<Fact>]
let ``a frame on a wrong path or with a flipped bit fails and leaves the window unchanged`` () =
    let k = keys ()
    use t1 = new KeyTable()
    t1.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    use t2 = new KeyTable()
    t2.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    let sealer = CellSealer(Direction.Up, 1344, t1, NoHeaderMask())
    let opener = CellOpener(Direction.Up, 1344, t2, NoHeaderMask())
    let f = makeFrame ()
    sealer.Seal(Span f, 42UL, 3us)
    Assert.Equal(42UL, BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan(f, 5, 8)))   // HP off: clear
    let mutable c = 0UL
    Assert.False(opener.TryOpen(Span(Array.copy f), 4us, &c))
    let bad = Array.copy f
    bad.[600] <- bad.[600] ^^^ 1uy
    Assert.False(opener.TryOpen(Span bad, 3us, &c))
    Assert.Equal(2L, opener.AuthFails)
    Assert.True(opener.TryOpen(Span f, 3us, &c))

[<Fact>]
let ``key table selects one generation per counter interval`` () =
    let k = keys ()
    use send = new KeyTable()
    send.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    use recv = new KeyTable()
    recv.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    recv.Install(1u, ReadOnlySpan(k.UpKey 1u), 5000UL)
    send.Install(1u, ReadOnlySpan(k.UpKey 1u), 5000UL)
    let sealer = CellSealer(Direction.Up, 1344, send, NoHeaderMask())
    let opener = CellOpener(Direction.Up, 1344, recv, NoHeaderMask())
    let mutable c = 0UL
    for counter in [ 4999UL; 5000UL ] do
        let f = makeFrame ()
        sealer.Seal(Span f, counter, 1us)
        Assert.True(opener.TryOpen(Span f, 1us, &c))
    recv.RetireOldest()
    let late = makeFrame ()
    sealer.Seal(Span late, 4998UL, 1us)
    Assert.False(opener.TryOpen(Span late, 1us, &c))   // old interval removed: no trial decryption
    Assert.Throws<ArgumentException>(fun () -> recv.Install(5u, ReadOnlySpan(k.UpKey 1u), 9000UL)) |> ignore

[<Fact>]
let ``JOIN and JOIN_ACK match the JOINACK2 vectors byte for byte`` () =
    let v = joinAck.Value
    let kPath = ReadOnlySpan(hex (str v "key_path_hex"))
    let nonce = ReadOnlySpan(hex (str v "join_nonce_hex"))
    let req: Join.JoinRequest = { PathId = 256us; Incarnation = 7u; MssClient = 1460us; Role = Join.JoinRole.Lane }
    let wire = Join.buildRequest kPath nonce req (ReadOnlySpan(Array.init 1031 byte))
    Assert.Equal(str v "join_wire_hex", toHex (ReadOnlySpan wire))
    Assert.Equal(ValueSome req, Join.tryOpenRequest kPath (ReadOnlySpan wire))
    let jth = Join.transcriptHash (ReadOnlySpan wire)
    Assert.Equal(str v "jth_hex", toHex (ReadOnlySpan jth))
    let ack: Join.JoinAck =
        { Status = Join.JoinStatus.Accept; Role = Join.JoinRole.Lane; Suite = 2uy
          SessionId = Guid(ReadOnlySpan(hex (str v "session_id_hex")), true); PathId = 256us; Incarnation = 7u
          Symbol = 1344us; MaxPaths = 288us; Caps = 0x070dus; MssClient = 1460us; MssServer = 1440us }
    let ackWire = Join.buildAck kPath nonce (ReadOnlySpan jth) ack (ReadOnlySpan(Array.init 968 (fun i -> byte (0x55 + i))))
    Assert.Equal(str v "ack_wire_hex", toHex (ReadOnlySpan ackWire))
    Assert.Equal(ValueSome ack, Join.tryOpenAck kPath nonce (ReadOnlySpan jth) (ReadOnlySpan ackWire))
    // An ACK for another request transcript is rejected.
    let otherJth = Array.copy jth
    otherJth.[0] <- otherJth.[0] ^^^ 1uy
    Assert.True((Join.tryOpenAck kPath nonce (ReadOnlySpan otherJth) (ReadOnlySpan ackWire)).IsNone)
    Assert.True((Join.tryOpenRequest (ReadOnlySpan(Array.zeroCreate 32)) (ReadOnlySpan wire)).IsNone)

[<Fact>]
let ``seal, open and parity render allocate nothing in steady state`` () =
    let k = keys ()
    use t1 = new KeyTable()
    t1.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    use t2 = new KeyTable()
    t2.Install(0u, ReadOnlySpan(k.UpKey 0u), 0UL)
    let sealer = CellSealer(Direction.Up, 1344, t1, ChaChaHeaderMask(ReadOnlySpan k.HpUp))
    let opener = CellOpener(Direction.Up, 1344, t2, ChaChaHeaderMask(ReadOnlySpan k.HpUp))
    let enc = Tbt.Codec.Encoder(k.GraphSeedUp, 1344, 512)
    let mutable sym = Span<byte>.Empty
    for t in 0 .. 299 do
        for slot in 0 .. 15 do
            if enc.TryGetFillSlot(slot, &sym) then sym.Fill(byte (t + slot))
        enc.Seal 16 |> ignore
    let f = makeFrame ()
    let mutable c = 0UL
    let mutable before = 0L
    for first in [| 0UL; 1000UL |] do   // round 1 warms up JIT and pools; round 2 is measured
        before <- GC.GetAllocatedBytesForCurrentThread()
        for i in 0UL .. 199UL do
            enc.TryRenderParity(299UL, int (i % 48UL), CodeField.Gf16, false, Span(f, Layout.SymbolOffset, 1344)) |> ignore
            sealer.Seal(Span f, first + i, 1us)
            opener.TryOpen(Span f, 1us, &c) |> ignore
    Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before)
    Assert.Equal(1199UL, c)
