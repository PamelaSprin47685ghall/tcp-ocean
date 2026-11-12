/// The carrier interface of one path (spec 3.11): kernel TCP (PathLink) or the server's
/// user-space TCP on TUN (TunPath). The session carrier sees only this.
namespace Tbt.Carrier

open System

/// The side of a path that owns the session.
type IPathOwner =
    /// One whole received frame of path. Returns its authenticated carrier_wm, or
    /// UInt64.MaxValue when it did not authenticate.
    abstract OnFrame: path: uint16 * frame: Span<byte> -> uint64
    /// Decode frontier t_lo of the receive half (hole swallow fast path, spec 6.8.2).
    abstract Frontier: uint64
    /// Content for a whole-frame hole of path (borrowed shell); renders into frame.
    abstract ProduceFill: path: uint16 * frame: Span<byte> -> unit
    /// An exception escaped while this path worked for the owner: the owner fails.
    abstract Fail: reason: string -> unit

type IPath =
    abstract PathId: uint16 with get, set
    /// Whole frame length of the session.
    abstract FrameLength: int with get, set
    abstract Alive: bool
    abstract Attach: owner: IPathOwner -> unit
    /// Cells the session holds for sending, before a send round (rate probing).
    abstract Backlog: int with set
    /// Register descriptors for the next wait (kernel paths).
    abstract Watch: poller: Poller * sending: bool -> unit
    /// Take what the last wait reported; whole frames go to the owner.
    abstract Receive: poller: Poller -> unit
    /// The frame area of the next send, when the path takes one more frame now.
    abstract TryBeginFrame: frame: byref<Span<byte>> -> bool
    /// Send the frame begun last.
    abstract CommitFrame: unit -> unit
    abstract Close: unit -> unit
