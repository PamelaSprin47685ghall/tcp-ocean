/// The 16-byte source envelope at the start of every source symbol (spec 5.3).
/// Little endian: real_len u16, flow_slot u8, stream_flags u8, flow_gen u32, stream_offset u64.
namespace Tbt.Wire

open System
open System.Buffers.Binary
open Tbt.Contracts

[<Flags>]
type StreamFlags =
    | None = 0uy
    | Open = 1uy
    | Fin = 2uy
    | Rst = 4uy
    | Pad = 8uy

[<Struct>]
type SourceEnvelope =
    { RealLength: int
      FlowSlot: byte
      Flags: StreamFlags
      FlowGeneration: uint32
      StreamOffset: uint64 }

module SourceEnvelope =
    let private reservedFlags = 0xF0uy

    let write (e: SourceEnvelope) (dst: Span<byte>) =
        BinaryPrimitives.WriteUInt16LittleEndian(dst, uint16 e.RealLength)
        dst.[2] <- e.FlowSlot
        dst.[3] <- byte e.Flags
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice 4, e.FlowGeneration)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 8, e.StreamOffset)

    /// Read and check an envelope of a symbol of length s. False on a bad length,
    /// reserved flag bits, or an offset that overflows.
    let tryRead (symbol: ReadOnlySpan<byte>) (e: byref<SourceEnvelope>) =
        let len = int (BinaryPrimitives.ReadUInt16LittleEndian symbol)
        let flags = symbol.[3]
        let offset = BinaryPrimitives.ReadUInt64LittleEndian(symbol.Slice 8)
        if len > symbol.Length - Layout.EnvelopeLength || flags &&& reservedFlags <> 0uy
           || offset > UInt64.MaxValue - uint64 len then
            false
        else
            e <- { RealLength = len
                   FlowSlot = symbol.[2]
                   Flags = LanguagePrimitives.EnumOfValue<byte, StreamFlags> flags
                   FlowGeneration = BinaryPrimitives.ReadUInt32LittleEndian(symbol.Slice 4)
                   StreamOffset = offset }
            true
