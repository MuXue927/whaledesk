using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WhaleDesk;

/// <summary>
/// Tray icon so the pet can be shown/hidden/reset/closed without hunting for the
/// process. WinForms NotifyIcon is used deliberately: it is part of the bundled
/// WindowsDesktop runtime, so no extra package is needed.
///
/// Two lessons from the user's bug report are baked in:
///  * a LEFT single click toggles visibility (before, only double-click worked, so
///    the icon looked dead in a classic Windows 10 style tray);
///  * the icon is loaded at the system's small-icon size instead of letting the
///    multi-size .ico pick, which renders badly in the notification area.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Log _log;
    private readonly ToolStripMenuItem _toggle;
    private readonly ToolStripMenuItem _pause;
    private string _hotkey = "Ctrl+Alt+W";

    /// <summary>The combination that actually registered on this machine, for the hint text.</summary>
    public void SetHotkeyName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && name != "(none)") _hotkey = name;
        _icon.Text = $"小鲸娘 · WhaleDesk（左键显示/隐藏，{_hotkey}）";
    }

    public TrayIcon(
        string iconPath,
        string tooltip,
        Log log,
        Action onToggle,
        Action onReset,
        Action onPause,
        Action onExit,
        Action<int> onPickMonitor,
        Action onOpenCleanup,
        IReadOnlyList<MonitorEntry> monitors)
    {
        _log = log;
        _toggle = new ToolStripMenuItem("隐藏小鲸娘", null, (_, _) => onToggle());
        _pause = new ToolStripMenuItem("暂停动画", null, (_, _) => onPause());

        var monitorsMenu = new ToolStripMenuItem("显示在哪个屏幕");
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var index = i;
            monitorsMenu.DropDownItems.Add(new ToolStripMenuItem($"{m.Label}  ({m.Detail})", null, (_, _) => onPickMonitor(index)));
        }
        if (monitors.Count <= 1) monitorsMenu.Enabled = false;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggle);
        menu.Items.Add(_pause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(monitorsMenu);
        menu.Items.Add(new ToolStripMenuItem("回到右下角", null, (_, _) => onReset()));
        menu.Items.Add(new ToolStripMenuItem("C 盘清理…", null, (_, _) => onOpenCleanup()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => onExit()));

        Icon? icon = null;
        try
        {
            if (File.Exists(iconPath))
            {
                // Explicit small size: a 256px-first .ico can render as a blurry blob
                // in the notification area otherwise.
                icon = new Icon(iconPath, SystemInformation.SmallIconSize);
            }
            else log.Warn($"tray icon missing: {iconPath}");
        }
        catch (Exception ex) { log.Warn("tray icon could not be loaded: " + ex.Message); }

        _icon = new NotifyIcon
        {
            Icon = icon ?? SystemIcons.Application,
            Text = tooltip,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) onToggle(); };
        _icon.DoubleClick += (_, _) => onToggle();
        _log.Info($"tray icon created (icon={(icon is null ? "fallback" : "tray.ico, small size")}, monitors={monitors.Count})");
    }

    /// <summary>One-off hint shown the first time she is hidden, so the way back is discoverable.</summary>
    public void ShowHiddenHint()
    {
        try
        {
            _icon.BalloonTipTitle = "小鲸娘躲起来了";
            _icon.BalloonTipText = $"左键单击托盘图标，或按 {_hotkey} 就能叫我出来；也可以再运行一次 WhaleDesk。";
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
        }
        catch (Exception ex) { _log.Warn("balloon tip failed: " + ex.Message); }
    }

    /// <summary>Periodic cleanup reminder: how much could be reclaimed, and how much of it is safe.</summary>
    public void ShowCleanupHint(long totalBytes, long safeBytes)
    {
        try
        {
            _icon.BalloonTipTitle = "小鲸娘：C 盘可以清理啦";
            _icon.BalloonTipText = $"扫描发现可回收 {Monitoring.Fmt.Bytes((ulong)Math.Max(0, totalBytes))}"
                                   + $"（其中放心项 {Monitoring.Fmt.Bytes((ulong)Math.Max(0, safeBytes))}）。"
                                   + "右键托盘图标 → 「C 盘清理…」看看每项的作用再决定。";
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(8000);
        }
        catch (Exception ex) { _log.Warn("cleanup balloon failed: " + ex.Message); }
    }

    public void SetVisibleState(bool visible) => _toggle.Text = visible ? "隐藏小鲸娘" : "显示小鲸娘";

    public void SetPausedState(bool paused) => _pause.Checked = paused;

    /// <summary>
    /// Re-add the icon after Explorer restarts (the shell drops every tray icon and
    /// broadcasts TaskbarCreated). Toggling Visible is what actually re-registers it.
    /// </summary>
    public void Recreate(Log log)
    {
        try
        {
            _icon.Visible = false;
            _icon.Visible = true;
            log.Info("tray icon recreated");
        }
        catch (Exception ex) { log.Warn("tray recreate failed: " + ex.Message); }
    }

    /// <summary>Tooltip used to surface the pomodoro countdown / low-memory state.</summary>
    /// <summary>System balloon for "long task finished" / memory warnings: visible even when
    /// the user is in another app or the pet is hidden.</summary>
    public void ShowTaskHint(string title, string text, int milliseconds = 9000)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = title.Contains("失败") ? ToolTipIcon.Warning : ToolTipIcon.Info;
            _icon.ShowBalloonTip(milliseconds);
        }
        catch { /* the tray may be gone during an Explorer restart */ }
    }

    public void SetStatus(string? status)
    {
        try
        {
            _icon.Text = string.IsNullOrEmpty(status)
                ? $"小鲸娘 · WhaleDesk（左键显示/隐藏，{_hotkey}）"
                : $"小鲸娘 · {status}（左键显示/隐藏）";
            if (_icon.Text.Length > 63) _icon.Text = _icon.Text[..63];   // NotifyIcon limit
        }
        catch { /* tooltip is cosmetic */ }
    }

    public void Dispose()
    {
        try { _icon.Visible = false; _icon.Dispose(); }
        catch (Exception ex) { _log.Warn("tray dispose failed: " + ex.Message); }
    }
}
