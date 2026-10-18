using Microsoft.Win32;

namespace WhaleDesk.Recovery;

/// <summary>
/// Autostart through the per-user Run key. Chosen deliberately: it needs NO administrator
/// rights (so no UAC prompt on every login) and it is trivially reversible from the menu.
/// </summary>
internal static class AutoStart
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "WhaleDesk";

    /// <summary>The exact command line written to the Run key.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" --live --seconds 0";

    public static bool TryRead(out string? command, out string? error)
    {
        command = null;
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            command = key?.GetValue(ValueName) as string;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsEnabled(string exePath)
    {
        if (!TryRead(out var command, out _)) return false;
        return !string.IsNullOrWhiteSpace(command) && command.Contains(exePath, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryEnable(string exePath, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) { error = "无法打开 Run 注册表项"; return false; }
            key.SetValue(ValueName, CommandFor(exePath), RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;   // e.g. policy-locked machines
            return false;
        }
    }

    public static bool TryDisable(out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
