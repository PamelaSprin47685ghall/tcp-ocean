using System.Runtime.InteropServices;

namespace Tbt.NativeLib;

/// <summary>C ABI exports (tbt.h). Exceptions never propagate across this boundary.</summary>
public static class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "tbt_start")]
    public static int Start(int tunFd, nint cfg)
    {
        try
        {
            return HostRuntime.Start(tunFd, cfg);
        }
        catch
        {
            return TbtErr.Attach;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "tbt_stop")]
    public static void Stop()
    {
        try
        {
            HostRuntime.Stop();
        }
        catch
        {
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "tbt_stats")]
    public static int Stats(nint buf, int len)
    {
        try
        {
            return HostRuntime.Stats(buf, len);
        }
        catch
        {
            return TbtErr.Args;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "tbt_set_protect")]
    public static int SetProtect(nint fn, nint ctx)
    {
        try
        {
            return HostRuntime.SetProtect(fn, ctx);
        }
        catch
        {
            return TbtErr.Args;
        }
    }
}
