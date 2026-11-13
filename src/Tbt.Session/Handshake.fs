/// Main handshake and path join at the session level (spec 6.1, 6.5.2-6.5.7, 6.5.9):
/// what each end sends and checks. Byte formats live in Tbt.Crypto.
module Tbt.Session.Handshake

open System
open System.Collections.Generic
open Tbt.Contracts
open Tbt.Crypto
open Tbt.Crypto.Bootstrap
open Tbt.Wire

/// Capabilities this implementation offers (DECISIONS.md D4, D5: no lanes, no FLOWSTATE).
let SupportedCaps = Caps.HpMask ||| Caps.StraddleOk ||| Caps.JoinAck2 ||| Caps.FlowState ||| Caps.FlowState2

/// FLOWSTATE counts only together with FLOWSTATE2 (DECISIONS.md D5).
let private flowStatePair (caps: uint16) =
    let both = Caps.FlowState ||| Caps.FlowState2
    if caps &&& both = both then caps else caps &&& ~~~both

/// The HTTP head of a path (spec 6.1). It ends with CRLF CRLF and is at most 1024 bytes.
let requestHead (host: string) =
    Kdf.ascii ("POST /upload HTTP/1.1\r\nHost: " + host
               + "\r\nContent-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\n\r\n")

let responseHead =
    Kdf.ascii "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\n\r\n"

[<Literal>]
let MaxHeadLength = 1024

let private random (entropy: IEntropy) (n: int) =
    let b = Array.zeroCreate<byte> n
    entropy.Fill(Span b)
    b

/// Client side of the main handshake on path 0. The CLIENT_INIT is rendered once (frozen).
[<Sealed>]
type ClientOffer(psk: byte[], clientId: Guid, entropy: IEntropy, unixTimeMs: uint64, mssClient: int, maxPaths: int) =
    let clientNonce = random entropy 32
    let hello =
        { Version = 2uy; Suites = Suite.ChaCha20Poly1305Bit; Caps = SupportedCaps; ClientId = clientId
          UnixTimeMs = unixTimeMs; MssClient = uint16 mssClient; MaxPaths = uint16 maxPaths }
    let content =
        ClientInit.build (ReadOnlySpan psk) (ReadOnlySpan clientNonce) hello ReadOnlySpan<byte>.Empty
                         (ReadOnlySpan(random entropy (ClientInit.BodyLength - 64)))

    member _.Frame = Bootstrap.frame (ReadOnlySpan content)
    member _.Hello = hello

    /// Check SERVER_INIT and derive the session keys. ValueNone fails the handshake.
    member _.TryAccept(frame: ReadOnlySpan<byte>) =
        let si = Bootstrap.content frame
        if si.IsEmpty then ValueNone
        else
            match ServerInit.tryOpen (ReadOnlySpan psk) (ReadOnlySpan clientNonce) si with
            | ValueSome h when h.Status = ServerStatus.Ok && h.Suite = Suite.ChaCha20Poly1305 && h.PathId = 0us
                               && Layout.isLegalSymbol (int h.Symbol) && h.Caps &&& ~~~SupportedCaps = 0us
                               && h.Caps &&& Caps.JoinAck2 <> 0us ->
                let keys =
                    SessionKeys(psk, clientNonce, si.Slice(0, 32).ToArray(), h.SessionId, clientId, h.Suite,
                                Layout.frameLength (int h.Symbol), content, si.ToArray())
                ValueSome(struct ({ Keys = keys; Symbol = int h.Symbol; Caps = h.Caps; Grant0 = h.RxWinGrant0 }, h))
            | _ -> ValueNone

/// Recent client nonces per PSK inside the +-120 s window (spec 6.5.3), at most 4096.
[<Sealed>]
type ReplayCache() =
    let seen = HashSet<UInt128>()
    let order = Queue<struct (int64 * UInt128)>()
    let limit = 4096

    /// False on a replay or a full table (no new session then).
    member _.TryAdd(nonce: ReadOnlySpan<byte>, nowMs: int64) =
        while order.Count > 0 && (let struct (exp, _) = order.Peek() in exp < nowMs) do
            let struct (_, k) = order.Dequeue()
            seen.Remove k |> ignore
        let key = Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian nonce
        if seen.Count >= limit || seen.Contains key then false
        else
            seen.Add key |> ignore
            order.Enqueue(struct (nowMs + 250_000L, key))
            true

