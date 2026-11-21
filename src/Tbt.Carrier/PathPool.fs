/// Client path-pool bookkeeping: which JOIN ids are missing, when to retry
/// them, and the next incarnation of each id.
///
/// The type holds no socket and starts no thread. The session thread owns it
/// and calls every member. The pool only answers "start these ids now"; the
/// caller runs the JOIN and reports the result back.
namespace Tbt.Carrier

open System
open System.Collections.Generic

[<Sealed>]
type PathPool(target: int, concurrency: int, baseDelayNs: int64, capDelayNs: int64, incStride: int) =
    let target = max 1 target
    let slots = target
    let cap = max 1 (min 64 concurrency)
    let stride = max 1 incStride
    let inFlight = HashSet<uint16>()
    /// Last incarnation handed out per id. 0 means "never used".
    let incarnation = Array.zeroCreate<uint32> slots
    let attempts = Array.zeroCreate<int> slots
    let nextAt = Array.zeroCreate<int64> slots
    let mutable saturated = false

    /// Path 0 comes from the handshake and the pool rebuilds it like any other
    /// id once it is lost. The caller hides id 0 until the handshake path has
    /// been attached, so a rebuild cannot race the initial PathAdd.
    member _.Target = target
    member _.InFlight = inFlight.Count
    member _.Saturated = saturated

    /// Ids to start now, at most the free in-flight slots. A returned
    /// incarnation starts a reserved stride, so retries inside one JOIN keep
    /// increasing and a later rebuild never reuses a value.
    member _.Request(nowNs: int64, present: HashSet<uint16>) : struct (uint16 * uint32)[] =
        let start = ResizeArray<struct (uint16 * uint32)>()
        let mutable id = 0
        while id < slots && id <= 65535 && inFlight.Count < cap do
            let key = uint16 id
            if not (present.Contains key) && not (inFlight.Contains key) && nowNs >= nextAt.[id] then
                if incarnation.[id] > UInt32.MaxValue - uint32 stride then
                    // Never reuse an incarnation: stop rebuilding this id.
                    saturated <- true
                    nextAt.[id] <- nowNs + max 1L capDelayNs
                else
                    incarnation.[id] <- incarnation.[id] + uint32 stride
                    inFlight.Add key |> ignore
                    start.Add(struct (key, incarnation.[id]))
            id <- id + 1
        start.ToArray()

    /// Reserve the next incarnation stride of a present id for a refresh JOIN (the
    /// server replaces a live path on a higher incarnation). None when the id is
    /// already joining or its incarnations are used up.
    member _.Reserve(id: uint16) : uint32 voption =
        let i = int id
        if i >= slots || inFlight.Contains id || incarnation.[i] > UInt32.MaxValue - uint32 stride then ValueNone
        else
            incarnation.[i] <- incarnation.[i] + uint32 stride
            inFlight.Add id |> ignore
            ValueSome incarnation.[i]

    /// A JOIN succeeded: clear the backoff of this id.
    member _.Joined(id: uint16) =
        inFlight.Remove id |> ignore
        let i = int id
        if i < slots then
            attempts.[i] <- 0
            nextAt.[i] <- 0L

    /// A JOIN gave up: wait before the next rebuild of this id.
    member _.Failed(id: uint16, nowNs: int64) =
        inFlight.Remove id |> ignore
        let i = int id
        if i < slots then
            let a = min 8 (attempts.[i] + 1)
            attempts.[i] <- a
            let shift = min 8 (a - 1)
            let delay = min (max 1L capDelayNs) ((max 1L baseDelayNs) <<< shift)
            nextAt.[i] <- nowNs + delay
