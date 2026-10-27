using System.Windows;

namespace WhaleDesk;

internal sealed record MonitorEntry(
    string Device, bool Primary,
    int Left, int Top, int Right, int Bottom, double Scale,
    int BoundsLeft = 0, int BoundsTop = 0, int BoundsRight = 0, int BoundsBottom = 0)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public string Label => Primary ? "主显示器" : $"副显示器 {ShortDevice}";
    public string ShortDevice
    {
        get
        {
            var digits = new string(Device.Where(char.IsDigit).ToArray());
            return digits.Length == 0 ? Device : digits;
        }
    }
    public string Detail => $"{Device}  work {Width}x{Height} @{Scale * 100:0}%";
    public string Bounds => BoundsRight > BoundsLeft ? $"{BoundsLeft},{BoundsTop}-{BoundsRight},{BoundsBottom}" : "?";
}

/// <summary>
/// Monitor inventory, all in PHYSICAL pixels (Win32 coordinates).
///
/// The whole placement pipeline works in physical pixels on purpose: WPF's
/// Window.Left/Top are DIPs whose meaning depends on which monitor the window
/// currently sits on, so mixing them with SetWindowPos silently drifts by the DPI
/// ratio when two monitors use different scaling.
/// </summary>
internal static class Monitors
{
    private static List<MonitorEntry>? _cache;

    public static IReadOnlyList<MonitorEntry> All(bool refresh = false)
    {
        if (_cache is not null && !refresh) return _cache;
        var found = new List<MonitorEntry>();

        // Enumeration comes from WinForms' Screen (device name + physical bounds and
        // work area) and the scale from GetDpiForMonitor on the monitor's centre.
        // The first implementation called EnumDisplayMonitors with a hand-written
        // MONITORINFOEX and read back wrong work areas for the second monitor
        // (1920x1040 @100% instead of 2400x1300 @125%), so the two APIs that had
        // already been verified individually are used instead.
        try
        {
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var b = s.Bounds;
                var wa = s.WorkingArea;
                var scale = 1.0;
                try
                {
                    // Microsoft's own guidance: GetDpiForMonitor is unreliable from a
                    // per-monitor-aware process (it reported 100% for a 125% monitor
                    // here). It is only the first guess; the authoritative scale comes
                    // from VisualTreeHelper.GetDpi() once the window is on the target
                    // monitor, and the settle pass corrects the placement with it.
                    var hmon = Win32.MonitorFromPoint(
                        new Win32.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 },
                        Win32.MONITOR_DEFAULTTONEAREST);
                    if (Win32.GetDpiForMonitor(hmon, Win32.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0)
                        scale = dx / 96.0;
                    else
                        scale = Win32.GetDpiForSystem() / 96.0;
                }
                catch { /* shcore missing: assume the system scale */ }
                found.Add(new MonitorEntry(
                    s.DeviceName ?? $"DISPLAY{found.Count + 1}",
                    s.Primary,
                    wa.Left, wa.Top, wa.Right, wa.Bottom,
                    scale,
                    b.Left, b.Top, b.Right, b.Bottom));
            }
        }
        catch (Exception) { /* fall through to the WPF fallback below */ }

        if (found.Count == 0)
        {
            // Fallback: the WPF work area (primary monitor only).
            var wa = SystemParameters.WorkArea;
            found.Add(new MonitorEntry("DISPLAY1", true, (int)wa.Left, (int)wa.Top, (int)wa.Right, (int)wa.Bottom, 1.0,
                (int)wa.Left, (int)wa.Top, (int)wa.Right, (int)wa.Bottom));
        }
        _cache = found;
        return found;
    }

    public static MonitorEntry Primary() => All().FirstOrDefault(m => m.Primary) ?? All()[0];

    /// <summary>Resolve a saved device name; falls back to the primary monitor when it is gone.</summary>
    public static MonitorEntry Resolve(string? device)
    {
        if (!string.IsNullOrEmpty(device))
        {
            var hit = All(refresh: true).FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return Primary();
    }

    /// <summary>The monitor containing the point, or the nearest one.</summary>
    public static MonitorEntry Nearest(int x, int y)
    {
        var all = All(refresh: true);
        foreach (var m in all)
            if (x >= m.Left && x < m.Right && y >= m.Top && y < m.Bottom) return m;
        return all.OrderBy(m => Math.Abs((m.Left + m.Right) / 2 - x) + Math.Abs((m.Top + m.Bottom) / 2 - y)).First();
    }

    public static MonitorEntry NearestToWindow(int x, int y, int w, int h) => Nearest(x + w / 2, y + h / 2);
}

/// <summary>Pure placement math in physical pixels, so --selftest can assert it.</summary>
internal static class Placement
{
    /// <summary>Scale a DIP margin to physical pixels, rounding half away from zero.</summary>
    private static int Px(double dip, double scale) => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);

    public static (int X, int Y) BottomRight(MonitorEntry m, int w, int h, double marginX, double marginY) =>
        (m.Right - w - Px(marginX, m.Scale),
         m.Bottom - h - Px(marginY, m.Scale));

    /// <summary>Horizontally centred on the monitor, down a fraction of its height.</summary>
    public static (int X, int Y) CenteredTop(MonitorEntry m, int w, int h, double topFraction) =>
        (m.Left + Math.Max(0, (m.Width - w) / 2),
         m.Top + (int)Math.Round(m.Height * topFraction));

    public static string DecideAnchor(MonitorEntry m, int windowCenterX) =>
        windowCenterX < (m.Left + m.Right) / 2 ? "left" : "right";

    public static (int X, int Y) Edge(MonitorEntry m, string anchor, int w, int h, int currentY, double marginX)
    {
        var margin = Px(marginX, m.Scale);
        var x = anchor == "left" ? m.Left + margin : m.Right - w - margin;
        var y = Math.Clamp(currentY, m.Top, Math.Max(m.Top, m.Bottom - h));
        return (x, y);
    }

    public static (int X, int Y) Clamp(MonitorEntry m, int x, int y, int w, int h) =>
        (Math.Clamp(x, m.Left, Math.Max(m.Left, m.Right - w)),
         Math.Clamp(y, m.Top, Math.Max(m.Top, m.Bottom - h)));

    /// <summary>Enough of the window must be on screen for the user to grab it.</summary>
    public static bool IsUsable(MonitorEntry m, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        var ox = Math.Min(x + w, m.Right) - Math.Max(x, m.Left);
        var oy = Math.Min(y + h, m.Bottom) - Math.Max(y, m.Top);
        return ox >= Math.Min(40, w) && oy >= Math.Min(40, h);
    }
}
