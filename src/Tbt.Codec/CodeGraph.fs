/// TBT-SC2 term lists (spec 5.6, 5.6a, 5.8). Byte-for-byte equal to ref/tbt_code.py.
/// A term says: add Coef * D[Step, Slot] to the symbol.
module Tbt.Codec.CodeGraph

open System
open Tbt.Contracts
open Tbt.Codec.SplitMix64

/// Source slots per step.
[<Literal>]
let SlotsPerStep = 16

/// Parity slots per step.
[<Literal>]
let MaxParity = 48

/// Parity indexes below this value are tier A (8 picks per stride); others are tier B (4 picks).
[<Literal>]
let TierBStart = 16

/// Past steps that each non-URGENT parity also covers.
let Strides: uint64[] = [| 1UL; 2UL; 4UL; 16UL; 64UL; 256UL |]

/// Longest stride: a source is used by parities up to this many steps later.
[<Literal>]
let HistorySpan = 256UL

/// Largest term count of one parity or BOOSTER symbol (16 + 6 * 8 = 64; BOOSTER 4 * 16 = 64).
[<Literal>]
let MaxTerms = 64

[<Literal>]
let MaxBoosterSpan = 4

[<Struct>]
type Term =
    { Step: uint64
      Slot: byte
      Coef: byte }

/// Nonzero GF(16) coefficient from one byte: (v mod 15) + 1.
let inline coef16 (v: uint64) = byte ((v &&& 0xFFUL) % 15UL + 1UL)

/// Write the terms of parity p of step t into dst; return the term count.
/// k = step_used of step t; urgent = self-contained parity without history terms.
let parityTerms (graphSeed: uint64) (t: uint64) (p: int) (field: CodeField) (k: int) (urgent: bool) (dst: Span<Term>) =
    if p < 0 || p >= MaxParity then invalidArg "p" "parity index out of range"
    if k < 1 || k > SlotsPerStep then invalidArg "k" "step_used out of range"
    let mutable rng = Rng(seedOf4 graphSeed DomParity (uint64 (int field)) t (uint64 p))
    let mutable n = 0
    // Part 1: dense terms on the current step. Slots >= k hold no source and are dropped.
    if p = 0 then
        for s in 0 .. k - 1 do
            dst.[n] <- { Step = t; Slot = byte s; Coef = 1uy }
            n <- n + 1
    else
        let mutable v = rng.Next()
        if field = CodeField.Gf2 then
            let mask =
                if k >= SlotsPerStep then
                    let m = v &&& 0xFFFFUL
                    if m = 0UL then 1UL <<< int ((v >>> 16) &&& 15UL) else m
                else
                    v % ((1UL <<< k) - 1UL) + 1UL
            for s in 0 .. k - 1 do
                if (mask >>> s) &&& 1UL <> 0UL then
                    dst.[n] <- { Step = t; Slot = byte s; Coef = 1uy }
                    n <- n + 1
        else
            for s in 0 .. SlotsPerStep - 1 do
                if s % 8 = 0 && s <> 0 then v <- rng.Next()
                if s < k then
                    dst.[n] <- { Step = t; Slot = byte s; Coef = coef16 (v >>> (8 * (s % 8))) }
                    n <- n + 1
    if not urgent then
        // Part 2: sparse terms on past steps (partial Fisher-Yates per stride). Random numbers
        // are drawn even for steps before stream start, so the stream never depends on t.
        // The 16-entry slot pool is packed as 16 nibbles of one uint64.
        let picks = if p < TierBStart then 8 else 4
        for r in Strides do
            let mutable pool = 0xFEDCBA9876543210UL
            for j in 0 .. picks - 1 do
                let v = rng.Next()
                let i = int (v % uint64 (SlotsPerStep - j))
                let last = SlotsPerStep - 1 - j
                let si = (pool >>> (4 * i)) &&& 0xFUL
                let sl = (pool >>> (4 * last)) &&& 0xFUL
                pool <- pool &&& ~~~((0xFUL <<< (4 * i)) ||| (0xFUL <<< (4 * last)))
                pool <- pool ||| (sl <<< (4 * i)) ||| (si <<< (4 * last))
                if t >= r then
                    let c = if field = CodeField.Gf2 then 1uy else coef16 (v >>> 32)
                    dst.[n] <- { Step = t - r; Slot = byte si; Coef = c }
                    n <- n + 1
    n

/// Write the terms of BOOSTER (tLo, span, seq) into dst; return the term count.
/// A BOOSTER is a dense GF(16) mix over all slots of steps tLo .. tLo + span - 1.
let boosterTerms (graphSeed: uint64) (tLo: uint64) (span: int) (seq: int) (dst: Span<Term>) =
    if span < 1 || span > MaxBoosterSpan then invalidArg "span" "BOOSTER span out of range"
    if seq < 0 || seq > 255 then invalidArg "seq" "booster_seq out of range"
    let mutable rng = Rng(seedOf4 graphSeed DomBoost tLo (uint64 span) (uint64 seq))
    let mutable n = 0
    let mutable v = 0UL
    for j in 0 .. span - 1 do
        for s in 0 .. SlotsPerStep - 1 do
            if s % 8 = 0 then v <- rng.Next()
            dst.[n] <- { Step = tLo + uint64 j; Slot = byte s; Coef = coef16 (v >>> (8 * (s % 8))) }
            n <- n + 1
    n
