using System.IO;
using System.Runtime.InteropServices;

namespace WhaleDesk.Recovery;

/// <summary>
/// Self-recovery helpers.
///
/// A desktop pet lives a long time and the environment around it changes: Explorer can
/// restart (the tray icon vanishes), a full-screen game steals topmost, the DSH loopback
/// API can go away and come back, or the machine can lose power mid-run. Each of those is
/// detected and repaired instead of leaving her half-broken.
/// </summary>
internal static class SelfHeal
{
    /// <summary>Written at startup, removed on a clean exit: its presence means the last run died.</summary>
    public const string MarkerFileName = "running.marker";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>The message Explorer broadcasts when the taskbar is (re)created.</summary>
    public static int TaskbarCreatedMessage => unchecked((int)RegisterWindowMessage("TaskbarCreated"));

    public static bool WasUncleanExit(string markerPath) => File.Exists(markerPath);

    public static void MarkRunning(string markerPath, Log log)
    {
        try
        {
            var dir = Path.GetDirectoryName(markerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(markerPath, $"{DateTime.Now:o}\npid={Environment.ProcessId}");
        }
        catch (Exception ex) { log.Warn("could not write the run marker: " + ex.Message); }
    }

    public static void MarkCleanExit(string markerPath, Log log)
    {
        try { if (File.Exists(markerPath)) File.Delete(markerPath); }
        catch (Exception ex) { log.Warn("could not clear the run marker: " + ex.Message); }
    }

    /// <summary>Is our window still topmost? Windows can drop it after exclusive fullscreen.</summary>
    public static bool IsTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var ex = GetWindowLongPtr(hwnd, -20).ToInt64();      // GWL_EXSTYLE
        return (ex & 0x00000008) != 0;                       // WS_EX_TOPMOST
    }

    /// <summary>Whether the DSH loopback state API answered recently.</summary>
    public static bool DshReachable(int consecutiveFailures, int failureThreshold = 3) =>
        consecutiveFailures < failureThreshold;
}
