/// Whole-frame channels and the IO threads that fill them.
///
/// Ownership is fixed and small:
///   * one IO thread owns each path's socket, receive buffer and transmit slots;
///   * the session thread owns Session, FlowBridge and TunStack;
///   * an RxChannel is the only object both sides touch, and only through a
///     ConcurrentQueue plus an atomic count.
///
/// The session thread never reads a socket. The IO threads never touch Session.
namespace Tbt.Carrier

open System
open System.Buffers
open System.Collections.Concurrent
open System.Net.Sockets
open System.Threading
open Tbt.Contracts
open Tbt.Session

/// One whole frame copied out of a path's receive buffer.
[<Sealed>]
type RxFrame(pathId: uint16, data: byte[], length: int) =
    member _.PathId = pathId
    member _.Data = data
    member _.Length = length

/// Frames read by IO threads for one session, drained by its session thread.
[<Sealed>]
type RxChannel() =
    let queue = ConcurrentQueue<RxFrame>()
    let mutable count = 0
    let wakeToken = [| 1uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |]
    // One descriptor per session: a shared one loses wakes when two session
    // threads race to consume the same eventfd count.
    let wakeFd = try Native.eventFd () with _ -> -1

    member _.HasItems = Volatile.Read(&count) > 0
    member _.Count = Volatile.Read(&count)
    member _.WakeFd = wakeFd

    member _.Enqueue(frame: RxFrame) =
        queue.Enqueue frame
        Interlocked.Increment(&count) |> ignore
        if wakeFd >= 0 then Native.write wakeFd (ReadOnlySpan<byte> wakeToken) |> ignore

    /// Consume the wake token on the session thread.
    member _.ClearWake() =
        if wakeFd >= 0 then
            let buffer = Array.zeroCreate<byte> 8
            Native.read wakeFd (Span<byte> buffer) |> ignore

    /// Deliver every queued frame on the calling (session) thread and return the
    /// pooled buffers. `owner` must be the session that owns this channel.
    member _.Drain(owner: IPathOwner) =
        let mutable frame = Unchecked.defaultof<RxFrame>
        let mutable delivered = 0
        while queue.TryDequeue(&frame) do
            Interlocked.Decrement(&count) |> ignore
            owner.OnFrame(frame.PathId, Span(frame.Data, 0, frame.Length)) |> ignore
            ArrayPool<byte>.Shared.Return frame.Data
            delivered <- delivered + 1
        delivered

    interface IDisposable with
        member _.Dispose() = if wakeFd >= 0 then Native.sysClose wakeFd |> ignore

/// What the IO pool needs from a path. Keeps the pool independent of IPath.
type IPathIo =
    abstract ThreadIndex: int
    abstract WatchForIo: poller: Poller -> unit
    abstract PumpRx: poller: Poller -> unit
    abstract PumpTx: unit -> unit
    abstract Closed: bool

