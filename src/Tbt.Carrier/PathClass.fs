/// Per-path evidence for "this tuple looks shaped" versus "these paths look
/// like one shared bottleneck". A missing or mixed signal stays Unknown.
/// Correlation alone never asserts a cause.
namespace Tbt.Carrier

open System

/// What the session may act on. Unknown is the default until several samples agree.
type PathKind =
    | Unknown = 0
    | Policed = 1
    | Physical = 2
    | Mixed = 3

[<Struct>]
type PathVerdict =
    { Kind: PathKind
      /// Bytes/s the path has actually delivered, or 0 when unknown.
      CapBps: int64
      /// 0..1000. Below ~600 the caller must not shrink a session budget.
      Confidence: int
      /// Coefficient of variation of recent positive delivery samples, or -1.
      Cv: float }

module PathClass =
    let minSamples = 4
    /// A plateau is "flat" when the spread is under a quarter of the mean.
    let plateauCv = 0.25
    let lowRetrans = 0.05
    /// RTT inflation that is not also a loss is not, by itself, physical congestion.
    let rttInflate = 3

    let private meanAndCv (rates: int64[]) (n: int) =
        let mutable sum = 0L
        let mutable count = 0
        for i in 0 .. n - 1 do
            if rates.[i] > 0L then
                sum <- sum + rates.[i]
                count <- count + 1
        if count < minSamples then struct (0L, -1.0, count)
        else
            let mean = sum / int64 count
            if mean <= 0L then struct (0L, -1.0, count)
            else
                let mutable acc = 0.0
                for i in 0 .. n - 1 do
                    if rates.[i] > 0L then
                        let d = float (rates.[i] - mean) / float mean
                        acc <- acc + d * d
                struct (mean, sqrt (acc / float count), count)

    /// One path, from its own samples only. A stable positive rate with almost no
    /// retransmission is a policer candidate. Loss, RTO, or a high retransmission
    /// rate is a physical-loss candidate. Either alone is not a session verdict.
    let ofSamples (rates: int64[]) (n: int) (retransRate: float) (caState: byte) (valid: bool) =
        if not valid || n < minSamples then
            { Kind = PathKind.Unknown; CapBps = 0L; Confidence = 0; Cv = -1.0 }
        else
            let struct (mean, cv, count) = meanAndCv rates n
            let flat = cv >= 0.0 && cv <= plateauCv && mean > 0L
            let quiet = retransRate < lowRetrans && caState < 4uy
            let lossy = caState >= 4uy || retransRate >= 0.15
            if flat && quiet && not lossy then
                // A quiet plateau may be application-limited. It is a candidate,
                // not enough evidence for the caller's high-confidence gate.
                { Kind = PathKind.Policed
                  CapBps = mean
                  Confidence = min 500 (200 + count * 40)
                  Cv = cv }
            elif lossy && not flat then
                { Kind = PathKind.Physical
                  CapBps = mean
                  Confidence = min 1000 (300 + count * 60)
                  Cv = cv }
            elif flat && lossy then
                { Kind = PathKind.Mixed
                  CapBps = mean
                  Confidence = min 1000 (200 + count * 40)
                  Cv = cv }
            else
                { Kind = PathKind.Unknown; CapBps = mean; Confidence = 100; Cv = cv }

    /// Session hint. `inflated` is how many valid paths have RTT >= 1.5 minRTT and
    /// a positive retransmission rate. `addedPathsRaisedGoodput` is the caller's
    /// observation from a bounded probe; pass None when no probe has run.
    /// A uniform plateau with low loss is not physical congestion: every tuple can
    /// sit on the same policer ceiling while the physical RTT stays put.
    let sessionHint (kinds: PathKind[]) (inflated: int) (validPaths: int)
                     (addedPathsRaisedGoodput: bool option) =
        let mutable policed = 0
        let mutable physical = 0
        let mutable known = 0
        for k in kinds do
            match k with
            | PathKind.Policed -> policed <- policed + 1; known <- known + 1
            | PathKind.Physical -> physical <- physical + 1; known <- known + 1
            | PathKind.Mixed -> physical <- physical + 1; policed <- policed + 1; known <- known + 1
            | _ -> ()
        let correlated = validPaths >= 3 && inflated * 2 >= validPaths
        let grew =
            match addedPathsRaisedGoodput with
            | Some true -> true
            | _ -> false
        let stalled =
            match addedPathsRaisedGoodput with
            | Some false -> true
            | _ -> false
        if known < 2 then PathKind.Unknown, 0
        elif correlated && stalled && physical > 0 then PathKind.Physical, min 1000 (500 + physical * 50)
        elif policed >= 2 && not correlated && grew && physical = 0 then
            PathKind.Policed, min 1000 (400 + policed * 40)
        elif physical > 0 && policed > 0 then PathKind.Mixed, 300
        else PathKind.Unknown, 100

