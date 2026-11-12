/// Frame length negotiation, chunk header, 64-bit sequence extension and frame position
/// (spec 3.1, 4.4, 6.1, 6.15).
module Tbt.Wire.FrameMath

open System
open Tbt.Contracts

/// SACK headroom kept below the MSS (spec 3.1).
[<Literal>]
let SackHeadroom = 32

/// Symbol length for an effective MSS: S = 64 * floor((MSS - 32 - 71) / 64), clamped to
/// 1024..1344. A clamped-up S does not fit one segment; see fitsOneSegment.
let symbolForMss (mssEff: int) =
    let raw = 64 * ((mssEff - SackHeadroom - Layout.FrameOverhead) / 64)
    Math.Clamp(raw, Layout.MinSymbol, Layout.MaxSymbol)

/// True when one frame of symbol length s plus SACK headroom fits into one segment.
let fitsOneSegment (s: int) (mss: int) = Layout.frameLength s + SackHeadroom <= mss

/// Write the chunk header for symbol length s ("580\r\n" for S = 1344). The chunk
/// carries L - 7 = S + 64 bytes. Hex digits are lower case.
let writeChunkHeader (s: int) (dst: Span<byte>) =
    let v = s + 64
    let digit (d: int) = if d < 10 then byte (48 + d) else byte (87 + d)
    dst.[0] <- digit ((v >>> 8) &&& 15)
    dst.[1] <- digit ((v >>> 4) &&& 15)
    dst.[2] <- digit (v &&& 15)
    dst.[3] <- 13uy
    dst.[4] <- 10uy

let private hexValue (b: byte) =
    if b >= 48uy && b <= 57uy then int b - 48
    elif b >= 97uy && b <= 102uy then int b - 87
    elif b >= 65uy && b <= 70uy then int b - 55
    else -1

/// Check the plaintext framing of one data frame of symbol length s: the chunk header
/// value (either hex case) and the trailing CRLF.
let checkFraming (s: int) (frame: ReadOnlySpan<byte>) =
    let l = Layout.frameLength s
    if frame.Length <> l then false
    else
        let a = hexValue frame.[0]
        let b = hexValue frame.[1]
        let c = hexValue frame.[2]
        a >= 0 && b >= 0 && c >= 0
        && ((a <<< 8) ||| (b <<< 4) ||| c) = s + 64
        && frame.[3] = 13uy && frame.[4] = 10uy
        && frame.[l - 2] = 13uy && frame.[l - 1] = 10uy

/// Extend a 32-bit TCP sequence number next to a 64-bit base (spec 4.4, 6.15).
/// ValueNone when the distance is 2^31 or more (half-space boundary: no guess).
let extendSeq (base64: uint64) (seg32: uint32) =
    let d = int32 (seg32 - uint32 base64)
    if d = Int32.MinValue then ValueNone
    else
        let r = int64 base64 + int64 d
        if r < 0L then ValueNone else ValueSome(uint64 r)

/// Frame index and offset of byte position seg64 in a stream whose data frames start at
/// seqData0. ValueNone before the first data frame.
let framePosition (seqData0: uint64) (frameLength: int) (seg64: uint64) =
    if seg64 < seqData0 then ValueNone
    else
        let d = seg64 - seqData0
        ValueSome(struct (d / uint64 frameLength, int (d % uint64 frameLength)))

/// Split a byte hole [lo, hi) into a left edge, whole frames [iLo, iHi) and a right edge
/// (spec 6.7.2). Returns struct (iLo, iHi); iLo > iHi never happens, iLo = iHi means no
/// whole frame. Edges are [lo, seqData0 + iLo * L) and [seqData0 + iHi * L, hi).
let wholeFrames (seqData0: uint64) (frameLength: int) (lo: uint64) (hi: uint64) =
    if lo < seqData0 || hi < lo then invalidArg "lo" "hole outside the data area"
    let l = uint64 frameLength
    let iLo = (lo - seqData0 + l - 1UL) / l
    let iHi = (hi - seqData0) / l
    if iHi < iLo then struct (iLo, iLo) else struct (iLo, iHi)
