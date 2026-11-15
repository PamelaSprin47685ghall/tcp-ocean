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

internal static class TbtErr
{
    public const int Ok = 0;
    public const int Args = -1;
    public const int Busy = -2;
    public const int Attach = -3;
    public const int Thread = -4;
    public const int NotStarted = -5;
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

    private static void SetNonblock(int fd)
    {
        if (!Platform.current.Value.HasLibc) return;
        int fl = Fcntl(fd, FGetFl, 0);
        if (fl >= 0)
            Fcntl(fd, FSetFl, fl | ONonblock);
    }

    private const int ConfigVersion = 1;
    private const int FlagSmoke = 1;

    private static readonly Lock Gate = new();
    private static int _running;
    private static IntPtr _protect;
    private static IntPtr _protectCtx;
    private static int _protectCalls;
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
        TbtConfig cfg;
        try
        {
            cfg = Marshal.PtrToStructure<TbtConfig>(cfgPtr);
        }
        catch (Exception)
        {
            return TbtErr.Args;
        }
        if (cfg.Version != ConfigVersion)
            return TbtErr.Args;

        lock (Gate)
        {
            if (_running != 0 || _thread is not null)
                return TbtErr.Busy;
            if (cfg.Protect != IntPtr.Zero)
            {
                _protect = cfg.Protect;
                _protectCtx = cfg.ProtectCtx;
            }
        }

        int port = cfg.LocalPort > 0 && cfg.LocalPort <= 65535 ? cfg.LocalPort : 8443;
        IPAddress addr = cfg.LocalIpv4 == 0
            ? IPAddress.Parse("10.200.3.2")
            : new IPAddress(new byte[]
            {
                (byte)(cfg.LocalIpv4 >> 24),
                (byte)(cfg.LocalIpv4 >> 16),
                (byte)(cfg.LocalIpv4 >> 8),
                (byte)cfg.LocalIpv4
            });
        int clamp = cfg.WindowClamp > 0 ? cfg.WindowClamp : SocketTuning.WindowClamp;
        int maxConn = cfg.MaxConnections > 0 ? cfg.MaxConnections : 64;
        var local = new IPEndPoint(addr, port);
        var clock = new HostClock();
        var entropy = new HostEntropy();

        TunStack? stack = TunStack.Attach(
            tunFd, local, clock, entropy, maxConn,
            FSharpOption<int>.Some(clamp),
            null);
        if (stack is null)
            return TbtErr.Attach;
        SetNonblock(tunFd);

        Interlocked.Exchange(ref _protectCalls, 0);
        if ((cfg.Flags & FlagSmoke) != 0)
            CallProtectOnThrowawaySocket();

        var thread = new Thread(Worker) { IsBackground = true, Name = "tbt-host" };
        lock (Gate)
        {
            if (_running != 0)
            {
                // A concurrent start raced; the host keeps the fd.
                return TbtErr.Busy;
            }
            _stack = stack;
            _tunFd = tunFd;
            _running = 1;
            _stats = "state=starting tun_fd=" + tunFd;
            _thread = thread;
        }
        try
        {
            thread.Start();
        }
        catch (Exception)
        {
            lock (Gate)
            {
                _running = 0;
                _thread = null;
                _stack = null;
                _tunFd = -1;
            }
            return TbtErr.Thread;
        }
        return TbtErr.Ok;
    }

    public static void Stop()
    {
        Thread? thread;
        lock (Gate)
        {
            _running = 0;
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
            while (Volatile.Read(ref _running) != 0)
            {
                poller.WatchFd(fd);
                poller.Wait(50_000);
                stack.Receive(null, null);
                stack.Service();
                string caps = Platform.describe();
                string snap = "state=running tun_fd=" + fd
                    + " packets_in=" + stack.LastReceiveCount
                    + " " + stack.Describe()
                    + " protect_calls=" + Volatile.Read(ref _protectCalls)
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

    private static string NotSentSource()
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

    private static int CallProtect(int fd)
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
            Interlocked.Increment(ref _protectCalls);
            return ok;
        }
    }

    private sealed class HostClock : IClock
    {
        private static readonly double Factor = 1_000_000_000.0 / Stopwatch.Frequency;
        public long NowNs() => (long)(Stopwatch.GetTimestamp() * Factor);
        public long FreshNs() => NowNs();
    }

    private sealed class HostEntropy : IEntropy
    {
        public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
    }
}
