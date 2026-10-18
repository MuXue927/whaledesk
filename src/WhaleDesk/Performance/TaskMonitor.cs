using WhaleDesk.Monitoring;

namespace WhaleDesk.Performance;

internal enum OsTaskEnd
{
    /// <summary>Activity fell back to idle but the process is still alive (a GUI app finishing a job).</summary>
    Stopped,
    /// <summary>The process exited.</summary>
    Exited,
}

internal sealed record OsTaskFinished(
    string Name,
    int ProcessCount,
    long ActiveMs,
    double PeakCpuPercent,
    ulong PeakIoBytesPerSecond,
    ulong TotalIoBytes,
    OsTaskEnd How);

/// <summary>
/// OS-level "long task" detection, independent of DSH.
///
/// The key insight (from the user's question about a music player running for an hour):
/// **process lifetime is not a task**. What matters is sustained *work* — CPU above a threshold
/// or disk I/O above a threshold. A music player uses 1-2% CPU and no disk, so it never
/// triggers; a compiler / a copy / a script does.
///
/// Pure state machine over one sample per invocation, so --selftest can pin:
///  * idle apps never trigger, however long they stay open,
///  * CPU-busy work triggers after the duration threshold,
///  * pure I/O work (a big folder copy) triggers too,
///  * below-threshold activity clears the run only after a grace period,
///  * system/daemon processes are never reported.
/// </summary>
internal sealed class TaskMonitor
{
    /// <summary>CPU% of the whole machine above which a process counts as working.</summary>
    public double CpuActivePercent { get; set; } = 5.0;

    /// <summary>Disk bytes/second above which a process counts as working (a copy).</summary>
    public ulong IoActiveBytesPerSecond { get; set; } = 5_000_000;

    /// <summary>How long the work must last before it is worth announcing.</summary>
    public int LongTaskMs { get; set; } = 3 * 60_000;

    /// <summary>A fast copy can finish in seconds: a large TOTAL transfer also counts
    /// (this was the user's point - "as soon as I copy a big folder, tell me").</summary>
    public ulong BigTotalIoBytes { get; set; } = 1_000_000_000;   // 1 GB

    /// <summary>Floor for the total-bytes rule, so a 2-second burst never counts.</summary>
    public int MinTaskMs { get; set; } = 10_000;

    /// <summary>How long the activity must stay below the thresholds to count as finished.</summary>
    public int IdleGraceMs { get; set; } = 60_000;

    private sealed class Run
    {
        public long ActiveSince;
        public long LastActiveAt;
        public long LastSampleAt;
        public double PeakCpu;
        public ulong PeakIo;
        public ulong TotalIo;
        public int Count;
    }

    private readonly Dictionary<string, Run> _runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Never reported: core Windows services and daemons. Note that explorer.exe is
    /// NOT in this set: Explorer performs folder copies, and the user wants to know about them.</summary>
    private static readonly HashSet<string> NeverReport = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "memory compression", "secure system",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "lsm",
        "svchost", "dwm", "sihost", "fontdrvhost", "taskhostw", "ctfmon",
        "audiodg", "spoolsv", "wudfhost", "conhost", "runtimebroker", "searchhost",
        "searchindexer", "msmpeng", "securityhealthservice", "nissrv", "smartscreen",
        "wmiprvse", "dllhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "systemsettings", "applicationframehost",
        "whaledesk",          // ourselves
        "deepseek harness",   // the DSH host: its turns have their own monitor
    };

    public static bool IsMonitored(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !NeverReport.Contains(name.Trim());

    public int TrackedCount => _runs.Count;

    /// <summary>
    /// Feed one sample of app families. Returns the tasks that finished on this sample.
    /// Pass null to mean "sampling failed": nothing is ended, nothing is reported.
    /// </summary>
    public IReadOnlyList<OsTaskFinished> Observe(IEnumerable<HogRow>? families, long now)
    {
        if (families is null) return Array.Empty<OsTaskFinished>();

        var finished = new List<OsTaskFinished>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var family in families)
        {
            if (!IsMonitored(family.Name)) continue;
            seen.Add(family.Name);

            var active = family.MaxCpuPercent >= CpuActivePercent || family.IoBytesPerSecond >= IoActiveBytesPerSecond;
            if (!_runs.TryGetValue(family.Name, out var run))
            {
                if (!active) continue;
                _runs[family.Name] = new Run
                {
                    ActiveSince = now, LastActiveAt = now, LastSampleAt = now,
                    PeakCpu = family.MaxCpuPercent, PeakIo = family.IoBytesPerSecond,
                    TotalIo = family.IoBytesPerSecond / 60,   // ~one sample worth of bytes
                    Count = family.Count,
                };
                continue;
            }

            run.Count = family.Count;
            var sampleMs = Math.Max(0, now - run.LastSampleAt);
            run.LastSampleAt = now;
            if (active)
            {
                run.LastActiveAt = now;
                if (family.MaxCpuPercent > run.PeakCpu) run.PeakCpu = family.MaxCpuPercent;
                if (family.IoBytesPerSecond > run.PeakIo) run.PeakIo = family.IoBytesPerSecond;
                // Integrate the rate so the total transferred is known even when the copy is
                // far shorter than the duration threshold.
                run.TotalIo += (ulong)(family.IoBytesPerSecond * (sampleMs / 1000.0));
            }
        }

        // Anything tracked whose process disappeared is finished (Exited).
        foreach (var name in _runs.Keys.ToList())
        {
            if (seen.Contains(name)) continue;
            var run = _runs[name];
            _runs.Remove(name);
            AddIfWorth(finished, name, run, run.LastActiveAt, OsTaskEnd.Exited);
        }

        // Anything tracked that stopped working is finished once the grace period elapses.
        foreach (var (name, run) in _runs.ToList())
        {
            if (now - run.LastActiveAt < IdleGraceMs) continue;
            _runs.Remove(name);
            AddIfWorth(finished, name, run, run.LastActiveAt, OsTaskEnd.Stopped);
        }

        if (_runs.Count > 200)
        {
            // pathological churn: forget the oldest idle runs
            foreach (var name in _runs.OrderByDescending(kv => kv.Value.LastActiveAt).Skip(100).Select(kv => kv.Key).ToList())
                _runs.Remove(name);
        }

        return finished;
    }

    private void AddIfWorth(List<OsTaskFinished> list, string name, Run run, long endedAt, OsTaskEnd how)
    {
        var activeMs = Math.Max(0, endedAt - run.ActiveSince);
        var longEnough = activeMs >= LongTaskMs;
        var bigEnough = run.TotalIo >= BigTotalIoBytes && activeMs >= MinTaskMs;
        if (!longEnough && !bigEnough) return;
        list.Add(new OsTaskFinished(name, run.Count, activeMs, run.PeakCpu, run.PeakIo, run.TotalIo, how));
    }

    /// <summary>Forget everything (e.g. when the user turns the feature off and on again).</summary>
    public void Reset() => _runs.Clear();
}
