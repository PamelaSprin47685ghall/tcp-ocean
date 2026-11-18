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
    // Ready sets (spec 16.3.3 RS-1…3): the carrier visits only these slots, never all 256.
    /// Slots with a local socket.
    let opened = ReadySet()
    /// Slots that wait for writability (connect or a short write).
    let writers = ReadySet()
    /// Slots that take reads now: connected, still sending, flow credit left. A slot that
    /// runs out of credit leaves and comes back on the Credit event (RS-2).
    let armed = ReadySet()
    let failures = ReadySet()
    let peekBuf = Array.zeroCreate 1
    /// Read-side diagnostics: how often a slot's Read ran, how many bytes it moved, and
    /// how often the local socket had nothing to give. These separate "the socket is
    /// empty" from "Read is called too rarely" without changing data-path behaviour.
    let mutable readCalls = 0L
    let mutable readBytes = 0L
    let mutable wouldBlock = 0L

    let wantsRead (slot: int) =
        not (isNull locals.[slot]) && not connecting.[slot] && session.FlowSending slot
        && session.FlowRoom slot > 0
    let rearm (slot: int) = armed.Set(slot, wantsRead slot)
    let rewrite (slot: int) = writers.Set(slot, not (isNull locals.[slot]) && (connecting.[slot] || wantWrite.[slot]))
    let fail (slot: int) =
        failed.[slot] <- true
        failures.Add slot

    let drop (slot: int) =
        match locals.[slot] with
        | null -> ()
        | s ->
            s.Dispose()
            locals.[slot] <- null
            active <- active - 1
        connecting.[slot] <- false
        wantWrite.[slot] <- false
        opened.Remove slot
        writers.Remove slot
        armed.Remove slot

    /// True when the write half is already dead: a full close or RST, not a SHUT_WR.
    let writeSideDead (socket: Socket) =
        try
            let code = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) :?> int
            if code <> 0 then true
            else socket.Poll(0, SelectMode.SelectError)
        with :? SocketException -> true

    /// Local read hit EOF or a hard error: RST if the socket is fully dead, else FIN on the
    /// close reserve so a full bulk queue cannot hold up the end (spec 6.10b.4).
    let localReadClosed (slot: int) (socket: Socket) =
        if writeSideDead socket then session.FailFlow slot
        else
            let mutable app = Span<byte>.Empty
            if session.TryBeginSourceClose(&app) then session.CommitSource(slot, 0, true)

    /// Start a non-blocking connect; return its first result.
    static member private Connect(socket: Socket, ep: EndPoint) =
        try
            SocketTuning.fastOpenConnect socket
            socket.Connect ep
            SocketError.Success
        with :? SocketException as e -> e.SocketErrorCode

    member _.Attach(s: Session) = session <- s
    member _.Active = active
    member _.ReadCalls = readCalls
    member _.ReadBytes = readBytes
    member _.WouldBlock = wouldBlock
    member _.Local(slot: int) = locals.[slot]
    member _.Opened = opened
    member _.Writers = writers
    member _.Armed = armed

    /// Client: a local connection from the listener. False when no slot is free.
    member _.TryAdd(socket: Socket) =
        let mutable slot = -1
        if not (session.TryOpenFlow(&slot)) then false
        else
            SocketTuning.local socket
            socket.Blocking <- false
            locals.[slot] <- socket
            active <- active + 1
            opened.Add slot
            // OPEN goes out at once, so that server-first protocols work (zero-length source).
            let mutable app = Span<byte>.Empty
            if session.TryBeginSource(&app) then session.CommitSource(slot, 0, false)
            rearm slot
            true

    /// The local socket of slot takes reads now.
    member _.WantsRead(slot: int) = wantsRead slot

    /// The local socket of slot waits for writability (connect or a short write).
    member _.WantsWrite(slot: int) = not (isNull locals.[slot]) && (connecting.[slot] || wantWrite.[slot])

    /// Move bytes from the local socket of slot into sources (at most 16 sources).
    member _.Read(slot: int) =
        match locals.[slot] with
        | null -> ()
        | socket ->
            readCalls <- readCalls + 1L
            let mutable budget = 4096
            let mutable app = Span<byte>.Empty
            let mutable go = true
            while go && budget > 0 && session.FlowSending slot do
                if session.FlowRoom slot > 0 && session.TryBeginSourceFor(slot, &app) then
                    let mutable err = SocketError.Success
                    let room = session.FlowRoom slot
                    let n = socket.Receive((if room < app.Length then app.Slice(0, room) else app), SocketFlags.None, &err)
                    if err = SocketError.WouldBlock then
                        wouldBlock <- wouldBlock + 1L
                        go <- false
                    elif err <> SocketError.Success then
                        session.FailFlow slot
                        go <- false
                    elif n = 0 then
                        // Do not commit FIN into a bulk source behind a full queue.
                        localReadClosed slot socket
                        go <- false
                    else
                        readBytes <- readBytes + int64 n
                        session.CommitSource(slot, n, false)
                        budget <- budget - 1
                else
                    // Credit or the bulk queue is full: still observe a local close.
                    go <- false
                    let mutable err = SocketError.Success
                    let n = socket.Receive(Span peekBuf, SocketFlags.Peek, &err)
                    if err = SocketError.WouldBlock then ()
                    elif err <> SocketError.Success then session.FailFlow slot
                    elif n = 0 then localReadClosed slot socket
            rearm slot

    /// The local socket of slot is writable: finish a connect, then deliver again.
    member _.OnWritable(slot: int) =
        match locals.[slot] with
        | null -> ()
        | socket ->
            if connecting.[slot] then
                let code = socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) :?> int
                connecting.[slot] <- false
                if code <> 0 then fail slot
            wantWrite.[slot] <- false
            if not failed.[slot] then session.PumpFlow slot
            rewrite slot
            rearm slot

    /// Report failures seen inside callbacks to the session.
    member _.Settle() =
        let mutable slot = failures.NextUp -1
        while slot >= 0 do
            failures.Remove slot
            if failed.[slot] then
                failed.[slot] <- false
                session.FailFlow slot
            slot <- failures.NextUp slot

    /// Close every local socket (the session ended).
    member _.CloseAll() =
        let mutable slot = opened.NextUp -1
        while slot >= 0 do
            drop slot
            slot <- opened.NextUp slot

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
                opened.Add slot
                match FlowBridge.Connect(socket, ep) with
                | SocketError.Success -> ()
                | SocketError.WouldBlock
                | SocketError.InProgress -> connecting.[slot] <- true
                | _ -> fail slot
                rewrite slot
                rearm slot
            | ValueNone -> ()

        member _.Write(slot, data) =
            match locals.[slot] with
            | null -> -1
            | _ when connecting.[slot] || failed.[slot] ->
                wantWrite.[slot] <- true
                writers.Add slot
                0
            | socket ->
                let mutable err = SocketError.Success
                let n = socket.Send(data, SocketFlags.None, &err)
                if err = SocketError.WouldBlock then
                    wantWrite.[slot] <- true
                    writers.Add slot
                    0
                elif err <> SocketError.Success then -1
                else
                    if n < data.Length then
                        wantWrite.[slot] <- true
                        writers.Add slot
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

        member _.Credit(slot) = if not (armed.Contains slot) then rearm slot
