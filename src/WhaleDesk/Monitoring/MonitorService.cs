using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

namespace WhaleDesk.Monitoring;

internal sealed record ProcessRow(string Name, int Pid, double CpuPercent, ulong WorkingSet, ulong IoBytesPerSecond = 0);

/// <summary>
/// One app family (all processes sharing a name). VS Code / Chrome are multi-process, so the
/// panel must show and act on the family: listing single child processes meant clicking a
/// renderer and leaving the app running (user report).
/// </summary>
internal sealed record HogRow(string Name, int Count, ulong WorkingSet, double MaxCpuPercent, IReadOnlyList<int> Pids, ulong IoBytesPerSecond)
{
    /// <summary>True when the family is doing real work right now (CPU or disk).</summary>
    public bool IsActive(double cpuThreshold, ulong ioThreshold) =>
        MaxCpuPercent >= cpuThreshold || IoBytesPerSecond >= ioThreshold;
}

internal sealed record Snapshot(
    double CpuPercent,
    ulong MemTotal, ulong MemAvailable, uint MemLoadPercent,
    bool GpuAvailable, double GpuPercent, ulong GpuDedicatedBytes,
    string DiskName, ulong DiskFree, ulong DiskTotal, double DiskBusyPercent,
    double NetDownBps, double NetUpBps,
    IReadOnlyList<ProcessRow> TopProcesses,
    /// <summary>Every app family this sample (memory summed, IO rate, CPU peak). The panel takes
    /// the top few; the OS task monitor needs all of them.</summary>
    IReadOnlyList<HogRow> Families,
    string? Error)
{
    public double DiskFreePercent => DiskTotal == 0 ? 0 : DiskFree * 100.0 / DiskTotal;
}

/// <summary>
/// Samples the machine once a second on a background thread and publishes an
/// immutable snapshot. Everything degrades instead of throwing: a machine without a
/// usable GPU counter set still gets CPU/memory/disk/network, and the panel says so.
/// </summary>
internal sealed class MonitorService : IDisposable
{
    private readonly Log _log;
    private PdhQuery _gpuQuery = new();
    private readonly List<(string Path, IntPtr Counter)> _gpuEngineCounters = new();
    private PdhQuery _gpuMemoryQuery = new();
    private readonly List<(string Path, IntPtr Counter)> _gpuMemoryCounters = new();
    private readonly PdhQuery _disk = new();
    private readonly IntPtr _diskCounter;
    private readonly bool _diskOk;

    private readonly Dictionary<int, (Process Proc, TimeSpan Cpu, long Stamp)> _procCache = new();
    private readonly string _systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

    private SysInfo.CpuTimes _lastCpu;
    private bool _hasCpuBaseline;
    private long _lastNetStamp;
    private long _lastNetDown;
    private long _lastNetUp;
    private int _tick;
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile int _intervalMs = 1000;
    private string _gpuNote = "";

    /// <summary>Change the sampling period (low-memory mode, or nobody looking at the panel).</summary>
    public void SetInterval(int milliseconds) => _intervalMs = Math.Clamp(milliseconds, 250, 60_000);

    public Snapshot Latest { get; private set; } = new(0, 0, 0, 0, false, 0, 0, "C:", 0, 0, 0, 0, 0, Array.Empty<ProcessRow>(), Array.Empty<HogRow>(), "尚未采样");
    public event Action<Snapshot>? Updated;

    public MonitorService(Log log)
    {
        _log = log;

        // GPU: expand the wildcards, register each instance ONCE and re-register
        // periodically (new processes appear constantly). Registering inside the
        // sample loop instead leaked a counter handle per second.
        RebuildGpuCounters();

        if (_disk.AddCounter(@"\LogicalDisk(_Total)\% Disk Time", out _diskCounter)) _diskOk = true;
        else _log.Warn($"disk counter unavailable ({_disk.LastError})");

        SysInfo.TryGetCpuTimes(out _lastCpu);
        _hasCpuBaseline = true;

        // Percentage/rate counters need two collections before they produce a value;
        // without this prime the first GPU/disk reading is always 0.
        PrimeCounters();
    }

    private void PrimeCounters()
    {
        _gpuQuery.Collect();
        _gpuMemoryQuery.Collect();
        if (_diskOk) _disk.Collect();
        Thread.Sleep(150);
        _gpuQuery.Collect();
        _gpuMemoryQuery.Collect();
        if (_diskOk) _disk.Collect();
    }

