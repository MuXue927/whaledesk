namespace WhaleDesk.Interaction;

internal enum TaskOutcome
{
    None,
    Completed,
    Failed,
}

/// <summary>A long task just ended: how, how long, and in which phase.</summary>
internal sealed record TaskFinish(TaskOutcome Outcome, long DurationMs, string? LastPhase);

/// <summary>
/// Watches the DSH session for "long tasks": a turn that keeps running (tools, thinking,
/// reviewing, ...) for longer than a threshold and then ends. The desktop pet then tells the
/// user, which matters when they walked away from the machine.
///
/// Pure state machine over the polled /api/pet/state, so --selftest can pin the rules:
///  * only real work phases count as busy (not "idle"/"done"/"blocked"),
///  * a failure seen at any point during the run marks the outcome as failed,
///  * a short turn produces no announcement at all,
///  * DSH going offline ends a run silently (the outcome is unknown).
/// </summary>
internal sealed class TaskWatch
{
    public const int DefaultLongTaskMs = 3 * 60_000;

    /// <summary>Phases that mean "this turn is working". Compared case-insensitively.</summary>
    private static readonly HashSet<string> BusyPhases = new(StringComparer.OrdinalIgnoreCase)
    {
        "prepare", "waiting", "thinking", "review", "tool", "toolResult", "toolresult",
        "read", "write", "edit", "running", "preparing",
    };

    private long _busySince;
    private bool _busy;
    private bool _sawFailure;
    private string? _lastPhase;

    public int LongTaskThresholdMs { get; set; } = DefaultLongTaskMs;

    public bool IsBusy => _busy;

    public long BusyElapsedMs(long now) => _busy ? Math.Max(0, now - _busySince) : 0;

    public bool IsLongTask(long now) => _busy && BusyElapsedMs(now) >= LongTaskThresholdMs;

    /// <summary>
    /// Feed one polled state. Returns a finished long task exactly once, when a run that
    /// lasted at least the threshold ends.
    /// </summary>
    public TaskFinish? Observe(PetState? state, long now)
    {
        if (state is null)
        {
            // DSH went away: end the run, but do not guess the outcome.
            _busy = false;
            _sawFailure = false;
            _lastPhase = null;
            return null;
        }

        var phase = state.Phase;
        var failed = string.Equals(phase, "failed", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(state.Animation, "failed", StringComparison.OrdinalIgnoreCase)
                     || (phase?.Contains("fail", StringComparison.OrdinalIgnoreCase) ?? false);
        var busy = phase is not null && BusyPhases.Contains(phase);

        if (busy)
        {
            if (!_busy)
            {
                _busy = true;
                _busySince = now;
                _sawFailure = false;
            }
            if (failed) _sawFailure = true;
            _lastPhase = phase;
            return null;
        }

        if (!_busy) return null;

        var duration = Math.Max(0, now - _busySince);
        var outcome = _sawFailure || failed ? TaskOutcome.Failed : TaskOutcome.Completed;
        var last = _lastPhase;
        _busy = false;
        _sawFailure = false;
        _lastPhase = null;
        return duration >= LongTaskThresholdMs ? new TaskFinish(outcome, duration, last) : null;
    }

    /// <summary>Human label for a phase, used by the status strip.</summary>
    public static string PhaseLabel(string? phase) => phase?.ToLowerInvariant() switch
    {
        null or "" => "空闲",
        "prepare" or "preparing" => "准备中",
        "waiting" => "等回复",
        "thinking" => "思考中",
        "review" => "整理中",
        "tool" or "toolresult" => "工具中",
        "read" => "读文件",
        "write" => "写文件",
        "edit" => "改文件",
        "failed" => "出错了",
        "blocked" => "等待确认",
        _ => phase!,
    };
}
