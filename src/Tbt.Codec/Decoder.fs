/// Receiver side of TBT-SC2: maximum-likelihood erasure decoding (spec 5.9).
/// Every source that the received sources and equations determine uniquely is recovered;
/// no other source is output. The method is incremental Gauss-Jordan elimination over
/// GF(16) (GF(2) rows are GF(16) rows with coefficient 1). Pivot rows stay fully reduced,
/// so a row with one column left is a solved source.
namespace Tbt.Codec

open System
open System.Collections.Generic
open Tbt.Contracts
open Tbt.Codec.CodeGraph

/// Result of offering one equation (parity or BOOSTER) to the decoder.
type EquationResult =
    /// The equation added rank.
    | Innovative = 0
    /// The equation gave no new information.
    | Redundant = 1
    /// Every source the equation covers is already known.
    | Useless = 2
    /// The equation covers a step outside the decode window; it was dropped.
    | OutOfWindow = 3

/// One pending equation: sum of Coefs[i] * x[Cols[i]] = Payload. Cols sorted ascending.
[<Sealed; AllowNullLiteral>]
type private Row(symbolSize: int) =
    member val Cols: uint64[] = Array.zeroCreate 64 with get, set
    member val Coefs: byte[] = Array.zeroCreate 64 with get, set
    member val Count = 0 with get, set
    /// Pivot column of this row. Other columns may be smaller or larger.
    member val Pivot = 0UL with get, set
    member val Payload: byte[] = Array.zeroCreate symbolSize
    /// Bytes of the arrays this row owns. The CLR object header and dictionary/list
    /// capacity are not row-owned arrays and are deliberately excluded.
    member val OwnedBytes: int64 = int64 symbolSize + 64L * 8L + 64L with get, set
    /// Position in the decoder row list, -1 when not in it.
    member val Index = -1 with get, set

    member r.Find(col: uint64) = Array.BinarySearch(r.Cols, 0, r.Count, col)

    member r.EnsureCapacity(n: int) =
        if r.Cols.Length < n then
            let size = max n (2 * r.Cols.Length)
            let c = Array.zeroCreate size
            let k = Array.zeroCreate size
            Array.Copy(r.Cols, c, r.Count)
            Array.Copy(r.Coefs, k, r.Count)
            r.Cols <- c
            r.Coefs <- k
            r.OwnedBytes <- int64 symbolSize + int64 size * 8L + int64 size

    member r.RemoveAt(i: int) =
        Array.Copy(r.Cols, i + 1, r.Cols, i, r.Count - i - 1)
        Array.Copy(r.Coefs, i + 1, r.Coefs, i, r.Count - i - 1)
        r.Count <- r.Count - 1

