namespace WhaleDesk.Cleanup;

/// <summary>
/// The two-step confirmation state for the delete buttons, kept separate from the UI
/// so --selftest can drive it.
///
/// The first version had no way out: once a row was armed the only escape was to
/// rescan, and an expired arm left the row red forever. This machine has an explicit
/// Cancel, an expiry the UI can poll to revert its visuals, and arming one row
/// disarms any other.
/// </summary>
internal sealed class CleanupArmState
{
    public const int ConfirmWindowMs = 6000;

    public string? ArmedId { get; private set; }
    private long _armedAt;

    public bool IsArmed(string id) => ArmedId == id;

    public void Arm(string id, long now)
    {
        ArmedId = id;
        _armedAt = now;
    }

    public void Cancel() => ArmedId = null;

    /// <summary>Returns true when this click is the confirming second click.</summary>
    public bool Click(string id, long now)
    {
        if (ArmedId == id && now - _armedAt <= ConfirmWindowMs)
        {
            ArmedId = null;
            return true;
        }
        // Either nothing armed, a different row, or the window expired: arm this row.
        Arm(id, now);
        return false;
    }

    /// <summary>True when an arm expired; the caller must revert that row's visuals.</summary>
    public bool ExpireIfNeeded(long now)
    {
        if (ArmedId is null) return false;
        if (now - _armedAt <= ConfirmWindowMs) return false;
        ArmedId = null;
        return true;
    }

    /// <summary>Seconds left in the confirmation window (0 when nothing is armed).</summary>
    public int SecondsLeft(long now)
    {
        if (ArmedId is null) return 0;
        var left = ConfirmWindowMs - (now - _armedAt);
        return left <= 0 ? 0 : (int)Math.Ceiling(left / 1000.0);
    }
}
