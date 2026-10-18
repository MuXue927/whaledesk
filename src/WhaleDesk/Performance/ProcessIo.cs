using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WhaleDesk.Performance;

/// <summary>
/// Per-process disk I/O rate, for the "large folder copy / big file move" case that CPU alone
/// misses. Uses GetProcessIoCounters (cumulative transfer counts) and reports the delta.
///
/// No administrator rights needed for same-user processes; protected processes simply fail to
/// open and are reported as 0, which is fine because their work is not attributable anyway.
/// </summary>
internal static class ProcessIo
{
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private static readonly Dictionary<int, (long Stamp, ulong Bytes)> Cache = new();

    /// <summary>Bytes per second since the previous call for this pid (0 on the first call).</summary>
    public static ulong BytesPerSecond(int pid, long nowMs)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return 0;
            if (!GetProcessIoCounters(handle, out var counters)) return 0;

            var total = counters.ReadTransferCount + counters.WriteTransferCount;
            if (Cache.TryGetValue(pid, out var previous))
            {
                var seconds = Math.Max(0.2, (nowMs - previous.Stamp) / 1000.0);
                var delta = total >= previous.Bytes ? total - previous.Bytes : 0;
                Cache[pid] = (nowMs, total);
                return (ulong)(delta / seconds);
            }

            Cache[pid] = (nowMs, total);
            return 0;
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    /// <summary>Drop entries for processes that are gone, so the cache cannot grow forever.</summary>
    public static void Prune(IReadOnlyCollection<int> alivePids)
    {
        if (Cache.Count <= alivePids.Count * 2) return;
        var alive = new HashSet<int>(alivePids);
        foreach (var pid in Cache.Keys.Where(p => !alive.Contains(p)).ToList()) Cache.Remove(pid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
