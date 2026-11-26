namespace Tbt.Session

open System
open Tbt.Contracts
open Tbt.Wire

/// Contract of the sending half; implementations retain ownership of their state.
type ITxHalf =
    abstract Rho: float
    abstract CellLoss: float
    abstract QueueMemoryBytes: int64
    abstract PeerAck: uint64
    abstract GrantLimit: uint64
    abstract Head: uint64
    abstract Queued: int
    abstract UrgentQueued: int
    abstract UrgentFull: int64
    abstract UrgentSteps: int64
    abstract UrgentSources: int64
    abstract CellsSent: int64
    abstract SealedCells: int64
    abstract DroppedAcked: int64
    abstract QueueHigh: int
    abstract UniqueAcked: int64
    abstract UniqueArrived: int64
    abstract UniqueRendered: int64
    abstract CanFill: bool
    abstract CanFillUrgent: bool
    abstract CanFillClose: bool
    abstract NoteUrgentFull: unit -> unit
    abstract TryBeginSource: symbol: byref<Span<byte>> -> bool
    abstract TryBeginSource: symbol: byref<Span<byte>> * interactive: bool * ?forClose: bool -> bool
    abstract CommitSource: SourceEnvelope * bool -> uint64
    abstract Flush: unit -> unit
    abstract OnPeerAck: uint64 * uint16 -> unit
    abstract InitialGrant: uint64 -> unit
    abstract OnPeerErasure: q16: uint16 * ?q95: uint16 -> unit
    abstract SrttNs: float
    abstract MinRttNs: int64
    abstract RttSkipped: int64
    abstract RepairRepeats: int64
    abstract RepairWaitNs: float
    abstract NewRepair: bool with get, set
    abstract EvidenceRepair: bool with get, set
    abstract ReoWndNs: int64
    abstract PeerSeenHi: uint64
    abstract BoostersSent: int64
    abstract SpuriousRepairs: int64
    abstract OnPeerSeen: uint64 -> unit
    abstract OnPeerDecodedBitmap: uint64 * uint64 * uint64 * uint64 * uint64 -> unit
    abstract OnNack: tLo: uint64 * span: int * deficit: int * missingMasks: uint64 -> unit
    abstract HeartbeatDue: bool
    abstract NextHeartbeatNs: int64
    abstract RenderHeartbeat: Span<byte> * uint16 * uint64 * uint16 * uint16 -> unit
    abstract Prepare: unit -> unit
    abstract RepairWaiting: bool
    abstract HasCandidate: bool
    abstract Seal: Span<byte> * uint16 -> unit
    abstract NextCounter: uint64
    abstract LastOnPath: uint16 -> uint64
    abstract SetCutLimit: uint64 * uint64 -> unit
    abstract ClearCutLimit: unit -> unit
    abstract CanSeal: bool
    abstract CanSealPayload: bool
    abstract TryRenderNext: frame: Span<byte> * path: uint16 * ackLo: uint64 * grant: uint16 * erasureQ16: uint16 * ?allowRepair: bool -> bool
    abstract RenderFill: Span<byte> * uint16 * uint64 * uint16 * uint16 -> CellType
    abstract FillsSent: int64
    abstract CarrierWm: uint16 -> uint64

