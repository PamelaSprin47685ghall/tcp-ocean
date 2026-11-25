/// Drives one session over its TCP paths and its local flows (spec 6.6 receive, 6.8
/// write gating): whole frames in, one frame per path per round out.
namespace Tbt.Carrier

open System
open System.Collections.Generic
open System.Collections.Concurrent
open Tbt.Contracts
open Tbt.Session

/// One session-level member of the client uplink RateGroup. Delivery for rate
/// samples is arrival evidence (WIN_ACK t_hi / UniqueArrived), not only
/// cumulative peer_ack: a later-seen step means the bottleneck already carried
/// those cells even if a hole still sits at the frontier. Inflight stays
/// custody-based so a hole cannot shrink the 1.25 BDP cap. Not kernel TCP ACK:
/// the kernel can ACK while the server decoder lags.
type private SessionRateMember(session: Session, frameOf: unit -> int, uniqueOut: unit -> int64, wireOut: unit -> int64) =
    let mutable lastAck = 0UL
    let mutable lastSeen = 0UL
    let mutable lastNs = 0L
    interface IRateMember with
        // Unique payload still awaiting custody, not every BOOSTER copy: counting copies
        // against cwnd left the group unable to send after a repair burst.
        member _.Inflight =
            let fl = int64 (frameOf())
            int (max 0L (uniqueOut() - session.Tx.UniqueAcked * fl))
        member _.Delivered = session.Tx.UniqueArrived * int64 (frameOf())
        member _.CopyEstimate = max 0L (wireOut() - uniqueOut())
        // RateGroup.Service will not take a bw sample while minRTT is unknown.
        // Karn can skip every seal-to-custody sample when BOOSTERs start before
        // the first ack (8-path rtt400 sat at the 64-frame startup cap for 40 s
        // with pace_bw=0). Fall back to SRTT so the group can still estimate.
        member _.MinRttNs =
            let m = session.Tx.MinRttNs
            if m <> System.Int64.MaxValue then m else max 1L (int64 session.Tx.SrttNs)
        member _.LastDeliveredNs =
            if session.Tx.PeerAck > lastAck || session.Tx.PeerSeenHi > lastSeen then
                lastAck <- session.Tx.PeerAck
                lastSeen <- session.Tx.PeerSeenHi
                lastNs <- session.Clock.NowNs()
            lastNs

/// The live path set, published as immutable snapshots.
///
/// A `List<IPath>` cannot be shared here. `AsyncSession.Stop` does `t.Join 5000 |> ignore`
/// and then calls `Close`, so a slow loop lets `Close` run on the Stop thread while the
/// owning thread is inside `Watch`/`writeBatch`/`prune`; and with no async IO group the
/// carrier is driven inline, so `AddPath` runs on the accept thread. `MemoryBarrier` orders
/// memory operations — it does not stop one thread from mutating a `List` that another is
/// enumerating or indexing, which is undefined behaviour (`InvalidOperationException`, null
/// slots, lost entries).
///
/// So mutations rebuild the array, publish it with a release store, and readers take one
/// acquire load: an index never shifts under a reader, an enumeration cannot be invalidated,
/// and no entry is ever seen half-written. The mutations are serialised by `gate` so a
/// concurrent `Add` cannot lose a neighbour. All of them are rare (JOIN, replacement, prune,
/// `Close`), so the copy costs nothing on the frame path.
[<Sealed>]
type PathSet() =
    let gate = obj ()
    let mutable items: IPath[] = Array.Empty<IPath>()
    let snapshot () = System.Threading.Volatile.Read(&items)

    /// Bumped inside `gate` by every mutation, so it orders with the published array.
    ///
    /// It has to live here, not in the session: a reader that takes the array and then reads a
    /// version kept elsewhere can pair a pre-change array with a post-change version, and then
    /// conclude "nothing changed" while holding a snapshot that points at a removed, closed path.
    let mutable version = 0L

    let publish (next: IPath[]) =
        System.Threading.Volatile.Write(&items, next)
        version <- version + 1L

    /// Structural change counter. Equal to the version of any snapshot taken at the same moment.
    member _.Version = System.Threading.Volatile.Read(&version)

    /// One array and the version it belongs to, from a single critical section.
    ///
    /// This is the pair a consumer needs: snapshot taken at `v`, and any later mutation produces
    /// `Version > v`, so comparing the two answers "has anything changed since this snapshot?"
    /// without a window in which the answer can be wrong.
    member _.SnapshotWithVersion() : IPath[] * int64 =
        lock gate (fun () -> (snapshot (), version))

    member _.Count = (snapshot ()).Length
    member _.Item with get (i: int) = (snapshot ()).[i]
    member _.ToArray() = snapshot ()

    member _.Add(p: IPath) =
        lock gate (fun () ->
            let cur = snapshot ()
            let next = Array.zeroCreate<IPath>(cur.Length + 1)
            Array.blit cur 0 next 0 cur.Length
            next.[cur.Length] <- p
            publish next)

    member _.RemoveAt(i: int) =
        lock gate (fun () ->
            let cur = snapshot ()
            if i >= 0 && i < cur.Length then
                let next = Array.zeroCreate<IPath>(cur.Length - 1)
                Array.blit cur 0 next 0 i
                Array.blit cur (i + 1) next i (cur.Length - i - 1)
                publish next)

    member _.FindIndex(pred: IPath -> bool) =
        let cur = snapshot ()
        let mutable i = 0
        let mutable found = -1
        while found < 0 && i < cur.Length do
            if pred cur.[i] then found <- i
            i <- i + 1
        found

    member _.Clear() =
        lock gate (fun () -> publish (Array.Empty<IPath>()))

    /// Remove the first entry matching `pred` and hand it to the caller, atomically.
    ///
    /// This is the only safe way to "close path X on this thread": `FindIndex` then `.[i]` then
    /// `RemoveAt i` are three different snapshots, so a concurrent `Add`/`Remove` between them
    /// either removes a *different* path than the one about to be closed, or removes the right
    /// index from a set that no longer holds it. Both outcomes leak a socket: one path is closed
    /// while still listed, the other is delisted and never closed. Deciding and removing under
    /// one lock makes "who owns this object now" a single fact, and the returned value is the
    /// only owner — so `Close` and this cannot both close it.
    member _.TakeIf(pred: IPath -> bool) : IPath option =
        lock gate (fun () ->
            let cur = snapshot ()
            let mutable i = 0
            let mutable found = -1
            while found < 0 && i < cur.Length do
                if pred cur.[i] then found <- i
                i <- i + 1
            if found < 0 then None
            else
                let victim = cur.[found]
                let next = Array.zeroCreate<IPath>(cur.Length - 1)
                Array.blit cur 0 next 0 found
                Array.blit cur (found + 1) next found (cur.Length - found - 1)
                publish next
                Some victim)

    /// Detach every entry and return them, atomically.
    ///
    /// `Close` needs the set to be empty before it starts closing sockets: taking a snapshot and
    /// then calling `Clear` leaves a window in which an add both lands in that snapshot (so it is
    /// closed here) and survives the clear (so `AddPathNowOpen`'s recheck finds and closes it
    /// too). One atomic take means exactly one side owns each path.
    member _.DrainAll() : IPath[] =
        lock gate (fun () ->
            let cur = snapshot ()
            publish (Array.Empty<IPath>())
            cur)

    /// Allocation-free enumeration: `for link in paths` binds the struct enumerator below, so a
    /// round over the path set costs zero bytes. A boxed `IEnumerator` would allocate one object
    /// per round — `BridgeTests` asserts the round loop allocates nothing once warm, and that
    /// assertion is the guard against per-frame garbage.
    member _.GetEnumerator() = PathEnum(snapshot ())

and [<Struct>] PathEnum =
    val mutable private items: IPath[]
    val mutable private idx: int
    new (src: IPath[]) = { items = src; idx = -1 }
    member this.MoveNext() =
        this.idx <- this.idx + 1
        this.idx < this.items.Length
    member this.Current = this.items.[this.idx]

/// A path change posted to the thread that owns SessionCarrier.paths.
/// The accept loop joins and replaces paths from its own thread while an async
/// session enumerates `paths` in Watch/Service; mutating the list from both
/// threads made the enumerator throw InvalidOperation_EnumFailedVersion.
type private PathOp =
    | PathAdd of IPath
    | PathRemove of uint16

