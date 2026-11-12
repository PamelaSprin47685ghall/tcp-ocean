/// The carrier interface of one path (spec 3.11): kernel TCP (PathLink) or the server's
/// user-space TCP on TUN (TunPath). The session carrier sees only this.
namespace Tbt.Carrier

open System

/// The side of a path that owns the session.
type IPathOwner =
    /// One whole received frame of path.
    abstract OnFrame: path: uint16 * frame: Span<byte> -> unit
    /// Content for a whole-frame hole of path (borrowed shell); renders into frame.
    abstract ProduceFill: path: uint16 * frame: Span<byte> -> unit

type IPath =
    abstract PathId: uint16
    /// Whole frame length of the session.
    abstract FrameLength: int with get, set
    abstract Alive: bool
    abstract Attach: owner: IPathOwner -> unit
    /// Register descriptors for the next wait (kernel paths).
    abstract Watch: poller: Poller * sending: bool -> unit
    /// Take what the last wait reported; whole frames go to the owner.
    abstract Receive: poller: Poller -> unit
    /// The frame area of the next send, when the path takes one more frame now.
    abstract TryBeginFrame: frame: byref<Span<byte>> -> bool
    /// Send the frame begun last.
    abstract CommitFrame: unit -> unit
    abstract Close: unit -> unit