/// Sliding delivery-rate memory for one path id. The session thread owns it.
[<Sealed>]
type PathClassTracker(capacity: int) =
    let width = max PathClass.minSamples (min 16 capacity)
    let rates = Array.zeroCreate<int64> width
    let mutable n = 0
    let mutable head = 0
    let scratch = Array.zeroCreate<int64> width

    member _.Observe(rateBps: int64) =
        rates.[head] <- max 0L rateBps
        head <- (head + 1) % width
        if n < width then n <- n + 1

    member _.Verdict(h: PathHealth) =
        let count = n
        let start = if n < width then 0 else head
        for i in 0 .. count - 1 do
            scratch.[i] <- rates.[(start + i) % width]
        PathClass.ofSamples scratch count h.RetransRate h.CaState h.Valid

    member _.Samples = n

/// Deficit round-robin. Quantum is bytes of credit added each round, proportional
/// to a path's delivered rate. A slow policed path therefore does not receive
/// 1/N of the visits. Unknown or equal rates stay equal, so an unmeasured set
/// still walks in order.
[<Sealed>]
type DeficitWheel(capacity: int) =
    let mutable deficit = Array.zeroCreate capacity
    let mutable weight = Array.zeroCreate capacity
    let mutable len = 0

    let ensure (n: int) =
        if deficit.Length < n then
            let ncap = max n (deficit.Length * 2)
            let d = Array.zeroCreate ncap
            let w = Array.zeroCreate ncap
            Array.Copy(deficit, d, len)
            Array.Copy(weight, w, len)
            deficit <- d
            weight <- w
        len <- n

    /// `weights[i]` is a positive visit share. Zero is treated as 1 so a path
    /// with no sample is not starved while it is being measured.
    member _.Refill(weights: int[]) =
        ensure weights.Length
        for i in 0 .. weights.Length - 1 do
            let q = max 1 weights.[i]
            weight.[i] <- q
            deficit.[i] <- deficit.[i] + q

    /// Index in `order` (a permutation of path slots) with the largest deficit,
    /// or -1 when every eligible slot is spent. `eligible` is scanned in order
    /// so equal deficits keep the caller's ranking (lower ETA first).
    member _.Pick(order: int[], scan: int, eligible: int -> bool) =
        let mutable best = -1
        let mutable bestDef = 0
        let n = min scan order.Length
        for j in 0 .. n - 1 do
            let idx = order.[j]
            if idx >= 0 && idx < len && eligible idx && deficit.[idx] > bestDef then
                bestDef <- deficit.[idx]
                best <- j
        best

    member _.Charge(idx: int, bytes: int) =
        if idx >= 0 && idx < len then
            deficit.[idx] <- deficit.[idx] - max 1 bytes

    member _.Deficit(idx: int) =
        if idx >= 0 && idx < len then deficit.[idx] else 0

    /// Visit share from a delivery rate. A path at `floorBps` gets share 1;
    /// a path four times faster gets share 4. Unknown rate gets share 1.
    static member Share(rateBps: int64) (floorBps: int64) =
        if rateBps <= 0L || floorBps <= 0L then 1
        else int (min 64L (max 1L ((rateBps + floorBps - 1L) / floorBps)))