[<Sealed>]
type Decoder(graphSeed: uint64, symbolSize: int, capacitySteps: int, maxRows: int) =
    let ring = StepRing(capacitySteps, symbolSize)
    let terms: Term[] = Array.zeroCreate MaxTerms
    let rows = List<Row>(maxRows)
    let freeRows = Stack<Row>(maxRows)
    let pivots = Dictionary<uint64, Row>(maxRows)
    let colRows = Dictionary<uint64, List<Row>>(maxRows * 4)
    let rowScratch = List<Row>(64)
    let solvedQueue = Stack<Row>()
    let knownQueue = Queue<struct (uint64 * byte * bool)>()
    let mutable kConflicts = 0L
    let mutable mergeCols: uint64[] = Array.zeroCreate 128
    let mutable mergeCoefs: byte[] = Array.zeroCreate 128
    let pivotScratch: uint64[] = Array.zeroCreate MaxTerms
    /// All steps below frontier are complete (t_lo).
    let mutable frontier = 0UL
    /// Lowest step still stored.
    let mutable baseStep = 0UL
    /// Highest step seen plus one.
    let mutable seenEnd = 0UL
    let mutable rowsAllocated = 0
    /// O(1) sum of row-owned payload/column arrays. Updated at the exact allocation and
    /// release sites; never derives from credit promises or index capacity.
    let mutable rowMemoryBytes = 0L
    /// Rows refused because they kept a column of a released step. Always zero in a healthy
    /// session; anything else means Release is finding unfinished equations, which is the
    /// signal that the decode window fell behind (see Release).
    let mutable staleRows = 0L
    /// Reused key list for clearing colRows / pivots entries of released steps.
    let staleCols = List<uint64>()
    /// Idle rows kept for the next burst; Release gives back anything above this.
    let warmRows = max 256 (min 1024 (capacitySteps / 2))

    let colOf (t: uint64) (s: int) = (t <<< 4) ||| uint64 s
    let stepOfCol (c: uint64) = c >>> 4
    let slotOfCol (c: uint64) = int (c &&& 15UL)

    let inWindow (t: uint64) = t >= baseStep && t < baseStep + uint64 capacitySteps

    /// Slots already handed to the owner, one bit per step slot.
    ///
    /// A source is reported once, and the flag it carries at that moment is the truth: the
    /// direct path reports `false` because the cell arrived, elimination reports `true`
    /// because it had to be rebuilt. Without this, `NoteStepUsed` re-reported every already
    /// known slot as `true` whenever k for that step arrived late — and it does arrive late
    /// for history steps, because RxHalf records their k from a later frame's k_hist
    /// (RxHalf.fs:243). Those sources had been received directly, so the re-report turned
    /// ordinary arrivals into FEC reconstructions: `counters.Recovered` inflated, and
    /// `observeTimely` counted a certain erasure for each one, which raised the reported
    /// erasure rate on a lossless link, drove rho up, and added parity overhead that no loss
    /// justified. It also broke the documented contract of `TryTakeKnown` ("each exactly
    /// once"). Reported bits are cleared whenever the step slot is (re)opened.
    let reported: uint16[] = Array.zeroCreate capacitySteps

    /// Slots whose first report said "rebuilt by elimination" rather than "arrived".
    /// A rebuilt source is not yet a proven erasure: the spec counts a cell as erased only
    /// when it was not delivered and authenticated *before the step's deadline*, so a
    /// source that parity solved early but whose own DATA cell still lands inside the
    /// deadline was never erased. The countdown lives in RxHalf; this bitmap is what lets
    /// it recognise that the arriving DATA cell is the one a rebuild already covered.
    let recoveredBits: uint16[] = Array.zeroCreate capacitySteps

    /// True while the slot's first report was a rebuild and no direct arrival has been
    /// reported for it. Cleared when the step slot is reused.
    let wasRecovered (t: uint64) (s: int) =
        let i = int (t % uint64 capacitySteps)
        s >= 0 && s < int CodeGraph.SlotsPerStep
        && recoveredBits.[i] &&& (1us <<< s) <> 0us

    /// Enqueue this slot at most once. A known source keeps the flag it was first
    /// reported with: the direct path says false, elimination says true.
    let report (t: uint64) (s: int) (recovered: bool) =
        let i = int (t % uint64 capacitySteps)
        let bit = 1us <<< s
        if reported.[i] &&& bit = 0us then
            reported.[i] <- reported.[i] ||| bit
            if recovered then recoveredBits.[i] <- recoveredBits.[i] ||| bit
            knownQueue.Enqueue(struct (t, byte s, recovered))

    let ensureStep (t: uint64) =
        if ring.Contains t then true
        elif inWindow t && ring.TryOpen t then
            // A fresh slot: its report bits belong to the step that just left.
            reported.[int (t % uint64 capacitySteps)] <- 0us
            recoveredBits.[int (t % uint64 capacitySteps)] <- 0us
            if t >= seenEnd then seenEnd <- t + 1UL
            true
        else false

    let rentRow () =
        if freeRows.Count > 0 then ValueSome(freeRows.Pop())
        elif rowsAllocated < maxRows then
            rowsAllocated <- rowsAllocated + 1
            let r = Row(symbolSize)
            rowMemoryBytes <- rowMemoryBytes + r.OwnedBytes
            ValueSome(r)
        else ValueNone

    let detach (r: Row) =
        let i = r.Index
        let last = rows.Count - 1
        if i <> last then
            rows.[i] <- rows.[last]
            rows.[i].Index <- i
        rows.RemoveAt last
        r.Index <- -1

    let recycle (r: Row) =
        r.Count <- 0
        freeRows.Push r

    /// Give up a live row that elimination reduced to no columns: drop its pivot entry
    /// when it owned one, then detach and recycle it. A zero row carries no information,
    /// but leaving it in `rows` burned a row for ever; once `rowsAllocated` reached
    /// `maxRows`, every later parity came back OutOfWindow and FEC died after minutes.
    let discard (q: Row) =
        match pivots.TryGetValue q.Pivot with
        | true, p when Object.ReferenceEquals(p, q) -> pivots.Remove q.Pivot |> ignore
        | _ -> ()
        detach q
        recycle q

    let addColIndex (col: uint64) (r: Row) =
        match colRows.TryGetValue col with
        | true, list -> list.Add r
        | _ ->
            let list = List<Row>(4)
            list.Add r
            colRows.[col] <- list

    let removeColIndex (col: uint64) (r: Row) =
        match colRows.TryGetValue col with
        | true, list ->
            list.Remove r |> ignore
            if list.Count = 0 then colRows.Remove col |> ignore
        | _ -> ()

    let addRowIndex (r: Row) =
        for i in 0 .. r.Count - 1 do
            addColIndex r.Cols.[i] r

    let removeRowIndex (r: Row) =
        for i in 0 .. r.Count - 1 do
            removeColIndex r.Cols.[i] r

    /// q <- q + c * r. Both rows sorted; zero sums are dropped.
    let addScaled (q: Row) (c: byte) (r: Row) =
        let indexed = q.Index >= 0
        let need = q.Count + r.Count
        if mergeCols.Length < need then
            mergeCols <- Array.zeroCreate (2 * need)
            mergeCoefs <- Array.zeroCreate (2 * need)
        let mutable i = 0
        let mutable j = 0
        let mutable n = 0
        while i < q.Count || j < r.Count do
            if j >= r.Count || (i < q.Count && q.Cols.[i] < r.Cols.[j]) then
                mergeCols.[n] <- q.Cols.[i]
                mergeCoefs.[n] <- q.Coefs.[i]
                n <- n + 1
                i <- i + 1
            elif i >= q.Count || r.Cols.[j] < q.Cols.[i] then
                let col = r.Cols.[j]
                if indexed then addColIndex col q
                mergeCols.[n] <- col
                mergeCoefs.[n] <- Gf16.mul c r.Coefs.[j]
                n <- n + 1
                j <- j + 1
            else
                let col = q.Cols.[i]
                let v = q.Coefs.[i] ^^^ Gf16.mul c r.Coefs.[j]
                if v <> 0uy then
                    mergeCols.[n] <- col
                    mergeCoefs.[n] <- v
                    n <- n + 1
                elif indexed then
                    removeColIndex col q
                i <- i + 1
                j <- j + 1
        let before = q.OwnedBytes
        q.EnsureCapacity n
        rowMemoryBytes <- rowMemoryBytes + q.OwnedBytes - before
        Array.Copy(mergeCols, q.Cols, n)
        Array.Copy(mergeCoefs, q.Coefs, n)
        q.Count <- n
        Gf16.mulAddInto c (ReadOnlySpan<byte>(r.Payload)) (Span<byte>(q.Payload))
        if n = 0 then
            if indexed then discard q
        elif n = 1 then solvedQueue.Push q

    /// Make the smallest column of r its pivot. Pivot rows keep a pivot coefficient of 1.

    /// and remove that column from all other rows.
    let addPivot (r: Row) =
        let p = r.Cols.[0]
        r.Pivot <- p
        let c = r.Coefs.[0]
        if c <> 1uy then
            let ic = Gf16.inv c
            for i in 0 .. r.Count - 1 do
                r.Coefs.[i] <- Gf16.mul ic r.Coefs.[i]
            Gf16.scaleInPlace ic (Span<byte>(r.Payload))
        match colRows.TryGetValue p with
        | true, list ->
            rowScratch.Clear()
            rowScratch.AddRange list
            for qi in 0 .. rowScratch.Count - 1 do
                let q = rowScratch.[qi]
                let at = q.Find p
                if at >= 0 then addScaled q q.Coefs.[at] r
        | _ -> ()
        r.Index <- rows.Count
        rows.Add r
        pivots.[p] <- r
        addRowIndex r
        if r.Count = 1 then solvedQueue.Push r

    /// Report a known source. Sources of a step with unknown k are reported when k arrives.
    let markKnown (t: uint64) (s: int) (recovered: bool) =
        ring.SetKnown(t, s)
        if s < ring.K t then report t s recovered
        elif ring.K t = 0 && ring.KnownMask t = UInt16.MaxValue then
            // All 16 slots of a step with unknown k are solved (its DATA and parities were
            // lost; BOOSTERs solved it). Unused slots are zero in every combination (5.6),
            // and a source symbol is never zero (its envelope is not), so k follows
            // (DECISIONS.md D7).
            let mutable k = SlotsPerStep
            while k > 1 && not ((Span<byte>.op_Implicit (ring.Symbol(t, k - 1))).ContainsAnyExcept 0uy) do
                k <- k - 1
            ring.SetK(t, k)
            for slot in 0 .. k - 1 do report t slot true

    /// Column (t, s) became known with value v (or known zero when v is empty).
    let substitute (t: uint64) (s: int) (v: ReadOnlySpan<byte>) =
        let col = colOf t s
        match pivots.TryGetValue col with
        | true, r ->
            pivots.Remove col |> ignore
            detach r
            removeRowIndex r
            if not v.IsEmpty then Gf16.xorInto v (Span<byte>(r.Payload))
            r.RemoveAt(r.Find col)
            if r.Count = 0 then recycle r else addPivot r
        | _ ->
            match colRows.TryGetValue col with
            | true, list ->
                rowScratch.Clear()
                rowScratch.AddRange list
                for qi in 0 .. rowScratch.Count - 1 do
                    let q = rowScratch.[qi]
                    let at = q.Find col
                    if at >= 0 then
                        if not v.IsEmpty then Gf16.mulAddInto q.Coefs.[at] v (Span<byte>(q.Payload))
                        q.RemoveAt at
                        if q.Count = 1 then solvedQueue.Push q
                        elif q.Count = 0 then discard q
                colRows.Remove col |> ignore
            | _ -> ()

    let advanceFrontier () =
        while ring.Contains frontier && ring.IsComplete frontier do
            frontier <- frontier + 1UL

    /// Pop solved rows until none is left. A solved row has one column with coefficient 1.
    let drainSolved () =
        while solvedQueue.Count > 0 do
            let r = solvedQueue.Pop()
            if r.Index >= 0 && r.Count = 0 then
                // Stale queue entry for a row that elimination already emptied.
                discard r
            elif r.Index >= 0 && r.Count = 1 then
                let col = r.Cols.[0]
                let t = stepOfCol col
                let s = slotOfCol col
                pivots.Remove col |> ignore
                detach r
                removeColIndex col r
                r.Payload.AsSpan().CopyTo(ring.Symbol(t, s))
                markKnown t s true
                // A source recovered by elimination is a known column for every other
                // equation too. Only the direct receive path (OfferSource) used to fold it
                // into the rows that still contained it, so an equation holding a recovered
                // column could never be reduced again: its column was known but no caller
                // eliminates it (substitute is private and nothing else touches a row's
                // columns). Such a row is unsolvable for the rest of the session, yet
                // `Deficit` still counts it as rank, so the caller was told there was more
                // rank available than existed and `addPivot` walked ever longer colRows lists
                // doing one symbolSize-byte GF16 multiply-add per member. Eliminate here,
                // exactly as substitute does for a received source.
                match colRows.TryGetValue col with
                | true, list ->
                    rowScratch.Clear()
                    rowScratch.AddRange list
                    for qi in 0 .. rowScratch.Count - 1 do
                        let q = rowScratch.[qi]
                        let at = q.Find col
                        if at >= 0 then
                            Gf16.mulAddInto q.Coefs.[at] (ReadOnlySpan<byte> r.Payload) (Span<byte>(q.Payload))
                            q.RemoveAt at
                            if q.Count = 1 then solvedQueue.Push q
                            elif q.Count = 0 then discard q
                    colRows.Remove col |> ignore
                | _ -> ()
                recycle r
        advanceFrontier ()

    /// Build a row from terms; subtract known sources. False when a term is outside the window.
    let buildRow (r: Row) (count: int) =
        let mutable ok = true
        let mutable n = 0
        let before = r.OwnedBytes
        r.EnsureCapacity count
        rowMemoryBytes <- rowMemoryBytes + r.OwnedBytes - before
        for i in 0 .. count - 1 do
            let term = terms.[i]
            let t = term.Step
            let s = int term.Slot
            if not (ensureStep t) then ok <- false
            elif ring.IsKnown(t, s) then
                let k = ring.K t
                if k = 0 || s < k then
                    Gf16.mulAddInto term.Coef (Span<byte>.op_Implicit (ring.Symbol(t, s))) (Span<byte>(r.Payload))
            else
                r.Cols.[n] <- colOf t s
                r.Coefs.[n] <- term.Coef
                n <- n + 1
        r.Count <- n
        if n > 1 then Array.Sort(r.Cols, r.Coefs, 0, n)
        ok

    let offer (count: int) (payload: ReadOnlySpan<byte>) =
        let mutable newest = 0UL
        for i in 0 .. count - 1 do
            if terms.[i].Step > newest then newest <- terms.[i].Step
        if newest < frontier then EquationResult.Useless
        else
            match rentRow () with
            | ValueNone -> EquationResult.OutOfWindow
            | ValueSome r ->
                payload.CopyTo(Span<byte>(r.Payload))
                if not (buildRow r count) then
                    recycle r
                    EquationResult.OutOfWindow
                elif r.Count = 0 then
                    recycle r
                    EquationResult.Useless
                else
                    // Reduce by existing pivots. A pivot row holds no other pivot column,
                    // so the pivot columns present now are the only ones to remove.
                    let mutable np = 0
                    for i in 0 .. r.Count - 1 do
                        if pivots.ContainsKey r.Cols.[i] then
                            pivotScratch.[np] <- r.Cols.[i]
                            np <- np + 1
                    for i in 0 .. np - 1 do
                        let col = pivotScratch.[i]
                        addScaled r r.Coefs.[r.Find col] pivots.[col]
                    if r.Count = 0 then
                        recycle r
                        EquationResult.Redundant
                    else
                        addPivot r
                        drainSolved ()
                        EquationResult.Innovative

    /// t_lo: every step below it is complete.
    member _.Frontier = frontier
    /// Steps for which the peer gave two different k (a protocol error of the peer).
    member _.KConflicts = kConflicts
    /// Lowest step whose sources are still stored.
    member _.BaseStep = baseStep
    /// Highest step seen plus one.
    member _.SeenEnd = seenEnd
    /// Pending (unsolved) equations.
    member _.PendingRows = rows.Count
    /// Rows handed out and not yet given back, including idle ones. The count must equal
    /// `rows.Count + freeRows.Count`: a row that is neither indexed nor idle is a leak.
    member _.RowsAllocated = rowsAllocated
    /// Idle rows available for reuse.
    member _.FreeRows = freeRows.Count
    /// Rows discarded by Release because they held a column of an already released step.
    member _.StaleRows = staleRows
    /// True when every source slot of step t is known. The peer reports these in the
    /// WIN_ACK decoded bitmap (spec 6.10) so the sender stops repairing steps that arrived.
    member _.IsStepComplete(t: uint64) =
        t < frontier || (ring.Contains t && ring.IsComplete t)
    /// True when this slot's first report said "rebuilt by elimination" and no direct
    /// arrival has been reported for it since. RxHalf uses it to withdraw a pending erasure
    /// that the slot's own DATA cell disproves inside the step deadline.
    member _.WasRecovered(t: uint64, s: int) = wasRecovered t s

    /// Retire the rebuild flag for one slot once its pending erasure was withdrawn.
    ///
    /// Duplicates are normal on this transport: a repair sends the same cell again, TCP
    /// retransmits a segment, and a late copy of a rebuilt slot arrives after the step
    /// decoded. Without retiring the flag, the second copy of the same slot withdrew the
    /// same pending erasure again, because the only other guard was "some slot of this step
    /// is still pending". One rebuild, several withdrawals: erasures were under-counted.
    member _.AcknowledgeRecovered(t: uint64, s: int) =
        let i = int (t % uint64 capacitySteps)
        if s >= 0 && s < int CodeGraph.SlotsPerStep then
            recoveredBits.[i] <- recoveredBits.[i] &&& ~~~(1us <<< s)
    /// Known source slots of step t among its k (0 when the step is outside the window).
    member _.StepKnownCount(t: uint64) =
        if not (ring.Contains t) then 0
        else
            let k = ring.K t
            let used = if k = 0 then SlotsPerStep else k
            let m = int (ring.KnownMask t) &&& ((1 <<< used) - 1)
            Numerics.BitOperations.PopCount(uint32 m)
    member _.SymbolSize = symbolSize

    /// 256-bit selective-ack bitmap (spec 6.10): bit j is set iff step (tLo + 1 + j) is complete.
    member _.BuildDecodedBitmap(tLo: uint64) =
        let mutable b0 = 0UL
        let mutable b1 = 0UL
        let mutable b2 = 0UL
        let mutable b3 = 0UL
        let limit = if seenEnd > tLo + 1UL then int (min 256UL (seenEnd - (tLo + 1UL))) else 0
        let mutable j = 0
        while j < limit do
            let st = tLo + 1UL + uint64 j
            if ring.Contains st && ring.IsComplete st then
                let bit = 1UL <<< (j &&& 63)
                match j >>> 6 with
                | 0 -> b0 <- b0 ||| bit
                | 1 -> b1 <- b1 ||| bit
                | 2 -> b2 <- b2 ||| bit
                | _ -> b3 <- b3 ||| bit
            j <- j + 1
        struct (b0, b1, b2, b3)

    /// Unknown count among the steps [t, t + span), counting 16 slots for steps with unknown k.
    member _.Unknowns(t: uint64, span: int) =
        let mutable n = 0
        for j in 0 .. span - 1 do
            let st = t + uint64 j
            if st >= frontier then
                if ring.Contains st then
                    let k = ring.K st
                    let used = if k = 0 then 16 else k
                    let m = int (ring.KnownMask st) &&& ((1 <<< used) - 1)
                    n <- n + used - Numerics.BitOperations.PopCount(uint32 m)
                else n <- n + 16
        n

    /// Rank deficit of steps [t, t + span): unknowns minus pending rows whose unknowns all
    /// lie in that range (a row that also needs later unknowns cannot solve this range alone).
    member this.Deficit(t: uint64, span: int) =
        let mutable rank = 0
        let stop = t + uint64 span
        for r in rows do
            if r.Count > 0 && stepOfCol r.Cols.[0] >= t && stepOfCol r.Cols.[r.Count - 1] < stop then rank <- rank + 1
        this.Unknowns(t, span) - rank

    /// Step t exists at the sender (HEARTBEAT, spec 5.8 tail rule). False outside the window.
    member _.NoteSeen(t: uint64) = t < frontier || ensureStep t

    /// Record step_used k of step t (from meta.step_used or meta.k_hist).
    /// False on a conflicting k or a step outside the window.
    member _.NoteStepUsed(t: uint64, k: int) =
        if k < 1 || k > SlotsPerStep then false
        elif t < frontier then true
        elif not (ensureStep t) then false
        else
            let old = ring.K t
            if old = k then true
            elif old <> 0 then
                // The authenticated peer gave two k for one step: its stream is inconsistent.
                kConflicts <- kConflicts + 1L
                false
            else
                ring.SetK(t, k)
                for s in 0 .. k - 1 do
                    // Only slots not yet reported: a directly received source keeps the
                    // `false` it was reported with, so k arriving late cannot relabel it as
                    // an FEC reconstruction (see `reported`).
                    if ring.IsKnown(t, s) then report t s true
                for s in k .. SlotsPerStep - 1 do
                    if not (ring.IsKnown(t, s)) then
                        ring.SetKnown(t, s)
                        substitute t s ReadOnlySpan<byte>.Empty
                drainSolved ()
                true

    /// A source symbol arrived (DATA cell). False when outside the window or slot >= k.
    member this.OfferSource(t: uint64, slot: int, k: int, symbol: ReadOnlySpan<byte>) =
        if slot < 0 || slot >= k || symbol.Length <> symbolSize then false
        elif t < frontier then true
        elif not (this.NoteStepUsed(t, k)) then false
        elif ring.IsKnown(t, slot) then true
        else
            symbol.CopyTo(ring.Symbol(t, slot))
            markKnown t slot false
            substitute t slot symbol
            drainSolved ()
            true

    /// Offer parity p of step t. The caller first records k of t and of its history steps.
    member _.OfferParity(t: uint64, p: int, field: CodeField, urgent: bool, k: int, payload: ReadOnlySpan<byte>) =
        if payload.Length <> symbolSize then invalidArg "payload" "bad symbol length"
        offer (parityTerms graphSeed t p field k urgent (Span<Term>(terms))) payload

    /// Offer BOOSTER (tLo, span, seq).
    member _.OfferBooster(tLo: uint64, span: int, seq: int, payload: ReadOnlySpan<byte>) =
        if payload.Length <> symbolSize then invalidArg "payload" "bad symbol length"
        offer (boosterTerms graphSeed tLo span seq (Span<Term>(terms))) payload

    /// Take the next newly known source (t, slot, recovered). Received and recovered sources
    /// both appear, each exactly once.
    member _.TryTakeKnown(t: byref<uint64>, slot: byref<int>, recovered: byref<bool>) =
        if knownQueue.Count = 0 then false
        else
            let struct (a, b, c) = knownQueue.Dequeue()
            t <- a
            slot <- int b
            recovered <- c
            true

    /// O(1): the ledger is maintained at the exact array allocation and release sites.
    member _.RowMemoryBytes = rowMemoryBytes

    /// Test-only slow recomputation from the arrays actually retained by active and warm-idle
    /// rows. Excludes CLR object/index overhead by the same contract as `RowMemoryBytes`.
    member _.RecomputeRowMemoryBytesForTest() =
        let mutable bytes = 0L
        for row in rows do bytes <- bytes + int64 row.Payload.Length + int64 row.Cols.Length * 8L + int64 row.Coefs.Length
        for row in freeRows do bytes <- bytes + int64 row.Payload.Length + int64 row.Cols.Length * 8L + int64 row.Coefs.Length
        bytes

    member _.StepUsed(t: uint64) = if ring.Contains t then ring.K t else 0

    member _.Source(t: uint64, slot: int) : ReadOnlySpan<byte> =
        if not (ring.Contains t) || not (ring.IsKnown(t, slot)) then invalidArg "t" "source not known"
        Span<byte>.op_Implicit (ring.Symbol(t, slot))

    /// Free storage of steps below `below`. Steps at or above frontier - 256 stay, because
    /// equations of incomplete steps can still refer to them.
    member _.Release(below: uint64) =
        let floor = if frontier > HistorySpan then frontier - HistorySpan else 0UL
        let limit = min below floor
        while baseStep < limit do
            ring.Free baseStep
            baseStep <- baseStep + 1UL
        // Row sweep. After `baseStep` has advanced, walk the live rows and:
        //   · recycle any row that already reduced to zero columns;
        //   · for a row whose smallest column belongs to a step below `baseStep`, remove that
        //     row's columns from `colRows` and then recycle it (counting `staleRows`).
        // The two loops after this sweep drop `colRows` / `pivots` keys whose step is below
        // `baseStep`.
        //
        // Column encoding, for anyone reading the comparison below: `colOf t s = (t <<< 4) ||| s`,
        // so a step contributes 16 columns (`SlotsPerStep`), and a column's step is `c >>> 4`.
        //
        // Tested: `CodecTests` has one concrete construction in which a pending equation's
        // smallest column sits exactly on the floor (`frontier − HistorySpan`). It asserts that
        // this sweep leaves that equation in place — `StaleRows = 0`, `BaseStep` equal to the
        // floor, `PendingRows` unchanged, the row-accounting identity intact — and that the
        // equation can still be completed afterwards, advancing the frontier. That is the extent
        // of the coverage: one boundary case in which the branch must not fire. Nothing here
        // establishes whether the branch fires in other states, and no claim is made about the
        // cost of this sweep (the design record §7.6 withdrew every causal statement about
        // decoder cost).
        //
        // Columns are sorted ascending, so Cols.[0] is the smallest one and decides
        // reachability. Detach swaps the last row into the freed slot, so the index must not
        // advance when a row is discarded.
        let mutable i = 0
        while i < rows.Count do
            let r = rows.[i]
            if r.Count = 0 then discard r
            elif stepOfCol r.Cols.[0] < baseStep then
                staleRows <- staleRows + 1L
                // Detach from the column index before recycling: `discard` calls `detach` and
                // `recycle`, but not `removeRowIndex`, so without this call the row's column
                // entries in `colRows` would keep pointing at a Row object that goes back to
                // `freeRows` and can be rented again for different columns. Every other discard
                // site (addScaled, drainSolved, substitute) has already removed its columns by
                // the time it calls `discard`.
                removeRowIndex r
                discard r
            else i <- i + 1
        if colRows.Count > 0 then
            staleCols.Clear()
            for key in colRows.Keys do
                if stepOfCol key < baseStep then staleCols.Add key
            for key in staleCols do
                colRows.Remove key |> ignore
        if pivots.Count > 0 then
            staleCols.Clear()
            for key in pivots.Keys do
                if stepOfCol key < baseStep then staleCols.Add key
            for key in staleCols do
                pivots.Remove key |> ignore
        // Give back rows left over from a burst. A row is symbolSize + 576 bytes, so the
        // 16384-row ceiling is about 32 MiB; holding all of it after one spike pinned a
        // small host for no benefit. Renting again later allocates, which is far cheaper
        // than carrying 32 MiB of idle payload buffers.
        let mutable drop = freeRows.Count - warmRows
        while drop > 0 do
            let r = freeRows.Pop()
            rowMemoryBytes <- rowMemoryBytes - r.OwnedBytes
            rowsAllocated <- rowsAllocated - 1
            drop <- drop - 1
