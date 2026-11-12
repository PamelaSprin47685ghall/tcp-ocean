/// Readiness wait over many sockets and raw descriptors. Linux: epoll(7) (default) or
/// poll(2), both also take the TUN descriptor. Other systems: Socket.Select (sockets only).
/// The caller states its interest each round; epoll keeps the kernel's interest set and
/// changes only what differs. Buffers are reused, so a wait does not allocate after warm-up.
namespace Tbt.Carrier

open System
open System.Collections.Generic
open System.Net.Sockets
open System.Threading

type PollerBackend =
    | Epoll = 0
    | Poll = 1
    | Select = 2

[<Sealed>]
type Poller(backend: PollerBackend) =
    let epfd =
        if OperatingSystem.IsLinux() && backend = PollerBackend.Epoll then Native.epollCreate () else -1
    let backend =
        if not (OperatingSystem.IsLinux()) || backend = PollerBackend.Select then PollerBackend.Select
        elif epfd >= 0 then PollerBackend.Epoll
        else PollerBackend.Poll
    let usePoll = backend <> PollerBackend.Select
    /// Interest the kernel holds (epoll): descriptor -> events.
    let registered = Dictionary<int, uint32>()
    /// The object behind each watched descriptor, this round and when registered: a closed
    /// socket leaves the epoll set by itself, and a new socket can get the same number.
    let ownerNow = Dictionary<int, obj>()
    let ownerRegistered = Dictionary<int, obj>()
    let gone = List<int>()
    /// Owner of raw descriptors (the TUN device), which stay open for the poller's life.
    let rawOwner = obj ()
    let scratch = Array.zeroCreate<byte> 16
    let events = Array.zeroCreate<byte> (Native.epollEventSize * 512)
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

    let watchFd (fd: int) (events: int16) (owner: obj) =
        if stale then
            stale <- false
            count <- 0
            entryOf.Clear()
            ownerNow.Clear()
        ownerNow.[fd] <- owner
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

    new() = new Poller(PollerBackend.Epoll)

    member _.Backend = backend

    member _.WatchRead(s: Socket) =
        if usePoll then watchFd (int s.Handle) Native.PollIn s else read.Add s

    member _.WatchWrite(s: Socket) =
        if usePoll then watchFd (int s.Handle) Native.PollOut s else write.Add s

    /// Watch a raw descriptor for input (poll backend only).
    member _.WatchFd(fd: int) = watchFd fd Native.PollIn rawOwner

    member _.IsReadable(s: Socket) = if usePoll then has (int s.Handle) Native.PollIn else readable.Contains s
    member _.IsWritable(s: Socket) = if usePoll then has (int s.Handle) Native.PollOut else writable.Contains s
    member _.IsFdReadable(fd: int) = has fd Native.PollIn

    /// Wait at most timeoutUs for the watched descriptors, then clear the watch lists.
    member _.Wait(timeoutUs: int) =
        if backend = PollerBackend.Epoll then
            if stale then
                // Nothing watched this round.
                count <- 0
                entryOf.Clear()
                ownerNow.Clear()
            // Bring the kernel's interest set to this round's watch list.
            for i in 0 .. count - 1 do
                let fd = fds.[i].Fd
                let want = uint32 (uint16 fds.[i].Events)
                fds.[i].Revents <- 0s
                let same =
                    match ownerRegistered.TryGetValue fd with
                    | true, o -> obj.ReferenceEquals(o, ownerNow.[fd])
                    | _ -> false
                if not same then
                    registered.Remove fd |> ignore
                    ownerRegistered.[fd] <- ownerNow.[fd]
                match registered.TryGetValue fd with
                | true, have when have = want -> ()
                | true, _ ->
                    if Native.epollCtl epfd Native.EpollCtlMod fd want scratch < 0 then
                        Native.epollCtl epfd Native.EpollCtlAdd fd want scratch |> ignore
                    registered.[fd] <- want
                | _ ->
                    if Native.epollCtl epfd Native.EpollCtlAdd fd want scratch < 0 then
                        Native.epollCtl epfd Native.EpollCtlMod fd want scratch |> ignore
                    registered.[fd] <- want
            gone.Clear()
            for kv in registered do if not (entryOf.ContainsKey kv.Key) then gone.Add kv.Key
            for fd in gone do
                // A closed descriptor already left the set; the error is expected then.
                Native.epollCtl epfd Native.EpollCtlDel fd 0u scratch |> ignore
                registered.Remove fd |> ignore
                ownerRegistered.Remove fd |> ignore
            if count = 0 then Thread.Sleep(max 1 (timeoutUs / 1000))
            else
                let n = Native.epollWait epfd events (max 0 ((timeoutUs + 999) / 1000))
                for k in 0 .. n - 1 do
                    let at = k * Native.epollEventSize
                    let ev = BitConverter.ToUInt32(events, at)
                    let fd = int (BitConverter.ToUInt64(events, at + Native.epollDataOffset))
                    match entryOf.TryGetValue fd with
                    | true, i -> fds.[i].Revents <- int16 (uint16 (ev &&& 0xFFFFu))
                    | _ -> ()
            stale <- true
        elif usePoll then
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

    /// Forget a descriptor that is about to close (epoll: it leaves the kernel set too).
    member _.Forget(fd: int) =
        if backend = PollerBackend.Epoll && registered.Remove fd then
            ownerRegistered.Remove fd |> ignore
            Native.epollCtl epfd Native.EpollCtlDel fd 0u scratch |> ignore

    interface IDisposable with
        member _.Dispose() = if epfd >= 0 then Native.sysClose epfd |> ignore
