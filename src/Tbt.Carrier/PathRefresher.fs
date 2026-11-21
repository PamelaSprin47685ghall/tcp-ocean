/// Rolling refresh of client carrier paths.
///
/// Long-lived carrier TCP connections lose throughput: on the live CN-JP link a
/// session's download fell 26 -> 5 Mbit/s over a few minutes of transfers while a
/// fresh session beside it kept 20+, and reconnecting the old session's sockets
/// (same session, new TCP connections) restored it at once. The state that decays is
/// per TCP connection (the server's BBR sockets leave STARTUP for a ProbeBW whose
/// bandwidth estimate, taken under ~20 % loss, keeps shrinking), so the cure is to
/// keep every connection young: re-JOIN the oldest path ids with a higher
/// incarnation, a few at a time. The server replaces the path on accept (spec
/// 6.5.7.4 rule 6), so the new connection is up before the old one is closed, and
/// the session's repair recovers any cell that was in flight on the old one.
///
/// Like PathPool this type holds no socket and starts no thread: the session thread
/// owns it, asks which ids to refresh, runs the JOINs, and reports results back.
namespace Tbt.Carrier

open System.Collections.Generic

[<Sealed>]
type PathRefresher(maxAgeNs: int64, perTick: int, tickNs: int64, maxInFlight: int, retryNs: int64) =
    let joinedAt = Dictionary<uint16, int64>()
    let inFlight = HashSet<uint16>()
    let mutable lastTickNs = 0L
    let mutable started = 0L
    let mutable replaced = 0L
    let mutable failed = 0L

    /// Off when maxAgeNs <= 0 or perTick <= 0.
    member _.Enabled = maxAgeNs > 0L && perTick > 0
    member _.InFlight = inFlight.Count
    member _.StartedCount = started
    member _.ReplacedCount = replaced
    member _.FailedCount = failed

    /// A path id got a new connection (first JOIN, rebuild or refresh).
    member _.NoteJoined(id: uint16, nowNs: int64) =
        inFlight.Remove id |> ignore
        joinedAt.[id] <- nowNs

    /// The id has no connection any more (the pool rebuilds it, which NoteJoined records).
    member _.NoteLost(id: uint16) =
        joinedAt.Remove id |> ignore

    /// Ids to re-JOIN now: present, not already refreshing, older than maxAge, oldest
    /// first; at most perTick per tick and maxInFlight at once. `ready` is false until
    /// the pool is complete, so a refresh never competes with building the pool.
    member this.Due(nowNs: int64, present: IEnumerable<uint16>, ready: bool) : uint16[] =
        if not this.Enabled || not ready || nowNs - lastTickNs < tickNs then Array.empty
        else
            lastTickNs <- nowNs
            let room = min perTick (maxInFlight - inFlight.Count)
            if room <= 0 then Array.empty
            else
                let cands = ResizeArray<struct (int64 * uint16)>()
                for id in present do
                    if not (inFlight.Contains id) then
                        match joinedAt.TryGetValue id with
                        | true, at when nowNs - at >= maxAgeNs -> cands.Add(struct (at, id))
                        | true, _ -> ()
                        | _ -> joinedAt.[id] <- nowNs // first sight: age from now
                cands.Sort(fun (struct (a, x)) (struct (b, y)) -> if a <> b then compare a b else compare x y)
                let n = min room cands.Count
                let out = Array.init n (fun i -> let struct (_, id) = cands.[i] in id)
                for id in out do inFlight.Add id |> ignore
                started <- started + int64 n
                out

    /// The refresh JOIN for id succeeded and the new connection replaces the old.
    member this.OnReplaced(id: uint16, nowNs: int64) =
        replaced <- replaced + 1L
        this.NoteJoined(id, nowNs)

    /// The refresh JOIN failed; the old connection stays. Try this id again after retryNs.
    member _.OnFailed(id: uint16, nowNs: int64) =
        failed <- failed + 1L
        inFlight.Remove id |> ignore
        joinedAt.[id] <- nowNs - maxAgeNs + retryNs
