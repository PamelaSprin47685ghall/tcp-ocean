module Tbt.Tests.WindowBridgeTests

open System
open System.Diagnostics
open Xunit
open Tbt.Codec
open Tbt.Session
open Tbt.Wire

let private fill (size: int) (seed: int) =
    Array.init size (fun j -> byte ((seed * 17 + j * 3) &&& 255))

[<Fact>]
let ``xor path matches the scalar oracle on aligned and odd lengths`` () =
    for size in [| 1; 15; 16; 31; 32; 33; 1344; 1345 |] do
        let a = fill size (size + 1)
        let b = fill size (size + 9)
        let simd = Array.copy a
        let scalar = Array.copy a
        WindowCodec.xorInto (ReadOnlySpan b) (Span simd)
        WindowCodec.xorIntoScalar (ReadOnlySpan b) (Span scalar)
        Assert.Equal<byte[]>(scalar, simd)

[<Fact>]
let ``step and slot map onto one 64-symbol window and back`` () =
    for step in 8UL .. 11UL do
        for slot = 0 to 15 do
            match WindowBridge.tryIndex (step, slot) with
            | ValueSome index ->
                Assert.Equal(2UL, WindowBridge.windowId step)
                let struct (off, back) = WindowBridge.stepSlot index
                Assert.Equal(int (step - WindowBridge.firstStep 2UL), off)
                Assert.Equal(slot, back)
            | ValueNone -> failwith "in-range slot rejected"
    Assert.True(WindowBridge.tryIndex(4UL, 16) |> ValueOption.isNone)
    Assert.Equal(48, WindowBridge.tailLength 3 0)
    Assert.Equal(50, WindowBridge.tailLength 3 2)
    Assert.Equal(64, WindowBridge.tailLength 4 0)

[<Fact>]
let ``parity meta round-trips and an out-of-order source recovers across the window`` () =
    let size = 32
    let tail = 50
    let rx = WindowReceive(size, 8, 1L <<< 20)
    let sources = Array.init tail (fun i -> fill size (i + 3))
    for delta = 2 downto 0 do
        let step = 8UL + uint64 delta
        for slot = 15 downto 0 do
            let index = int (step - 8UL) * 16 + slot
            if index < tail && index % 7 <> 0 then
                Assert.True(rx.AddSource(step, slot, tail, sources.[index]))
    let mutable restored = false
    let mutable p = 0
    while not restored && p < 48 do
        let mask = WindowCodec.WindowSessionCodec.parityMask 2UL p tail
        let mutable covers = false
        for idx = 0 to tail - 1 do
            if idx % 7 = 0 && mask &&& (1UL <<< idx) <> 0UL then covers <- true
        if covers then
            let parity = { WindowId = 2UL; FirstStep = 8UL; ParityIdx = p; Tail = tail }
            let meta = WindowBridge.parityMeta parity
            let buf = Array.zeroCreate 40
            Meta.write meta (Span buf)
            let mutable back = Unchecked.defaultof<Meta>
            Assert.True(Meta.tryRead (ReadOnlySpan buf) &back)
            match WindowBridge.tryParity back with
            | ValueSome got -> Assert.Equal(parity, got)
            | ValueNone -> failwith "parity meta rejected"
            let symbol = WindowCodec.WindowSessionCodec.encodeParity 2UL p (Array.sub sources 0 tail)
            rx.AddParity(back, symbol) |> ignore
            match rx.Pool.TryGet 2UL with
            | ValueSome decoder ->
                for idx = 0 to tail - 1 do
                    if idx % 7 = 0 then
                        match decoder.TryGet idx with
                        | Some actual ->
                            Assert.Equal<byte[]>(sources.[idx], actual)
                            restored <- true
                        | None -> ()
            | ValueNone -> failwith "window missing"
        p <- p + 1
    Assert.True(restored)
    rx.ReleaseThrough 12UL
    Assert.True(rx.Pool.TryGet 2UL |> ValueOption.isNone)
    Assert.Equal(0L, rx.AllocatedBytes)

[<Fact>]
let ``window xor encode is cheaper and smaller than a dense gf16 row for the same symbol`` () =
    let size = 1344
    let count = 64
    let src = Array.init count (fun i -> fill size (i + 1))
    let mask = WindowCodec.coefficients count 4 99UL
    let rounds = 40
    let watch = Stopwatch.StartNew()
    let mutable acc = 0
    for _ = 1 to rounds do
        let encoded = WindowCodec.encode src mask
        acc <- acc + int encoded.[0]
    let windowTicks = watch.ElapsedTicks
    watch.Restart()
    let dst = Array.zeroCreate size
    for _ = 1 to rounds do
        Array.Clear dst
        for i = 0 to count - 1 do
            Gf16.mulAddInto 7uy (ReadOnlySpan src.[i]) (Span dst)
        acc <- acc + int dst.[0]
    let gf16Ticks = watch.ElapsedTicks
    Assert.True(windowTicks * 2L < gf16Ticks, sprintf "window %d gf16 %d" windowTicks gf16Ticks)
    let pool = WindowCodec.WindowCodecPool(size, 4, WindowCodec.WindowSessionCodec.MaxDecoderBytes)
    Assert.True(pool.TryOpen(0UL, count))
    Assert.True(pool.AllocatedBytes < (16L <<< 20))
    Assert.True(int64 (1024 * 16 * size) > 8L * pool.AllocatedBytes)
    Assert.NotEqual(0, acc)
