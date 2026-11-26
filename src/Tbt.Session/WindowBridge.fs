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

    /// Encode a nonempty prefix of sealed step lengths into authenticated parity fields.
    /// Aux is the exclusive physical-slot boundary; KHist stores four k-1 nibbles.
    let sealSnapshot (stepLengths: int[]) =
        if isNull (box stepLengths) || stepLengths.Length < 1 || stepLengths.Length > StepsPerWindow then
            invalidArg (nameof stepLengths) "expected one to four sealed steps"
        let mutable history = 0u
        let mutable active = 0UL
        for i = 0 to stepLengths.Length - 1 do
            let k = stepLengths.[i]
            if k < 1 || k > SlotsPerStep then invalidArg (nameof stepLengths) "step length must be 1..16"
            history <- history ||| (uint32 (k - 1) <<< (4 * i))
            active <- active ||| (((1UL <<< k) - 1UL) <<< (i * SlotsPerStep))
        let tail = (stepLengths.Length - 1) * SlotsPerStep + stepLengths.[stepLengths.Length - 1]
        struct (uint16 tail, history, active)

    /// Validate and decode a parity snapshot. It must already have been authenticated.
    let trySnapshot (tail: uint16) (history: uint32) =
        if tail < 1us || tail > uint16 SymbolsPerWindow || history &&& 0xFFFF0000u <> 0u then ValueNone
        else
            let count = (int tail - 1) / SlotsPerStep + 1
            let usedBits = count * 4
            let allowed = (1u <<< usedBits) - 1u
            if history &&& ~~~allowed <> 0u then ValueNone
            else
                let last = int ((history >>> ((count - 1) * 4)) &&& 15u) + 1
                if (count - 1) * SlotsPerStep + last <> int tail then ValueNone
                else
                    let mutable active = 0UL
                    for i = 0 to count - 1 do
                        let k = int ((history >>> (4 * i)) &&& 15u) + 1
                        active <- active ||| (((1UL <<< k) - 1UL) <<< (i * SlotsPerStep))
                    ValueSome active

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

    /// Rebuild coefficients from one authenticated, immutable parity snapshot.
    /// This experimental API is not wired into WindowReceive or the live protocol.
    let trySnapshotParityMask (meta: Meta) =
        if meta.Type <> CellType.Parity || meta.Slot >= 48uy
           || meta.StepId % uint64 StepsPerWindow <> 0UL then ValueNone
        else
            match trySnapshot meta.Aux meta.KHist with
            | ValueNone -> ValueNone
            | ValueSome active ->
                let count = int (System.Numerics.BitOperations.PopCount active)
                let degree = WindowCodec.WindowSessionCodec.degree count
                let seed = WindowCodec.WindowSessionCodec.paritySeed (windowId meta.StepId) (int meta.Slot) (int meta.Aux)
                ValueSome (WindowCodec.coefficientsFromMask active degree seed)

    /// Build parity metadata and its coefficients from the same sealed-step snapshot.
    let snapshotParity (id: uint64) (parityIdx: int) (stepLengths: int[]) =
        if id > UInt64.MaxValue / uint64 StepsPerWindow then invalidArg (nameof id) "window origin overflows"
        let struct (tail, history, _) = sealSnapshot stepLengths
        let meta = parityMeta { WindowId = id; FirstStep = firstStep id; ParityIdx = parityIdx; Tail = int tail }
        let meta = { meta with KHist = history }
        match trySnapshotParityMask meta with
        | ValueSome mask -> struct (meta, mask)
        | ValueNone -> invalidOp "invalid sealed parity snapshot"

