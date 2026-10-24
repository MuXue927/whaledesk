using System.IO;
using System.Text.Json;

namespace WhaleDesk;

/// <summary>
/// Everything the pet remembers between runs. Positions are PHYSICAL pixels of a
/// named monitor, so restoring works across monitors with different DPI scaling.
/// </summary>
internal sealed class UiState
{
    public string? Monitor { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public string? Anchor { get; set; }
    public bool Paused { get; set; }
    public bool FollowCursor { get; set; }
    /// <summary>When the disk was last scanned for reclaimable space (UTC, ISO-8601).</summary>
    public string? LastCleanupScanUtc { get; set; }
    /// <summary>Remind me every N days; 0 disables the reminder.</summary>
    public int CleanupReminderDays { get; set; } = 14;
    /// <summary>User-resized cleanup card width (DIP).</summary>
    public double? CleanupCardWidth { get; set; }
    /// <summary>User-resized cleanup list height (DIP).</summary>
    public double? CleanupListHeight { get; set; }

    // ---- P5 ----
    public bool LowMemoryMode { get; set; }
    /// <summary>Announce long DSH tasks when they finish (default on).</summary>
    public bool LongTaskAlerts { get; set; } = true;
    /// <summary>Announce DSH task-board cards that just finished; also shows the counts in the menu.</summary>
    public bool TaskBoardAlerts { get; set; } = true;
    /// <summary>Warn once when the battery gets low while unplugged.</summary>
    public bool BatteryAlerts { get; set; } = true;
    /// <summary>Announce OS-level long tasks (any app, not just DSH).</summary>
    public bool OsTaskAlerts { get; set; } = true;
    /// <summary>Gentle "this app has been in front for an hour" nudge (off by default).</summary>
    public bool AppTooLongAlerts { get; set; }
    /// <summary>Announce files that finished downloading.</summary>
    public bool DownloadAlerts { get; set; } = true;
    /// <summary>Accept files/text dropped onto the pet.</summary>
    public bool DropZone { get; set; } = true;
    /// <summary>Nudge when the user has worked too long without a break.</summary>
    public bool SedentaryAlerts { get; set; } = true;
    /// <summary>Record copied text locally (clipboard history).</summary>
    public bool ClipboardHistory { get; set; } = true;
    /// <summary>First-run wizard already shown.</summary>
    public bool Onboarded { get; set; }
    /// <summary>The "no DSH" guide was already shown once.</summary>
    public bool DshGuideShown { get; set; }
    /// <summary>Easter egg: drift towards the cursor while the user is away.</summary>
    public bool CursorFollow { get; set; }
    /// <summary>Silence cute chatter while a pomodoro is running.</summary>
    public bool FocusQuiet { get; set; } = true;
    /// <summary>Last DSH loopback base URL that answered (the port can change between launches).</summary>
    public string? DshBaseUrl { get; set; }
    /// <summary>Follow-cursor speed in physical px/s (80 slow, 160 medium, 320 fast).</summary>
    public int FollowSpeed { get; set; } = 160;
    /// <summary>Check GitHub Releases for a newer build (passive: bubble + menu item only).</summary>
    public bool AutoUpdateCheck { get; set; } = true;
    /// <summary>Pomodoro survives a restart: the phase and its absolute end time.</summary>
    public string? PomodoroPhase { get; set; }
    public long PomodoroEndsAtMs { get; set; }
    public int PomodoroCompleted { get; set; }
    public int PomodoroFocusMinutes { get; set; } = Interaction.Pomodoro.DefaultFocusMinutes;
    public int PomodoroBreakMinutes { get; set; } = Interaction.Pomodoro.DefaultBreakMinutes;
    /// <summary>Focus preset in minutes; 0 means "pick it automatically".</summary>
    public int PomodoroPresetMinutes { get; set; }
    /// <summary>Set once the first run is complete, so we can greet a returning user.</summary>
    public int RunCount { get; set; }
    /// <summary>Learned location of the DSH executable, so we can start it when it is closed.</summary>
    public string? DshExePath { get; set; }

    /// <summary>Last automatic cleanup (UTC, ISO-8601).</summary>
    public string? LastAutoCleanupUtc { get; set; }
    /// <summary>Run the Safe-tier-only cleanup automatically once a week (default on).</summary>
    public bool WeeklyCleanup { get; set; } = true;
    /// <summary>Mention DSH plugins that are behind their published npm version (default on).</summary>
    public bool PluginUpdateAlerts { get; set; } = true;

    /// <summary>True when the weekly automatic cleanup is due (or has never run).</summary>
    public bool IsAutoCleanupDue(DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(LastAutoCleanupUtc)) return true;
        if (!DateTime.TryParse(LastAutoCleanupUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)) return true;
        return (utcNow - last).TotalDays >= 7;
    }

    /// <summary>True when the periodic reminder is due (or has never run).</summary>
    public bool IsCleanupScanDue(DateTime utcNow)
    {
        if (CleanupReminderDays <= 0) return false;
        if (string.IsNullOrWhiteSpace(LastCleanupScanUtc)) return true;
        if (!DateTime.TryParse(LastCleanupScanUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)) return true;
        return (utcNow - last).TotalDays >= CleanupReminderDays;
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UiState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new UiState();
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UiState>(text) ?? new UiState();
        }
        catch
        {
            // A corrupt state file must never stop the pet from starting.
            return new UiState();
        }
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Atomic: a crash mid-write must not leave a truncated state file.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Persisting position is best effort.
        }
    }
}
