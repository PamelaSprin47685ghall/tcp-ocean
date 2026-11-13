/// Rate control shared by the paths to one client address. The paths of a session cross the
/// same bottleneck; a delivery-rate controller per path sees only its own share, and the sum
/// of the per-path cwnds (each 2 x its BDP plus probing) keeps a standing queue there. The
/// group measures the delivery rate of all its paths together and gates cwnd and pacing
/// for the sum (spec 16.3 Q-2: in-flight bound for the bottleneck, not per path).
namespace Tbt.Carrier

open System
open System.Collections.Generic
open Tbt.Contracts

/// What the group reads from one member path.
type IRateMember =
    abstract Inflight: int
    /// Unique payload bytes the peer acknowledged (cumulative ACK or SACK evidence); a
    /// segment counts once however many copies were sent.
    abstract Delivered: int64
    /// Estimate, not evidence: repair copies beyond the first of segments that were acked,
    /// taken to have crossed the bottleneck too (no ACK tells which copy arrived).
    abstract CopyEstimate: int64
    abstract MinRttNs: int64
    /// Last time the member delivered anything (cumulative ACK or SACK).
    abstract LastDeliveredNs: int64

[<Sealed>]
type RateGroup(clock: IClock) =
    let members = List<IRateMember>()
    let filter = Array.zeroCreate<float> 10
    let mutable slot = 0
    let mutable bw = 0.0
    let mutable sampleNs = 0L
    let mutable sampleDelivered = 0L
    let mutable sampleCopies = 0L
    /// Unique delivery rate of the last sample (goodput, bytes per ns), for the log.
    let mutable goodput = 0.0
    let mutable limited = false
    let mutable startup = true
    let mutable draining = false
    let mutable fullBw = 0.0
    let mutable fullCount = 0
    let mutable appLimited = 0
    let mutable cycle = 0
    let mutable tokens = 0.0
    let mutable tokensNs = clock.NowNs()
    let mutable repairBytes = 0L
    let mutable repairTokens = 0.0
    /// Last sample round in which no path delivered anything (a shared outage).
    let mutable outageNs = Int64.MinValue / 2L
    /// Bytes the group's paths sent (new frames and repair copies), and the send rate of
    /// the last sample interval: a delivery sample may not exceed it (BBR send_elapsed).
    /// Without the bound, an ACK burst after a repaired hole gave samples up to 375 Mbit/s
    /// on a 200 Mbit/s link, and the 10-round max filter kept them.
    let mutable sentBytes = 0L
    let mutable sampleSent = 0L
    let mutable lastSendRate = Double.MaxValue
    /// Steady-state in-flight cap over the group BDP: 1.25 lets the 1.25 probe phase fill the
    /// pipe and keeps the standing queue at the bottleneck to about 0.25 BDP (was 2.0: up to
    /// one BDP of queue, docs/cases/under-bulk-tail.md).
    static let cwndGain = 1.25
    /// Pacing gain of the 6 cruise phases. 1.0 with no standing queue left the link idle in
    /// small gaps (bulk down -1.6 %); slightly above 1 keeps it busy, the 1.25 BDP cap bounds
    /// the queue this can build.
    static let cruiseGain = 1.05

    let minRtt () =
        let mutable r = Int64.MaxValue
        // A negative member value (only a clock-jump artefact) must not drive the group cwnd below 0.
        for m in members do if m.MinRttNs >= 0L then r <- min r m.MinRttNs
        r

    let inflight () =
        let mutable n = 0
        for m in members do n <- n + m.Inflight
        n

    let delivered () =
        let mutable n = 0L
        for m in members do n <- n + m.Delivered
        n

    let copyEstimate () =
        let mutable n = 0L
        for m in members do n <- n + m.CopyEstimate
        n

    let gain () =
        if startup then 2.885
        elif draining then 1.0 / 2.885
        else
            match cycle with
            | 0 -> 1.25
            | 1 -> 0.75
            | _ -> cruiseGain

    let restartSample (now: int64) =
        sampleNs <- now
        sampleDelivered <- delivered ()
        sampleCopies <- copyEstimate ()
        sampleSent <- sentBytes
        lastSendRate <- Double.MaxValue

    /// True when some member delivered at or after t: a stall on every path at once is an
    /// outage, not one dead path, and must not demote them all.
    member _.AnyDeliveredSince(t: int64) =
        let mutable any = false
        for i in 0 .. members.Count - 1 do
            if members.[i].LastDeliveredNs >= t then any <- true
        any

    /// Last round in which no member delivered anything.
    member _.LastOutageNs = outageNs

    member _.Add(m: IRateMember) =
        members.Add m
        restartSample (clock.NowNs())

    member _.Remove(m: IRateMember) =
        members.Remove m |> ignore
        restartSample (clock.NowNs())

    member _.Count = members.Count
    /// Bytes per ns; 0 before the first sample.
    member _.Bandwidth = bw
    member _.Ready = bw > 0.0 && minRtt () <> Int64.MaxValue

    /// Refill both buckets; returns the recovery reserve.
    member private _.Refill(l: int) =
        let now = clock.NowNs()
        let rate = gain () * bw
        let depth = max (4.0 * float l) (rate * 2_000_000.0)
        let reserve = max (16.0 * float l) (rate * 2_000_000.0)
        let dt = float (now - tokensNs)
        tokens <- min depth (tokens + rate * dt)
        repairTokens <- min reserve (repairTokens + 0.5 * rate * dt)
        tokensNs <- now
        reserve

    member _.Cwnd(frameLength: int) =
        let g = if startup then 2.885 else cwndGain
        int (g * bw * float (minRtt ())) + 4 * frameLength

    /// May one more frame of l bytes go now? bulk: the session has a real backlog (a refusal
    /// then marks the round as not app-limited).
    member this.TryTake(l: int, bulk: bool) =
        this.Refill l |> ignore
        if inflight () + l > this.Cwnd l || tokens < 0.0 then
            if bulk then limited <- true
            false
        else true

    /// May a repair of bytes go now? Repairs are paced by the same tokens as new frames, which
    /// they may run down to -reserve (bounded recovery reserve: 16 frames or 2 ms at the rate),
    /// and by a repair bucket that refills at half the rate: recovery cannot starve the new
    /// frames of the other paths, and new frames cannot starve recovery. OnRepair charges both.
    member this.TryTakeRepair(bytes: int, l: int) =
        let reserve = this.Refill l
        repairTokens >= float bytes && tokens - float bytes >= -reserve

    member _.OnSent(bytes: int) =
        tokens <- tokens - float bytes
        sentBytes <- sentBytes + int64 bytes

    /// Repair copies (HOLE-2) take the same pacer as new frames: they load the same bottleneck.
    member _.OnRepair(bytes: int) =
        tokens <- tokens - float bytes
        repairTokens <- repairTokens - float bytes
        repairBytes <- repairBytes + int64 bytes
        sentBytes <- sentBytes + int64 bytes

    member _.RepairBytes = repairBytes
    /// Payload bytes the group's paths put on the wire: new frames and every repair copy.
    member _.WireSentBytes = sentBytes
    /// Unique delivery rate of the last sample (bytes per ns).
    member _.Goodput = goodput
    member _.InStartup = startup
    member _.Cycle = cycle
    static member CwndGain = cwndGain

    /// One sample per round (min RTT, at least 5 ms): max filter over 10 rounds, startup,
    /// drain, 8-phase gain cycle. App-limited rounds keep the estimate.
    member _.Service(now: int64) =
        let rtt = minRtt ()
        // The 0.75 phase only drains the queue the 1.25 phase built: it ends as soon as the
        // group's in-flight bytes are down to one BDP (BBR), not after a whole round, else
        // with no queue it idles the link for 1 round in 8 (bulk down -3.6 %).
        if cycle = 1 && not startup && not draining && rtt <> Int64.MaxValue
           && inflight () <= int (bw * float rtt) then
            cycle <- 2
        if rtt <> Int64.MaxValue && members.Count > 0 then
            if sampleNs = 0L then restartSample now
            elif now - sampleNs >= max rtt 5_000_000L && delivered () = sampleDelivered then
                // Nothing delivered on any path for a whole round: an outage, not a rate. A zero
                // sample would empty the max filter in 10 rounds, and the paced repairs that
                // end the outage would then wait on a pacer with no rate.
                sampleNs <- now
                sampleSent <- sentBytes
                sampleCopies <- copyEstimate ()
                limited <- false
                // Shared only when the paths that wait are more than one (or the only one): a
                // single dead path while the others have nothing in flight is not an outage.
                let mutable waiting = 0
                for m in members do if m.Inflight > 0 then waiting <- waiting + 1
                if waiting >= min 2 members.Count then outageNs <- now
            elif now - sampleNs >= max rtt 5_000_000L then
                let d = delivered ()
                let cp = copyEstimate ()
                // The bottleneck carried the unique bytes plus, by estimate, the extra copies:
                // the wire rate the pacer spends (it charges every copy) is their sum.
                goodput <- float (max 0L (d - sampleDelivered)) / float (now - sampleNs)
                let ackRate = float (max 0L (d - sampleDelivered) + max 0L (cp - sampleCopies)) / float (now - sampleNs)
                let sendRate = float (sentBytes - sampleSent) / float (now - sampleNs)
                // What was delivered in this interval was sent in about the last one.
                let rate = min ackRate (max sendRate lastSendRate)
                lastSendRate <- sendRate
                sampleSent <- sentBytes
                slot <- (slot + 1) % filter.Length
                filter.[slot] <- if limited || rate > bw then rate else bw
                bw <- Array.max filter
                if limited then appLimited <- 0 else appLimited <- appLimited + 1
                if startup && limited then
                    if bw >= fullBw * 1.25 then
                        fullBw <- bw
                        fullCount <- 0
                    else
                        fullCount <- fullCount + 1
                        if fullCount >= 3 then
                            startup <- false
                            draining <- true
                elif draining then
                    if inflight () <= int (bw * float rtt) then
                        draining <- false
                        cycle <- 2
                elif not startup then
                    cycle <- (cycle + 1) % 8
                    if appLimited >= 3 then
                        startup <- true
                        fullBw <- 0.0
                        fullCount <- 0
                limited <- false
                sampleNs <- now
                sampleDelivered <- d
                sampleCopies <- cp

    member _.Describe() =
        "group paths=" + string members.Count + " bw_mbit=" + (bw * 8000.0).ToString("F1")
        + " goodput_mbit=" + (goodput * 8000.0).ToString("F1") + " copy_est_kib=" + string (copyEstimate () / 1024L)
        + " inflight=" + string (inflight ()) + " repair_kib=" + string (repairBytes / 1024L) + " phase="
        + (if startup then "startup" elif draining then "drain" else "probe" + string cycle)

