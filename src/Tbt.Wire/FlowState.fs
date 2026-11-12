/// FLOW_STATE format 2 page (spec 6.10b, caps bit8 + bit9). Little endian.
/// The 20-byte head is followed by 20-byte records; the rest of the symbol is zero.
namespace Tbt.Wire

open System
open System.Buffers.Binary

type FlowStateKind =
    | Flow = 1uy
    | Session = 2uy

type FlowLifecycle =
    | OpenPending = 0uy
    | Open = 1uy
    | HalfClosedLocal = 2uy
    | HalfClosedRemote = 3uy
    | Closed = 4uy
    | Reset = 5uy

[<Struct>]
type FlowStateHead =
    { Snapshot: bool
      LastPage: bool
      RecordCount: int
      TotalFlows: int
      PageIndex: int
      StateSeq: uint32
      StateEpoch: uint32
      SnapshotId: uint32
      /// True when every record kind is known (needed to prove a snapshot complete).
      AllKindsKnown: bool }

[<Struct>]
type FlowRecord =
    { State: FlowLifecycle
      FlowSlot: int
      FlowGeneration: uint32
      DeliveredOffset: uint64
      CreditBytes: uint32 }

[<Struct>]
type SessionRecord =
    { InteractiveActive: bool
      InteractiveSourceCredit: uint16
      MicroCredit: uint16
      PoolFreeKiB: uint32 }

module FlowState =
    [<Literal>]
    let FormatVersion = 2uy

    [<Literal>]
    let HeadLength = 20

    [<Literal>]
    let RecordLength = 20

    let maxRecords (s: int) = (s - HeadLength) / RecordLength

    let private recordAt (i: int) = HeadLength + i * RecordLength

    let writeHead (h: FlowStateHead) (dst: Span<byte>) =
        dst.Clear()
        dst.[0] <- FormatVersion
        dst.[1] <- (if h.Snapshot then 1uy else 0uy) ||| (if h.LastPage then 2uy else 0uy)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 2, uint16 h.RecordCount)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 4, uint16 h.TotalFlows)
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice 6, uint16 h.PageIndex)
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice 8, h.StateSeq)
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice 12, h.StateEpoch)
        BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice 16, h.SnapshotId)

    let writeFlow (i: int) (r: FlowRecord) (dst: Span<byte>) =
        let d = dst.Slice(recordAt i, RecordLength)
        d.[0] <- byte FlowStateKind.Flow
        d.[1] <- byte r.State
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice 2, uint16 r.FlowSlot)
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice 4, r.FlowGeneration)
        BinaryPrimitives.WriteUInt64LittleEndian(d.Slice 8, r.DeliveredOffset)
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice 16, r.CreditBytes)

    let writeSession (i: int) (r: SessionRecord) (dst: Span<byte>) =
        let d = dst.Slice(recordAt i, RecordLength)
        d.Clear()
        d.[0] <- byte FlowStateKind.Session
        d.[1] <- if r.InteractiveActive then 1uy else 0uy
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice 2, r.InteractiveSourceCredit)
        BinaryPrimitives.WriteUInt16LittleEndian(d.Slice 4, r.MicroCredit)
        BinaryPrimitives.WriteUInt32LittleEndian(d.Slice 8, r.PoolFreeKiB)

    let kindAt (src: ReadOnlySpan<byte>) (i: int) = src.[recordAt i]

    let readFlow (src: ReadOnlySpan<byte>) (i: int) =
        let d = src.Slice(recordAt i, RecordLength)
        { State = LanguagePrimitives.EnumOfValue<byte, FlowLifecycle> d.[1]
          FlowSlot = int (BinaryPrimitives.ReadUInt16LittleEndian(d.Slice 2))
          FlowGeneration = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice 4)
          DeliveredOffset = BinaryPrimitives.ReadUInt64LittleEndian(d.Slice 8)
          CreditBytes = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice 16) }

    let readSession (src: ReadOnlySpan<byte>) (i: int) =
        let d = src.Slice(recordAt i, RecordLength)
        { InteractiveActive = d.[1] &&& 1uy <> 0uy
          InteractiveSourceCredit = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice 2)
          MicroCredit = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice 4)
          PoolFreeKiB = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice 8) }

    let private flowOk (d: ReadOnlySpan<byte>) =
        let slot = BinaryPrimitives.ReadUInt16LittleEndian(d.Slice 2)
        let delivered = BinaryPrimitives.ReadUInt64LittleEndian(d.Slice 8)
        let credit = uint64 (BinaryPrimitives.ReadUInt32LittleEndian(d.Slice 16))
        d.[1] <= 5uy && slot <= 255us && delivered <= UInt64.MaxValue - credit

    let private sessionOk (d: ReadOnlySpan<byte>) =
        d.[1] &&& 0xFEuy = 0uy && d.[6] = 0uy && d.[7] = 0uy && d.Slice(12, 8).IndexOfAnyExcept(0uy) < 0

    /// Validate a whole page (spec 6.10b.2, rule: reject the whole report, never half of it).
    /// False on a wrong version or flags, a count that does not fit, snapshot fields that do
    /// not match the page kind, a bad record, a repeated FLOW key, more than one SESSION
    /// record, or nonzero bytes after the records.
    let tryReadHead (src: ReadOnlySpan<byte>) (h: byref<FlowStateHead>) =
        let flags = src.[1]
        let n = int (BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 2))
        let total = int (BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 4))
        let page = int (BinaryPrimitives.ReadUInt16LittleEndian(src.Slice 6))
        let seq = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice 8)
        let epoch = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice 12)
        let sid = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice 16)
        let snapshot = flags &&& 1uy <> 0uy
        let headOk =
            src.[0] = FormatVersion && flags &&& 0xFCuy = 0uy && n <= maxRecords src.Length
            && seq <> 0u && total <= 256
            && (if snapshot then sid = seq else total = 0 && page = 0 && sid = 0u)
            && src.Slice(recordAt n).IndexOfAnyExcept(0uy) < 0
        if not headOk then false
        else
            let mutable ok = true
            let mutable sessions = 0
            let mutable allKnown = true
            let mutable i = 0
            while ok && i < n do
                let d = src.Slice(recordAt i, RecordLength)
                match LanguagePrimitives.EnumOfValue<byte, FlowStateKind> d.[0] with
                | FlowStateKind.Flow ->
                    if not (flowOk d) then ok <- false
                    else
                        // A FLOW key is (slot, generation); a page must not repeat it.
                        let key = BinaryPrimitives.ReadUInt64LittleEndian(d.Slice 0) >>> 16
                        let mutable j = 0
                        while ok && j < i do
                            let e = src.Slice(recordAt j, RecordLength)
                            if e.[0] = byte FlowStateKind.Flow
                               && BinaryPrimitives.ReadUInt64LittleEndian(e.Slice 0) >>> 16 = key then
                                ok <- false
                            j <- j + 1
                | FlowStateKind.Session ->
                    sessions <- sessions + 1
                    if sessions > 1 || not (sessionOk d) then ok <- false
                | _ -> allKnown <- false
                i <- i + 1
            if ok then
                h <- { Snapshot = snapshot
                       LastPage = flags &&& 2uy <> 0uy
                       RecordCount = n
                       TotalFlows = total
                       PageIndex = page
                       StateSeq = seq
                       StateEpoch = epoch
                       SnapshotId = sid
                       AllKindsKnown = allKnown }
            ok
