/// Data-frame sealing and opening (spec 6.3, 6.4, 6.5.6, 6.6) for suite 0x02,
/// ChaCha20-Poly1305 (RFC 8439), own non-throwing implementation. One instance per direction per end; single owner thread.
///
/// Frame: chunk_hdr(5) | masked cell_seq(8) | AEAD(meta 40 | symbol S) | tag(16) | CRLF(2).
/// Nonce (96 bits): dir | BE16 path_id | 0 | BE64 cell_seq.
/// AAD (18 bytes): chunk_hdr(5) | BE64 cell_seq | BE16 path_id | dir | BE16 L.
namespace Tbt.Crypto

open System
open System.Buffers.Binary
open Tbt.Contracts

/// Counter-interval key table: at most two generations (spec 6.5.6). A counter selects
/// exactly one key; there is no trial decryption.
[<Sealed>]
type KeyTable() =
    let gens: uint32[] = Array.zeroCreate 2
    let starts: uint64[] = Array.zeroCreate 2
    let aeads: ChaCha20Poly1305Aead array = Array.zeroCreate 2
    let mutable count = 0

    member _.Count = count
    member _.NewestGeneration = if count = 0 then invalidOp "no key" else gens.[count - 1]

    /// Install generation gen for counters >= fromCounter. The first key must be gen 0
    /// from counter 0; later keys must be the next generation with a larger start.
    member _.Install(gen: uint32, key: ReadOnlySpan<byte>, fromCounter: uint64) =
        if count = 2 then invalidOp "two generations already live"
        if count = 0 && (gen <> 0u || fromCounter <> 0UL) then invalidArg "gen" "first key is generation 0 from counter 0"
        if count = 1 && (gen <> gens.[0] + 1u || fromCounter <= starts.[0]) then invalidArg "gen" "next generation expected"
        gens.[count] <- gen
        starts.[count] <- fromCounter
        aeads.[count] <- ChaCha20Poly1305Aead key
        count <- count + 1

    /// Remove the older of two generations (KEY_RETIRE).
    member _.RetireOldest() =
        if count <> 2 then invalidOp "nothing to retire"
        aeads.[0].Wipe()
        gens.[0] <- gens.[1]
        starts.[0] <- starts.[1]
        aeads.[0] <- aeads.[1]
        aeads.[1] <- Unchecked.defaultof<_>
        count <- 1

    /// The key for counter c, or null when c is outside every interval.
    member _.Select(c: uint64) : ChaCha20Poly1305Aead | null =
        if count = 2 && c >= starts.[1] then aeads.[1]
        elif count >= 1 && c >= starts.[0] then aeads.[0]
        else null

module private Frame =
    let writeNonce (dir: Direction) (path: uint16) (counter: uint64) (nonce: Span<byte>) =
        nonce.[0] <- byte dir
        BinaryPrimitives.WriteUInt16BigEndian(nonce.Slice 1, path)
        nonce.[3] <- 0uy
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice 4, counter)

    let writeAad (frame: ReadOnlySpan<byte>) (dir: Direction) (path: uint16) (counter: uint64) (aad: Span<byte>) =
        frame.Slice(0, Layout.ChunkHeaderLength).CopyTo aad
        BinaryPrimitives.WriteUInt64BigEndian(aad.Slice 5, counter)
        BinaryPrimitives.WriteUInt16BigEndian(aad.Slice 13, path)
        aad.[15] <- byte dir
        BinaryPrimitives.WriteUInt16BigEndian(aad.Slice 16, uint16 frame.Length)

    let inline readOnly (s: Span<byte>) : ReadOnlySpan<byte> = Span<byte>.op_Implicit s

/// Seals data frames of one direction.
[<Sealed>]
type CellSealer(dir: Direction, symbolSize: int, keys: KeyTable, mask: IHeaderMask) =
    let nonce: byte[] = Array.zeroCreate 12
    let aad: byte[] = Array.zeroCreate 18
    let hp: byte[] = Array.zeroCreate 8
    let frameLength = Layout.frameLength symbolSize

    member _.Keys = keys

    /// Encrypt frame in place. The caller wrote chunk header, meta, symbol and CRLF, and
    /// owns the rule that one counter seals one plaintext only (spec 6.4.2).
    member _.Seal(frame: Span<byte>, counter: uint64, path: uint16) =
        if frame.Length <> frameLength then invalidArg "frame" "bad frame length"
        match keys.Select counter with
        | null -> invalidOp "counter outside every key interval"
        | aead ->
            Frame.writeNonce dir path counter (Span nonce)
            Frame.writeAad (Frame.readOnly frame) dir path counter (Span aad)
            let body = frame.Slice(Layout.MetaOffset, Layout.MetaLength + symbolSize)
            aead.Encrypt(ReadOnlySpan nonce, Frame.readOnly body, body,
                         frame.Slice(Layout.tagOffset symbolSize, Layout.TagLength), ReadOnlySpan aad)
            mask.Compute(Frame.readOnly (frame.Slice(Layout.MetaOffset, 16)), Span hp)
            let seq = frame.Slice(Layout.CellSeqOffset, Layout.CellSeqLength)
            BinaryPrimitives.WriteUInt64BigEndian(seq, counter)
            for i in 0 .. 7 do seq.[i] <- seq.[i] ^^^ hp.[i]

/// Opens data frames of one direction; owns the replay window of that direction.
[<Sealed>]
type CellOpener(dir: Direction, symbolSize: int, keys: KeyTable, mask: IHeaderMask) =
    let nonce: byte[] = Array.zeroCreate 12
    let aad: byte[] = Array.zeroCreate 18
    let hp: byte[] = Array.zeroCreate 8
    let replay = ReplayWindow()
    let frameLength = Layout.frameLength symbolSize
    let mutable replayDrops = 0L
    let mutable authFails = 0L

    member _.Keys = keys
    member _.ReplayDrops = replayDrops
    member _.AuthFails = authFails

    /// Recover cell_seq without changing any state (receive pipeline step 3).
    member _.PeekCounter(frame: ReadOnlySpan<byte>) =
        mask.Compute(frame.Slice(Layout.MetaOffset, 16), Span hp)
        let mutable c = 0UL
        for i in 0 .. 7 do
            c <- (c <<< 8) ||| uint64 (frame.[Layout.CellSeqOffset + i] ^^^ hp.[i])
        c

    /// Decrypt frame in place (pipeline steps 3..5). On success the counter is returned and
    /// the replay window moves; on failure nothing changes and the frame is not plaintext.
    /// The caller checked framing (step 2) before this call.
    member this.TryOpen(frame: Span<byte>, path: uint16, counter: byref<uint64>) =
        if frame.Length <> frameLength then false
        else
            let c = this.PeekCounter(Frame.readOnly frame)
            if not (replay.Check c) then
                replayDrops <- replayDrops + 1L
                false
            else
                match keys.Select c with
                | null ->
                    authFails <- authFails + 1L
                    false
                | aead ->
                    Frame.writeNonce dir path c (Span nonce)
                    Frame.writeAad (Frame.readOnly frame) dir path c (Span aad)
                    let body = frame.Slice(Layout.MetaOffset, Layout.MetaLength + symbolSize)
                    let tag = frame.Slice(Layout.tagOffset symbolSize, Layout.TagLength)
                    if aead.TryDecrypt(ReadOnlySpan nonce, Frame.readOnly body, Frame.readOnly tag, body, ReadOnlySpan aad) then
                        replay.Commit c
                        counter <- c
                        true
                    else
                        authFails <- authFails + 1L
                        false
