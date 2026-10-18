using System.Runtime.InteropServices;

namespace WhaleDesk.Monitoring;

/// <summary>Win32 system counters that need no PDH: CPU times and physical memory.</summary>
internal static class SysInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    public static bool TryGetCpuTimes(out CpuTimes times)
    {
        times = default;
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return false;
        times = new CpuTimes(idle.Value, kernel.Value, user.Value);
        return true;
    }

    /// <summary>
    /// CPU load between two samples. Kernel time already includes idle, so the busy
    /// share is (kernel+user - idle) / (kernel+user).
    /// </summary>
    public static double CpuLoadPercent(CpuTimes previous, CpuTimes current)
    {
        var idle = current.Idle - previous.Idle;
        var kernel = current.Kernel - previous.Kernel;
        var user = current.User - previous.User;
        var total = kernel + user;
        if (total == 0) return 0;
        var busy = total > idle ? total - idle : 0;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    public readonly record struct MemoryInfo(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent);

    public static bool TryGetMemory(out MemoryInfo info)
    {
        info = default;
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return false;
        info = new MemoryInfo(status.ullTotalPhys, status.ullAvailPhys, status.dwMemoryLoad);
        return true;
    }
}

/// <summary>Human-readable formatting shared by the panel and the log.</summary>
internal static class Fmt
{
    public static string Bytes(ulong bytes) => bytes switch
    {
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):0.0} GB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):0.0} MB",
        >= 1UL << 10 => $"{bytes / (double)(1UL << 10):0} KB",
        _ => $"{bytes} B",
    };

    public static string Percent(double value) => $"{Math.Clamp(value, 0, 100):0}%";

    /// <summary>Token counts read best as 1.23M / 45.6K / 789.</summary>
    public static string Tokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:0.##}B",
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.##}M",
        >= 1_000 => $"{tokens / 1_000.0:0.#}K",
        _ => tokens.ToString(),
    };
    public static string Rate(double bytesPerSecond) => $"{Bytes((ulong)Math.Max(0, bytesPerSecond))}/s";
}
