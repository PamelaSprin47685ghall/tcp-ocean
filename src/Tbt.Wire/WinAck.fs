/// WIN_ACK symbol: acknowledgement, NACK state and path statistics (spec 6.10). Little endian.
namespace Tbt.Wire

open System
open System.Buffers.Binary

[<Struct>]
type PathStat =
    { PathId: uint16
      Flags: byte
      HighestCellSeqLow32: uint32
      CellsRx10ms: uint16
      Late10ms: uint16
      OwdDeltaUs: int32 }

[<Struct>]
type WinAckHead =
    { TLo: uint64
      Deficit: uint16
      Span: byte
      /// bit0 = request BOOSTER.
      NackFlags: byte
      ErasureMeanQ16: uint16
      ErasureP95Q16: uint16
      THi: uint64
      RxTimeUs: uint64
      PathCount: int
      /// Bit j: step t_lo + 1 + j decoded on its own (selective ack, 256 bits).
      DecodedBitmap0: uint64
      DecodedBitmap1: uint64
      DecodedBitmap2: uint64
      DecodedBitmap3: uint64
      CtrlAckHi: uint32
      CtrlAckBitmap: uint64 }

module WinAck =
    [<Literal>]
    let HeadLength = 96

    [<Literal>]
    let PathStatLength = 16

    let maxPaths (s: int) = (s - HeadLength) / PathStatLength

    let writeHead (h: WinAckHead) (dst: Span<byte>) =
        dst.Slice(0, HeadLength).Clear()
        BinaryPrimitives.WriteUInt64LittleEndian(dst, h.TLo)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 8, h.Deficit)
        dst.[10] <- h.Span
        dst.[11] <- h.NackFlags
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 12, h.ErasureMeanQ16)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 14, h.ErasureP95Q16)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 16, h.THi)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 24, h.RxTimeUs)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 32, uint16 h.PathCount)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 36, h.DecodedBitmap0)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 44, h.DecodedBitmap1)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 52, h.DecodedBitmap2)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 60, h.DecodedBitmap3)
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice 68, h.CtrlAckHi)
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice 72, h.CtrlAckBitmap)

    let writePathStat (i: int) (p: PathStat) (dst: Span<byte>) =
        let d = dst.Slice(HeadLength + i * PathStatLength, PathStatLength)
        BinaryPrimitives.WriteUInt16LittleEndian(d, p.PathId)
        d.[2] <- p.Flags
        d.[3] <- 0uy
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice 4, p.HighestCellSeqLow32)
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice 8, p.CellsRx10ms)
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice 10, p.Late10ms)
        BinaryPrimitives.WriteInt32LittleEndian(d.Slice 12, p.OwdDeltaUs)

    /// Read the head. False on nonzero reserved bytes, span outside 1..4 with a deficit,
    /// or a path count that does not fit the symbol.
    let tryReadHead (src: ReadOnlySpan<byte>) (h: byref<WinAckHead>) =
        let paths = int (BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 32))
        let span = src.[10]
        let deficit = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 8)
        if src.[34] <> 0uy || src.[35] <> 0uy || src.Slice(80, 16).IndexOfAnyExcept(0uy) >= 0
           || paths > maxPaths src.Length || (deficit <> 0us && (span < 1uy || span > 4uy)) then
            false
        else
            h <- { TLo = BinaryPrimitives.ReadUInt64LittleEndian src
                   Deficit = deficit
                   Span = span
                   NackFlags = src.[11]
                   ErasureMeanQ16 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 12)
                   ErasureP95Q16 = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 14)
                   THi = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 16)
                   RxTimeUs = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 24)
                   PathCount = paths
                   DecodedBitmap0 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 36)
                   DecodedBitmap1 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 44)
                   DecodedBitmap2 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 52)
                   DecodedBitmap3 = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 60)
                   CtrlAckHi = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice 68)
                   CtrlAckBitmap = BinaryPrimitives.ReadUInt64LittleEndian(src.Slice 72) }
            true

    let readPathStat (src: ReadOnlySpan<byte>) (i: int) =
        let d = src.Slice(HeadLength + i * PathStatLength, PathStatLength)
        { PathId = BinaryPrimitives.ReadUInt16LittleEndian d
          Flags = d.[2]
          HighestCellSeqLow32 = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice 4)
          CellsRx10ms = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice 8)
          Late10ms = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice 10)
          OwdDeltaUs = BinaryPrimitives.ReadInt32LittleEndian(d.Slice 12) }