/// Fixed-size IO thread pool shared by every session of one server or client.
/// Each thread runs its own Poller; a path is assigned to one thread for life,
/// so its socket is only ever touched by that thread.
[<Sealed>]
type AsyncPollerGroup(threadCount: int) =
    static let wakeToken = [| 1uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |]
    let width = max 1 threadCount
    let running = ref 1
    let started = ref 0
    let threads = ResizeArray<Thread>(width)
    let polls: Poller[] = Array.init width (fun _ -> new Poller())
    // One queue per IO thread. A shared queue cannot work here: every thread
    // dequeues it, and a path whose ThreadIndex is not the dequeuer's would be
    // dropped instead of handed to its owner.
    let pending: ConcurrentQueue<IPathIo>[] =
        Array.init width (fun _ -> ConcurrentQueue<IPathIo>())
    let locals: ResizeArray<IPathIo>[] = Array.init width (fun _ -> ResizeArray())
    let wakeFd = try Native.eventFd () with _ -> -1
    // One eventfd per IO thread. A new path wakes exactly the thread that owns
    // it, and the thread blocks in epoll instead of timing out every millisecond.
    let threadWakes: int[] =
        Array.init width (fun _ -> try Native.eventFd () with _ -> -1)

    member _.ThreadCount = width
    /// Shared eventfd; -1 when the platform has none, in which case session
    /// threads fall back to a 1 ms poll.
    member _.WakeFd = wakeFd
    member _.ThreadFor(pathId: uint16) = int pathId % width

    /// A new path is picked up by its thread on the next round.
    member _.Add(path: IPathIo) =
        let i = ((path.ThreadIndex % width) + width) % width
        pending.[i].Enqueue path
        if threadWakes.[i] >= 0 then
            Native.write threadWakes.[i] (ReadOnlySpan<byte> wakeToken) |> ignore

    /// Wake every session thread waiting on the shared descriptor.
    member _.Wake() =
        if wakeFd >= 0 then Native.write wakeFd (ReadOnlySpan<byte> wakeToken) |> ignore

    /// Wake one IO thread. A path with a newly committed frame must wake its
    /// owner: the owner blocks in epoll, and a socket that is only writable
    /// produces no event, so PumpTx would otherwise never run.
    member _.WakeThread(index: int) =
        let i = ((index % width) + width) % width
        if threadWakes.[i] >= 0 then
            Native.write threadWakes.[i] (ReadOnlySpan<byte> wakeToken) |> ignore

    /// Consume the wake token.
    member _.ClearWake() =
        if wakeFd >= 0 then
            let buffer = Array.zeroCreate<byte> 8
            Native.read wakeFd (Span<byte> buffer) |> ignore

    member private _.Loop(index: int) =
        let poller = polls.[index]
        let local = locals.[index]
        let mine = pending.[index]
        let wake = threadWakes.[index]
        let wakeBuffer = Array.zeroCreate<byte> 8
        if wake >= 0 then poller.WatchFd wake
        while Volatile.Read(&running.contents) = 1 do
            let mutable path = Unchecked.defaultof<IPathIo>
            while mine.TryDequeue(&path) do
                if not path.Closed then local.Add path
            let mutable i = local.Count - 1
            while i >= 0 do
                if local.[i].Closed then local.RemoveAt i
                i <- i - 1
            for path in local do path.WatchForIo poller
            poller.Wait -1
            if wake >= 0 then Native.read wake (Span<byte> wakeBuffer) |> ignore
            for path in local do path.PumpRx poller
            for path in local do path.PumpTx()

    member this.Start() =
        if Interlocked.Exchange(&started.contents, 1) = 0 then
            for i in 0 .. width - 1 do
                let t = Thread(fun () -> this.Loop i)
                t.IsBackground <- true
                t.Name <- "tbtun-io-" + string i
                threads.Add t
                t.Start()

    member _.Stop() =
        Volatile.Write(&running.contents, 0)
        for fd in threadWakes do
            if fd >= 0 then Native.write fd (ReadOnlySpan<byte> wakeToken) |> ignore
        for t in threads do t.Join 2000 |> ignore
        for p in polls do (p :> IDisposable).Dispose()
        for fd in threadWakes do
            if fd >= 0 then Native.sysClose fd |> ignore
        if wakeFd >= 0 then Native.sysClose wakeFd |> ignore

