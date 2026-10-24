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
/// </summary>
internal static class PopupFrame
{
    /// <summary>
    /// Show <paramref name="window"/> at the bottom-right of the given work area (DIP), with the
    /// placement done before the window is visible.
    /// </summary>
    /// <param name="fallbackHeight">Used only when the content cannot be measured at all.</param>
    public static void ShowBottomRight(Window window, double workRightDip, double workBottomDip,
        double marginDip, double fallbackHeight)
    {
        PlaceAtBottomRight(window, workRightDip, workBottomDip, marginDip, fallbackHeight);
        window.Show();
        CorrectThenReveal(window, () => AlignBottomRight(window, workRightDip, workBottomDip, marginDip));
    }

    /// <summary>
    /// Flush the window against the corner using the rectangle Win32 reports, not WPF's
    /// ActualHeight: with SizeToContent, ActualHeight keeps reporting the height reserved before
    /// the window was shrunk (293 vs 253), which is what left the palette 40 px above the corner.
    /// </summary>
    private static void AlignBottomRight(Window window, double workRightDip, double workBottomDip, double marginDip)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !Win32.GetWindowRect(handle, out var rect)) return;

        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (scale <= 0) scale = 1.0;

        var dx = (workRightDip - marginDip) - rect.Right / scale;
        var dy = (workBottomDip - marginDip) - rect.Bottom / scale;
        if (Math.Abs(dx) > 0.5) window.Left += dx;
        if (Math.Abs(dy) > 0.5) window.Top += dy;
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

    private static void PlaceAtBottomRight(Window window, double workRightDip, double workBottomDip,
        double marginDip, double fallbackHeight)
    {
        var width = window.Width > 0 ? window.Width : 320;
        var height = GuessHeight(window, width, fallbackHeight);
        window.Left = Math.Max(0, workRightDip - width - marginDip);
        window.Top = Math.Max(0, workBottomDip - height - marginDip);
        window.Opacity = 0;
    }

    private static void CorrectThenReveal(Window window, Action place)
    {
        // Two passes on purpose: the first one runs right after the window's first layout, where
        // ActualHeight still reports the height WPF reserved before SizeToContent shrank the
        // window (293 where the palette really ends up 253 high); the second one runs after that
        // pass and sees the real numbers. The window is transparent for both.
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

    /// <summary>
    /// Height the content is expected to want. The handle is created first (still hidden) so the
    /// measurement runs in the window's real DPI context instead of a detached one.
    /// </summary>
    private static double GuessHeight(Window window, double width, double fallbackHeight)
    {
        try
        {
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.UpdateLayout();
            var height = window.ActualHeight;
            if (height > 1) return window.MaxHeight > 0 ? Math.Min(height, window.MaxHeight) : height;

            if (window.Content is FrameworkElement content)
            {
                content.Measure(new Size(width, double.PositiveInfinity));
                if (content.DesiredSize.Height > 1)
                {
                    var desired = content.DesiredSize.Height;
                    if (window.MaxHeight > 0) desired = Math.Min(desired, window.MaxHeight);
                    return Math.Ceiling(desired);
                }
            }
        }
        catch
        {
            // best effort; the reveal pass below still lands the window correctly
        }
        return fallbackHeight;
    }
}