/// Optional per-5-tuple pacer for the client uplink (e.g. PROFILE=rtt400 at 4 Mbit).
/// Off when rateMbit <= 0 so the 20 ms / 200 Mbit matrix is not capped per path.
/// Paces at 1.25× the configured rate; burst depth is about 1.25 BDP, or 4 frames
/// before a min-RTT sample exists.
[<Sealed>]
type PathPacer(rateMbit: float) =
    let bytesPerNs = rateMbit / 8000.0
    let pace = 1.25 * bytesPerNs
    let mutable tokens = 0.0
    let mutable tokensNs = 0L
    let mutable minRttNs = Int64.MaxValue

    member _.RateMbit = rateMbit

    member _.SetMinRtt(ns: int64) =
        if ns > 0L && ns < minRttNs then minRttNs <- ns

    /// True when one more frame of `bytes` may go now. Does not charge; call OnSent after a send.
    /// The bucket starts at 4 frames (not a full BDP) so a late min-RTT does not grant every
    /// path 1.25 BDP at once; refill dt is capped so a long pause is not a burst.
    member _.TryTake(bytes: int, now: int64) =
        if rateMbit <= 0.0 then true
        else
            let depth =
                if minRttNs = Int64.MaxValue then 4.0 * float bytes
                else max (4.0 * float bytes) (1.25 * bytesPerNs * float minRttNs)
            if tokensNs = 0L then
                tokens <- 4.0 * float bytes
                tokensNs <- now
            else
                let dt = min 20_000_000L (max 0L (now - tokensNs))
                tokens <- min depth (tokens + pace * float dt)
                tokensNs <- now
            tokens >= float bytes

    member _.OnSent(bytes: int) = tokens <- tokens - float bytes

