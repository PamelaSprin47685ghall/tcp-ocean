/// The server's user-space TCP endpoint on a TUN device (spec 4.1): one listening address,
/// one TunPath per client connection. Packets in: parse, find the connection, hand over.
/// The inbound socket stays a plain TCP listener when no TUN is configured (kernel mode).
namespace Tbt.Carrier

open System
open System.Collections.Generic
open System.Net
open Tbt.Contracts

[<Sealed>]
type TunStack(fd: int, local: IPEndPoint, clock: IClock, entropy: IEntropy, maxConnections: int) =
    let localIp =
        let b = local.Address.GetAddressBytes()
        Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ReadOnlySpan b)
    let localPort = uint16 local.Port
    let rx: byte[] = Array.zeroCreate 65536
    let scratch: byte[] = Array.zeroCreate 65536
    let conns = Dictionary<uint64, TunPath>()
    let fresh = List<TunPath>()
    /// One rate group per client address (its paths share a bottleneck).
    let groups = Dictionary<uint32, RateGroup>()
    let counters = TunCounters()
    let mutable packetsIn = 0L
    let mutable packetsOut = 0L
    let mutable writeFailures = 0L
    let isn () =
        let b = Array.zeroCreate<byte> 4
        entropy.Fill(Span b)
        BitConverter.ToUInt32(b, 0)

    let output =
        { new ITunOutput with
            member _.Write(packet) =
                if Native.write fd packet < 0 then writeFailures <- writeFailures + 1L
                else packetsOut <- packetsOut + 1L
            member _.Scratch = scratch }

    let key (ip: uint32) (port: uint16) = (uint64 ip <<< 16) ||| uint64 port

    /// RST for a segment that has no connection (RFC 793 reset generation).
    let reset (seg: Packet.Segment) =
        if seg.Flags &&& Packet.Rst = 0uy then
            let ackFlag = seg.Flags &&& Packet.Ack <> 0uy
            let seq = if ackFlag then seg.AckNo else 0u
            let ack = seg.Seq + uint32 seg.PayloadLength + (if seg.Flags &&& Packet.Syn <> 0uy then 1u else 0u)
            let n = Packet.build (Span scratch) localIp seg.SrcIp localPort seg.SrcPort seq ack
                                 (if ackFlag then Packet.Rst else Packet.Rst ||| Packet.Ack) 0us ReadOnlySpan.Empty 0 0us
            output.Write(ReadOnlySpan(scratch, 0, n))

    member _.Fd = fd
    member _.Counters = counters
    member _.Connections = conns.Count
    /// Connections whose bootstrap frame is whole and that wait for classification.
    member _.Fresh = fresh

    /// Read every packet the device holds now (at most 4096) and hand them over.
    member _.Receive() =
        let mutable go = true
        let mutable budget = 4096
        while go && budget > 0 do
            let n = Native.read fd (Span rx)
            if n <= 0 then go <- false
            else
                budget <- budget - 1
                packetsIn <- packetsIn + 1L
                let mutable seg = Unchecked.defaultof<Packet.Segment>
                let packet = Span(rx, 0, n)
                if Packet.tryParse (Span<byte>.op_Implicit packet) &seg && seg.DstIp = localIp && seg.DstPort = localPort then
                    let k = key seg.SrcIp seg.SrcPort
                    let now = clock.NowNs()
                    match conns.TryGetValue k with
                    | true, c when not c.Closed ->
                        let before = c.BootState
                        c.OnSegment(seg, packet, now)
                        if before = 0 && c.BootState <> 0 && not c.Attached then fresh.Add c
                    | _ ->
                        if seg.Flags &&& Packet.Syn <> 0uy && seg.Flags &&& Packet.Ack = 0uy
                           && conns.Count < maxConnections then
                            conns.Remove k |> ignore
                            let g =
                                match groups.TryGetValue seg.SrcIp with
                                | true, g -> g
                                | _ ->
                                    let g = RateGroup(clock)
                                    groups.[seg.SrcIp] <- g
                                    g
                            let c = TunPath(output, counters, localIp, localPort, seg.SrcIp, seg.SrcPort, isn (), clock, g)
                            g.Add c
                            conns.[k] <- c
                            c.OnSyn(seg.Seq, Span<byte>.op_Implicit (packet.Slice(seg.OptionsAt, seg.OptionsLength)),
                                    1460, now)
                        else reset seg

    /// Timers and pending ACKs of every connection; closed ones leave the table.
    member _.Service() =
        let now = clock.NowNs()
        let mutable dead = 0
        for c in conns.Values do
            c.Service now
            c.FlushAck()
            if c.Closed then dead <- dead + 1
        if dead > 0 then
            let gone = List<uint64>()
            for kv in conns do if kv.Value.Closed then gone.Add kv.Key
            for k in gone do
                let c = conns.[k]
                match groups.TryGetValue c.RemoteIp with
                | true, g ->
                    g.Remove c
                    if g.Count = 0 then groups.Remove c.RemoteIp |> ignore
                | _ -> ()
                conns.Remove k |> ignore
        for g in groups.Values do g.Service now

    /// True when a connection needs the next wait to be short.
    member _.Busy =
        let mutable b = false
        for c in conns.Values do if c.Busy then b <- true
        b

    member _.Describe() =
        "tun conns=" + string conns.Count + " pkts_in=" + string packetsIn + " pkts_out=" + string packetsOut
        + " write_fail=" + string writeFailures + " seg_sent=" + string counters.SegmentsSent
        + " seg_lost=" + string counters.SegmentsLost + " borrow_retx=" + string counters.BorrowRetx
        + " tail_repairs=" + string counters.TailRepairs + " up_holes=" + string counters.UpHoles
        + " virtual_ack=" + string counters.VirtualAcks + " fast_swallow=" + string counters.FastSwallows
        + " cells_rx=" + string counters.CellsReceived + " cells_spanning=" + string counters.CellsSpanning
        + " cells_dropped=" + string counters.CellsDropped
        + String.Join("", groups.Values |> Seq.map (fun g -> "\n  " + g.Describe()))
        + String.Join("", conns.Values |> Seq.map (fun c -> "\n  path " + string (c :> IPath).PathId + " " + c.Describe()))

    /// Open the TUN device name; null when it fails.
    static member Open(name: string, local: IPEndPoint, clock: IClock, entropy: IEntropy, maxConnections: int) : TunStack | null =
        let fd = Native.openTun name
        if fd < 0 then null else TunStack(fd, local, clock, entropy, maxConnections)
