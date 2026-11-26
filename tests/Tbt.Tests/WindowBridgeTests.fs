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
let ``sealed snapshots preserve sparse slots even when a step is wholly lost`` () =
    let struct (tail, history, mask) = WindowBridge.sealSnapshot [| 1; 16; 3 |]
    Assert.Equal(35us, tail)
    Assert.Equal(0x2F0u, history)
    Assert.Equal(1UL ||| (0xFFFFUL <<< 16) ||| (7UL <<< 32), mask)
    // No DATA from the second step is needed to reconstruct its active slots.
    Assert.Equal(ValueSome mask, WindowBridge.trySnapshot tail history)
    for lengths in [| [|1|]; [|16|]; [|1; 1|]; [|16; 16; 16; 16|]; [|3; 1; 16; 7|] |] do
        let struct (t, h, expected) = WindowBridge.sealSnapshot lengths
        Assert.Equal(ValueSome expected, WindowBridge.trySnapshot t h)

[<Fact>]
let ``sealed snapshots reject malformed boundary and unused bits`` () =
    Assert.True(WindowBridge.trySnapshot 0us 0u |> ValueOption.isNone)
    Assert.True(WindowBridge.trySnapshot 65us 0u |> ValueOption.isNone)
    Assert.True(WindowBridge.trySnapshot 1us 1u |> ValueOption.isNone)
    Assert.True(WindowBridge.trySnapshot 1us 0x10u |> ValueOption.isNone)
    Assert.True(WindowBridge.trySnapshot 1us 0x10000u |> ValueOption.isNone)
    Assert.Throws<ArgumentException>(fun () -> WindowBridge.sealSnapshot [||] |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> WindowBridge.sealSnapshot [|1; 0|] |> ignore) |> ignore
    Assert.Throws<ArgumentException>(fun () -> WindowBridge.sealSnapshot [|17|] |> ignore) |> ignore

[<Fact>]
let ``snapshot parity derives identical masks from authenticated sparse metadata`` () =
    for lengths in [| [|1; 16; 3|]; [|16; 16; 16; 16|]; [|1|] |] do
        for idx in [|0; 7; 47|] do
            let struct (meta, mask) = WindowBridge.snapshotParity 2UL idx lengths
            let struct (_, _, active) = WindowBridge.sealSnapshot lengths
            Assert.NotEqual(0UL, mask)
            Assert.Equal(0UL, mask &&& ~~~active)
            let buf = Array.zeroCreate 40
            Meta.write meta (Span buf)
            let mutable parsed = Unchecked.defaultof<Meta>
            Assert.True(Meta.tryRead (ReadOnlySpan buf) &parsed)
            Assert.Equal(ValueSome mask, WindowBridge.trySnapshotParityMask parsed)
            Assert.Equal(ValueSome mask, WindowBridge.trySnapshotParityMask meta)
    let struct (oldMeta, oldMask) = WindowBridge.snapshotParity 2UL 7 [|1; 16|]
    let struct (newMeta, _) = WindowBridge.snapshotParity 2UL 7 [|1; 16; 3|]
    Assert.Equal(ValueSome oldMask, WindowBridge.trySnapshotParityMask oldMeta)
    Assert.NotEqual(oldMeta.Aux, newMeta.Aux)
    Assert.True(WindowBridge.trySnapshotParityMask { oldMeta with KHist = oldMeta.KHist ||| 0x10000u } |> ValueOption.isNone)
    Assert.True(WindowBridge.trySnapshotParityMask { oldMeta with Slot = 48uy } |> ValueOption.isNone)
    Assert.True(WindowBridge.trySnapshotParityMask { oldMeta with StepId = 9UL } |> ValueOption.isNone)
    Assert.True(WindowBridge.trySnapshotParityMask { oldMeta with Aux = 0us } |> ValueOption.isNone)

[<Fact>]
let ``snapshot tracker handles reordering duplicates and atomic conflicts`` () =
    let tracker = WindowSnapshotTracker(2UL)
    let struct (short, _) = WindowBridge.snapshotParity 2UL 0 [| 1; 16 |]
    let struct (long, _) = WindowBridge.snapshotParity 2UL 1 [| 1; 16; 3; 7 |]
    let struct (bad, _) = WindowBridge.snapshotParity 2UL 2 [| 1; 15; 3 |]
    Assert.True(tracker.TryData(10UL, 2, 3))
    Assert.True(tracker.TrySnapshot short)
    Assert.True(tracker.TrySnapshot long)
    Assert.True(tracker.TrySnapshot short)
    Assert.True(tracker.TrySnapshot long)
    Assert.False(tracker.TrySnapshot bad)
    Assert.Equal(ValueSome 16, tracker.StepUsed 1)
    Assert.Equal(ValueSome 7, tracker.StepUsed 3)
    Assert.False(tracker.TryData(11UL, 0, 8))
    Assert.False(tracker.TryData(12UL, 0, 1))
    Assert.False(tracker.TryData(8UL, -1, 1))
    Assert.Equal(ValueSome 1, tracker.StepUsed 0)
    let fresh = WindowSnapshotTracker(2UL)
    Assert.True(fresh.TryData(10UL, 0, 4))
    Assert.False(fresh.TrySnapshot long)
    Assert.Equal(ValueNone, fresh.StepUsed 0)
    Assert.Equal(ValueSome 4, fresh.StepUsed 2)
    Assert.False(fresh.TrySnapshot { long with KHist = long.KHist ||| 0x10000u })
    Assert.Equal(ValueNone, fresh.StepUsed 1)

[<Fact>]
let ``partial step acknowledgement retains the active window`` () =
    let rx = WindowReceive(32, 8, 1L <<< 20)
    let original = fill 32 4
    Assert.True(rx.AddSource(0UL, 0, 64, original))
    for ack in 1UL .. 3UL do
        rx.ReleaseThrough ack
        match rx.Pool.TryGet 0UL with
        | ValueSome decoder -> Assert.Equal<byte[]>(original, decoder.TryGet(0).Value)
        | ValueNone -> failwith "active window was released"
    rx.ReleaseThrough 4UL
    Assert.True(rx.Pool.TryGet 0UL |> ValueOption.isNone)
    Assert.Equal(0L, rx.AllocatedBytes)

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
