/// One TBTun session at one end (spec 3.11, 6.9, 6.10): the sending half of one direction,
/// the receiving half of the other, and the flows. Single owner thread; no I/O here. The
/// host gives it whole frames from paths and asks it for frames when a path may send.
namespace Tbt.Session

open System
open Tbt.Contracts
open Tbt.Codec
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
open Tbt.Wire

type Role =
    | Client = 0
    | Server = 1

[<Struct>]
type SessionConfig =
    { /// Parity ratio of full steps (spec 5.7).
      Rho: float
      /// Steps in the send window and in the decode window.
      WindowSteps: int
      /// Pending equations in the decoder.
      MaxRows: int
      /// Path id bound (256 bulk paths, 288 with lanes).
      MaxPaths: int
      /// Cells that may wait for a path before new local input waits (sender queue bound).
      TxQueueCells: int
      /// Per-flow byte credit; it is on only when caps bit8 and bit9 are both negotiated.
      Credit: CreditPolicy }

module SessionConfig =
    let defaults =
        { Rho = 0.25; WindowSteps = 4096; MaxRows = 16384; MaxPaths = 288; TxQueueCells = 256
          Credit = CreditPolicy.defaults }

/// The negotiated result of the main handshake that a session needs.
[<Struct>]
type SessionParams =
    { Keys: SessionKeys
      Symbol: int
      Caps: uint16
      /// SERVER_INIT rx_win_grant0: the client may send up steps below it at once.
      Grant0: uint16 }

[<Sealed>]
type private DecoderStore(decoder: Decoder) =
    interface ISourceStore with
        member _.Source(step, slot) = decoder.Source(step, slot)

