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
type PathRefresher(maxAgeNs: int64, perTick: int, perTickIdle: int, tickNs: int64, maxInFlight: int, retryNs: int64) =
    let joinedAt = Dictionary<uint16, int64>()
    /// When each id is due for refresh: joinedAt + maxAge scaled by a per-join jitter.
    let dueAt = Dictionary<uint16, int64>()
    let mutable jitter = 0.0
    let mutable rng = System.Random(0x7b7)
    let ageFor () =
        if jitter <= 0.0 then maxAgeNs
        else int64 (float maxAgeNs * (1.0 - jitter + 2.0 * jitter * rng.NextDouble()))
    let inFlight = HashSet<uint16>()
    let mutable lastTickNs = 0L
    let mutable started = 0L
    let mutable replaced = 0L
    let mutable failed = 0L

    /// Off when maxAgeNs <= 0 or perTick <= 0.
    member _.Enabled = maxAgeNs > 0L && (perTick > 0 || perTickIdle > 0)
    member _.InFlight = inFlight.Count
    member _.StartedCount = started
    member _.ReplacedCount = replaced
    member _.FailedCount = failed

    /// Spread of the refresh age, 0..0.9: each join is refreshed after maxAge * U(1-j, 1+j).
    /// Without it a new session's 256 paths all reach maxAge in the same second and
    /// the refresh runs as one wave (bench: refresh-only sessions were slowest in
    /// their first minutes). Default 0 keeps the fixed age.
    member _.Jitter
        with get () = jitter
        and set (v: float) = jitter <- max 0.0 (min 0.9 v)
    member _.Seed(seed: int) = rng <- System.Random(seed)

    /// A path id got a new connection (first JOIN, rebuild or refresh).
    member _.NoteJoined(id: uint16, nowNs: int64) =
        inFlight.Remove id |> ignore
        joinedAt.[id] <- nowNs
        dueAt.[id] <- nowNs + ageFor ()

    /// The id has no connection any more (the pool rebuilds it, which NoteJoined records).
    member _.NoteLost(id: uint16) =
        joinedAt.Remove id |> ignore
        dueAt.Remove id |> ignore

    /// Ids to re-JOIN now: present, not already refreshing, older than maxAge, oldest
    /// first; at most perTick per tick (perTickIdle while the session is idle) and
    /// maxInFlight at once. `ready` is false until the pool is complete, so a refresh
    /// never competes with building the pool. Bench: refreshing at full rate during a
    /// bulk transfer cost repairs (JOIN replies queue behind bulk for 6-11 s and the
    /// server closes the old connection at accept), so most of the work runs idle.
    member this.Due(nowNs: int64, present: IEnumerable<uint16>, ready: bool, idle: bool) : uint16[] =
        if not this.Enabled || not ready || nowNs - lastTickNs < tickNs then Array.empty
        else
            lastTickNs <- nowNs
            let room = min (if idle then max perTick perTickIdle else perTick) (maxInFlight - inFlight.Count)
            if room <= 0 then Array.empty
            else
                let cands = ResizeArray<struct (int64 * uint16)>()
                for id in present do
                    if not (inFlight.Contains id) then
                        match dueAt.TryGetValue id with
                        | true, due when nowNs >= due -> cands.Add(struct (due, id))
                        | true, _ -> ()
                        | _ -> // first sight: age from now
                            joinedAt.[id] <- nowNs
                            dueAt.[id] <- nowNs + ageFor ()
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
        dueAt.[id] <- nowNs + retryNs

/// Client exit rule for an empty path list. The handshake session may be dead
/// (exit so the service manager starts a fresh one), or paths are still on their
/// way: the initial JOINs right after the handshake, or rejoins after a mass reset
/// (the server keeps the session --no-path-grace-s). Exiting while JOINs are in
/// flight threw away a live session 3 s after "session up" (bench fr13 ERRs).
/// Exit after exitNs with no path and no JOIN in flight, or after capNs with no path.
[<Sealed>]
type NoPathWatch(exitNs: int64, capNs: int64) =
    let mutable watching = false
    let mutable since = 0L
    let mutable lastBusy = 0L
    /// Call on every housekeeping pass; true when the client should exit.
    member _.ShouldExit(nowNs: int64, noPath: bool, joinsInFlight: int) =
        if not noPath then
            watching <- false
            false
        else
            if not watching then
                watching <- true
                since <- nowNs
                lastBusy <- nowNs
            if joinsInFlight > 0 then lastBusy <- nowNs
            nowNs - lastBusy >= exitNs || (capNs > 0L && nowNs - since >= capNs)
    /// True while the list is empty (the caller keeps a timer wake running).
    member _.Watching = watching

/// Rolling congestion-control re-init plan for carrier sockets (no reconnect):
/// toggling TCP_CONGESTION re-runs the module's init, so BBR restarts in STARTUP.
/// Each socket is nudged at most once per periodNs, spread evenly over ticks;
/// pick "oldest" (longest since nudge/first seen) or "worst" (lowest score first).
[<Sealed>]
type CcNudgePlan<'k when 'k: not struct and 'k: not null and 'k: equality>(periodNs: int64, tickNs: int64, worst: bool) =
    let last = System.Collections.Generic.Dictionary<'k, int64>(HashIdentity.Reference)
    let mutable lastTick = 0L
    let mutable nudged = 0L
    member _.Enabled = periodNs > 0L
    member _.Nudged = nudged
    member _.Due(nowNs: int64, keys: 'k[], score: 'k -> int64) : 'k[] =
        if periodNs <= 0L || nowNs - lastTick < tickNs then Array.empty
        else
            lastTick <- nowNs
            let present = System.Collections.Generic.HashSet<'k>(keys, HashIdentity.Reference)
            for k in Seq.toArray last.Keys do
                if not (present.Contains k) then last.Remove k |> ignore
            for k in keys do
                if not (last.ContainsKey k) then last.[k] <- nowNs
            let per = int ((int64 keys.Length * tickNs + periodNs - 1L) / periodNs)
            let eligible = keys |> Array.filter (fun k -> nowNs - last.[k] >= periodNs)
            let ordered =
                if worst then eligible |> Array.sortBy (fun k -> score k, last.[k])
                else eligible |> Array.sortBy (fun k -> last.[k])
            let pick = Array.truncate per ordered
            for k in pick do last.[k] <- nowNs
            nudged <- nudged + int64 pick.Length
            pick
