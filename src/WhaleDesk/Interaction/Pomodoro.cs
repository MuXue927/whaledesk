namespace WhaleDesk.Interaction;

internal enum PomodoroPhase { Idle, Focus, Break }

/// <summary>
/// The focus timer. Pure state machine with injected time, so --selftest can run a whole
/// 25-minute cycle in microseconds instead of waiting for real minutes to pass.
/// </summary>
internal sealed class Pomodoro
{
    public const int DefaultFocusMinutes = 25;
    public const int DefaultBreakMinutes = 5;
    public const int MinMinutes = 1;
    public const int MaxMinutes = 180;

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Idle;
    public int FocusMinutes { get; private set; } = DefaultFocusMinutes;
    public int BreakMinutes { get; private set; } = DefaultBreakMinutes;
    public int CompletedFocusSessions { get; private set; }
    public long EndsAtMs { get; private set; }

    public bool Running => Phase != PomodoroPhase.Idle;

    public void Configure(int focusMinutes, int breakMinutes)
    {
        FocusMinutes = Math.Clamp(focusMinutes, MinMinutes, MaxMinutes);
        BreakMinutes = Math.Clamp(breakMinutes, MinMinutes, MaxMinutes);
    }

    public void StartFocus(long now)
    {
        Phase = PomodoroPhase.Focus;
        EndsAtMs = now + FocusMinutes * 60_000L;
    }

    public void StartBreak(long now)
    {
        Phase = PomodoroPhase.Break;
        EndsAtMs = now + BreakMinutes * 60_000L;
    }

    public void Stop()
    {
        Phase = PomodoroPhase.Idle;
        EndsAtMs = 0;
    }

    /// <summary>Resume a running timer after a restart (persisted endsAt).</summary>
    public void Restore(PomodoroPhase phase, long endsAtMs, int completedFocusSessions)
    {
        if (phase == PomodoroPhase.Idle) return;
        Phase = phase;
        EndsAtMs = endsAtMs;
        CompletedFocusSessions = completedFocusSessions;
    }

    public int RemainingSeconds(long now)
    {
        if (!Running) return 0;
        var left = EndsAtMs - now;
        return left <= 0 ? 0 : (int)Math.Ceiling(left / 1000.0);
    }

    /// <summary>
    /// Advance the clock. Returns the phase that was ENTERED when a boundary was crossed,
    /// or null when nothing changed — the caller shows one message per transition.
    /// </summary>
    public PomodoroPhase? Tick(long now)
    {
        if (!Running || now < EndsAtMs) return null;
        if (Phase == PomodoroPhase.Focus)
        {
            CompletedFocusSessions++;
            StartBreak(now);
            return PomodoroPhase.Break;
        }
        Stop();
        return PomodoroPhase.Idle;
    }

    public string Describe(long now)
    {
        if (!Running) return CompletedFocusSessions == 0 ? "未开始" : $"已完成 {CompletedFocusSessions} 个专注";
        var seconds = RemainingSeconds(now);
        var label = Phase == PomodoroPhase.Focus ? "专注" : "休息";
        return $"{label} {seconds / 60:00}:{seconds % 60:00}";
    }

    // ---------------------------------------------------------------- auto duration

    /// <summary>Set to a preset, or 0 for "pick one automatically".</summary>
    public int AutoPresetMinutes { get; private set; }

    public void SetPreset(int minutes)
    {
        AutoPresetMinutes = minutes <= 0 ? 0 : Math.Clamp(minutes, MinMinutes, MaxMinutes);
        if (AutoPresetMinutes > 0) Configure(AutoPresetMinutes, BreakMinutes);
    }

    public string PresetLabel => AutoPresetMinutes == 0 ? "自动" : $"{AutoPresetMinutes} 分钟";

    /// <summary>
    /// Pick a focus length from the situation instead of forcing 25 minutes: short bursts
    /// while the machine is loaded or memory is tight, longer ones during an active DSH
    /// session, and a gentle default late at night. Pure, so --selftest pins the rules.
    /// </summary>
    public static (int Minutes, string Reason) ChooseAutoMinutes(
        DateTime localNow, bool dshSessionActive, double cpuPercent, ulong availableBytes, ulong totalBytes)
    {
        var freePercent = totalBytes == 0 ? 100 : (int)(availableBytes * 100 / totalBytes);
        if (freePercent < 12) return (15, $"内存只剩 {freePercent}%，先来短的");
        if (localNow.Hour >= 22 || localNow.Hour < 5) return (20, "夜深了，短一点");
        if (dshSessionActive) return (45, "DSH 正在会话中，来一轮长的");
        if (cpuPercent >= 60) return (25, "机器有点忙，中等长度");
        return (30, "状态不错，标准长度");
    }

    /// <summary>Start a focus session, choosing the length automatically when set to auto.</summary>
    public (int Minutes, string Reason) StartFocusAuto(DateTime localNow, bool dshSessionActive, double cpuPercent, ulong availableBytes, ulong totalBytes, long now)
    {
        int minutes;
        string reason;
        if (AutoPresetMinutes > 0)
        {
            minutes = AutoPresetMinutes;
            reason = "按你选的时长";
        }
        else
        {
            (minutes, reason) = ChooseAutoMinutes(localNow, dshSessionActive, cpuPercent, availableBytes, totalBytes);
        }
        Configure(minutes, BreakMinutes);
        StartFocus(now);
        return (minutes, reason);
    }

    /// <summary>Bubble text for the current state (short enough for the speech bubble).</summary>
    public string BubbleText(long now)
    {
        if (!Running) return "";
        var seconds = RemainingSeconds(now);
        return Phase == PomodoroPhase.Focus
            ? $"🍅 专注中 {seconds / 60:00}:{seconds % 60:00}"
            : $"☕ 休息一下 {seconds / 60:00}:{seconds % 60:00}";
    }
}
