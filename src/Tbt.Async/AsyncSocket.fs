/// Async socket wrapper using SocketAsyncEventArgs for zero-allocation IO.
namespace Tbt.Async

open System
open System.Net.Sockets
open System.Threading

/// Pre-allocated buffer pool to avoid per-receive allocations.
[<Sealed>]
type BufferPool(bufferSize: int, poolSize: int) =
    do
        if bufferSize < 1 then invalidArg "bufferSize" "must be positive"
        if poolSize < 1 then invalidArg "poolSize" "must be positive"

    let buffers = Array.init poolSize (fun _ -> Array.zeroCreate<byte> bufferSize)
    let available = SPSCQueue<byte[]>(poolSize)

    do
        for buf in buffers do
            available.TryEnqueue(buf) |> ignore

    /// Rent a buffer. None means the pool is exhausted.
    member _.TryRent() = available.TryDequeue()

    /// Return a buffer. Extra returns are ignored once the pool is full.
    member _.Return(buffer: byte[]) =
        available.TryEnqueue(buffer) |> ignore

/// Non-blocking socket operations with one completion callback each.
///
/// One receive and one send may be in flight at a time. The two
/// SocketAsyncEventArgs objects are allocated once and reused; the callbacks
/// run on the socket's completion thread. The caller must serialise its own state.
[<Sealed>]
type AsyncSocket(socket: Socket, bufferSize: int) =
    do
        if bufferSize < 1 then invalidArg "bufferSize" "must be positive"
        socket.Blocking <- false

    let sendBuffer: byte[] = Array.zeroCreate bufferSize
    let recvBuffer: byte[] = Array.zeroCreate bufferSize
    let sendArgs = new SocketAsyncEventArgs()
    let recvArgs = new SocketAsyncEventArgs()
    let mutable sendPending = 0
    let mutable recvPending = 0
    let mutable closed = 0
    let mutable sendCallback: (int -> SocketError -> unit) | null = null
    let mutable recvCallback: (int -> SocketError -> unit) | null = null

    do
        sendArgs.SetBuffer(sendBuffer, 0, bufferSize)
        recvArgs.SetBuffer(recvBuffer, 0, bufferSize)
        sendArgs.Completed.Add(fun e ->
            Volatile.Write(&sendPending, 0)
            let cb = sendCallback
            sendCallback <- null
            match cb with
            | null -> ()
            | f -> f e.BytesTransferred e.SocketError)
        recvArgs.Completed.Add(fun e ->
            Volatile.Write(&recvPending, 0)
            let cb = recvCallback
            recvCallback <- null
            match cb with
            | null -> ()
            | f -> f e.BytesTransferred e.SocketError)

    /// Start one receive. The callback owns the bytes in `RecvBuffer` until the
    /// next receive starts.
    member _.ReceiveAsync(callback: int -> SocketError -> unit) =
        if Volatile.Read(&closed) <> 0 then callback 0 SocketError.OperationAborted
        elif Interlocked.CompareExchange(&recvPending, 1, 0) <> 0 then
            invalidOp "receive already pending"
        else
            recvCallback <- callback
            recvArgs.SetBuffer(0, bufferSize)
            let sync =
                try socket.ReceiveAsync recvArgs
                with ex ->
                    Volatile.Write(&recvPending, 0)
                    recvCallback <- null
                    callback 0 SocketError.SocketError
                    ignore ex
                    true
            if sync then
                Volatile.Write(&recvPending, 0)
                recvCallback <- null
                callback recvArgs.BytesTransferred recvArgs.SocketError

    /// Start one send of `data` (copied into the send buffer).
    member _.SendAsync(data: ReadOnlySpan<byte>, callback: int -> SocketError -> unit) =
        if Volatile.Read(&closed) <> 0 then callback 0 SocketError.OperationAborted
        elif data.Length > bufferSize then invalidArg "data" "data exceeds buffer size"
        elif Interlocked.CompareExchange(&sendPending, 1, 0) <> 0 then
            invalidOp "send already pending"
        else
            data.CopyTo(Span(sendBuffer, 0, data.Length))
            sendCallback <- callback
            sendArgs.SetBuffer(0, data.Length)
            let sync =
                try socket.SendAsync sendArgs
                with _ ->
                    Volatile.Write(&sendPending, 0)
                    sendCallback <- null
                    callback 0 SocketError.SocketError
                    true
            if sync then
                Volatile.Write(&sendPending, 0)
                sendCallback <- null
                callback sendArgs.BytesTransferred sendArgs.SocketError

    /// Bytes of the last completed receive.
    member _.RecvBuffer = ReadOnlySpan(recvBuffer)

    member _.Socket = socket

    member _.Close() =
        if Interlocked.Exchange(&closed, 1) = 0 then
            try socket.Close() with _ -> ()
            sendArgs.Dispose()
            recvArgs.Dispose()

    interface IDisposable with
        member this.Dispose() = this.Close()

module AsyncSocket =
    let wrap (socket: Socket) (bufferSize: int) = new AsyncSocket(socket, bufferSize)

    let createTcp (bufferSize: int) =
        let socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        wrap socket bufferSize
