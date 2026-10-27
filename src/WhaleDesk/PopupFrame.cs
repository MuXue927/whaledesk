using System.Windows;
using System.Windows.Threading;

namespace WhaleDesk;

/// <summary>
/// Shows a popup so that it is never painted away from its final position.
///
/// Evidence (window-rect trace of the 0.24.5 build, 5 ms polling, --probe-launcher): the
/// quick-launch window first became visible at 154,154 with the size it would keep, and only
/// moved to 1196,552 about 370 ms later. The right-click menu shows the same pattern (visible at
/// 179,179, moved to 374,442 after ~360 ms), and the second-level flyout does the same. Cause:
/// the first Show() of a layered (AllowsTransparency) WPF window does not return until the window
/// has already been on screen for a few hundred milliseconds, so the old "Show() then place"
/// order always painted one wrong-position frame first - that frame is the "panel flashes"
/// report.
///
/// Fix: the window is created and placed while still hidden, stays fully transparent through
/// its first layout pass, is corrected to the exact corner once the layout reports a real
/// height, and only then becomes opaque. Any frame spent at a wrong position is invisible by
/// construction.
///
/// Placement is done in PHYSICAL pixels with SetWindowPos, against the target monitor's work
/// rectangle. WPF's Window.Left/Top are DIPs whose origin follows the primary monitor, so a
/// popup on a secondary monitor placed with them lands at the primary monitor's edge and gets cut
/// off by the screen boundary (reported 2026-10-28 for the clipboard panel).
/// </summary>
internal static class PopupFrame
{
    /// <summary>
    /// Show <paramref name="window"/> at the bottom-right of <paramref name="monitor"/>'s work area.
    /// </summary>
    public static void ShowBottomRight(Window window, MonitorEntry monitor, double marginDip)
    {
        window.Opacity = 0;
        try
        {
            // A handle is enough for a physical placement, so the first visible frame already
            // lands in the corner; the passes below refine it once the real height is known.
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        }
        catch
        {
            // the correction passes below still run once the window is up
        }
        window.Show();
        CorrectThenReveal(window, () => PlaceBottomRight(window, monitor, marginDip));
    }

    /// <summary>
    /// Show <paramref name="window"/> centred horizontally on <paramref name="monitor"/>, with its
    /// top at <paramref name="topFraction"/> of the monitor height (the search palette).
    /// </summary>
    public static void ShowCenteredTop(Window window, MonitorEntry monitor, double topFraction)
    {
        window.Opacity = 0;
        try
        {
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        }
        catch
        {
            // the correction passes below still run once the window is up
        }
        window.Show();
        CorrectThenReveal(window, () => PlaceCenteredTop(window, monitor, topFraction));
    }

    /// <summary>
    /// Show <paramref name="window"/> and let the caller place it (physical-pixel windows such as
    /// the menu, which need their own handle before they can compute the anchor).
    /// </summary>
    public static void ShowPlaced(Window window, Action place)
    {
        window.Opacity = 0;
        try
        {
            // A handle is enough for a physical placement, so the window is already in its corner
            // before it is shown at all; the passes below only correct it.
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            place();
        }
        catch
        {
            // placement is retried below once the window is up
        }
        window.Show();
        CorrectThenReveal(window, place);
    }

    /// <summary>Physical bottom-right placement of a window that is already shown.</summary>
    public static void PlaceBottomRight(Window window, MonitorEntry monitor, double marginDip)
    {
        if (!TryGetPhysicalSize(window, out var size)) return;
        var (x, y) = Placement.BottomRight(monitor, size.Width, size.Height, marginDip, marginDip);
        var (clampedX, clampedY) = Placement.Clamp(monitor, x, y, size.Width, size.Height);
        MovePhysical(window, clampedX, clampedY);
    }

    /// <summary>Physical centred-top placement (the search palette).</summary>
    public static void PlaceCenteredTop(Window window, MonitorEntry monitor, double topFraction)
    {
        if (!TryGetPhysicalSize(window, out var size)) return;
        var (x, y) = Placement.CenteredTop(monitor, size.Width, size.Height, topFraction);
        var (clampedX, clampedY) = Placement.Clamp(monitor, x, y, size.Width, size.Height);
        MovePhysical(window, clampedX, clampedY);
    }

    private static bool TryGetPhysicalSize(Window window, out (int Width, int Height) size)
    {
        size = default;
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !Win32.GetWindowRect(handle, out var rect)) return false;
            size = (rect.Right - rect.Left, rect.Bottom - rect.Top);
            return size.Width > 0 && size.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void MovePhysical(Window window, int x, int y)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            Win32.SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        catch
        {
            // a placement error must never leave the popup invisible
        }
    }

    private static void CorrectThenReveal(Window window, Action place)
    {
        // Two passes on purpose: the first one runs right after the window's first layout, where
        // the size Win32 reports is still the one reserved before SizeToContent shrank the window;
        // the second one runs after that pass and sees the real numbers. The window is transparent
        // for both.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            TryPlace(place);
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                TryPlace(place);
                window.Opacity = 1;
            }));
        }));
    }

    private static void TryPlace(Action place)
    {
        try
        {
            place();
        }
        catch
        {
            // a placement error must never leave the popup invisible
        }
    }
}
