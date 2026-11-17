/// Opt-in runner-only, single-event-loop lifecycle recorder. No production hooks.
namespace Tbt.ArrivalShadowRunner

open System
open System.IO
open Tbt.Session

[<Struct>]
type LifecycleEvent =
    { Name:string; MonotonicNs:int64; Slot:int; Generation:uint32
      WrittenBytes:int64; TotalWrittenBytes:int64; Active:int }

/// Caller constructs this only when explicitly requested. No per-Write clock reads.
/// Written bytes mean successful local socket acceptance, NOT peer application consumption.
/// FlowBridge.CloseAll bypasses IFlowPort: caller must bracket carrier.Close with Record.
[<Sealed>]
type LifecyclePort(inner:IFlowPort, capacity:int, ?nowNs:unit->int64) =
    let clock=defaultArg nowNs Monotonic.nowNs
    do if capacity<1 || capacity>16384 then invalidArg "capacity" "range 1..16384"
    let events=Array.zeroCreate<LifecycleEvent> capacity
    let written=Array.zeroCreate<int64> 256
    let generations=Array.zeroCreate<uint32> 256
    let mutable count=0
    let mutable dropped=0L
    let mutable errors=0L
    let mutable total=0L
    let mutable calls=0L
    let validSlot slot = slot>=0 && slot<written.Length
    let record name slot active =
        // Diagnostics must never mask a product return or original exception.
        try
            if count>=events.Length then dropped<-dropped+1L
            else
                let stamp=clock()
                if stamp<0L || (count>0 && stamp<events.[count-1].MonotonicNs) then errors<-errors+1L
                else
                    events.[count]<-
                        {Name=name;MonotonicNs=stamp;Slot=slot;Generation=if validSlot slot then generations.[slot] else 0u
                         WrittenBytes=if validSlot slot then written.[slot] else 0L
                         TotalWrittenBytes=total;Active=active}
                    count<-count+1
        with _ -> errors<-errors+1L
    let around name slot action =
        record (name+"_before") slot -1
        try
            action()
            record (name+"_after") slot -1
        with _ ->
            record (name+"_throw") slot -1
            reraise()
    member _.Record(name:string,active:int) = record name -1 active
    member _.Recorded=count
    member _.Dropped=dropped
    member _.Errors=errors
    member _.Complete=dropped=0L && errors=0L
    member _.TotalWrittenBytes=total
    member _.WriteCalls=calls
    member _.Events=events.[0..count-1]
    member _.WriteCsv(path:string) =
        try
            use writer=new StreamWriter(path,false)
            writer.WriteLine("name,monotonic_ns,slot,generation,written_bytes,total_written_bytes,active")
            for i in 0..count-1 do
                let e=events.[i]
                let name="\""+e.Name.Replace("\"","\"\"")+"\""
                writer.WriteLine(String.Join(",",[|name;string e.MonotonicNs;string e.Slot;string e.Generation;string e.WrittenBytes;string e.TotalWrittenBytes;string e.Active|]))
        with _ -> errors<-errors+1L
    interface IFlowPort with
        member _.Open(slot,gen) =
            if validSlot slot then
                generations.[slot]<-gen
                written.[slot]<-0L
            else errors<-errors+1L
            around "open" slot (fun ()->inner.Open(slot,gen))
        member _.Write(slot,data) =
            let n=inner.Write(slot,data)
            // Return unchanged even when diagnostics cannot represent its counters.
            if calls<Int64.MaxValue then calls<-calls+1L else errors<-errors+1L
            if n>0 then
                if validSlot slot && int64 n<=Int64.MaxValue-written.[slot] && int64 n<=Int64.MaxValue-total then
                    written.[slot]<-written.[slot]+int64 n
                    total<-total+int64 n
                else errors<-errors+1L
            n
        member _.Finish slot=around "finish" slot (fun ()->inner.Finish slot)
        member _.Reset slot=around "reset" slot (fun ()->inner.Reset slot)
        member _.Close slot=around "close" slot (fun ()->inner.Close slot)
        member _.Credit slot=inner.Credit slot
