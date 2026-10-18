namespace WhaleDesk.Interaction;

/// <summary>What a hidden interaction produced.</summary>
internal enum Reaction
{
    None,
    Combo1,          // 3 quick clicks
    Combo2,          // 6 quick clicks
    Dizzy,           // too many clicks, or shaken side to side
    LongPress,       // held without moving
    Overfed,         // fed too often too fast
    CornerSettle,    // parked in a corner for a while
    LateNight,       // past 2am
    HourlyChime,     // on the hour
    Comeback,        // interacted after a long silence
}

/// <summary>
/// The easter-egg triggers. All decisions are pure functions of (event, now) so the
/// behaviour can be asserted in --selftest without waiting for real seconds to pass.
/// </summary>
internal sealed class EasterEggs
{
    public const int ComboWindowMs = 1800;      // was 1200: six clicks in 1.2s was unreasonable
    public const int Combo1Count = 3;
    public const int Combo2Count = 6;
    public const int DizzyCount = 12;
    public const int LongPressMs = 1400;
    public const int FeedWindowMs = 60_000;
    public const int FeedOverloadCount = 4;
    public const int ShakeWindowMs = 2500;
    public const int ShakeReversals = 4;
    /// <summary>A direction change only counts once the cursor actually moved this far.</summary>
    public const int ShakeMinStepPx = 6;
    public const int CornerSettleMs = 2500;
    public const int ComebackSilenceMs = 20 * 60_000;

    private int _comboCount;
    private long _comboLastMs;
    private readonly List<long> _feedTimes = new();
    private readonly List<(long At, int Sign)> _shakeReversals = new();
    private int _shakeSign;
    private int _lastSampleDx;
    private long _lastInteractionMs;
    private long _cornerSinceMs;
    private string? _lastCorner;
    private bool _cornerFired;
    private int _lastHourlyHour = -1;
    private int _lastLateNightDay = -1;

    public int ComboCount => _comboCount;
    public int FeedsInWindow => _feedTimes.Count;

    /// <summary>Rapid clicks escalate: 3 -> cheer, 6 -> excited, 12 -> dizzy.</summary>
    public Reaction RegisterClick(long now)
    {
        var comeback = RegisterInteraction(now);
        _comboCount = now - _comboLastMs <= ComboWindowMs ? _comboCount + 1 : 1;
        _comboLastMs = now;
        if (_comboCount >= DizzyCount) return Reaction.Dizzy;
        if (_comboCount == Combo2Count) return Reaction.Combo2;
        if (_comboCount == Combo1Count) return Reaction.Combo1;
        return comeback;
    }

    /// <summary>Called when the button has been held past the threshold without moving.</summary>
    public Reaction RegisterLongPress(long now)
    {
        RegisterInteraction(now);
        return Reaction.LongPress;
    }

    /// <summary>Feeding too many times in a minute: she is full.</summary>
    public Reaction RegisterFeed(long now)
    {
        RegisterInteraction(now);
        _feedTimes.RemoveAll(t => now - t > FeedWindowMs);
        _feedTimes.Add(now);
        return _feedTimes.Count >= FeedOverloadCount ? Reaction.Overfed : Reaction.None;
    }

    /// <summary>
    /// A drag that keeps reversing direction is a "shake". Takes the TOTAL drag offset and
    /// derives the step itself: the previous version computed the sign in the window with a
    /// broken expression that always produced 0, so this easter egg was dead code.
    /// </summary>
    public Reaction RegisterDragSample(long now, int totalDx)
    {
        RegisterInteraction(now);
        var step = totalDx - _lastSampleDx;
        _lastSampleDx = totalDx;
        if (Math.Abs(step) < ShakeMinStepPx) return Reaction.None;
        var sign = Math.Sign(step);
        if (sign == _shakeSign) return Reaction.None;
        _shakeSign = sign;
        _shakeReversals.RemoveAll(r => now - r.At > ShakeWindowMs);
        _shakeReversals.Add((now, sign));
        return _shakeReversals.Count >= ShakeReversals ? Reaction.Dizzy : Reaction.None;
    }

    /// <summary>Kept for callers that already know the direction.</summary>
    public Reaction RegisterDragDirection(long now, int sign)
    {
        RegisterInteraction(now);
        if (sign == 0 || sign == _shakeSign) return Reaction.None;
        _shakeSign = sign;
        _shakeReversals.RemoveAll(r => now - r.At > ShakeWindowMs);
        _shakeReversals.Add((now, sign));
        return _shakeReversals.Count >= ShakeReversals ? Reaction.Dizzy : Reaction.None;
    }

    /// <summary>
    /// She has been parked in the same corner for a while. Fires ONCE per stay: the first
    /// version returned CornerSettle on every check, so the line repeated every second.
    /// </summary>
    public Reaction RegisterPlacement(long now, string corner)
    {
        if (!string.Equals(corner, _lastCorner, StringComparison.Ordinal))
        {
            _lastCorner = corner;
            _cornerSinceMs = now;
            _cornerFired = false;      // a new position re-arms the easter egg
            return Reaction.None;
        }
        if (corner == "none" || _cornerSinceMs == 0 || _cornerFired) return Reaction.None;
        if (now - _cornerSinceMs < CornerSettleMs) return Reaction.None;
        _cornerFired = true;
        return Reaction.CornerSettle;
    }

    /// <summary>On the hour, once per hour.</summary>
    public Reaction RegisterClock(long now, DateTime localNow)
    {
        if (localNow.Minute != 0) return Reaction.None;
        if (localNow.Hour == _lastHourlyHour) return Reaction.None;
        _lastHourlyHour = localNow.Hour;
        return Reaction.HourlyChime;
    }

    /// <summary>Past 2am, once per night.</summary>
    public Reaction RegisterLateNight(long now, DateTime localNow)
    {
        if (localNow.Hour is < 2 or >= 5) return Reaction.None;
        var day = localNow.DayOfYear;
        if (day == _lastLateNightDay) return Reaction.None;
        _lastLateNightDay = day;
        return Reaction.LateNight;
    }

    /// <summary>Anything that counts as contact, so "come back" can be detected.</summary>
    private Reaction RegisterInteraction(long now)
    {
        var silentFor = _lastInteractionMs == 0 ? 0 : now - _lastInteractionMs;
        _lastInteractionMs = now;
        return silentFor >= ComebackSilenceMs ? Reaction.Comeback : Reaction.None;
    }

    public void ResetCombo() => _comboCount = 0;

    /// <summary>Which screen corner a physical window rect is parked in (or "none").</summary>
    public static string CornerOf(int left, int top, int right, int bottom, int workLeft, int workTop, int workRight, int workBottom, int band)
    {
        var nearLeft = left <= workLeft + band;
        var nearRight = right >= workRight - band;
        var nearTop = top <= workTop + band;
        var nearBottom = bottom >= workBottom - band;
        if (nearTop && nearLeft) return "top-left";
        if (nearTop && nearRight) return "top-right";
        if (nearBottom && nearLeft) return "bottom-left";
        if (nearBottom && nearRight) return "bottom-right";
        return "none";
    }
}