/// What the server learned from an accepted CLIENT_INIT.
[<Struct>]
type ServerAccept =
    { Params: SessionParams
      Hello: ClientHello
      /// SERVER_INIT bootstrap frame to send after the HTTP head.
      Reply: byte[] }

/// Server side of the main handshake. psks: the configured client keys.
[<Sealed>]
type ServerOffer(psks: byte[][], entropy: IEntropy, grant0: uint16, maxPaths: int) =
    let caches = Array.init psks.Length (fun _ -> ReplayCache())

    /// Try the bootstrap frame as CLIENT_INIT (spec 6.5.9 step 1). ValueNone: not ours, or
    /// rejected; the caller stays silent.
    member _.TryAccept(frame: ReadOnlySpan<byte>, unixTimeMs: int64, mssServer: int) =
        let ci = Bootstrap.content frame
        if ci.IsEmpty then ValueNone
        else
            let cn = ClientInit.clientNonce ci
            let mutable result = ValueNone
            let mutable i = 0
            while result.IsNone && i < psks.Length do
                let psk = psks.[i]
                if (ReadOnlySpan(keyHint (ReadOnlySpan psk) cn)).SequenceEqual(ClientInit.hint ci) then
                    match ClientInit.tryOpen (ReadOnlySpan psk) ci with
                    | ValueSome(h, _) when abs (int64 h.UnixTimeMs - unixTimeMs) <= 120_000L
                                           && h.Suites &&& Suite.ChaCha20Poly1305Bit <> 0uy
                                           && h.Caps &&& Caps.JoinAck2 <> 0us && h.MssClient > 0us
                                           && caches.[i].TryAdd(cn, unixTimeMs) ->
                        let symbol = FrameMath.symbolForMss (min (int h.MssClient) mssServer)
                        let caps = flowStatePair (h.Caps &&& SupportedCaps)
                        let sid = Guid(ReadOnlySpan(random entropy 16), true)
                        let sh =
                            { Status = ServerStatus.Ok; Suite = Suite.ChaCha20Poly1305; Caps = caps; SessionId = sid
                              Symbol = uint16 symbol; PathId = 0us; MaxPaths = uint16 (min maxPaths (int h.MaxPaths))
                              RxWinGrant0 = grant0; EchoTimeMs = h.UnixTimeMs; Cookie = UInt128.Zero }
                        let sn = random entropy 32
                        let si = ServerInit.build (ReadOnlySpan psk) cn (ReadOnlySpan sn) sh ReadOnlySpan<byte>.Empty
                                                  (ReadOnlySpan(random entropy (ServerInit.BodyLength - 84)))
                        let keys = SessionKeys(psk, cn.ToArray(), sn, sid, h.ClientId, sh.Suite, Layout.frameLength symbol,
                                               ci.ToArray(), si)
                        result <- ValueSome
                            { Params = { Keys = keys; Symbol = symbol; Caps = caps; Grant0 = grant0 }
                              Hello = h
                              Reply = Bootstrap.frame (ReadOnlySpan si) }
                    | _ -> ()
                i <- i + 1
            result

