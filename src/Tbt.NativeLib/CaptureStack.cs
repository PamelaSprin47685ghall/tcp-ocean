using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Tbt.Carrier;
using Tbt.Contracts;

namespace Tbt.NativeLib;

/// <summary>
/// Userspace IPv4 TCP terminator on an adopted TUN fd. Each established
/// connection is spliced through a loopback TCP pair into FlowBridge.TryAdd,
/// so captured app flows ride the existing session without changing FlowBridge
/// or the default <c>tbtun client</c> listener.
/// </summary>
internal sealed class CaptureStack : IDisposable
{
    private const int MaxPacket = 2048;
    private const int Mss = 1460;
    private const int RcvWindow = 65535;
    private const long RtoNs = 250_000_000;
    private static readonly byte[] MssOption = { 2, 4, 0x05, 0xb4 };

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint SysRead(int fd, ref byte buf, nuint n);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint SysWrite(int fd, ref byte buf, nuint n);

    private static int TunRead(int fd, Span<byte> buf) =>
        (int)SysRead(fd, ref MemoryMarshal.GetReference(buf), (nuint)buf.Length);

    private static int TunWrite(int fd, ReadOnlySpan<byte> buf) =>
        (int)SysWrite(fd, ref MemoryMarshal.GetReference(buf), (nuint)buf.Length);

    private readonly int _fd;
    private readonly FlowBridge _bridge;
    private readonly IClock _clock;
    private readonly byte[] _rx = new byte[MaxPacket];
    private readonly byte[] _tx = new byte[MaxPacket];
    private readonly Dictionary<long, Conn> _byTuple = new();
    private readonly List<Conn> _conns = new();
    private ushort _ipId = 1;
    private long _packetsIn;
    private long _packetsOut;
    private long _parseFail;
    private long _flowsOpened;
    private bool _disposed;

    public CaptureStack(int fd, FlowBridge bridge, IClock clock)
    {
        _fd = fd;
        _bridge = bridge;
        _clock = clock;
    }

    public long PacketsIn => _packetsIn;
    public long PacketsOut => _packetsOut;
    public long ParseFail => _parseFail;
    public long FlowsOpened => _flowsOpened;
    public int Connections => _conns.Count;

    public string Describe() =>
        "capture pkts_in=" + _packetsIn + " pkts_out=" + _packetsOut
        + " parse_fail=" + _parseFail + " flows=" + _flowsOpened
        + " conns=" + _conns.Count;

    public void Receive()
    {
        while (true)
        {
            int n = TunRead(_fd, _rx.AsSpan());
            if (n <= 0)
                return;
            _packetsIn++;
            Packet.Segment seg = default;
            if (!Packet.tryParse(new ReadOnlySpan<byte>(_rx, 0, n), ref seg))
            {
                _parseFail++;
                continue;
            }
            OnSegment(in seg);
        }
    }

    public void Service()
    {
        long now = _clock.NowNs();
        for (int i = _conns.Count - 1; i >= 0; i--)
        {
            Conn c = _conns[i];
            if (c.Closed)
            {
                Drop(c, i);
                continue;
            }
            PumpLocal(c);
            if (c.State == ConnState.Established || c.State == ConnState.FinWait)
                TrySend(c, now);
            if (c.AckDue)
                SendControl(c, Packet.Ack, now);
            if ((c.State == ConnState.SynReceived || c.SndUna < c.SndNxt)
                && now - c.LastSendNs >= RtoNs)
                Retransmit(c, now);
            if (c.Closed)
                Drop(c, i);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int i = _conns.Count - 1; i >= 0; i--)
            Drop(_conns[i], i);
    }

