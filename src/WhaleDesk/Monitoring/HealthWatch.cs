namespace WhaleDesk.Monitoring;

/// <summary>
/// "The link to DSH is flaky" detector.
///
/// A single failed poll is normal (DSH restarting, a busy moment). What is worth telling the
/// user is a *burst* of failures inside a short window — that usually means DSH is restarting
/// or the loopback port is taken. One gentle notice per cooldown window.
/// </summary>
internal sealed class HealthWatch
{
    /// <summary>Failures are counted inside this window (default 2 minutes).</summary>
    public int WindowMs { get; set; } = 120_000;

    /// <summary>How many failures in the window count as flaky (default 5).</summary>
    public int FailuresToWarn { get; set; } = 5;

    /// <summary>Do not repeat the notice more often than this (default 10 minutes).</summary>
    public int CooldownMs { get; set; } = 10 * 60_000;

    private readonly List<long> _failures = new();
    private long _lastWarnAtMs = long.MinValue / 2;

    /// <summary>Feed one poll result; returns true when the user should be told.</summary>
    public bool Record(bool failed, long now)
    {
        if (!failed)
        {
            _failures.Clear();   // any success means the link works again
            return false;
        }

        _failures.Add(now);
        _failures.RemoveAll(t => now - t > WindowMs);
        if (_failures.Count < FailuresToWarn) return false;
        if (now - _lastWarnAtMs < CooldownMs) return false;
        _lastWarnAtMs = now;
        return true;
    }

    public int RecentFailures(long now)
    {
        _failures.RemoveAll(t => now - t > WindowMs);
        return _failures.Count;
    }
}
