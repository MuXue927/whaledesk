using System.Windows;
using System.Windows.Threading;

namespace WhaleDesk;

/// <summary>
/// Closes a popup window when the user clicks somewhere else.
///
/// Why not "close on Deactivated": that is what made the right-click menu vanish the moment the
/// user moved the mouse towards it (the pointer crossing another window deactivates us, and a
/// NOACTIVATE pet window can also steal activation back). Click-outside is the behaviour a menu
/// is expected to have anyway.
/// </summary>
internal static class PopupDismiss
{
    public static void Install(Window window, Func<IReadOnlyList<Window>>? extraWindows = null, Action? onDismiss = null)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160),
        };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!Win32.LeftButtonDown()) return;
                if (!Win32.GetCursorPos(out var cursor)) return;
                if (Inside(window, cursor)) return;
                if (extraWindows is not null && extraWindows().Any(w => Inside(w, cursor))) return;
                onDismiss?.Invoke();
                window.Close();
            }
            catch
            {
                // never let the watchdog take the app down
            }
        };
        window.Closed += (_, _) => timer.Stop();
        window.Loaded += (_, _) => timer.Start();
    }

    private static bool Inside(Window window, Win32.POINT cursor)
    {
        if (!window.IsVisible) return false;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        if (!Win32.GetWindowRect(handle, out var rect)) return false;
        return cursor.X >= rect.Left && cursor.X <= rect.Right && cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;
    }
}
