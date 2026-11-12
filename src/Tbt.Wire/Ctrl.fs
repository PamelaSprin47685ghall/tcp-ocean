/// CTRL symbol: one reliable control operation (spec 6.10a). Little endian.
namespace Tbt.Wire

open System
open System.Buffers.Binary

type CtrlOp =
    | KeyPrepare = 1us
    | KeyReady = 2us
    | KeyRetire = 3us
    | PathClose = 4us
    | PathClosed = 5us
    | CapacityChange = 6us
    | SessionClose = 7us
    | SessionClosed = 8us
    | MssNotice = 9us

[<Struct>]
type CtrlHead =
    { ControlSeq: uint32
      Op: CtrlOp
      BodyLength: int }

module Ctrl =
    [<Literal>]
    let HeadLength = 8

    /// Exact body length of each operation; -1 for an unknown operation.
    let bodyLength (op: CtrlOp) =
        match op with
        | CtrlOp.KeyPrepare -> 12
        | CtrlOp.KeyReady -> 4
        | CtrlOp.KeyRetire -> 4
        | CtrlOp.PathClose -> 11
        | CtrlOp.PathClosed -> 2
        | CtrlOp.CapacityChange -> 8
        | CtrlOp.SessionClose -> 1
        | CtrlOp.SessionClosed -> 0
        | CtrlOp.MssNotice -> 4
        | _ -> -1

    /// Write the head and zero the rest of the symbol; return the body span to fill.
    let write (controlSeq: uint32) (op: CtrlOp) (dst: Span<byte>) =
        let n = bodyLength op
        if n < 0 then invalidArg "op" "unknown operation"
        if controlSeq = 0u then invalidArg "controlSeq" "control_seq starts at 1"
        dst.Clear()
        BinaryPrimitives.WriteUInt32LittleEndian(dst, controlSeq)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 4, uint16 op)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 6, uint16 n)
        dst.Slice(HeadLength, n)

    /// Read the head. False on control_seq 0, an unknown operation, a body length that is
    /// not exact, or nonzero bytes after the body.
    let tryReadHead (src: ReadOnlySpan<byte>) (h: byref<CtrlHead>) =
        let seq = BinaryPrimitives.ReadUInt32LittleEndian src
        let op = LanguagePrimitives.EnumOfValue<uint16, CtrlOp>(BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 4))
        let n = int (BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 6))
        if seq = 0u || bodyLength op <> n || n > src.Length - HeadLength
           || src.Slice(HeadLength + n).IndexOfAnyExcept(0uy) >= 0 then
            false
        else
            h <- { ControlSeq = seq; Op = op; BodyLength = n }
            true
