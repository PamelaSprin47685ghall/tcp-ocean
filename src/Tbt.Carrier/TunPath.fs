/// One path on the server's user-space TCP over TUN (spec 4.1, 4.3, 4.4, 6.7, 6.8).
/// The client side stays an unmodified kernel TCP. Receive: frames are taken out of
/// order as soon as they are whole; holes inside the source-retention lease are swallowed
/// (the cumulative ACK moves over them; the codec repairs the loss). Send: one frame per
/// segment; a whole-frame hole is refilled with new content (borrowed shell, new cell_seq);
/// the HTTP head and the bootstrap reply are kept as exact bytes until acknowledged.
/// Rate: a loss-blind delivery-rate pacer (BBR-like), constant repair timer 1.15 x SRTT.
namespace Tbt.Carrier

open System
open System.Buffers.Binary
open Tbt.Contracts
open Tbt.Session

/// Where TUN packets go out.
type ITunOutput =
    abstract Write: packet: ReadOnlySpan<byte> -> unit
    /// Shared buffer for building one packet (valid until the next Write).
    abstract Scratch: byte[]

/// Counters of spec 4.9.
[<Sealed>]
type TunCounters() =
    member val SegmentsSent = 0L with get, set
    member val SegmentsLost = 0L with get, set
    member val BorrowRetx = 0L with get, set
    member val VirtualAcks = 0L with get, set
    member val FastSwallows = 0L with get, set
    member val UpHoles = 0L with get, set
    member val CellsSpanning = 0L with get, set
    member val CellsReceived = 0L with get, set
    member val CellsDropped = 0L with get, set
    /// Payload bytes whose cumulative ACK advanced over a hole (grace or frontier swallow).
    member val BytesSwallowed = 0L with get, set
    /// Times the fragment-slot table grew (memory budget = advertised window).
    member val ReasmGrows = 0L with get, set
    /// Times a fragment was not stored: rcvNxt / interval merge stopped (backpressure, not a drop).
    member val ReasmPressure = 0L with get, set
    /// Times the OFO interval (gap) table grew (memory budget = advertised window).
    member val GapGrows = 0L with get, set
    /// Times a new gap was not stored: backpressure, not early_retire swallow.
    member val GapPressure = 0L with get, set
    member val TailRepairs = 0L with get, set
    /// RTO fallback episodes: snd.una did not move for the RTO (no SACK needed).
    member val Rtos = 0L with get, set
    /// ACKs that reported a duplicate segment as a D-SACK block (RFC 2883).
    member val Dsacks = 0L with get, set
    /// Times a path was demoted: data in flight, nothing delivered for the demotion time.
    member val Demotions = 0L with get, set
    /// Exceptions caught at a path entry point: the owner failed, the connection was reset.
    member val Faults = 0L with get, set
    /// Gaps between two calls to this path longer than the stall limit, cut from the timeline.
    ///
    /// Read it as "this path went >300 ms unvisited", not as a stall duration:
    ///   * `TunStack.Service` visits every connection on each tick and they read one clock, so a
    ///     single gap raises this counter once per open path — four paths log +4. Do not multiply
    ///     the aggregate by the stall limit and call it stall seconds; that over-counts by the
    ///     path count.
    ///   * It also fires when the path has nothing in flight, where the shift moves no stamp at
    ///     all. So an increment is not proof that a segment's RTT or rate sample was discarded.
    /// What it *does* establish: the service loop for this stack did not run for >300 ms, so every
    /// timing sample spanning that gap is unreliable (onDelivered drops them, see `jumpNs`).
    member val ClockJumps = 0L with get, set
    /// Swallowed (virtually acked) bytes whose first copy arrived later: not duplicates.
    member val LateFirstBytes = 0L with get, set
    /// Swallowed ranges forgotten because the record was full (a later first arrival of
    /// their bytes is then taken for a duplicate).
    member val SwallowedForgotten = 0L with get, set
    /// Repairs requested by RACK, the tail timer or the RTO fallback (each sent once paced).
    member val RepairRequests = 0L with get, set
    /// Times a waiting repair found the pacer empty and waited.
    member val RepairsDeferred = 0L with get, set
    /// Repairs sent while the path was demoted (still charged to the bottleneck).
    member val DemotedRepairs = 0L with get, set
    /// Deliveries of segments sent before a clock jump: no RTT or rate sample taken.
    member val JumpSamplesSkipped = 0L with get, set
    /// Largest interval between two `catchUp` calls on this path, in ns — gaps under the stall
    /// limit included, and gaps with nothing outstanding included. Sampled at entry, before the
    /// shift, so it is the raw wall interval and not a shifted one.
    ///
    /// It is a maximum over observations, not a proven bound: if two threads ever wrote it
    /// concurrently a `max` update could be lost (that can only hide a larger value, never invent
    /// one). Today one loop thread services the whole stack, so no update is lost.
    member val MaxGapNs = 0L with get, set
    /// Gaps over the stall limit whose entry time had an **unacked segment in the ring** on this
    /// path. This is the only class for which the shift really refuses measurements: the segments
    /// in the ring were sent before the jump, so `onDelivered` gives them no RTT and no rate sample
    /// (`JumpSamplesSkipped`). It says "work was outstanding when the gap was noticed", NOT "work
    /// was outstanding for the whole gap": path state can change inside a gap without a `catchUp`
    /// (a SYN arrives through `OnSyn`, which mutates state and does not sample), so neither this
    /// class nor the next is a proof about the gap's interior. Summed per path.
    member val GapsWithUnacked = 0L with get, set
    /// Gaps over the stall limit whose entry time had no unacked segment, but did have work that
    /// the shift moves: a reorder interval, or an armed RTO. No delivery sample is refused for
    /// these, so they do not by themselves invalidate a rate or RTT measurement. Summed per path.
    member val GapsWithWork = 0L with get, set
    /// Gaps over the stall limit whose entry time showed nothing outstanding on this path: no
    /// unacked segment, no reorder interval, no armed RTO. The shift moved no stamp. Summed per
    /// path, like `ClockJumps` — so a single loop gap with `n` open paths adds `n`.
    member val GapsWithoutWork = 0L with get, set
    /// Largest of `GapsWithUnacked`, in ns: the longest gap that actually refused measurements on
    /// this path. `max`, not a sum, and (like `MaxGapNs`) not a proven bound under concurrent
    /// writers.
    member val MaxUnackedGapNs = 0L with get, set

[<Struct>]
type SentSegment =
    { mutable Seq: uint64
      mutable Length: int
      /// 0 exact bytes (head, bootstrap reply), 1 data frame (refill with new content).
      mutable Kind: byte
      mutable Sacked: bool
      mutable Retx: int
      mutable SentNs: int64
      mutable Delivered: int64
      mutable DeliveredNs: int64
      mutable FirstSentNs: int64
      /// The group's delivery state when this copy was sent (BBR per-ACK rate sample of the
      /// whole group, not only this path).
      mutable GroupDelivered: int64
      mutable GroupDeliveredNs: int64
      mutable GroupFirstSentNs: int64
      /// Repair copies beyond the first, in bytes (HOLE-2 duplicates on the wire).
      mutable Duplicates: int
      /// A repair is requested and waits for the pacer.
      mutable Due: bool }

type TunState =
    | SynReceived = 0
    | Established = 1
    | Closed = 2

