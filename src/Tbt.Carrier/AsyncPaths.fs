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

/// One whole frame copied out of a path's receive buffer. Opened: the IO thread
/// already authenticated and decrypted it in place (Counter is its cell_seq).
[<Sealed>]
type RxFrame(pathId: uint16, data: byte[], length: int, opened: bool, counter: uint64) =
    member _.PathId = pathId
    member _.Data = data
    member _.Length = length
    member _.Opened = opened
    member _.Counter = counter

/// Session entry for a frame an IO thread already opened (see RxPreOpener).
type PreOpenedHandler = delegate of uint16 * Span<byte> * uint64 -> uint64

/// Frames read by IO threads for one session, drained by its session thread.
[<Sealed>]
type RxChannel() =
    let queue = ConcurrentQueue<RxFrame>()
    let mutable count = 0
    let wakeToken = [| 1uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |]
    // One descriptor per session: a shared one loses wakes when two session
    // threads race to consume the same eventfd count.
    let wakeFd = try Native.eventFd () with _ -> -1
    // The session thread is the only caller of ClearWake, so the 8-byte read
    // buffer is reused instead of allocated on every wake. AsyncPollerGroup.Loop
    // keeps the same kind of per-owner buffer for its own eventfd.
    let wakeBuffer = Array.zeroCreate<byte> 8
    let mutable wakeArmed = 0
    let mutable disposed = 0
    let mutable preOpened = 0L
    /// Wake/ClearWake calls inside the fd; Dispose waits for zero before closing.
    let mutable inFd = 0

    /// Per-path pre-opener factory and the session entry for opened frames. Both set:
    /// IO threads authenticate and decrypt cells in parallel, so the single session
    /// thread no longer spends ~20% of its time in ChaCha20-Poly1305 on the uplink.
    member val PreOpenerFactory: Func<RxPreOpener> | null = null with get, set
    member val OnPreOpened: PreOpenedHandler | null = null with get, set

    /// Frames the session received already opened (session thread only).
    member _.PreOpenedCount = preOpened
    member _.HasItems = Volatile.Read(&count) > 0
    member _.Count = Volatile.Read(&count)
    member _.WakeFd = wakeFd

    /// Wake the session thread without delivering a frame. A path that drained its
    /// socket buffer makes the write gate admit again, but the kernel reports no
    /// readiness for bytes that merely left the send queue, so the drain itself is
    /// the event. Costs one eventfd write, no allocation.
    ///
    /// Coalesced: while one wake is outstanding (written and not yet consumed by
    /// ClearWake) further wakes skip the syscall. Every frame enqueue and every tx
    /// drain used to write the eventfd, one syscall per 1.3 KiB frame on both IO
    /// and session sides. The session drains the queue and runs Service after
    /// ClearWake, so anything that raced with the clear is still picked up.
    member _.Wake() =
        if wakeFd >= 0 && Interlocked.Exchange(&wakeArmed, 1) = 0 then
            Interlocked.Increment(&inFd) |> ignore
            if Volatile.Read(&disposed) = 0 then
                Native.write wakeFd (ReadOnlySpan<byte> wakeToken) |> ignore
            Interlocked.Decrement(&inFd) |> ignore

    member this.Enqueue(frame: RxFrame) =
        queue.Enqueue frame
        Interlocked.Increment(&count) |> ignore
        this.Wake()

    /// Consume the wake token on the session thread.
    member _.ClearWake() =
        if wakeFd >= 0 then
            Interlocked.Increment(&inFd) |> ignore
            if Volatile.Read(&disposed) = 0 then
                Native.read wakeFd (Span<byte> wakeBuffer) |> ignore
            Interlocked.Decrement(&inFd) |> ignore
            // Re-arm after the read: a wake that lands between the read and this
            // write is skipped, but the caller drains and services right after.
            Volatile.Write(&wakeArmed, 0)

    /// Deliver every queued frame on the calling (session) thread and return the
    /// pooled buffers. `owner` must be the session that owns this channel.
    member this.Drain(owner: IPathOwner) = this.Drain(owner, Int32.MaxValue)

    /// Deliver at most `budget` queued frames and return how many were delivered.
    ///
    /// The budget is what keeps the coordinator alive. The unbounded form is a trap: at
    /// 100 Mbit/s over 32 paths the IO threads keep the queue non-empty, so `TryDequeue`
    /// never fails and the session thread never leaves this loop. It then cannot send a
    /// single WIN_ACK or FLOW_STATE, cannot run a protocol timer, and never logs a stats
    /// line — live forensics measured exactly that as 9.4 s and 12.5 s holes in the client
    /// log, with the peer's credit frozen and its transmit rate down to 0.2 Mbit/s. The
    /// caller re-arms the wake when frames remain, so the next pass resumes immediately.
    member this.Drain(owner: IPathOwner, budget: int) =
        let mutable frame = Unchecked.defaultof<RxFrame>
        let mutable delivered = 0
        let pre = this.OnPreOpened
        while delivered < budget && queue.TryDequeue(&frame) do
            Interlocked.Decrement(&count) |> ignore
            match pre with
            | null -> owner.OnFrame(frame.PathId, Span(frame.Data, 0, frame.Length)) |> ignore
            | h ->
                if frame.Opened then
                    preOpened <- preOpened + 1L
                    h.Invoke(frame.PathId, Span(frame.Data, 0, frame.Length), frame.Counter) |> ignore
                else owner.OnFrame(frame.PathId, Span(frame.Data, 0, frame.Length)) |> ignore
            ArrayPool<byte>.Shared.Return frame.Data
            delivered <- delivered + 1
        delivered

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                let mutable frame = Unchecked.defaultof<RxFrame>
                while queue.TryDequeue(&frame) do
                    ArrayPool<byte>.Shared.Return frame.Data
                if wakeFd >= 0 then
                    // An IO thread may be inside Wake. Every Wake counts itself in inFd
                    // before it reads `disposed` (both full fences), so once inFd is zero
                    // here no later Wake can still write: close without risking a write
                    // to a reused descriptor number.
                    let mutable spin = SpinWait()
                    while Volatile.Read(&inFd) <> 0 do spin.SpinOnce()
                    Native.sysClose wakeFd |> ignore

