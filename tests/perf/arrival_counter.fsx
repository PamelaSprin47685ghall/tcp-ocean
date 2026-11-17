// Run after a Release build, passing the matching test output directory via
// dotnet fsi --lib:<output> tests/perf/arrival_counter.fsx [samples]
// Component work only: no receiver, network, authentication of feedback,
// scheduling, or throughput acceptance. Warm-up rows are retained separately.
#r "Tbt.Contracts.dll"
#r "Tbt.Codec.dll"
#r "Tbt.Wire.dll"
#r "Tbt.Kernels.dll"
#r "Tbt.Crypto.dll"
#r "Tbt.Session.dll"

open System
open System.Diagnostics
open Tbt.Contracts
open Tbt.Codec
open Tbt.Crypto
open Tbt.Wire
open Tbt.Session

let samples = if fsi.CommandLineArgs.Length>1 then int fsi.CommandLineArgs.[1] else 7
let run size mode =
    let clock =
        {new IClock with
            member _.NowNs()=1_000_000_000L
            member _.FreshNs()=1_000_000_000L}
    let keys=KeyTable()
    keys.Install(0u,ReadOnlySpan(Array.create 32 17uy),0UL)
    let sealer=CellSealer(Direction.Up,32,keys,NoHeaderMask())
    let encoder=Encoder(73UL,32,max 512 size)
    let tx=TxHalf(Direction.Up,32,CodeField.Gf16,encoder,sealer,0.25,1,100_000,clock)
    tx.InitialGrant 100_000UL
    for _ in 1..size do
        let mutable dst=Span<byte>.Empty
        if not(tx.TryBeginSource(&dst)) then failwith "source blocked"
        tx.CommitSource({RealLength=0;FlowSlot=0uy;Flags=StreamFlags.None;FlowGeneration=1u;StreamOffset=0UL}) |> ignore
        tx.Flush()
    let frame=Array.zeroCreate<byte>(Layout.frameLength 32)
    let render () =
        let mutable n=0
        while tx.TryRenderNext(Span frame,0us,0UL,512us,0us,allowRepair=false) do n<-n+1
        if n<>size*3 then failwithf "unexpected frame count %d" n
    if mode<>"render" then render()
    if mode="ack" then tx.OnPeerSeen(uint64(size-1))
    let before=GC.GetAllocatedBytesForCurrentThread()
    let start=Stopwatch.GetTimestamp()
    match mode with
    | "seen" -> for i in 1..size-1 do tx.OnPeerSeen(uint64 i)
    | "ack" -> for i in 1..size do tx.OnPeerAck(uint64 i,512us)
    | _ -> render()
    let elapsed=Stopwatch.GetTimestamp()-start
    let allocated=GC.GetAllocatedBytesForCurrentThread()-before
    if tx.UniqueRendered<>int64(size*3) then failwith "render accounting"
    if mode<>"render" && tx.UniqueArrived<>int64(size*3) then failwith "arrival accounting"
    elapsed,allocated

printfn "phase,size,mode,sample,ticks,frequency,allocated_bytes"
for size in [128;512;2048;4096] do
    for mode in ["seen";"ack";"render"] do
        for sample in 0..samples do
            let ticks,allocated=run size mode
            printfn "%s,%d,%s,%d,%d,%d,%d" (if sample=0 then "warmup" else "sample") size mode sample ticks Stopwatch.Frequency allocated