[<Sealed>]
type SessionCarrier(session: Session, bridge: FlowBridge, ?uplinkPace: bool, ?tupleMbit: float,
                    ?rxChannel: RxChannel, ?asyncIo: AsyncPollerGroup, ?gatePass2: int, ?txSlots: int,
                    ?minActivePaths: int) =
    let pace = defaultArg uplinkPace false
    let tupleMbit = defaultArg tupleMbit 0.0
    let gatePass2 = defaultArg gatePass2 SocketTuning.GatePass2Limit
    let txSlots = SocketTuning.txSlotsFor (defaultArg txSlots 16)
    let channel = rxChannel
    let io = asyncIo
    let paths = PathSet()
    /// Copy of the path ids in `paths`, republished by the owning thread after every
    /// change. The accept loop (server JOIN admission, client PathPool) runs on another
    /// thread in async mode and used to read `paths` directly: List.Exists racing a
    /// RemoveAt on the coordinator saw a null slot and killed the server
    /// (NullReferenceException in HasPath when a client reconnected).
    let mutable pathIds: uint16[] = Array.Empty<uint16>()
    /// Kernel sockets of `paths`, published with pathIds (congestion-control nudges).
    let mutable pathSockets: System.Net.Sockets.Socket[] = Array.Empty<System.Net.Sockets.Socket>()
    /// Cached per-path health snapshots published by the owning thread for PathRefresher / CcNudge.
    let mutable pathHealthSnap: struct (uint16 * PathHealth)[] = Array.Empty()
    let mutable socketHealthSnap: struct (System.Net.Sockets.Socket * PathHealth)[] = Array.Empty()
    /// Clock time the path list became empty (0 while it has paths).
    /// Starts at construction: a session whose first path is still in the coordinator's
    /// queue (or not yet posted) must not look dead to the server's Alive check.
    let mutable noPathSinceNs = max 1L (session.Clock.NowNs())
    /// Indices into `paths` ordered from healthiest to worst. Rebuilt on path change
    /// and on each periodic health refresh (never per frame).
    let mutable rankedOrder: int[] = Array.Empty<int>()
    let mutable pathTiers: int[] = Array.Empty<int>()
    let mutable hasHealthyPath = true
    let mutable lastHealthRefreshNs = 0L
    let mutable lastBacklogSampleNs = 0L
    let mutable framesIn = 0L
    let mutable framesOut = 0L
    let mutable lastActivityFrames = 0L
    let mutable lastActivityNs = 0L
    let mutable healthCursor = 0
    // Keep the measured conservative default for general networks; callers may explicitly
    // raise the floor for a tested multipath deployment.
    let defaultMinActivePaths = 16
    let minActivePaths = max 1 (defaultArg minActivePaths defaultMinActivePaths)
    let mutable activeTarget = minActivePaths
    let mutable lastShrinkCheckNs = 0L
    let mutable overflowInWindow = false
    let mutable txActivityAtWindowStart = 0L
    let mutable txActivityWindowOpen = false
    let mutable backlogSeenInWindow = false
    let mutable framesOutAtWindowStart = 0L
    /// Reusable builders for periodic Describe() snapshots (avoids ~120 string allocs/s).
    let pathTxBuf = System.Text.StringBuilder(1024)
    let pathTcpBuf = System.Text.StringBuilder(2048)
    let creditDiagBuf = System.Text.StringBuilder(128)
    /// Peer-report freshness for the same flow slots: `<slot>:<gen>:<delivered>:<ageMs>` plus
    /// the stale/foreign/regression counters. Separate from `flow_credit` so the existing field
    /// keeps its exact meaning and no parser has to re-learn it.
    let peerDiagBuf = System.Text.StringBuilder(128)
    let describeBuf = System.Text.StringBuilder(4096)

    let healthOf (link: IPath) : PathHealth =
        match link with
        | :? IPathHealth as h -> h.Health
        | :? AsyncPathLink as ap -> ap.Health
        | _ -> PathHealth.unknown

    let refreshPathHealth (link: IPath) (nowNs: int64) (minIntervalNs: int64) =
        match link with
        | :? IPathHealth as h -> h.RefreshHealth(nowNs, minIntervalNs)
        | :? AsyncPathLink as ap -> ap.RefreshHealth(nowNs, minIntervalNs)
        | _ -> ()

    /// Tier 0 = healthy Open with headroom (or neutral non-kernel path),
    /// Tier 1 = CWR / SACK Recovery / reordering / queueing: normal on a lossy link,
    /// Tier 2 = TCP_CA_Loss (a real RTO stall) only.
    ///
    /// `TCP_CA_Recovery` (3) and `TCP_CA_CWR` (2) are what Linux does continuously at 24 %
    /// packet loss while still carrying line rate. Treating them as sick put nearly every
    /// socket of the live 24 %-loss link into Tier 2, so the write loop skipped them all,
    /// output froze for seconds and `hskip` reached 28 million. Only a socket stuck in
    /// `TCP_CA_Loss` (4) is genuinely not moving right now.
    let healthTier (h: PathHealth) =
        if not h.Valid then 0
        elif h.CaState >= 4uy then 2
        elif h.CaState >= 2uy || h.Lost > 0u then 1
        elif h.CaState = 0uy && h.Retrans = 0u && h.RetransRate < 0.15
             && (h.CwndBytes = 0u || h.UnackedBytes < (h.CwndBytes * 9u) / 10u)
             && (h.MinRttUs = 0u || h.RttUs <= h.MinRttUs * 3u / 2u) then 0
        else 1

    let healthRank (h: PathHealth) =
        if not h.Valid then 0L
        else
            let lossPenalty = int64 (h.RetransRate * 10000.0) * 1_000_000L
            let rttTerm = int64 h.RttUs
            let rateBonus = min 500_000L (h.DeliveryRate / 1000L)
            lossPenalty + rttTerm - rateBonus

    /// The snapshot the current `rankedOrder` was built from.
    ///
    /// `rankedOrder` stores positions, so a batch must index the very array those positions were
    /// computed over. Reading `paths.[idx]` instead would index a *newer* array with an older
    /// position: after a concurrent removal that is an out-of-range read or a different path than
    /// the ranking described.
    let mutable rankSnap: IPath[] = Array.Empty<IPath>()

    /// Counts structural changes to the path set: add, remove, and close.
    ///
    /// Declared before the ranking because the ranking validity test reads it, and incremented
    /// with `Interlocked` because `AddPath`/`RemovePath`/`Close` run on different threads — a
    /// plain read-modify-write can lose one and then look like "nothing changed" to a batch that
    /// is holding a stale snapshot.
    let mutable pathVersion = 0L

    /// The `pathVersion` this ranking was computed from.
    ///
    /// Length alone cannot tell whether the snapshot is still valid: a replacement keeps the
    /// count identical (one path leaves, one joins), so `rankSnap` would keep pointing at the
    /// removed — and closed — path while `rankedOrder` still holds its position. The batch would
    /// then hand frames to a dead socket and rank by its stale health. Every add/remove/close
    /// bumps `pathVersion`, so comparing it is the actual identity test.
    let mutable rankVersion = -1L

    let rebuildRanking () =
        // Snapshot and version from ONE critical section. Reading the version *after* taking the
        // snapshot (the previous form) leaves a window: a replacement landing in between makes
        // the snapshot pre-change and the version post-change, so the batch's `rankVersion =
        // Version` test passes while `rankSnap` still points at the removed, already-closed path
        // — frames go to a dead socket and health ranking describes a set that no longer exists.
        // Every mutation bumps the version inside the same `gate` that publishes the array, so
        // equality of the pair now means "nothing changed after this snapshot".
        let live, ver = paths.SnapshotWithVersion()
        rankSnap <- live
        rankVersion <- ver
        let n = live.Length
        if rankedOrder.Length <> n then rankedOrder <- Array.init n id
        else
            for i in 0 .. n - 1 do rankedOrder.[i] <- i
        if pathTiers.Length <> n then pathTiers <- Array.zeroCreate<int> n
        let mutable healthy = false
        let keys = Array.zeroCreate<struct (int * int64 * int)> n
        let pSnap = Array.zeroCreate<struct (uint16 * PathHealth)> n
        let sSnap = ResizeArray<struct (System.Net.Sockets.Socket * PathHealth)>(n)
        for i in 0 .. n - 1 do
            let p = live.[i]
            let h = healthOf p
            let t = healthTier h
            pathTiers.[i] <- t
            if t < 2 then healthy <- true
            keys.[i] <- struct (t, healthRank h, i)
            pSnap.[i] <- struct (p.PathId, h)
            match p with
            | :? PathLink as l -> sSnap.Add(struct (l.Socket, h))
            | :? AsyncPathLink as l -> sSnap.Add(struct (l.Socket, h))
            | _ -> ()
        if n > 1 then
            System.Array.Sort(keys, System.Comparison(fun (struct (ta, ra, ia)) (struct (tb, rb, ib)) ->
                let c1 = compare ta tb
                if c1 <> 0 then c1
                else
                    let c2 = compare ra rb
                    if c2 <> 0 then c2 else compare ia ib))
        for i in 0 .. n - 1 do
            let struct (_, _, idx) = keys.[i]
            rankedOrder.[i] <- idx
        hasHealthyPath <- healthy || n = 0
        System.Threading.Volatile.Write(&pathHealthSnap, pSnap)
        System.Threading.Volatile.Write(&socketHealthSnap, sSnap.ToArray())

    let refreshHealthAndRank (nowNs: int64) =
        let mutable written = 0L
        let live = paths.ToArray()
        let n = live.Length
        let totalFrames = framesIn + framesOut
        if totalFrames <> lastActivityFrames || lastActivityNs = 0L then
            lastActivityFrames <- totalFrames
            lastActivityNs <- nowNs
        let idleLong = lastHealthRefreshNs > 0L && nowNs - lastActivityNs >= 2_000_000_000L
        if n > 0 && not idleLong && (lastHealthRefreshNs = 0L || nowNs - lastHealthRefreshNs >= 50_000_000L) then
            lastHealthRefreshNs <- nowNs
            // Refresh the whole active working set within one 50 ms tick. Amortising over
            // 16 per tick left a 256-path bundle stale for 800 ms (and 64 paths for 200 ms),
            // so the rank table and write gate could make decisions on old TCP_INFO samples
            // while live queues were already deep. TCP_INFO is cheap relative to one frame.
            let batch = if n <= 128 then n else max 64 (n / 3)
            let start = healthCursor % n
            for j in 0 .. batch - 1 do
                refreshPathHealth live.[(start + j) % n] nowNs 50_000_000L
            healthCursor <- (start + batch) % n
            rebuildRanking ()
        // Independent 50 ms cadence: a blocked sender can be idle for more than 2 s while
        // its kernel or local backlog remains, so this must not share the idleLong gate.
        if n > minActivePaths && (lastBacklogSampleNs = 0L || nowNs - lastBacklogSampleNs >= 50_000_000L) then
            lastBacklogSampleNs <- nowNs
            // Sample kernel backlog only on this cadence, never every Service call.
            // The flag remembers a backlog that appears and drains before the window ends.
            if n > minActivePaths then
                let mutable backlog = 0L
                let mutable sampled = 0L
                for p in live do
                    let unsent = int64 p.UnsentBytes
                    if unsent > 0L then backlog <- backlog + unsent
                    match p with
                    | :? AsyncPathLink as ap -> sampled <- sampled + ap.BytesWritten
                    | :? PathLink as pl -> sampled <- sampled + pl.BytesWritten
                    | _ -> ()
                if backlog > 0L || session.Tx.Queued > 0 || framesOut <> framesOutAtWindowStart
                   || sampled <> txActivityAtWindowStart then
                    backlogSeenInWindow <- true
                written <- sampled
        // Decision remains independent of the health gate, using only counters and flags.
        if n > minActivePaths then
            if lastShrinkCheckNs = 0L || not txActivityWindowOpen then
                lastShrinkCheckNs <- nowNs
                txActivityAtWindowStart <- written
                framesOutAtWindowStart <- framesOut
                txActivityWindowOpen <- true
            elif nowNs - lastShrinkCheckNs >= 2_000_000_000L then
                if not overflowInWindow && activeTarget > minActivePaths && not backlogSeenInWindow then
                    activeTarget <- max minActivePaths (activeTarget * 3 / 4)
                overflowInWindow <- false
                backlogSeenInWindow <- false
                txActivityAtWindowStart <- written
                framesOutAtWindowStart <- framesOut
                lastShrinkCheckNs <- nowNs

    let publishIds () =
        // One snapshot for the whole function: `paths.[i]` re-reads the published array, so a
        // concurrent removal between the count and the reads could index past the end. Taking
        // the array once also keeps the ids and the sockets describing the same set.
        let live = paths.ToArray()
        let a = Array.zeroCreate<uint16> live.Length
        for i in 0 .. a.Length - 1 do a.[i] <- live.[i].PathId
        let socks = List<System.Net.Sockets.Socket>(live.Length)
        for p in live do
            match p with
            | :? PathLink as l -> socks.Add l.Socket
            | :? AsyncPathLink as l -> socks.Add l.Socket
            | _ -> ()
        System.Threading.Volatile.Write(&pathSockets, socks.ToArray())
        if a.Length > 0 then noPathSinceNs <- 0L
        elif noPathSinceNs = 0L then noPathSinceNs <- max 1L (session.Clock.NowNs())
        rebuildRanking ()
        System.Threading.Volatile.Write(&pathIds, a)
    /// Posted from a watching thread; applied by the thread that owns `paths`.
    let pathOps = ConcurrentQueue<PathOp>()
    let pathOpsInline = io.IsNone
    /// Posted path ops not yet applied. The client logs "paths ready" only when
    /// this reaches zero: Paths.Count read before the queue drains is stale.
    let mutable pathOpsPending = 0
    /// 1 once `Close` has begun. Nothing applies ops after that, so a `PathAdd` that loses the
    /// race with `Close` must be closed by whoever posted it — otherwise its socket and buffers
    /// leak and `HasPath` keeps reporting the path as joined.
    let mutable closed = 0
    /// Thread id of the loop that owns `paths`, recorded on its first round.
    ///
    /// `Close` must not return a path's pooled buffers while that loop sits between
    /// `TryBeginFrame` and `CommitFrame`: the frame being written lives inside the path's slab,
    /// and handing that slab back to `ArrayPool` lets another path rent the same bytes — a torn
    /// frame, attributed to the wrong path. The race is reachable from outside because
    /// `AsyncSession.Stop` tolerates a join timeout (`t.Join 5000 |> ignore`) and then calls
    /// `Close` on the Stop thread. So the owner id decides the behaviour: from the owner, close
    /// now; from anywhere else, only ask.
    let mutable ownerThread = 0
    /// 1 once a foreign thread has asked for the close and the owner has not performed it yet.
    let mutable closePending = 0
    /// 1 once the close body has been entered. `CloseNow` may be reached from the owner loop and
    /// from a caller that has seen the owner exit; the body runs once.
    let mutable closeDone = 0
    /// 1 once the owning loop has returned. After that nothing reads a frame out of a path, so a
    /// close from any thread is safe again.
    let mutable ownerExited = 0
    let mutable next = 0
    let mutable rxNext = 0
    let mutable rxMore = false
    /// Frames the last writeRound put out, against its 256-frame budget. A full
    /// budget means data still waits; a short one means every path refused.
    let mutable lastRoundSent = 0
    /// Bytes of DATA/PARITY/BOOSTER put on the wire (not WIN_ACK/CTRL/heartbeat).
    let mutable wireOut = 0L
    /// First successfully rendered DATA/initial PARITY bytes on charged submissions.
    /// Repeated DATA and BOOSTER copies remain in wireOut, not uniqueOut.
    let mutable uniqueOut = 0L
    let group = RateGroup(session.Clock)
    let member' = SessionRateMember(session, (fun () -> session.FrameLength), (fun () -> uniqueOut), (fun () -> wireOut))
    let mutable samplePrimed = false
    do
        if pace then
            group.Add member'
            group.ProtectRecovery <- true
        // Async IO: authenticate/decrypt cells on the IO threads (TBT_NO_PREOPEN=1 turns it off).
        match channel with
        | Some rx when isNull (Environment.GetEnvironmentVariable "TBT_NO_PREOPEN") ->
            rx.PreOpenerFactory <- Func<RxPreOpener>(fun () -> session.CreatePreOpener())
            rx.OnPreOpened <-
                PreOpenedHandler(fun path frame c ->
                    framesIn <- framesIn + 1L
                    session.OnFramePreOpened(path, frame, c))
        | _ -> ()
    /// Per-path uplink pacers, keyed by path id.
    ///
    /// `ConcurrentDictionary`, not `Dictionary`: a path can be removed from the accept thread.
    /// With the async IO group the removal runs through `ApplyPathOps` on the owning thread, but
    /// with `TBT_ASYNC_MODE=0` the carrier is inline (`pathOpsInline = io.IsNone`) and `AddPath` /
    /// `RemovePath` execute on the thread that accepted the connection. A plain `Dictionary`
    /// mutated there while `writeBatch` reads it is undefined behaviour — a lost entry at best,
    /// an unbounded probe loop at worst. Reads stay allocation-free either way.
    let pacers = System.Collections.Concurrent.ConcurrentDictionary<uint16, PathPacer>()
    /// Local slots visited by Watch and Service (RS-3: grows with armed flows, not 256).
    let mutable slotVisits = 0L
    /// Unpaced writeBatch: originals sent since the last admitted repair.
    let mutable originalsSinceRepair = 0
    /// A path change breaks the interval between aggregate kernel ACK samples.
    let mutable statsPathVersion = -1L
    let mutable statsNs = 0L
    let mutable statsAck = -1L

    let pacerOf (pathId: uint16) =
        if tupleMbit <= 0.0 then None
        else
            match pacers.TryGetValue pathId with
            | true, p -> Some p
            | _ ->
                let p = PathPacer(tupleMbit)
                pacers.[pathId] <- p
                Some p

    let mutable tuplePacerDenied = false

    let pathAllows (pathId: uint16) (bytes: int) (now: int64) =
        match pacerOf pathId with
        | None -> true
        | Some p ->
            p.SetMinRtt session.Tx.MinRttNs
            let ok = p.TryTake(bytes, now)
            // A tuple pacer refusal is capacity evidence exactly like a gate refusal.
            // Without this, --tuple-mbit sessions could stay pinned to the initial
            // working set while spare paths sat idle (the 64 -> 27 shrink signature).
            if not ok then tuplePacerDenied <- true
            ok

    let chargePath (pathId: uint16) (bytes: int) =
        match pacerOf pathId with
        | Some p -> p.OnSent bytes
        | None -> ()

    let uniqueInflight (fl: int) =
        int (max 0L (uniqueOut - session.Tx.UniqueAcked * int64 fl))

    /// Restart the group's sample on the first send opportunity that has an RTT
    /// (true min-RTT, or SRTT when Karn has skipped every seal-to-custody sample),
    /// so the first delivery rate is not averaged over the handshake delay (that
    /// sample was ~1 frame / several seconds and left cwnd at 4 frames forever).
    let primeSample () =
        if pace && not samplePrimed then
            let rtt =
                let m = session.Tx.MinRttNs
                if m <> Int64.MaxValue then m else int64 session.Tx.SrttNs
            if rtt > 0L then
                group.Remove member'
                group.Add member'
                samplePrimed <- true

    /// 64 unique frames is enough to finish several encoder steps and get a
    /// real RTT sample, but far below a many-path flood. Once the group has a
    /// healthy cwnd, TryTake is the cap (1.25 BDP).
    let startupCap (fl: int) = 64 * fl

    let groupAllows (fl: int) (repair: bool) (bulk: bool) =
        if repair then
            if group.Ready && group.TryTakeRepair(fl, fl) then true
            else not group.Ready && uniqueInflight fl <= startupCap fl
        elif group.Ready && group.TryTake(fl, bulk) then true
        else uniqueInflight fl + fl <= startupCap fl

    /// One half-open probe per second. `Poll` is a syscall; a peer that closed
    /// must not keep a dead slot or `PathPool` can never rejoin that id. Async
    /// paths already have their own `CheckAlive`.
    let mutable lastHalfOpenProbeNs = 0L

    let prune () =
        // Skip entirely when there is nothing to prune: no snapshot, no loop. (`ToArray` on an
        // empty published set returns the shared empty array, but a round should still do no
        // work at all — `BridgeTests` asserts the warm round loop allocates nothing.)
        if paths.Count > 0 then
            // Walk a snapshot (a concurrent `Close` on the Stop thread empties the set), and take
            // each dead path out atomically: closing by index would let a concurrent add/remove
            // shift positions so the wrong entry is delisted, or the right one is closed twice.
            let live = paths.ToArray()
            let mutable i = live.Length - 1
            let mutable changed = false
            while i >= 0 do
                let dead = live.[i]
                if not dead.Alive then
                    match paths.TakeIf(fun p -> obj.ReferenceEquals(p, dead)) with
                    | Some v ->
                        (try v.Close() with _ -> ())
                        changed <- true
                    | None -> ()
                i <- i - 1
            if changed then
                System.Threading.Interlocked.Increment(&pathVersion) |> ignore
                publishIds ()

    let hot = ReadySet()
    /// Q-3 (spec 16.3): one batch gives each path at most 4 frames, within budget. Returns frames sent.
    /// When uplinkPace is set, WIN_ACK/CTRL/heartbeat skip the group; new data uses TryTake
    /// (aggregate 1.25 BDP); BOOSTERs use the charged repair reserve (TryTakeRepair / OnRepair)
    /// and are skipped when that reserve is dry so new data can still go.
    /// When unpaced (server downlink, or client with pacing off), unsent originals go first.
    /// A repair is admitted only if the original queue is empty, or after every
    /// RepairSendOrder.OriginalsBeforeRepair originals, so a BOOSTER storm cannot
    /// freeze tx_head. TryRenderNext still prefers a BOOSTER when allowRepair is set
    /// (a paced repair slot, or an unpaced shared slot).
    /// notSent reported by each path in pass 1 of the current batch. Pass 2 reuses
    /// it instead of asking the kernel twice, and only revisits paths whose gate
    /// refused them in pass 1.
    let mutable batchNotSent = Array.zeroCreate<int> 512
    /// writeBatch gate diagnostics. Cumulative; the difference between two
    /// Describe windows is the per-window rate.
    let mutable gate1Pass = 0L
    let mutable gate1Deny = 0L
    let mutable gate2Pass = 0L
    let mutable gate2Deny = 0L
    let mutable beginFail = 0L
    let mutable produceFail = 0L
    let mutable healthSkips = 0L

    let writeBatch (budget0: int) =
        let mutable budget = budget0
        let mutable progress = true
        let mutable gated = false
        let mutable passes = 0
        let fl = session.FrameLength
        let now = session.Clock.NowNs()
        primeSample ()
        if paths.Count > batchNotSent.Length then
            batchNotSent <- Array.zeroCreate<int> (max paths.Count (batchNotSent.Length * 2))
        // Rank against one snapshot, then index *that* array. `rankedOrder` holds positions, so
        // reading `paths.[idx]` would apply an old position to a newer array: after a concurrent
        // removal that is an out-of-range read or a different path than the ranking described.
        // Rebuild when the set changed at all — not merely when the count changed. A replacement
        // (PathRefresher's make-before-break) removes one path and adds another, so the count is
        // equal while `rankSnap` still holds the closed one and `rankedOrder` its position.
        if rankVersion <> paths.Version || rankedOrder.Length <> rankSnap.Length then
            rebuildRanking ()
        let live = rankSnap
        let mutable batchLimited = false
        // Control frames and repairs are work even when the application has nothing to send.
        // Gating the whole batch on WantsToSend is what let a server with a pending WIN_ACK
        // spin (WaitUs returns 0 while ControlWaiting) and emit nothing for 8 s.
        while progress && passes < 4 && budget > 0 && paths.Count > 0
              && (session.WantsToSend || session.ControlWaiting || session.RepairWaiting) do
            progress <- false
            gated <- false
            passes <- passes + 1
            let n = live.Length
            let mutable idle = false
            // Phase 0: control frames (WIN_ACK, FLOW_STATE, CTRL) are tens of bytes and must
            // never wait for a health tier or a write gate. On the live 24 %-loss link every
            // socket sat in SACK Recovery, tier/gate verdicts denied them all, and no ACK or
            // credit left the server for 8 s: `frames_out` stayed pinned at 54111 while
            // `frames_in` climbed by 5000, the peer's flow credit froze and its uplink died.
            // Try every path in rank order and take the first that accepts a frame, whatever
            // its health or unsent verdict says.
            while session.ControlWaiting && budget > 0 && not idle do
                let mutable sentCtrl = false
                let mutable k = 0
                while k < n && not sentCtrl do
                    let idx = rankedOrder.[k]
                    let link = live.[idx]
                    let mutable frame = Span<byte>.Empty
                    if link.Alive && link.TryBeginFrame(&frame) then
                        if session.TryProduce(link.PathId, frame, true) then
                            link.CommitFrame()
                            framesOut <- framesOut + 1L
                            budget <- budget - 1
                            progress <- true
                            sentCtrl <- true
                            if not (session.WantsToSend || session.RepairWaiting) then idle <- true
                    k <- k + 1
                if not sentCtrl then idle <- true
            // Elastic working set: start at min(n, max 16 activeTarget) healthiest paths.
            // If all active paths saturate or fail to advance, spill immediately into all n paths
            // and grow activeTarget so per-tuple shaped links (e.g. rtt400 64-path) reach full width.
            let baseActive = if n <= minActivePaths then n else min n (max minActivePaths activeTarget)
            let mutable scanLimit = baseActive
            let mutable expanded = false
            // If every path in the active working set is Tier 2 (e.g. simultaneous loss/blackout),
            // expand immediately or allow degraded paths so recovery never stalls.
            let mutable activeHasHealthy = false
            let mutable chk = 0
            let checkLimit = min n baseActive
            while chk < checkLimit && not activeHasHealthy do
                if pathTiers.[rankedOrder.[chk]] < 2 then activeHasHealthy <- true
                chk <- chk + 1
            if not activeHasHealthy && scanLimit < n then
                scanLimit <- n
                expanded <- true
            let allowDegraded = not hasHealthyPath
            let mutable activeSaturated = false
            // Reset the saturation evidence at the start of each pass. A tuple pacer refusal
            // from pass 1 must reach the spillover test; pass 2 must not inherit an old flag.
            tuplePacerDenied <- false
            // A health verdict may not starve output. At 24 % loss nearly every socket is in
            // SACK Recovery, and a tier table up to one refresh batch (50 ms) stale can hide
            // the paths that would have carried the frame, so an unconditional skip let the
            // loop walk all 32 paths and send nothing: `frames_out` froze for seconds while
            // `hskip` ran into the millions. Each round may skip at most one scan-length of
            // paths; after that every path is fair game again, and a stale verdict can only
            // cost one ranking position instead of the whole round.
            let mutable healthSkipBudget = max 1 scanLimit
            for pass in 1 .. 2 do
              if (pass = 1 || gated || healthSkipBudget <= 0) && not idle then
                let mutable j = 0
                while j < scanLimit && budget > 0 && not idle do
                    let rankPos = (next + j) % scanLimit
                    let idx = rankedOrder.[rankPos]
                    let link = live.[idx]
                    let tier = pathTiers.[idx]
                    let skipForHealth = tier >= 2 && not allowDegraded && healthSkipBudget > 0
                    if skipForHealth then
                        healthSkipBudget <- healthSkipBudget - 1
                        if pass = 1 then
                            batchNotSent.[idx] <- Int32.MaxValue
                            healthSkips <- healthSkips + 1L
                            // A Tier-2 verdict is NOT evidence that the working set is saturated.
                            // The spillover branch below reads its input as capacity pressure, and
                            // only a gate refusal or a full tx ring is such proof — both still set
                            // `activeSaturated`. Counting a skip here grew `activeTarget` by 50 %
                            // per stray verdict, and the shrink clause needs `Tx.Queued = 0`,
                            // which a session with pending data does not reach, so the set only
                            // ever walked up.
                            // A working set with no healthy path at all is handled before the
                            // scan (`activeHasHealthy` widens `scanLimit` to n), so nothing that
                            // needs widening loses it here.
                    else
                        let h = healthOf link
                        // Cached sample only. A positive DeliveryRate scales the queue; no sample
                        // uses the 16 KiB probe inside rateAwareLimits, not the 64 KiB cwnd floor.
                        let struct (pathPass1, pathPass2) =
                            WriteGate.rateAwareLimits gatePass2 h.Valid h.DeliveryRate (int h.CaState)
                        let mutable frame = Span<byte>.Empty
                        let notSent =
                            if pass = 1 then
                                let v = link.UnsentBytes
                                batchNotSent.[idx] <- v
                                if v <> 0 then gated <- true
                                v
                            else batchNotSent.[idx]
                        let eligible = pass = 1 || (notSent <> 0 && notSent <> Int32.MaxValue)
                        let admitted =
                            if not eligible then false
                            elif (if pass <= 1 then notSent >= 0 && notSent <= pathPass1
                                      else notSent < 0 || notSent <= pathPass2) then
                                if pass = 1 then gate1Pass <- gate1Pass + 1L
                                else gate2Pass <- gate2Pass + 1L
                                true
                            else
                                if pass = 1 then
                                    gate1Deny <- gate1Deny + 1L
                                    activeSaturated <- true
                                else gate2Deny <- gate2Deny + 1L
                                batchLimited <- true
                                false
                        let begun = admitted && link.TryBeginFrame(&frame)
                        if admitted && not begun then
                            beginFail <- beginFail + 1L
                            batchLimited <- true
                            activeSaturated <- true
                        if begun then
                            let mutable allowRepair = true
                            let mutable charge = 0
                            let mutable attempt = false
                            let mutable unpacedRepair = false
                            let mutable unpacedData = false
                            if session.ControlWaiting then
                                attempt <- true
                            elif not pace then
                                if RepairSendOrder.canSendRepair session.Tx.Queued session.RepairWaiting originalsSinceRepair then
                                    attempt <- true
                                    unpacedRepair <- true
                                elif session.WantsToSend then
                                    attempt <- true
                                    allowRepair <- false
                                    unpacedData <- true
                            elif session.RepairWaiting && groupAllows fl true (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                                attempt <- true
                                charge <- 1
                            elif groupAllows fl false (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                                // allowRepair=false: a dry repair reserve must not block new data.
                                attempt <- true
                                allowRepair <- false
                                charge <- 2
                            let uniqueBefore = session.Tx.UniqueRendered
                            let produced = attempt && session.TryProduce(link.PathId, frame, allowRepair)
                            if attempt && not produced then produceFail <- produceFail + 1L
                            if produced then
                                let uniqueDelta = session.Tx.UniqueRendered - uniqueBefore
                                link.CommitFrame()
                                framesOut <- framesOut + 1L
                                budget <- budget - 1
                                progress <- true
                                if not (session.WantsToSend || session.RepairWaiting) then idle <- true
                                if unpacedRepair then originalsSinceRepair <- 0
                                elif unpacedData then originalsSinceRepair <- originalsSinceRepair + 1
                                if charge = 1 || charge = 2 then
                                    uniqueOut <- uniqueOut + uniqueDelta * int64 fl
                                if charge = 1 then
                                    wireOut <- wireOut + int64 fl
                                    group.OnRepair fl
                                    chargePath link.PathId fl
                                elif charge = 2 then
                                    wireOut <- wireOut + int64 fl
                                    group.OnSent fl
                                    chargePath link.PathId fl
                    j <- j + 1
                if pass = 1 && tuplePacerDenied then activeSaturated <- true
                // Spillover: if pass 1 actually saturated the active working set — a pass-1 gate
                // refusal or a full tx ring, both capacity facts — while demand remains and spare
                // paths exist, expand scanLimit to all n paths immediately and grow activeTarget.
                // A Tier-2 (TCP_CA_Loss) skip is deliberately not a saturation signal: see the
                // note at the skip branch.
                if pass = 1 && not expanded && scanLimit < n && budget > 0 && not idle && activeSaturated then
                    let prevLimit = scanLimit
                    scanLimit <- n
                    expanded <- true
                    overflowInWindow <- true
                    activeTarget <- min n (activeTarget + max 4 (activeTarget / 2))
                    // Probe newly revealed paths in pass 1 right away so pass 2 can also see them.
                    let mutable k = prevLimit
                    while k < n && budget > 0 && not idle do
                        let idx = rankedOrder.[k]
                        let link = live.[idx]
                        let tier = pathTiers.[idx]
                        if tier >= 2 && not allowDegraded then
                            batchNotSent.[idx] <- Int32.MaxValue
                            healthSkips <- healthSkips + 1L
                        else
                            let h = healthOf link
                            let struct (pathPass1, _) =
                                WriteGate.rateAwareLimits gatePass2 h.Valid h.DeliveryRate (int h.CaState)
                            let v = link.UnsentBytes
                            batchNotSent.[idx] <- v
                            if v <> 0 then gated <- true
                            let admitted =
                                if v >= 0 && v <= pathPass1 then
                                    gate1Pass <- gate1Pass + 1L
                                    true
                                else
                                    gate1Deny <- gate1Deny + 1L
                                    batchLimited <- true
                                    false
                            let mutable frame = Span<byte>.Empty
                            let begun = admitted && link.TryBeginFrame(&frame)
                            if admitted && not begun then
                                beginFail <- beginFail + 1L
                                batchLimited <- true
                            if begun then
                                let mutable allowRepair = true
                                let mutable charge = 0
                                let mutable attempt = false
                                let mutable unpacedRepair = false
                                let mutable unpacedData = false
                                if session.ControlWaiting then attempt <- true
                                elif not pace then
                                    if RepairSendOrder.canSendRepair session.Tx.Queued session.RepairWaiting originalsSinceRepair then
                                        attempt <- true
                                        unpacedRepair <- true
                                    elif session.WantsToSend then
                                        attempt <- true
                                        allowRepair <- false
                                        unpacedData <- true
                                elif session.RepairWaiting && groupAllows fl true (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                                    attempt <- true
                                    charge <- 1
                                elif groupAllows fl false (session.Tx.Queued > 0) && pathAllows link.PathId fl now then
                                    attempt <- true
                                    allowRepair <- false
                                    charge <- 2
                                let uniqueBefore = session.Tx.UniqueRendered
                                let produced = attempt && session.TryProduce(link.PathId, frame, allowRepair)
                                if attempt && not produced then produceFail <- produceFail + 1L
                                if produced then
                                    let uniqueDelta = session.Tx.UniqueRendered - uniqueBefore
                                    link.CommitFrame()
                                    framesOut <- framesOut + 1L
                                    budget <- budget - 1
                                    progress <- true
                                    if not (session.WantsToSend || session.RepairWaiting) then idle <- true
                                    if unpacedRepair then originalsSinceRepair <- 0
                                    elif unpacedData then originalsSinceRepair <- originalsSinceRepair + 1
                                    if charge = 1 || charge = 2 then
                                        uniqueOut <- uniqueOut + uniqueDelta * int64 fl
                                    if charge = 1 then
                                        wireOut <- wireOut + int64 fl
                                        group.OnRepair fl
                                        chargePath link.PathId fl
                                    elif charge = 2 then
                                        wireOut <- wireOut + int64 fl
                                        group.OnSent fl
                                        chargePath link.PathId fl
                        k <- k + 1
            if scanLimit > 0 then next <- (next + 1) % scanLimit
        if pace && batchLimited then group.NoteLimited()
        budget0 - budget

    let readPass (poller: Poller) (interactive: bool) =
        let armed = bridge.Armed
        let mutable slot = armed.NextUp -1
        let mutable readable = 0
        while slot >= 0 do
            if session.IsInteractive slot = interactive && bridge.WantsRead slot then
                readable <- readable + 1
            slot <- armed.NextUp slot
        let quantum = if readable <= 1 then 4096 else FlowBridge.Quantum
        // `NextUp` does not wrap. A cursor left at maxSlot+1 made the next round
        // start past every armed flow and then never visit them again.
        let start = if readable > 1 then bridge.ReadCursor &&& 255 else -1
        let mutable slot = if start < 0 then armed.NextUp -1 else armed.NextAfter (start - 1)
        let stop = slot
        let mutable wrapped = false
        let mutable visited = 0
        let mutable firstRead = -1
        while slot >= 0 && visited < 256 && not (wrapped && slot = stop) do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s ->
                if session.IsInteractive slot = interactive then
                    if interactive then hot.Add slot
                    if bridge.WantsRead slot then
                        if firstRead < 0 then firstRead <- slot
                        bridge.Read(slot, limit = quantum)
            visited <- visited + 1
            let next = armed.NextAfter slot
            if next <= slot then wrapped <- true
            slot <- next
        if readable > 1 && firstRead >= 0 then
            bridge.ReadCursor <- (firstRead + 1) &&& 255

    /// Send at most 256 frames in batches; the interactive input is read again between batches.
    let writeRound () =
        let backlog = session.Tx.Queued
        for link in paths do link.Backlog <- backlog
        if pace && backlog >= RateGroup.BacklogFrames then group.NoteBacklog()
        let mutable budget = 256
        let mutable go = true
        while go do
            let sent = writeBatch budget
            budget <- budget - sent
            go <- sent > 0 && budget > 0 && (session.WantsToSend || session.ControlWaiting)
            if go && not hot.IsEmpty then
                let mutable slot = hot.NextUp -1
                while slot >= 0 do
                    slotVisits <- slotVisits + 1L
                    if session.IsInteractive slot then
                        if bridge.WantsRead slot then bridge.Read slot
                    slot <- hot.NextUp slot
        lastRoundSent <- 256 - budget

    member _.Session = session
    member _.Bridge = bridge
    /// The live path list: only for the thread that owns it (the session thread).
    member _.Paths = paths
    /// Path ids as of the last change; safe from any thread.
    member _.PathIds = System.Threading.Volatile.Read(&pathIds)
    /// Path count as of the last change; safe from any thread.
    member _.PathCount = System.Threading.Volatile.Read(&pathIds).Length
    /// Snapshot of path sockets; safe from any thread (a socket may be closed by then).
    member _.PathSockets = System.Threading.Volatile.Read(&pathSockets)
    /// Cached per-path health snapshot; safe from any thread, zero syscalls.
    member _.SnapshotPathHealth() = System.Threading.Volatile.Read(&pathHealthSnap)
    /// Cached per-socket health snapshot for health-targeted CC nudge; safe from any thread.
    member _.SnapshotSocketHealth() = System.Threading.Volatile.Read(&socketHealthSnap)
    /// Current elastic active working set size.
    member _.ActivePathTarget = min paths.Count (max (min paths.Count minActivePaths) activeTarget)
    /// Refresh cached health snapshots and rerank paths now (also called periodically in Service).
    member _.RefreshHealthNow() =
        let nowNs = session.Clock.NowNs()
        // Snapshot once: `0 .. paths.Count - 1` with `paths.[i]` mixes two arrays, and a removal
        // between them indexes past the end.
        let live = paths.ToArray()
        for i in 0 .. live.Length - 1 do refreshPathHealth live.[i] nowNs 0L
        rebuildRanking ()
    /// Path adds/removes posted but not yet applied by the session thread.
    member _.PathOpsPending = System.Threading.Interlocked.CompareExchange(&pathOpsPending, 0, 0)
    member _.FramesIn = framesIn
    member _.FramesOut = framesOut
    member _.SlotVisits = slotVisits
    /// One Q-3 batch (tests): at most 4 frames per path.
    member _.WriteBatch(budget: int) = writeBatch budget
    /// Wakes the thread that owns `paths` after an op is posted from another thread.
    ///
    /// Without it the post is invisible to that thread's wait: `AddPath`/`RemovePath` run on
    /// the accept / PathPool thread, and their only trace is a queue plus a counter. The
    /// session thread can already be inside `epoll_wait(-1)` with nothing armed but the
    /// receive-channel wake fd — a post is not a frame, so it never becomes readable and the
    /// queued path waits for an unrelated socket event (or, with `--stats-s`, up to one
    /// second). The coordinator owns the channel, so it installs the wake; a carrier built
    /// without one is inline and self-applying.
    member val WakePathOps: (unit -> unit) option = None with get, set
    /// Optional owner predicate: the session stays alive with no path while the
    /// owner is still rebuilding its path pool. None keeps the old rule.
    member val KeepAlive: (unit -> bool) option = None with get, set
    /// How long the session survives with no path at all (server side). A burst of
    /// resets (middlebox, NAT rebind, ss -K) can take every path at once; the old
    /// rule closed the session on the spot, the client's JOINs for it were refused,
    /// and the client crawled down to "no path remains" and exited 37 s later.
    /// With a grace the client rejoins into the same session. 0 keeps the old rule.
    member val NoPathGraceNs = 0L with get, set
    member this.Alive =
        let hasPath = System.Threading.Volatile.Read(&pathIds).Length > 0 || not pathOps.IsEmpty
        (hasPath
         || (match this.KeepAlive with Some f -> f () | None -> false)
         || (this.NoPathGraceNs > 0L && session.Clock.NowNs() - noPathSinceNs < this.NoPathGraceNs))
        && not session.Faulted && not session.Closed
    member _.HasPendingRx = rxMore
    member _.UplinkPace = pace
    member _.TupleMbit = tupleMbit
    /// Poller wait when the uplink group is token-limited: a 5 s wait would stall the pipe.
    /// Pure query (no side-effecting group.Service(); Watch/Service advance the group).
    member _.WaitMs() =
        if rxMore then 0
        elif not pace then 5000
        elif session.ControlWaiting || not session.WantsToSend then 5000
        else
            let fl = session.FrameLength
            if groupAllows fl false false || groupAllows fl true false then 5000
            else 2

    /// Microseconds to block before the next Watch/Service; -1 blocks until a
    /// descriptor fires. Pure query: group.Service() runs in Watch/Service only.
    member _.WaitUs() =
        // No path can carry anything: a socket or listener event is the only thing
        // that can change this, so do not spin on a control cell that has nowhere to go.
        //
        // But "no path" is not the same as "nothing to do": a join is posted from another
        // thread, and its only trace here is `pathOpsPending`. Blocking indefinitely while an
        // op is queued means the session thread waits for an unrelated socket event even
        // though `ApplyPathOps` already has work — the loop that owns `paths` is the only
        // place that work can happen. Verified shape: `AddPath`/`RemovePath` enqueue and
        // increment `pathOpsPending`, and no code path wakes this poller (there is no
        // `Poller.Wake`, and the carrier holds no handle to the session's `Poller`).
        // With `--stats-s` the stats deadline bounds the wait to ≤1 s; with stats off
        // (`StatsEveryMs = 0`) the wait is unbounded, so the queued add would sit until some
        // unrelated descriptor fired. 1 ms is a backoff, not a poll: it applies only while an
        // op is pending, and the next call finds `pathOpsPending = 0`.
        if paths.Count = 0 then
            // A closed carrier has no future: no op will be applied and no path will appear.
            if System.Threading.Volatile.Read(&closed) <> 0 then -1
            elif System.Threading.Interlocked.CompareExchange(&pathOpsPending, 0, 0) > 0 then 1000
            else -1
        elif rxMore then 0
        elif session.ControlWaiting then (if lastRoundSent = 0 then 2 else 0)
        elif lastRoundSent >= 256 then 0
        // A round that put nothing on the wire while the session still has data is blocked on
        // transport backpressure (every path's ring full or gated), not waiting for a timer.
        // Returning 0 here spun the session thread at ~120 000 rounds/s on a 2-vCPU host: it
        // burned a whole core, starved the IO threads, and produced 2.58 M futile refusals.
        // Sleep 1 ms; AsyncPathLink wakes this channel within microseconds once a ring drains.
        elif lastRoundSent = 0 && session.WantsToSend then 1000
        else
            let timerUs = session.NextTimerUs()
            if not session.WantsToSend then int timerUs
            elif not pace then int timerUs
            else
                let fl = session.FrameLength
                if groupAllows fl false false || groupAllows fl true false then 0
                else
                    let paceUs = max 1L ((group.TokenWaitNs fl + 999L) / 1000L)
                    if timerUs < 0L then int paceUs else int (min timerUs paceUs)

    /// One line of counters for the log.
    member _.Describe() =
        let tx = session.Tx
        let rx = session.Rx
        let mutable consTx = 0L
        let mutable consAck = 0L
        let mutable ackKnown = true
        let mutable i = 0
        let pathTx = pathTxBuf.Clear()
        let pathTcp = pathTcpBuf.Clear()
        let kernel = KernelTcpTotals()
        let appendKernelPath (id: uint16) (inc: uint32) (written: int64) (info: KernelTcpInfo voption) =
            consTx <- consTx + written
            kernel.Add info
            let acked = match info with ValueSome value -> value.BytesAcked | ValueNone -> -1L
            if acked < 0L then ackKnown <- false
            else consAck <- consAck + acked
            if pathTx.Length > 0 then pathTx.Append(',') |> ignore
            pathTx.Append(int id).Append(':').Append(written) |> ignore
            if acked >= 0L then pathTx.Append('/').Append(acked) |> ignore
            if pathTcp.Length > 0 then pathTcp.Append(',') |> ignore
            pathTcp.Append(int id).Append('@').Append(inc).Append(':') |> ignore
            match info with
            | ValueSome value -> pathTcp.Append(KernelTcpInfo.describePath value) |> ignore
            | ValueNone -> pathTcp.Append('-') |> ignore
        // Stats run every second while `Close` may be draining the set from the Stop thread, so
        // `while i < paths.Count do paths.[i]` can index past the end of a shorter array. One
        // snapshot also keeps the per-path numbers describing the same set.
        let live = paths.ToArray()
        while i < live.Length do
            match live.[i] with
            | :? PathLink as pl ->
                appendKernelPath pl.PathId pl.Incarnation pl.BytesWritten pl.TcpInfo
            | :? AsyncPathLink as ap ->
                appendKernelPath ap.Id ap.Incarnation ap.BytesWritten (ap.TryTcpInfo())
            | _ -> ()
            i <- i + 1
        let now = session.Clock.FreshNs()
        let creditDiag = creditDiagBuf.Clear()
        let peerDiag = peerDiagBuf.Clear()
        let mutable shown = 0
        let mutable fs = 0
        while fs < 256 && shown < 4 do
            let f = session.Flows.[fs]
            if f.Active then
                if creditDiag.Length > 0 then creditDiag.Append(' ') |> ignore
                creditDiag.Append(fs).Append(':').Append(f.Gen).Append(':')
                    .Append(f.TxOffset).Append('/').Append(f.TxLimit).Append(':')
                    .Append(f.RxDelivered).Append('/').Append(f.Reserve.Count) |> ignore
                if peerDiag.Length > 0 then peerDiag.Append(' ') |> ignore
                // `TxDeliveredReported` is the peer's rx CUSTODY frontier as last reported, so it
                // lags by one FLOW_STATE page and is not its read position. `ageMs` is a LOCAL
                // age of that acceptance (needs no clock agreement with the peer); `-` means no
                // report has been accepted for this generation yet. stale/foreign/regr are
                // cumulative counters, so read their deltas between stats lines.
                peerDiag.Append(fs).Append(':').Append(f.Gen).Append(':')
                    .Append(f.TxDeliveredReported).Append(':')
                    .Append(if f.TxReportNs > 0L then (now - f.TxReportNs) / 1_000_000L else -1L).Append(':')
                    .Append(f.TxReportsStale).Append('/').Append(f.TxReportsForeign).Append('/')
                    .Append(f.TxDeliveredRegressions) |> ignore
                shown <- shown + 1
            fs <- fs + 1
        let ackRate =
            if kernel.Count = 0 || not ackKnown || statsAck < 0L || consAck < statsAck
               || pathVersion <> statsPathVersion || now <= statsNs then "-"
            else
                (float (consAck - statsAck) * 8000.0 / float (now - statsNs)).ToString("F3", Globalization.CultureInfo.InvariantCulture)
        statsNs <- now
        statsAck <- if ackKnown then consAck else -1L
        statsPathVersion <- pathVersion
        let sb = describeBuf.Clear()
        sb.Append("paths=").Append(paths.Count)
          .Append(" active_paths=").Append(min paths.Count (max (min paths.Count minActivePaths) activeTarget))
          .Append(" frames_in=").Append(framesIn).Append(" frames_out=").Append(framesOut) |> ignore
        match channel with
        | Some rx -> sb.Append(" preopened=").Append(rx.PreOpenedCount) |> ignore
        | None -> ()
        sb.Append(" tx_direction=").Append(if session.Role = Role.Client then "up" else "down")
          .Append(" send_control=").Append(if pace then "group" elif kernel.Count > 0 then "kernel" else "tun")
          .Append(" tx_head=").Append(tx.Head).Append(" peer_ack=").Append(tx.PeerAck).Append(" grant_limit=").Append(tx.GrantLimit)
          .Append(" queued=").Append(tx.Queued).Append(" srtt_ms=").Append(int (tx.SrttNs / 1e6))
          // Decoder health belongs in the one line an operator reads. A row that is neither
          // pending nor idle is the leak that silently turns FEC off once rowsAllocated hits
          // maxRows, and `stale` counts equations Release had to throw away because the
          // decode window fell behind. Both were invisible for three live rounds.
          .Append(" dec_rows=").Append(rx.Decoder.PendingRows).Append('/').Append(rx.Decoder.FreeRows)
          .Append('/').Append(rx.Decoder.RowsAllocated).Append(" dec_stale=").Append(rx.Decoder.StaleRows)
          .Append(" minrtt_ms=").Append(if tx.MinRttNs = Int64.MaxValue then "-" else string (tx.MinRttNs / 1_000_000L))
          .Append(" rtt_skipped=").Append(tx.RttSkipped)
          .Append(" rho=").Append(tx.Rho.ToString("F3", Globalization.CultureInfo.InvariantCulture))
          .Append(" cell_loss=").Append(if tx.CellLoss < 0.0 then "-" else tx.CellLoss.ToString("F4", Globalization.CultureInfo.InvariantCulture))
          // What the reported erasure was made of, cumulative: cells the decoder had to
          // rebuild / cells late by the step clock alone / sources accounted. `cell_loss`
          // above is the peer's report for OUR send direction; these three are OUR receive
          // direction, so a direction that looks lossy while the other does not can be read
          // off one line instead of being guessed at from rho.
          .Append(" er_src=").Append(rx.Counters.ErasureRecovered).Append('/').Append(rx.Counters.ErasureLateClock).Append('/').Append(rx.Counters.TimelyDue)
          // What the deadline ledger booked for PENDING REBUILDS and what the controller
          // actually reads. `er_src` above is cumulative reports: a rebuild is counted the
          // moment FEC solves a slot, before the step deadline decides whether any cell was
          // really absent (18.4 % of a lossless run read that way). `er_rebuild=missing/due` is
          // the subset of the 10 ms windows that came from settling those rebuilds — direct
          // arrivals are booked separately by observeTimely and are NOT in this pair, so its
          // ratio is not the window's rate. `er_cancel` counts rebuilds a timely arrival
          // vindicated, and `er_now=mean/p95` is the q16 pair TxHalf receives in this direction:
          // that, not er_rebuild, is the number the controller acted on.
          .Append(" er_rebuild=").Append(rx.Counters.RebuildSettledMissing).Append('/').Append(rx.Counters.RebuildSettledDue)
          .Append(" er_cancel=").Append(rx.Counters.ErasureCancelled)
          .Append(" er_now=").Append(int rx.ErasureQ16).Append('/').Append(int rx.ErasureP95Q16)
          // Shape of the distribution behind `er_now`'s percentile: closed-window candidates,
          // how many of them were thinner than 16 due cells, and the (due, erased) pair of the
          // extreme window. Read this before concluding anything from a saturated p95 — one
          // one-cell window at 1.00 and a wholly-erased 16-cell window print the same number.
          .Append(" er_win=").Append(rx.P95Candidates).Append('/').Append(rx.P95SmallWindows)
          .Append(':').Append(rx.P95PickDue).Append(':').Append(rx.P95PickErased)
          .Append(" boosters=").Append(tx.BoostersSent).Append(" repair_repeats=").Append(tx.RepairRepeats)
          .Append(" spurious_repairs=").Append(tx.SpuriousRepairs)
          .Append(" ctrl=").Append(session.CtrlSent).Append('/').Append(session.CtrlExecuted).Append('/').Append(session.CtrlRejected)
          .Append(" close=").Append(int session.CloseState)
          .Append(" rx_ack=").Append(rx.AckStepLo).Append(" rx_base=").Append(rx.Decoder.BaseStep)
          .Append(" recovered=").Append(rx.Counters.Recovered)
          .Append(" interactive=").Append(session.InteractiveSources).Append('/').Append(session.Tx.UrgentSteps)
          .Append(" efloor=").Append(rx.EstimateFloor).Append(" efolds=").Append(rx.EstimateFolds)
          .Append(" elast=").Append(rx.LastFoldedExpected).Append('/').Append(rx.LastFoldedReceived).Append('@').Append(rx.LastFoldedStep)
          .Append(" urgent_full=").Append(session.Tx.UrgentFull).Append(" flows=").Append(bridge.Active)
          .Append(" flowread=").Append(bridge.ReadCalls).Append('/').Append(bridge.ReadBytes).Append('/').Append(bridge.WouldBlock)
          .Append(" opened=").Append(session.Flows.Counters.Opened).Append(" closed=").Append(session.Flows.Counters.Closed)
          .Append(" local_fail=").Append(session.Flows.Counters.LocalFailures)
          .Append(" stale=").Append(session.Flows.Counters.StaleSources)
          .Append(" flow_state=").Append(session.FlowStatesSent).Append('/').Append(session.FlowStatesRejected)
          .Append(" flow_credit=").Append(if creditDiag.Length = 0 then "-" else creditDiag.ToString())
          // `flow_peer=<slot>:<gen>:<reportedDelivered>:<ageMs>:<stale>/<foreign>/<regr>` — the
          // peer's reported rx-custody frontier and how old that acceptance is on OUR clock.
          // It is a REPORTED offset: it lags one FLOW_STATE page and is not the peer's app read.
          .Append(" flow_peer=").Append(if peerDiag.Length = 0 then "-" else peerDiag.ToString())
          .Append(" fills=").Append(tx.FillsSent)
          .Append(" gate1=").Append(gate1Pass).Append('/').Append(gate1Deny)
          .Append(" gate2=").Append(gate2Pass).Append('/').Append(gate2Deny)
          .Append(" beginfail=").Append(beginFail).Append(" producefail=").Append(produceFail)
          .Append(" hskip=").Append(healthSkips)
          .Append(" sealed=").Append(session.Tx.SealedCells).Append(" dropack=").Append(session.Tx.DroppedAcked)
          .Append(" qhigh=").Append(session.Tx.QueueHigh)
          .Append(" cons_tx_b=").Append(consTx).Append(" cons_ack_b=").Append(if ackKnown then string consAck else "-")
          .Append(" path_tx=").Append(pathTx.ToString()) |> ignore
        if kernel.Count > 0 then
            sb.Append(kernel.Describe()).Append(" kernel_ack_mbit=").Append(ackRate)
              .Append(" kernel_generation=").Append(pathVersion)
              .Append(" kernel_path=").Append(pathTcp.ToString()) |> ignore
        if pace then
            sb.Append(" pace_bw_mbit=").Append((group.Bandwidth * 8000.0).ToString("F1"))
              .Append(" pace_inflight=").Append(max 0L (uniqueOut - tx.UniqueAcked * int64 session.FrameLength))
              .Append(" pace_phase=").Append(group.Phase)
              .Append(" pace_pacing_mbit=").Append((group.PacingRate * 8000.0).ToString("F1"))
              .Append(" pace_full_rounds=").Append(group.FullPipeRounds)
              .Append(" pace_app_rounds=").Append(group.AppLimitedRounds)
              .Append(" pace_repair_kib=").Append(group.RepairBytes / 1024L) |> ignore
        sb.ToString()

    /// Post an op for the thread that owns `paths`, then wake it.
    ///
    /// Order matters: the counter rises before the enqueue (so a thread that applies ops
    /// between the two still sees work pending and takes the bounded wait instead of a bare
    /// -1), and the wake comes last (so it cannot be consumed before the op is visible).
    member private this.PostPathOp(op: PathOp) =
        System.Threading.Interlocked.Increment(&pathOpsPending) |> ignore
        pathOps.Enqueue op
        match this.WakePathOps with
        | Some wake -> wake ()
        | None -> ()
        // If `Close` has already run, nothing applies ops any more: claim the op here. Exactly
        // one drainer gets it — `ConcurrentQueue.TryDequeue` hands each item to one thread — so
        // a path `Close` already took is not closed twice.
        if System.Threading.Volatile.Read(&closed) <> 0 then this.CloseOrphanOps()

    /// Add a path whose bootstrap is done; frames it already holds come in at once.
    member this.AddPath(link: IPath) =
        if pathOpsInline then this.AddPathNow link
        else this.PostPathOp(PathAdd link)

    member private this.AddPathNow(link: IPath) =
        // After `Close` emptied the set, nothing owns a path added then: it would never be
        // closed nor removed, so its socket leaks and `HasPath` would keep reporting it. Refuse
        // the add and close it here — this is the one place that knows both facts.
        if System.Threading.Volatile.Read(&closed) <> 0 then (try link.Close() with _ -> ())
        else this.AddPathNowOpen link

    /// Add, then recheck the closed flag.
    ///
    /// `Close` raises the flag, takes the snapshot and clears the set, then closes the sockets.
    /// This side publishes the add and only afterwards reads the flag: either `Close` snapshot
    /// includes this path, or this read sees the flag and takes the path back out here. Both
    /// sides read and write a published array, so the recheck needs no barrier trick — it is an
    /// ordinary load that follows the release store of `paths.Add`.
    member private this.AddPathNowOpen(link: IPath) =
        // A JOIN of a path_id whose PATH_CLOSE transaction has ended must clear the
        // session block (spec 6.10a). A path still in Closing is not reusable.
        session.NotePathJoined link.PathId |> ignore
        if session.PathState link.PathId <> PathCloseState.Closing then
            link.FrameLength <- session.FrameLength
            let path, ioPath =
                match io, channel with
                | Some group, Some rx ->
                    match link with
                    | :? PathLink as pl ->
                        let asyncPath = AsyncPathLink.TakeOver(pl, group, rx, txSlots)
                        asyncPath :> IPath, Some asyncPath
                    | _ -> link, None
                | _ -> link, None
            paths.Add path
            System.Threading.Interlocked.Increment(&pathVersion) |> ignore
            publishIds ()
            // Attach drains the bytes already read. Only after that may an IO
            // thread pick the path up and touch its receive buffer.
            path.Attach this
            match ioPath, io with
            | Some asyncPath, Some group -> group.Add asyncPath
            | _ -> ()
            if System.Threading.Volatile.Read(&closed) <> 0 then
                // Take the path out atomically and close it only if this thread actually took it:
                // `Close` may have drained the set and closed it already, and a `FindIndex` +
                // `RemoveAt` pair across snapshots could delist a *different* path instead.
                match paths.TakeIf(fun p -> obj.ReferenceEquals(p, path)) with
                | Some victim ->
                    System.Threading.Interlocked.Increment(&pathVersion) |> ignore
                    publishIds ()
                    pacers.Remove victim.PathId |> ignore
                    (try victim.Close() with _ -> ())
                | None -> ()

    member _.HasPath(pathId: uint16) =
        Array.contains pathId (System.Threading.Volatile.Read(&pathIds))
        || (pathOps |> Seq.exists (function PathAdd p -> p.PathId = pathId | _ -> false))

    /// Close and drop the path with pathId (replaced by a higher incarnation).
    member this.RemovePath(pathId: uint16) =
        if pathOpsInline then this.RemovePathNow pathId
        else this.PostPathOp(PathRemove pathId)

    member private this.RemovePathNow(pathId: uint16) =
        // After `Close` emptied the set there is nothing this removal could name, and the ops
        // that would apply it are claimed by their own posters.
        if System.Threading.Volatile.Read(&closed) <> 0 then ()
        else this.RemovePathOpen pathId

    member private this.RemovePathOpen(pathId: uint16) =
        // Decide and remove in one step: `FindIndex` then `.[i]` then `RemoveAt i` are three
        // snapshots, so a concurrent add/remove could delist a different path than the one about
        // to be closed. Whoever gets the object out of the set is its only owner.
        match paths.TakeIf(fun p -> p.PathId = pathId) with
        | Some victim ->
            System.Threading.Interlocked.Increment(&pathVersion) |> ignore
            publishIds ()
            pacers.Remove pathId |> ignore
            // Close outside the removal: `Close` may block in the socket. It cannot happen twice
            // — `TakeIf` already took this path out of the published set.
            victim.Close()
        | None -> ()

    /// Apply posted path changes on the thread that owns `paths`.
    member private this.ApplyPathOps() =
        let mutable op = Unchecked.defaultof<PathOp>
        while pathOps.TryDequeue(&op) do
            try
                match op with
                | PathAdd p -> this.AddPathNow p
                | PathRemove id -> this.RemovePathNow id
            finally
                System.Threading.Interlocked.Decrement(&pathOpsPending) |> ignore

    /// Close ops that will never be applied because the carrier is closed.
    ///
    /// `Close` and `PostPathOp` both call this, from their own threads. `ConcurrentQueue.TryDequeue`
    /// hands every op to exactly one drainer, so each `PathAdd` is closed exactly once and a
    /// `PathRemove` needs nothing. The counter is only a busy-wait hint after closure, so a
    /// transient negative is clamped rather than propagated into `PathOpsPending`.
    member private this.CloseOrphanOps() =
        let mutable op = Unchecked.defaultof<PathOp>
        while pathOps.TryDequeue(&op) do
            match op with
            | PathAdd p -> (try p.Close() with _ -> ())
            | PathRemove _ -> ()
            if System.Threading.Interlocked.Decrement(&pathOpsPending) < 0 then
                System.Threading.Interlocked.Exchange(&pathOpsPending, 0) |> ignore

    /// Record that the owning loop has returned (called by the loop itself).
    member _.NoteOwnerExit() = System.Threading.Volatile.Write(&ownerExited, 1)

    /// Record this thread as the owner of `paths` (first caller wins; a stopped loop never
    /// migrates to a new thread).
    member _.NoteOwnerThread() =
        if System.Threading.Volatile.Read(&ownerThread) = 0 then
            System.Threading.Interlocked.CompareExchange(&ownerThread, Environment.CurrentManagedThreadId, 0)
            |> ignore

    /// True once a close has been asked for and not yet performed.
    member _.CloseRequested = System.Threading.Volatile.Read(&closed) <> 0

    /// Ask for the close from any thread; returns at once.
    ///
    /// The owner loop performs it at the top of its next round (`ApplyPendingClose`), where no
    /// frame is in flight. A caller that has already joined that loop may close inline instead.
    member this.RequestClose() =
        System.Threading.Interlocked.Exchange(&closed, 1) |> ignore
        System.Threading.Volatile.Write(&closePending, 1)
        match this.WakePathOps with
        | Some wake -> wake ()
        | None -> ()

    /// Owner-thread hook: perform a close that another thread asked for.
    member this.ApplyPendingClose() =
        if System.Threading.Volatile.Read(&closed) <> 0
           && System.Threading.Interlocked.Exchange(&closePending, 0) = 1 then
            this.CloseNow()

    /// Close every path and local socket.
    ///
    /// OWNERSHIP: call from the thread that owns `paths` (`Watch`/`Service`), or after that
    /// thread has provably exited. From any other thread use `RequestClose` — this method then
    /// degrades to a request rather than releasing buffers under a live frame.
    member this.Close() =
        let owner = System.Threading.Volatile.Read(&ownerThread)
        if owner <> 0
           && owner <> Environment.CurrentManagedThreadId
           && System.Threading.Volatile.Read(&ownerExited) = 0 then
            this.RequestClose()
        else
            System.Threading.Volatile.Write(&closePending, 0)
            this.CloseNow()

    member this.Watch(poller: Poller) =
        this.NoteOwnerThread()
        this.ApplyPendingClose()
        this.ApplyPathOps()
        let sending =
            if not pace then session.WantsToSend
            elif session.ControlWaiting then true
            elif not session.WantsToSend then false
            else
                let fl = session.FrameLength
                group.Service(session.Clock.NowNs())
                groupAllows fl false false || groupAllows fl true false
        // Index a snapshot rather than `for link in paths`: a `for` over the published set binds
        // the struct enumerator, and if the compiler ever falls back to the boxed interface the
        // round loop would allocate — `BridgeTests` asserts zero bytes per round once warm, and
        // that assertion is the guard against per-frame garbage.
        let live = paths.ToArray()
        let mutable pi = 0
        while pi < live.Length do
            live.[pi].Watch(poller, sending)
            pi <- pi + 1
        // Always arm the local sockets. The old code skipped them while the bulk
        // queue was full (`CanFill` false). The session thread then woke only on
        // tunnel RX frames, so a download read about one cell per wake and ran at
        // 1.5 Mbit/s instead of 10. Read still backs off inside
        // TryBeginSourceFor, so arming a socket costs one WouldBlock, not a stall.
        let armed = bridge.Armed
        let mutable slot = armed.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s -> poller.WatchRead s
            slot <- armed.NextUp slot
        let writers = bridge.Writers
        let mutable slot = writers.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s -> poller.WatchWrite s
            slot <- writers.NextUp slot

    /// Handle what the last wait reported, then send.
    member this.Service(poller: Poller) =
        this.NoteOwnerThread()
        this.ApplyPendingClose()
        // One snapshot for the whole round. `let n = paths.Count` followed by `paths.[i]` is a
        // count from one array and an index into another: `Close` (Stop thread) drains the set and
        // `PathRefresher` removes paths, so the second read can be past the end — and an exception
        // here escapes into the coordinator loop and ends the session.
        let live = paths.ToArray()
        let n = live.Length
        let mutable rxBudget = 256
        let mutable more = false
        if n > 0 then
            let start = rxNext % n
            let mutable j = 0
            while j < n do
                let link = live.[(start + j) % n]
                match link with
                | :? PathLink as pl ->
                    if rxBudget > 0 then
                        if pl.ReceiveBudgeted(poller, &rxBudget) then more <- true
                    elif pl.HasBufferedFrame || poller.IsReadable pl.Socket then
                        more <- true
                | _ -> link.Receive poller
                j <- j + 1
            rxNext <- (start + 1) % n
        rxMore <- more
        let nowNs = session.Clock.NowNs()
        if nowNs - lastHalfOpenProbeNs >= 1_000_000_000L then
            lastHalfOpenProbeNs <- nowNs
            for link in paths do
                match link with
                | :? PathLink as pl -> pl.CheckAlive() |> ignore
                | _ -> ()
        prune ()
        refreshHealthAndRank nowNs
        let writers = bridge.Writers
        let mutable slot = writers.NextUp -1
        while slot >= 0 do
            slotVisits <- slotVisits + 1L
            match bridge.Local slot with
            | null -> ()
            | s -> if poller.IsWritable s then bridge.OnWritable slot
            slot <- writers.NextUp slot
        // Deliveries staged while the RxChannel was drained go out now.
        bridge.Flush()
        bridge.Settle()
        // Interactive flows read first (spec 16.3 Q-3: bulk input after the interactive queue).
        hot.Clear()
        readPass poller true
        readPass poller false
        session.Flush()
        session.Tick()
        if pace then
            primeSample ()
            group.Service(session.Clock.NowNs())
        writeRound ()
        bridge.Flush()
        bridge.Settle()
        prune ()

    /// Close every path and local socket.
    ///
    /// Order, and why: raise `closed`, then take the snapshot and clear it, publish the empty
    /// id set, and only then close the sockets. A concurrent `AddPath` either landed before the
    /// snapshot (so it is in it and closed here) or observes `closed` and closes its own path.
    /// Clearing before closing keeps a late add from being both listed and closed twice, and the
    /// snapshot is a plain array, so no reader is enumerating a collection that is being changed.
    member this.CloseNow() =
        // Exactly once, even if two threads both believe they may close.
        //
        // The owner loop performs a requested close on its way out, and a caller that has
        // observed the owner's exit may close inline; both can reach here for the same carrier.
        // `DrainAll` already hands each path to one thread, but the rest of the body is not
        // idempotent (`bridge.CloseAll`, the orphan sweep, the id publish), and running it twice
        // concurrently would close the local sockets twice. One exchange makes the whole body the
        // body of a single event.
        if System.Threading.Interlocked.Exchange(&closeDone, 1) = 1 then ()
        else this.CloseEverything()

    /// The body of the one close. Reached from exactly one thread (see `CloseNow`).
    member private this.CloseEverything() =
        System.Threading.Interlocked.Exchange(&closed, 1) |> ignore
        // Take the whole set atomically. A snapshot followed by `Clear` would let a concurrent
        // add land inside the snapshot (so this thread closes it) *and* survive the clear (so
        // `AddPathNowOpen`'s recheck finds it and closes it too) — a double close on one socket.
        // After `DrainAll` each path has exactly one owner: whoever took it.
        let closing = paths.DrainAll()
        System.Threading.Interlocked.Increment(&pathVersion) |> ignore
        publishIds ()
        for link in closing do
            (try link.Close() with _ -> ())
        this.CloseOrphanOps()
        bridge.CloseAll()

    interface IPathOwner with
        member _.OnFrame(path, frame) =
            framesIn <- framesIn + 1L
            session.OnFrame(path, frame)
        member _.Frontier = session.Frontier
        member _.ProduceFill(path, frame) = session.ProduceFill(path, frame)
        member _.Fail(reason) = session.Fail reason