    private void RebuildGpuCounters()
    {
        _gpuQuery.Dispose();
        _gpuQuery = new PdhQuery();
        _gpuEngineCounters.Clear();
        var enginePaths = PdhQuery.ExpandVerbose(@"\GPU Engine(*)\Utilization Percentage", out var engineDiag);
        foreach (var path in enginePaths)
            if (_gpuQuery.AddCounter(path, out var counter)) _gpuEngineCounters.Add((path, counter));

        _gpuMemoryQuery.Dispose();
        _gpuMemoryQuery = new PdhQuery();
        _gpuMemoryCounters.Clear();
        var memoryPaths = PdhQuery.ExpandVerbose(@"\GPU Adapter Memory(*)\Dedicated Usage", out var memoryDiag);
        foreach (var path in memoryPaths)
            if (_gpuMemoryQuery.AddCounter(path, out var counter)) _gpuMemoryCounters.Add((path, counter));

        _gpuNote = $"{_gpuEngineCounters.Count} engine / {_gpuMemoryCounters.Count} adapter-memory instances";
        _log.Info($"GPU counters registered: {_gpuNote}");
        _log.Info($"GPU expand diagnostics: engines[{engineDiag}] sample={(enginePaths.Count > 0 ? enginePaths[0] : "none")}");
        _log.Info($"GPU expand diagnostics: memory[{memoryDiag}] sample={(memoryPaths.Count > 0 ? memoryPaths[0] : "none")}");
    }

    public void Start(int intervalMs = 1000)
    {
        _intervalMs = Math.Clamp(intervalMs, 250, 60_000);
        _thread = new Thread(() =>
        {
            while (!_stopping)
            {
                try { Sample(); }
                catch (Exception ex) { _log.Warn("monitor sample failed: " + ex.Message); }
                Thread.Sleep(_intervalMs);
            }
        })
        { IsBackground = true, Name = "WhaleDesk.Monitor" };
        _thread.Start();
        _log.Info($"monitor service started (interval {intervalMs}ms)");
    }

    internal Snapshot Sample()
    {
        _tick++;
        string? error = null;

        // ---- CPU ----
        var cpu = 0.0;
        if (SysInfo.TryGetCpuTimes(out var times))
        {
            if (_hasCpuBaseline) cpu = SysInfo.CpuLoadPercent(_lastCpu, times);
            _lastCpu = times;
            _hasCpuBaseline = true;
        }
        else error = "CPU 时间不可读";

        // ---- memory ----
        ulong memTotal = 0, memAvail = 0;
        uint memLoad = 0;
        if (SysInfo.TryGetMemory(out var mem))
        {
            memTotal = mem.TotalBytes;
            memAvail = mem.AvailableBytes;
            memLoad = mem.LoadPercent;
        }

        // ---- GPU ----
        var gpuAvailable = false;
        var gpu = 0.0;
        ulong vram = 0;
        if (_tick % 30 == 1) RebuildGpuCounters();
        if (_gpuEngineCounters.Count > 0 && _gpuQuery.Collect())
        {
            var samples = new List<(string, double)>();
            foreach (var (path, counter) in _gpuEngineCounters)
                if (_gpuQuery.TryRead(counter, out var value)) samples.Add((path, value));
            if (samples.Count > 0)
            {
                gpu = PdhQuery.AggregateGpuEngine(samples);
                gpuAvailable = true;
            }
        }
        if (_gpuMemoryCounters.Count > 0 && _gpuMemoryQuery.Collect())
        {
            foreach (var (_, counter) in _gpuMemoryCounters)
                if (_gpuMemoryQuery.TryRead(counter, out var bytes)) vram = Math.Max(vram, (ulong)Math.Max(0, bytes));
        }

        // ---- disk ----
        ulong diskFree = 0, diskTotal = 0;
        try
        {
            var drive = new DriveInfo(_systemDrive);
            if (drive.IsReady) { diskFree = (ulong)drive.AvailableFreeSpace; diskTotal = (ulong)drive.TotalSize; }
        }
        catch (Exception ex) { error ??= "磁盘信息不可读: " + ex.Message; }

        var diskBusy = 0.0;
        if (_diskOk && _disk.Collect() && _disk.TryRead(_diskCounter, out var busy)) diskBusy = busy;

        // ---- network ----
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = nic.GetIPv4Statistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
        }
        catch { /* network stats are optional */ }

