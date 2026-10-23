using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WhaleDesk;

/// <summary>
/// Tray icon so the pet can be shown/hidden/reset/closed without hunting for the
/// process. WinForms NotifyIcon is used deliberately: it is part of the bundled
/// WindowsDesktop runtime, so no extra package is needed.
///
/// Three lessons from the user's bug report are baked in:
///  * a LEFT single click toggles visibility (before, only double-click worked, so
///    the icon looked dead in a classic Windows 10 style tray);
///  * a DOUBLE click must still toggle only ONCE: WinForms reports the first press of a
///    double click as MouseClick and the second as DoubleClick, so the old
///    "MouseClick -> toggle, DoubleClick -> toggle" pair switched her off again right
///    after switching her on (the pet/panel "flashed and vanished");
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

    /// <summary>
    /// Environment.TickCount64 of the last accepted left click; 0 = none yet. A DoubleClick
    /// is always preceded by a MouseClick for the same gesture, so this timestamp is what
    /// lets the second half of a double click be recognised and dropped.
    /// </summary>
    private long _lastLeftClickTicks;

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
        // Double click no longer toggles a second time. NotifyIcon reports a double click
        // in two events (MouseClick for the first press, then DoubleClick), and depending
        // on timing the second MouseClick instead - subscribing to both is what made one
        // gesture toggle twice. One handler, one rule:
        //   * only MouseClick (left) toggles;
        //   * a left click that arrives within SystemInformation.DoubleClickTime of the
        //     previous accepted one is part of the same gesture and is ignored.
        // Behaviour change (user visible): double-clicking the icon toggles exactly once,
        // and two very fast deliberate clicks collapse into one toggle. DoubleClickTime is
        // the window Windows itself uses to decide "this is a double click", so no normal
        // single click is ever swallowed while both possible event orderings are covered.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var now = Environment.TickCount64;
            var debounceMs = Math.Max(200, SystemInformation.DoubleClickTime);
            if (_lastLeftClickTicks != 0 && now - _lastLeftClickTicks < debounceMs)
            {
                _log.Info($"tray left click ignored: {now - _lastLeftClickTicks}ms after the previous one (< {debounceMs}ms double-click window)");
                return;
            }
            _lastLeftClickTicks = now;
            onToggle();
        };
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
