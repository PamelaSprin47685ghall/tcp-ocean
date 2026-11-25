/// One TCP path: the HTTP head, the bootstrap frame, then whole data frames (spec 6.1).
namespace Tbt.Carrier

open System
open System.Buffers
open System.Net.Sockets
open Tbt.Contracts
open Tbt.Session

/// Result of a non-blocking read or write.
type IoResult =
    | Progress = 0
    | WouldBlock = 1
    | Closed = 2

[<Sealed>]
type PathLink(socket: Socket, probe: INotSentProbe) =
    static let bufferLength = 65536
    let mutable rx: byte[] | null = ArrayPool<byte>.Shared.Rent bufferLength
    let mutable rxStart = 0
    let mutable rxEnd = 0
    let mutable tx: byte[] | null = ArrayPool<byte>.Shared.Rent 4096
    let mutable txStart = 0
    let mutable txEnd = 0
    let mutable headDone = false
    let mutable owner = Unchecked.defaultof<IPathOwner>
    let mutable alive = true
    let mutable detached = false
    let mutable health = PathHealth.unknown
    let mutable healthSampled = false
    let mutable previousSegsOut = -1L
    let mutable previousRetrans = -1L

    let returnBuffers (returnRx: bool) =
        if returnRx then
            match rx with
            | null -> ()
            | b ->
                rx <- null
                ArrayPool<byte>.Shared.Return b
        match tx with
        | null -> ()
        | b ->
            tx <- null
            ArrayPool<byte>.Shared.Return b

    member _.Socket = socket
    member val PathId = 0us with get, set
    member val Incarnation = 0u with get, set
    /// Whole frame length once the session is known; the bootstrap frame before that.
    member val FrameLength = Layout.BootFrameLength with get, set
    member _.Buffered = rxEnd - rxStart
    member _.TxIdle = txStart = txEnd
    member this.HasBufferedFrame = alive && (rxEnd - rxStart) >= this.FrameLength
    /// Bytes actually handed to the kernel on this path (partial Send counts).
    member val BytesWritten = 0L with get, set
    /// Peer cumulative ACK of this socket's payload (tcpi_bytes_acked), or -1.
    member _.BytesAcked = SocketTuning.tryBytesAcked socket
    /// Local TCP sender state. This read is for diagnostics only.
    member _.TcpInfo = KernelTcpInfo.tryRead socket
    /// Last cached health snapshot. Never calls into the kernel.
    member _.Health = health
    /// Refresh the cached TCP health snapshot at most once per `minIntervalNs`.
    member _.RefreshHealth(nowNs: int64, minIntervalNs: int64) =
        if alive && not detached && (not healthSampled || nowNs - health.SampledNs >= minIntervalNs) then
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

    /// Read what the kernel holds into the buffer.
    member _.Receive() =
        match rx with
        | null -> IoResult.Closed
        | rxBuf ->
            if rxStart > 0 && (rxStart = rxEnd || rxStart > bufferLength / 2) then
                Buffer.BlockCopy(rxBuf, rxStart, rxBuf, 0, rxEnd - rxStart)
                rxEnd <- rxEnd - rxStart
                rxStart <- 0
            if rxEnd = bufferLength then IoResult.Progress
            else
                let mutable err = SocketError.Success
                let n = socket.Receive(Span(rxBuf, rxEnd, bufferLength - rxEnd), SocketFlags.None, &err)
                if err = SocketError.WouldBlock then IoResult.WouldBlock
                elif err <> SocketError.Success || n = 0 then IoResult.Closed
                else
                    rxEnd <- rxEnd + n
                    IoResult.Progress

    /// Skip the HTTP head. False while it is incomplete; Closed when it is too long.
    member _.TryTakeHead(failed: byref<bool>) =
        if headDone then true
        else
            match rx with
            | null ->
                failed <- true
                false
            | rxBuf ->
                let data = ReadOnlySpan(rxBuf, rxStart, rxEnd - rxStart)
                let at = data.IndexOf(ReadOnlySpan "\r\n\r\n"B)
                if at >= 0 && at + 4 <= Handshake.MaxHeadLength then
                    rxStart <- rxStart + at + 4
                    headDone <- true
                    true
                else
                    failed <- at >= 0 || data.Length >= Handshake.MaxHeadLength
                    false

    /// The next whole frame in the buffer, valid until the next Receive.
    member this.TryTakeFrame(frame: byref<Span<byte>>) =
        let n = this.FrameLength
        match rx with
        | null -> false
        | rxBuf ->
            if rxEnd - rxStart < n then false
            else
                frame <- Span(rxBuf, rxStart, n)
                rxStart <- rxStart + n
                true

    /// Queue bytes to send; the caller checks TxIdle first for data frames.
    member _.Queue(data: ReadOnlySpan<byte>) =
        match tx with
        | null -> invalidOp "path link closed"
        | txBuf ->
            if txStart = txEnd then
                txStart <- 0
                txEnd <- 0
            if data.Length > 4096 - txEnd then invalidOp "path tx buffer full"
            data.CopyTo(Span(txBuf, txEnd, data.Length))
            txEnd <- txEnd + data.Length

    /// Hand queued bytes to the kernel.
    member this.Send() =
        if txStart = txEnd then IoResult.Progress
        else
            match tx with
            | null -> IoResult.Closed
            | txBuf ->
                let mutable err = SocketError.Success
                let n = socket.Send(ReadOnlySpan(txBuf, txStart, txEnd - txStart), SocketFlags.None, &err)
                if err = SocketError.WouldBlock then IoResult.WouldBlock
                elif err <> SocketError.Success then IoResult.Closed
                else
                    txStart <- txStart + n
                    this.BytesWritten <- this.BytesWritten + int64 n
                    IoResult.Progress

    /// Hand queued bytes to the kernel asynchronously (used by parallel bootstrap/JOIN).
    member this.SendAsync(ct: Threading.CancellationToken) : Threading.Tasks.ValueTask<IoResult> =
        if txStart = txEnd then Threading.Tasks.ValueTask<IoResult>(IoResult.Progress)
        else
            match tx with
            | null -> Threading.Tasks.ValueTask<IoResult>(IoResult.Closed)
            | txBuf ->
                let t =
                    task {
                        let mutable res = IoResult.Progress
                        while txStart < txEnd && res = IoResult.Progress do
                            try
                                let! n = socket.SendAsync(ReadOnlyMemory<byte>(txBuf, txStart, txEnd - txStart), SocketFlags.None, ct).AsTask()
                                if n <= 0 then res <- IoResult.Closed
                                else
                                    txStart <- txStart + n
                                    this.BytesWritten <- this.BytesWritten + int64 n
                            with
                            | :? OperationCanceledException -> res <- IoResult.Closed
                            | :? SocketException -> res <- IoResult.Closed
                            | :? ObjectDisposedException -> res <- IoResult.Closed
                        return res
                    }
                Threading.Tasks.ValueTask<IoResult>(t)

    /// Read bytes into rx buffer asynchronously (used by parallel bootstrap/JOIN;
    /// preserves any trailing data frames arriving back-to-back with the reply head).
    member _.ReceiveAsync(ct: Threading.CancellationToken) : Threading.Tasks.ValueTask<IoResult> =
        match rx with
        | null -> Threading.Tasks.ValueTask<IoResult>(IoResult.Closed)
        | rxBuf ->
            if rxStart > 0 && (rxStart = rxEnd || rxStart > bufferLength / 2) then
                Buffer.BlockCopy(rxBuf, rxStart, rxBuf, 0, rxEnd - rxStart)
                rxEnd <- rxEnd - rxStart
                rxStart <- 0
            if rxEnd = bufferLength then Threading.Tasks.ValueTask<IoResult>(IoResult.Progress)
            else
                let t =
                    task {
                        try
                            let! n = socket.ReceiveAsync(Memory<byte>(rxBuf, rxEnd, bufferLength - rxEnd), SocketFlags.None, ct).AsTask()
                            if n <= 0 then return IoResult.Closed
                            else
                                rxEnd <- rxEnd + n
                                return IoResult.Progress
                        with
                        | :? OperationCanceledException -> return IoResult.Closed
                        | :? SocketException -> return IoResult.Closed
                        | :? ObjectDisposedException -> return IoResult.Closed
                    }
                Threading.Tasks.ValueTask<IoResult>(t)

    member _.Close() =
        alive <- false
        if not detached then SocketTuning.closeGraceful socket
        returnBuffers (not detached)

    /// Half-open probe. The peer sent FIN and the kernel has no bytes left:
    /// `SelectRead` is true while `Available` is 0. A path with buffered bytes
    /// is not condemned, and a healthy idle path reports `SelectRead` false.
    /// Closing here lets `prune` drop the slot and `PathPool` rejoin that id.
    member this.CheckAlive() =
        if alive then
            try
                if socket.Poll(0, SelectMode.SelectRead) && socket.Available = 0 then
                    this.Close()
                    false
                else true
            with _ ->
                this.Close()
                false
        else false

    /// Hand the socket and the bytes already read to an async IO thread. The
    /// link must not be used afterwards; the receiver owns the socket and rx buffer.
    member _.Detach() =
        if detached then invalidOp "path already detached"
        match rx with
        | null -> invalidOp "path already closed"
        | rxBuf ->
            detached <- true
            alive <- false
            headDone <- true
            rx <- null
            returnBuffers false
            struct (socket, rxBuf, rxStart, rxEnd, probe)

    /// Drain up to `budget` frames (decrementing `budget`); returns true when more
    /// buffered frames remain or the socket hit the budget while readable.
    member this.ReceiveBudgeted(poller: Poller, budget: byref<int>) =
        if alive && this.Send() = IoResult.Closed then this.Close()
        if not alive then false
        else
            let mutable f = Span<byte>.Empty
            while budget > 0 && alive && this.TryTakeFrame(&f) do
                owner.OnFrame(this.PathId, f) |> ignore
                budget <- budget - 1
            let mutable closed = false
            if budget > 0 && alive && poller.IsReadable socket then
                if TraceGate.On then Trace.record Trace.Readable (uint64 socket.Available) this.PathId 0
                let r = this.Receive()
                while budget > 0 && alive && this.TryTakeFrame(&f) do
                    owner.OnFrame(this.PathId, f) |> ignore
                    budget <- budget - 1
                if r = IoResult.Closed && (rxEnd - rxStart) < this.FrameLength then closed <- true
            if closed then
                this.Close()
                false
            else
                alive && ((rxEnd - rxStart) >= this.FrameLength || (budget = 0 && poller.IsReadable socket))

    interface IPath with
        member this.PathId with get () = this.PathId and set v = this.PathId <- v
        member this.FrameLength with get () = this.FrameLength and set v = this.FrameLength <- v
        member _.Alive = alive
        member _.Backlog with set _ = ()
        member this.Attach(o) =
            owner <- o
            let mutable f = Span<byte>.Empty
            while this.TryTakeFrame(&f) do owner.OnFrame(this.PathId, f) |> ignore

        member this.Watch(poller, sending) =
            poller.WatchRead socket
            if not this.TxIdle || sending then poller.WatchWrite socket

        member this.Receive(poller) =
            if alive && this.Send() = IoResult.Closed then this.Close()
            if alive && poller.IsReadable socket then
                if TraceGate.On then Trace.record Trace.Readable (uint64 socket.Available) this.PathId 0
                let r = this.Receive()
                let mutable f = Span<byte>.Empty
                while this.TryTakeFrame(&f) do owner.OnFrame(this.PathId, f) |> ignore
                if r = IoResult.Closed then this.Close()

        member _.UnsentBytes = probe.NotSent socket
        /// Write gating (spec 3.14): one frame at a time, and only while the kernel holds
        /// at most NotSentLimit unsent bytes. The carrier prefers notsent = 0 first.
        member this.TryBeginFrame(frame) =
            match tx with
            | null -> false
            | txBuf ->
                if alive && this.TxIdle && WriteGate.admits 2 (probe.NotSent socket) then
                    txStart <- 0
                    txEnd <- 0
                    frame <- Span(txBuf, 0, this.FrameLength)
                    true
                else false

        member this.CommitFrame() =
            txEnd <- this.FrameLength
            if this.Send() = IoResult.Closed then this.Close()

        member this.Close() = this.Close()

    interface IPathHealth with
        member _.Health = health
        member this.RefreshHealth(nowNs, minIntervalNs) = this.RefreshHealth(nowNs, minIntervalNs)
