/// IPv4 and TCP headers for the user-space TCP on TUN (RFC 791, 793, 7323, 2018).
/// Parse in place, build in place; no allocation.
module Tbt.Carrier.Packet

open System
open System.Buffers.Binary

[<Literal>]
let Fin = 0x01uy

[<Literal>]
let Syn = 0x02uy

[<Literal>]
let Rst = 0x04uy

[<Literal>]
let Psh = 0x08uy

[<Literal>]
let Ack = 0x10uy

/// Offset of the payload in a segment this end builds without TCP options.
[<Literal>]
let DataOffset = 40

[<Struct>]
type Segment =
    { SrcIp: uint32
      DstIp: uint32
      SrcPort: uint16
      DstPort: uint16
      Seq: uint32
      AckNo: uint32
      Flags: byte
      Window: uint16
      OptionsAt: int
      OptionsLength: int
      PayloadAt: int
      PayloadLength: int }

/// One's complement sum of data as big-endian 16-bit words, added to sum.
let sum (acc: uint64) (data: ReadOnlySpan<byte>) =
    let mutable s = acc
    let mutable i = 0
    while i + 4 <= data.Length do
        s <- s + uint64 (BinaryPrimitives.ReadUInt32BigEndian(data.Slice i))
        i <- i + 4
    if i + 2 <= data.Length then
        s <- s + uint64 (BinaryPrimitives.ReadUInt16BigEndian(data.Slice i))
        i <- i + 2
    if i < data.Length then s <- s + (uint64 data.[i] <<< 8)
    s

let fold (acc: uint64) =
    let mutable s = acc
    while s > 0xFFFFUL do s <- (s &&& 0xFFFFUL) + (s >>> 16)
    uint16 (~~~(uint16 s))

let private pseudo (src: uint32) (dst: uint32) (tcpLength: int) =
    uint64 (src >>> 16) + uint64 (src &&& 0xFFFFu) + uint64 (dst >>> 16) + uint64 (dst &&& 0xFFFFu)
    + 6UL + uint64 tcpLength

/// Parse an IPv4 TCP packet with valid checksums and no fragments.
let tryParse (p: ReadOnlySpan<byte>) (seg: byref<Segment>) =
    if p.Length < 40 || p.[0] >>> 4 <> 4uy || p.[9] <> 6uy then false
    else
        let ihl = int (p.[0] &&& 15uy) * 4
        let total = int (BinaryPrimitives.ReadUInt16BigEndian(p.Slice 2))
        let frag = BinaryPrimitives.ReadUInt16BigEndian(p.Slice 6)
        if ihl < 20 || total > p.Length || total < ihl + 20 || frag &&& 0x3FFFus <> 0us then false
        elif fold (sum 0UL (p.Slice(0, ihl))) <> 0us then false
        else
            let t = p.Slice(ihl, total - ihl)
            let off = int (t.[12] >>> 4) * 4
            let src = BinaryPrimitives.ReadUInt32BigEndian(p.Slice 12)
            let dst = BinaryPrimitives.ReadUInt32BigEndian(p.Slice 16)
            if off < 20 || off > t.Length then false
            elif fold (sum (pseudo src dst t.Length) t) <> 0us then false
            else
                seg <- { SrcIp = src; DstIp = dst
                         SrcPort = BinaryPrimitives.ReadUInt16BigEndian t
                         DstPort = BinaryPrimitives.ReadUInt16BigEndian(t.Slice 2)
                         Seq = BinaryPrimitives.ReadUInt32BigEndian(t.Slice 4)
                         AckNo = BinaryPrimitives.ReadUInt32BigEndian(t.Slice 8)
                         Flags = t.[13]
                         Window = BinaryPrimitives.ReadUInt16BigEndian(t.Slice 14)
                         OptionsAt = ihl + 20; OptionsLength = off - 20
                         PayloadAt = ihl + off; PayloadLength = t.Length - off }
                true

