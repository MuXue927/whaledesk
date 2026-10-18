namespace WhaleDesk.Interaction;

internal sealed record SedentaryNudge(int ActiveMinutes, int NudgeCount);

/// <summary>
/// "You have been sitting here for an hour" - detected from real input activity, not from a
/// wall-clock timer: the counter only runs while the user is actually touching the keyboard or
/// mouse, a real break (idle for several minutes) resets it, and it stays quiet outside working
/// hours and during the snooze window after a nudge.
///
/// Pure over the samples it is fed, so --selftest can pin every rule.
/// </summary>
internal sealed class SedentaryWatch
{
    /// <summary>Continuous activity that earns a nudge (default 50 minutes).</summary>
    public int RemindAfterMinutes { get; set; } = 50;

    /// <summary>Idle for this long counts as a break and resets the counter (default 5 min).</summary>
    public int BreakIdleSeconds { get; set; } = 300;

    /// <summary>Quiet period after a nudge (default 30 min).</summary>
    public int SnoozeMinutes { get; set; } = 30;

    /// <summary>Below this idle time the user counts as active (default 60 s).</summary>
    public int ActiveIdleSeconds { get; set; } = 60;

    /// <summary>Only nudge inside these hours.</summary>
    public int WorkStartHour { get; set; } = 9;
    public int WorkEndHour { get; set; } = 23;

    private long _activeSinceMs;
    private long _lastNudgeMs;
    private int _nudges;

    public int NudgeCount => _nudges;

    /// <summary>Feed one idle-time sample; returns a nudge when it is time to stand up.</summary>
    public SedentaryNudge? Sample(int idleSeconds, long nowMs, int hourOfDay)
    {
        if (hourOfDay < WorkStartHour || hourOfDay >= WorkEndHour) return null;

        if (idleSeconds >= BreakIdleSeconds)
        {
            // a real break: the sitting streak is over
            _activeSinceMs = 0;
            _nudges = 0;
            _lastNudgeMs = 0;
            return null;
        }

        if (idleSeconds > ActiveIdleSeconds) return null;   // neither working nor resting

        if (_activeSinceMs == 0) _activeSinceMs = nowMs;
        var activeMs = nowMs - _activeSinceMs;
        if (activeMs < RemindAfterMinutes * 60_000L) return null;

        if (_lastNudgeMs != 0 && nowMs - _lastNudgeMs < SnoozeMinutes * 60_000L) return null;

        _lastNudgeMs = nowMs;
        _nudges++;
        // Restart the streak from the nudge, so the next one is another full interval away.
        _activeSinceMs = nowMs;
        return new SedentaryNudge((int)(activeMs / 60_000), _nudges);
    }

    public void Reset()
    {
        _activeSinceMs = 0;
        _lastNudgeMs = 0;
        _nudges = 0;
    }
}