/// What the IO pool needs from a path. Keeps the pool independent of IPath.
type IPathIo =
    abstract ThreadIndex: int
    abstract WatchForIo: poller: Poller -> unit
    abstract PumpRx: poller: Poller -> unit
    /// Send committed slots. Returns true when more unsent slots remain and did not WouldBlock.
    abstract PumpTx: unit -> unit
    abstract HasPendingTx: bool
    abstract AcquireIoRef: unit -> unit
    abstract ReleaseIoRef: unit -> unit
    /// Detect a socket the kernel destroyed under us (SOCK_DESTROY, RST after
    /// close). epoll drops such a descriptor silently, so a readable-event
    /// driven PumpRx never runs; this probe runs once per idle timeout.
    abstract CheckAlive: unit -> bool
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
    let threadArmed: int[] = Array.zeroCreate width
    // 1 while the IO thread is inside, or entering, epoll_wait. A producer that
    // publishes a frame after the loop's last HasPendingTx scan but before the
    // wait sees this flag and writes the eventfd, so no wakeup can be lost in
    // that window. A busy thread keeps it at 0 and pays no syscall.
    let threadSleeping: int[] = Array.zeroCreate width

    member _.ThreadCount = width
    /// Shared eventfd; -1 when the platform has none, in which case session
    /// threads fall back to a 1 ms poll.
    member _.WakeFd = wakeFd
    member _.ThreadFor(pathId: uint16) = int pathId % width

    /// A new path is picked up by its thread on the next round.
    member _.Add(path: IPathIo) =
        path.AcquireIoRef()
        let i = ((path.ThreadIndex % width) + width) % width
        pending.[i].Enqueue path
        if threadWakes.[i] >= 0 then
            Native.write threadWakes.[i] (ReadOnlySpan<byte> wakeToken) |> ignore

    /// Wake every session thread waiting on the shared descriptor.
    member _.Wake() =
        if wakeFd >= 0 then Native.write wakeFd (ReadOnlySpan<byte> wakeToken) |> ignore

    /// Wake one IO thread. Coalesced per thread via a fast Volatile.Read check
    /// followed by a full-fence Interlocked.Exchange. Pairs with the IO loop's
    /// full-fence clear of threadArmed before checking HasPendingTx and sleeping,
    /// forming a race-free Dekker handoff (no lost wakeups).
    member _.WakeThread(index: int) =
        let i = ((index % width) + width) % width
        if threadWakes.[i] >= 0 then
            if Volatile.Read(&threadSleeping.[i]) = 1 then
                // The thread is entering or inside epoll_wait, and it has already
                // consumed every earlier token, so the coalescing flag cannot stand
                // in for this wake: write one unconditionally.
                Interlocked.Exchange(&threadArmed.[i], 1) |> ignore
                Native.write threadWakes.[i] (ReadOnlySpan<byte> wakeToken) |> ignore
            elif Volatile.Read(&threadArmed.[i]) = 0
                 && Interlocked.Exchange(&threadArmed.[i], 1) = 0 then
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
        let mutable lastAliveCheck = 0L
        while Volatile.Read(&running.contents) = 1 do
            if wake >= 0 then poller.WatchFd wake
            let mutable path = Unchecked.defaultof<IPathIo>
            while mine.TryDequeue(&path) do
                if path.Closed then path.ReleaseIoRef()
                else local.Add path
            let mutable i = local.Count - 1
            while i >= 0 do
                if local.[i].Closed then
                    let removed = local.[i]
                    local.RemoveAt i
                    removed.ReleaseIoRef()
                i <- i - 1
            // Clear the wake flag with a full fence BEFORE checking pending tx
            // slots so a concurrent CommitFrame cannot slip between the empty
            // check and epoll_wait without seeing threadArmed == 0.
            Interlocked.Exchange(&threadArmed.[index], 0) |> ignore
            let mutable needImmediatePump = false
            for path in local do
                if path.HasPendingTx then needImmediatePump <- true
            if needImmediatePump then
                for path in local do
                    try path.PumpTx() with :? ObjectDisposedException -> ()
            for path in local do
                try path.WatchForIo poller with :? ObjectDisposedException -> ()
            // Publish "about to block", then scan again under a full fence. A producer
            // that commits between this store and the wait sees the flag in WakeThread
            // and writes the eventfd; a producer that committed before it is seen by
            // this second scan. The two fenced store/load pairs (here and in
            // WakeThread) are a Dekker handshake, so a committed frame can never wait
            // for the 1 s timeout: the window left by the first scan is closed.
            Volatile.Write(&threadSleeping.[index], 1)
            Thread.MemoryBarrier()
            let mutable mayBlock = not mine.IsEmpty
            if not mayBlock then
                for path in local do
                    if path.HasPendingTx then mayBlock <- true
            if mayBlock then Volatile.Write(&threadSleeping.[index], 0)
            poller.Wait(if mayBlock then 0 else 1_000_000)
            Volatile.Write(&threadSleeping.[index], 0)
            if wake >= 0 && poller.IsFdReadable wake then
                Native.read wake (Span<byte> wakeBuffer) |> ignore
                Interlocked.Exchange(&threadArmed.[index], 0) |> ignore
            // Outgoing work first: at 140 Mbit/s ingress the receive loop below can spend the
            // whole budget in recv(2), and every ACK, WIN_ACK and credit frame for the peer
            // travels through PumpTx. Draining tx before rx keeps the acknowledgement clock
            // running, and the second PumpTx flushes the frames rx just produced.
            for path in local do
                try path.PumpTx() with :? ObjectDisposedException -> ()
            for path in local do
                try path.PumpRx poller with :? ObjectDisposedException -> ()
            for path in local do
                try path.PumpTx() with :? ObjectDisposedException -> ()
            let nowMs = Environment.TickCount64
            if nowMs - lastAliveCheck >= 1000L then
                lastAliveCheck <- nowMs
                for path in local do path.CheckAlive() |> ignore
        // Release pooled buffers of all remaining paths on shutdown.
        let mutable leftover = Unchecked.defaultof<IPathIo>
        while mine.TryDequeue(&leftover) do leftover.ReleaseIoRef()
        for path in local do path.ReleaseIoRef()
        local.Clear()

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
                            channel: RxChannel, rx: byte[], rxStart0: int, rxEnd0: int, slots: int) =
    static let bufferLength = 65536
    let slotCount = if slots < 1 then 16 else slots
    // The session frame is about 1.3 KiB at the default MSS; 2 KiB per slot.
    // The product default is 16 slots (32 KiB per path); --tx-slots widens it.
    static let maxFrame = max 2048 Layout.BootFrameLength
    let mutable pathId = 0us
    let mutable frameLength = Layout.BootFrameLength
    let mutable alive = 1
    let mutable owner = Unchecked.defaultof<IPathOwner>
    let mutable preOpener: RxPreOpener | null = null
    let mutable rxStart = rxStart0
    let mutable rxEnd = rxEnd0
    let mutable bytesWritten = 0L
    // Single pooled slab for all tx slots + pooled gather buffer, returned once
    // both the session thread and IO thread release their references.
    let mutable rxBuf: byte[] | null = rx
    let mutable txSlab: byte[] | null = ArrayPool<byte>.Shared.Rent(slotCount * maxFrame)
    /// Set when a producer found the tx ring full and stopped submitting. The IO thread clears
    /// it and wakes the session channel as soon as the ring drains, so a full ring costs one
    /// eventfd round trip instead of a polling interval (or a busy spin).
    let mutable txFullWait = 0
    let txSent: int[] = Array.zeroCreate slotCount
    let txLen: int[] = Array.zeroCreate slotCount
    let mutable txHead = 0
    let mutable txTail = 0
    // Gather buffer for PumpTx: up to 8 frames go out in one send(2).
    static let gatherLength = 8 * maxFrame
    let mutable gather: byte[] | null = ArrayPool<byte>.Shared.Rent gatherLength
    let mutable bufRefs = 1
    let mutable sessionReleased = 0
    let mutable ioReleased = 0
    // Cached health snapshot. The session thread refreshes it at most once per
    // interval, so path ranking never calls getsockopt on the frame path.
    let mutable health = PathHealth.unknown
    let mutable healthSampled = false
    let mutable previousSegsOut = -1L
    let mutable previousRetrans = -1L

    let releaseRef () =
        if Interlocked.Decrement(&bufRefs) = 0 then
            // Interlocked.Exchange returns the nullable field type; pattern-match the
            // null away instead of asserting the element is non-nullable.
            match Interlocked.Exchange(&rxBuf, null) with
            | null -> ()
            | r -> ArrayPool<byte>.Shared.Return r
            match Interlocked.Exchange(&txSlab, null) with
            | null -> ()
            | t -> ArrayPool<byte>.Shared.Return t
            match Interlocked.Exchange(&gather, null) with
            | null -> ()
            | g -> ArrayPool<byte>.Shared.Return g

    member _.Id with get () = pathId and set value = pathId <- value
    member _.Length
        with get () = Volatile.Read(&frameLength)
        and set value = Volatile.Write(&frameLength, value)
    member _.Alive = Volatile.Read(&alive) = 1
    member _.BytesWritten = Volatile.Read(&bytesWritten)
    member _.BytesAcked = SocketTuning.tryBytesAcked socket
    member _.TcpInfo = KernelTcpInfo.tryRead socket
    /// Same snapshot as `TcpInfo`, named for the health sampler.
    member _.TryTcpInfo() = KernelTcpInfo.tryRead socket

    /// Last cached snapshot. Never blocks and never calls into the kernel.
    member _.Health = health

    /// Refresh the cached snapshot when `minIntervalNs` has elapsed since the
    /// previous one. `RetransRate` is measured between two samples, so it is
    /// zero until a second sample exists; a compare-and-rank never sees a
    /// stale rate pretending to be current.
    member _.RefreshHealth(nowNs: int64, minIntervalNs: int64) =
        if not healthSampled || nowNs - health.SampledNs >= minIntervalNs then
            match KernelTcpInfo.tryRead socket with
            | ValueSome info ->
                let rate =
                    if previousSegsOut >= 0L && info.SegsOut > previousSegsOut then
                        let sent = float (info.SegsOut - previousSegsOut)
                        let retransmitted = float (max 0L (info.TotalRetrans - previousRetrans))
                        min 1.0 (max 0.0 (retransmitted / sent))
                    else 0.0
                previousSegsOut <- info.SegsOut
                previousRetrans <- info.TotalRetrans
                health <- PathHealth.ofInfo nowNs info rate
                healthSampled <- true
            | ValueNone -> ()
    member val Incarnation = 0u with get, set

    /// Take the socket and the bytes already read from a synchronous link.
    /// The source link must not be used again.
    static member TakeOver(link: PathLink, group: AsyncPollerGroup, channel: RxChannel, slots: int) =
        let struct (socket, rx, rxStart, rxEnd, probe) = link.Detach()
        // The session thread never blocks on a socket: the IO thread must get
        // WouldBlock back instead of sleeping inside Send/Receive with the
        // whole path group behind it.
        try socket.Blocking <- false with _ -> ()
        let path = AsyncPathLink(socket, probe, group, channel, rx, rxStart, rxEnd, slots)
        path.Id <- link.PathId
        path.Length <- link.FrameLength
        path.Incarnation <- link.Incarnation
        path

    member private this.TryTakeFrame(frame: byref<Span<byte>>) =
        let n = Volatile.Read(&frameLength)
        match rxBuf with
        | null -> false
        | rxArr ->
            if n > maxFrame || rxEnd - rxStart < n then false
            else
                frame <- Span(rxArr, rxStart, n)
                rxStart <- rxStart + n
                true

    member private this.ReceiveInto() =
        match rxBuf with
        | null -> false
        | rxArr ->
            if rxStart > 0 && (rxStart = rxEnd || rxStart > bufferLength / 2) then
                Buffer.BlockCopy(rxArr, rxStart, rxArr, 0, rxEnd - rxStart)
                rxEnd <- rxEnd - rxStart
                rxStart <- 0
            if rxEnd = bufferLength then false
            else
                let mutable err = SocketError.Success
                let n = socket.Receive(Span(rxArr, rxEnd, bufferLength - rxEnd), SocketFlags.None, &err)
                if err = SocketError.WouldBlock then false
                elif err <> SocketError.Success || n = 0 then
                    Volatile.Write(&alive, 0)
                    false
                else
                    rxEnd <- rxEnd + n
                    true

    /// Queue any complete frames already in the receive buffer (bootstrap tail).
    member private this.QueueFrames(ioThread: bool) =
        let mutable frame = Span<byte>.Empty
        while this.TryTakeFrame(&frame) do
            let data = ArrayPool<byte>.Shared.Rent frame.Length
            frame.CopyTo(Span<byte>(data, 0, frame.Length))
            // Only the IO thread uses the path's pre-opener (it is single-owner).
            if ioThread && isNull preOpener then
                match channel.PreOpenerFactory with
                | null -> ()
                | f -> preOpener <- f.Invoke()
            let mutable c = 0UL
            let opened =
                ioThread
                && (match preOpener with
                    | null -> false
                    | p -> p.TryOpen(Span<byte>(data, 0, frame.Length), pathId, &c))
            channel.Enqueue(RxFrame(pathId, data, frame.Length, opened, c))

    member private this.BeginFrame(frame: byref<Span<byte>>) =
        let len = Volatile.Read(&frameLength)
        match txSlab with
        | null -> false
        | slab ->
            if Volatile.Read(&alive) = 1 && len <= maxFrame
               && Volatile.Read(&txHead) - Volatile.Read(&txTail) < slotCount then
                let slot = Volatile.Read(&txHead) % slotCount
                txSent.[slot] <- 0
                frame <- Span(slab, slot * maxFrame, len)
                true
            else
                // Ring full (or closing): leave a note so the IO thread wakes this producer the
                // moment it drains, instead of the producer polling the ring on a timer.
                if Volatile.Read(&alive) = 1 && len <= maxFrame then Volatile.Write(&txFullWait, 1)
                false

    /// Send every committed slot from txTail in one syscall: copy them into the
    /// gather buffer and hand the kernel one write. Returns false when the socket
    /// would block or failed. One send(2) per 1.3 KiB frame was ~20% of the
    /// server's CPU; with NoDelay each frame also left as its own short segment.
    member private this.SendGathered(head: int, progress: byref<bool>) =
        match txSlab, gather with
        | null, _ | _, null -> false
        | slab, gatherBuf ->
            let mutable used = 0
            let mutable k = txTail
            while k < head && used + maxFrame <= gatherLength do
                let slot = k % slotCount
                let offset = txSent.[slot]
                let length = txLen.[slot] - offset
                if length > 0 then
                    Buffer.BlockCopy(slab, slot * maxFrame + offset, gatherBuf, used, length)
                    used <- used + length
                k <- k + 1
            if used = 0 then
                // Only empty slots: retire them.
                while txTail < k do
                    txSent.[txTail % slotCount] <- 0
                    txTail <- txTail + 1
                true
            else
                let mutable err = SocketError.Success
                let n = socket.Send(ReadOnlySpan<byte>(gatherBuf, 0, used), SocketFlags.None, &err)
                if err = SocketError.WouldBlock then false
                elif err <> SocketError.Success then
                    Volatile.Write(&alive, 0)
                    false
                elif n <= 0 then false
                else
                    bytesWritten <- bytesWritten + int64 n
                    progress <- true
                    // Retire whole slots; a partial one keeps its offset.
                    let mutable left = n
                    while left > 0 && txTail < k do
                        let slot = txTail % slotCount
                        let remaining = txLen.[slot] - txSent.[slot]
                        if left >= remaining then
                            left <- left - remaining
                            txSent.[slot] <- 0
                            txTail <- txTail + 1
                        else
                            txSent.[slot] <- txSent.[slot] + left
                            left <- 0
                    n = used

    member private this.PumpTx() =
        if Volatile.Read(&alive) = 1 then
            match txSlab with
            | null -> ()
            | slab ->
                let mutable go = true
                let mutable progress = false
                while go && Volatile.Read(&txHead) - txTail >= 2 do
                    go <- this.SendGathered(Volatile.Read(&txHead), &progress)
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
                        let n = socket.Send(ReadOnlySpan<byte>(slab, slot * maxFrame + offset, length), SocketFlags.None, &err)
                        if err = SocketError.WouldBlock then go <- false
                        elif err <> SocketError.Success then
                            Volatile.Write(&alive, 0)
                            go <- false
                        elif n <= 0 then
                            // Zero bytes with no error: retry next round rather than spin.
                            go <- false
                        else
                            bytesWritten <- bytesWritten + int64 n
                            progress <- true
                            if offset + n >= committed then
                                txSent.[slot] <- 0
                                txTail <- txTail + 1
                            else
                                txSent.[slot] <- offset + n
                                go <- false
                // Bytes reached the kernel: unsent is now lower, so WriteGate may admit a
                // new frame at once. Without this wake the session thread waits out a timer.
                if progress then channel.Wake()
                // A producer that stopped because the ring was full is waiting for this: hand
                // the slots back as soon as half the ring is free, one eventfd round trip away.
                if Volatile.Read(&txFullWait) = 1
                   && Volatile.Read(&txHead) - Volatile.Read(&txTail) < slotCount / 2 then
                    Volatile.Write(&txFullWait, 0)
                    channel.Wake()

    member private this.PumpRx(poller: Poller) =
        if Volatile.Read(&alive) = 1 && poller.IsReadable socket then
            let mutable more = true
            let mutable rounds = 0
            while more && rounds < 16 && Volatile.Read(&alive) = 1 do
                more <- this.ReceiveInto()
                rounds <- rounds + 1
                this.QueueFrames true

    member this.CheckAlive() =
        if Volatile.Read(&alive) = 1 then
            try
                if socket.Poll(0, SelectMode.SelectError) then
                    this.CloseSocketOnly()
                    false
                else true
            with _ ->
                this.CloseSocketOnly()
                false
        else false

    member _.Socket = socket

    /// True when this path's user-space tx ring can accept another frame. A producer should
    /// ask before TryBeginFrame: the ring is the queue, and `UnsentBytes` reports it.
    member _.HasTxSpace =
        Volatile.Read(&alive) = 1
        && Volatile.Read(&txHead) - Volatile.Read(&txTail) < slotCount

    /// Frames committed to the ring but not yet handed to the kernel, in bytes.
    member _.PendingTxBytes =
        max 0 (Volatile.Read(&txHead) - Volatile.Read(&txTail)) * Volatile.Read(&frameLength)

    member private _.CloseSocketOnly() =
        if Interlocked.Exchange(&alive, 0) = 1 then
            try socket.Dispose () with _ -> ()

    member this.Close() =
        this.CloseSocketOnly()
        if Interlocked.Exchange(&sessionReleased, 1) = 0 then
            releaseRef ()

    interface IPathIo with
        member _.ThreadIndex = group.ThreadFor pathId
        member this.WatchForIo(poller) =
            if Volatile.Read(&alive) = 1 then
                poller.WatchRead socket
                if Volatile.Read(&txTail) < Volatile.Read(&txHead) then poller.WatchWrite socket
        member this.PumpRx(poller) = this.PumpRx poller
        member this.PumpTx() = this.PumpTx()
        member _.HasPendingTx = Volatile.Read(&alive) = 1 && Volatile.Read(&txTail) < Volatile.Read(&txHead)
        member _.AcquireIoRef() = Interlocked.Increment(&bufRefs) |> ignore
        member _.ReleaseIoRef() =
            if Interlocked.Exchange(&ioReleased, 1) = 0 then
                releaseRef ()
        member this.CheckAlive() = this.CheckAlive()
        member _.Closed = Volatile.Read(&alive) = 0

    interface IPath with
        member _.PathId with get () = pathId and set value = pathId <- value
        member _.FrameLength
            with get () = Volatile.Read(&frameLength)
            and set value = Volatile.Write(&frameLength, value)
        member _.Alive = Volatile.Read(&alive) = 1
        member this.Attach(o) =
            owner <- o
            this.QueueFrames false
        member _.Backlog with set _ = ()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member _.UnsentBytes =
            // A frame queued in this path's own tx ring has not reached the kernel at all, so
            // SIOCOUTQNSD reports 0 for it. Reporting only the kernel counter told WriteGate
            // that a path holding a full 128 KiB ring was idle: it admitted every frame,
            // TryBeginFrame refused it, and the live 24 %-loss run logged 2 579 328 such
            // refusals — 95.4 % of all admitted attempts — while the session thread spun at
            // full speed. The ring is part of the path's backlog, so it is part of this number.
            let queued = max 0 (Volatile.Read(&txHead) - Volatile.Read(&txTail)) * Volatile.Read(&frameLength)
            let kernel = try probe.NotSent socket with _ -> 0
            if kernel < 0 then kernel else kernel + queued
        member this.TryBeginFrame(frame) = this.BeginFrame(&frame)
        member this.CommitFrame() =
            let head = Volatile.Read(&txHead)
            txLen.[head % slotCount] <- Volatile.Read(&frameLength)
            // Full StoreLoad fence: publish txHead BEFORE checking threadArmed
            // inside WakeThread, pairing with the IO loop's fenced clear of threadArmed.
            Interlocked.Exchange(&txHead, head + 1) |> ignore
            group.WakeThread(group.ThreadFor pathId)
        member this.Close() = this.Close()