/// SYN options: MSS (0 when absent), window scale (-1 when absent), SACK permitted.
let synOptions (o: ReadOnlySpan<byte>) (mss: byref<int>) (wscale: byref<int>) (sackOk: byref<bool>) =
    mss <- 0
    wscale <- -1
    sackOk <- false
    let mutable i = 0
    let mutable go = true
    while go && i < o.Length do
        match o.[i] with
        | 0uy -> go <- false
        | 1uy -> i <- i + 1
        | kind ->
            if i + 1 >= o.Length || int o.[i + 1] < 2 || i + int o.[i + 1] > o.Length then go <- false
            else
                let len = int o.[i + 1]
                if kind = 2uy && len = 4 then mss <- int (BinaryPrimitives.ReadUInt16BigEndian(o.Slice(i + 2)))
                elif kind = 3uy && len = 3 then wscale <- min 14 (int o.[i + 2])
                elif kind = 4uy && len = 2 then sackOk <- true
                i <- i + len

/// The SACK option (kind 5) in o: offset of its first block and the block count.
let sackBlocks (o: ReadOnlySpan<byte>) (at: byref<int>) =
    let mutable n = 0
    let mutable i = 0
    let mutable go = true
    while go && i < o.Length do
        match o.[i] with
        | 0uy -> go <- false
        | 1uy -> i <- i + 1
        | kind ->
            if i + 1 >= o.Length || int o.[i + 1] < 2 || i + int o.[i + 1] > o.Length then go <- false
            else
                let len = int o.[i + 1]
                if kind = 5uy && (len - 2) % 8 = 0 then
                    at <- i + 2
                    n <- (len - 2) / 8
                    go <- false
                i <- i + len
    n

/// Write IPv4 + TCP headers in front of a payload already at 40 + options.Length and
/// return the packet length.
let build (p: Span<byte>) (src: uint32) (dst: uint32) (sport: uint16) (dport: uint16) (seq: uint32) (ack: uint32)
          (flags: byte) (window: uint16) (options: ReadOnlySpan<byte>) (payload: int) (ipId: uint16) =
    let tcpLength = 20 + options.Length + payload
    let total = 20 + tcpLength
    p.[0] <- 0x45uy
    p.[1] <- 0uy
    BinaryPrimitives.WriteUInt16BigEndian(p.Slice 2, uint16 total)
    BinaryPrimitives.WriteUInt16BigEndian(p.Slice 4, ipId)
    BinaryPrimitives.WriteUInt16BigEndian(p.Slice 6, 0x4000us)
    p.[8] <- 64uy
    p.[9] <- 6uy
    BinaryPrimitives.WriteUInt16BigEndian(p.Slice 10, 0us)
    BinaryPrimitives.WriteUInt32BigEndian(p.Slice 12, src)
    BinaryPrimitives.WriteUInt32BigEndian(p.Slice 16, dst)
    BinaryPrimitives.WriteUInt16BigEndian(p.Slice 10, fold (sum 0UL (Span<byte>.op_Implicit (p.Slice(0, 20)))))
    let t = p.Slice(20, tcpLength)
    BinaryPrimitives.WriteUInt16BigEndian(t, sport)
    BinaryPrimitives.WriteUInt16BigEndian(t.Slice 2, dport)
    BinaryPrimitives.WriteUInt32BigEndian(t.Slice 4, seq)
    BinaryPrimitives.WriteUInt32BigEndian(t.Slice 8, ack)
    t.[12] <- byte ((20 + options.Length) / 4 <<< 4)
    t.[13] <- flags
    BinaryPrimitives.WriteUInt16BigEndian(t.Slice 14, window)
    BinaryPrimitives.WriteUInt16BigEndian(t.Slice 16, 0us)
    BinaryPrimitives.WriteUInt16BigEndian(t.Slice 18, 0us)
    options.CopyTo(t.Slice 20)
    BinaryPrimitives.WriteUInt16BigEndian(t.Slice 16, fold (sum (pseudo src dst tcpLength) (Span<byte>.op_Implicit t)))
    total
