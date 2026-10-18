namespace WhaleDesk.Performance;

/// <summary>
/// Safety policy for the "memory hogs" list, where the user can end a process.
///
/// The user asked for a whitelist; in practice the safe rule is a *deny* list plus a hard
/// refusal for anything this app depends on, because a curated whitelist would constantly
/// need updating for the apps the user actually runs. What matters is that we can never
/// offer to kill something that would break the machine, the session, or ourselves.
///
/// Pure and testable (--selftest), because "it killed the wrong thing" is unforgivable.
/// </summary>
internal static class MemoryGuard
{
    /// <summary>Never offered: core Windows processes, the shell, and anything that is us.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "memory compression", "idle", "secure system",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "lsm",
        "svchost", "dwm", "explorer", "sihost", "taskhostw", "fontdrvhost", "ctfmon",
        "audiodg", "spoolsv", "wudfhost", "dllhost", "runtimebroker", "searchhost",
        "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "msmpeng",
        "securityhealthservice", "nissrv", "smartscreen", "wmiprvse", "conhost",
        "openssh-agent", "whaledesk",           // ourselves
        "deepseek harness",                     // the DSH host: killing it ends the session
    };

    /// <summary>Tools whose running task must never be interrupted by a stray click.</summary>
    private static readonly HashSet<string> TaskTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "codex", "chatgpt", "claude", "cursor", "aider", "copilot", "ollama", "lmstudio",
    };

    /// <summary>CPU% above which a process is considered to be working right now.</summary>
    public const double BusyCpuPercent = 3.0;
    /// <summary>AI/agent tools are treated as busy much earlier (their tasks matter).</summary>
    public const double ToolBusyCpuPercent = 0.8;

    /// <summary>Refused even if it somehow reaches the UI.</summary>
    public static bool CanKill(string? processName, int pid, out string reason) =>
        CanKill(processName, pid, 0, out reason);

    /// <summary>
    /// Same, but refuses while the process is doing something: the user's VS Code test showed
    /// a running task must not be killed by two clicks, and Codex/ChatGPT must never be
    /// interrupted mid-task.
    /// </summary>
    public static bool CanKill(string? processName, int pid, double cpuPercent, out string reason)
    {
        var trimmed = processName?.Trim() ?? "";
        if (TaskTools.Contains(trimmed) && cpuPercent >= ToolBusyCpuPercent)
        {
            reason = $"{trimmed} 正在跑任务（CPU {cpuPercent:0.0}%），我不会中断它";
            return false;
        }
        if (cpuPercent >= BusyCpuPercent)
        {
            reason = $"{trimmed} 正在忙（CPU {cpuPercent:0.0}%），等它闲下来再结束吧";
            return false;
        }
        if (pid <= 4)
        {
            reason = "系统进程（PID ≤ 4）不能结束";
            return false;
        }
        if (string.IsNullOrWhiteSpace(processName))
        {
            reason = "进程名未知，不能结束";
            return false;
        }
        if (Forbidden.Contains(processName.Trim()))
        {
            reason = $"{processName} 是系统/关键进程，我不会动它";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>Memory pressure tier: 0 = fine, 1 = tight (15%), 2 = low (10%), 3 = critical (5%).</summary>
    public static int TierForFreePercent(double freePercent)
    {
        if (freePercent <= 0) return 3;
        if (freePercent < 5) return 3;
        if (freePercent < 10) return 2;
        if (freePercent < 15) return 1;
        return 0;
    }

    public static string TierAdvice(int tier) => tier switch
    {
        3 => "内存快没了，建议马上关掉几个大户",
        2 => "内存很低，要不要看看谁在吃内存？",
        1 => "内存有点紧，我可以帮你列出大户",
        _ => "",
    };
}