    private void OnSegment(in Packet.Segment seg)
    {
        long key = Key(seg.SrcIp, seg.SrcPort, seg.DstIp, seg.DstPort);
        if ((seg.Flags & Packet.Syn) != 0 && (seg.Flags & Packet.Ack) == 0)
        {
            if (_byTuple.ContainsKey(key))
            {
                // Duplicate SYN: resend SYN-ACK.
                if (_byTuple.TryGetValue(key, out Conn? existing))
                    SendSynAck(existing, _clock.NowNs());
                return;
            }
            OnSyn(in seg, key);
            return;
        }
        if (!_byTuple.TryGetValue(key, out Conn? c) || c.Closed)
        {
            if ((seg.Flags & Packet.Rst) == 0)
                SendRst(seg.DstIp, seg.SrcIp, seg.DstPort, seg.SrcPort, seg.AckNo, 0);
            return;
        }
        if ((seg.Flags & Packet.Rst) != 0)
        {
            c.Closed = true;
            return;
        }
        if ((seg.Flags & Packet.Ack) != 0)
            OnAck(c, seg.AckNo);

        if (c.State == ConnState.SynReceived && (seg.Flags & Packet.Ack) != 0 && seg.AckNo == c.Iss + 1)
        {
            if (!OpenFlow(c))
            {
                SendRst(c.LocalIp, c.RemoteIp, c.LocalPort, c.RemotePort, c.SndNxt, c.RcvNxt);
                c.Closed = true;
                return;
            }
            c.State = ConnState.Established;
        }

        uint seq = seg.Seq;
        int payload = seg.PayloadLength;
        if (payload > 0 && (c.State == ConnState.Established || c.State == ConnState.FinWait))
        {
            if (seq == c.RcvNxt)
            {
                ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(_rx, seg.PayloadAt, payload);
                int wrote = WriteLocal(c, data);
                if (wrote > 0)
                {
                    c.RcvNxt += (uint)wrote;
                    c.AckDue = true;
                }
            }
            else if (SeqLt(seq, c.RcvNxt))
            {
                c.AckDue = true; // duplicate
            }
        }

        if ((seg.Flags & Packet.Fin) != 0 && seq + (uint)payload == c.RcvNxt
            && !c.FinReceived && (c.State == ConnState.Established || c.State == ConnState.FinWait))
        {
            c.RcvNxt++;
            c.FinReceived = true;
            c.AckDue = true;
            try { c.Local?.Shutdown(SocketShutdown.Send); }
            catch (SocketException) { }
        }
    }

    private void OnSyn(in Packet.Segment seg, long key)
    {
        uint iss = (uint)Random.Shared.Next();
        var c = new Conn
        {
            LocalIp = seg.DstIp,
            RemoteIp = seg.SrcIp,
            LocalPort = seg.DstPort,
            RemotePort = seg.SrcPort,
            Iss = iss,
            SndNxt = iss + 1,
            SndUna = iss + 1,
            RcvNxt = seg.Seq + 1,
            PeerWindow = seg.Window == 0 ? (ushort)RcvWindow : seg.Window,
            State = ConnState.SynReceived,
        };
        _byTuple[key] = c;
        _conns.Add(c);
        SendSynAck(c, _clock.NowNs());
    }

    private bool OpenFlow(Conn c)
    {
        var pair = ConnectedPair();
        if (pair is null)
            return false;
        c.Local = pair.Value.Local;
        c.Peer = pair.Value.Peer;
        if (!_bridge.TryAdd(c.Peer))
        {
            c.Local.Dispose();
            c.Peer.Dispose();
            c.Local = null;
            c.Peer = null;
            return false;
        }
        // FlowBridge owns Peer.
        c.Peer = null;
        _flowsOpened++;
        return true;
    }

    private static (Socket Local, Socket Peer)? ConnectedPair()
    {
        Socket? listener = null, local = null, peer = null;
        try
        {
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            local.Connect(listener.LocalEndPoint!);
            peer = listener.Accept();
            listener.Dispose();
            listener = null;
            local.NoDelay = true;
            peer.NoDelay = true;
            local.Blocking = false;
            peer.Blocking = false;
            return (local, peer);
        }
        catch (SocketException)
        {
            listener?.Dispose();
            local?.Dispose();
            peer?.Dispose();
            return null;
        }
    }

