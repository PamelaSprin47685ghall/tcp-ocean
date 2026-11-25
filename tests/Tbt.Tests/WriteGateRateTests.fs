module Tbt.Tests.WriteGateRateTests

open System
open Xunit
open Tbt.Carrier

[<Fact>]
let ``a slow sampled path keeps a 40 ms queue, not a 64 KiB floor`` () =
    // 131 KiB/s * 40 ms = 5240 B, raised to the 6 KiB floor.
    let struct (pass1, pass2) = WriteGate.rateAwareLimits 262144 true 131_072L 0
    Assert.Equal(4096, pass1)
    Assert.Equal(6144, pass2)
    Assert.True(pass2 <= 8192)
    Assert.True(WriteGate.admitsWith pass2 2 6144)
    Assert.False(WriteGate.admitsWith pass2 2 6145)

[<Fact>]
let ``a fast sampled path opens toward its 40 ms budget and stays capped`` () =
    // 1.3 MiB/s * 40 ms is about 54 KiB; well above the old slow-path floor and below 128 KiB.
    let struct (pass1, pass2) = WriteGate.rateAwareLimits 262144 true (1_300_000L * 1024L / 1000L) 0
    Assert.True(pass1 > 4096 && pass1 <= 16384)
    Assert.True(pass2 >= 52_000 && pass2 <= 131072)
    let struct (_, huge) = WriteGate.rateAwareLimits 262144 true Int64.MaxValue 0
    Assert.Equal(131072, huge)

[<Fact>]
let ``recovery tightens the gate and a missing sample stays a probe`` () =
    let struct (_, recovering) = WriteGate.rateAwareLimits 262144 true 2_000_000L 3
    Assert.True(recovering <= 8192)
    let struct (cold1, cold2) = WriteGate.rateAwareLimits 262144 false 0L 0
    Assert.Equal(SocketTuning.GatePass1Limit, cold1)
    Assert.Equal(16384, cold2)
    let struct (_, zeroRate) = WriteGate.rateAwareLimits 8192 true 0L 0
    Assert.Equal(8192, zeroRate)

[<Fact>]
let ``configured ceiling and integer overflow cannot widen or wrap the gate`` () =
    let struct (_, capped) = WriteGate.rateAwareLimits 8192 true 2_000_000L 0
    Assert.Equal(8192, capped)
    let struct (pass1, pass2) = WriteGate.rateAwareLimits Int32.MaxValue true Int64.MaxValue 0
    Assert.True(pass1 > 0 && pass2 > 0)
    Assert.Equal(16384, pass1)
    Assert.Equal(131072, pass2)
