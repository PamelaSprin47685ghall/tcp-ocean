module Tbt.Tests.CliTests

open System
open Xunit
open Tbt.Crypto.Bootstrap
open Tbt.Cli.Common

[<Fact>]
let ``omitting --suites and --x25519 keeps the historical default offer`` () =
    let o = Options([| "--psk-file"; "x"; "--paths"; "4" |])
    let struct (aegis, chacha8, chacha20, x25519) = offerFromOptions o
    Assert.False(aegis)
    Assert.False(chacha8)
    Assert.True(chacha20)
    Assert.False(x25519)
    Assert.Equal(Suite.ChaCha20Poly1305Bit, Suite.advertisedBits chacha8 aegis chacha20)

[<Fact>]
let ``--suites lists the offered suites and --x25519 is a bare flag`` () =
    let o = Options([| "--suites"; "aegis,chacha8,chacha20"; "--x25519" |])
    let struct (aegis, chacha8, chacha20, x25519) = offerFromOptions o
    Assert.True(aegis)
    Assert.True(chacha8)
    Assert.True(chacha20)
    Assert.True(x25519)
    let off = Options([| "--suites"; "chacha20"; "--x25519"; "0" |])
    let struct (_, _, c20, x) = offerFromOptions off
    Assert.True(c20)
    Assert.False(x)

[<Fact>]
let ``describeOffer formats the negotiated suite and X25519 cap`` () =
    Assert.Equal("suite=0x02(chacha20) x25519=0", describeOffer Suite.ChaCha20Poly1305 0us)
    Assert.Equal("suite=0x01(aegis) x25519=1", describeOffer Suite.Aegis128L Caps.X25519)
