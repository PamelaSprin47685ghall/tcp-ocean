using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.FSharp.Core;
using Tbt.Carrier;
using Tbt.Contracts;
using Tbt.Session;
using static Tbt.Session.Handshake;

namespace Tbt.NativeLib;

/// <summary>
/// Android / NativeLib client entry: handshake over protected path sockets,
/// then carry TUN-captured TCP flows on the session. Not used by
/// <c>tbtun client</c>.
/// </summary>
internal static class ClientHost
{
    public static int Start(HostOptions o)
    {
        var entropy = new HostRuntime.HostEntropy();
        var clock = new CachedClock(new HostRuntime.HostClock(), null, null);
        PathLink? link0 = null;
        Tbt.Session.SessionParams p;
        int helloMaxPaths;
        try
        {
            var accepted = HandshakePath(o, entropy, pathId: 0, incarnation: 0, existing: null, joinMaxPaths: 0);
            if (accepted.Error != 0)
                return accepted.Error;
            link0 = accepted.Link!;
            p = accepted.Params;
            helloMaxPaths = accepted.MaxPaths;
        }
        catch (Exception)
        {
            return TbtErr.Handshake;
        }

        var bridge = new FlowBridge(FSharpValueOption<EndPoint>.ValueNone);
        var d = SessionConfigModule.defaults;
        var config = new SessionConfig(
            d.Rho,
            1024,
            d.MaxRows,
            d.MaxPaths,
            d.TxQueueCells,
            d.Credit,
            false);
        var session = new Tbt.Session.Session(Role.Client, p, bridge, clock, config);
        bridge.Attach(session);
        var carrier = new SessionCarrier(session, bridge, null, null);
        carrier.AddPath(link0);

        int want = Math.Max(1, o.PathCount);
        int maxPaths = Math.Max(1, helloMaxPaths);
        var pending = new ConcurrentQueue<PathLink>();
        for (int id = 1; id < Math.Min(want, maxPaths); id++)
        {
            try
            {
                var j = HandshakePath(o, entropy, pathId: id, incarnation: 1, existing: p, joinMaxPaths: maxPaths);
                if (j.Error == 0 && j.Link is not null)
                {
                    j.Link.PathId = (ushort)id;
                    j.Link.Incarnation = 1u;
                    pending.Enqueue(j.Link);
                }
                else
                    j.Link?.Close();
            }
            catch (Exception)
            {
            }
        }
        DrainPending(carrier, pending);

        var capture = new CaptureStack(o.TunFd, bridge, clock);
        HostRuntime.SetNonblock(o.TunFd);

        var thread = new Thread(() => Worker(o.TunFd, session, carrier, capture, pending, clock))
        {
            IsBackground = true,
            Name = "tbt-client",
        };
        if (!HostRuntime.ArmWorker(thread, o.TunFd))
        {
            capture.Dispose();
            carrier.Close();
            return TbtErr.Busy;
        }
        try
        {
            thread.Start();
        }
        catch (Exception)
        {
            HostRuntime.DisarmWorker();
            capture.Dispose();
            carrier.Close();
            return TbtErr.Thread;
        }
        return TbtErr.Ok;
    }

    private static void DrainPending(SessionCarrier carrier, ConcurrentQueue<PathLink> pending)
    {
        while (pending.TryDequeue(out PathLink? link) && link is not null)
            carrier.AddPath(link);
    }

    private static void Worker(int fd, Tbt.Session.Session session, SessionCarrier carrier, CaptureStack capture,
        ConcurrentQueue<PathLink> pending, CachedClock clock)
    {
        try
        {
            using var poller = new Poller();
            using (capture)
            {
                while (Volatile.Read(ref HostRuntime.Running) != 0 && carrier.Alive)
                {
                    DrainPending(carrier, pending);
                    poller.WatchFd(fd);
                    carrier.Watch(poller);
                    poller.Wait(carrier.WaitMs());
                    clock.Tick();
                    capture.Receive();
                    capture.Service();
                    carrier.Service(poller);
                    HostRuntime.PublishStats(
                        "state=running tun_fd=" + fd
                        + " " + capture.Describe()
                        + " " + carrier.Describe()
                        + " protect_calls=" + Volatile.Read(ref HostRuntime.ProtectCalls)
                        + " notsent_source=" + HostRuntime.NotSentSource()
                        + " " + Platform.describe());
                }
                if (session.CloseState == CloseState.Open)
                    session.Close(0);
                carrier.Close();
            }
        }
        catch (Exception ex)
        {
            HostRuntime.PublishStats("state=error " + ex.GetType().Name + " " + ex.Message);
            try { carrier.Close(); } catch { }
        }
    }

    private readonly struct PathAttempt
    {
        public int Error { get; init; }
        public PathLink? Link { get; init; }
        public Tbt.Session.SessionParams Params { get; init; }
        public int MaxPaths { get; init; }
    }