/// Tracks authenticated step lengths for one window; updates are atomic on conflict.
[<Sealed>]
type WindowSnapshotTracker(windowId: uint64) =
    let lengths: byte[] = Array.zeroCreate WindowBridge.StepsPerWindow

    member _.TryData(step: uint64, slot: int, stepUsed: int) =
        if WindowBridge.windowId step <> windowId || stepUsed < 1 || stepUsed > 16
           || slot < 0 || slot >= stepUsed then false
        else
            let i = int (step % uint64 WindowBridge.StepsPerWindow)
            if lengths.[i] <> 0uy && int lengths.[i] <> stepUsed then false
            else
                lengths.[i] <- byte stepUsed
                true

    /// The caller authenticates the frame before supplying its metadata.
    member _.TrySnapshot(meta: Meta) =
        if WindowBridge.windowId meta.StepId <> windowId
           || (WindowBridge.trySnapshotParityMask meta |> ValueOption.isNone) then false
        else
            let count = (int meta.Aux - 1) / WindowBridge.SlotsPerStep + 1
            let mutable valid = true
            for i = 0 to count - 1 do
                let k = byte (int ((meta.KHist >>> (4 * i)) &&& 15u) + 1)
                if lengths.[i] <> 0uy && lengths.[i] <> k then valid <- false
            if valid then
                for i = 0 to count - 1 do
                    lengths.[i] <- byte (int ((meta.KHist >>> (4 * i)) &&& 15u) + 1)
            valid

    member this.Copy() =
        let copy = WindowSnapshotTracker(windowId)
        for i = 0 to WindowBridge.StepsPerWindow - 1 do
            if lengths.[i] <> 0uy then
                copy.TryData(windowId * 4UL + uint64 i, 0, int lengths.[i]) |> ignore
        copy

    member _.StepUsed(offset: int) =
        if offset < 0 || offset >= WindowBridge.StepsPerWindow then invalidArg (nameof offset) "outside window"
        if lengths.[offset] = 0uy then ValueNone else ValueSome(int lengths.[offset])

[<Sealed>]
type WindowReceive(symbolSize: int, maxWindows: int, maxBytes: int64) =
    let pool = WindowCodec.WindowCodecPool(symbolSize, maxWindows, maxBytes)
    let trackers = System.Collections.Generic.Dictionary<uint64, WindowSnapshotTracker>()

    let add (id: uint64) (data: byte[]) (update: WindowSnapshotTracker -> bool) (mask: uint64) =
        if isNull (box data) || data.Length <> symbolSize then false
        else
            let tracker =
                match trackers.TryGetValue id with
                | true, existing -> existing.Copy()
                | _ -> WindowSnapshotTracker(id)
            if not (update tracker) then false
            elif pool.TryGet(id).IsNone && not (pool.TryOpen(id, 64)) then false
            else
                match pool.TryGet id with
                | ValueNone -> false
                | ValueSome decoder ->
                    try
                        let innovative = decoder.Add(mask, data)
                        trackers.[id] <- tracker
                        innovative
                    with :? InvalidOperationException -> false

    member _.Pool = pool
    member _.Count = pool.Count
    member _.AllocatedBytes = pool.AllocatedBytes

    /// Only authenticated frames may be submitted. False includes duplicates and rejects.
    member _.AddSource(step: uint64, slot: int, stepUsed: int, data: byte[]) =
        match WindowBridge.tryIndex (step, slot) with
        | ValueNone -> false
        | ValueSome index ->
            let id = WindowBridge.windowId step
            add id data (fun tracker -> tracker.TryData(step, slot, stepUsed)) (1UL <<< index)

    member _.AddParity(meta: Meta, data: byte[]) =
        match WindowBridge.trySnapshotParityMask meta with
        | ValueNone -> false
        | ValueSome mask ->
            let id = WindowBridge.windowId meta.StepId
            add id data (fun tracker -> tracker.TrySnapshot meta) mask

    /// Release only windows fully below the exclusive delivered step frontier.
    member _.ReleaseThrough(ackStep: uint64) =
        let completed = ackStep / uint64 WindowBridge.StepsPerWindow
        if completed > 0UL then
            pool.ReleaseThrough(completed - 1UL)
            let removed = ResizeArray<uint64>()
            for KeyValue(id, _) in trackers do
                if id < completed then removed.Add id
            for id in removed do trackers.Remove id |> ignore
