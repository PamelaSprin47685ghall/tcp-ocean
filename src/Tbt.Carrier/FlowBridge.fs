/// The local end of the flows of one session: a kernel TCP socket per flow slot.
/// The client gets its sockets from a listener; the server connects to its target.
namespace Tbt.Carrier

open System
open System.Buffers
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
    // Delivery coalescing. The session hands decoded data to Write one cell
    // (~1.3 KiB) at a time; one send(2) per cell was ~18% of the server session
    // thread on uplink (the single thread that also decrypts and decodes). Small
    // writes are staged per slot and flushed when the stage is half full and at
    // the end of every Service round, so no byte waits longer than one round.
    static let stageCap = 65536
    /// Delivery coalescing buffers, rented from the shared pool only while a slot has bytes
    /// to stage and returned as soon as it drains. They were one 64 KiB array per slot held
    /// for the life of the bridge, 16 MiB of resident memory on a 256-slot server.
    let stage: byte[][] = Array.init slotCount (fun _ -> Array.Empty<byte>())
    let stageLen: int[] = Array.zeroCreate slotCount
    let finishAfterStage: bool[] = Array.zeroCreate slotCount
    let staged = ReadySet()
    /// Bytes handed to the kernel per slot, for the opt-in `Trace.Sent` record.
    ///
    /// This exists because `Write` above only STAGES: it copies into the 64 KiB buffer and
    /// returns the length, so a flow's bytes can sit here while the local socket refuses them.
    /// Without a separate mark at the real `socket.Send`, "the bridge owns the bytes" and "the
    /// kernel accepted the bytes" would be indistinguishable, and a probe blocked on a slow
    /// local reader would look identical to one blocked on the network. One int64 per slot,
    /// written only when the gate is on; no per-frame allocation and no new lock, because
    /// `flushSlot` runs on the session thread, the ring's single writer.
    let sent: int64[] = Array.zeroCreate slotCount
    let markSent (slot: int) (n: int) =
        if TraceGate.On && n > 0 then
            let total = sent.[slot] + int64 n
            sent.[slot] <- total
            Trace.record Trace.Sent (uint64 total) (uint16 slot) -1
    let releaseStage (slot: int) =
        let buf = stage.[slot]
        if buf.Length > 0 then
            ArrayPool<byte>.Shared.Return buf
            stage.[slot] <- Array.Empty<byte>()
        stageLen.[slot] <- 0
        staged.Remove slot
    /// Read-side diagnostics: how often a slot's Read ran, how many bytes it moved, and
    /// how often the local socket had nothing to give. These separate "the socket is
    /// empty" from "Read is called too rarely" without changing data-path behaviour.
    let mutable readCalls = 0L
    let mutable readBytes = 0L
    let mutable wouldBlock = 0L

    /// Readability of a slot is a SESSION decision: the peer's credit and the local close state
    /// live there. An unattached bridge therefore has no opinion — `rearm`/`WantsRead` must be
    /// called after `Attach`, exactly as `Server`/`Client` do before any slot can open. Letting
    /// an unattached bridge answer "readable" would arm slots whose credit is unknown, and it
    /// would hide a missing `Attach` from every caller instead of failing loudly at the boundary.
    let wantsRead (slot: int) =
        not (isNull locals.[slot]) && not connecting.[slot]
        && session.FlowSending slot && session.FlowRoom slot > 0
    let rearm (slot: int) = armed.Set(slot, wantsRead slot)
    let rewrite (slot: int) = writers.Set(slot, not (isNull locals.[slot]) && (connecting.[slot] || wantWrite.[slot]))
    let fail (slot: int) =
        failed.[slot] <- true
        failures.Add slot

    /// Send the staged bytes of slot. True when the stage is empty afterwards.
    let flushSlot (slot: int) =
        let len = stageLen.[slot]
        if len = 0 then true
        else
            match locals.[slot] with
            | null ->
                releaseStage slot
                true
            | socket ->
                let mutable err = SocketError.Success
                let n = socket.Send(ReadOnlySpan<byte>(stage.[slot], 0, len), SocketFlags.None, &err)
                if err = SocketError.WouldBlock then
                    wantWrite.[slot] <- true
                    writers.Add slot
                    false
                elif err <> SocketError.Success then
                    // The socket is gone: give the staging chunk back now, or a failed slot
                    // that is never dropped keeps 64 KiB rented for the life of the bridge.
                    releaseStage slot
                    fail slot
                    true
                else
                    if n < len then
                        // A short write accepted n bytes: they ARE in the kernel, and the
                        // remainder stays staged. Omitting this call made `Sent` under-count
                        // exactly the sends that matter most on a slow local reader — the
                        // ones that could not complete in one go — so `Stage − Sent` read as
                        // if all of a partially accepted stage were still in the bridge.
                        markSent slot n
                        Buffer.BlockCopy(stage.[slot], n, stage.[slot], 0, len - n)
                        stageLen.[slot] <- len - n
                        wantWrite.[slot] <- true
                        writers.Add slot
                        false
                    else
                        // The kernel took the whole stage: this is the real send event, as
                        // opposed to `Write` merely copying into the buffer.
                        markSent slot len
                        releaseStage slot
                        if finishAfterStage.[slot] then
                            finishAfterStage.[slot] <- false
                            try socket.Shutdown SocketShutdown.Send with :? SocketException -> ()
                        true

    let drop (slot: int) =
        releaseStage slot
        finishAfterStage.[slot] <- false
        // A slot's kernel-send counter belongs to the socket that carried those bytes. Resetting
        // it here makes `Sent` a per-slot-lifetime total instead of a per-process one, so a new
        // generation reusing the slot starts from zero and its deltas mean what they say. (The
        // record's `aux` is still -1: the bridge does not track generations, so a reader must
        // pair records with the slot's open/close, not assume a generation id.)
        sent.[slot] <- 0L
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
                    if session.FlowRoom slot <= 0 then
                        // The peer's limit is the reason the socket is not read. Observe a
                        // close here, or the far end waits on a FIN that cannot be read.
                        let mutable err = SocketError.Success
                        let n = socket.Receive(Span peekBuf, SocketFlags.Peek, &err)
                        if err = SocketError.WouldBlock then ()
                        elif err <> SocketError.Success then session.FailFlow slot
                        elif n = 0 then localReadClosed slot socket
                    // Otherwise only the bulk source ring is momentarily full: it clears in
                    // this same round, so a Peek here would spend a syscall for nothing.
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
            let empty = flushSlot slot
            if empty && not failed.[slot] then session.PumpFlow slot
            rewrite slot
            rearm slot

    /// Send every staged delivery (end of a Service round).
    member _.Flush() =
        let mutable slot = staged.NextUp -1
        while slot >= 0 do
            let next = staged.NextUp slot
            flushSlot slot |> ignore
            slot <- next

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
                let len = stageLen.[slot]
                if len + data.Length <= stageCap then
                    // Stage it; flush once half full, the rest at the end of the round.
                    if stage.[slot].Length = 0 then stage.[slot] <- ArrayPool<byte>.Shared.Rent stageCap
                    data.CopyTo(Span<byte>(stage.[slot], len, data.Length))
                    stageLen.[slot] <- len + data.Length
                    staged.Add slot
                    if stageLen.[slot] >= stageCap / 2 then flushSlot slot |> ignore
                    data.Length
                elif not (flushSlot slot) then
                    // The socket is full: take what still fits in the stage.
                    let room = stageCap - stageLen.[slot]
                    let take = min room data.Length
                    if take > 0 then
                        if stage.[slot].Length = 0 then stage.[slot] <- ArrayPool<byte>.Shared.Rent stageCap
                        data.Slice(0, take).CopyTo(Span<byte>(stage.[slot], stageLen.[slot], take))
                        stageLen.[slot] <- stageLen.[slot] + take
                        staged.Add slot
                    take
                elif failed.[slot] then -1
                else
                    let mutable err = SocketError.Success
                    let n = socket.Send(data, SocketFlags.None, &err)
                    if err = SocketError.WouldBlock then
                        wantWrite.[slot] <- true
                        writers.Add slot
                        0
                    elif err <> SocketError.Success then -1
                    else
                        // Direct send path (the chunk alone exceeded the stage): also a real
                        // kernel send, so it belongs on the same `Sent` timeline.
                        markSent slot n
                        if n < data.Length then
                            wantWrite.[slot] <- true
                            writers.Add slot
                        n

        member _.Finish(slot) =
            match locals.[slot] with
            | null -> ()
            | socket ->
                // FIN goes after the staged bytes.
                if flushSlot slot then
                    try socket.Shutdown SocketShutdown.Send with :? SocketException -> ()
                else finishAfterStage.[slot] <- true

        member _.Reset(slot) =
            match locals.[slot] with
            | null -> ()
            | socket ->
                try socket.LingerState <- LingerOption(true, 0) with :? SocketException -> ()
                drop slot

        member _.Close(slot) = drop slot

        member _.Credit(slot) = if not (armed.Contains slot) then rearm slot