/// One TCP path whose socket is driven by an IO thread.
///
/// The session thread calls TryBeginFrame/CommitFrame exactly as it does on
/// PathLink. CommitFrame publishes a transmit slot; the IO thread sends it and
/// advances the tail. Receive frames are copied into pooled buffers and queued.
[<Sealed>]
type AsyncPathLink private (socket: Socket, probe: INotSentProbe, group: AsyncPollerGroup,
                            channel: RxChannel, rx: byte[], rxStart0: int, rxEnd0: int) =
    static let bufferLength = 65536
    static let slotCount = 16
    // The session frame is about 1.3 KiB at the default MSS; 2 KiB per slot
    // keeps 16 slots at 32 KiB per path, so 256 paths stay near 24 MiB.
    static let maxFrame = max 2048 Layout.BootFrameLength
    let mutable pathId = 0us
    let mutable frameLength = Layout.BootFrameLength
    let mutable alive = 1
    let mutable owner = Unchecked.defaultof<IPathOwner>
    let mutable rxStart = rxStart0
    let mutable rxEnd = rxEnd0
    let mutable bytesWritten = 0L
    let txSlots: byte[][] = Array.init slotCount (fun _ -> GC.AllocateUninitializedArray<byte> maxFrame)
    let txSent: int[] = Array.zeroCreate slotCount
    let txLen: int[] = Array.zeroCreate slotCount
    let mutable txHead = 0
    let mutable txTail = 0

    member _.Id with get () = pathId and set value = pathId <- value
    member _.Length
        with get () = Volatile.Read(&frameLength)
        and set value = Volatile.Write(&frameLength, value)
    member _.Alive = Volatile.Read(&alive) = 1
    member _.BytesWritten = Volatile.Read(&bytesWritten)
    member val Incarnation = 0u with get, set

    /// Take the socket and the bytes already read from a synchronous link.
    /// The source link must not be used again.
    static member TakeOver(link: PathLink, group: AsyncPollerGroup, channel: RxChannel) =
        let struct (socket, rx, rxStart, rxEnd, probe) = link.Detach()
        // The session thread never blocks on a socket: the IO thread must get
        // WouldBlock back instead of sleeping inside Send/Receive with the
        // whole path group behind it.
        try socket.Blocking <- false with _ -> ()
        let path = AsyncPathLink(socket, probe, group, channel, rx, rxStart, rxEnd)
        path.Id <- link.PathId
        path.Length <- link.FrameLength
        path.Incarnation <- link.Incarnation
        path

    member private this.TryTakeFrame(frame: byref<Span<byte>>) =
        let n = Volatile.Read(&frameLength)
        if n > maxFrame || rxEnd - rxStart < n then false
        else
            frame <- Span(rx, rxStart, n)
            rxStart <- rxStart + n
            true

    member private this.ReceiveInto() =
        if rxStart > 0 && (rxStart = rxEnd || rxStart > bufferLength / 2) then
            Buffer.BlockCopy(rx, rxStart, rx, 0, rxEnd - rxStart)
            rxEnd <- rxEnd - rxStart
            rxStart <- 0
        if rxEnd = bufferLength then false
        else
            let mutable err = SocketError.Success
            let n = socket.Receive(Span(rx, rxEnd, bufferLength - rxEnd), SocketFlags.None, &err)
            if err = SocketError.WouldBlock then false
            elif err <> SocketError.Success || n = 0 then
                Volatile.Write(&alive, 0)
                false
            else
                rxEnd <- rxEnd + n
                true

    /// Queue any complete frames already in the receive buffer (bootstrap tail).
    member private this.QueueFrames() =
        let mutable frame = Span<byte>.Empty
        while this.TryTakeFrame(&frame) do
            let data = ArrayPool<byte>.Shared.Rent frame.Length
            frame.CopyTo(Span<byte>(data, 0, frame.Length))
            channel.Enqueue(RxFrame(pathId, data, frame.Length))

    member private this.BeginFrame(frame: byref<Span<byte>>) =
        let len = Volatile.Read(&frameLength)
        if Volatile.Read(&alive) = 1 && len <= maxFrame
           && Volatile.Read(&txHead) - Volatile.Read(&txTail) < slotCount then
            let slot = Volatile.Read(&txHead) % slotCount
            txSent.[slot] <- 0
            frame <- Span(txSlots.[slot], 0, len)
            true
        else false

    member private this.PumpTx() =
        if Volatile.Read(&alive) = 1 then
            let mutable go = true
            while go && txTail < Volatile.Read(&txHead) do
                let slot = txTail % slotCount
                let offset = txSent.[slot]
                let committed = txLen.[slot]
                let length = committed - offset
                if length <= 0 then
                    txSent.[slot] <- 0
                    txTail <- txTail + 1
                else
                    let mutable err = SocketError.Success
                    let n = socket.Send(ReadOnlySpan<byte>(txSlots.[slot], offset, length), SocketFlags.None, &err)
                    if err = SocketError.WouldBlock then go <- false
                    elif err <> SocketError.Success then
                        Volatile.Write(&alive, 0)
                        go <- false
                    elif n <= 0 then
                        // Zero bytes with no error: retry next round rather than spin.
                        go <- false
                    else
                        bytesWritten <- bytesWritten + int64 n
                        if offset + n >= committed then
                            txSent.[slot] <- 0
                            txTail <- txTail + 1
                        else
                            txSent.[slot] <- offset + n
                            go <- false

    member private this.PumpRx(poller: Poller) =
        if Volatile.Read(&alive) = 1 && poller.IsReadable socket then
            let mutable more = true
            let mutable rounds = 0
            while more && rounds < 16 && Volatile.Read(&alive) = 1 do
                more <- this.ReceiveInto()
                rounds <- rounds + 1
                this.QueueFrames()

    member this.Close() =
        if Interlocked.Exchange(&alive, 0) = 1 then
            try socket.Dispose () with _ -> ()

    interface IPathIo with
        member _.ThreadIndex = group.ThreadFor pathId
        member this.WatchForIo(poller) =
            if Volatile.Read(&alive) = 1 then
                poller.WatchRead socket
                if Volatile.Read(&txTail) < Volatile.Read(&txHead) then poller.WatchWrite socket
        member this.PumpRx(poller) = this.PumpRx poller
        member this.PumpTx() = this.PumpTx()
        member _.Closed = Volatile.Read(&alive) = 0

    interface IPath with
        member _.PathId with get () = pathId and set value = pathId <- value
        member _.FrameLength
            with get () = Volatile.Read(&frameLength)
            and set value = Volatile.Write(&frameLength, value)
        member _.Alive = Volatile.Read(&alive) = 1
        member this.Attach(o) =
            owner <- o
            this.QueueFrames ()
        member _.Backlog with set _ = ()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member _.UnsentBytes =
            try probe.NotSent socket with _ -> 0
        member this.TryBeginFrame(frame) = this.BeginFrame(&frame)
        member this.CommitFrame() =
            let head = Volatile.Read(&txHead)
            let idle = head = Volatile.Read(&txTail)
            txLen.[head % slotCount] <- Volatile.Read(&frameLength)
            Volatile.Write(&txHead, head + 1)
            if idle then group.WakeThread(group.ThreadFor pathId)
        member this.Close() = this.Close()
