using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.FSharp.Core;
using Tbt.Carrier;
using Tbt.Contracts;

namespace Tbt.NativeLib;

[StructLayout(LayoutKind.Sequential)]
internal struct TbtConfig
{
    public int Version;
    public int Flags;
    public int LocalPort;
    public uint LocalIpv4;
    public int WindowClamp;
    public int MaxConnections;
    public IntPtr Protect;
    public IntPtr ProtectCtx;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TbtConfigV2
{
    public int Version;
    public int Flags;
    public int LocalPort;
    public uint LocalIpv4;
    public int WindowClamp;
    public int MaxConnections;
    public IntPtr Protect;
    public IntPtr ProtectCtx;
    public uint ServerIpv4;
    public int ServerPort;
    public IntPtr Psk;
    public int PskLen;
    public int PathCount;
    public int Mss;
    public IntPtr Host;
}

internal static class TbtErr
{
    public const int Ok = 0;
    public const int Args = -1;
    public const int Busy = -2;
    public const int Attach = -3;
    public const int Thread = -4;
    public const int NotStarted = -5;
    public const int Handshake = -6;
    public const int Protect = -7;
}


/// <summary>Process-wide NativeAOT host. One session at a time. The TUN fd is not closed.</summary>
internal static class HostRuntime
{
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fd, int cmd, int arg);

    // Linux / bionic: F_GETFL=3, F_SETFL=4, O_NONBLOCK=0x800 (04000).
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonblock = 0x800;

    internal static void SetNonblock(int fd)
    {
        if (!Platform.current.Value.HasLibc) return;
        int fl = Fcntl(fd, FGetFl, 0);
        if (fl >= 0)
            Fcntl(fd, FSetFl, fl | ONonblock);
    }

    private const int ConfigVersion = 2;
    private const int ConfigVersionV1 = 1;
    private const int FlagSmoke = 1;

    private static readonly Lock Gate = new();
    internal static int Running;
    private static IntPtr _protect;
    private static IntPtr _protectCtx;
    internal static int ProtectCalls;
    private static int _tunFd = -1;
    private static string _stats = "state=stopped";
    private static Thread? _thread;
    private static TunStack? _stack;

    public static int SetProtect(IntPtr fn, IntPtr ctx)
    {
        lock (Gate)
        {
            _protect = fn;
            _protectCtx = ctx;
            return TbtErr.Ok;
        }
    }

    public static int Start(int tunFd, IntPtr cfgPtr)
    {
        if (tunFd < 0 || cfgPtr == IntPtr.Zero)
            return TbtErr.Args;
        int version;
        try
        {
            version = Marshal.ReadInt32(cfgPtr);
        }
        catch (Exception)
        {
            return TbtErr.Args;
        }
        if (version != ConfigVersion && version != ConfigVersionV1)
            return TbtErr.Args;

        TbtConfig cfg;
        TbtConfigV2 v2 = default;
        try
        {
            if (version == ConfigVersion)
                v2 = Marshal.PtrToStructure<TbtConfigV2>(cfgPtr);
            cfg = version == ConfigVersion
                ? new TbtConfig
                {
                    Version = v2.Version,
                    Flags = v2.Flags,
                    LocalPort = v2.LocalPort,
                    LocalIpv4 = v2.LocalIpv4,
                    WindowClamp = v2.WindowClamp,
                    MaxConnections = v2.MaxConnections,
                    Protect = v2.Protect,
                    ProtectCtx = v2.ProtectCtx,
                }
                : Marshal.PtrToStructure<TbtConfig>(cfgPtr);
        }
        catch (Exception)
        {
            return TbtErr.Args;
        }

        lock (Gate)
        {
            if (Running != 0 || _thread is not null)
                return TbtErr.Busy;
            if (cfg.Protect != IntPtr.Zero)
            {
                _protect = cfg.Protect;
                _protectCtx = cfg.ProtectCtx;
            }
            Running = 1;
            _tunFd = tunFd;
            _stats = "state=starting tun_fd=" + tunFd;
        }
        Interlocked.Exchange(ref ProtectCalls, 0);

        bool realClient = version == ConfigVersion
            && (cfg.Flags & FlagSmoke) == 0
            && v2.ServerPort > 0 && v2.ServerPort <= 65535
            && v2.Psk != IntPtr.Zero && v2.PskLen == 32;
        try
        {
            if (realClient)
            {
                var psk = new byte[32];
                Marshal.Copy(v2.Psk, psk, 0, 32);
                string host = v2.Host != IntPtr.Zero
                    ? (Marshal.PtrToStringUTF8(v2.Host) ?? "")
                    : "";
                var server = new IPEndPoint(Ipv4(v2.ServerIpv4), v2.ServerPort);
                if (string.IsNullOrEmpty(host))
                    host = server.Address.ToString();
                int clamp = cfg.WindowClamp > 0 ? cfg.WindowClamp : SocketTuning.WindowClamp;
                var opts = new HostOptions
                {
                    TunFd = tunFd,
                    WindowClamp = clamp,
                    PathCount = v2.PathCount > 0 ? v2.PathCount : 1,
                    Mss = v2.Mss > 0 ? v2.Mss : 1448,
                    Server = server,
                    Psk = psk,
                    Host = host,
                };
                int rc = ClientHost.Start(opts);
                if (rc != TbtErr.Ok)
                    ClearReservation();
                return rc;
            }
            return StartSmoke(tunFd, cfg);
        }
        catch (Exception)
        {
            ClearReservation();
            return TbtErr.Attach;
        }
    }