    private void OnAck(Conn c, uint ack)
    {
        if (!SeqLeq(c.SndUna, ack) || SeqLt(c.SndNxt, ack))
            return;
        int advanced = (int)(ack - c.SndUna);
        if (advanced <= 0)
            return;
        if (advanced >= c.Unacked.Count)
            c.Unacked.Clear();
        else
            c.Unacked.RemoveRange(0, advanced);
        c.SentOfs = Math.Max(0, c.SentOfs - Math.Min(advanced, c.SentOfs));
        // ACK may also cover a previously sent FIN (one sequence number past the data).
        if (c.FinSent && ack == c.SndNxt)
            c.SentOfs = c.Unacked.Count;
        c.SndUna = ack;
        if (c.FinSent && c.SndUna == c.SndNxt && c.FinReceived)
            c.Closed = true;
    }

    private void PumpLocal(Conn c)
    {
        if (c.Local is null || c.State == ConnState.SynReceived)
            return;
        var buf = c.Scratch;
        while (true)
        {
            int n;
            SocketError err;
            try
            {
                n = c.Local.Receive(buf, SocketFlags.None, out err);
            }
            catch (SocketException)
            {
                c.Closed = true;
                SendControl(c, Packet.Rst | Packet.Ack, _clock.NowNs());
                return;
            }
            if (err == SocketError.WouldBlock)
                break;
            if (err != SocketError.Success)
            {
                c.Closed = true;
                SendControl(c, Packet.Rst | Packet.Ack, _clock.NowNs());
                return;
            }
            if (n == 0)
            {
                if (!c.FinSent && c.State == ConnState.Established)
                {
                    c.FinPending = true;
                    c.State = ConnState.FinWait;
                }
                break;
            }
            for (int i = 0; i < n; i++)
                c.Unacked.Add(buf[i]);
            c.SndNxt += (uint)n;
        }
    }

    private static int WriteLocal(Conn c, ReadOnlySpan<byte> data)
    {
        if (c.Local is null)
            return 0;
        try
        {
            SocketError err;
            int n = c.Local.Send(data, SocketFlags.None, out err);
            if (err == SocketError.WouldBlock)
                return 0;
            if (err != SocketError.Success)
            {
                c.Closed = true;
                return 0;
            }
            return n;
        }
        catch (SocketException)
        {
            c.Closed = true;
            return 0;
        }
    }

    private void TrySend(Conn c, long now)
    {
        int unacked = c.Unacked.Count;
        int already = (int)(c.SndNxt - c.SndUna);
        // SndNxt already accounts for bytes in Unacked (and FIN). Retransmit path uses SndUna.
        // Newly queued bytes sit at the end of Unacked; they were just added and SndNxt moved.
        // Send the not-yet-transmitted tail: from (c.SentOfs) .
        int inflight = c.SentOfs;
        int fresh = unacked - inflight;
        if (c.FinPending && !c.FinSent && fresh == 0)
        {
            SendControl(c, (byte)(Packet.Ack | Packet.Fin), now);
            c.FinSent = true;
            c.FinPending = false;
            c.SndNxt++;
            c.SentOfs = unacked; // FIN is seq, not in Unacked
            return;
        }
        int window = Math.Min((int)c.PeerWindow, RcvWindow);
        int can = Math.Min(fresh, Math.Max(0, window - inflight));
        int off = inflight;
        while (can > 0)
        {
            int n = Math.Min(Mss, can);
            var payload = CollectionsMarshalSlice(c.Unacked, off, n);
            SendData(c, c.SndUna + (uint)off, payload, now, psh: true);
            off += n;
            can -= n;
            c.SentOfs += n;
        }
        if (c.FinPending && !c.FinSent && c.SentOfs == c.Unacked.Count)
        {
            SendControl(c, (byte)(Packet.Ack | Packet.Fin), now);
            c.FinSent = true;
            c.FinPending = false;
            c.SndNxt++;
        }
    }

    private void Retransmit(Conn c, long now)
    {
        if (c.State == ConnState.SynReceived)
        {
            SendSynAck(c, now);
            return;
        }
        int len = Math.Min(Mss, (int)(c.SndNxt - c.SndUna) - (c.FinSent ? 1 : 0));
        if (len > 0 && len <= c.Unacked.Count)
            SendData(c, c.SndUna, CollectionsMarshalSlice(c.Unacked, 0, len), now, psh: true);
        else if (c.FinSent)
            SendControl(c, (byte)(Packet.Ack | Packet.Fin), now);
        else
            SendControl(c, Packet.Ack, now);
    }

