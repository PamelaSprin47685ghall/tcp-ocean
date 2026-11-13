namespace Tbt.Contracts

/// Wire direction byte (nonce byte 0, AAD). Values are the wire values.
type Direction =
    | Up = 1uy
    | Down = 2uy

/// meta.type wire values (spec 6.2).
type CellType =
    | Data = 1uy
    | Parity = 2uy
    | Booster = 3uy
    | WinAck = 4uy
    | Heartbeat = 5uy
    | Pad = 6uy
    | Ctrl = 8uy
    | FlowState = 9uy

/// Field of the bulk parity code in one direction: GF(2) XOR or GF(16).
type CodeField =
    | Gf2 = 2
    | Gf16 = 16

/// Monotonic clock in nanoseconds. All time comes from here (rule R6).
/// NowNs may be a cached sample; FreshNs always hits the inner source.
type IClock =
    abstract NowNs: unit -> int64
    abstract FreshNs: unit -> int64

/// One sample per event-loop Tick, plus a staleness bound. Hot-path
/// bookkeeping (RateGroup / TunPath Inflight scans) reads NowNs without
/// a clock_gettime each time. Seal, send, and repair deadlines must call
/// FreshNs so a long iteration cannot stamp times in the past or make a
/// just-sealed step look RepairWait-expired.
[<Sealed>]
type CachedClock(inner: IClock, ?staleMs: int64, ?refreshEvery: int) =
    let staleMs = defaultArg staleMs 1L
    let refreshEvery = defaultArg refreshEvery 32
    let mutable now = inner.NowNs()
    let mutable lastTc = System.Environment.TickCount64
    let mutable reads = 0
    let refresh () =
        now <- inner.NowNs()
        lastTc <- System.Environment.TickCount64
        reads <- 0
        now
    member _.Tick() = refresh()
    member _.FreshNs() = refresh()
    interface IClock with
        member _.NowNs() =
            reads <- reads + 1
            let tc = System.Environment.TickCount64
            if reads >= refreshEvery || tc - lastTc >= staleMs then refresh()
            else now
        member _.FreshNs() = refresh()

/// Entropy source. Production uses the OS CSPRNG; tests inject a seeded source (rule R6).
type IEntropy =
    abstract Fill: destination: System.Span<byte> -> unit
