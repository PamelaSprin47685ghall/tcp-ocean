/// Maps the 16-slot step of TBT-SC2 onto a 64-symbol sliding window.
/// Four consecutive steps are one window: symbol index = (step mod 4) * 16 + slot.
/// Parity carries no coefficient vector. Slot is the parity index; Aux is the sealed
/// tail length k. StepId is the window's first step, so both ends rebuild the mask.
/// This file does not touch the live send or receive path.
namespace Tbt.Session

open System
open Tbt.Codec
open Tbt.Contracts
open Tbt.Wire

[<Struct>]
type WindowParity =
    { WindowId: uint64
      /// First step of the window. Four steps share one id.
      FirstStep: uint64
      ParityIdx: int
      /// Sealed source count, 1..64. A short tail does not wait to fill the window.
      Tail: int }

module WindowBridge =
    [<Literal>]
    let StepsPerWindow = 4

    [<Literal>]
    let SlotsPerStep = 16

    [<Literal>]
    let SymbolsPerWindow = 64

    let windowId (step: uint64) = step / uint64 StepsPerWindow

    let firstStep (id: uint64) = id * uint64 StepsPerWindow

    /// Index inside the 64-symbol window. False when the slot is outside a 16-slot step.
    let tryIndex (step: uint64, slot: int) =
        if slot < 0 || slot >= SlotsPerStep then ValueNone
        else ValueSome(int (step % uint64 StepsPerWindow) * SlotsPerStep + slot)

    /// Inverse of tryIndex. Step offset is 0..3 inside the window.
    let stepSlot (index: int) =
        if index < 0 || index >= SymbolsPerWindow then invalidArg (nameof index) "outside window"
        struct (index / SlotsPerStep, index % SlotsPerStep)

    /// Tail length implied by a sealed step count. A full window is 64; a burst
    /// ending mid-window is (completed steps)*16 + used slots of the last step.
    let tailLength (sealedStepsInWindow: int) (lastStepUsed: int) =
        if sealedStepsInWindow < 0 || sealedStepsInWindow > StepsPerWindow then
            invalidArg (nameof sealedStepsInWindow) "0..4"
        if lastStepUsed < 0 || lastStepUsed > SlotsPerStep then
            invalidArg (nameof lastStepUsed) "0..16"
        if sealedStepsInWindow = StepsPerWindow then SymbolsPerWindow
        elif lastStepUsed = 0 then sealedStepsInWindow * SlotsPerStep
        else sealedStepsInWindow * SlotsPerStep + lastStepUsed

    /// DATA meta for a systematic source. KHist stays zero: the window code does not
    /// use the old stride history, and a zero field is legal for an old reader.
    let sourceMeta (step: uint64) (slot: int) (stepUsed: int) =
        if stepUsed < 1 || stepUsed > SlotsPerStep || slot >= stepUsed then
            invalidArg (nameof slot) "slot outside step_used"
        { Type = CellType.Data
          Flags = MetaFlags.None
          Slot = byte slot
          StepUsed = byte stepUsed
          Aux = 0us
          RxWinGrant = 0us
          StepId = step
          AckStepLo = 0UL
          CarrierWm = 0UL
          KHist = 0u
          ErasureQ16 = 0us }

    /// PARITY meta. StepId is the window origin so the receiver derives the same mask.
    /// Aux is k, the sealed tail. Slot is the parity index, still inside the 0..47 range
    /// Meta.tryRead already accepts.
    let parityMeta (parity: WindowParity) =
        if parity.Tail < 1 || parity.Tail > SymbolsPerWindow then
            invalidArg (nameof parity) "tail length"
        if parity.ParityIdx < 0 || parity.ParityIdx >= 48 then
            invalidArg (nameof parity) "parity index"
        if parity.FirstStep <> firstStep parity.WindowId then
            invalidArg (nameof parity) "first step is not the window origin"
        { Type = CellType.Parity
          Flags = MetaFlags.None
          Slot = byte parity.ParityIdx
          StepUsed = 16uy
          Aux = uint16 parity.Tail
          RxWinGrant = 0us
          StepId = parity.FirstStep
          AckStepLo = 0UL
          CarrierWm = 0UL
          KHist = 0u
          ErasureQ16 = 0us }

    let tryParity (meta: Meta) =
        if meta.Type <> CellType.Parity || meta.Aux < 1us || meta.Aux > uint16 SymbolsPerWindow
           || int meta.Slot >= 48 || meta.StepId % uint64 StepsPerWindow <> 0UL then
            ValueNone
        else
            ValueSome
                { WindowId = windowId meta.StepId
                  FirstStep = meta.StepId
                  ParityIdx = int meta.Slot
                  Tail = int meta.Aux }

[<Sealed>]
type WindowReceive(symbolSize: int, maxWindows: int, maxBytes: int64) =
    let pool = WindowCodec.WindowCodecPool(symbolSize, maxWindows, maxBytes)

    member _.Pool = pool
    member _.Count = pool.Count
    member _.AllocatedBytes = pool.AllocatedBytes

    /// Ingest one systematic source. Opens the window at the running tail length so a
    /// symbol can land before the tail is sealed. False when the pool refuses the window.
    member _.AddSource(step: uint64, slot: int, tail: int, data: byte[]) =
        match WindowBridge.tryIndex (step, slot) with
        | ValueNone -> false
        | ValueSome index when index >= tail -> false
        | ValueSome index ->
            let id = WindowBridge.windowId step
            if pool.TryGet(id).IsNone && not (pool.TryOpen(id, tail)) then false
            else
                match pool.TryGet id with
                | ValueSome decoder -> decoder.Add(1UL <<< index, data)
                | ValueNone -> false

    /// Ingest one parity symbol whose mask is rebuilt from the meta, not from the wire.
    member _.AddParity(meta: Meta, data: byte[]) =
        match WindowBridge.tryParity meta with
        | ValueNone -> false
        | ValueSome p ->
            let id = p.WindowId
            if pool.TryGet(id).IsNone && not (pool.TryOpen(id, p.Tail)) then false
            else
                match pool.TryGet id with
                | ValueSome decoder ->
                    let mask = WindowCodec.WindowSessionCodec.parityMask id p.ParityIdx p.Tail
                    decoder.Add(mask, data)
                | ValueNone -> false

    /// Delivered through this window id. Storage goes back to the pool budget.
    member _.ReleaseThrough(ackStep: uint64) =
        if ackStep > 0UL then pool.ReleaseThrough(WindowBridge.windowId (ackStep - 1UL))
