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
    abstract Delivered: int64
    abstract MinRttNs: int64

[<Sealed>]
type RateGroup(clock: IClock) =
    let members = List<IRateMember>()
    let filter = Array.zeroCreate<float> 10
    let mutable slot = 0
    let mutable bw = 0.0
    let mutable sampleNs = 0L
    let mutable sampleDelivered = 0L
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
        for m in members do r <- min r m.MinRttNs
        r

    let inflight () =
        let mutable n = 0
        for m in members do n <- n + m.Inflight
        n

    let delivered () =
        let mutable n = 0L
        for m in members do n <- n + m.Delivered
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
        sampleSent <- sentBytes
        lastSendRate <- Double.MaxValue

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

    member _.Cwnd(frameLength: int) =
        let g = if startup then 2.885 else cwndGain
        int (g * bw * float (minRtt ())) + 4 * frameLength

    /// May one more frame of l bytes go now? bulk: the session has a real backlog (a refusal
    /// then marks the round as not app-limited).
    member this.TryTake(l: int, bulk: bool) =
        let now = clock.NowNs()
        let rate = gain () * bw
        let depth = max (4.0 * float l) (rate * 2_000_000.0)
        tokens <- min depth (tokens + rate * float (now - tokensNs))
        tokensNs <- now
        if inflight () + l > this.Cwnd l || tokens < 0.0 then
            if bulk then limited <- true
            false
        else true

    member _.OnSent(bytes: int) =
        tokens <- tokens - float bytes
        sentBytes <- sentBytes + int64 bytes

    /// Repair copies (HOLE-2) take the same pacer as new frames: they load the same bottleneck.
    member _.OnRepair(bytes: int) =
        tokens <- tokens - float bytes
        repairBytes <- repairBytes + int64 bytes
        sentBytes <- sentBytes + int64 bytes

    member _.RepairBytes = repairBytes
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
            elif now - sampleNs >= max rtt 5_000_000L then
                let d = delivered ()
                let ackRate = float (max 0L (d - sampleDelivered)) / float (now - sampleNs)
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

    member _.Describe() =
        "group paths=" + string members.Count + " bw_mbit=" + (bw * 8000.0).ToString("F1")
        + " inflight=" + string (inflight ()) + " repair_kib=" + string (repairBytes / 1024L) + " phase="
        + (if startup then "startup" elif draining then "drain" else "probe" + string cycle)
