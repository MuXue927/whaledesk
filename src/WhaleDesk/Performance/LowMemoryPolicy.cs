namespace WhaleDesk.Performance;

/// <summary>
/// The low-memory policy: pure decisions, so --selftest can assert the intervals without
/// running the app. Two effects are separated:
///  * sampling is only as fast as its consumer — with the monitoring panel closed nobody
///    reads the snapshot, so 1 Hz was pure waste even in normal mode;
///  * low-memory mode tightens everything further and drops render quality.
/// </summary>
internal sealed class LowMemoryPolicy
{
    public bool Enabled { get; set; }
    /// <summary>Turn itself on when free memory gets tight.</summary>
    public bool Auto { get; set; } = true;

    public const int AutoEnableFreePercent = 12;

    public int MonitorIntervalMs(bool panelOpen) => (Enabled, panelOpen) switch
    {
        (false, true) => 1000,
        (false, false) => 5000,      // nobody is looking at it
        (true, true) => 2000,
        (true, false) => 20000,
    };

    public int FrameIntervalMs => Enabled ? 66 : 33;

    public bool LowQualityRendering => Enabled;

    /// <summary>Auto-enable once free physical memory falls below the threshold.</summary>
    public static bool ShouldAutoEnable(ulong availableBytes, ulong totalBytes, bool alreadyEnabled)
    {
        if (alreadyEnabled) return false;
        if (totalBytes == 0) return false;
        return availableBytes * 100 / totalBytes < AutoEnableFreePercent;
    }

    public string Describe(int sampledIntervalMs) =>
        Enabled
            ? $"低内存模式：开（采样 {sampledIntervalMs}ms / 帧 {FrameIntervalMs}ms / 低画质）"
            : $"低内存模式：关（采样 {sampledIntervalMs}ms / 帧 {FrameIntervalMs}ms / 高画质）";
}
