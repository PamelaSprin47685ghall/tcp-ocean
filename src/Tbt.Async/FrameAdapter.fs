/// Frame adapter over the session's own receive pipeline (spec 6.6).
/// Authentication, framing and meta validation stay in RxHalf; this module only
/// names the authenticated result so a coordinator can classify and count frames.
namespace Tbt.Async

open System
open Tbt.Contracts
open Tbt.Session
open Tbt.Wire

/// What an authenticated frame carries.
type FrameKind =
    | Coded of kind: CellType * step: uint64 * slot: int * used: int
    | Ack
    | Heartbeat of step: uint64
    | Control
    | FlowState
    | Other of kind: CellType

type FrameInfo =
    { PathId: uint16
      Meta: Meta
      Kind: FrameKind }

module FrameAdapter =
    let kindOf (m: Meta) =
        match m.Type with
        | CellType.Data -> Coded(CellType.Data, m.StepId, int m.Slot, int m.StepUsed)
        | CellType.Parity -> Coded(CellType.Parity, m.StepId, int m.Slot, int m.StepUsed)
        | CellType.Booster -> Coded(CellType.Booster, m.StepId, int m.Slot, int m.StepUsed)
        | CellType.WinAck -> Ack
        | CellType.Heartbeat -> Heartbeat m.StepId
        | CellType.Ctrl -> Control
        | CellType.FlowState -> FlowState
        | other -> Other other

    /// Authenticate one whole frame and return its meta. None means the frame was
    /// dropped and counted by the receive half (framing, tag, or proto error).
    let tryParse (session: Session) (path: uint16) (frame: Span<byte>) : FrameInfo option =
        match session.Rx.Open(frame, path) with
        | ValueSome m -> Some { PathId = path; Meta = m; Kind = kindOf m }
        | ValueNone -> None

    /// Run the full receive pipeline: authenticate, dispatch by cell type, decode,
    /// deliver. Returns the authenticated carrier watermark and whether the frame
    /// authenticated; UInt64.MaxValue means it was dropped and counted.
    let dispatch (session: Session) (path: uint16) (frame: Span<byte>) : uint64 * bool =
        let wm = session.OnFrame(path, frame)
        (wm, wm <> System.UInt64.MaxValue)

    /// Drop counters of the session's receive half.
    let counters (session: Session) = session.Rx.Counters
