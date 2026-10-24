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
///
/// The watchdog judges the PRESS, not the pointer:
///  * the decision is taken on the rising edge of the left button (up -> down), the one tick
///    at which the cursor is still at the point the user actually pressed;
///  * a press that started inside the popup keeps it open however far the pointer is dragged
///    afterwards (scrolling a long list, a shaky hand dragging a row off the edge);
///  * a press whose edge is only observed after a long stall of the dispatcher is ignored:
///    the sampled point is no longer the press point, so it must not decide anything;
///  * it runs at DispatcherPriority.Input and 15ms instead of Background/160ms, so a busy UI
///    thread (opening the menu enumerates every window first) can no longer starve it.
///    Each tick is one GetAsyncKeyState read while the button is idle and two more only on the
///    edge - no allocation, no window walking - which is orders of magnitude cheaper than the
///    one rendered frame the pet produces every 16ms anyway.
/// </summary>
internal static class PopupDismiss
{
    /// <summary>~one frame: the press point is sampled at most one frame after the press.</summary>
    private const int PollIntervalMs = 15;

    /// <summary>
    /// If the dispatcher was blocked longer than this (~four poll intervals) between two ticks,
    /// the sample taken on the press edge cannot be trusted to be the press point any more, so
    /// that press is ignored. TickCount64 is only ~15ms accurate, so normal jitter stays below it.
    /// </summary>
    private const long StallGuardMs = 60;

    /// <param name="allowDismiss">
    /// Optional veto: while it returns false a press outside is ignored. The quick-launch palette
    /// uses it to stay up while its own file/folder dialog is open - closing the owner under a
    /// native modal dialog is what crashed the process (0xC0000005) on 0.24.3.
    /// </param>
    public static void Install(Window window, Func<IReadOnlyList<Window>>? extraWindows = null,
        Action? onDismiss = null, Func<bool>? allowDismiss = null)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
        };

        // Sampled, not assumed. If the popup appears while the button is already held (opened
        // by the same press, or by a hotkey with the button down) that press has no observed
        // start point, so it must not be mistaken for "pressed outside".
        var wasDown = false;
        var lastTick = 0L;

        timer.Tick += (_, _) =>
        {
            try
            {
                var now = Environment.TickCount64;
                var gap = lastTick == 0 ? 0 : now - lastTick;
                lastTick = now;

                var down = Win32.LeftButtonDown();
                if (down == wasDown) return;    // steady state: nothing changed, do nothing
                wasDown = down;
                if (!down) return;              // release: the next press re-samples the origin

                // Rising edge: this is the first tick that sees the button down, i.e. at most
                // one poll interval after the physical press. That point is the press origin.
                // Unless the dispatcher was blocked in between - then the pointer may already be
                // somewhere else and acting on this sample would bring back the very bug this
                // replaces (press inside, drag out, menu closes). Fail open: one extra click is
                // better than a menu that vanishes under a dragging hand.
                if (gap > StallGuardMs) return;

                if (!Win32.GetCursorPos(out var pressedAt)) return;
                if (Inside(window, pressedAt)) return;
                if (extraWindows is not null && extraWindows().Any(w => Inside(w, pressedAt))) return;
                if (allowDismiss is not null && !allowDismiss()) return;
                onDismiss?.Invoke();
                window.Close();
            }
            catch
            {
                // never let the watchdog take the app down
            }
        };
        window.Closed += (_, _) => timer.Stop();
        window.Loaded += (_, _) =>
        {
            wasDown = Win32.LeftButtonDown();
            lastTick = 0;     // the first tick has no previous sample to measure a stall against
            timer.Start();
        };
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
