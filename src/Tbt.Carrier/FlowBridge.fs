/// The local end of the flows of one session: a kernel TCP socket per flow slot.
/// The client gets its sockets from a listener; the server connects to its target.
namespace Tbt.Carrier

open System
open System.Net
open System.Net.Sockets
open Tbt.Contracts
open Tbt.Session

[<Sealed>]
type FlowBridge(target: EndPoint voption) =
    static let slotCount = 256
    let locals: (Socket | null)[] = Array.zeroCreate slotCount
    let connecting: bool[] = Array.zeroCreate slotCount
    let wantWrite: bool[] = Array.zeroCreate slotCount
    /// A local failure seen inside a flow callback; handled outside it.
    let failed: bool[] = Array.zeroCreate slotCount
    let mutable session: Session = Unchecked.defaultof<_>
    let mutable active = 0

    let drop (slot: int) =
        match locals.[slot] with
        | null -> ()
        | s ->
            s.Dispose()
            locals.[slot] <- null
            active <- active - 1
        connecting.[slot] <- false
        wantWrite.[slot] <- false

    /// Start a non-blocking connect; return its first result.
    static member private Connect(socket: Socket, ep: EndPoint) =
        try
            socket.Connect ep
            SocketError.Success
        with :? SocketException as e -> e.SocketErrorCode

    member _.Attach(s: Session) = session <- s
    member _.Active = active
    member _.Local(slot: int) = locals.[slot]

    /// Client: a local connection from the listener. False when no slot is free.
    member _.TryAdd(socket: Socket) =
        let mutable slot = -1
        if not (session.TryOpenFlow(&slot)) then false
        else
            SocketTuning.local socket
            socket.Blocking <- false
            locals.[slot] <- socket
            active <- active + 1
            // OPEN goes out at once, so that server-first protocols work (zero-length source).
            let mutable app = Span<byte>.Empty
            if session.TryBeginSource(&app) then session.CommitSource(slot, 0, false)
            true

    /// The local socket of slot takes reads now.
    member _.WantsRead(slot: int) =
        not (isNull locals.[slot]) && not connecting.[slot] && session.FlowSending slot
        && session.FlowRoom slot > 0

    /// The local socket of slot waits for writability (connect or a short write).
    member _.WantsWrite(slot: int) = not (isNull locals.[slot]) && (connecting.[slot] || wantWrite.[slot])

    /// Move bytes from the local socket of slot into sources (at most 16 sources).
    member _.Read(slot: int) =
        match locals.[slot] with
        | null -> ()
        | socket ->
            let mutable budget = 16
            let mutable app = Span<byte>.Empty
            // The peer's flow limit bounds each read; at zero only this flow waits.
            while budget > 0 && session.FlowSending slot && session.FlowRoom slot > 0 && session.TryBeginSourceFor(slot, &app) do
                let mutable err = SocketError.Success
                let room = session.FlowRoom slot
                let n = socket.Receive((if room < app.Length then app.Slice(0, room) else app), SocketFlags.None, &err)
                if err = SocketError.WouldBlock then budget <- 0
                elif err <> SocketError.Success then
                    session.FailFlow slot
                    budget <- 0
                elif n = 0 then
                    session.CommitSource(slot, 0, true)
                    budget <- 0
                else
                    session.CommitSource(slot, n, false)
                    budget <- budget - 1

    /// The local socket of slot is writable: finish a connect, then deliver again.
    member _.OnWritable(slot: int) =
        match locals.[slot] with
        | null -> ()
        | socket ->
            if connecting.[slot] then
                let code = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) :?> int
                connecting.[slot] <- false
                if code <> 0 then failed.[slot] <- true
            wantWrite.[slot] <- false
            if not failed.[slot] then session.PumpFlow slot

    /// Report failures seen inside callbacks to the session.
    member _.Settle() =
        for slot in 0 .. slotCount - 1 do
            if failed.[slot] then
                failed.[slot] <- false
                session.FailFlow slot

    /// Close every local socket (the session ended).
    member _.CloseAll() =
        for slot in 0 .. slotCount - 1 do drop slot

    interface IFlowPort with
        member _.Open(slot, _) =
            match target with
            | ValueSome ep ->
                drop slot
                let socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                SocketTuning.local socket
                socket.Blocking <- false
                locals.[slot] <- socket
                active <- active + 1
                match FlowBridge.Connect(socket, ep) with
                | SocketError.Success -> ()
                | SocketError.WouldBlock
                | SocketError.InProgress -> connecting.[slot] <- true
                | _ -> failed.[slot] <- true
            | ValueNone -> ()

        member _.Write(slot, data) =
            match locals.[slot] with
            | null -> -1
            | _ when connecting.[slot] || failed.[slot] ->
                wantWrite.[slot] <- true
                0
            | socket ->
                let mutable err = SocketError.Success
                let n = socket.Send(data, SocketFlags.None, &err)
                if err = SocketError.WouldBlock then
                    wantWrite.[slot] <- true
                    0
                elif err <> SocketError.Success then -1
                else
                    if n < data.Length then wantWrite.[slot] <- true
                    n

        member _.Finish(slot) =
            match locals.[slot] with
            | null -> ()
            | socket -> try socket.Shutdown SocketShutdown.Send with :? SocketException -> ()

        member _.Reset(slot) =
            match locals.[slot] with
            | null -> ()
            | socket ->
                try socket.LingerState <- LingerOption(true, 0) with :? SocketException -> ()
                drop slot

        member _.Close(slot) = drop slot