    private static IPAddress Ipv4(uint v)
    {
        if (v == 0)
            return IPAddress.Parse("10.200.3.2");
        return new IPAddress(new byte[]
        {
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v
        });
    }

    private static int StartSmoke(int tunFd, TbtConfig cfg)
    {
        int port = cfg.LocalPort > 0 && cfg.LocalPort <= 65535 ? cfg.LocalPort : 8443;
        var local = new IPEndPoint(Ipv4(cfg.LocalIpv4), port);
        int clamp = cfg.WindowClamp > 0 ? cfg.WindowClamp : SocketTuning.WindowClamp;
        int maxConn = cfg.MaxConnections > 0 ? cfg.MaxConnections : 64;
        TunStack? stack = TunStack.Attach(
            tunFd, local, new HostClock(), new HostEntropy(), maxConn,
            FSharpOption<int>.Some(clamp),
            null);
        if (stack is null)
        {
            ClearReservation();
            return TbtErr.Attach;
        }
        SetNonblock(tunFd);
        if ((cfg.Flags & FlagSmoke) != 0)
            CallProtectOnThrowawaySocket();

        var thread = new Thread(Worker) { IsBackground = true, Name = "tbt-host" };
        lock (Gate)
        {
            if (_thread is not null)
            {
                ClearReservation();
                return TbtErr.Busy;
            }
            _stack = stack;
            _thread = thread;
        }
        try
        {
            thread.Start();
        }
        catch (Exception)
        {
            ClearReservation();
            return TbtErr.Thread;
        }
        return TbtErr.Ok;
    }

    internal static bool ArmWorker(Thread thread, int tunFd)
    {
        lock (Gate)
        {
            if (_thread is not null)
                return false;
            _tunFd = tunFd;
            Running = 1;
            _thread = thread;
            _stats = "state=starting tun_fd=" + tunFd;
            return true;
        }
    }

    internal static void DisarmWorker() => ClearReservation();

    private static void ClearReservation()
    {
        lock (Gate)
        {
            Running = 0;
            _thread = null;
            _stack = null;
            _tunFd = -1;
            _stats = "state=stopped";
        }
    }

    internal static void PublishStats(string text)
    {
        lock (Gate)
            _stats = text;
    }

    public static void Stop()
    {
        Thread? thread;
        lock (Gate)
        {
            Running = 0;
            thread = _thread;
        }
        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(2000);
        lock (Gate)
        {
            _thread = null;
            _stack = null;
            _tunFd = -1;
            _stats = "state=stopped";
        }
    }

    public static int Stats(IntPtr buf, int len)
    {
        if (buf == IntPtr.Zero || len <= 1)
            return TbtErr.Args;
        string text;
        lock (Gate)
            text = _stats;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int n = Math.Min(bytes.Length, len - 1);
        Marshal.Copy(bytes, 0, buf, n);
        Marshal.WriteByte(buf, n, 0);
        return n;
    }

    private static void Worker()
    {
        TunStack? stack;
        int fd;
        lock (Gate)
        {
            stack = _stack;
            fd = _tunFd;
        }
        if (stack is null)
            return;
        try
        {
            using var poller = new Poller();
            while (Volatile.Read(ref Running) != 0)
            {
                poller.WatchFd(fd);
                poller.Wait(50_000);
                stack.Receive(null, null);
                stack.Service();
                string caps = Platform.describe();
                string snap = "state=running tun_fd=" + fd
                    + " packets_in=" + stack.LastReceiveCount
                    + " " + stack.Describe()
                    + " protect_calls=" + Volatile.Read(ref ProtectCalls)
                    + " notsent_source=" + NotSentSource()
                    + " " + caps;
                lock (Gate)
                    _stats = snap;
            }
        }
        catch (Exception ex)
        {
            lock (Gate)
                _stats = "state=error " + ex.GetType().Name;
        }
    }

    internal static string NotSentSource()
    {
        var c = Platform.current.Value;
        if (c.HasSiocoutqnsd) return "ioctl";
        if (c.HasTcpInfoNotSent) return "tcp_info";
        return "none";
    }

    /// <summary>Exercise the host protect callback without connecting anywhere.</summary>
    private static void CallProtectOnThrowawaySocket()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            CallProtect((int)s.Handle);
        }
        catch (SocketException)
        {
        }
    }

    internal static int CallProtect(int fd)
    {
        IntPtr fn, ctx;
        lock (Gate)
        {
            fn = _protect;
            ctx = _protectCtx;
        }
        if (fn == IntPtr.Zero)
            return 1;
        unsafe
        {
            var call = (delegate* unmanaged[Cdecl]<int, IntPtr, int>)fn;
            int ok = call(fd, ctx);
            Interlocked.Increment(ref ProtectCalls);
            return ok;
        }
    }

    internal sealed class HostClock : IClock
    {
        private static readonly double Factor = 1_000_000_000.0 / Stopwatch.Frequency;
        public long NowNs() => (long)(Stopwatch.GetTimestamp() * Factor);
        public long FreshNs() => NowNs();
    }

    internal sealed class HostEntropy : IEntropy
    {
        public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
    }
}