        var now = Environment.TickCount64;
        var netDownBps = 0.0;
        var netUpBps = 0.0;
        if (_lastNetStamp != 0)
        {
            var seconds = Math.Max(0.2, (now - _lastNetStamp) / 1000.0);
            netDownBps = Math.Max(0, (down - _lastNetDown) / seconds);
            netUpBps = Math.Max(0, (up - _lastNetUp) / seconds);
        }
        _lastNetStamp = now;
        _lastNetDown = down;
        _lastNetUp = up;

        var snapshot = new Snapshot(
            cpu, memTotal, memAvail, memLoad,
            gpuAvailable, gpu, vram,
            _systemDrive, diskFree, diskTotal, diskBusy,
            netDownBps, netUpBps,
            TopProcesses(cpu > 0 ? 5 : 5), TopHogs(int.MaxValue),
            error);

        Latest = snapshot;
        Updated?.Invoke(snapshot);
        return snapshot;
    }


    /// <summary>Top app families by working set (see HogRow).</summary>
    private IReadOnlyList<HogRow> TopHogs(int families)
    {
        var rows = new List<ProcessRow>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var cpuTime = proc.TotalProcessorTime;
                double percent = 0;
                if (_procCache.TryGetValue(proc.Id, out var previous))
                {
                    var seconds = Math.Max(0.2, (Environment.TickCount64 - previous.Stamp) / 1000.0);
                    percent = Math.Max(0, (cpuTime - previous.Cpu).TotalMilliseconds / 1000.0) / (seconds * Environment.ProcessorCount) * 100.0;
                }
                var io = Performance.TaskMonitor.IsMonitored(proc.ProcessName)
                    ? Performance.ProcessIo.BytesPerSecond(proc.Id, Environment.TickCount64)
                    : 0;
                rows.Add(new ProcessRow(proc.ProcessName, proc.Id, Math.Clamp(percent, 0, 100 * Environment.ProcessorCount), (ulong)proc.WorkingSet64, io));
            }
            catch { }
            finally { proc.Dispose(); }
        }
        Performance.ProcessIo.Prune(rows.Select(r => r.Pid).ToList());
        return AggregateByName(rows, families);
    }

    /// <summary>
    /// Pure aggregation (asserted by --selftest): one row per process name, memory summed,
    /// the busiest instance's CPU kept so the "is it working?" guard still works, PIDs kept so
    /// the kill can cover the whole family.
    /// </summary>
    internal static IReadOnlyList<HogRow> AggregateByName(IEnumerable<ProcessRow> rows, int families)
    {
        return rows
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HogRow(
                g.Key,
                g.Count(),
                g.Aggregate(0UL, (sum, r) => sum + r.WorkingSet),
                g.Max(r => r.CpuPercent),
                g.Select(r => r.Pid).OrderBy(p => p).ToList(),
                g.Aggregate(0UL, (sum, r) => sum + r.IoBytesPerSecond)))
            .OrderByDescending(h => h.WorkingSet)
            .Take(families)
            .ToList();
    }

    private IReadOnlyList<ProcessRow> TopProcesses(int count)
    {
        var now = Environment.TickCount64;
        var rows = new List<ProcessRow>();
        var cores = Environment.ProcessorCount;
        var seen = new HashSet<int>();

        foreach (var proc in Process.GetProcesses())
        {
            seen.Add(proc.Id);
            try
            {
                var cpuTime = proc.TotalProcessorTime;
                var ws = (ulong)proc.WorkingSet64;
                var name = proc.ProcessName;
                double percent = 0;
                if (_procCache.TryGetValue(proc.Id, out var previous))
                {
                    var seconds = Math.Max(0.2, (now - previous.Stamp) / 1000.0);
                    percent = Math.Max(0, (cpuTime - previous.Cpu).TotalMilliseconds / 1000.0) / (seconds * cores) * 100.0;
                }
                _procCache[proc.Id] = (proc, cpuTime, now);
                rows.Add(new ProcessRow(name, proc.Id, Math.Clamp(percent, 0, 100 * cores), ws));
            }
            catch { /* process exited between enumeration and read */ }
            finally { proc.Dispose(); }
        }

        foreach (var pid in _procCache.Keys.Where(k => !seen.Contains(k)).ToList()) _procCache.Remove(pid);

        return rows
            .OrderByDescending(r => r.CpuPercent)
            .ThenByDescending(r => r.WorkingSet)
            .Take(count)
            .ToList();
    }

    public void Dispose()
    {
        _stopping = true;
        try { _thread?.Join(600); } catch { }
        _gpuQuery.Dispose();
        _gpuMemoryQuery.Dispose();
        _disk.Dispose();
    }
}
