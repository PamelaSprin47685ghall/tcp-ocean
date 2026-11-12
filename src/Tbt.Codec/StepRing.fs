/// Fixed ring of step records with their 16 symbol slots. One allocation at start;
/// no allocation afterwards. The encoder and the decoder each own one.
namespace Tbt.Codec

open System

[<Sealed>]
type StepRing(capacitySteps: int, symbolSize: int) =
    do
        if capacitySteps < 1 || capacitySteps &&& (capacitySteps - 1) <> 0 then
            invalidArg "capacitySteps" "capacity must be a power of two"
        if symbolSize < 16 || symbolSize % 16 <> 0 then invalidArg "symbolSize" "bad symbol size"

    let mask = uint64 (capacitySteps - 1)
    let stride = CodeGraph.SlotsPerStep * symbolSize
    let data: byte[] = GC.AllocateUninitializedArray<byte>(capacitySteps * stride)
    let stepOf: uint64[] = Array.zeroCreate capacitySteps
    let used: bool[] = Array.zeroCreate capacitySteps
    /// 0 = step_used not known yet; else 1..16.
    let kOf: byte[] = Array.zeroCreate capacitySteps
    /// Bit s set = slot s value is known (received, recovered, or known zero).
    let known: uint16[] = Array.zeroCreate capacitySteps

    let index (t: uint64) = int (t &&& mask)

    member _.Capacity = capacitySteps
    member _.SymbolSize = symbolSize

    member _.Contains(t: uint64) =
        let i = index t
        used.[i] && stepOf.[i] = t

    /// Claim the record of step t. False when another live step holds the record.
    member _.TryOpen(t: uint64) =
        let i = index t
        if used.[i] then stepOf.[i] = t
        else
            used.[i] <- true
            stepOf.[i] <- t
            kOf.[i] <- 0uy
            known.[i] <- 0us
            true

    member this.Free(t: uint64) =
        if this.Contains t then used.[index t] <- false

    member _.K(t: uint64) = int kOf.[index t]
    member _.SetK(t: uint64, k: int) = kOf.[index t] <- byte k
    member _.KnownMask(t: uint64) = known.[index t]
    member _.SetKnown(t: uint64, slot: int) = known.[index t] <- known.[index t] ||| (1us <<< slot)
    member _.IsKnown(t: uint64, slot: int) = (known.[index t] >>> slot) &&& 1us <> 0us

    /// True when k is known and every used slot is known.
    member _.IsComplete(t: uint64) =
        let i = index t
        let k = int kOf.[i]
        k <> 0 && (int known.[i] &&& ((1 <<< k) - 1)) = (1 <<< k) - 1

    member _.Symbol(t: uint64, slot: int) = Span<byte>(data, index t * stride + slot * symbolSize, symbolSize)
