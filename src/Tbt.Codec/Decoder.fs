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

    let colOf (t: uint64) (s: int) = (t <<< 4) ||| uint64 s
    let stepOfCol (c: uint64) = c >>> 4
    let slotOfCol (c: uint64) = int (c &&& 15UL)

    let inWindow (t: uint64) = t >= baseStep && t < baseStep + uint64 capacitySteps

    let ensureStep (t: uint64) =
        if ring.Contains t then true
        elif inWindow t && ring.TryOpen t then
            if t >= seenEnd then seenEnd <- t + 1UL
            true
        else false

    let rentRow () =
        if freeRows.Count > 0 then ValueSome(freeRows.Pop())
        elif rowsAllocated < maxRows then
            rowsAllocated <- rowsAllocated + 1
            ValueSome(Row(symbolSize))
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

    /// q <- q + c * r. Both rows sorted; zero sums are dropped.
    let addScaled (q: Row) (c: byte) (r: Row) =
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
                mergeCols.[n] <- r.Cols.[j]
                mergeCoefs.[n] <- Gf16.mul c r.Coefs.[j]
                n <- n + 1
                j <- j + 1
            else
                let v = q.Coefs.[i] ^^^ Gf16.mul c r.Coefs.[j]
                if v <> 0uy then
                    mergeCols.[n] <- q.Cols.[i]
                    mergeCoefs.[n] <- v
                    n <- n + 1
                i <- i + 1
                j <- j + 1
        q.EnsureCapacity n
        Array.Copy(mergeCols, q.Cols, n)
        Array.Copy(mergeCoefs, q.Coefs, n)
        q.Count <- n
        Gf16.mulAddInto c (ReadOnlySpan<byte>(r.Payload)) (Span<byte>(q.Payload))
        if n = 1 then solvedQueue.Push q

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
        for qi in 0 .. rows.Count - 1 do
            let q = rows.[qi]
            let at = q.Find p
            if at >= 0 then addScaled q q.Coefs.[at] r
        r.Index <- rows.Count
        rows.Add r
        pivots.[p] <- r
        if r.Count = 1 then solvedQueue.Push r

    /// Report a known source. Sources of a step with unknown k are reported when k arrives.
    let markKnown (t: uint64) (s: int) (recovered: bool) =
        ring.SetKnown(t, s)
        if s < ring.K t then knownQueue.Enqueue(struct (t, byte s, recovered))
        elif ring.K t = 0 && ring.KnownMask t = UInt16.MaxValue then
            // All 16 slots of a step with unknown k are solved (its DATA and parities were
            // lost; BOOSTERs solved it). Unused slots are zero in every combination (5.6),
            // and a source symbol is never zero (its envelope is not), so k follows
            // (DECISIONS.md D7).
            let mutable k = SlotsPerStep
            while k > 1 && not ((Span<byte>.op_Implicit (ring.Symbol(t, k - 1))).ContainsAnyExcept 0uy) do
                k <- k - 1
            ring.SetK(t, k)
            for slot in 0 .. k - 1 do knownQueue.Enqueue(struct (t, byte slot, true))

    /// Column (t, s) became known with value v (or known zero when v is empty).
    let substitute (t: uint64) (s: int) (v: ReadOnlySpan<byte>) =
        let col = colOf t s
        match pivots.TryGetValue col with
        | true, r ->
            pivots.Remove col |> ignore
            detach r
            if not v.IsEmpty then Gf16.xorInto v (Span<byte>(r.Payload))
            r.RemoveAt(r.Find col)
            if r.Count = 0 then recycle r else addPivot r
        | _ ->
            for qi in 0 .. rows.Count - 1 do
                let q = rows.[qi]
                let at = q.Find col
                if at >= 0 then
                    if not v.IsEmpty then Gf16.mulAddInto q.Coefs.[at] v (Span<byte>(q.Payload))
                    q.RemoveAt at
                    if q.Count = 1 then solvedQueue.Push q

    let advanceFrontier () =
        while ring.Contains frontier && ring.IsComplete frontier do
            frontier <- frontier + 1UL

    /// Pop solved rows until none is left. A solved row has one column with coefficient 1.
    let drainSolved () =
        while solvedQueue.Count > 0 do
            let r = solvedQueue.Pop()
            if r.Index >= 0 && r.Count = 1 then
                let col = r.Cols.[0]
                let t = stepOfCol col
                let s = slotOfCol col
                pivots.Remove col |> ignore
                detach r
                r.Payload.AsSpan().CopyTo(ring.Symbol(t, s))
                markKnown t s true
                recycle r
        advanceFrontier ()

    /// Build a row from terms; subtract known sources. False when a term is outside the window.
    let buildRow (r: Row) (count: int) =
        let mutable ok = true
        let mutable n = 0
        r.EnsureCapacity count
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
    member _.SymbolSize = symbolSize

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
                    if ring.IsKnown(t, s) then knownQueue.Enqueue(struct (t, byte s, true))
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
