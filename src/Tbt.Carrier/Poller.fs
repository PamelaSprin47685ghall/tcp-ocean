/// Readiness wait over many sockets (Socket.Select: poll(2) on Unix). Lists and sets
/// are reused, so a wait does not allocate after warm-up.
namespace Tbt.Carrier

open System.Collections.Generic
open System.Net.Sockets
open System.Threading

[<Sealed>]
type Poller() =
    let read = List<Socket>(64)
    let write = List<Socket>(64)
    let readable = HashSet<Socket>(HashIdentity.Reference)
    let writable = HashSet<Socket>(HashIdentity.Reference)

    member _.WatchRead(s: Socket) = read.Add s
    member _.WatchWrite(s: Socket) = write.Add s
    member _.IsReadable(s: Socket) = readable.Contains s
    member _.IsWritable(s: Socket) = writable.Contains s

    /// Wait at most timeoutUs for the watched sockets, then clear the watch lists.
    member _.Wait(timeoutUs: int) =
        readable.Clear()
        writable.Clear()
        if read.Count = 0 && write.Count = 0 then Thread.Sleep(max 1 (timeoutUs / 1000))
        else
            Socket.Select(read, write, null, timeoutUs)
            for s in read do readable.Add s |> ignore
            for s in write do writable.Add s |> ignore
        read.Clear()
        write.Clear()
