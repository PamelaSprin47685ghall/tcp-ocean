/// Sender side of TBT-SC2: keeps source symbols of the send window and renders
/// parity and BOOSTER symbols from them (spec 5.6, 5.8, 6.9.2).
namespace Tbt.Codec

open System
open Tbt.Contracts
open Tbt.Codec.CodeGraph

[<Sealed>]
type Encoder(graphSeed: uint64, symbolSize: int, capacitySteps: int) =
    let ring = StepRing(capacitySteps, symbolSize)
    let terms: Term[] = Array.zeroCreate MaxTerms
    /// Oldest step still held.
    let mutable tail = 0UL
    /// Step being filled; all steps below it are sealed.
    let mutable head = 0UL
    let mutable headOpen = false

    let combine (count: int) (dst: Span<byte>) =
        dst.Clear()
        let mutable ok = true
        for i in 0 .. count - 1 do
            let term = terms.[i]
            if term.Step < tail then ok <- false
            elif int term.Slot < ring.K term.Step then
                Gf16.mulAddInto term.Coef (Span<byte>.op_Implicit (ring.Symbol(term.Step, int term.Slot))) dst
        ok

    member _.SymbolSize = symbolSize
    /// Most steps held at once.
    member _.Capacity = capacitySteps
    /// Next step number to be sealed.
    member _.Head = head
    /// Oldest step whose sources are still held.
    member _.Tail = tail
    /// Number of steps held (sealed or being filled).
    member _.HeldSteps = int (head - tail) + (if headOpen then 1 else 0)

    /// Slot s of the step being filled. The caller writes envelope and bytes, zero padded.
    /// False when the window is full (the oldest step is not released yet).
    member _.TryGetFillSlot(slot: int, symbol: byref<Span<byte>>) =
        if slot < 0 || slot >= SlotsPerStep then invalidArg "slot" "slot out of range"
        if not headOpen then
            if head - tail >= uint64 capacitySteps || not (ring.TryOpen head) then
                false
            else
                headOpen <- true
                symbol <- ring.Symbol(head, slot)
                true
        else
            symbol <- ring.Symbol(head, slot)
            true

    /// Seal the step being filled with k used slots. Returns its step number.
    member _.Seal(k: int) =
        if not headOpen then invalidOp "no step is being filled"
        if k < 1 || k > SlotsPerStep then invalidArg "k" "step_used out of range"
        ring.SetK(head, k)
        let t = head
        head <- head + 1UL
        headOpen <- false
        t

    member _.StepUsed(t: uint64) = if t >= tail && t < head then ring.K t else 0

    member _.Source(t: uint64, slot: int) : ReadOnlySpan<byte> =
        if t < tail || t >= head || slot >= ring.K t then invalidArg "t" "source not held"
        Span<byte>.op_Implicit (ring.Symbol(t, slot))

    /// Render parity p of sealed step t into dst (length S). False when a needed source is released.
    member _.TryRenderParity(t: uint64, p: int, field: CodeField, urgent: bool, dst: Span<byte>) =
        if t < tail || t >= head then false
        else combine (parityTerms graphSeed t p field (ring.K t) urgent (Span<Term>(terms))) dst

    /// Render BOOSTER (tLo, span, seq) into dst (length S). False when a covered step is not held.
    member _.TryRenderBooster(tLo: uint64, span: int, seq: int, dst: Span<byte>) =
        if tLo < tail || tLo + uint64 span > head then false
        else combine (boosterTerms graphSeed tLo span seq (Span<Term>(terms))) dst

    /// Release all sources of steps below `below` (spec 6.9.2: below = peer_ack - 256,
    /// after borrows end). The caller owns that policy; this call only frees storage.
    member _.Release(below: uint64) =
        let limit = min below head
        while tail < limit do
            ring.Free tail
            tail <- tail + 1UL