/// Client side of one path join (spec 6.5.7). The JOIN is rendered once (frozen).
[<Sealed>]
type JoinOffer(kPath: byte[], entropy: IEntropy, sessionId: Guid, symbol: int, caps: uint16, maxPaths: int,
               pathId: uint16, incarnation: uint32, mssClient: int) =
    let nonce = random entropy 16
    let request: Join.JoinRequest = { PathId = pathId; Incarnation = incarnation; MssClient = uint16 mssClient; Role = Join.JoinRole.Bulk }
    let wire = Join.buildRequest (ReadOnlySpan kPath) (ReadOnlySpan nonce) request (ReadOnlySpan(random entropy 1031))
    let jth = Join.transcriptHash (ReadOnlySpan wire)

    member _.Frame = wire
    member _.PathId = pathId

    /// Open a JOIN_ACK for this request whatever its status (ValueNone: not ours or broken).
    member _.TryOpen(frame: ReadOnlySpan<byte>) =
        Join.tryOpenAck (ReadOnlySpan kPath) (ReadOnlySpan nonce) (ReadOnlySpan jth) frame

    /// Check JOIN_ACK field by field (spec 6.5.7.3). ValueNone closes the new path.
    member _.TryAccept(frame: ReadOnlySpan<byte>) =
        match Join.tryOpenAck (ReadOnlySpan kPath) (ReadOnlySpan nonce) (ReadOnlySpan jth) frame with
        | ValueSome a when a.Status = Join.JoinStatus.Accept && a.Role = request.Role && a.Suite = Suite.ChaCha20Poly1305
                           && a.SessionId = sessionId && a.PathId = pathId && a.Incarnation = incarnation
                           && int a.Symbol = symbol && int a.MaxPaths = maxPaths && a.Caps = caps
                           && a.MssClient = request.MssClient && a.MssServer > 0us -> ValueSome a
        | _ -> ValueNone

/// Server side of a path join: does this bootstrap frame carry a JOIN for kPath?
let tryJoin (kPath: byte[]) (frame: ReadOnlySpan<byte>) =
    let c = Bootstrap.content frame
    if c.IsEmpty then ValueNone
    elif not ((ReadOnlySpan(Join.joinTag (ReadOnlySpan kPath) (c.Slice(0, 16)))).SequenceEqual(c.Slice(16, 8))) then ValueNone
    else Join.tryOpenRequest (ReadOnlySpan kPath) frame

/// Server: the JOIN_ACK for an accepted request.
let joinAck (kPath: byte[]) (entropy: IEntropy) (sessionId: Guid) (symbol: int) (caps: uint16) (maxPaths: int)
            (joinFrame: ReadOnlySpan<byte>) (r: Join.JoinRequest) (status: Join.JoinStatus) (mssServer: int) =
    let ack: Join.JoinAck =
        { Status = status; Role = r.Role; Suite = Suite.ChaCha20Poly1305; SessionId = sessionId
          PathId = r.PathId; Incarnation = r.Incarnation; Symbol = uint16 symbol; MaxPaths = uint16 maxPaths
          Caps = caps; MssClient = r.MssClient; MssServer = uint16 mssServer }
    Join.buildAck (ReadOnlySpan kPath) (Bootstrap.content(joinFrame).Slice(0, 16)) (ReadOnlySpan(Join.transcriptHash joinFrame))
                  ack (ReadOnlySpan(random entropy 968))

/// What the server does with one authenticated JOIN (spec 6.5.7.4).
type JoinVerdict =
    /// Close the new connection; no bytes.
    | Close = 0
    /// New decision ACCEPT: send Ack, then register the path (replacing its old incarnation).
    | Accept = 1
    /// New decision with a refusal status: send Ack, then close the new connection.
    | Reject = 2
    /// The same request again: send the frozen Ack bytes of its decision, then close; never activate.
    | Replay = 3

[<Struct>]
type JoinDecision =
    { Verdict: JoinVerdict
      /// The frozen JOIN_ACK frame (empty for Close).
      Ack: byte[]
      Request: Join.JoinRequest }