[<Sealed>]
type TunPath(output: ITunOutput, counters: TunCounters, localIp: uint32, localPort: uint16,
             remoteIp: uint32, remotePort: uint16, isn: uint32, clock: IClock, group: RateGroup,
             ?windowClamp: int) =
    let now0 = clock.NowNs()
    // ---- constants ---------------------------------------------------------------------
    /// Advertised receive window per path: the spec's 600 000 B per subflow (ch. 9, same as the
    /// client's TCP_WINDOW_CLAMP). It bounds the client's in-flight bytes per path, so the
    /// uplink cannot build a standing queue at the bottleneck beyond it (was 4 MiB: about 350 ms).
    /// Overridable by --window-clamp; default remains WindowClamp.
    let windowBytes = defaultArg windowClamp SocketTuning.WindowClamp
    /// Window scale we advertise: 7, because the spec's 600 000 B per-path window needs
    /// 65535 <<< 3.36. A peer that offers no WS option never scales its window, so our
    /// advertised value must then be unscaled: ourScale becomes 0 and window() advertises
    /// min (windowBytes, 65535) = 65535 B. Shifting by 7 in that case advertised 4687 B, a
    /// 4.6 KB window worth about 41 KB/s on a 113 ms path.
    let mutable ourScale = 7
    /// Initial OFO interval (gap) table; grows up to the advertised window (see gapBudget).
    static let gapCount0 = 16
    /// Initial fragment-slot table; grows up to the advertised window (see slotBudget).
    static let slotCount0 = 32
    static let graceNs = 15_000_000L
    /// A gap this long between two calls is a stall (process or whole box), not network time.
    static let stallNs = 300_000_000L
    /// Tail repairs per timer: the oldest few; their SACKs then let RACK find the rest.
    static let tailBudget = 4
    /// Waiting repairs a path may hold before it stops taking new frames. Above this the
    /// path is genuinely behind; below it, new data still goes out, so a single hole that
    /// the pacer has not cleared yet cannot turn the path into a retransmit-only pipe.
    static let repairBurst = 8
    static let headCap = Handshake.MaxHeadLength + Layout.BootFrameLength + 16384
    static let wrapBase = 1UL <<< 32
    let sackOption = Array.zeroCreate<byte> 36

    let mutable state = TunState.SynReceived
    let mutable owner = Unchecked.defaultof<IPathOwner>
    let mutable attached = false
    let mutable pathId = 0us
    let mutable frameLength = 0
    let mutable ipId = 1us
    let mutable closing = false

    // ---- receive -----------------------------------------------------------------------
    let mutable rcvNxt = 0UL
    let mutable bytesAccepted = 0L
    let mutable swallowedBytes = 0L
    let mutable lateFirstLocal = 0L
    let mutable peerFinAt = UInt64.MaxValue
    // OFO intervals (spec 6.8.3 early_retire used a fixed 16). Sized from the same
    // advertised-window budget as fragment slots. Overflow must not swallow: a new
    // gap is refused (TCP backpressure, GapPressure) so sender aggressiveness is
    // not turned into virtual-ACK loss.
    let mutable gapCap = gapCount0
    let mutable ivStart = Array.zeroCreate<uint64> (gapCount0 + 2)
    let mutable ivEnd = Array.zeroCreate<uint64> (gapCount0 + 2)
    /// When the gap in front of interval i was first seen.
    let mutable ivSince = Array.zeroCreate<int64> (gapCount0 + 2)
    /// carrier_wm of the first authenticated cell after the gap in front of interval i.
    let mutable ivWm = Array.zeroCreate<uint64> (gapCount0 + 2)
    let mutable ivCount = 0
    let mutable ackDue = false
    /// Duplicate bytes to report as the first SACK block of the next ACK (RFC 2883 D-SACK):
    /// a re-sent segment whose bytes were already acked or swallowed. The sender then knows the
    /// retransmission was not needed and undoes its cwnd cut (Linux: undo on D-SACK), so an
    /// outage that only lost ACKs, or a hole TBT did not need, does not shrink the kernel window.
    let mutable dsackLo = 0UL
    let mutable dsackHi = 0UL
    /// Receive history: ranges below rcvNxt that were swallowed (virtually acked), not
    /// received. Bytes below rcvNxt outside them really arrived. Bounded; oldest forgotten.
    static let swallowedMax = 64
    let swStart = Array.zeroCreate<uint64> (swallowedMax + 1)
    let swEnd = Array.zeroCreate<uint64> (swallowedMax + 1)
    let mutable swCount = 0
    // Head phase: in-order bytes only, until the bootstrap frame is whole.
    let mutable head: byte[] = Array.zeroCreate headCap
    let mutable headLen = 0
    let mutable headAt = 0UL
    let mutable bootAt = -1
    let mutable dataPhase = false
    let mutable data0 = 0UL
    // Partial frames (spec 6.8.3). Sized from a memory budget equal to the advertised
    // receive window (windowBytes / frameLength slots), not a fixed 32. Overflow must
    // not drop cells: takeBytes returns the prefix we could store and the caller holds
    // rcvNxt / interval merge there (TCP backpressure), counted in ReasmPressure.
    let mutable slotCap = slotCount0
    let mutable slotFrame = Array.create slotCount0 -1L
    let mutable slotFill = Array.zeroCreate<int> slotCount0
    let mutable slotBuf: byte[][] = Array.init slotCount0 (fun _ -> Array.Empty<byte>())

    // ---- send --------------------------------------------------------------------------
    let mutable sndUna = 0UL
    let mutable sndNxt = 0UL
    let mutable peerWnd = 65535
    let mutable peerScale = 0
    let mutable peerMss = 536
    let mutable sackOk = false
    let mutable reliable: byte[] = Array.zeroCreate 4096
    let mutable relBase = 0UL
    let mutable relLen = 0
    let mutable ring = Array.zeroCreate<SentSegment> 256
    let mutable ringHead = 0
    let mutable ringCount = 0
    let mutable inflight = 0
    let mutable rackSentNs = 0L
    /// True once a SACK block has confirmed out-of-order delivery after sndUna;
    /// pure in-order TLP probes without SACK evidence are never network reordering.
    let mutable sackReordSeen = false
    /// RFC 8985 reordering window: how far in send time behind a delivered segment an
    /// unacked one may sit before RACK calls it lost. Zero until this path shows reordering,
    /// then the observed span, capped at min(minRtt/4, 20 ms). A reordering path must not
    /// turn every reordered segment into a repair (that was 1.5-2x wire amplification on a
    /// 24 % loss link), while a path without reordering keeps exact fast loss detection.
    let mutable reoWndNs = 0L
    /// RTO fallback (independent of SACK and RACK): snd.una still for the RTO makes every
    /// unsacked segment sent before it lost; they go again from snd.una (go-back-N), paced.
    let mutable unaNs = 0L
    let mutable rtoShift = 0
    let mutable rtoActive = false
    let mutable rtoFireNs = 0L
    let mutable rtoSeq = 0UL
    let mutable rtoEnd = 0UL
    /// Set while a cumulative advance is released by a repaired segment: the segments behind
    /// it waited for the hole, so they give no RTT sample.
    let mutable heldBehindRepair = false
    // Delivery-rate pacer.
    let mutable delivered = 0L
    let mutable copyEstimate = 0L
    /// Payload bytes put on the wire: new frames and every repair copy.
    let mutable wireSent = 0L
    let mutable deliveredNs = now0
    let mutable lastDeliveredSentNs = now0
    let mutable srttNs = 0.0
    let mutable minRttNs = Int64.MaxValue
    let mutable minRttStamp = now0
    let bwRounds = Array.zeroCreate<float> 10
    let mutable round = 0L
    let mutable nextRoundDelivered = 0L
    let mutable btlBw = 0.0
    let mutable startup = true
    let mutable draining = false
    let mutable fullBw = 0.0
    let mutable fullBwRounds = 0
    let mutable cycle = 0
    let mutable cycleNs = now0
    let mutable tokens = 0.0
    let mutable tokensNs = now0
    let mutable synAckNs = now0
    /// The SYN-ACK options, sent again unchanged when the SYN repeats.
    let synAckOptions = Array.zeroCreate<byte> 12
    let mutable synAckLength = 0
    let mutable demoted = false
    /// Last time OnSegment or Service ran (stall detection), and the last tail repair.
    let mutable seenNs = now0
    let mutable tailNs = now0
    let mutable demotedShift = 0
    /// This round met the cwnd or the pacer at least once with a real backlog (not app-limited).
    let mutable limitedThisRound = false
    let mutable appLimitedRounds = 0
    /// Cells the session holds for sending (set by the carrier before a send round).
    let mutable backlog = 0

    let at (i: int) = (ringHead + i) % ring.Length

    let unwrapRx (seq: uint32) = rcvNxt + uint64 (int64 (int32 (seq - uint32 rcvNxt)))
    let unwrapTx (seq: uint32) = sndUna + uint64 (int64 (int32 (seq - uint32 sndUna)))

    let window () =
        // `ourScale` is set from the peer's own window-scale option in OnSyn (7, or 0 when
        // the peer sent none, RFC 7323 s2.2). Shifting unconditionally used to tell such a
        // peer 600000 >>> 7 = 4687 bytes, a 4.6 KiB window that caps it at about 41 KB/s at
        // 113 ms RTT; every modern Linux offers WS, so only a middlebox that strips the
        // option would have hit it.
        uint16 (min 65535 (windowBytes >>> ourScale))

    let send (flags: byte) (seq: uint64) (options: ReadOnlySpan<byte>) (payload: int) =
        let p = output.Scratch
        let n = Packet.build (Span p) localIp remoteIp localPort remotePort (uint32 seq) (uint32 rcvNxt) flags
                             (if flags &&& Packet.Syn <> 0uy then 65535us else window ()) options payload ipId
        ipId <- ipId + 1us
        output.Write(ReadOnlySpan(p, 0, n))

    let pacingGain () =
        if startup then 2.885
        elif draining then 1.0 / 2.885
        else
            match cycle with
            | 0 -> 1.25
            | 1 -> 0.75
            | _ -> 1.0

    let pacingRate () =
        // Bytes per ns. Before the first sample: 10 segments per handshake RTT.
        let bw = if btlBw > 0.0 then btlBw else 10.0 * 1415.0 / max 1e6 srttNs
        pacingGain () * bw

    let cwnd () =
        let l = max 1 frameLength
        if btlBw <= 0.0 || minRttNs = Int64.MaxValue then 10 * l
        else
            let gain = if startup then 2.885 else 2.0
            max (4 * l) (int (gain * btlBw * float minRttNs) + 3 * l)

    let refillTokens (now: int64) =
        let rate = pacingRate ()
        let depth = max (4.0 * float (max 1 frameLength)) (rate * 2_000_000.0)
        tokens <- min depth (tokens + rate * float (now - tokensNs))
        tokensNs <- now

    // ---- receive: frames ---------------------------------------------------------------

    let deliver (frame: Span<byte>) (start: uint64) =
        counters.CellsReceived <- counters.CellsReceived + 1L
        let wm = owner.OnFrame(pathId, frame)
        if wm <> UInt64.MaxValue then
            // The first authenticated cell after a gap tells which steps the gap held.
            for i in 0 .. ivCount - 1 do
                if start >= ivStart.[i] && ivWm.[i] = UInt64.MaxValue then ivWm.[i] <- wm

    let dropSlot (i: int) =
        slotFrame.[i] <- -1L
        slotFill.[i] <- 0

    let slotBudget () =
        let fl = max 1 frameLength
        max slotCount0 (windowBytes / fl + 2)

    let growSlots () =
        let budget = slotBudget ()
        if slotCap >= budget then false
        else
            let n = min budget (max slotCount0 (slotCap * 2))
            let nf = Array.create n -1L
            let nfill = Array.zeroCreate<int> n
            let nbuf = Array.init n (fun _ -> Array.Empty<byte>())
            Array.Copy(slotFrame, nf, slotCap)
            Array.Copy(slotFill, nfill, slotCap)
            Array.Copy(slotBuf, nbuf, slotCap)
            slotFrame <- nf
            slotFill <- nfill
            slotBuf <- nbuf
            slotCap <- n
            counters.ReasmGrows <- counters.ReasmGrows + 1L
            true

    let findSlot (index: int64) =
        let rec search () =
            let mutable s = -1
            let mutable free = -1
            for i in 0 .. slotCap - 1 do
                if slotFrame.[i] = index then s <- i
                elif free < 0 && slotFrame.[i] < 0L then free <- i
            if s >= 0 then s
            elif free >= 0 then
                slotFrame.[free] <- index
                slotFill.[free] <- 0
                if slotBuf.[free].Length < frameLength then slotBuf.[free] <- Array.zeroCreate frameLength
                free
            elif growSlots () then search ()
            else
                counters.ReasmPressure <- counters.ReasmPressure + 1L
                -1
        search ()

    /// Bytes [a, a + data.Length) are new in the data phase: cut them at frame borders.
    /// Returns how many bytes from the start were stored or delivered. If a fragment
    /// cannot be placed even after growing to the window budget, the suffix is left
    /// unstored so the caller can hold the ACK (backpressure) instead of dropping it.
    let takeBytes (a: uint64) (data: Span<byte>) =
        let l = uint64 frameLength
        let mutable pos = a
        let mutable rest = data
        let mutable stop = false
        while rest.Length > 0 && not stop do
            let index = int64 ((pos - data0) / l)
            let off = int ((pos - data0) % l)
            let n = min rest.Length (frameLength - off)
            let start = data0 + uint64 index * l
            if off = 0 && n = frameLength then
                deliver (rest.Slice(0, n)) start
                pos <- pos + uint64 n; bytesAccepted <- bytesAccepted + int64 n
                rest <- rest.Slice n
            else
                let s = findSlot index
                if s < 0 then stop <- true
                else
                    rest.Slice(0, n).CopyTo(Span(slotBuf.[s], off, n))
                    slotFill.[s] <- slotFill.[s] + n
                    if slotFill.[s] = frameLength then
                        counters.CellsSpanning <- counters.CellsSpanning + 1L
                        let index' = slotFrame.[s]
                        dropSlot s
                        deliver (Span(slotBuf.[s], 0, frameLength)) (data0 + uint64 index' * l)
                    pos <- pos + uint64 n; bytesAccepted <- bytesAccepted + int64 n
                    rest <- rest.Slice n
        int (pos - a)

    /// Drop fragment slots of frames that touch [lo, hi): they can never be whole.
    let dropSlotsIn (lo: uint64) (hi: uint64) =
        let l = uint64 frameLength
        for i in 0 .. slotCap - 1 do
            if slotFrame.[i] >= 0L then
                let s = data0 + uint64 slotFrame.[i] * l
                if s < hi && s + l > lo then
                    counters.CellsDropped <- counters.CellsDropped + 1L
                    dropSlot i

    let removeInterval (i: int) =
        for j in i .. ivCount - 2 do
            ivStart.[j] <- ivStart.[j + 1]
            ivEnd.[j] <- ivEnd.[j + 1]
            ivSince.[j] <- ivSince.[j + 1]
            ivWm.[j] <- ivWm.[j + 1]
        ivCount <- ivCount - 1

    /// Move rcvNxt over intervals that now touch it.
    let advance () =
        while ivCount > 0 && ivStart.[0] <= rcvNxt do
            if ivEnd.[0] > rcvNxt then rcvNxt <- ivEnd.[0]
            removeInterval 0

    /// Lease check (spec 6.8.2): data phase (after frame 0 and the head), path attached and
    /// not closing, no FIN seen. The advance never passes the observed right edge.
    let leaseValid () = dataPhase && attached && not closing && peerFinAt = UInt64.MaxValue

    /// An ACK can leave now, or its gap still needs the lease grace.
    let canFlushAck () =
        if not ackDue || state = TunState.Closed then false
        else
            let grace = ivCount > 0 && leaseValid ()
            let below = sackOk && dsackHi > dsackLo && dsackLo < rcvNxt
            not grace || below

    /// Read the first gap without changing its timer or the receive frontier.
    let firstGapAction (now: int64) =
        if state <> TunState.Established || ivCount = 0 || not (leaseValid ()) then struct (false, false)
        else
            let fast = ivWm.[0] <> UInt64.MaxValue && owner.Frontier > ivWm.[0]
            struct (fast || now - ivSince.[0] >= graceNs, fast)

    let removeSwallowed (i: int) =
        for j in i .. swCount - 2 do
            swStart.[j] <- swStart.[j + 1]
            swEnd.[j] <- swEnd.[j + 1]
        swCount <- swCount - 1

    /// Record [lo, hi) as swallowed (ranges kept in order, newest last).
    let recordSwallowed (lo: uint64) (hi: uint64) =
        if hi > lo then
            if swCount = swallowedMax then
                removeSwallowed 0
                counters.SwallowedForgotten <- counters.SwallowedForgotten + 1L
            swStart.[swCount] <- lo
            swEnd.[swCount] <- hi
            swCount <- swCount + 1

    /// [a, b) arrived below rcvNxt: the parts inside swallowed ranges are first arrivals (cut
    /// out of the record); returns the first contiguous run of really duplicate bytes, or
    /// (0, 0).
    let lateArrival (a: uint64) (b: uint64) =
        let mutable dupLo = 0UL
        let mutable dupHi = 0UL
        let mutable pos = a
        let mutable i = 0
        while pos < b do
            // The first swallowed range that ends after pos.
            while i < swCount && swEnd.[i] <= pos do i <- i + 1
            if i < swCount && swStart.[i] <= pos then
                let e = min b swEnd.[i]
                let n = int64 (e - pos)
                counters.LateFirstBytes <- counters.LateFirstBytes + n
                lateFirstLocal <- lateFirstLocal + n
                // Cut [pos, e) out of range i.
                let s0 = swStart.[i]
                let e0 = swEnd.[i]
                if s0 = pos && e0 = e then removeSwallowed i
                elif s0 = pos then swStart.[i] <- e
                elif e0 = e then swEnd.[i] <- pos
                else
                    // Split: [s0, pos) and [e, e0).
                    if swCount = swallowedMax then
                        removeSwallowed 0
                        counters.SwallowedForgotten <- counters.SwallowedForgotten + 1L
                        i <- i - 1
                    for j in swCount - 1 .. -1 .. i + 1 do
                        swStart.[j + 1] <- swStart.[j]
                        swEnd.[j + 1] <- swEnd.[j]
                    swCount <- swCount + 1
                    swEnd.[i] <- pos
                    swStart.[i + 1] <- e
                    swEnd.[i + 1] <- e0
                pos <- e
            else
                let e = if i < swCount then min b swStart.[i] else b
                if dupHi = dupLo then
                    dupLo <- pos
                    dupHi <- e
                pos <- e
        struct (dupLo, dupHi)

    /// Swallow the gap in front of interval 0.
    let swallow (fast: bool) =
        let lo = rcvNxt
        let hi = ivStart.[0]
        recordSwallowed lo hi
        dropSlotsIn lo hi
        let hole = int64 (hi - lo)
        counters.BytesSwallowed <- counters.BytesSwallowed + hole
        swallowedBytes <- swallowedBytes + hole
        rcvNxt <- ivEnd.[0]
        removeInterval 0
        advance ()
        counters.VirtualAcks <- counters.VirtualAcks + 1L
        if fast then counters.FastSwallows <- counters.FastSwallows + 1L
        ackDue <- true

    let gapBudget () =
        let fl = max 1 frameLength
        max gapCount0 (windowBytes / fl + 2)

    let growGaps () =
        let budget = gapBudget ()
        if gapCap >= budget then false
        else
            let n = min budget (max gapCount0 (gapCap * 2))
            let ns = Array.zeroCreate<uint64> (n + 2)
            let ne = Array.zeroCreate<uint64> (n + 2)
            let nsi = Array.zeroCreate<int64> (n + 2)
            let nw = Array.zeroCreate<uint64> (n + 2)
            Array.Copy(ivStart, ns, ivCount)
            Array.Copy(ivEnd, ne, ivCount)
            Array.Copy(ivSince, nsi, ivCount)
            Array.Copy(ivWm, nw, ivCount)
            ivStart <- ns
            ivEnd <- ne
            ivSince <- nsi
            ivWm <- nw
            gapCap <- n
            counters.GapGrows <- counters.GapGrows + 1L
            true

    /// Insert [a, b) into the interval set (b > rcvNxt, a > rcvNxt): new bytes only.
    let addInterval (a: uint64) (b: uint64) (now: int64) (data: Span<byte>) =
        // Walk the set; give takeBytes each piece not yet held.
        let mutable pos = a
        let mutable i = 0
        while i < ivCount && ivEnd.[i] <= pos do i <- i + 1
        let mutable storedHi = a
        let mutable blocked = false
        while pos < b && not blocked do
            if i < ivCount && ivStart.[i] <= pos then
                pos <- min b ivEnd.[i]
                storedHi <- pos
                i <- i + 1
            else
                let e = if i < ivCount then min b ivStart.[i] else b
                // A disjoint piece that touches neither neighbour is a new gap.
                let extendPrev = i > 0 && ivEnd.[i - 1] = pos
                let extendNext = i < ivCount && e >= ivStart.[i]
                if not extendPrev && not extendNext && ivCount >= gapCap && not (growGaps ()) then
                    counters.GapPressure <- counters.GapPressure + 1L
                    blocked <- true
                else
                    let n = takeBytes pos (data.Slice(int (pos - a), int (e - pos)))
                    storedHi <- pos + uint64 n
                    if n < int (e - pos) then blocked <- true
                    else pos <- e
        // Merge only the prefix we stored; an unstored suffix is not ACKed.
        if storedHi > a then
            let b = storedHi
            let mutable lo = a
            let mutable hi = b
            let mutable first = 0
            while first < ivCount && ivEnd.[first] < lo do first <- first + 1
            let mutable last = first
            while last < ivCount && ivStart.[last] <= hi do
                lo <- min lo ivStart.[last]
                hi <- max hi ivEnd.[last]
                last <- last + 1
            let since = if last > first then ivSince.[first] else now
            let wm = if last > first then ivWm.[first] else UInt64.MaxValue
            let removed = last - first
            if removed = 0 then
                // A new interval: a new gap in front of it.
                for j in ivCount - 1 .. -1 .. first do
                    ivStart.[j + 1] <- ivStart.[j]
                    ivEnd.[j + 1] <- ivEnd.[j]
                    ivSince.[j + 1] <- ivSince.[j]
                    ivWm.[j + 1] <- ivWm.[j]
                ivCount <- ivCount + 1
                counters.UpHoles <- counters.UpHoles + 1L
            else
                for j in 1 .. removed - 1 do removeInterval (first + 1)
            ivStart.[first] <- lo
            ivEnd.[first] <- hi
            ivSince.[first] <- since
            ivWm.[first] <- wm

    let onData (seq64: uint64) (data: Span<byte>) (now: int64) =
        // Trim what is old or past the window.
        let mutable a = seq64
        let mutable d = data
        if a < rcvNxt && d.Length > 0 then
            // From the receive history: a first arrival of swallowed bytes is no duplicate.
            let struct (lo, hi) = lateArrival a (min (a + uint64 d.Length) rcvNxt)
            if hi > lo then
                dsackLo <- lo
                dsackHi <- hi
        elif d.Length > 0 && dataPhase then
            // Wholly inside a held interval: a duplicate too.
            let e = a + uint64 d.Length
            for i in 0 .. ivCount - 1 do
                if ivStart.[i] <= a && e <= ivEnd.[i] then
                    dsackLo <- a
                    dsackHi <- e
        if a < rcvNxt then
            let cut = int (min (uint64 d.Length) (rcvNxt - a))
            d <- d.Slice cut
            a <- a + uint64 cut
        let limit = rcvNxt + uint64 windowBytes
        if a + uint64 d.Length > limit then d <- d.Slice(0, int (max 0UL (limit - min limit a)))
        ackDue <- true
        if d.Length > 0 then
            if not dataPhase then
                // In order only, into the head buffer.
                if a = rcvNxt && headLen + d.Length <= head.Length then
                    d.CopyTo(Span(head, headLen, d.Length))
                    headLen <- headLen + d.Length
                    rcvNxt <- rcvNxt + uint64 d.Length
                    if bootAt < 0 then
                        let at = ReadOnlySpan(head, 0, headLen).IndexOf(ReadOnlySpan "\r\n\r\n"B)
                        if at >= 0 && at + 4 <= Handshake.MaxHeadLength then bootAt <- at + 4
                        elif at >= 0 || headLen >= Handshake.MaxHeadLength then bootAt <- -2
            else
                if a = rcvNxt then
                    let n = takeBytes a d
                    rcvNxt <- rcvNxt + uint64 n
                    advance ()
                else addInterval a (a + uint64 d.Length) now d
                // Capacity is the window budget. A full table back-pressures in addInterval
                // (no early_retire swallow). Grace-timer / frontier swallow is unchanged.

    // ---- demotion ---------------------------------------------------------------------

    /// A path with data in flight and no delivery for max(1 s, 3 RTO) is demoted (spec: slow
    /// subflows are demoted, not dropped): it takes no new frames, so one dead path cannot
    /// hold the shared cwnd of the others. Its in-flight bytes stay in the group's sum (the
    /// hidden-bytes variant let the survivors overshoot the bottleneck); the repairs that go
    /// on are charged like any other, and the first delivery restores it.
    let isDemoted (now: int64) =
        let rto = max (1.15 * srttNs) (srttNs + 10_000_000.0)
        let after = max 1_000_000_000L (int64 (3.0 * rto))
        // Only when another path of the group still delivers: if all stall together
        // (an outage), demoting them all would only hold the recovery back.
        // A shared outage within the time also excludes it: paths come back a few ms apart,
        // and the last one back is not dead.
        let d = inflight > 0 && now - deliveredNs > after && group.AnyDeliveredSince(now - after)
                && now - group.LastOutageNs > after
        if d && not demoted then counters.Demotions <- counters.Demotions + 1L
        demoted <- d
        d

    // ---- clock jumps ------------------------------------------------------------------

    /// Last clock jump (stamp after the shift): segments sent before it crossed the stall.
    let mutable jumpNs = Int64.MinValue

    /// Cut a stall from the timeline: every send, delivery and timer stamp moves forward by d,
    /// so after the stall the timers run on from where they were. Without this, all timers
    /// expire at once (a repair storm) and the first ACKs give RTT samples as long as the stall.
    /// The shift is right only if the peer stalled too (the whole box paused). If only this
    /// process stalled, the peer's ACKs kept coming and wait in the TUN queue: against shifted
    /// send stamps they read as RTTs of a few ms. Segments that crossed a jump therefore give
    /// no RTT and no rate sample (onDelivered); see docs/cases/server-stall-rtt.md.
    let catchUp (now: int64) =
        let gap = now - seenNs
        seenNs <- now
        // Sample the gap and the outstanding-work state BEFORE the shift below.
        //
        // What this sample is allowed to claim: the interval since the previous `catchUp` call,
        // and the work-queue state at THIS entry, i.e. as left by whatever last touched this path.
        // What it must not claim: the state *during* the gap. `catchUp` is the first statement in
        // `OnSegmentCore`, `SendReliable`, `ServiceCore` and `TryBeginFrame`, so any of those
        // entries would itself have reset `seenNs`; but `OnSyn` mutates state (rcvNxt, peerMss,
        // window, sndUna, SYN-ACK) without sampling, and the clock source is bounded to 1 ms per
        // 32 reads, so a gap's interior is not observable here. Treat these counters as
        // "outstanding when the gap was noticed", never as "outstanding for the whole gap".
        if gap > counters.MaxGapNs then counters.MaxGapNs <- gap
        if gap > stallNs then
            if ringCount > 0 then
                counters.GapsWithUnacked <- counters.GapsWithUnacked + 1L
                if gap > counters.MaxUnackedGapNs then counters.MaxUnackedGapNs <- gap
            elif ivCount > 0 || rtoActive then counters.GapsWithWork <- counters.GapsWithWork + 1L
            else counters.GapsWithoutWork <- counters.GapsWithoutWork + 1L
            let d = gap - 1_000_000L
            counters.ClockJumps <- counters.ClockJumps + 1L
            jumpNs <- now
            for i in 0 .. ringCount - 1 do
                let s = &ring.[at i]
                s.SentNs <- s.SentNs + d
                s.DeliveredNs <- s.DeliveredNs + d
                s.FirstSentNs <- s.FirstSentNs + d
            if rackSentNs > 0L then rackSentNs <- rackSentNs + d
            deliveredNs <- deliveredNs + d
            lastDeliveredSentNs <- lastDeliveredSentNs + d
            minRttStamp <- minRttStamp + d
            cycleNs <- cycleNs + d
            synAckNs <- synAckNs + d
            tailNs <- tailNs + d
            unaNs <- unaNs + d
            if rtoActive then rtoFireNs <- rtoFireNs + d
            for i in 0 .. ivCount - 1 do ivSince.[i] <- ivSince.[i] + d

    // ---- send: delivery accounting -----------------------------------------------------

    let onDelivered (s: byref<SentSegment>) (now: int64) =
        delivered <- delivered + int64 s.Length
        copyEstimate <- copyEstimate + int64 s.Duplicates
        inflight <- inflight - s.Length
        // RACK time (RFC 8985 6.2): a repaired segment counts only when the ACK cannot be
        // for its first copy (at least min RTT after the repair). Otherwise a late ACK of the
        // first copy would make every segment sent before the repair look lost.
        // Reordering evidence (RFC 8985 6.2): either an unrepaired segment sent before one
        // already delivered arrived late, or a repaired segment was ACKed within less than
        // minRtt of its repair (proving its original copy arrived late — spurious retransmit).
        // Its span bounds how far an unacked segment may lag rackSentNs before RACK declares
        // it lost, stopping reordering from multiplying repairs on the wire.
        if rackSentNs > 0L then
            let cap = if minRttNs <> Int64.MaxValue then min (minRttNs / 4L) 20_000_000L else 20_000_000L
            if s.Retx = 0 && s.SentNs < rackSentNs then
                let span = min cap (max 1_000_000L (rackSentNs - s.SentNs))
                if span > reoWndNs then reoWndNs <- span
            elif sackReordSeen && s.Retx > 0 && minRttNs <> Int64.MaxValue && now - s.SentNs < minRttNs then
                let step = max 1_000_000L (max (minRttNs / 8L) (now - s.SentNs))
                let nextWnd = min cap (reoWndNs + step)
                if nextWnd > reoWndNs then reoWndNs <- nextWnd
        if s.SentNs > rackSentNs && (s.Retx = 0 || now - s.SentNs >= minRttNs) then rackSentNs <- s.SentNs
        let ackElapsed = now - s.DeliveredNs
        let sendElapsed = s.SentNs - s.FirstSentNs
        let interval = max ackElapsed sendElapsed
        deliveredNs <- now
        lastDeliveredSentNs <- s.SentNs
        group.OnDelivered(s.Length, s.GroupDelivered, s.GroupDeliveredNs, s.GroupFirstSentNs, s.SentNs, now)
        if s.Retx > 0 then heldBehindRepair <- true
        // Sent before the last clock jump: its RTT and rate are unknowable (see catchUp).
        // One compare on the normal path; the samples below are main's code unchanged.
        if s.SentNs < jumpNs then counters.JumpSamplesSkipped <- counters.JumpSamplesSkipped + 1L
        else
            if s.Retx = 0 && not heldBehindRepair && now >= s.SentNs then
                let rtt = float (now - s.SentNs)
                srttNs <- if srttNs = 0.0 then rtt else 0.875 * srttNs + 0.125 * rtt
                let r = now - s.SentNs
                if TraceGate.On then Trace.record Trace.Rtt (uint64 inflight) pathId (int (r / 1000L))
                if r < minRttNs || now - minRttStamp > 10_000_000_000L then
                    minRttNs <- r
                    minRttStamp <- now
            // One controller per bottleneck: once the group has an estimate it owns rate and
            // pacing, and this path keeps only RTT, RACK and RTO state (the two local phase
            // clocks were fighting over the same BDP).
            if interval > 0L && not group.Ready then
                let rate = float (delivered - s.Delivered) / float interval
                let slot = int (round % int64 bwRounds.Length)
                if rate > bwRounds.[slot] then bwRounds.[slot] <- rate
                btlBw <- Array.max bwRounds
        // Round trips counted by delivery (BBR).
        if s.Delivered >= nextRoundDelivered then
            nextRoundDelivered <- delivered
            let limited = limitedThisRound
            limitedThisRound <- false
            let last = bwRounds.[int (round % int64 bwRounds.Length)]
            round <- round + 1L
            // An app-limited round (the session had too little to send) carries the estimate
            // over instead of letting it decay: its low samples say nothing of the path.
            bwRounds.[int (round % int64 bwRounds.Length)] <- if limited then 0.0 else max last btlBw
            // Bulk after an app-limited spell: the old estimate came from small demand.
            // Probe from it again as at the start (BBR restart).
            if limited && appLimitedRounds >= 3 && not startup then
                startup <- true
                draining <- false
                fullBw <- 0.0
                fullBwRounds <- 0
            appLimitedRounds <- if limited then 0 else appLimitedRounds + 1
            // App-limited rounds say nothing about the bottleneck (BBR full-pipe check).
            if startup && limited then
                if btlBw >= fullBw * 1.25 then
                    fullBw <- btlBw
                    fullBwRounds <- 0
                else
                    fullBwRounds <- fullBwRounds + 1
                    if fullBwRounds >= 3 then
                        startup <- false
                        draining <- true

    /// Send segment i again: exact bytes for the head part, new content for a frame
    /// (borrowed shell). Two copies, the HOLE-2 redundancy that keeps one lost repair copy
    /// from costing another round on a lossy link. It no longer grows to four: on a 24 %
    /// loss path the escalation doubled the wire cost of every repeatedly-repaired segment,
    /// and a segment that loses both copies is repaired again in the next round.
    /// RACK time (RFC 8985 6.2) is the comment below, kept where the stamps it reads are set.
    let repair (i: int) (now: int64) =
        let s = &ring.[at i]
        let p = output.Scratch
        let copies = 2
        let mutable filled = true
        if s.Kind = 0uy then
            let off = int (s.Seq - relBase)
            Buffer.BlockCopy(reliable, off, p, Packet.DataOffset, s.Length)
        else
            filled <- owner.ProduceFill(pathId, Span(p, Packet.DataOffset, s.Length))
            if filled then counters.BorrowRetx <- counters.BorrowRetx + 1L
        if filled then
            let n = Packet.build (Span p) localIp remoteIp localPort remotePort (uint32 s.Seq) (uint32 rcvNxt)
                                 (Packet.Ack ||| Packet.Psh) (window ()) ReadOnlySpan.Empty s.Length ipId
            ipId <- ipId + 1us
            for _ in 1 .. copies do output.Write(ReadOnlySpan(p, 0, n))
            wireSent <- wireSent + int64 (copies * s.Length)
            counters.SegmentsSent <- counters.SegmentsSent + int64 copies
            counters.SegmentsLost <- counters.SegmentsLost + 1L
            tokens <- tokens - float (copies * n)
            // Every copy crosses the same bottleneck, a demoted path's too: all are charged to the
            // group (payload bytes, as its rate samples count).
            if demoted then counters.DemotedRepairs <- counters.DemotedRepairs + 1L
            group.OnRepair(copies * s.Length)
            s.Duplicates <- s.Duplicates + (copies - 1) * s.Length
            s.Retx <- s.Retx + 1
            s.SentNs <- now
            s.Delivered <- delivered
            s.DeliveredNs <- deliveredNs
            s.FirstSentNs <- lastDeliveredSentNs
            s.GroupDelivered <- group.DeliveredBytes
            s.GroupDeliveredNs <- group.DeliveredNs
            s.GroupFirstSentNs <- group.FirstSentNs
            ackDue <- false

    /// Index of the segment that starts at seq, or -1.
    let find (seq: uint64) =
        let mutable lo = 0
        let mutable hi = ringCount - 1
        let mutable found = -1
        while found < 0 && lo <= hi do
            let mid = (lo + hi) / 2
            let s = ring.[at mid].Seq
            if s = seq then found <- mid
            elif s < seq then lo <- mid + 1
            else hi <- mid - 1
        if found >= 0 then found else lo

    /// Repairs wait in order of request; the pacer spaces what is actually sent.
    let repairQ = System.Collections.Generic.Queue<uint64>(1024)

    /// RACK, the tail timer and the RTO only ask for a repair; drainRepairs sends it.
    let requestRepair (i: int) =
        let s = &ring.[at i]
        if not s.Due then
            s.Due <- true
            repairQ.Enqueue s.Seq
            counters.RepairRequests <- counters.RepairRequests + 1L

    /// May a repair of bytes on the wire go now? The shared pacer (or this path's own before
    /// the group has an estimate) with a bounded recovery reserve: repairs may run the tokens
    /// down to -reserve, never further, and every byte stays charged.
    let repairAllowed (bytes: int) (now: int64) =
        if group.Ready then group.TryTakeRepair(bytes, frameLength)
        else
            refillTokens now
            let reserve = max (16.0 * float (max 1 frameLength)) (pacingRate () * 2_000_000.0)
            tokens - float bytes >= -reserve

    let drainRepairs (now: int64) =
        let mutable go = true
        while go && repairQ.Count > 0 do
            let seq = repairQ.Peek()
            let i = if ringCount > 0 then find seq else 0
            if i < ringCount && ring.[at i].Seq = seq && ring.[at i].Due && not ring.[at i].Sacked then
                let s = &ring.[at i]
                let copies = 2
                if repairAllowed (copies * s.Length) now then
                    repairQ.Dequeue() |> ignore
                    s.Due <- false
                    repair i now
                else
                    counters.RepairsDeferred <- counters.RepairsDeferred + 1L
                    go <- false
            else
                // Acked, SACKed or already gone: nothing to send.
                repairQ.Dequeue() |> ignore
                if i < ringCount && ring.[at i].Seq = seq then ring.[at i].Due <- false

    let onAck (ack64: uint64) (options: ReadOnlySpan<byte>) (wnd: uint16) (now: int64) =
        peerWnd <- int wnd <<< peerScale
        if ack64 > sndUna && ack64 <= sndNxt then
            sndUna <- ack64
            unaNs <- now
            rtoShift <- 0
            heldBehindRepair <- false
            while ringCount > 0 && ring.[ringHead].Seq + uint64 ring.[ringHead].Length <= sndUna do
                let s = &ring.[ringHead]
                if not s.Sacked then onDelivered &s now
                ringHead <- (ringHead + 1) % ring.Length
                ringCount <- ringCount - 1
            heldBehindRepair <- false
            if relLen > 0 && sndUna >= relBase + uint64 relLen then relLen <- 0
        // SACK blocks.
        let mutable blocksAt = 0
        let n = if sackOk then Packet.sackBlocks options &blocksAt else 0
        let mutable highSacked = sndUna
        for b in 0 .. n - 1 do
            let l = unwrapTx (BinaryPrimitives.ReadUInt32BigEndian(options.Slice(blocksAt + 8 * b)))
            let r = unwrapTx (BinaryPrimitives.ReadUInt32BigEndian(options.Slice(blocksAt + 8 * b + 4)))
            if l >= sndUna && r <= sndNxt && l < r then
                if l > sndUna then sackReordSeen <- true
                if r > highSacked then highSacked <- r
                let mutable i = find l
                while i < ringCount && ring.[at i].Seq + uint64 ring.[at i].Length <= r do
                    let s = &ring.[at i]
                    if not s.Sacked && s.Seq >= l then
                        s.Sacked <- true
                        onDelivered &s now
                    i <- i + 1
            elif l < sndUna && l < r then
                // D-SACK (RFC 2883 / RFC 8985 6.2): peer received a duplicate of already-ACKed
                // bytes, proving a spurious retransmit; widen the reordering window.
                sackReordSeen <- true
                let cap = if minRttNs <> Int64.MaxValue then min (minRttNs / 4L) 20_000_000L else 20_000_000L
                if cap > reoWndNs then reoWndNs <- max reoWndNs (min cap (max 1_000_000L (cap / 2L)))
        // RACK (RFC 8985): a segment sent before a delivered one, by more than the reordering
        // window, is lost. The window is 0 until this path shows reordering (see reoWndNs).
        if n > 0 then
            let mutable i = 0
            while i < ringCount && ring.[at i].Seq < highSacked do
                let s = &ring.[at i]
                if not s.Sacked && s.SentNs + reoWndNs < rackSentNs then requestRepair i
                i <- i + 1
            drainRepairs now
        if draining && inflight <= int (btlBw * float minRttNs) then
            draining <- false
            cycle <- 2
            cycleNs <- now

    member _.State = state
    member _.Counters = counters
    member _.RemoteIp = remoteIp
    member _.RemotePort = remotePort
    member _.Attached = attached
    member _.DataPhase = dataPhase
    member _.Srtt = srttNs
    member _.BottleneckBw = btlBw
    /// Send time of the newest segment known delivered (RACK).
    member _.RackSentNs = rackSentNs
    /// The proven reordering span (RFC 8985): 0 until this path shows a late arrival, then the
    /// observed span capped at min(minRtt/4, 20 ms). Exposed so a test can assert that a pure
    /// reorder widens the window instead of turning into a repair.
    member _.ReoWndNs = reoWndNs
    member _.SrttNs = srttNs
    member _.Inflight = inflight
    /// Unique payload bytes acknowledged (ACK/SACK evidence).
    member _.UniqueDelivered = delivered
    /// Estimate: extra repair copies of acknowledged segments (see IRateMember.CopyEstimate).
    member _.CopyEstimate = copyEstimate
    /// Payload bytes put on the wire, every copy included.
    member _.WireSentBytes = wireSent
    /// TCP payload bytes accepted into reassembly (in-order or stored out-of-order).
    member _.BytesAccepted = bytesAccepted
    /// Payload bytes this path virtually ACKed (holes swallowed, never given to the frame reader).
    member _.BytesSwallowed = swallowedBytes
    /// Cumulative ACK minus the start of the data phase (0 before it). Comparable to acc_b + sw_b.
    member _.BytesCumAcked = if dataPhase then int64 (rcvNxt - data0) else 0L
    member _.LateFirstBytes = lateFirstLocal
    member _.Describe() =
        "bw_mbit=" + (btlBw * 8000.0).ToString("F1") + " srtt_ms=" + (srttNs / 1e6).ToString("F1")
        + " min_rtt_ms=" + (if minRttNs = Int64.MaxValue then "-" else (float minRttNs / 1e6).ToString("F1"))
        + " cwnd=" + string (cwnd ()) + " inflight=" + string inflight + " peer_wnd=" + string peerWnd
        + " phase=" + (if startup then "startup" elif draining then "drain" else "probe" + string cycle)
        + " rq=" + string repairQ.Count + " req=" + string counters.RepairRequests + " defer=" + string counters.RepairsDeferred
        + " acc_b=" + string bytesAccepted + " sw_b=" + string swallowedBytes
        + " ack_b=" + string (if dataPhase then int64 (rcvNxt - data0) else 0L)
        + " late_b=" + string lateFirstLocal
        + " reo_ms=" + (float reoWndNs / 1e6).ToString("F1")
        + (if demoted then " demoted" else "")
    /// The HTTP head and bootstrap frame are whole: BootFrame holds the frame. -2: bad head.
    member _.BootState = if bootAt = -2 then -2 elif bootAt >= 0 && headLen >= bootAt + Layout.BootFrameLength then 1 else 0
    member _.BootFrame = ReadOnlySpan(head, bootAt, Layout.BootFrameLength)
    /// True while timers or sending may need a short wait.
    member _.AckPending = ackDue && state <> TunState.Closed
    /// Yield the receive loop only when Service can act on the pending ACK or gap.
    /// The query does not move timers; Service still applies clock-jump handling.
    member _.AckNeedsService(now: int64) =
        if canFlushAck () then true
        else
            let struct (ready, _) = firstGapAction now
            ready
    member _.Busy = ringCount > 0 || ivCount > 0 || ackDue
    member _.Closed = state = TunState.Closed

    /// The first SYN: take the peer's options and answer SYN-ACK (MSS, SACK, window scale).
    member _.OnSyn(seq: uint32, options: ReadOnlySpan<byte>, mss: int, now: int64) =
        let mutable m = 0
        let mutable ws = -1
        let mutable sack = false
        Packet.synOptions options &m &ws &sack
        rcvNxt <- wrapBase + uint64 seq + 1UL
        headAt <- rcvNxt
        peerMss <- if m > 0 then min m mss else 536
        peerScale <- if ws >= 0 then min 14 ws else 0
        // Without WS the receive window is never scaled: advertise min (windowBytes, 65535).
        ourScale <- if ws >= 0 then 7 else 0
        sackOk <- sack
        sndUna <- wrapBase + uint64 isn
        sndNxt <- sndUna + 1UL
        relBase <- sndNxt
        let o = Span<byte>(Array.zeroCreate 12)
        o.[0] <- 2uy
        o.[1] <- 4uy
        BinaryPrimitives.WriteUInt16BigEndian(o.Slice 2, uint16 mss)
        let mutable len = 4
        if sack then
            o.[len] <- 1uy
            o.[len + 1] <- 1uy
            o.[len + 2] <- 4uy
            o.[len + 3] <- 2uy
            len <- len + 4
        if ws >= 0 then
            o.[len] <- 1uy
            o.[len + 1] <- 3uy
            o.[len + 2] <- 3uy
            o.[len + 3] <- byte ourScale
            len <- len + 4
        synAckNs <- now
        o.Slice(0, len).CopyTo(Span synAckOptions)
        synAckLength <- len
        send (Packet.Syn ||| Packet.Ack) sndUna (Span<byte>.op_Implicit (o.Slice(0, len))) 0

    /// A non-SYN segment of this connection; packet is the whole IP packet.
    member private _.OnSegmentCore(seg: Packet.Segment, packet: Span<byte>, now: int64) =
        catchUp now
        if seg.Flags &&& Packet.Rst <> 0uy then state <- TunState.Closed
        elif state <> TunState.Closed then
            if seg.Flags &&& Packet.Syn <> 0uy then
                // A repeated SYN: answer again with the same SYN-ACK.
                if state = TunState.SynReceived then
                    send (Packet.Syn ||| Packet.Ack) sndUna (ReadOnlySpan(synAckOptions, 0, synAckLength)) 0
            elif seg.Flags &&& Packet.Ack <> 0uy then
                let ack64 = unwrapTx seg.AckNo
                if state = TunState.SynReceived && ack64 = sndUna + 1UL then
                    state <- TunState.Established
                    sndUna <- ack64
                    srttNs <- float (max 1_000_000L (now - synAckNs))
                if state = TunState.Established then
                    onAck ack64 (Span<byte>.op_Implicit (packet.Slice(seg.OptionsAt, seg.OptionsLength))) seg.Window now
                    let seq64 = unwrapRx seg.Seq
                    if seg.PayloadLength > 0 then onData seq64 (packet.Slice(seg.PayloadAt, seg.PayloadLength)) now
                    if seg.Flags &&& Packet.Fin <> 0uy then
                        peerFinAt <- seq64 + uint64 seg.PayloadLength
                        ackDue <- true
                    if peerFinAt <> UInt64.MaxValue && rcvNxt >= peerFinAt then
                        // The carrier ends (not a flow FIN): answer FIN and close.
                        rcvNxt <- peerFinAt + 1UL
                        send (Packet.Fin ||| Packet.Ack) sndNxt ReadOnlySpan.Empty 0
                        state <- TunState.Closed

    /// Bytes this end must deliver exactly (HTTP head, bootstrap reply): sent at once.
    member _.SendReliable(data: ReadOnlySpan<byte>) =
        if relLen + data.Length > reliable.Length then Array.Resize(&reliable, relLen + data.Length)
        data.CopyTo(Span(reliable, relLen, data.Length))
        relLen <- relLen + data.Length
        let mss = peerMss
        let mutable at' = 0
        // Bounded NowNs is enough for send stamps (≤1 ms / 32 reads). FreshNs
        // is reserved for seal and repair deadlines in TxHalf.
        let now = clock.NowNs()
        catchUp now
        while at' < data.Length do
            let n = min mss (data.Length - at')
            data.Slice(at', n).CopyTo(Span(output.Scratch, Packet.DataOffset, n))
            if ringCount = ring.Length then
                let next = Array.zeroCreate<SentSegment> (ring.Length * 2)
                for i in 0 .. ringCount - 1 do next.[i] <- ring.[at i]
                ring <- next
                ringHead <- 0
            if ringCount = 0 then unaNs <- now
            ring.[at ringCount] <- { Seq = sndNxt; Length = n; Kind = 0uy; Sacked = false; Retx = 0; SentNs = now; Duplicates = 0; Due = false
                                     Delivered = delivered; DeliveredNs = deliveredNs; FirstSentNs = lastDeliveredSentNs
                                     GroupDelivered = group.DeliveredBytes; GroupDeliveredNs = group.DeliveredNs; GroupFirstSentNs = group.FirstSentNs }
            ringCount <- ringCount + 1
            inflight <- inflight + n
            send (Packet.Ack ||| Packet.Psh) sndNxt ReadOnlySpan.Empty n
            counters.SegmentsSent <- counters.SegmentsSent + 1L
            sndNxt <- sndNxt + uint64 n
            at' <- at' + n

    /// Timers: swallow gaps past the grace or proven by the decode frontier, repair tail
    /// losses (constant 1.15 x SRTT, no back-off), pending ACK, ProbeBW gain cycle.
    member private _.ServiceCore(now: int64) =
        catchUp now
        isDemoted now |> ignore
        if state = TunState.Established then
            let mutable go = true
            while go && ivCount > 0 do
                let struct (ready, fast) = firstGapAction now
                if ready then swallow fast
                else go <- false
            // Fragments that can no longer complete.
            if dataPhase then
                let l = uint64 frameLength
                for i in 0 .. slotCap - 1 do
                    if slotFrame.[i] >= 0L && data0 + uint64 slotFrame.[i] * l + l <= rcvNxt then
                        counters.CellsDropped <- counters.CellsDropped + 1L
                        dropSlot i
            // Tail repair.
            // A tail is only a tail when nothing was delivered for a whole timer: while ACKs
            // come in, a growing queue makes old segments look late (spurious repairs).
            let rto = int64 (max (1.15 * srttNs) (srttNs + 10_000_000.0))
            // One batch per timer, not one per call: later calls in the same timer would
            // otherwise repair the next segments, and so on through the whole ring.
            // A demoted path (dead while others deliver) probes with one repair per timer and
            // backs the timer off (x2 per firing, up to x64): its copies cost the shared
            // bottleneck. The first delivery ends the demotion and the back-off.
            if not demoted then demotedShift <- 0
            let tailEvery = if demoted then rto <<< demotedShift else rto
            if ringCount > 0 && srttNs > 0.0 && now - deliveredNs > rto && now - tailNs > tailEvery then
                let mutable i = 0
                let mutable budget = if demoted then 1 else tailBudget
                if demoted then demotedShift <- min 6 (demotedShift + 1)
                while i < ringCount && budget > 0 do
                    let s = &ring.[at i]
                    // Any segment older than the timer: RACK repairs only below the highest
                    // SACK, so a segment above it sent before rackSentNs (before a delivered
                    // repair) has no other way back once an outage silences every ACK.
                    if not s.Sacked && not s.Due && now - s.SentNs > rto then
                        requestRepair i
                        counters.TailRepairs <- counters.TailRepairs + 1L
                        budget <- budget - 1
                    i <- i + 1
                if budget < (if demoted then 1 else tailBudget) then tailNs <- now
            // RTO fallback: works with any peer (no SACK, SACK stripped). Base 2 x the tail
            // timer, at least 200 ms, doubled per episode up to 3.2 s; reset when snd.una moves.
            if ringCount > 0 && srttNs > 0.0 then
                let rto = int64 (max (1.15 * srttNs) (srttNs + 10_000_000.0))
                let timeout = min 3_200_000_000L ((max (2L * rto) 200_000_000L) <<< rtoShift)
                if now - unaNs > timeout then
                    rtoActive <- true
                    rtoFireNs <- now
                    rtoSeq <- sndUna
                    rtoEnd <- sndNxt
                    rtoShift <- min 4 (rtoShift + 1)
                    unaNs <- now
                    counters.Rtos <- counters.Rtos + 1L
            // Go-back-N is for a path that may come back as a whole; a demoted one only probes.
            if rtoActive && not demoted then
                if rtoSeq < sndUna then rtoSeq <- sndUna
                let mutable i = if ringCount > 0 then find rtoSeq else 0
                let mutable budget = tailBudget
                while i < ringCount && budget > 0 && ring.[at i].Seq < rtoEnd do
                    let s = &ring.[at i]
                    if not s.Sacked && not s.Due && s.SentNs < rtoFireNs then
                        requestRepair i
                        budget <- budget - 1
                    rtoSeq <- s.Seq + uint64 s.Length
                    i <- i + 1
                if i >= ringCount || rtoSeq >= rtoEnd then rtoActive <- false
            drainRepairs now
            // The 8-phase gain cycle belongs to this path's own pacer. Once the group holds
            // the estimate it is the single controller of the shared bottleneck (spec Q-2),
            // so rotating a second phase clock here only spends CPU: pacingGain and
            // cwnd are not consulted on the group path.
            if not group.Ready && not startup && not draining && minRttNs <> Int64.MaxValue
               && now - cycleNs > minRttNs then
                cycle <- (cycle + 1) % 8
                cycleNs <- now

    /// Send the pending ACK, unless a gap is in its grace (spec 6.8.2: no duplicate ACK and
    /// no SACK while the lease covers it).
    member _.FlushAck() =
        if canFlushAck () then
            let dsack = sackOk && dsackHi > dsackLo
            let grace = ivCount > 0 && leaseValid ()
            // A duplicate above the cumulative ACK is reported in the RFC 2883 two-block form
            // (the D-SACK, then the SACK block holding it), which shows the hole: in the grace
            // it waits until the gap is filled or swallowed.
            let above = dsack && dsackLo >= rcvNxt
            let mutable c = -1
            if above then
                for i in 0 .. ivCount - 1 do
                    if ivStart.[i] <= dsackLo && dsackHi <= ivEnd.[i] then c <- i
            // In the grace only a D-SACK below the cumulative ACK may go out, alone: interval
            // blocks would be duplicate ACKs; such a D-SACK block covers no new data.
            let blocks = if grace || not sackOk then 0 else min (if dsack then 3 else 4) ivCount
            let d = if dsack then 1 else 0
            if blocks + d > 0 then
                let o = Span<byte>(sackOption, 0, 4 + 8 * (blocks + d))
                o.[0] <- 1uy
                o.[1] <- 1uy
                o.[2] <- 5uy
                o.[3] <- byte (2 + 8 * (blocks + d))
                if dsack then
                    BinaryPrimitives.WriteUInt32BigEndian(o.Slice 4, uint32 dsackLo)
                    BinaryPrimitives.WriteUInt32BigEndian(o.Slice 8, uint32 dsackHi)
                    counters.Dsacks <- counters.Dsacks + 1L
                // Interval order, the one holding an above-ACK duplicate first.
                for k in 0 .. blocks - 1 do
                    let i = if c < 0 then k elif k = 0 then c elif k - 1 < c then k - 1 else k
                    BinaryPrimitives.WriteUInt32BigEndian(o.Slice(4 + 8 * (k + d)), uint32 ivStart.[i])
                    BinaryPrimitives.WriteUInt32BigEndian(o.Slice(8 + 8 * (k + d)), uint32 ivEnd.[i])
                send Packet.Ack sndNxt (Span<byte>.op_Implicit o) 0
            else send Packet.Ack sndNxt ReadOnlySpan.Empty 0
            dsackLo <- 0UL
            dsackHi <- 0UL
            // A grace-time D-SACK leaves the gap's ACK for later.
            ackDue <- grace

    /// Last resort (the causes are fixed where found): an exception from this path or its
    /// owner fails only that owner (one session) and resets this connection.
    member private this.Faulted(ex: exn) =
        counters.Faults <- counters.Faults + 1L
        if attached then owner.Fail("path " + string pathId + ": " + ex.GetType().Name + ": " + ex.Message)
        this.Abort()

    /// A non-SYN segment of this connection; packet is the whole IP packet.
    member this.OnSegment(seg: Packet.Segment, packet: Span<byte>, now: int64) =
        try this.OnSegmentCore(seg, packet, now)
        with ex -> this.Faulted ex

    /// Timers (see ServiceCore).
    member this.Service(now: int64) =
        // The last backlog the session reported still stands: not an app-limited round.
        if backlog >= RateGroup.BacklogFrames then group.NoteBacklog()
        // A waiting repair the pacer has not paid for is a backlog the transport holds, not
        // an application that ran dry: the group must not read this round as app-limited.
        if repairQ.Count > repairBurst then group.NoteLimited()
        try this.ServiceCore now
        with ex -> this.Faulted ex

    /// Abort: RST to the peer.
    member _.Abort() =
        if state <> TunState.Closed then
            send (Packet.Rst ||| Packet.Ack) sndNxt ReadOnlySpan.Empty 0
            state <- TunState.Closed

    interface IRateMember with
        // Use the flag ServiceCore refreshes once per tick; do not call
        // isDemoted(NowNs) here (that was O(n) AnyDeliveredSince per path).
        // A demoted path takes no new frames, so its bytes are not queued at the shared
        // bottleneck: keeping them in the sum starved every healthy path (TunTests: one dead
        // peer left inflight=571660 while goodput was 0). Its repairs are still charged.
        member _.Inflight = if demoted then 0 else inflight
        member _.Delivered = delivered
        member _.CopyEstimate = copyEstimate
        member _.MinRttNs = minRttNs
        member _.LastDeliveredNs = deliveredNs

    interface IPath with
        member _.PathId with get () = pathId and set v = pathId <- v
        member _.FrameLength
            with get () = frameLength
            and set v = frameLength <- v
        member _.Alive = state <> TunState.Closed

        /// Start the data phase after the bootstrap frame; replay bytes already held.
        member _.Attach(o) =
            owner <- o
            attached <- true
            dataPhase <- true
            data0 <- headAt + uint64 (bootAt + Layout.BootFrameLength)
            let extra = headLen - (bootAt + Layout.BootFrameLength)
            if extra > 0 then takeBytes data0 (Span(head, bootAt + Layout.BootFrameLength, extra)) |> ignore
            head <- Array.Empty<byte>()
            headLen <- 0

        member _.Backlog
            with set v =
                backlog <- v
                // Data waiting is not an app-limited round for the group's startup check.
                if v >= RateGroup.BacklogFrames then group.NoteBacklog()
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()
        member _.UnsentBytes = 0

        member _.TryBeginFrame(frame) =
            // A send stamps the ring with now, so the stall shift must happen first: a frame
            // sent after a pause and shifted later would carry a future send time, give a
            // negative RTT and, through the group's min RTT, a negative cwnd (no sends ever).
            // NowNs is already bound to 1 ms / 32 reads; FreshNs per frame was a 20/200 0%
            // uplink regression vs main (and vs dropping CachedClock entirely).
            let now = clock.NowNs()
            catchUp now
            if state <> TunState.Established || not attached || frameLength > peerMss then false
            // Waiting repairs go before new frames, but only while they are more than one
            // burst: a queue that never empties (the pacer defers, the hole is on a slow
            // path) must not stop this path from carrying new data, which is what turned a
            // lossy link into a retransmit-only one.
            elif repairQ.Count > 0 && (drainRepairs now; repairQ.Count > repairBurst) then
                group.NoteLimited()
                false
            else
                let l = frameLength
                let bulk = backlog >= RateGroup.BacklogFrames
                if int (sndNxt + uint64 l - sndUna) > peerWnd then
                    // The peer, not the application, is holding this path: a real sample.
                    if bulk then group.NoteLimited()
                    false
                elif isDemoted now then
                    // Demotion held this path, not an idle application: the round is a
                    // real sample, not evidence that the sender ran dry.
                    if bulk then group.NoteLimited()
                    false
                elif group.Ready then
                    // The group gates the shared bottleneck; this path keeps its estimate.
                    if group.TryTake(l, bulk) then
                        frame <- Span(output.Scratch, Packet.DataOffset, l)
                        true
                    else
                        if bulk then limitedThisRound <- true
                        false
                elif inflight + l > cwnd () then
                    if bulk then limitedThisRound <- true
                    false
                else
                    refillTokens now
                    if tokens < 0.0 then
                        if bulk then limitedThisRound <- true
                        false
                    else
                        frame <- Span(output.Scratch, Packet.DataOffset, l)
                        true

        member _.CommitFrame() =
            let l = frameLength
            let now = clock.NowNs()
            if ringCount = ring.Length then
                let next = Array.zeroCreate<SentSegment> (ring.Length * 2)
                for i in 0 .. ringCount - 1 do next.[i] <- ring.[at i]
                ring <- next
                ringHead <- 0
            if inflight = 0 then
                deliveredNs <- now
                lastDeliveredSentNs <- now
            if group.Inflight = 0 then group.OnSendStart now
            if ringCount = 0 then unaNs <- now
            ring.[at ringCount] <- { Seq = sndNxt; Length = l; Kind = 1uy; Sacked = false; Retx = 0; SentNs = now; Duplicates = 0; Due = false
                                     Delivered = delivered; DeliveredNs = deliveredNs; FirstSentNs = lastDeliveredSentNs
                                     GroupDelivered = group.DeliveredBytes; GroupDeliveredNs = group.DeliveredNs; GroupFirstSentNs = group.FirstSentNs }
            ringCount <- ringCount + 1
            inflight <- inflight + l
            let p = output.Scratch
            let n = Packet.build (Span p) localIp remoteIp localPort remotePort (uint32 sndNxt) (uint32 rcvNxt)
                                 (Packet.Ack ||| Packet.Psh) (window ()) ReadOnlySpan.Empty l ipId
            ipId <- ipId + 1us
            output.Write(ReadOnlySpan(p, 0, n))
            counters.SegmentsSent <- counters.SegmentsSent + 1L
            wireSent <- wireSent + int64 l
            tokens <- tokens - float n
            group.OnSent l
            sndNxt <- sndNxt + uint64 l
            ackDue <- false

        member this.Close() =
            closing <- true
            this.Abort()
