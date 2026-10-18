namespace WhaleDesk.Interaction;

/// <summary>What kind of thing the pet wants to say.</summary>
internal enum SpeechKind
{
    /// <summary>Small talk when idle (the "she says something cute" lines).</summary>
    IdleChatter,
    /// <summary>Easter-egg reactions (corner, late night, comeback, combo...).</summary>
    EasterEgg,
    /// <summary>A normal informational bubble in response to a user action.</summary>
    Bubble,
    /// <summary>Resource / task / download reminders: never silenced.</summary>
    Reminder,
}

/// <summary>
/// "Focus mode" (专注联动): while a pomodoro is running, keep her quiet for everything that is
/// merely cute, but still let real reminders through - a full disk must not be swallowed because
/// the user happens to be concentrating.
/// </summary>
internal static class FocusPolicy
{
    public static bool IsFocusActive(bool pomodoroRunning, bool quietEnabled) => pomodoroRunning && quietEnabled;

    public static bool ShouldSpeak(SpeechKind kind, bool focusActive) => !focusActive || kind switch
    {
        SpeechKind.Reminder => true,
        SpeechKind.Bubble => true,
        _ => false,
    };

    public static string Label(bool focusActive) => focusActive ? "专注中" : string.Empty;

    /// <summary>Status-strip text with the focus prefix when applicable.</summary>
    public static string Decorate(string status, bool focusActive) =>
        focusActive && !string.IsNullOrEmpty(status) ? $"专注中 · {status}" : status;
}
