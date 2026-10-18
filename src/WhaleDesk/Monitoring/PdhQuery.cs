using System.Runtime.InteropServices;
using System.Text;

namespace WhaleDesk.Monitoring;

/// <summary>
/// Thin PDH wrapper. English counter paths are used on purpose: the localized names
/// differ per Windows display language, so "\\GPU Engine(*)\\Utilization Percentage"
/// is resolved through PdhAddEnglishCounter instead of PdhAddCounter.
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_MORE_DATA = 0x800007D2;
    private const uint ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
        [FieldOffset(8)] public long LongValue;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    // Buffer type matters: with StringBuilder the marshaller copies back only up to
    // the first NUL, so a multi-string result (405 GPU engine instances) collapsed to
    // exactly one entry. A char[] is copied back whole.
    [DllImport("pdh.dll", EntryPoint = "PdhExpandWildCardPathW", CharSet = CharSet.Unicode)]
    private static extern uint PdhExpandWildCardPath(string? dataSource, string path, [Out] char[]? buffer, ref uint length, uint flags);

    [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    private static extern uint PdhCloseQuery(IntPtr query);

    private readonly IntPtr _query;
    private readonly List<IntPtr> _counters = new();
    private bool _disposed;

    public bool IsOpen => _query != IntPtr.Zero;
    public string LastError { get; private set; } = "";

    public PdhQuery()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != ERROR_SUCCESS) _query = IntPtr.Zero;
    }

    public bool AddCounter(string path, out IntPtr counter)
    {
        counter = IntPtr.Zero;
        if (!IsOpen) return false;
        var rc = PdhAddEnglishCounter(_query, path, IntPtr.Zero, out counter);
        if (rc != ERROR_SUCCESS)
        {
            LastError = $"0x{rc:X8}";
            return false;
        }
        _counters.Add(counter);
        return true;
    }

    public bool Collect() => IsOpen && PdhCollectQueryData(_query) == ERROR_SUCCESS;

    public bool TryRead(IntPtr counter, out double value, bool noCap100 = false)
    {
        value = 0;
        if (!IsOpen || counter == IntPtr.Zero) return false;
        var rc = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | (noCap100 ? PDH_FMT_NOCAP100 : 0), out _, out var v);
        if (rc != ERROR_SUCCESS) return false;
        value = v.DoubleValue;
        return true;
    }

    /// <summary>Expand a wildcard counter path into concrete instance paths.</summary>
    public static IReadOnlyList<string> Expand(string wildcard) => ExpandVerbose(wildcard, out _);

    /// <summary>
    /// Expand with diagnostics. The two-call protocol (size probe, then buffer) is
    /// easy to get subtly wrong, so the raw lengths and return codes are surfaced.
    /// </summary>
    public static IReadOnlyList<string> ExpandVerbose(string wildcard, out string diag)
    {
        var results = new List<string>();
        uint length = 0;
        var first = PdhExpandWildCardPath(null, wildcard, null, ref length, 0);
        if (first != PDH_MORE_DATA && first != ERROR_SUCCESS)
        {
            diag = $"first call rc=0x{first:X8} (not MORE_DATA)";
            return results;
        }
        if (length == 0)
        {
            diag = "first call reported length 0";
            return results;
        }
        var capacity = (int)length;
        var buffer = new char[capacity];
        var second = PdhExpandWildCardPath(null, wildcard, buffer, ref length, 0);
        if (second != ERROR_SUCCESS)
        {
            diag = $"second call rc=0x{second:X8} capacity={capacity} need={length}";
            return results;
        }
        // The API writes a MULTI_SZ list ending with a double NUL; `length` is the
        // number of TCHARs used, so slice by it rather than trusting the array length.
        var used = (int)Math.Min(length == 0 ? capacity : length, capacity);
        var text = new string(buffer, 0, used);
        foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (!string.IsNullOrWhiteSpace(part)) results.Add(part.Trim());
        diag = $"first rc=0x{first:X8} length={capacity} second rc=0 used={used} entries={results.Count}";
        return results;
    }

    /// <summary>
    /// Task-Manager-style GPU aggregation: per engine TYPE the process instances are
    /// summed, then the busiest engine type is the adapter's utilisation. Summing
    /// across engine types instead would double count (a 3D + Copy workload).
    /// </summary>
    public static double AggregateGpuEngine(IEnumerable<(string Instance, double Value)> samples)
    {
        var byEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in samples)
        {
            var engine = ParseEngineType(instance);
            if (engine is null) continue;
            byEngine[engine] = byEngine.TryGetValue(engine, out var acc) ? acc + value : value;
        }
        return byEngine.Count == 0 ? 0 : Math.Min(100, byEngine.Values.Max());
    }

    /// <summary>"pid_17296_luid_0x0_0xdc14_phys_0_eng_0_engtype_3d" -> "3d".</summary>
    public static string? ParseEngineType(string instance)
    {
        var marker = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var rest = instance[(marker + "engtype_".Length)..];
        var end = rest.IndexOfAny(new[] { '_', '#' });
        return (end < 0 ? rest : rest[..end]).Trim();
    }

    /// <summary>Adapter LUID from an instance name, so one adapter can be isolated.</summary>
    public static string? ParseLuid(string instance)
    {
        var marker = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var rest = instance[(marker + 5)..];
        var end = rest.IndexOf("_phys", StringComparison.OrdinalIgnoreCase);
        return (end < 0 ? rest : rest[..end]).Trim();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsOpen) PdhCloseQuery(_query);
        _counters.Clear();
    }
}
