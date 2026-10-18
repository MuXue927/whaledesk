using System.Runtime.InteropServices;
using System.Text;

namespace WhaleDesk;

internal static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint SPI_GETWORKAREA = 0x0030;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    // EntryPoint is mandatory here: without it the runtime looks for a function
    // literally named "GetWindowLongPtr64", which does not exist, and the
    // EntryPointNotFoundException kills the app inside Loaded (round 3 bug).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    /// <summary>Synthetic mouse input, used only by the automated interaction probe.</summary>
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    public static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder buffer, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder buffer, int max);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Class and title of a window, for diagnosing "who took the focus".</summary>
    public static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";
        var cls = new StringBuilder(256);
        var title = new StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        GetWindowTextW(hwnd, title, title.Capacity);
        return $"0x{hwnd.ToInt64():X} class='{cls}' title='{title}'";
    }

    public static long GetExStyle(IntPtr hwnd) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64() : GetWindowLong32(hwnd, GWL_EXSTYLE);

    public static void SetExStyle(IntPtr hwnd, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(value));
        else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)value);
    }

    public static bool HasFlag(long style, int flag) => (style & flag) == flag;

    public static RECT GetPhysicalWorkArea()
    {
        var r = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref r, 0);
        return r;
    }

    public static string Describe(long style)
    {
        var parts = new List<string>();
        if (HasFlag(style, WS_EX_TRANSPARENT)) parts.Add("TRANSPARENT");
        if (HasFlag(style, WS_EX_TOOLWINDOW)) parts.Add("TOOLWINDOW");
        if (HasFlag(style, WS_EX_LAYERED)) parts.Add("LAYERED");
        if (HasFlag(style, WS_EX_NOACTIVATE)) parts.Add("NOACTIVATE");
        return parts.Count == 0 ? "(none)" : string.Join("|", parts);
    }

    // ---------------------------------------------------------------- monitors

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int MDT_EFFECTIVE_DPI = 0;
    public const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", EntryPoint = "GetDpiForSystem")]
    public static extern uint GetDpiForSystem();

    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---------------------------------------------------------------- hotkey

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    // ---------------------------------------------------------------- other windows

    public const int SW_RESTORE = 9;

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "EnumWindows")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>First visible top-level window whose title contains the fragment (e.g. the DSH app).</summary>
    public static IntPtr FindWindowByTitleFragment(string fragment)
    {
        var found = IntPtr.Zero;
        EnumWindowsProc proc = (hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            if (sb.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }
            return true;
        };
        try { EnumWindows(proc, IntPtr.Zero); } catch { }
        GC.KeepAlive(proc);
        return found;
    }

    /// <summary>Restore a minimised window and give it the foreground (explicit user action only).</summary>
    public static void RestoreAndActivate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Sent to DSH's main window: its own handler hides it to the tray.</summary>
    public const int WM_CLOSE = 0x0010;

    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int max);

    [DllImport("user32.dll", EntryPoint = "BringWindowToTop")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "AttachThreadInput")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachFlag);

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "SwitchToThisWindow")]
    private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>
    /// Find the DSH main window even when it is HIDDEN (closed to the tray). The previous
    /// version filtered on IsWindowVisible, so a tray-hidden window was never found and the
    /// menu item silently gave up. Prefers Electron's real top-level window class over its
    /// helper windows, and a visible one over a hidden one.
    /// </summary>
    public static IntPtr FindMainWindow(string titleFragment, out bool wasHidden, out string detail)
    {
        var best = IntPtr.Zero;
        var bestScore = -1;
        var bestDetail = "not found";
        EnumWindowsProc proc = (hwnd, _) =>
        {
            var title = new StringBuilder(400);
            GetWindowTextW(hwnd, title, title.Capacity);
            var text = title.ToString();
            if (text.Length == 0 || !text.Contains(titleFragment, StringComparison.OrdinalIgnoreCase)) return true;

            var cls = new StringBuilder(200);
            GetClassName(hwnd, cls, cls.Capacity);
            var className = cls.ToString();
            var visible = IsWindowVisible(hwnd);
            // Chrome_WidgetWin_1 is Electron's real top-level window; _0 windows are helpers.
            var score = (className == "Chrome_WidgetWin_1" ? 2 : 0) + (visible ? 1 : 0);
            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
                bestDetail = $"hwnd=0x{hwnd.ToInt64():X} class={className} visible={visible} title='{text}'";
            }
            return true;
        };
        try { EnumWindows(proc, IntPtr.Zero); } catch { }
        GC.KeepAlive(proc);
        wasHidden = best != IntPtr.Zero && !IsWindowVisible(best);
        detail = bestDetail;
        return best;
    }

    /// <summary>
    /// Bring a window to the front, including one that was hidden to the tray.
    /// SetForegroundWindow usually fails for a background process (Windows only lets the
    /// foreground process hand focus over), so the thread-input attach trick runs first and
    /// SwitchToThisWindow is the last resort. Returns which steps actually ran.
    /// </summary>
    public static string BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "no window";
        var steps = new List<string>();
        if (IsIconic(hwnd)) { ShowWindow(hwnd, SW_RESTORE); steps.Add("restore"); }
        if (!IsWindowVisible(hwnd)) { ShowWindow(hwnd, SW_SHOW); steps.Add("show"); }

        var foreground = GetForegroundWindow();
        uint foregroundThread = 0;
        if (foreground != IntPtr.Zero) foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            steps.Add(attached ? "attach+setforeground" : "setforeground");
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }

        if (GetForegroundWindow() != hwnd)
        {
            SwitchToThisWindow(hwnd, true);
            steps.Add("switchto");
        }
        // Windows refuses to let a BACKGROUND process take focus from whatever the user is
        // typing in (by design). The classic nudge that still works is minimise+restore:
        // the shell then hands the window the foreground.
        if (GetForegroundWindow() != hwnd)
        {
            ShowWindow(hwnd, SW_MINIMIZE);
            Thread.Sleep(60);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            steps.Add("minimize+restore-nudge");
        }
        steps.Add(GetForegroundWindow() == hwnd ? "focused" : "visible-but-not-foreground");
        return string.Join(",", steps);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Seconds since the last keyboard/mouse input (0 if unavailable).</summary>
    public static int IdleSeconds()
    {
        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;
            var idleMs = Environment.TickCount - (int)info.dwTime;
            return idleMs < 0 ? 0 : idleMs / 1000;
        }
        catch
        {
            return 0;
        }
    }

    private const int CF_UNICODETEXT = 13;

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(int format);

    /// <summary>
    /// Cheap check whether the clipboard holds text. Unlike Clipboard.ContainsText this does not
    /// open the clipboard, so a busy owner cannot freeze our UI thread.
    /// </summary>
    private const int CF_BITMAP = 2;
    private const int CF_DIB = 8;

    /// <summary>True when the clipboard holds a bitmap (used by the image history).</summary>
    public static bool ClipboardHasImage()
    {
        try { return IsClipboardFormatAvailable(CF_BITMAP) || IsClipboardFormatAvailable(CF_DIB); }
        catch { return false; }
    }

    public static bool ClipboardHasText()
    {
        try { return IsClipboardFormatAvailable(CF_UNICODETEXT); }
        catch { return false; }
    }
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LBUTTON = 0x01;

    /// <summary>True while the physical left mouse button is held down.</summary>
    public static bool LeftButtonDown()
    {
        try { return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0; }
        catch { return false; }
    }}