[<Sealed>]
type Session(role: Role, p: SessionParams, port: IFlowPort, clock: IClock, config: SessionConfig) =
    let s = p.Symbol
    let keys = p.Keys
    let client = role = Role.Client
    let txDir = if client then Direction.Up else Direction.Down
    let rxDir = if client then Direction.Down else Direction.Up
    // URG-1 aside, bulk steps use GF(16) up and GF(2) down unless DL16 is on (spec 5.6a).
    let fieldOf (d: Direction) =
        if d = Direction.Up || p.Caps &&& Caps.Dl16 <> 0us then CodeField.Gf16 else CodeField.Gf2
    let mask (hp: byte[]) : IHeaderMask =
        if p.Caps &&& Caps.HpMask <> 0us then ChaChaHeaderMask(ReadOnlySpan hp) else NoHeaderMask()
    let txKeys = KeyTable()
    let rxKeys = KeyTable()
    do
        txKeys.Install(0u, ReadOnlySpan(if client then keys.UpKey 0u else keys.DownKey 0u), 0UL)
        rxKeys.Install(0u, ReadOnlySpan(if client then keys.DownKey 0u else keys.UpKey 0u), 0UL)
    let sealer = CellSealer(txDir, s, txKeys, mask (if client then keys.HpUp else keys.HpDown))
    let opener = CellOpener(rxDir, s, rxKeys, mask (if client then keys.HpDown else keys.HpUp))
    let encoder = Encoder((if client then keys.GraphSeedUp else keys.GraphSeedDown), s, config.WindowSteps)
    let decoder = Decoder((if client then keys.GraphSeedDown else keys.GraphSeedUp), s, config.WindowSteps, config.MaxRows)
    let tx = TxHalf(txDir, s, fieldOf txDir, encoder, sealer, config.Rho, config.MaxPaths, config.TxQueueCells, clock)
    let rx = RxHalf(s, fieldOf rxDir, decoder, opener, config.WindowSteps, config.MaxPaths)
    let both = Caps.FlowState ||| Caps.FlowState2
    let credit = { config.Credit with Enabled = config.Credit.Enabled && p.Caps &&& both = both }
    let flows = FlowTable(port, DecoderStore decoder, Action<uint64, int>(fun t d -> rx.StepUndelivered(t, d)), credit)
    let flowStatePeriodNs = 10_000_000L
    let snapshotPeriodNs = 1_000_000_000L
    let mutable stateSeq = 0u
    let mutable stateEpoch = 0u
    let mutable lastFlowStateNs = 0L
    let mutable lastSnapshotNs = 0L
    let mutable flowStateDue = false
    // A snapshot is frozen when it starts (spec 6.10b.3 rule 3), then sent page by page.
    let snapshot = Array.zeroCreate<FlowRecord> 256
    let mutable snapshotCount = 0
    let mutable snapshotSent = 0
    let mutable snapshotPage = 0
    let mutable snapshotSeq = 0u
    let mutable flowStatesSent = 0L
    let mutable flowStatesRejected = 0L
    let resets = Collections.Generic.Queue<int>()
    let winAckPeriodNs = 10_000_000L
    let keepAliveNs = 1_000_000_000L
    let mutable lastAckLo = 0UL
    let mutable lastGrantLimit = 0UL
    let mutable lastAckNs = 0L
    let mutable ackDue = false
    let mutable peerSeen = false
    let mutable winAcksSent = 0L
    let mutable nackSpan = 0
    let mutable nackDeficit = 0
    let mutable lastNackNs = 0L
    do
        rx.Attach flows
        // The client may send at once within rx_win_grant0. The server sends no down step
        // before the first authenticated client cell gives a grant (DECISIONS.md D6).
        if client then tx.InitialGrant(uint64 p.Grant0)

    let noteAckSent (now: int64) =
        lastAckLo <- rx.AckStepLo
        lastGrantLimit <- rx.AckStepLo + uint64 rx.Grant
        lastAckNs <- now
        ackDue <- false

    let renderWinAck (frame: Span<byte>) (path: uint16) (now: int64) =
        let symbol = frame.Slice(Layout.SymbolOffset, s)
        let head =
            { TLo = rx.AckStepLo; Deficit = uint16 nackDeficit; Span = byte (max 1 nackSpan)
              NackFlags = (if nackDeficit > 0 then 1uy else 0uy)
              ErasureMeanQ16 = rx.ErasureQ16; ErasureP95Q16 = rx.ErasureQ16
              THi = (if decoder.SeenEnd > 0UL then decoder.SeenEnd - 1UL else 0UL)
              RxTimeUs = uint64 (now / 1000L); PathCount = 0
              DecodedBitmap0 = 0UL; DecodedBitmap1 = 0UL; DecodedBitmap2 = 0UL; DecodedBitmap3 = 0UL
              CtrlAckHi = 1u; CtrlAckBitmap = 0UL }
        symbol.Clear()
        WinAck.writeHead head symbol
        let meta =
            { Type = CellType.WinAck; Flags = MetaFlags.None; Slot = 0uy; StepUsed = 0uy; Aux = 0us
              RxWinGrant = rx.Grant; StepId = 0UL; AckStepLo = rx.AckStepLo; CarrierWm = tx.CarrierWm path
              KHist = 0u; ErasureQ16 = rx.ErasureQ16 }
        FrameMath.writeChunkHeader s frame
        Meta.write meta (frame.Slice(Layout.MetaOffset, Layout.MetaLength))
        frame.[frame.Length - 2] <- 13uy
        frame.[frame.Length - 1] <- 10uy
        tx.Seal(frame, path)
        winAcksSent <- winAcksSent + 1L

    let nextSeq () =
        if stateSeq = UInt32.MaxValue then
            stateEpoch <- stateEpoch + 1u
            stateSeq <- 0u
        stateSeq <- stateSeq + 1u
        stateSeq

    let startSnapshot () =
        snapshotCount <- 0
        for slot in 0 .. 255 do
            if flows.Reportable slot then
                snapshot.[snapshotCount] <- flows.Record slot
                snapshotCount <- snapshotCount + 1
        snapshotSent <- 0
        snapshotPage <- 0
        snapshotSeq <- nextSeq ()

    let snapshotPending () = snapshotSeq <> 0u

    /// One FLOW_STATE page: the next snapshot page when one is open, else an increment
    /// with the flows whose limit grew.
    let renderFlowState (frame: Span<byte>) (path: uint16) =
        let symbol = frame.Slice(Layout.SymbolOffset, s)
        let room = FlowState.maxRecords s
        if snapshotPending () then
            let n = min room (snapshotCount - snapshotSent)
            let last = snapshotSent + n = snapshotCount
            FlowState.writeHead
                { Snapshot = true; LastPage = last; RecordCount = n; TotalFlows = snapshotCount
                  PageIndex = snapshotPage; StateSeq = snapshotSeq; StateEpoch = stateEpoch
                  SnapshotId = snapshotSeq; AllKindsKnown = true } symbol
            for i in 0 .. n - 1 do FlowState.writeFlow i snapshot.[snapshotSent + i] symbol
            snapshotSent <- snapshotSent + n
            snapshotPage <- snapshotPage + 1
            if last then snapshotSeq <- 0u
        else
            let seq = nextSeq ()
            let mutable n = 0
            let mutable slot = 0
            symbol.Clear()
            while n < room && slot < 256 do
                if flows.TakeReport slot then
                    FlowState.writeFlow n (flows.Record slot) symbol
                    n <- n + 1
                slot <- slot + 1
            // writeHead clears its span: give it the head bytes only.
            FlowState.writeHead
                { Snapshot = false; LastPage = false; RecordCount = n; TotalFlows = 0; PageIndex = 0
                  StateSeq = seq; StateEpoch = stateEpoch; SnapshotId = 0u; AllKindsKnown = true }
                (symbol.Slice(0, FlowState.HeadLength))
        let meta =
            { Type = CellType.FlowState; Flags = MetaFlags.None; Slot = 0uy; StepUsed = 0uy; Aux = 0us
              RxWinGrant = rx.Grant; StepId = 0UL; AckStepLo = rx.AckStepLo; CarrierWm = tx.CarrierWm path
              KHist = 0u; ErasureQ16 = rx.ErasureQ16 }
        FrameMath.writeChunkHeader s frame
        Meta.write meta (frame.Slice(Layout.MetaOffset, Layout.MetaLength))
        frame.[frame.Length - 2] <- 13uy
        frame.[frame.Length - 1] <- 10uy
        tx.Seal(frame, path)
        flowStatesSent <- flowStatesSent + 1L

    member _.Role = role
    member _.SymbolSize = s
    member _.FrameLength = Layout.frameLength s
    member _.Keys = keys
    member _.Caps = p.Caps
    member _.Tx = tx
    member _.Rx = rx
    member _.Flows = flows
    member _.WinAcksSent = winAcksSent
    member _.Opener = opener
    /// Decode frontier t_lo of the receive half.
    member _.Frontier = rx.AckStepLo

    /// Receive pipeline for one whole data frame of path (spec 6.6). Returns the cell's
    /// authenticated carrier_wm, or UInt64.MaxValue when the frame did not authenticate.
    member _.OnFrame(path: uint16, frame: Span<byte>) : uint64 =
        match rx.Open(frame, path) with
        | ValueNone -> UInt64.MaxValue
        | ValueSome m ->
            peerSeen <- true
            tx.OnPeerAck(m.AckStepLo, m.RxWinGrant)
            tx.OnPeerErasure m.ErasureQ16
            flows.OnPeerAck tx.PeerAck
            match m.Type with
            | CellType.Data
            | CellType.Parity
            | CellType.Booster ->
                rx.OnCoded(m, Span<byte>.op_Implicit (frame.Slice(Layout.SymbolOffset, s)), path, clock.NowNs())
                rx.Release()
            | CellType.WinAck ->
                let mutable h = Unchecked.defaultof<WinAckHead>
                if WinAck.tryReadHead (Span<byte>.op_Implicit (frame.Slice(Layout.SymbolOffset, s))) &h
                   && h.NackFlags &&& 1uy <> 0uy then
                    tx.OnNack(h.TLo, int h.Span, int h.Deficit)
            | CellType.Heartbeat -> rx.OnHeartbeat m.StepId
            | CellType.FlowState when credit.Enabled ->
                let page = Span<byte>.op_Implicit (frame.Slice(Layout.SymbolOffset, s))
                let mutable h = Unchecked.defaultof<FlowStateHead>
                // A bad page is dropped whole (spec 6.10b.2). Absence of a flow means nothing
                // (rule 5), so each FLOW record stands alone and snapshots need no assembly.
                if FlowState.tryReadHead page &h then
                    for i in 0 .. h.RecordCount - 1 do
                        if FlowState.kindAt page i = byte FlowStateKind.Flow then
                            flows.OnPeerCredit(h.StateEpoch, h.StateSeq, FlowState.readFlow page i)
                else flowStatesRejected <- flowStatesRejected + 1L
            | _ -> ()
            m.CarrierWm

    /// True when a cell waits for a path.
    member _.WantsToSend = ackDue || flowStateDue || tx.HasCandidate || (peerSeen && tx.HeartbeatDue)

    member _.FlowStatesSent = flowStatesSent
    member _.FlowStatesRejected = flowStatesRejected

    /// Render and seal the next cell for path. False when nothing waits.
    member _.TryProduce(path: uint16, frame: Span<byte>) =
        let now = clock.NowNs()
        if ackDue && peerSeen then
            renderWinAck frame path now
            noteAckSent now
            true
        elif flowStateDue then
            renderFlowState frame path
            lastFlowStateNs <- now
            flowStateDue <- snapshotPending ()
            true
        elif tx.TryRenderNext(frame, path, rx.AckStepLo, rx.Grant, rx.ErasureQ16) then
            noteAckSent now
            true
        elif peerSeen && tx.HeartbeatDue then
            tx.RenderHeartbeat(frame, path, rx.AckStepLo, rx.Grant, rx.ErasureQ16)
            true
        elif ackDue then
            renderWinAck frame path now
            noteAckSent now
            true
        else false

    /// Content for a whole-frame hole of path (borrowed shell, spec 6.7.2): BOOSTER, else an
    /// extra parity of the oldest unacknowledged step, else PAD; new cell_seq, flags FILL.
    member _.ProduceFill(path: uint16, frame: Span<byte>) =
        tx.RenderFill(frame, path, rx.AckStepLo, rx.Grant, rx.ErasureQ16)

    /// Timers: WIN_ACK at most every 10 ms while the ack state changes, else every second.
    member _.Tick() =
        let now = clock.NowNs()
        rx.Service now
        rx.Release()
        let changed = rx.AckStepLo <> lastAckLo || rx.AckStepLo + uint64 rx.Grant <> lastGrantLimit
        if (changed && now - lastAckNs >= winAckPeriodNs) || now - lastAckNs >= keepAliveNs then ackDue <- true
        // A NACK is state: repeat it every 10 ms while the frontier lacks rank (spec 5.8).
        if now - lastNackNs >= winAckPeriodNs then
            let mutable span = 0
            let mutable deficit = 0
            if rx.TryNack(now, &span, &deficit) then
                nackSpan <- span
                nackDeficit <- deficit
                lastNackNs <- now
                ackDue <- true
            else nackDeficit <- 0
        // FLOW_STATE (spec 6.10b.4, FST-1): changes at most every 10 ms, a snapshot every 1 s.
        if credit.Enabled && not flowStateDue then
            if now - lastSnapshotNs >= snapshotPeriodNs && (flows.ReportsDue > 0 || flows.RxActive > 0) then
                lastSnapshotNs <- now
                startSnapshot ()
                flowStateDue <- true
            elif flows.ReportsDue > 0 && now - lastFlowStateNs >= flowStatePeriodNs then
                flowStateDue <- true

    // ---- local flows ------------------------------------------------------------------

    /// Client: a new local connection. False when all 256 slots are busy.
    member _.TryOpenFlow(slot: byref<int>) = flows.TryAllocate(&slot)

    /// Bytes slot may still send under the peer's flow limit.
    member _.FlowRoom(slot: int) = flows.TxRoom slot

    /// True while the tx side of slot takes bytes.
    member _.FlowSending(slot: int) = flows.[slot].Active && not flows.[slot].TxEndSent

    member _.CanFill = tx.CanFill

    /// The user-byte area (S - 16 bytes) of the next source. False when window or grant is full.
    member _.TryBeginSource(app: byref<Span<byte>>) =
        let mutable symbol = Span<byte>.Empty
        if tx.TryBeginSource(&symbol) then
            app <- symbol.Slice Layout.EnvelopeLength
            true
        else false

    /// Commit the source begun last: len user bytes of slot; fin ends the stream.
    member _.CommitSource(slot: int, len: int, fin: bool) =
        let e = flows.NextEnvelope(slot, len, fin)
        let t = tx.CommitSource e
        if e.Flags &&& (StreamFlags.Fin ||| StreamFlags.Rst) <> StreamFlags.None then flows.NoteEndStep(slot, t)

    /// The local connection of slot failed: deliver nothing more, send RST.
    member _.FailFlow(slot: int) =
        flows.Fail slot
        if flows.ResetPending slot then resets.Enqueue slot

    /// The local connection of slot may take bytes again.
    member _.PumpFlow(slot: int) =
        flows.Pump slot
        rx.Release()

    /// End of an input round: send pending RSTs, then seal a partly filled step.
    member this.Flush() =
        let mutable app = Span<byte>.Empty
        while resets.Count > 0 && this.TryBeginSource(&app) do
            let slot = resets.Dequeue()
            if flows.ResetPending slot then this.CommitSource(slot, 0, false)
        tx.Flush()