    private void SendSynAck(Conn c, long now)
    {
        WritePacket(c.LocalIp, c.RemoteIp, c.LocalPort, c.RemotePort,
            c.Iss, c.RcvNxt, (byte)(Packet.Syn | Packet.Ack), RcvWindow, MssOption, ReadOnlySpan<byte>.Empty);
        c.LastSendNs = now;
        c.AckDue = false;
    }

    private void SendControl(Conn c, byte flags, long now)
    {
        uint seq = (flags & Packet.Fin) != 0 && c.FinSent ? c.SndNxt - 1 : c.SndNxt;
        if ((flags & Packet.Rst) != 0)
            seq = c.SndNxt;
        WritePacket(c.LocalIp, c.RemoteIp, c.LocalPort, c.RemotePort,
            seq, c.RcvNxt, flags, RcvWindow, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);
        c.LastSendNs = now;
        c.AckDue = false;
    }

    private void SendData(Conn c, uint seq, ReadOnlySpan<byte> payload, long now, bool psh)
    {
        byte flags = Packet.Ack;
        if (psh)
            flags |= Packet.Psh;
        WritePacket(c.LocalIp, c.RemoteIp, c.LocalPort, c.RemotePort,
            seq, c.RcvNxt, flags, RcvWindow, ReadOnlySpan<byte>.Empty, payload);
        c.LastSendNs = now;
        c.AckDue = false;
    }

    private void SendRst(uint src, uint dst, ushort sport, ushort dport, uint seq, uint ack)
    {
        byte flags = Packet.Rst;
        if (ack != 0)
            flags |= Packet.Ack;
        WritePacket(src, dst, sport, dport, seq, ack, flags, 0, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);
    }

    private void WritePacket(uint src, uint dst, ushort sport, ushort dport, uint seq, uint ack,
        byte flags, int window, ReadOnlySpan<byte> options, ReadOnlySpan<byte> payload)
    {
        int opt = options.Length;
        int hdr = 40 + opt;
        if (hdr + payload.Length > _tx.Length)
            return;
        options.CopyTo(_tx.AsSpan(40));
        payload.CopyTo(_tx.AsSpan(hdr));
        int n = Packet.build(
            _tx.AsSpan(), src, dst, sport, dport, seq, ack, flags, (ushort)window,
            options, payload.Length, _ipId++);
        if (TunWrite(_fd, _tx.AsSpan(0, n)) > 0)
            _packetsOut++;
    }

    private void Drop(Conn c, int index)
    {
        _byTuple.Remove(Key(c.RemoteIp, c.RemotePort, c.LocalIp, c.LocalPort));
        try { c.Local?.Dispose(); } catch (SocketException) { }
        c.Local = null;
        c.Closed = true;
        if (index >= 0 && index < _conns.Count && ReferenceEquals(_conns[index], c))
            _conns.RemoveAt(index);
        else
            _conns.Remove(c);
    }

    private static long Key(uint sip, ushort sport, uint dip, ushort dport) =>
        ((long)sip << 32) ^ ((long)dip << 8) ^ ((long)sport << 16) ^ dport;

    private static bool SeqLt(uint a, uint b) => (int)(a - b) < 0;
    private static bool SeqLeq(uint a, uint b) => (int)(a - b) <= 0;

    private static ReadOnlySpan<byte> CollectionsMarshalSlice(List<byte> list, int off, int len)
    {
        // Copy to a small stack buffer via the list indexer to avoid a span API on List<byte>.
        return list.GetRange(off, len).ToArray();
    }

    private enum ConnState { SynReceived, Established, FinWait }

    private sealed class Conn
    {
        public uint LocalIp, RemoteIp;
        public ushort LocalPort, RemotePort;
        public uint Iss, SndNxt, SndUna, RcvNxt;
        public ushort PeerWindow = RcvWindow;
        public ConnState State;
        public Socket? Local;
        public Socket? Peer;
        public bool AckDue, FinPending, FinSent, FinReceived, Closed;
        public long LastSendNs;
        public int SentOfs;
        public readonly List<byte> Unacked = new();
        public readonly byte[] Scratch = new byte[Mss];
    }
}
