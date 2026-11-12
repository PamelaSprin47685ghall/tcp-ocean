/// One TCP path: the HTTP head, the bootstrap frame, then whole data frames (spec 6.1).
namespace Tbt.Carrier

open System
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
    let rx: byte[] = GC.AllocateUninitializedArray bufferLength
    let mutable rxStart = 0
    let mutable rxEnd = 0
    let tx: byte[] = GC.AllocateUninitializedArray 4096
    let mutable txStart = 0
    let mutable txEnd = 0
    let mutable headDone = false
    let mutable owner = Unchecked.defaultof<IPathOwner>
    let mutable alive = true
    let mutable frameLength = Layout.BootFrameLength
    let mutable notSentLimit = SocketTuning.NotSentLimit

    member _.Socket = socket
    member val PathId = 0us with get, set
    member val Incarnation = 0u with get, set
    /// Whole frame length once the session is known; the bootstrap frame before that.
    /// Setting it also sets the unsent bound to 2 frames (spec 16.3 Q-1): a new frame goes
    /// in only while less than one frame waits unsent, and the kernel wakes the writer then.
    member _.FrameLength
        with get () = frameLength
        and set v =
            frameLength <- v
            notSentLimit <- v
            SocketTuning.setNotSentLowat socket v
    member _.Buffered = rxEnd - rxStart
    member _.TxIdle = txStart = txEnd

    /// Read what the kernel holds into the buffer.
    member _.Receive() =
        if rxStart > 0 && (rxStart = rxEnd || rxStart > bufferLength / 2) then
            Buffer.BlockCopy(rx, rxStart, rx, 0, rxEnd - rxStart)
            rxEnd <- rxEnd - rxStart
            rxStart <- 0
        if rxEnd = bufferLength then IoResult.Progress
        else
            let mutable err = SocketError.Success
            let n = socket.Receive(Span(rx, rxEnd, bufferLength - rxEnd), SocketFlags.None, &err)
            if err = SocketError.WouldBlock then IoResult.WouldBlock
            elif err <> SocketError.Success || n = 0 then IoResult.Closed
            else
                rxEnd <- rxEnd + n
                IoResult.Progress

    /// Skip the HTTP head. False while it is incomplete; Closed when it is too long.
    member _.TryTakeHead(failed: byref<bool>) =
        if headDone then true
        else
            let data = ReadOnlySpan(rx, rxStart, rxEnd - rxStart)
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
        if rxEnd - rxStart < n then false
        else
            frame <- Span(rx, rxStart, n)
            rxStart <- rxStart + n
            true

    /// Queue bytes to send; the caller checks TxIdle first for data frames.
    member _.Queue(data: ReadOnlySpan<byte>) =
        if txStart = txEnd then
            txStart <- 0
            txEnd <- 0
        if data.Length > tx.Length - txEnd then invalidOp "path tx buffer full"
        data.CopyTo(Span(tx, txEnd, data.Length))
        txEnd <- txEnd + data.Length

    /// Hand queued bytes to the kernel.
    member _.Send() =
        if txStart = txEnd then IoResult.Progress
        else
            let mutable err = SocketError.Success
            let n = socket.Send(ReadOnlySpan(tx, txStart, txEnd - txStart), SocketFlags.None, &err)
            if err = SocketError.WouldBlock then IoResult.WouldBlock
            elif err <> SocketError.Success then IoResult.Closed
            else
                txStart <- txStart + n
                IoResult.Progress

    member _.Close() =
        alive <- false
        socket.Dispose()

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
                let r = this.Receive()
                let mutable f = Span<byte>.Empty
                while this.TryTakeFrame(&f) do owner.OnFrame(this.PathId, f) |> ignore
                if r = IoResult.Closed then this.Close()

        /// Write gating (spec 3.14): one frame at a time, and only while the kernel holds
        /// less than notSentLimit unsent bytes.
        member this.TryBeginFrame(frame) =
            if alive && this.TxIdle && probe.NotSent socket < notSentLimit then
                txStart <- 0
                txEnd <- 0
                frame <- Span(tx, 0, this.FrameLength)
                true
            else false

        member this.CommitFrame() =
            txEnd <- this.FrameLength
            if this.Send() = IoResult.Closed then this.Close()

        member this.Close() = this.Close()
