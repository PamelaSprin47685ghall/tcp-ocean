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
type IClock =
    abstract NowNs: unit -> int64

/// Entropy source. Production uses the OS CSPRNG; tests inject a seeded source (rule R6).
type IEntropy =
    abstract Fill: destination: System.Span<byte> -> unit
