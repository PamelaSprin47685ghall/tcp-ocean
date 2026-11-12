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
type PathLink(socket: Socket) =
    static let bufferLength = 65536
    let rx: byte[] = GC.AllocateUninitializedArray bufferLength
    let mutable rxStart = 0
    let mutable rxEnd = 0
    let tx: byte[] = GC.AllocateUninitializedArray 4096
    let mutable txStart = 0
    let mutable txEnd = 0
    let mutable headDone = false

    member _.Socket = socket
    member val PathId = 0us with get, set
    member val Incarnation = 0u with get, set
    /// Whole frame length once the session is known; the bootstrap frame before that.
    member val FrameLength = Layout.BootFrameLength with get, set
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

    member _.Close() = socket.Dispose()
