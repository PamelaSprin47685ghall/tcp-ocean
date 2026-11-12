/// The 40-byte encrypted meta of every cell (spec 6.2). Little endian.
namespace Tbt.Wire

open System
open System.Buffers.Binary
open Tbt.Contracts

[<Flags>]
type MetaFlags =
    | None = 0uy
    | EndOfBurst = 1uy
    | CongestionWarn = 2uy
    | Fill = 4uy
    | Urgent = 16uy

[<Struct>]
type Meta =
    { Type: CellType
      Flags: MetaFlags
      /// DATA: source slot; PARITY: parity index; BOOSTER: booster_seq; else 0.
      Slot: byte
      /// DATA/PARITY: k of the step; BOOSTER: span; else 0.
      StepUsed: byte
      /// DATA/PARITY: m_t (statistics only); BOOSTER: count n of this round; else 0.
      Aux: uint16
      RxWinGrant: uint16
      /// DATA/PARITY: t; BOOSTER: t_lo; HEARTBEAT: tx_head_step; else 0.
      StepId: uint64
      AckStepLo: uint64
      CarrierWm: uint64
      /// k - 1 of steps t-1, t-2, t-4, t-16, t-64, t-256: nibble i = stride i.
      KHist: uint32
      ErasureQ16: uint16 }

module Meta =
    let private reservedFlags = 0xE8uy

    /// Pack history step_used values: nibble i (bits 4i..4i+3, little endian 24-bit) holds
    /// k - 1 of step t - stride[i]. Steps before stream start use 0.
    let packKHist (k: ReadOnlySpan<int>) =
        let mutable v = 0u
        for i in 0 .. 5 do
            let ki = if k.[i] < 1 then 0 else k.[i] - 1
            v <- v ||| (uint32 (ki &&& 15) <<< (4 * i))
        v

    /// step_used of history entry i (1..16).
    let kHistAt (kHist: uint32) (i: int) = int ((kHist >>> (4 * i)) &&& 15u) + 1

    let write (m: Meta) (dst: Span<byte>) =
        dst.[0] <- byte m.Type
        dst.[1] <- byte m.Flags
        dst.[2] <- m.Slot
        dst.[3] <- m.StepUsed
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 4, m.Aux)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 6, m.RxWinGrant)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 8, m.StepId)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 16, m.AckStepLo)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 24, m.CarrierWm)
        dst.[32] <- byte m.KHist
        dst.[33] <- byte (m.KHist >>> 8)
        dst.[34] <- byte (m.KHist >>> 16)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 35, m.ErasureQ16)
        dst.Slice(37, 3).Clear()

    let private knownType (t: byte) = (t >= 1uy && t <= 6uy) || t = 8uy || t = 9uy

    /// Range checks that do not need session state (spec 6.2, 6.15).
    let private shapeOk (m: Meta) =
        match m.Type with
        | CellType.Data -> m.StepUsed >= 1uy && m.StepUsed <= 16uy && m.Slot < m.StepUsed
        | CellType.Parity -> m.StepUsed >= 1uy && m.StepUsed <= 16uy && m.Slot < 48uy
        | CellType.Booster -> m.StepUsed >= 1uy && m.StepUsed <= 4uy
        | _ -> true

    /// Read authenticated meta. False on nonzero reserved bytes, reserved flags, an unknown
    /// type or out-of-range slot fields (receive pipeline step 6: drop and count proto_error).
    let tryRead (src: ReadOnlySpan<byte>) (m: byref<Meta>) =
        if src.[37] <> 0uy || src.[38] <> 0uy || src.[39] <> 0uy
           || src.[1] &&& reservedFlags <> 0uy || not (knownType src.[0]) then
            false
        else
            let v =
                { Type = LanguagePrimitives.EnumOfValue<byte, CellType> src.[0]
                  Flags = LanguagePrimitives.EnumOfValue<byte, MetaFlags> src.[1]
                  Slot = src.[2]
                  StepUsed = src.[3]
                  Aux = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 4)
                  RxWinGrant = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 6)
                  StepId = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 8)
                  AckStepLo = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 16)
                  CarrierWm = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 24)
                  KHist = uint32 src.[32] ||| (uint32 src.[33] <<< 8) ||| (uint32 src.[34] <<< 16)
                  ErasureQ16 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 35) }
            if shapeOk v then
                m <- v
                true
            else false
