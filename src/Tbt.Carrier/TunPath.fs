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
    member val TailRepairs = 0L with get, set
    /// RTO fallback episodes: snd.una did not move for the RTO (no SACK needed).
    member val Rtos = 0L with get, set
    /// Times a path was demoted: data in flight, nothing delivered for the demotion time.
    member val Demotions = 0L with get, set
    /// Exceptions caught at a path entry point: the owner failed, the connection was reset.
    member val Faults = 0L with get, set
    /// Gaps between two calls longer than the stall limit, cut from the timeline.
    member val ClockJumps = 0L with get, set

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
      /// Repair copies beyond the first, in bytes (HOLE-2 duplicates on the wire).
      mutable Duplicates: int }

type TunState =
    | SynReceived = 0
    | Established = 1
    | Closed = 2

[<Sealed>]
type TunPath(output: ITunOutput, counters: TunCounters, localIp: uint32, localPort: uint16,
             remoteIp: uint32, remotePort: uint16, isn: uint32, clock: IClock, group: RateGroup) =
    let now0 = clock.NowNs()
    // ---- constants ---------------------------------------------------------------------
    static let windowBytes = 4 <<< 20
    static let ourScale = 7
    static let maxGaps = 16
    static let slotCount = 32
    static let graceNs = 15_000_000L
    /// A gap this long between two calls is a stall (process or whole box), not network time.
    static let stallNs = 300_000_000L
    /// Tail repairs per timer: the oldest few; their SACKs then let RACK find the rest.
    static let tailBudget = 4
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
    let mutable peerFinAt = UInt64.MaxValue
    let ivStart = Array.zeroCreate<uint64> (maxGaps + 2)
    let ivEnd = Array.zeroCreate<uint64> (maxGaps + 2)
    /// When the gap in front of interval i was first seen.
    let ivSince = Array.zeroCreate<int64> (maxGaps + 2)
    /// carrier_wm of the first authenticated cell after the gap in front of interval i.
    let ivWm = Array.zeroCreate<uint64> (maxGaps + 2)
    let mutable ivCount = 0
    let mutable ackDue = false
    // Head phase: in-order bytes only, until the bootstrap frame is whole.
    let mutable head: byte[] = Array.zeroCreate headCap
    let mutable headLen = 0
    let mutable headAt = 0UL
    let mutable bootAt = -1
    let mutable dataPhase = false
    let mutable data0 = 0UL
    // Partial frames (spec 6.8.3 fragment slots).
    let slotFrame = Array.create slotCount -1L
    let slotFill = Array.zeroCreate<int> slotCount
    let slotBuf: byte[][] = Array.init slotCount (fun _ -> Array.Empty<byte>())

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
    let mutable duplicatesDelivered = 0L
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
    /// This round met the cwnd or the pacer at least once with a real backlog (not app-limited).
    let mutable limitedThisRound = false
    let mutable appLimitedRounds = 0
    /// Cells the session holds for sending (set by the carrier before a send round).
    let mutable backlog = 0

    let at (i: int) = (ringHead + i) % ring.Length

    let unwrapRx (seq: uint32) = rcvNxt + uint64 (int64 (int32 (seq - uint32 rcvNxt)))
    let unwrapTx (seq: uint32) = sndUna + uint64 (int64 (int32 (seq - uint32 sndUna)))

    let window () = uint16 (min 65535 (windowBytes >>> ourScale))

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

    /// Bytes [a, a + data.Length) are new in the data phase: cut them at frame borders.
    let takeBytes (a: uint64) (data: Span<byte>) =
        let l = uint64 frameLength
        let mutable pos = a
        let mutable rest = data
        while rest.Length > 0 do
            let index = int64 ((pos - data0) / l)
            let off = int ((pos - data0) % l)
            let n = min rest.Length (frameLength - off)
            let start = data0 + uint64 index * l
            if off = 0 && n = frameLength then deliver (rest.Slice(0, n)) start
            else
                let mutable s = -1
                let mutable free = -1
                for i in 0 .. slotCount - 1 do
                    if slotFrame.[i] = index then s <- i
                    elif free < 0 && slotFrame.[i] < 0L then free <- i
                if s < 0 && free >= 0 then
                    s <- free
                    slotFrame.[s] <- index
                    slotFill.[s] <- 0
                    if slotBuf.[s].Length < frameLength then slotBuf.[s] <- Array.zeroCreate frameLength
                if s < 0 then counters.CellsDropped <- counters.CellsDropped + 1L
                else
                    rest.Slice(0, n).CopyTo(Span(slotBuf.[s], off, n))
                    slotFill.[s] <- slotFill.[s] + n
                    if slotFill.[s] = frameLength then
                        counters.CellsSpanning <- counters.CellsSpanning + 1L
                        let index' = slotFrame.[s]
                        dropSlot s
                        deliver (Span(slotBuf.[s], 0, frameLength)) (data0 + uint64 index' * l)
            pos <- pos + uint64 n
            rest <- rest.Slice n

    /// Drop fragment slots of frames that touch [lo, hi): they can never be whole.
    let dropSlotsIn (lo: uint64) (hi: uint64) =
        let l = uint64 frameLength
        for i in 0 .. slotCount - 1 do
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

    /// Swallow the gap in front of interval 0.
    let swallow (fast: bool) =
        let lo = rcvNxt
        let hi = ivStart.[0]
        dropSlotsIn lo hi
        rcvNxt <- ivEnd.[0]
        removeInterval 0
        advance ()
        counters.VirtualAcks <- counters.VirtualAcks + 1L
        if fast then counters.FastSwallows <- counters.FastSwallows + 1L
        ackDue <- true

    /// Insert [a, b) into the interval set (b > rcvNxt, a > rcvNxt): new bytes only.
    let addInterval (a: uint64) (b: uint64) (now: int64) (data: Span<byte>) =
        // Walk the set; give takeBytes each piece not yet held.
        let mutable pos = a
        let mutable i = 0
        while i < ivCount && ivEnd.[i] <= pos do i <- i + 1
        while pos < b do
            if i < ivCount && ivStart.[i] <= pos then
                pos <- min b ivEnd.[i]
                i <- i + 1
            else
                let e = if i < ivCount then min b ivStart.[i] else b
                takeBytes pos (data.Slice(int (pos - a), int (e - pos)))
                pos <- e
        // Merge [a, b) into the set.
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
                    takeBytes a d
                    rcvNxt <- rcvNxt + uint64 d.Length
                    advance ()
                else addInterval a (a + uint64 d.Length) now d
                // Too many gaps: retire the oldest at once (spec 6.8.3 early_retire). Outside
                // the lease, forget the newest interval: TCP sends it again.
                while ivCount > maxGaps do
                    if leaseValid () then swallow false else removeInterval (ivCount - 1)

    // ---- demotion ---------------------------------------------------------------------

    /// A path with data in flight and no delivery for max(1 s, 3 RTO) is demoted (spec: slow
    /// subflows are demoted, not dropped): it takes no new frames and its in-flight bytes
    /// leave the group's sum, so one dead path cannot hold the shared cwnd of the others.
    /// Repairs go on; the first delivery restores it.
    let isDemoted (now: int64) =
        let rto = max (1.15 * srttNs) (srttNs + 10_000_000.0)
        let after = max 1_000_000_000L (int64 (3.0 * rto))
        // Only when another path of the group still delivers: if all stall together
        // (an outage), demoting them all would only hold the recovery back.
        let d = inflight > 0 && now - deliveredNs > after && group.AnyDeliveredSince(now - after)
        if d && not demoted then counters.Demotions <- counters.Demotions + 1L
        demoted <- d
        d

    // ---- clock jumps ------------------------------------------------------------------

    /// Cut a stall from the timeline: every send, delivery and timer stamp moves forward by d,
    /// so after the stall the timers run on from where they were. Without this, all timers
    /// expire at once (a repair storm) and the first ACKs give RTT samples as long as the stall.
    let catchUp (now: int64) =
        let gap = now - seenNs
        seenNs <- now
        if gap > stallNs then
            let d = gap - 1_000_000L
            counters.ClockJumps <- counters.ClockJumps + 1L
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
        duplicatesDelivered <- duplicatesDelivered + int64 s.Duplicates
        inflight <- inflight - s.Length
        // RACK time (RFC 8985 6.2): a repaired segment counts only when the ACK cannot be
        // for its first copy (at least min RTT after the repair). Otherwise a late ACK of the
        // first copy would make every segment sent before the repair look lost.
        if s.SentNs > rackSentNs && (s.Retx = 0 || now - s.SentNs >= minRttNs) then rackSentNs <- s.SentNs
        let ackElapsed = now - s.DeliveredNs
        let sendElapsed = s.SentNs - s.FirstSentNs
        let interval = max ackElapsed sendElapsed
        deliveredNs <- now
        lastDeliveredSentNs <- s.SentNs
        if s.Retx > 0 then heldBehindRepair <- true
        if s.Retx = 0 && not heldBehindRepair then
            let rtt = float (now - s.SentNs)
            srttNs <- if srttNs = 0.0 then rtt else 0.875 * srttNs + 0.125 * rtt
            let r = now - s.SentNs
            if TraceGate.On then Trace.record Trace.Rtt (uint64 inflight) pathId (int (r / 1000L))
            if r < minRttNs || now - minRttStamp > 10_000_000_000L then
                minRttNs <- r
                minRttStamp <- now
        if interval > 0L then
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
    /// (borrowed shell). HOLE-2: min(2 + j, 4) copies for the j-th repair.
    let repair (i: int) (now: int64) =
        let s = &ring.[at i]
        let p = output.Scratch
        let copies = min (2 + s.Retx) 4
        if s.Kind = 0uy then
            let off = int (s.Seq - relBase)
            Buffer.BlockCopy(reliable, off, p, Packet.DataOffset, s.Length)
        else
            owner.ProduceFill(pathId, Span(p, Packet.DataOffset, s.Length))
            counters.BorrowRetx <- counters.BorrowRetx + 1L
        let n = Packet.build (Span p) localIp remoteIp localPort remotePort (uint32 s.Seq) (uint32 rcvNxt)
                             (Packet.Ack ||| Packet.Psh) (window ()) ReadOnlySpan.Empty s.Length ipId
        ipId <- ipId + 1us
        for _ in 1 .. copies do output.Write(ReadOnlySpan(p, 0, n))
        counters.SegmentsSent <- counters.SegmentsSent + int64 copies
        counters.SegmentsLost <- counters.SegmentsLost + 1L
        tokens <- tokens - float (copies * n)
        // A demoted path has left the group's sums: its repairs do not spend the others' pacer.
        // The group counts payload bytes, as its rate samples (delivered) do.
        if not demoted then group.OnRepair(copies * s.Length)
        s.Duplicates <- s.Duplicates + (copies - 1) * s.Length
        s.Retx <- s.Retx + 1
        s.SentNs <- now
        s.Delivered <- delivered
        s.DeliveredNs <- deliveredNs
        s.FirstSentNs <- lastDeliveredSentNs
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
                if r > highSacked then highSacked <- r
                let mutable i = find l
                while i < ringCount && ring.[at i].Seq + uint64 ring.[at i].Length <= r do
                    let s = &ring.[at i]
                    if not s.Sacked && s.Seq >= l then
                        s.Sacked <- true
                        onDelivered &s now
                    i <- i + 1
        // RACK, reordering window 0 (HOLE-1): a segment sent before a delivered one is lost.
        if n > 0 then
            let mutable i = 0
            while i < ringCount && ring.[at i].Seq < highSacked do
                let s = &ring.[at i]
                if not s.Sacked && s.SentNs < rackSentNs then repair i now
                i <- i + 1
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
    member _.SrttNs = srttNs
    member _.Inflight = inflight
    member _.Describe() =
        "bw_mbit=" + (btlBw * 8000.0).ToString("F1") + " srtt_ms=" + (srttNs / 1e6).ToString("F1")
        + " min_rtt_ms=" + (if minRttNs = Int64.MaxValue then "-" else (float minRttNs / 1e6).ToString("F1"))
        + " cwnd=" + string (cwnd ()) + " inflight=" + string inflight + " peer_wnd=" + string peerWnd
        + " phase=" + (if startup then "startup" elif draining then "drain" else "probe" + string cycle)
        + (if demoted then " demoted" else "")
    /// The HTTP head and bootstrap frame are whole: BootFrame holds the frame. -2: bad head.
    member _.BootState = if bootAt = -2 then -2 elif bootAt >= 0 && headLen >= bootAt + Layout.BootFrameLength then 1 else 0
    member _.BootFrame = ReadOnlySpan(head, bootAt, Layout.BootFrameLength)
    /// True while timers or sending may need a short wait.
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
        peerScale <- max 0 ws
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
        if ws < 0 then peerScale <- 0

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
        let now = clock.NowNs()
        while at' < data.Length do
            let n = min mss (data.Length - at')
            data.Slice(at', n).CopyTo(Span(output.Scratch, Packet.DataOffset, n))
            if ringCount = ring.Length then
                let next = Array.zeroCreate<SentSegment> (ring.Length * 2)
                for i in 0 .. ringCount - 1 do next.[i] <- ring.[at i]
                ring <- next
                ringHead <- 0
            if ringCount = 0 then unaNs <- now
            ring.[at ringCount] <- { Seq = sndNxt; Length = n; Kind = 0uy; Sacked = false; Retx = 0; SentNs = now; Duplicates = 0
                                     Delivered = delivered; DeliveredNs = deliveredNs; FirstSentNs = lastDeliveredSentNs }
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
        if state = TunState.Established then
            if leaseValid () then
                let mutable go = true
                while go && ivCount > 0 do
                    let fast = ivWm.[0] <> UInt64.MaxValue && owner.Frontier > ivWm.[0]
                    if fast || now - ivSince.[0] >= graceNs then swallow fast
                    else go <- false
            // Fragments that can no longer complete.
            if dataPhase then
                let l = uint64 frameLength
                for i in 0 .. slotCount - 1 do
                    if slotFrame.[i] >= 0L && data0 + uint64 slotFrame.[i] * l + l <= rcvNxt then
                        counters.CellsDropped <- counters.CellsDropped + 1L
                        dropSlot i
            // Tail repair.
            // A tail is only a tail when nothing was delivered for a whole timer: while ACKs
            // come in, a growing queue makes old segments look late (spurious repairs).
            let rto = int64 (max (1.15 * srttNs) (srttNs + 10_000_000.0))
            // One batch per timer, not one per call: later calls in the same timer would
            // otherwise repair the next segments, and so on through the whole ring.
            if ringCount > 0 && srttNs > 0.0 && now - deliveredNs > rto && now - tailNs > rto then
                let mutable i = 0
                let mutable budget = tailBudget
                while i < ringCount && budget > 0 do
                    let s = &ring.[at i]
                    // Any segment older than the timer: RACK repairs only below the highest
                    // SACK, so a segment above it sent before rackSentNs (before a delivered
                    // repair) has no other way back once an outage silences every ACK.
                    if not s.Sacked && now - s.SentNs > rto then
                        repair i now
                        counters.TailRepairs <- counters.TailRepairs + 1L
                        budget <- budget - 1
                    i <- i + 1
                if budget < tailBudget then tailNs <- now
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
            if rtoActive then
                if rtoSeq < sndUna then rtoSeq <- sndUna
                let mutable i = if ringCount > 0 then find rtoSeq else 0
                let mutable budget = tailBudget
                while i < ringCount && budget > 0 && ring.[at i].Seq < rtoEnd do
                    let s = &ring.[at i]
                    if not s.Sacked && s.SentNs < rtoFireNs then
                        repair i now
                        budget <- budget - 1
                    rtoSeq <- s.Seq + uint64 s.Length
                    i <- i + 1
                if i >= ringCount || rtoSeq >= rtoEnd then rtoActive <- false
            if not startup && not draining && minRttNs <> Int64.MaxValue && now - cycleNs > minRttNs then
                cycle <- (cycle + 1) % 8
                cycleNs <- now

    /// Send the pending ACK, unless a gap is in its grace (spec 6.8.2: no duplicate ACK and
    /// no SACK while the lease covers it).
    member _.FlushAck() =
        if ackDue && state <> TunState.Closed then
            if not (ivCount > 0 && leaseValid ()) then
                if ivCount > 0 && sackOk then
                    let n = min 4 ivCount
                    let o = Span<byte>(sackOption, 0, 4 + 8 * n)
                    o.[0] <- 1uy
                    o.[1] <- 1uy
                    o.[2] <- 5uy
                    o.[3] <- byte (2 + 8 * n)
                    for i in 0 .. n - 1 do
                        BinaryPrimitives.WriteUInt32BigEndian(o.Slice(4 + 8 * i), uint32 ivStart.[i])
                        BinaryPrimitives.WriteUInt32BigEndian(o.Slice(8 + 8 * i), uint32 ivEnd.[i])
                    send Packet.Ack sndNxt (Span<byte>.op_Implicit o) 0
                else send Packet.Ack sndNxt ReadOnlySpan.Empty 0
                ackDue <- false

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
        try this.ServiceCore now
        with ex -> this.Faulted ex

    /// Abort: RST to the peer.
    member _.Abort() =
        if state <> TunState.Closed then
            send (Packet.Rst ||| Packet.Ack) sndNxt ReadOnlySpan.Empty 0
            state <- TunState.Closed

    interface IRateMember with
        member _.Inflight = if isDemoted (clock.NowNs()) then 0 else inflight
        // Delivered segments plus their duplicate repair copies: what crossed the bottleneck.
        member _.Delivered = delivered + duplicatesDelivered
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
            if extra > 0 then takeBytes data0 (Span(head, bootAt + Layout.BootFrameLength, extra))
            head <- Array.Empty<byte>()
            headLen <- 0

        member _.Backlog with set v = backlog <- v
        member _.Watch(_, _) = ()
        member _.Receive(_) = ()

        member _.TryBeginFrame(frame) =
            if state <> TunState.Established || not attached || frameLength > peerMss then false
            else
                let l = frameLength
                let bulk = backlog >= 16
                if int (sndNxt + uint64 l - sndUna) > peerWnd then false
                elif isDemoted (clock.NowNs()) then false
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
                    refillTokens (clock.NowNs())
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
            if ringCount = 0 then unaNs <- now
            ring.[at ringCount] <- { Seq = sndNxt; Length = l; Kind = 1uy; Sacked = false; Retx = 0; SentNs = now; Duplicates = 0
                                     Delivered = delivered; DeliveredNs = deliveredNs; FirstSentNs = lastDeliveredSentNs }
            ringCount <- ringCount + 1
            inflight <- inflight + l
            let p = output.Scratch
            let n = Packet.build (Span p) localIp remoteIp localPort remotePort (uint32 sndNxt) (uint32 rcvNxt)
                                 (Packet.Ack ||| Packet.Psh) (window ()) ReadOnlySpan.Empty l ipId
            ipId <- ipId + 1us
            output.Write(ReadOnlySpan(p, 0, n))
            counters.SegmentsSent <- counters.SegmentsSent + 1L
            tokens <- tokens - float n
            group.OnSent l
            sndNxt <- sndNxt + uint64 l
            ackDue <- false

        member this.Close() =
            closing <- true
            this.Abort()