    /// <summary>Connect a path socket, calling protect(fd) before connect.</summary>
    private static PathAttempt HandshakePath(HostOptions o, IEntropy entropy, int pathId, uint incarnation,
        Tbt.Session.SessionParams? existing, int joinMaxPaths = 0)
    {
        var socket = new Socket(o.Server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            SocketTuning.path(socket, o.WindowClamp);
            int fd = (int)socket.Handle;
            if (HostRuntime.CallProtect(fd) == 0)
            {
                socket.Dispose();
                return new PathAttempt { Error = TbtErr.Protect };
            }
            socket.ReceiveTimeout = o.TimeoutMs;
            socket.SendTimeout = o.TimeoutMs;
            var connect = socket.ConnectAsync(o.Server);
            if (!connect.Wait(o.TimeoutMs))
            {
                socket.Dispose();
                return new PathAttempt { Error = TbtErr.Handshake };
            }
            connect.GetAwaiter().GetResult();

            byte[] boot;
            if (existing is null)
            {
                var offer = new ClientOffer(
                    o.Psk, Guid.NewGuid(), entropy,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    o.Mss, Math.Max(1, o.PathCount),
                    null, null, null, null, null);
                boot = offer.Frame;
                var got = Bootstrap(socket, o, boot);
                if (got.Error != 0)
                    return new PathAttempt { Error = got.Error };
                var acc = offer.TryAccept(new ReadOnlySpan<byte>(got.Reply!));
                if (acc.IsNone)
                {
                    got.Link!.Close();
                    return new PathAttempt { Error = TbtErr.Handshake };
                }
                var tup = acc.Value;
                got.Link!.Socket.Blocking = false;
                return new PathAttempt { Error = 0, Link = got.Link, Params = tup.Item1, MaxPaths = (int)tup.Item2.MaxPaths };
            }
            else
            {
                var p = existing.Value;
                int joinMax = joinMaxPaths > 0 ? joinMaxPaths : Math.Max(1, o.PathCount);
                var join = new JoinOffer(
                    p.Keys.PathKey, entropy, p.Keys.SessionId, p.Symbol, p.Caps,
                    joinMax, (ushort)pathId, incarnation, o.Mss,
                    FSharpOption<byte>.Some(p.Keys.Suite));
                boot = join.Frame;
                var got = Bootstrap(socket, o, boot);
                if (got.Error != 0)
                    return new PathAttempt { Error = got.Error };
                if (join.TryAccept(new ReadOnlySpan<byte>(got.Reply!)).IsNone)
                {
                    got.Link!.Close();
                    return new PathAttempt { Error = TbtErr.Handshake };
                }
                got.Link!.Socket.Blocking = false;
                return new PathAttempt { Error = 0, Link = got.Link, Params = p };
            }
        }
        catch (Exception)
        {
            socket.Dispose();
            return new PathAttempt { Error = TbtErr.Handshake };
        }
    }

    private readonly struct BootGot
    {
        public int Error { get; init; }
        public PathLink? Link { get; init; }
        public byte[]? Reply { get; init; }
    }

    private static BootGot Bootstrap(Socket socket, HostOptions o, byte[] boot)
    {
        var link = new PathLink(socket, SocketTuning.probe());
        try
        {
            byte[] head = Handshake.requestHead(o.Host);
            link.Queue(head);
            link.Queue(boot);
            while (!link.TxIdle)
            {
                if (link.Send() != IoResult.Progress)
                {
                    link.Close();
                    return new BootGot { Error = TbtErr.Handshake };
                }
            }
            bool failed = false;
            byte[]? result = null;
            var deadline = Environment.TickCount64 + o.TimeoutMs;
            while (result is null)
            {
                if (Environment.TickCount64 > deadline)
                {
                    link.Close();
                    return new BootGot { Error = TbtErr.Handshake };
                }
                IoResult r = link.Receive();
                if (r == IoResult.Closed)
                {
                    link.Close();
                    return new BootGot { Error = TbtErr.Handshake };
                }
                Span<byte> reply = default;
                if (link.TryTakeHead(ref failed) && link.TryTakeFrame(ref reply))
                    result = reply.ToArray();
                else if (failed)
                {
                    link.Close();
                    return new BootGot { Error = TbtErr.Handshake };
                }
                else if (r == IoResult.WouldBlock)
                    Thread.Sleep(5);
            }
            return new BootGot { Error = 0, Link = link, Reply = result };
        }
        catch (Exception)
        {
            link.Close();
            return new BootGot { Error = TbtErr.Handshake };
        }
    }
}

/// <summary>Parsed start options for the NativeLib client entry.</summary>
internal sealed class HostOptions
{
    public int TunFd;
    public int WindowClamp;
    public int PathCount = 1;
    public int Mss = 1448;
    public int TimeoutMs = 3000;
    public IPEndPoint Server = new(IPAddress.Loopback, 0);
    public byte[] Psk = Array.Empty<byte>();
    public string Host = "127.0.0.1";
}