/// Receive counters (spec 6.6 failure targets).
[<Sealed>]
type RxCounters() =
    member val Cells = 0L with get, set
    member val FramingErrors = 0L with get, set
    member val ProtoErrors = 0L with get, set
    member val EnvelopeErrors = 0L with get, set
    member val Recovered = 0L with get, set
    member val Unsupported = 0L with get, set
    /// Parities given to the decoder, and parities dropped unused (spec 5.9 note in ParityHold).
    member val ParitiesUsed = 0L with get, set
    member val ParitiesUnused = 0L with get, set
    /// Cumulative erasure evidence for this receive direction, split by what proved it.
    ///
    /// `ErasureQ16` says how much was erased; these say why, which is the difference between
    /// a link that loses cells and a link that only delivers them out of order. A source the
    /// decoder had to rebuild is certain proof the cell did not arrive; a source marked late
    /// by the step clock alone is weaker, because normal multi-path queueing also puts a cell
    /// after its step's deadline. Splitting the two turns "reported loss is 0.24 on a lossless
    /// link" from an unattributable number into a measurement.
    ///
    /// All three are session-cumulative. The rate the controller reads is the 10 ms window in
    /// `ErasureQ16`/`ErasureP95Q16`, not a ratio of these; compare their deltas between two
    /// stats lines to see what the recent erasures were made of.
    member val TimelyDue = 0L with get, set
    member val ErasureRecovered = 0L with get, set
    member val ErasureLateClock = 0L with get, set
    /// Rebuilds withdrawn because the source's own DATA cell arrived inside the deadline.
    /// A rebuild means the decoder solved the slot by elimination before the cell showed up;
    /// the spec's erasure is "not delivered and authenticated before the deadline", so an
    /// arrival inside the deadline makes that rebuild a latency artefact, not an erasure.
    member val ErasureCancelled = 0L with get, set
    /// Sources booked into the 10 ms windows by `settlePendingSlot` — i.e. the deadline
    /// settlements of PENDING REBUILDS only.
    ///
    /// This is deliberately NOT the whole window. A source that arrives directly is booked by
    /// `observeTimely` (one accounted source, erased or not) and never passes through
    /// `settlePendingSlot`, so `Missing/Due` here is a strict subset of what `winDue`/
    /// `winErased` contain. Do not present it as the window's own total, and do not divide it
    /// and call the result the reported erasure rate: `ErasureQ16`/`ErasureP95Q16` are the
    /// window's answer, this pair only says how the rebuild part settled.
    ///
    /// `due` counts every accounted source of the settled step (missing + vindicated), `missing`
    /// only those whose cell never arrived by the deadline. `ErasureRecovered` above counts
    /// reports, not outcomes: a rebuild is reported before its deadline decides, and
    /// `ErasureCancelled` subtracts the ones a timely arrival vindicated.
    member val RebuildSettledDue = 0L with get, set
    member val RebuildSettledMissing = 0L with get, set

/// Backend-neutral receive operations and diagnostics.
type IRxHalf =
    abstract Counters: RxCounters
    abstract AckStepLo: uint64
    abstract Grant: uint16
    abstract ErasureQ16: uint16
    abstract ErasureP95Q16: uint16
    abstract P95Candidates: int
    abstract P95SmallWindows: int
    abstract P95PickDue: int
    abstract P95PickErased: int
    abstract EstimateFloor: uint64
    abstract EstimateFolds: int64
    abstract LastFoldedStep: uint64
    abstract LastFoldedExpected: int
    abstract LastFoldedReceived: int
    abstract PendingRows: int
    abstract FreeRows: int
    abstract RowsAllocated: int
    abstract StaleRows: int64
    abstract BaseStep: uint64
    abstract KConflicts: int64
    abstract SeenEnd: uint64
    abstract IsStepComplete: uint64 -> bool
    abstract Attach: FlowTable -> unit
    abstract FillDecodedBitmap: unit -> struct (uint64 * uint64 * uint64 * uint64)
    abstract MissingBitmaps: uint64 -> uint64
    abstract StepUndelivered: uint64 * int -> unit
    abstract Release: unit -> unit
    abstract Open: Span<byte> * uint16 -> Meta voption
    abstract OpenPre: Span<byte> * uint64 -> Meta voption
    abstract OnCoded: Meta * ReadOnlySpan<byte> * uint16 * int64 -> unit
    abstract Service: int64 -> unit
    abstract OnHeartbeat: uint64 -> unit
    abstract TryNack: int64 * byref<int> * byref<int> -> bool

