namespace WhaleDesk.Interaction;

internal sealed record AppTooLong(string Name, long ForegroundMs);

/// <summary>
/// The "you have had this app in front for an hour" nudge.
///
/// This is deliberately NOT based on process lifetime: a music player can be open all day and
/// that is not a problem. What this measures is time spent in the FOREGROUND, so it only fires
/// for an app the user actually sat in front of.
/// </summary>
internal sealed class ForegroundWatch
{
    /// <summary>Foreground time that counts as "too long" (default one hour).</summary>
    public int ThresholdMs { get; set; } = 60 * 60_000;

    /// <summary>Do not nag about the same app more often than this.</summary>
    public int CooldownMs { get; set; } = 3 * 60 * 60_000;

    /// <summary>A single sample can never add more than this (sleep/hibernate, clock jumps).</summary>
    public int MaxSampleDeltaMs { get; set; } = 5_000;

    private readonly Dictionary<string, long> _foregroundMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _lastReminded = new(StringComparer.OrdinalIgnoreCase);
    private string? _last;
    private long _lastAt;

    /// <summary>Apps we never nag about: shells, system UI, and ourselves.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "shellexperiencehost", "startmenuexperiencehost", "searchhost",
        "textinputhost", "applicationframehost", "systemsettings", "lockapp", "whaledesk",
        "taskmgr", "conhost", "backgroundtaskhost",
    };

    /// <summary>Call about once a second with the process owning the foreground window.</summary>
    public void Sample(string? processName, long now)
    {
        var previous = _last;
        var previousAt = _lastAt;
        _last = processName;
        _lastAt = now;

        if (previous is null || previousAt == 0) return;
        if (string.IsNullOrWhiteSpace(previous) || Ignored.Contains(previous)) return;

        var delta = now - previousAt;
        if (delta <= 0) return;
        if (delta > MaxSampleDeltaMs) delta = MaxSampleDeltaMs;

        _foregroundMs[previous] = _foregroundMs.GetValueOrDefault(previous) + delta;
    }

    public long ForegroundMs(string name) => _foregroundMs.GetValueOrDefault(name);

    /// <summary>Returns a reminder at most once per cooldown window, for the first app that crossed the threshold.</summary>
    public AppTooLong? TakeReminder(long now)
    {
        foreach (var (name, total) in _foregroundMs.OrderByDescending(kv => kv.Value))
        {
            if (total < ThresholdMs) break;
            if (_lastReminded.TryGetValue(name, out var last) && now - last < CooldownMs) continue;
            _lastReminded[name] = now;
            return new AppTooLong(name, total);
        }
        return null;
    }

    /// <summary>Used when the user resets the counters (e.g. after acknowledging).</summary>
    public void Reset()
    {
        _foregroundMs.Clear();
        _lastReminded.Clear();
        _last = null;
        _lastAt = 0;
    }
}
