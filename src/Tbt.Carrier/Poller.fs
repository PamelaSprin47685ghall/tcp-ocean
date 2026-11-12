/// Readiness wait over many sockets and raw descriptors. Linux: poll(2) directly, which
/// also takes the TUN descriptor. Other systems: Socket.Select (sockets only). Buffers are
/// reused, so a wait does not allocate after warm-up.
namespace Tbt.Carrier

open System
open System.Collections.Generic
open System.Net.Sockets
open System.Threading

[<Sealed>]
type Poller() =
    static let usePoll = OperatingSystem.IsLinux()
    // poll(2) backend: one entry per descriptor; an index finds a descriptor's entry.
    let mutable fds = Array.zeroCreate<Native.PollFd> 256
    let mutable count = 0
    let entryOf = Dictionary<int, int>()
    // Select backend.
    let read = List<Socket>(64)
    let write = List<Socket>(64)
    let readable = HashSet<Socket>(HashIdentity.Reference)
    let writable = HashSet<Socket>(HashIdentity.Reference)

    // After a wait the results stay readable; the first watch call starts a new round.
    let mutable stale = false

    let watchFd (fd: int) (events: int16) =
        if stale then
            stale <- false
            count <- 0
            entryOf.Clear()
        match entryOf.TryGetValue fd with
        | true, i -> fds.[i].Events <- fds.[i].Events ||| events
        | _ ->
            if count = fds.Length then Array.Resize(&fds, count * 2)
            fds.[count].Fd <- fd
            fds.[count].Events <- events
            fds.[count].Revents <- 0s
            entryOf.[fd] <- count
            count <- count + 1

    let has (fd: int) (events: int16) =
        match entryOf.TryGetValue fd with
        | true, i -> fds.[i].Revents &&& (events ||| Native.PollErr ||| Native.PollHup) <> 0s
        | _ -> false

    static member UsesPoll = usePoll

    member _.WatchRead(s: Socket) =
        if usePoll then watchFd (int s.Handle) Native.PollIn else read.Add s

    member _.WatchWrite(s: Socket) =
        if usePoll then watchFd (int s.Handle) Native.PollOut else write.Add s

    /// Watch a raw descriptor for input (poll backend only).
    member _.WatchFd(fd: int) = watchFd fd Native.PollIn

    member _.IsReadable(s: Socket) = if usePoll then has (int s.Handle) Native.PollIn else readable.Contains s
    member _.IsWritable(s: Socket) = if usePoll then has (int s.Handle) Native.PollOut else writable.Contains s
    member _.IsFdReadable(fd: int) = has fd Native.PollIn

    /// Wait at most timeoutUs for the watched descriptors, then clear the watch lists.
    member _.Wait(timeoutUs: int) =
        if usePoll then
            if count = 0 then
                Thread.Sleep(max 1 (timeoutUs / 1000))
            else Native.poll fds count (max 0 ((timeoutUs + 999) / 1000)) |> ignore
            stale <- true
        else
            readable.Clear()
            writable.Clear()
            if read.Count = 0 && write.Count = 0 then Thread.Sleep(max 1 (timeoutUs / 1000))
            else
                Socket.Select(read, write, null, timeoutUs)
                for s in read do readable.Add s |> ignore
                for s in write do writable.Add s |> ignore
            read.Clear()
            write.Clear()