/// Server admission of path joins for one session (spec 6.5.7.4); single owner (the server loop).
/// Per path: highest_seen and active incarnation until the session ends. Each decision is
/// recorded with its JTH and nonce and its JOIN_ACK is rendered exactly once; a repeat of the
/// same request only replays those bytes (a re-render would put new padding under the same key
/// and zero nonce: AEAD nonce reuse). Bounded: 1024 decision digests, 288 frozen ACKs kept for
/// FrozenNs (a proxy for the ACK's TCP delivery); when no slot is free, new admissions are refused without any state change, and
/// nothing that still holds a frozen ACK is forgotten.
[<Sealed>]
type JoinAdmission(kPath: byte[], entropy: IEntropy, clock: IClock, sessionId: Guid, symbol: int, caps: uint16,
                   maxPaths: int, mssServer: int, idLimit: int) =
    static let pathLimit = 288
    static let recordLimit = 1024
    static let frozenLimit = 288
    let highest = Array.zeroCreate<uint32> pathLimit
    let active = Array.zeroCreate<uint32> pathLimit
    let records = Collections.Generic.Dictionary<string, struct (int64 * byte[])>()
    let nonces = Collections.Generic.Dictionary<string, string>()
    let order = Collections.Generic.Queue<struct (string * string)>()
    let mutable frozen = 0
    let mutable refused = 0L

    /// How long a frozen JOIN_ACK is kept for replay (bounds the original TCP delivery).
    static member FrozenNs = 30_000_000_000L

    member _.Highest(path: uint16) = highest.[int path]
    member _.Active(path: uint16) = active.[int path]
    /// New admissions refused because the decision cache had no free slot.
    member _.Refused = refused
    member _.FrozenCount = frozen
    member _.Records = records.Count

    member private _.Expire(now: int64) =
        // Frozen bytes past their time keep only the digest (spec 6.5.7.4 rule 8).
        if frozen > 0 then
            for struct (jth, _) in order do
                match records.TryGetValue jth with
                | true, struct (at, ack) when ack.Length > 0 && now - at >= JoinAdmission.FrozenNs ->
                    records.[jth] <- struct (at, Array.Empty<byte>())
                    frozen <- frozen - 1
                | _ -> ()

    /// Decide on an authenticated JOIN frame with request r. activePaths: paths the session
    /// serves now; pathAlive: whether r's path id is among them; closing: the session closes.
    member this.Decide(frame: ReadOnlySpan<byte>, r: Join.JoinRequest, activePaths: int, pathAlive: bool, closing: bool) =
        let close = { Verdict = JoinVerdict.Close; Ack = Array.Empty<byte>(); Request = r }
        let p = int r.PathId
        if p >= pathLimit then close
        else
            let now = clock.NowNs()
            this.Expire now
            let jthBytes = Join.transcriptHash frame
            let jth = Convert.ToHexString jthBytes
            let nonce = Convert.ToHexString(Bootstrap.content(frame).Slice(0, 16))
            match records.TryGetValue jth with
            | true, struct (_, ack) ->
                if ack.Length = 0 then close else { Verdict = JoinVerdict.Replay; Ack = ack; Request = r }
            | _ ->
                if nonces.ContainsKey nonce || r.Incarnation = 0u || r.Incarnation <= highest.[p] then close
                else
                    // Reserve a frozen-ACK buffer and a digest slot first (rule 3).
                    let mutable room = frozen < frozenLimit
                    while room && records.Count >= recordLimit do
                        let struct (oldJth, oldNonce) = order.Peek()
                        let struct (_, ack) = records.[oldJth]
                        if ack.Length = 0 then
                            order.Dequeue() |> ignore
                            records.Remove oldJth |> ignore
                            nonces.Remove oldNonce |> ignore
                        else room <- false
                    if not room then
                        refused <- refused + 1L
                        close
                    else
                        let status =
                            // No LANE support: role 1 is invalid. idLimit: path ids this
                            // session's tables hold (SessionConfig.MaxPaths).
                            if r.Role <> Join.JoinRole.Bulk || p >= 256 || p >= idLimit then Join.JoinStatus.InvalidRequest
                            elif closing then Join.JoinStatus.SessionClosing
                            elif not pathAlive && activePaths >= maxPaths then Join.JoinStatus.Capacity
                            else Join.JoinStatus.Accept
                        highest.[p] <- r.Incarnation
                        if status = Join.JoinStatus.Accept then active.[p] <- r.Incarnation
                        let ack = joinAck kPath entropy sessionId symbol caps maxPaths frame r status mssServer
                        records.[jth] <- struct (now, ack)
                        nonces.[nonce] <- jth
                        order.Enqueue(struct (jth, nonce))
                        frozen <- frozen + 1
                        { Verdict = (if status = Join.JoinStatus.Accept then JoinVerdict.Accept else JoinVerdict.Reject)
                          Ack = ack; Request = r }
