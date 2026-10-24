using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WhaleDesk.Cleanup;
using WhaleDesk.Interaction;
using WhaleDesk.Monitoring;
using WhaleDesk.Performance;
using WhaleDesk.Recovery;
namespace WhaleDesk;
/// <summary>
/// The real desktop window: transparent, borderless, topmost, never activated,
/// docked to an edge of a monitor the user picks.///
/// Placement is done in PHYSICAL pixels with SetWindowPos. WPF's Left/Top are DIPs
/// whose meaning depends on the monitor the window currently sits on, so they are
/// only used for layout inside the window, never for positioning.
/// </summary>
internal sealed partial class PetWindow : Window
{    private const int HotkeyId = 0xB001;
    private readonly CliOptions _opts;
    private readonly PetSpec _spec;
    private readonly AtlasLoader _atlas;
    private readonly Log _log;
    private readonly PetVisual _visual;
    private readonly TrackPlayer _player;
    private PetStateClient _state = new();
    private readonly LineBank _lines = new();
    private readonly UiState _ui;
    private DshLocator _dsh;
    private readonly DispatcherTimer _geometryTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    // ---- P5: pomodoro / easter eggs / low memory / recovery ----
    private readonly Pomodoro _pomodoro = new();
    private readonly EasterEggs _eggs = new();
    private readonly LowMemoryPolicy _lowMemory = new();
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _longPressTimer = new() { Interval = TimeSpan.FromMilliseconds(EasterEggs.LongPressMs) };

    private bool _uncleanExit;
    private int _lostTopmostStreak;
    private int _taskbarCreatedMessage;
    private bool _trayWasLost;
    private int _lastPomodoroBubbleSecond = -1;

    private readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _hitTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private TrayIcon? _tray;
    private IntPtr _hwnd;
    private HwndSource? _source;
    private MonitorEntry _monitor;
    private readonly long _foregroundBefore;
    private bool _clickThrough;
    private bool _polling;
    private bool _hotkeyRegistered;
    private string _hotkeyName = "(none)";
    private string _stateSummary = "not-polled-yet";
    private bool _dragging;
    private bool _dragMoved;
    private int _lastDragStepX;
    private Win32.POINT _dragOriginCursor;
    private (int X, int Y) _dragOriginWindow;
    private MenuWindow? _menuWindow;
    private OnboardingWindow? _onboardingWindow;
    private DshGuideWindow? _dshGuideWindow;
    private PluginHelperWindow? _pluginWindow;
    private bool _menuOpen;

    private double _menuHeight;
    private bool _panelOpen;
    private double _panelHeight;
    private bool _cleanupOpen;
    private double _cleanupHeight;
    private bool _cleanupScanning;
    private bool _cleanupBatchRunning;                  // 闸门：扫描有守卫，清理原来没有
    private CancellationTokenSource? _cleanupBatchCts;  // 运行中可取消（只在目标之间检查）
    private readonly double _baseHeight;
    // window height in DIP with no card open
    private double _extraHeight;
            // extra DIP currently applied for the open cards

    private readonly MonitorService _monitorService;
    private Snapshot _lastSnapshot = new(0, 0, 0, 0, false, 0, 0, "C:", 0, 0, 0, 0, 0, Array.Empty<ProcessRow>(), Array.Empty<HogRow>(), "尚未采样");
    private long _localBubbleUntilMs;
    private long _lastIdleChatterMs;
    private bool _paused;
    public PetWindow(CliOptions opts, PetSpec spec, AtlasLoader atlas, Log log)
    {
        _opts = opts; _spec = spec; _atlas = atlas; _log = log;
        _player = new TrackPlayer(spec);
        _monitorService = new MonitorService(log);
        _monitorService.Updated += OnSnapshot;
        _ui = opts.ResetPosition ? new UiState() : UiState.Load(opts.StatePath);
        _dsh = new DshLocator(log, _ui.DshExePath, opts.ProbeDsh);
        // --dsh-base lets tests (and non-default ports) point the poller elsewhere.
        _dshBase = opts.DshBase ?? _ui.DshBaseUrl ?? PetStateClient.DefaultBase;
        _state = new PetStateClient(_dshBase);
        log.Info($"state poller base = {_state.BaseUrl}");
        if (opts.DshBase is null && !opts.NoDiscovery) _ = DiscoverDshEndpointAsync(initial: true);
        log.Info($"state poller base = {_state.BaseUrl}");
        // ---- P5 restore ----
        _lowMemory.Enabled = _ui.LowMemoryMode;
        _pomodoro.Configure(_ui.PomodoroFocusMinutes, _ui.PomodoroBreakMinutes);
        _pomodoro.SetPreset(_ui.PomodoroPresetMinutes);
        _longTaskAlerts = _ui.LongTaskAlerts;
        _osTaskAlerts = _ui.OsTaskAlerts;
        _downloadAlerts = _ui.DownloadAlerts;
        _watchFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        // Startup baseline: files that already exist are NOT new downloads (a month-old image
        // used to be announced on every launch).
        try
        {

            foreach (var existing in FolderWatch.Enumerate(_watchFolder)) _folderWatch.MarkKnown(existing.Path);
        }
        catch { }
        _dropEnabled = _ui.DropZone;
        _sedentaryAlerts = _ui.SedentaryAlerts;
        _cursorFollow = _ui.CursorFollow;
        _focusQuiet = _ui.FocusQuiet;
        _autoUpdateCheck = _ui.AutoUpdateCheck;
        if (_autoUpdateCheck) _ = CheckUpdateAsync(silent: true);
        _followSpeed = _ui.FollowSpeed <= 0 ? 160 : _ui.FollowSpeed;
        _followTimer.Tick += (_, _) => UpdateCursorFollow();
        if (_cursorFollow) _followTimer.Start();
        _clipHistoryEnabled = _ui.ClipboardHistory;
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        _appTooLongAlerts = _ui.AppTooLongAlerts;
        if (Enum.TryParse<PomodoroPhase>(_ui.PomodoroPhase, out var restoredPhase) && _ui.PomodoroEndsAtMs > 0)
            _pomodoro.Restore(restoredPhase, _ui.PomodoroEndsAtMs, _ui.PomodoroCompleted);
        _uncleanExit = SelfHeal.WasUncleanExit(MarkerPath);        SelfHeal.MarkRunning(MarkerPath, log);
        _monitor = Monitors.Resolve(_ui.Monitor);        Title = "WhaleDesk";        WindowStyle = WindowStyle.None;        AllowsTransparency = true;        Background = Brushes.Transparent;        Topmost = true;
        ShowInTaskbar = false;        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;        Focusable = false;        SnapsToDevicePixels = true;        UseLayoutRounding = true;
        _visual = new PetVisual(atlas, spec, opts.PetHeight) { HorizontalAlignment = HorizontalAlignment.Stretch };        Content = _visual;
        // Width accommodates the WIDEST card so no card's text is clipped; the pet stays
        // right-aligned, so widening extends leftward and her screen position is unchanged.
        Width = Math.Max(_visual.PetWidth, Math.Max(MonitorPanel.CardWidth, CleanupPanel.CardWidth)) + 6;
        _baseHeight = Math.Round(_visual.PetHeight + 132);        Height = _baseHeight;
        _visual.PetImage.MouseLeftButtonDown += OnPetMouseDown;
        _visual.PetImage.MouseMove += OnPetMouseMove;
        _visual.PetImage.MouseLeftButtonUp += OnPetMouseUp;
        _visual.PetImage.MouseRightButtonUp += OnPetRightClick;
        _visual.PetImage.LostMouseCapture += (_, _) => { _dragging = false; };
        _visual.Cleanup.ScanRequested += () => _ = ScanCleanupAsync();
        _visual.Cleanup.CleanRequested += target => _ = CleanTargetAsync(target);
        _visual.Cleanup.SafeBatchRequested += () => _ = RunSafeBatchAsync();
        _visual.Cleanup.ElevatedCleanRequested += target => _ = RunElevatedCleanAsync(target);
        _visual.Cleanup.Resized += OnCleanupResized;
        if (_ui.CleanupCardWidth is double savedWidth) _visual.Cleanup.SetCardWidth(savedWidth);
        if (_ui.CleanupListHeight is double savedHeight) _visual.Cleanup.SetListHeight(savedHeight);
        _visual.Cleanup.TempAgeChanged += days =>
        {
            _cleanupTempAgeDays = days;
            _log.Info($"cleanup temp age set to {(days <= 0 ? "all ages" : days + " days")} - rescanning");
            _ = ScanCleanupAsync();
        };        Loaded += OnLoaded;        DpiChanged += OnDpiChanged;
        Closed += (_, _) =>
        {
            Persist();
            PersistPomodoro();            SelfHeal.MarkCleanExit(MarkerPath, _log);   // a marker left behind means "unclean exit"
            if (_hotkeyRegistered && _hwnd != IntPtr.Zero) Win32.UnregisterHotKey(_hwnd, HotkeyId);
            _source?.RemoveHook(WndProcHook);
            _tray?.Dispose();
            _monitorService.Dispose();
            _state.Dispose();
            _log.Info("window closed");
        };
        _foregroundBefore = Win32.GetForegroundWindow().ToInt64();
    }
    public IReadOnlyList<string> Diagnostics { get; private set; } = Array.Empty<string>();
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var before = Win32.GetExStyle(_hwnd);
        Win32.SetExStyle(_hwnd, before | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        _log.Info($"exstyle applied at SourceInitialized (pre-show): {Win32.Describe(before)} -> {Win32.Describe(Win32.GetExStyle(_hwnd))}");
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProcHook);
        _taskbarCreatedMessage = SelfHeal.TaskbarCreatedMessage;        RegisterHotkey();        Activated += OnActivated;
    }
    /// <summary>Path of the "was running" marker used to spot an unclean exit.</summary>
    private string MarkerPath => Path.Combine(_opts.Root, "state", SelfHeal.MarkerFileName);
    /// <summary>
    /// Self-recovery loop, once per second: pomodoro countdown/transitions, easter-egg
    /// clock triggers, and re-asserting things Windows quietly takes away (topmost) or
    /// destroys (the tray icon when Explorer restarts).
    /// </summary>
    private void OnSecondTick()
    {
        RefreshDshPresence();
        UpdateStatusStrip();
        SampleForeground();
        CheckSedentary();

        if (_cursorFollow && !_followTimer.IsEnabled) _followTimer.Start();
        if (!_cursorFollow && _followTimer.IsEnabled) _followTimer.Stop();
        _focusFollowSummary = $"focus={FocusActive} follow={_cursorFollow}";
        PollClipboardHistory();
        if (_autoUpdateCheck && Environment.TickCount64 - _lastUpdateCheckMs > 24L * 3600 * 1000) _ = CheckUpdateAsync(silent: true);
        PollDownloadFolder();
        var now = Environment.TickCount64;
        var local = DateTime.Now;
        if (_pomodoro.Running)
        {
            var phase = _pomodoro.Tick(now);
            if (phase == PomodoroPhase.Break)
            {
                _log.Info($"pomodoro: focus finished ({_pomodoro.CompletedFocusSessions} completed), break started");
                ShowLocalBubble("🍅 专注结束！" + (_lines.Pick("pomodoro-break") ?? "去休息 5 分钟"), 8000, force: true);
                _player.Play("jumping", now, restart: true);
                _visual.SetFrame(_player.Track.Row, _player.FrameColumn);
            }
            else if (phase == PomodoroPhase.Idle)
            {
                _log.Info($"pomodoro: break finished ({_pomodoro.CompletedFocusSessions} sessions today)");
                ShowLocalBubble("☕ 休息结束，" + (_lines.Pick("pomodoro-done") ?? "要继续吗"), 8000, force: true);
            }
            else
            {
                // refresh the countdown every 5s without spamming the bubble
                var seconds = _pomodoro.RemainingSeconds(now);
                if (Math.Abs(seconds - _lastPomodoroBubbleSecond) >= 5)
                {
                    _lastPomodoroBubbleSecond = seconds;
                    ShowLocalBubble(_pomodoro.BubbleText(now), 5500, force: true);
                }
            }
            PersistPomodoro();
        }
        // ---- easter eggs on the clock ----
        var late = _eggs.RegisterLateNight(now, local);
        if (late == Reaction.LateNight) React(late);
        var hourly = _eggs.RegisterClock(now, local);
        if (hourly == Reaction.HourlyChime && _pomodoro.Phase == PomodoroPhase.Idle)            React(hourly);
        // ---- corner settle ----
        var (x, y, w, h) = PhysicalRect();
        if (w > 0)
        {
            var corner = EasterEggs.CornerOf(x, y, x + w, y + h, _monitor.Left, _monitor.Top, _monitor.Right, _monitor.Bottom, 40);
            var settle = _eggs.RegisterPlacement(now, corner);
            if (settle == Reaction.CornerSettle) React(settle);
        }
        // ---- self-recovery: topmost is stolen by exclusive fullscreen apps ----
        if (_hwnd != IntPtr.Zero && IsVisible)
        {
            if (SelfHeal.IsTopmost(_hwnd)) _lostTopmostStreak = 0;
            else if (++_lostTopmostStreak == 3)
            {
                _log.Warn("window lost its topmost style - re-asserting");
                Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
                _lostTopmostStreak = 0;
            }
        }
        // ---- self-recovery: the tray icon can be gone (Explorer restart) ----
        if (_trayWasLost && _tray is not null)
        {
            _trayWasLost = false;
            _tray.Recreate(_log);
            _log.Info("tray icon re-registered after the taskbar was recreated");
        }
        // ---- self-recovery: DSH offline -> slower polling, and say it once ----
        if (_state.ConsecutiveFailures >= 3)
        {
            // v0.24.4 backed off to 5s here, and the request timeout alone is 1.5s: after quitting
            // DSH the pet needed up to ~6s to notice it was back. A refused loopback connection
            // returns in a millisecond, and _polling already keeps in-flight requests from piling
            // up, so the offline cadence can stay tight.
            _stateTimer.Interval = TimeSpan.FromMilliseconds(1500);
        }
        else if (_stateTimer.Interval != TimeSpan.FromMilliseconds(800))
        {
            if (_wasOffline)
            {
                _wasOffline = false;
                _log.Info($"DSH state API is reachable again base={_state.BaseUrl}");
                ShowLocalBubble("连上 DSH 了，继续陪你", 4000, force: true);
            }
            _stateTimer.Interval = TimeSpan.FromMilliseconds(800);
        }
        _wasOffline = _state.ConsecutiveFailures >= 3;
    }
    private bool _wasOffline;
    private DshLocator.DshPresence _dshPresence = DshLocator.DshPresence.Unknown;
    private long _lastPresenceCheckMs;
    private long _dshSuppressUntilMs;
    private bool _autoStartEnabled;
    // ---- DSH endpoint discovery: the port is not always 19387 (0.2.1-rc.2 used a dynamic one) ----
    private string? _dshBase;
    private long _lastDiscoveryMs;
    // ---- optional extras: cursor follow (easter egg) + focus quiet mode ----
    private bool _cursorFollow;
    private bool _focusQuiet = true;
    private long _lastFollowPersistMs;
    private long _lastFollowLogMs;
    private int _followDecisionsLogged;
    private int _followSpeed = 160;
    private int _idleOverrideSeconds = -1;   // probe-only: pretend the user is away
    private long _lastFollowTickMs;
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private string _focusFollowSummary = "";
    // ---- auto update check ----
    private const string UpdateRepo = UpdateChecker.DefaultRepo;
    private bool _autoUpdateCheck = true;
    private long _lastUpdateCheckMs;
    private string? _updateAvailableTag;
    private bool _dshSessionActive;
    // ---- P1: long-task alerts, status strip, memory guard ----
    private readonly TaskWatch _taskWatch = new();
    private bool _longTaskAlerts = true;
    private string? _lastStatePhase;
    private long _lastStatusStripMs;
    private Border? _statusStrip;
    private TextBlock? _statusText;
    private bool _panelWired;
    private int _killArmPid;
    private long _killArmUntilMs;
    private int _memTierWarned;
    private int _diskTierWarned;
    private long _diskTierWarnAtMs;
    private long _bigDirsScannedAtMs;
    private bool _bigDirsScanning;
    private readonly FolderWatch _folderWatch = new();

    private Interaction.LauncherStore? _launchers;
    private LauncherWindow? _launcherWindow;
    private AnalysisWindow? _analysisWindow;
    private Interaction.KeepList? _keepList;
    private string KeepListPath => Path.Combine(Path.GetDirectoryName(_opts.StatePath) ?? ".", "keep-list.json");
    private Interaction.IgnoreList? _ignoreList;
    private readonly Interaction.SedentaryWatch _sedentary = new();
    private Interaction.ClipboardHistoryStore? _clipHistory;
    private ClipboardWindow? _clipWindow;
    private bool _sedentaryAlerts = true;
    private bool _clipHistoryEnabled = true;
    private long _lastClipPollMs;
    private string? _lastClipText;
    private long _lastClipImageHash;
    private long _lastClipImagePollMs;
    private string SedentaryPath => Path.Combine(Path.GetDirectoryName(_opts.StatePath) ?? ".", "sedentary.json");
    private string ClipHistoryPath => Path.Combine(Path.GetDirectoryName(_opts.StatePath) ?? ".", "clipboard-history.json");
    private string IgnoreListPath => Path.Combine(Path.GetDirectoryName(_opts.StatePath) ?? ".", "ignore-list.json");
    private bool _dropEnabled = true;
    private readonly List<string> _lastDroppedFiles = new();
    private string LauncherConfigPath => Path.Combine(Path.GetDirectoryName(_opts.StatePath) ?? ".", "quick-launch.json");
    private readonly HealthWatch _healthWatch = new();
    private bool _downloadAlerts = true;
    private string? _watchFolder;
    private long _lastFolderPollMs;

    // ---- OS-level task monitor + "app in front too long" ----
    private readonly Performance.TaskMonitor _taskMonitor = new();
    private readonly Interaction.ForegroundWatch _foregroundWatch = new();
    private bool _osTaskAlerts = true;
    private bool _appTooLongAlerts;
    private long _memTierWarnAtMs;
    /// <summary>P5: the focus timer, driven from the context menu.</summary>
    private void TogglePomodoro()
    {
        if (_pomodoro.Running)
        {
            _log.Info($"pomodoro stopped after {_pomodoro.CompletedFocusSessions} completed session(s)");
            _pomodoro.Stop();
            PersistPomodoro();
            ShowLocalBubble("番茄钟已停止", 3000, force: true);
            return;
        }
        // Automatic length: chosen from DSH activity, CPU, free memory and the hour.
        var snapshot = _lastSnapshot;
        var (minutes, reason) = _pomodoro.StartFocusAuto(            DateTime.Now, _dshSessionActive, snapshot.CpuPercent,            snapshot.MemAvailable, snapshot.MemTotal, Environment.TickCount64);
        PersistPomodoro();
        _log.Info($"pomodoro started: {minutes}min focus / {_pomodoro.BreakMinutes}min break (preset={_pomodoro.PresetLabel}, reason={reason})");
        ShowLocalBubble($"🍅 {minutes} 分钟专注（{reason}）", 6000, force: true);
    }
    /// <summary>Cycle the focus preset: 自动 → 15 → 25 → 45 → 60 → 自动.</summary>
    private void CyclePomodoroPreset()
    {
        var next = _pomodoro.AutoPresetMinutes switch { 0 => 15, 15 => 25, 25 => 45, 45 => 60, _ => 0 };
        _pomodoro.SetPreset(next);
        _ui.PomodoroPresetMinutes = next;
        PersistPomodoro();
        _log.Info($"pomodoro preset -> {_pomodoro.PresetLabel}");
        ShowLocalBubble(next == 0            ? "番茄钟时长：自动（按 DSH 状态 / CPU / 内存 / 时间挑选）"            : $"番茄钟时长：{next} 分钟", 4000, force: true);
    }
    /// <summary>P5: per-user autostart (HKCU Run, so no UAC prompt at all).</summary>
    private void ToggleAutoStart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            ShowLocalBubble("拿不到程序自身路径，无法设置自启", 4000, force: true);
            return;
        }
        if (AutoStart.IsEnabled(exe))
        {
            var ok = AutoStart.TryDisable(out var error);
            _log.Info($"autostart disable: ok={ok} error={error ?? "none"}");
            ShowLocalBubble(ok ? "已关闭开机自启" : $"关闭失败：{error}", 4000, force: true);
        }
        else
        {
            var ok = AutoStart.TryEnable(exe, out var error);
            _log.Info($"autostart enable: ok={ok} error={error ?? "none"} command=\"{AutoStart.CommandFor(exe)}\"");
            ShowLocalBubble(ok ? "下次开机我会自己出现" : $"设置失败：{error}", 4000, force: true);
        }
        _autoStartEnabled = AutoStart.IsEnabled(exe);
    }
    /// <summary>
    /// Show one reaction: its own line (resolved here with a SHORT cooldown, so a repeated
    /// easter egg still shows a real line instead of a short fallback), plus a matching
    /// animation where it makes sense.
    /// </summary>
    private void React(Reaction reaction)
    {
        var key = ReactionKey(reaction);
        var text = _lines.Pick(key, 600) ?? reaction switch
        {            Reaction.Combo1 => "三连击！你今天心情不错嘛",            Reaction.Combo2 => "好啦好啦，我知道你很喜欢我！",            Reaction.Dizzy => "@_@ 转晕了…让我缓一下",            Reaction.LongPress => "你…就这样一直摸着我不说话？",            Reaction.Overfed => "嗝——吃不下了，真的吃不下了",            Reaction.CornerSettle => "这个角落视野真好，我就在这儿扎根了",            Reaction.LateNight => "夜深了，记得喝水，别硬撑",            Reaction.HourlyChime => "整点报时：又过了一小时，起来动动？",            Reaction.Comeback => "你回来啦！我等了好久",
            _ => null,
        };
        if (text is null) return;
        _log.Info($"reaction {reaction} (line {key}): {text}");
        ShowLocalBubble(text, reaction == Reaction.Dizzy ? 4000 : 3500, force: true);
        switch (reaction)
        {
            case Reaction.Combo2:
            case Reaction.HourlyChime:
                _player.Play("jumping", Environment.TickCount64, restart: true);
                _visual.SetFrame(_player.Track.Row, _player.FrameColumn);                break;
            case Reaction.Dizzy:
                _player.Play("failed", Environment.TickCount64, restart: true);
                _visual.SetFrame(_player.Track.Row, _player.FrameColumn);                break;
            case Reaction.LateNight:
                _player.Play("waiting", Environment.TickCount64, restart: true);
                _visual.SetFrame(_player.Track.Row, _player.FrameColumn);                break;
        }
    }
    /// <summary>
    /// Keep the pet's idea of DSH in step with reality: running with a window, running but
    /// hidden in the tray, or gone. Runs on the 1s tick (throttled to every 2s) and announces
    /// only real transitions, so "DSH 已经打开了" can no longer survive DSH being closed.
    /// </summary>
    private void RefreshDshPresence(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastPresenceCheckMs < 500) return;
        _lastPresenceCheckMs = now;

        var presence = DshLocator.Probe(_log, out var detail);
        if (presence == DshLocator.DshPresence.Unknown) return;
        var previous = _dshPresence;
        if (presence == previous) return;

        _dshPresence = presence;
        if (presence == DshLocator.DshPresence.NotRunning && !_ui.DshGuideShown)
        {
            _ui.DshGuideShown = true;
            Persist();
            ShowLocalBubble("没找到 DSH：右键我 →「没找到 DSH 怎么办…」", 8000, force: true);
        }
        if (presence != DshLocator.DshPresence.Starting)
            _log.Info($"DSH presence {previous} -> {presence} ({detail})");
        else
            _log.Diag("DSH presence", $"{previous} -> Starting ({detail})");
        _tray?.SetStatus(presence switch
        {
            DshLocator.DshPresence.Visible => "DSH：打开着",
            DshLocator.DshPresence.Hidden => "DSH：在托盘里",
            _ => "DSH：没有运行",
        });

        var known = previous != DshLocator.DshPresence.Unknown;
        if (!known || now < _dshSuppressUntilMs) return;   // no chatter during a launch we started
        if (presence == DshLocator.DshPresence.Starting) return;   // booting/exiting: do not guess

        var line = (previous, presence) switch
        {
            (_, DshLocator.DshPresence.NotRunning) => "DSH 关掉了，我先在这儿等你",
            (DshLocator.DshPresence.NotRunning, DshLocator.DshPresence.Visible) => "DSH 打开了！",
            (DshLocator.DshPresence.NotRunning, DshLocator.DshPresence.Hidden) => "DSH 起来了，但在托盘里",
            (_, DshLocator.DshPresence.Hidden) => "DSH 收进托盘了",
            (DshLocator.DshPresence.Hidden, DshLocator.DshPresence.Visible) => "DSH 又回来了",
            _ => null,
        };
        if (line is not null) ShowLocalBubble(line, 3500, force: true);
    }

    /// <summary>Close DSH's window the way its own title-bar X does: DSH hides itself to the tray.</summary>
    private void HideDshWindow()
    {
        if (DshLocator.HideWindow(_log, out var detail))
        {
            _dshPresence = DshLocator.DshPresence.Hidden;
            _dshSuppressUntilMs = Environment.TickCount64 + 2_500;
            ShowLocalBubble("DSH 收进托盘了，点它的托盘图标就能叫回来", 4000, force: true);
        }
        else
        {
            ShowLocalBubble("没能收起 DSH：" + detail, 5000, force: true);
        }
    }

    private void OnLongPressElapsed()
    {
        _longPressTimer.Stop();
        // A press ALWAYS starts a drag, so requiring "!dragging" here made this easter egg
        // impossible to trigger. What matters is: still held, and nothing moved.
        if (InteractionInput.IsLongPress(_dragging, _dragMoved, AnyCardOpen, IsCursorOverPet()))
        {
            React(_eggs.RegisterLongPress(Environment.TickCount64));
        }
    }
    /// <summary>Apply the low-memory policy everywhere it matters.</summary>
    private void ApplyLowMemoryMode(bool enabled, string reason)
    {
        _lowMemory.Enabled = enabled;
        _ui.LowMemoryMode = enabled;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(_lowMemory.FrameIntervalMs);
        _monitorService.SetInterval(_lowMemory.MonitorIntervalMs(_panelOpen));        RenderOptions.SetBitmapScalingMode(_visual.PetImage,
            _lowMemory.LowQualityRendering ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality);
        _log.Info($"low-memory mode {(enabled ? "ON" : "OFF")} ({reason}): frame={_lowMemory.FrameIntervalMs}ms monitor={_lowMemory.MonitorIntervalMs(_panelOpen)}ms quality={(_lowMemory.LowQualityRendering ? "low" : "high")}");
        _ui.Save(_opts.StatePath);
    }
    private void PersistPomodoro()
    {
        _ui.PomodoroPhase = _pomodoro.Phase.ToString();
        _ui.PomodoroEndsAtMs = _pomodoro.EndsAtMs;
        _ui.PomodoroCompleted = _pomodoro.CompletedFocusSessions;
        _ui.PomodoroFocusMinutes = _pomodoro.FocusMinutes;
        _ui.PomodoroBreakMinutes = _pomodoro.BreakMinutes;
        _ui.PomodoroPresetMinutes = _pomodoro.AutoPresetMinutes;
        _ui.Save(_opts.StatePath);
    }
    /// <summary>
    /// Ctrl+Alt+W is the documented shortcut, but it may already be taken (it was on
    /// the user's machine). Try a short list and report which one actually registered,
    /// so the hint text can name the working combination.
    /// </summary>
    private void RegisterHotkey()
    {        (uint Mods, uint Vk, string Name)[] candidates =
        {            (Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x57 /*W*/, "Ctrl+Alt+W"),            (Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, 0x57 /*W*/, "Ctrl+Shift+W"),            (Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x50 /*P*/, "Ctrl+Alt+P"),            (Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT, 0x51 /*Q*/, "Ctrl+Alt+Q"),
        };
        foreach (var (mods, vk, name) in candidates)
        {
            if (!Win32.RegisterHotKey(_hwnd, HotkeyId, mods, vk)) continue;
            _hotkeyRegistered = true;
            _hotkeyName = name;
            _log.Info($"global hotkey registered: {name} (show/hide)");
            return;
        }
        _hotkeyName = "(none)";
        _log.Warn("no global hotkey could be registered; tray left-click and re-launching remain");
    }
    public string HotkeyName => _hotkeyName;
    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Self-recovery: Explorer restarting destroys every tray icon. Windows tells us
        // through this broadcast; we re-register instead of silently losing the icon.
        if (_taskbarCreatedMessage != 0 && msg == _taskbarCreatedMessage)
        {
            _trayWasLost = true;
            _log.Warn("taskbar recreated (Explorer restarted) - will re-register the tray icon");
        }
        if (msg == Win32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _log.Info("hotkey Ctrl+Alt+W pressed");
            ToggleVisible();            handled = true;
        }
        return IntPtr.Zero;
    }
    private void OnActivated(object? sender, EventArgs e)
    {
        if (_foregroundBefore == 0 || _foregroundBefore == _hwnd.ToInt64()) return;
        var target = new IntPtr(_foregroundBefore);
        if (!Win32.IsWindow(target)) return;
        _log.Warn($"window got activated (unexpected) - handing focus back to {Win32.DescribeWindow(target)}");
        Win32.SetForegroundWindow(target);
    }
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try { OnLoadedCore(); }
        catch (Exception ex) { _log.Error("window setup failed: " + ex); }
    }
    private void OnLoadedCore()
    {
        _log.Info($"exstyle at Loaded: {Win32.Describe(Win32.GetExStyle(_hwnd))}");
        var dpi = VisualTreeHelper.GetDpi(this);
        _log.Diag("dpiScale", $"{dpi.DpiScaleX}x{dpi.DpiScaleY}");
        foreach (var m in Monitors.All(refresh: true))
            _log.Diag("monitor", $"{m.Device} primary={m.Primary} bounds={m.Bounds} work=({m.Left},{m.Top})-({m.Right},{m.Bottom}) {m.Width}x{m.Height} scaleEst={m.Scale:0.##}");
        _log.Diag("cleanupCard", $"width={_visual.Cleanup.PreferredWidth:0} listHeight={_visual.Cleanup.ListHeight:0} scrollbar={_visual.Cleanup.StyleDiagnostics ?? "unknown"}");        RestorePosition();
        var fgNow = Win32.GetForegroundWindow().ToInt64();
        _log.Diag("foregroundBeforeShow", Win32.DescribeWindow(new IntPtr(_foregroundBefore)));
        _log.Diag("foregroundAfterShow", Win32.DescribeWindow(new IntPtr(fgNow)));
        _log.Diag("foregroundIsOwnWindow", fgNow == _hwnd.ToInt64() ? "YES (focus stolen)" : "no (expected)");
        _visual.SetFrame(_player.Track.Row, _player.FrameColumn);
        _visual.SetBubble("启动中…");
        if (!_opts.NoTray)
        {
            _tray = new TrayIcon(_opts.TrayIconPath, "小鲸娘 · WhaleDesk（左键显示/隐藏）", _log,
                ToggleVisible, ResetPosition, TogglePause, FinishAndClose, PickMonitor, OpenCleanupCard, Monitors.All(refresh: true));
            _tray.SetHotkeyName(_hotkeyName);
            _tray.SetPausedState(_paused);
        }
        _frameTimer.Tick += (_, _) => OnFrameTick();
        _geometryTimer.Tick += (_, _) => OnGeometryTick();
        _tickTimer.Tick += (_, _) => OnSecondTick();
        _longPressTimer.Tick += (_, _) => OnLongPressElapsed();
        _hitTimer.Tick += (_, _) => OnHitTick();
        _stateTimer.Tick += async (_, _) => await PollStateAsync();
        if (!_paused) _frameTimer.Start();
        _hitTimer.Start();
        _stateTimer.Start();
        _monitorService.Start(_lowMemory.MonitorIntervalMs(panelOpen: false));
        _tickTimer.Start();
        ApplyLowMemoryMode(_lowMemory.Enabled, reason: "startup");
        _ = PollStateAsync();
        if (_uncleanExit)
        {
            // Self-recovery: the previous run died (power loss / force kill). Say so, and
            // make sure the UI is in a sane state rather than restoring a half-open card.
            _log.Warn("previous run did not exit cleanly - running recovery");
            _visual.Cleanup.Visibility = Visibility.Collapsed;
            _visual.Panel.Visibility = Visibility.Collapsed;
            ShowLocalBubble("上次我摔了一跤，已经自己爬起来啦", 6000, force: true);
        }
        else if (_ui.RunCount > 0 && _ui.LastCleanupScanUtc is not null)
        {
            ShowLocalBubble(_lines.Pick("resume") ?? "我回来啦", 4000, force: true);
        }
        _ui.RunCount++;
        _ui.Save(_opts.StatePath);
        if (_ui.IsCleanupScanDue(DateTime.UtcNow)) _ = PeriodicCleanupReminderAsync();
        if (_opts.ProbeHitTest) _ = RunHitTestProbeAsync();
        if (_opts.ProbeInteract) _ = RunInteractProbeAsync();
        if (_opts.ProbeMenu) _ = RunMenuProbeAsync();
        if (_opts.ProbeMenuMonitor) _ = RunMenuMonitorProbeAsync();
        if (_opts.ProbeMoveLeft) _ = RunMoveLeftProbeAsync();
        if (_opts.ProbeHide) _ = RunHideProbeAsync();
        if (_opts.ProbeMonitors) _ = RunMonitorProbeAsync();
        if (_opts.ProbeMonitorPanel) _ = RunMonitorPanelProbeAsync();
        if (_opts.ProbeCleanupPanel) _ = RunCleanupProbeAsync();
        if (_opts.ProbeOverflow) _ = RunOverflowProbeAsync();
        if (_opts.ProbePlacement) _ = RunPlacementProbeAsync();
        if (_opts.ProbeResize) _ = RunResizeProbeAsync();
        if (_opts.ProbeEdges) _ = RunEdgeProbeAsync();
        if (_opts.ProbeSpawn) _ = RunSpawnProbeAsync();
        if (_opts.ProbeDrop) _ = RunDropProbeAsync();
        if (_opts.ProbeLauncher) _ = RunLauncherProbeAsync();
        if (_opts.ProbeAnalyze) _ = RunAnalyzeProbeAsync();
        if (_opts.ProbeDshApi) _ = RunDshApiProbeAsync();
        if (_opts.ProbePlugins) _ = RunPluginProbeAsync();
        if (_opts.ProbeHotplug) _ = RunHotplugProbeAsync();
        if (_opts.ProbeExtras) _ = RunExtrasProbeAsync();
        if (_opts.ProbeFollow) _ = RunFollowProbeAsync();
        if (_opts.ProbeUpdate) _ = RunUpdateProbeAsync();
        if (_opts.ProbeCleanupTools) _ = RunCleanupToolsProbeAsync();
        if (_opts.ProbeClipboardImage) _ = RunClipboardImageProbeAsync();
        if (_opts.ProbeClipboard) _ = RunClipboardProbeAsync();
        if (_opts.ProbeDshGuide) _ = RunDshGuideProbeAsync();
        if (_opts.ProbeFocus) _ = RunFocusProbeAsync();
        if (_opts.ShowOnboarding || (!_ui.Onboarded && !_opts.NoOnboard)) _ = ShowOnboardingAfterStartupAsync();
        if (_opts.ProbeDsh) _ = RunDshProbeAsync();
    }
    // ---------------------------------------------------------------- placement (physical pixels)
    private (int X, int Y, int W, int H) PhysicalRect()
    {
        if (_hwnd == IntPtr.Zero || !Win32.GetWindowRect(_hwnd, out var r)) return (0, 0, 0, 0);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
    private void MovePhysical(int x, int y) =>
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    private void RestorePosition()
    {
        var m = Monitors.Resolve(_ui.Monitor);
        var w = (int)Math.Round(Width * m.Scale);
        var h = (int)Math.Round(Height * m.Scale);
        if (_ui.X is int x && _ui.Y is int y && Placement.IsUsable(m, x, y, w, h))
        {
            _monitor = m;
            MovePhysical(x, y);
            _log.Info($"position restored from state: monitor={m.Device} {x},{y} anchor={_ui.Anchor ?? "none"}");
            LogRect();
            return;
        }
        if (_ui.X is not null)
            _log.Warn($"saved position {_ui.X},{_ui.Y} is not usable on {m.Device} - re-docking");        DockToAnchor(m, _ui.Anchor ?? "right");
    }
    /// <summary>
    /// Dock to a monitor. <paramref name="kind"/> = "dock" puts her in the bottom
    /// corner of the chosen edge; "snap" keeps her current vertical position (used
    /// after a drag). Conflating the two made the reset land at a random height.
    /// </summary>
    private void DockToAnchor(MonitorEntry m, string anchor, string kind = "dock")
    {
        var (cx, cy, w, h) = PhysicalRect();
        if (w <= 0 || h <= 0)
        {            w = (int)Math.Round(Width * m.Scale);            h = (int)Math.Round(Height * m.Scale);
        }        int x, y;
        if (kind == "snap" && (anchor == "left" || anchor == "right"))
        {            (x, y) = Placement.Edge(m, anchor, w, h, cy, _opts.MarginX);
        }
        else
        {            (x, y) = Placement.BottomRight(m, w, h, _opts.MarginX, _opts.MarginY);
        }
        _monitor = m;
        MovePhysical(x, y);
        _ui.Monitor = m.Device;
        _ui.Anchor = anchor;
        Persist();
        _log.Info($"docked ({kind}): monitor={m.Device} anchor={anchor} -> {x},{y} (phys {w}x{h}, estimate scale {m.Scale:0.##})");
        LogRect();        ScheduleSettle(anchor, kind, m.Device);
    }
    /// <summary>
    /// Second placement pass, 250ms after the move: by then Windows has delivered the
    /// per-monitor DPI change, so the window's real physical size and scale are known
    /// and the corner/edge can be hit exactly instead of with an estimated scale.
    /// </summary>
    private void ScheduleSettle(string anchor, string kind, string device)
    {        Dispatcher.BeginInvoke(new Action(() =>
        {
            var (x, y, w, h) = PhysicalRect();
            if (w <= 0) return;
            var docked = Monitors.Resolve(device);
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;   // authoritative per-monitor scale
            var marginX = (int)Math.Round(_opts.MarginX * scale, MidpointRounding.AwayFromZero);
            var marginY = (int)Math.Round(_opts.MarginY * scale, MidpointRounding.AwayFromZero);            int nx, ny;
            if (kind == "snap")
            {                nx = anchor == "left" ? docked.Left + marginX : docked.Right - w - marginX;                ny = Math.Clamp(y, docked.Top, Math.Max(docked.Top, docked.Bottom - h));
            }
            else
            {                nx = docked.Right - w - marginX;                ny = docked.Bottom - h - marginY;
            }
            MovePhysical(nx, ny);
            Persist();
            _log.Info($"settled: monitor={device} anchor={anchor} -> {nx},{ny} (phys {w}x{h}, real scale {scale:0.##}, dip {Width:0}x{Height:0})");
            LogRect();
        }), DispatcherPriority.Background);
    }
    /// <summary>Open the cleanup card from the tray (the card is normally reached via the right-click menu).</summary>
    private void OpenCleanupCard()
    {
        if (!IsVisible) RequestShow("tray cleanup");
        if (!_cleanupOpen) ToggleCleanup();
    }
    private void ResetPosition() => DockToAnchor(Monitors.Resolve(_ui.Monitor), "right");
    private void PickMonitor(int index)
    {
        var all = Monitors.All(refresh: true);
        if (index < 0 || index >= all.Count) return;        DockToAnchor(all[index], "right");
        ShowLocalBubble($"搬到{all[index].Label}啦", 2500, force: true);
    }
    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        // Crossing to a monitor with different scaling resizes the window; re-apply the
        // edge anchor so the pet stays glued to the corner instead of drifting.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var (x, y, w, h) = PhysicalRect();
            if (w <= 0) return;
            var m = Monitors.NearestToWindow(x, y, w, h);
            var margin = (int)Math.Round(_opts.MarginX * m.Scale);
            var anchor = Placement.DecideAnchor(m, x + w / 2);
            var nx = anchor == "left" ? m.Left + margin : m.Right - w - margin;
            var ny = Math.Clamp(y, m.Top, Math.Max(m.Top, m.Bottom - h));
            _monitor = m;
            MovePhysical(nx, ny);
            _log.Info($"DPI changed -> re-anchored on {m.Device} at {nx},{ny} (scale {m.Scale:0.##})");
            Persist();
        }), DispatcherPriority.Background);
    }
    private void LogRect()
    {
        var (x, y, w, h) = PhysicalRect();
        if (w > 0) _log.Diag("windowRect", $"L{x} T{y} R{x + w} B{y + h} ({w}x{h})");
    }
    private void Persist()
    {
        var (x, y, w, h) = PhysicalRect();
        if (w <= 0) return;
        // Always store the COLLAPSED geometry: while a card is open the window is
        // taller and moved up, and saving that made her float too high next start.
        var extraPhys = (int)Math.Round(_extraHeight * _monitor.Scale);
        _ui.X = x;
        _ui.Y = y + extraPhys;
        _ui.Monitor = _monitor.Device;
        _ui.Paused = _paused;
        _ui.CleanupCardWidth = _visual.Cleanup.PreferredWidth;
        _ui.CleanupListHeight = _visual.Cleanup.ListHeight;
        _ui.Save(_opts.StatePath);
    }
    // ---------------------------------------------------------------- timers
    private void OnFrameTick()
    {
        var now = Environment.TickCount64;
        if (_player.Tick(now))
            _visual.SetFrame(_player.Track.Row, _player.FrameColumn);        MaybeIdleChatter(now);
    }
    private void MaybeIdleChatter(long now)
    {
        if (_localBubbleUntilMs > now) return;
        if (now - _lastIdleChatterMs < 4 * 60 * 1000) return;
        if (_player.TrackName != "idle") return;
        if (IsCursorOverPet()) return;
        if (!Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.IdleChatter, FocusActive)) return;
        var line = _lines.Pick("idle", 60_000);
        if (line is null) return;
        _lastIdleChatterMs = now;
        ShowLocalBubble(line, holdMs: 6000);
    }
    private void OnHitTick()
    {
        // The menu is its own window now: do NOT close it when the cursor leaves the pet rect -
        // that is exactly what made it vanish while the user was moving the mouse towards it.
        // MenuWindow dismisses itself on Esc / item chosen / left-click outside.

        if (AnyCardOpen || _dragging)
        {
            SetClickThrough(false);
            if (_cleanupOpen) _visual.Cleanup.Tick();   // expire a stale 确认 and revert its row
            // Persist a card resize once the user stops dragging (not on every mouse move).
            if (_sizeDirty && Environment.TickCount64 - _lastSizeChangeMs > 700)
            {
                _sizeDirty = false;
                Persist();
                _log.Info($"cleanup card resized: width={_visual.Cleanup.PreferredWidth:0} list={_visual.Cleanup.ListHeight:0} rect={Rect()}");
            }
            return;
        }
        SetClickThrough(!IsCursorOverPet());
    }
    private async Task PollStateAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var state = await _state.FetchAsync().ConfigureAwait(true);
            if (state is null)
            {
                _stateSummary = $"offline (failures={_state.ConsecutiveFailures} last={_state.LastError})";
                _taskWatch.Observe(null, Environment.TickCount64);
                // DSH takes a NEW port on every launch, so a stale base URL fails for ever. The
                // discovery probe was written for exactly this (it self-throttles to one run per
                // 10s) but nothing called it after startup: quitting DSH left the pet pinned to a
                // dead port until the next relaunch. That was the "state never catches up" bug.
                if (_state.ConsecutiveFailures >= 2 && !_opts.NoDiscovery) _ = DiscoverDshEndpointAsync();
                if (_state.ConsecutiveFailures >= 3)
                {
                    // Nothing on screen may keep claiming a live DSH: drop the line the server
                    // left behind (a petting/pomodoro line is left alone until its hold expires).
                    _dshSessionActive = false;
                    if (Environment.TickCount64 >= _localBubbleUntilMs && !string.IsNullOrEmpty(_visual.BubbleText))
                    {
                        _visual.SetBubble(null);
                        _log.Info("bubble cleared: DSH is offline");
                    }
                }
                if (_healthWatch.Record(true, Environment.TickCount64))
                {
                    _log.Warn($"DSH link is flaky: {_healthWatch.RecentFailures(Environment.TickCount64)} failures in the window");
                    ShowLocalBubble("连 DSH 有点不稳定，我会继续重试", 6000, force: true);
                }
                UpdateStatusStrip();
                if (_state.ConsecutiveFailures == 3) ShowLocalBubble("DSH 离线，我自己待会儿", 4000, force: true);
                return;
            }
            _stateSummary = $"animation={state.Animation} phase={state.Phase} bubble={state.Bubble} sessionActive={state.SessionActive} points={state.AffinityPoints}";
            _dshSessionActive = state.SessionActive;
            _healthWatch.Record(false, Environment.TickCount64);
            _lastStatePhase = state.Phase;
            var finishedTask = _taskWatch.Observe(state, Environment.TickCount64);
            if (finishedTask is not null) OnLongTaskFinished(finishedTask);
            if (!_paused && _player.Play(state.Animation, Environment.TickCount64))
            {
                _visual.SetFrame(_player.Track.Row, _player.FrameColumn);
                _log.Info($"track -> {state.Animation} (phase={state.Phase})");
            }
            // Compare against what is actually on screen, not against the last state we received.
            // A local line (petting, pomodoro, idle chatter, presence hints) overwrites the bubble
            // without touching any "last state" variable, so the old comparison (state.Bubble vs
            // _lastBubble) kept showing the local line for ever whenever DSH's own text had not
            // changed since the previous poll. "W" is removed with this fix.
            if (Environment.TickCount64 >= _localBubbleUntilMs &&
                !string.Equals(state.Bubble ?? "", _visual.BubbleText ?? "", StringComparison.Ordinal))
            {
                // 留一行日志：本地台词被 DSH 接管或清空的时刻，出问题时能直接从日志看出"谁压住了气泡"。
                var onScreen = (_visual.BubbleText ?? "").Trim();
                _log.Info(string.IsNullOrEmpty(state.Bubble)
                    ? $"bubble cleared: DSH has nothing to say (was '{onScreen}')"
                    : $"bubble <- DSH: {state.Bubble} (was '{onScreen}')");
                _visual.SetBubble(state.Bubble);
            }
        }
        finally { _polling = false; }
    }
    // ---------------------------------------------------------------- hit test
    private bool IsCursorOverPet() =>
        Win32.GetCursorPos(out var cursor) && IsScreenPointOverPet(cursor.X, cursor.Y);
    private bool IsScreenPointOverPet(int screenX, int screenY)
    {
        if (_hwnd == IntPtr.Zero) return false;
        var (wx, wy, ww, wh) = PhysicalRect();
        if (ww <= 0) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var petOrigin = _visual.PetImage.TranslatePoint(new Point(0, 0), this);
        var x = (screenX - wx) / dpi.DpiScaleX - petOrigin.X;
        var y = (screenY - wy) / dpi.DpiScaleY - petOrigin.Y;
        var w = _visual.PetImage.ActualWidth;
        var h = _visual.PetImage.ActualHeight;
        if (w <= 0 || h <= 0) return false;
        if (x < 0 || y < 0 || x >= w || y >= h) return false;
        return _atlas.IsOpaqueDilated(_player.Track.Row, _player.FrameColumn, x / w, y / h);
    }
    private void SetClickThrough(bool on)
    {
        if (on == _clickThrough) return;
        _clickThrough = on;
        var style = Win32.GetExStyle(_hwnd);        style = on ? style | Win32.WS_EX_TRANSPARENT : style & ~Win32.WS_EX_TRANSPARENT;
        Win32.SetExStyle(_hwnd, style);
        _log.Info($"click-through {(on ? "ON (clicks pass through)" : "OFF (pet is clickable)")} exstyle={Win32.Describe(Win32.GetExStyle(_hwnd))}");
    }
    // ---------------------------------------------------------------- mouse
    private void OnPetMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_menuOpen) { CloseMenu(); return; }
        if (!Win32.GetCursorPos(out var cursor)) return;
        // Register BEFORE the double-click branch: returning early there meant a rapid
        // burst only ever counted its first click, so 6-in-a-row could never fire.
        var (reaction, celebrate) = InteractionInput.OnPress(_eggs, e.ClickCount, Environment.TickCount64);
        if (reaction != Reaction.None) React(reaction);
        if (celebrate)
        {
            _ = CelebrateAsync(e.ClickCount);            e.Handled = true;
            return;
        }

        _longPressTimer.Stop();
        _longPressTimer.Start();        BeginDragAt(cursor.X, cursor.Y);
        _visual.PetImage.CaptureMouse();        e.Handled = true;
    }
    private static string ReactionKey(Reaction reaction) => reaction switch
    {        Reaction.Combo1 => "combo1",        Reaction.Combo2 => "combo2",        Reaction.Dizzy => "dizzy",        Reaction.Overfed => "overfed",        Reaction.CornerSettle => "corner",        Reaction.LateNight => "latenight",        Reaction.HourlyChime => "hourly",        Reaction.Comeback => "comeback",        Reaction.LongPress => "longpress",
        _ => "idle",
    };
    private void OnPetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (Win32.GetCursorPos(out var cursor)) UpdateDragAt(cursor.X, cursor.Y);
    }
    private void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        _longPressTimer.Stop();
        if (!_dragging) return;        e.Handled = true;
        _ = CompleteDragAsync();
    }
    // ---- drag behaviour, split from input acquisition so probes can drive it with
    // ---- injected coordinates instead of depending on the physical mouse ----
    private void BeginDragAt(int screenX, int screenY)
    {
        _dragging = true;
        _dragMoved = false;
        _lastDragStepX = 0;
        _dragOriginCursor = new Win32.POINT { X = screenX, Y = screenY };
        _dragOriginWindow = (PhysicalRect().X, PhysicalRect().Y);
        SetClickThrough(false);
    }
    private void UpdateDragAt(int screenX, int screenY)
    {
        if (!_dragging) return;
        var dx = screenX - _dragOriginCursor.X;
        var dy = screenY - _dragOriginCursor.Y;
        if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)
        {
            _longPressTimer.Stop();
      // it turned into a drag, so it is not a long press
            if (!_dragMoved)
            {
                _dragMoved = true;
                ShowLocalBubble(_lines.Pick("drag") ?? "诶诶诶——", 1800);
            }
        }
        // P5: shaking her side to side makes her dizzy. Feed the TOTAL offset and let the
        // easter-egg code derive the step (the old inline sign expression was always 0,
        // which is exactly why the shake never triggered).
        var reaction = _eggs.RegisterDragSample(Environment.TickCount64, dx);
        if (reaction == Reaction.Dizzy) React(reaction);
        _lastDragStepX = dx - _lastDragStepX;
        MovePhysical(_dragOriginWindow.X + dx, _dragOriginWindow.Y + dy);
    }
    private async Task CompleteDragAsync()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_visual.PetImage.IsMouseCaptured) _visual.PetImage.ReleaseMouseCapture();
        if (!_dragMoved)
        {
            await InteractAsync("pet");
            // a click without movement = petting
            return;
        }
        // A fast release throws her: glide a little before the snap.
        if (Math.Abs(_lastDragStepX) > 22)
        {
            var (x, y, _, _) = PhysicalRect();
            var drift = Math.Sign(_lastDragStepX) * 40;
            for (var i = 3; i >= 1; i--)
            {
                MovePhysical(x + drift * i / 3, y);
                await Task.Delay(16).ConfigureAwait(true);
            }
            ShowLocalBubble(_lines.Pick("drag") ?? "飞起来啦～", 1500, force: true);
        }
        var (rx, ry, rw, rh) = PhysicalRect();
        var m = Monitors.NearestToWindow(rx, ry, rw, rh);
        var anchor = Placement.DecideAnchor(m, rx + rw / 2);
        var (nx, ny) = Placement.Edge(m, anchor, rw, rh, ry, _opts.MarginX);
        _monitor = m;
        MovePhysical(nx, ny);
        _ui.Monitor = m.Device;
        _ui.Anchor = anchor;
        Persist();
        _log.Info($"drag ended: {rx},{ry} -> snapped {nx},{ny} anchor={anchor} monitor={m.Device}");
        LogRect();
        // Dragging her somewhere else while a card is open must re-place the card,
        // otherwise the panel stays where it was and can hang off the screen.
        if (AnyCardOpen) ApplyExpandedHeight();
        ShowLocalBubble(_lines.Pick("snap") ?? "就这儿了", 2500);
    }
    private void OnPetRightClick(object sender, MouseButtonEventArgs e)
    {        e.Handled = true;
        ToggleMenu();
    }
    // ---------------------------------------------------------------- interactions
    private async Task CelebrateAsync(int clicks)
    {
        var line = clicks >= 3 ? "好啦好啦，我知道你很喜欢我！" : "耶！";
        ShowLocalBubble(line, 2200, force: true);
        _player.Play("jumping", Environment.TickCount64, restart: true);
        _visual.SetFrame(_player.Track.Row, _player.FrameColumn);
        _log.Info($"double-click celebration (clicks={clicks})");
        await Task.Delay(120);
    }
    private async Task InteractAsync(string kind)
    {
        // P5: feeding her too fast has a consequence.
        if (kind == "feed")
        {
            var reaction = _eggs.RegisterFeed(Environment.TickCount64);
            if (reaction == Reaction.Overfed)
            {                React(reaction);
                return;
      // she refuses the snack, so DSH is not told about it
            }
        }
        var line = _lines.Pick(kind) ?? (kind == "feed" ? "谢谢老板" : "嘿嘿");
        ShowLocalBubble(line, kind == "feed" ? 3000 : 2200);
        var error = await _state.InteractAsync(kind).ConfigureAwait(true);
        if (error is null) _log.Info($"interact '{kind}' accepted by DSH (affinity updated)");
        else _log.Warn($"interact '{kind}' refused: {error}");
    }
    private void ShowLocalBubble(string text, int holdMs, bool force = false)
    {
        if (!force && Environment.TickCount64 < _localBubbleUntilMs) return;
        _localBubbleUntilMs = Environment.TickCount64 + holdMs;
        _visual.SetBubble(text);
    }

    /// <summary>
    /// 对外的一句话提示（App 的 UI 异常守门人用）。走本地气泡通道，不碰托盘状态：
    /// 托盘那行是 DSH 的在线状态，被覆盖会误导用户。
    /// </summary>
    public void ShowNotice(string text, int holdMs = 4000) => ShowLocalBubble(text, holdMs, force: true);
    // ---------------------------------------------------------------- menu
    private void ToggleMenu()
    {
        if (_menuOpen) CloseMenu();
        else OpenMenu();
    }
    private void OpenMenu()
    {
        // A menu opened on top of an already-open card made the window taller than the
        // work area, which pushed the pet off the bottom of the screen (she showed up
        // half cut). Only one card at a time, and ApplyExpandedHeight has a hard ceiling.
        CloseCardsExcept("menu");
        EnforcePetVisible("before-menu");
        RefreshDshPresence(force: true);   // the label must reflect DSH right now, not up to 2s ago
        var monitors = Monitors.All(refresh: true);
        var nowTicks = Environment.TickCount64;

        // Hot items are inlined on the first page; everything else lives in a group.
        var hot = new List<PetMenuEntry>
        {
            new("摸摸头", () => _ = InteractAsync("pet")),
            new("喂小鱼干", () => _ = InteractAsync("feed")),
            new("", null, Separator: true),
            new(_dshPresence switch
            {
                DshLocator.DshPresence.Visible => "切到 DSH 窗口",
                DshLocator.DshPresence.Hidden => "把 DSH 窗口叫回来",
                _ => "启动 DSH",
            }, BringDshToFront),
            new(_panelOpen ? "关闭监控面板" : "监控面板", TogglePanel),
            new(_cleanupOpen ? "关闭 C 盘清理" : "C 盘清理", ToggleCleanup),
            new("快捷启动…", ShowLauncher),
            new("剪贴板历史…", ShowClipboardHistory),
        };

        var display = new List<PetMenuEntry>
        {
            new("贴右下角", ResetPosition),
            new(_paused ? "继续动画" : "暂停动画", TogglePause),
            new("隐藏（Ctrl+Alt+W 唤回）", ToggleVisible),
        };
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var index = i;
            display.Add(new($"搬到 {m.Label}", () => PickMonitor(index), Checked: m.Device == _monitor.Device));
        }
        display.Add(new("退出小鲸娘", () => QuitGracefully("menu")));

        var focus = new List<PetMenuEntry>
        {
            new(_pomodoro.Running ? $"停止番茄钟（{_pomodoro.Describe(nowTicks)}）" : $"开始番茄钟（{(_pomodoro.AutoPresetMinutes == 0 ? "自动" : _pomodoro.AutoPresetMinutes + " 分钟")}）", TogglePomodoro),
            new($"番茄钟时长：{_pomodoro.PresetLabel}", CyclePomodoroPreset),
            new(_lowMemory.Enabled ? "关闭低内存模式" : "开启低内存模式", () => ApplyLowMemoryMode(!_lowMemory.Enabled, "menu"), Checked: _lowMemory.Enabled),
            new("开机自动启动", ToggleAutoStart, Checked: _autoStartEnabled),
            new(_focusQuiet ? "专注时安静：开" : "专注时安静：关", ToggleFocusQuiet, Checked: _focusQuiet),
            new(_cursorFollow ? "跟随光标（彩蛋）：开" : "跟随光标（彩蛋）：关", ToggleCursorFollow, Checked: _cursorFollow),
            new($"跟随速度：{FollowSpeedLabel}", CycleFollowSpeed),
        };

        var alerts = new List<PetMenuEntry>
        {
            new(_longTaskAlerts ? "长任务完成提醒：开" : "长任务完成提醒：关", ToggleLongTaskAlerts, Checked: _longTaskAlerts),
            new(_osTaskAlerts ? "系统任务提醒：开" : "系统任务提醒：关", ToggleOsTaskAlerts, Checked: _osTaskAlerts),
            new(_appTooLongAlerts ? "应用开太久提醒：开" : "应用开太久提醒：关", ToggleAppTooLongAlerts, Checked: _appTooLongAlerts),
            new(_downloadAlerts ? "下载完成提醒：开" : "下载完成提醒：关", ToggleDownloadAlerts, Checked: _downloadAlerts),
            new(_sedentaryAlerts ? "久坐提醒：开" : "久坐提醒：关", ToggleSedentaryAlerts, Checked: _sedentaryAlerts),
            new(_clipHistoryEnabled ? "剪贴板历史：开" : "剪贴板历史：关", ToggleClipboardHistory, Checked: _clipHistoryEnabled),
            new(_dropEnabled ? "拖放区：开" : "拖放区：关", ToggleDropZone, Checked: _dropEnabled),
            new("复制拖入的文件到…", CopyLastDroppedTo, Enabled: _lastDroppedFiles.Count > 0),
        };

        var dsh = new List<PetMenuEntry>
        {
            new(_dshPresence switch
            {
                DshLocator.DshPresence.Visible => "切到 DSH 窗口",
                DshLocator.DshPresence.Hidden => "把 DSH 窗口叫回来",
                _ => "启动 DSH",
            }, BringDshToFront),
        };
        if (_dshPresence == DshLocator.DshPresence.Visible)
            dsh.Add(new("把 DSH 收进托盘（关闭窗口）", HideDshWindow));
        dsh.Add(new(_panelOpen ? "关闭监控面板" : "监控面板", TogglePanel));
        dsh.Add(new(_cleanupOpen ? "关闭 C 盘清理" : "C 盘清理", ToggleCleanup));

        var help = new List<PetMenuEntry>
        {
            new("新手引导…", ShowOnboarding),
            new("没找到 DSH 怎么办…", ShowDshGuide),
            new("插件体检…", ShowPluginHelper),
            new(_updateAvailableTag is null ? "检查更新" : $"检查更新（发现 {_updateAvailableTag}）", () => _ = CheckUpdateAsync(silent: false)),
            new("打开发布页", () => OpenReleasePage($"https://github.com/{UpdateRepo}/releases/latest")),
        };

        var sections = new List<MenuSection>
        {
            new("常用", hot, Inline: true),
            new("显示与位置", display),
            new("专注与系统", focus),
            new("提醒与开关", alerts),
            new("DSH 与监控", dsh),
            new("新手与帮助", help),
        };

        var visibleItems = sections.Sum(s => s.Items.Count(i => !i.Separator));
        try
        {
            _menuWindow?.Close();
            // Physical coordinates on purpose: with mixed DPI (125% + 100%) a DIP position means
            // different pixels depending on which monitor the menu lands on - that made the menu
            // invisible on monitor 2.
            var (px, py, pw, ph) = PhysicalRect();
            var petPhys = new Rect(px, py, pw, ph);
            var workPhys = new Rect(_monitor.Left, _monitor.Top,
                _monitor.Right - _monitor.Left, _monitor.Bottom - _monitor.Top);
            _menuWindow = new MenuWindow(sections, petPhys, workPhys, _monitor.Scale);
            // Closing the menu window by itself (Esc / outside click / item chosen) must reset our
            // state too: _menuOpen feeds AnyCardOpen, and a stale true blocked follow-cursor and
            // kept the window in "card open" mode forever.
            _menuWindow.Closed += (_, _) =>
            {
                // Null the field even when the flags below already say "closed": a stale reference
                // to a closed window made later Close() calls silent no-ops.
                _menuWindow = null;
                if (!_menuOpen) return;
                _menuOpen = false;
                _menuHeight = 0;
                _log.Info("menu window closed");
                EnforcePetVisible("menu-closed");
            };
            // Show + place in one step, with the window transparent until the placement is done:
            // "Show() then Place()" painted one frame at the default window position first (the
            // reported "menu flashes" of 0.24.x).
            var menu = _menuWindow;
            PopupFrame.ShowPlaced(menu, () =>
            {
                // Mark the menu open BEFORE placing it: Place() can throw (monitor unplugged, work
                // area nonsense), and the old order left a visible menu on screen while our own
                // state said "closed", so nothing could ever close it.
                _menuOpen = true;
                menu.Place();
                _log.Info("menu window placed: " + menu.PlaceDiagnostics);
            });
            _log.Info($"menu monitor={_monitor.Device} scale={_monitor.Scale:0.##}");
            // The menu is a separate window now, so the pet window must be untouched; prove it.
            EnforcePetVisible("menu-open");
        }
        catch (Exception ex)
        {
            _log.Warn("menu window failed: " + ex.Message);
            // A half-opened menu must not outlive the failure: close it and reset every flag so
            // the next right-click starts from a clean state.
            try { _menuWindow?.Close(); } catch { }
            _menuWindow = null;
            _menuOpen = false;
            _menuHeight = 0;
            ShowLocalBubble("菜单打不开：" + ex.Message, 4000, force: true);
            return;
        }
        _menuHeight = 0;

        _log.Info($"menu opened as window: sections={sections.Count} items={visibleItems}");
    }

    private void BringDshToFront()
    {
        _ = BringDshToFrontAsync();
    }

    private async Task<DshActivationResult?> BringDshToFrontAsync()
    {
        try
        {
            var progress = new Progress<string>(message =>
            {
                _log.Info($"open-DSH bubble: {message}");
                // 90 s（v0.24.4 的值）是为"冷启动可能很慢"猜的，但它同时把气泡锁死了：PollStateAsync
                // 的成功分支只在 hold 过期后才允许用 DSH 的文案覆盖屏幕，于是启动完成之后
                // "DSH 已经打开了"还会继续挂一分半。进度行本来就会被下一条进度替换，8 s 足够兜住
                // 一步慢操作，超时后立刻把气泡还给 DSH。
                ShowLocalBubble(message, 8_000, force: true);
            });
            var result = await _dsh.BringToFrontAsync(progress);
            if (!string.IsNullOrWhiteSpace(result.ExePath))
            {
                _ui.DshExePath = result.ExePath;
                Persist();
            }

            // 启动流程已经结束，确认语只需要 4 s：之后 DSH 有自己的话说就显示它的话，
            // 没话说（空文案）就清空，不再由本地台词长期霸屏。
            ShowLocalBubble(result.UserMessage, 4_000, force: true);
            _log.Info(
                $"open-DSH completed outcome={result.Outcome} mechanism={result.Mechanism} " +
                $"elapsedMs={result.ElapsedMs} processes={result.ProcessCountBefore}->{result.ProcessCountAfter}");
            return result;
        }
        catch (Exception ex)
        {
            _log.Error("open-DSH failed: " + ex);
            ShowLocalBubble("DSH 唤起失败，详情已写入日志", 9_000, force: true);
            return null;
        }
    }
    private void CloseMenu()
    {
        if (!_menuOpen) return;
        _menuWindow?.Close();
        _menuWindow = null;
        _menuOpen = false;
        _menuHeight = 0;
        _log.Info("menu closed");
        EnforcePetVisible("menu-close");
        Persist();
    }

    /// <summary>
    /// Safety net: the pet window must always be fully inside the work area.
    ///
    /// Every layout bug this project had (pet clipped, pet pushed off screen) came from one
    /// window growing without a supervisor. This clamp runs after any layout change; if it ever
    /// has to move the window it logs a warning, so the underlying bug stays visible instead of
    /// being silently papered over.
    /// </summary>
    private void EnforcePetVisible(string when)
    {
        try
        {
            var (x, y, w, h) = PhysicalRect();
            if (w <= 0 || h <= 0) return;
            var right = x + w;
            var bottom = y + h;
            if (x >= _monitor.Left && y >= _monitor.Top && right <= _monitor.Right && bottom <= _monitor.Bottom) return;

            var nx = Math.Max(_monitor.Left, Math.Min(x, _monitor.Right - w));
            var ny = Math.Max(_monitor.Top, Math.Min(y, _monitor.Bottom - h));
            _log.Warn($"pet was outside the work area [{when}]: ({x},{y})-({right},{bottom}) vs " +
                      $"({_monitor.Left},{_monitor.Top})-({_monitor.Right},{_monitor.Bottom}); moved to ({nx},{ny})");
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, nx, ny, w, h,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            Persist();
        }
        catch (Exception ex)
        {
            _log.Diag("enforce-visible", ex.Message);
        }
    }
    /// <summary>Show/hide the monitoring card. Only one card is open at a time.</summary>
    /// <summary>One-time wiring of the panel's kill requests (the panel is created with the visual).</summary>
    private void EnsurePanelWiring()
    {
        if (_panelWired) return;
        _panelWired = true;
        _visual.Panel.KillRequested += OnKillRequested;
        _visual.Panel.OpenPathRequested += OpenPath;
        _visual.Panel.AnalyzeRequested += path => _ = AnalyzeAndShowAsync(path);
    }

    // ---------------------------------------------------------------- P1 features

    private void OnLongTaskFinished(TaskFinish finish)
    {
        var minutes = (int)(finish.DurationMs / 60_000);
        var seconds = (int)((finish.DurationMs % 60_000) / 1000);
        var length = minutes > 0 ? $"{minutes} 分 {seconds} 秒" : $"{seconds} 秒";
        var ok = finish.Outcome == TaskOutcome.Completed;
        _log.Info($"long task {(ok ? "completed" : "failed")} after {finish.DurationMs}ms (last phase={finish.LastPhase ?? "none"}, alerts={_longTaskAlerts})");
        if (!_longTaskAlerts) return;

        ShowLocalBubble(ok ? $"长任务完成啦（用了 {length}）" : $"长任务失败了（跑了 {length}）", 7000, force: true);
        _player.Play(ok ? "jumping" : "failed", Environment.TickCount64, restart: true);
        _visual.SetFrame(_player.Track.Row, _player.FrameColumn);
        _tray?.ShowTaskHint(ok ? "小鲸娘：长任务完成" : "小鲸娘：长任务失败",
            ok ? $"跑了 {length}，可以回来看看结果了" : $"跑了 {length} 后失败了，回来看看吧", 10_000);
    }

    /// <summary>Report OS-level long tasks: any app that worked hard for a while and then stopped.</summary>
    private void OnOsTasksFinished(IReadOnlyList<OsTaskFinished> tasks)
    {
        foreach (var task in tasks)
        {
            var minutes = (int)(task.ActiveMs / 60_000);
            var seconds = (int)((task.ActiveMs % 60_000) / 1000);
            var length = minutes > 0 ? $"{minutes} 分 {seconds} 秒" : $"{seconds} 秒";
            var how = task.How == OsTaskEnd.Exited ? "结束了" : "看起来做完了";
            var ioNote = task.TotalIoBytes >= 50_000_000
                ? $" · 读写约 {Fmt.Bytes(task.TotalIoBytes)}（峰值 {Fmt.Rate(task.PeakIoBytesPerSecond)}）"
                : "";
            var countNote = task.ProcessCount > 1 ? $"（{task.ProcessCount} 个进程）" : "";
            _log.Info($"os task finished: {task.Name}{countNote} {length} {how} peakCpu={task.PeakCpuPercent:0.0}% peakIo={task.PeakIoBytesPerSecond}B/s totalIo={task.TotalIoBytes}B end={task.How}");

            if (!_osTaskAlerts) continue;
            if (_taskWatch.IsBusy)
            {
                _log.Info("os task announcement muted: a DSH session is running, its own monitor covers it");
                continue;
            }
            ShowLocalBubble($"{task.Name}{countNote} 跑了 {length}，{how}{ioNote}", 7000, force: true);
            _tray?.ShowTaskHint("小鲸娘：一个任务结束了", $"{task.Name} 跑了 {length}，{how}{ioNote}", 10_000);
            _player.Play("jumping", Environment.TickCount64, restart: true);
            _visual.SetFrame(_player.Track.Row, _player.FrameColumn);
        }
    }

    /// <summary>Track which app owns the foreground window (for the "open too long" nudge).</summary>
    private void SampleForeground()
    {
        string? name = null;
        try
        {
            var hwnd = Win32.GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                Win32.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid > 4 && pid != (uint)Environment.ProcessId)
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    name = proc.ProcessName;
                }
            }
        }
        catch { }

        var now = Environment.TickCount64;
        _foregroundWatch.Sample(name, now);
        if (!_appTooLongAlerts) return;

        var reminder = _foregroundWatch.TakeReminder(now);
        if (reminder is null) return;
        var minutes = (int)(reminder.ForegroundMs / 60_000);
        _log.Info($"app-too-long: {reminder.Name} in front {minutes}min");
        ShowLocalBubble($"{reminder.Name} 已经在前台 {minutes} 分钟了，要不要喝口水、动一下？", 8000, force: true);
    }

    private void ToggleOsTaskAlerts()
    {
        _osTaskAlerts = !_osTaskAlerts;
        _ui.OsTaskAlerts = _osTaskAlerts;
        _ui.Save(_opts.StatePath);
        _taskMonitor.Reset();
        _log.Info($"os task alerts {(_osTaskAlerts ? "on" : "off")}");
        ShowLocalBubble(_osTaskAlerts ? "别的程序跑完长任务我也会告诉你" : "系统任务提醒关掉了", 3000, force: true);
    }

    private void ToggleAppTooLongAlerts()
    {
        _appTooLongAlerts = !_appTooLongAlerts;
        _ui.AppTooLongAlerts = _appTooLongAlerts;
        _ui.Save(_opts.StatePath);
        if (!_appTooLongAlerts) _foregroundWatch.Reset();
        _log.Info($"app-too-long alerts {(_appTooLongAlerts ? "on" : "off")}");
        ShowLocalBubble(_appTooLongAlerts ? "哪个程序在前台开太久，我会轻轻提醒你" : "应用开太久提醒关掉了", 3000, force: true);
    }

    private void ToggleLongTaskAlerts()
    {
        _longTaskAlerts = !_longTaskAlerts;
        _ui.LongTaskAlerts = _longTaskAlerts;
        _ui.Save(_opts.StatePath);
        _log.Info($"long-task alerts {(_longTaskAlerts ? "on" : "off")}");
        ShowLocalBubble(_longTaskAlerts ? "长任务完成时我会提醒你" : "长任务提醒关掉了", 3000, force: true);
    }

    /// <summary>Small caption at the bottom of the pet: what DSH is doing and for how long.</summary>
    private void UpdateStatusStrip()
    {
        var now = Environment.TickCount64;
        if (_statusText is null || now - _lastStatusStripMs < 900) return;
        _lastStatusStripMs = now;

        var busy = _taskWatch.IsBusy && _dshPresence != Recovery.DshLocator.DshPresence.NotRunning;
        if (!busy)
        {
            if (_statusStrip is not null) _statusStrip.Visibility = Visibility.Collapsed;
            return;
        }

        EnsureStatusStrip();
        var elapsed = TimeSpan.FromMilliseconds(_taskWatch.BusyElapsedMs(now));
        _statusText!.Text = $"DSH · {TaskWatch.PhaseLabel(_lastStatePhase)} · {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
        Grid.SetRow(_statusStrip!, Grid.GetRow(_visual.PetImage));   // follows the pet row when the card flips sides
        _statusStrip!.Visibility = Visibility.Visible;
    }

    private void EnsureStatusStrip()
    {
        if (_statusStrip is not null) return;
        _statusText = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF0, 0xE6, 0xEF, 0xFF)),
        };
        _statusStrip = new Border
        {
            Child = _statusText,
            Background = new SolidColorBrush(Color.FromArgb(0xB4, 0x0E, 0x14, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x8A, 0xB4, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1),
            IsHitTestVisible = false,          // clicks still reach the pet
            Visibility = Visibility.Collapsed,
        };
        _visual.Children.Add(_statusStrip);
    }

    private void CheckMemoryTiers(Snapshot s)
    {
        if (s.MemTotal == 0) return;
        var freePercent = s.MemAvailable * 100.0 / s.MemTotal;
        var tier = MemoryGuard.TierForFreePercent(freePercent);
        if (tier == 0) { _memTierWarned = 0; return; }

        var now = Environment.TickCount64;
        if (tier <= _memTierWarned && now - _memTierWarnAtMs < 10 * 60_000) return;
        _memTierWarned = tier;
        _memTierWarnAtMs = now;

        var advice = MemoryGuard.TierAdvice(tier);
        ShowLocalBubble($"内存只剩 {Fmt.Bytes(s.MemAvailable)}（{freePercent:0}%），{advice}", 7000, force: true);
        if (tier >= 2)
            _tray?.ShowTaskHint("小鲸娘：内存告急",
                $"可用 {Fmt.Bytes(s.MemAvailable)} / {Fmt.Bytes(s.MemTotal)}（{freePercent:0}%）。打开监控面板，里面有大户清单。", 10_000);
        _log.Info($"memory tier {tier}: free={freePercent:0.0}% ({s.MemAvailable}/{s.MemTotal} bytes)");
    }

    /// <summary>First click arms, second click (within 8s) kills; the guard refuses anything critical.</summary>
    /// <summary>
    /// First click arms, second click (within 8s) kills. The target is an app FAMILY: killing a
    /// single renderer left VS Code running (user report), so every PID of that name is killed
    /// together with its children.
    /// </summary>
    private void OnKillRequested(HogRow family)
    {
        EnsurePanelWiring();
        if (!MemoryGuard.CanKill(family.Name, family.Pids.FirstOrDefault(), family.MaxCpuPercent, out var reason))
        {
            _log.Warn($"kill refused name='{family.Name}': {reason}");
            ShowLocalBubble(reason, 5000, force: true);
            return;
        }

        var label = family.Count > 1 ? $"{family.Name}（{family.Count} 个进程）" : family.Name;
        var now = Environment.TickCount64;
        if (_killArmPid == family.Pids.FirstOrDefault() && now < _killArmUntilMs)
        {
            _killArmPid = 0;
            _killArmUntilMs = 0;
            var killed = 0;
            var freed = 0UL;
            foreach (var pid in family.Pids)
            {
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById(pid);
                    var ws = (ulong)proc.WorkingSet64;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2500);
                    killed++;
                    freed += ws;
                }
                catch (Exception ex)
                {
                    _log.Warn($"kill pid={pid} name='{family.Name}' failed: {ex.Message}");
                }
            }
            _log.Info($"killed family name='{family.Name}' killed={killed}/{family.Pids.Count} freed~{freed} bytes");
            ShowLocalBubble(killed > 0
                ? $"已经把 {label} 结束了（约释放 {Fmt.Bytes(freed)}）"
                : $"没能结束 {label}，详情见日志", 5000, force: true);
            return;
        }

        _killArmPid = family.Pids.FirstOrDefault();
        _killArmUntilMs = now + 8_000;
        _log.Info($"kill armed name='{family.Name}' count={family.Count} ws={family.WorkingSet}");
        ShowLocalBubble($"再点一次就结束 {label}（约 {Fmt.Bytes(family.WorkingSet)}）", 6000, force: true);
    }
    /// <summary>Report files that just finished appearing in the Downloads folder.</summary>
    private void PollDownloadFolder()
    {
        if (_watchFolder is null) return;
        var now = Environment.TickCount64;
        if (now - _lastFolderPollMs < 2_500) return;
        _lastFolderPollMs = now;

        try
        {
            var files = FolderWatch.Enumerate(_watchFolder);
            foreach (var done in _folderWatch.Sample(files, now))
            {
                _log.Info($"download finished: {done.Name} ({done.Bytes} bytes)");
                if (!_downloadAlerts) continue;
                ShowLocalBubble($"下载完成：{done.Name}（{Fmt.Bytes((ulong)done.Bytes)}）", 6000, force: true);
                _tray?.ShowTaskHint("小鲸娘：下载完成", $"{done.Name}（{Fmt.Bytes((ulong)done.Bytes)}）已经下载好了", 8000);
            }
        }
        catch (Exception ex)
        {
            _log.Diag("folderwatch", ex.Message);
        }
    }

    private void ToggleDownloadAlerts()
    {
        _downloadAlerts = !_downloadAlerts;
        _ui.DownloadAlerts = _downloadAlerts;
        _ui.Save(_opts.StatePath);
        _log.Info($"download alerts {(_downloadAlerts ? "on" : "off")}");
        ShowLocalBubble(_downloadAlerts ? "下载完成时我会告诉你" : "下载完成提醒关掉了", 3000, force: true);
    }

    /// <summary>C 盘分级预警: percentage and absolute free space, whichever is worse.</summary>
    private void CheckDiskTier(Snapshot s)
    {
        if (s.DiskTotal == 0) return;
        var tier = DiskGuard.Tier(s.DiskFree, s.DiskTotal);
        if (tier == 0) { _diskTierWarned = 0; return; }

        var now = Environment.TickCount64;
        if (tier <= _diskTierWarned && now - _diskTierWarnAtMs < 10 * 60_000) return;
        _diskTierWarned = tier;
        _diskTierWarnAtMs = now;

        var drive = s.DiskName.TrimEnd('\\');
        var advice = DiskGuard.Advice(tier);
        ShowLocalBubble($"{drive} 只剩 {Fmt.Bytes(s.DiskFree)}（{Fmt.Percent(s.DiskFreePercent)}），{advice}", 7000, force: true);
        if (tier >= 2)
            _tray?.ShowTaskHint("小鲸娘：磁盘空间告急",
                $"{drive} 可用 {Fmt.Bytes(s.DiskFree)} / {Fmt.Bytes(s.DiskTotal)}（{Fmt.Percent(s.DiskFreePercent)}）。打开监控面板，最下面有最占空间的目录。", 10_000);
        _log.Info($"disk tier {tier}: {drive} free={Fmt.Bytes(s.DiskFree)} ({s.DiskFreePercent:0.0}%)");
    }

    /// <summary>Measure the biggest directories on the system drive (background, cached ~10 min).</summary>
    private async Task ScanBigDirectoriesAsync()
    {
        if (_bigDirsScanning) return;
        if (Environment.TickCount64 - _bigDirsScannedAtMs < 10 * 60_000) return;
        _bigDirsScanning = true;
        try
        {
            _visual.Panel.SetBigDirectories(Array.Empty<BigDirectory>(), scanning: true);
            var list = await Task.Run(() => Cleanup.DirectorySizer.Scan(10, 12_000, _log)).ConfigureAwait(true);
            _bigDirsScannedAtMs = Environment.TickCount64;
            _visual.Panel.SetBigDirectories(list, scanning: false);
            _log.Info("big directories: " + string.Join(", ", list.Take(10).Select(d => $"{d.Label}={Fmt.Bytes(d.Bytes)}")));
            // The card just got taller; without this the window keeps the height measured when
            // it was empty and the pet ends up outside the work area (user report).
            ApplyExpandedHeight();
            LogLayoutBounds("after-scan");
        }
        catch (Exception ex)
        {
            _log.Warn("big directory scan failed: " + ex.Message);
            _visual.Panel.SetBigDirectories(Array.Empty<BigDirectory>(), scanning: false);
        }
        finally { _bigDirsScanning = false; }
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                ShowLocalBubble("这个路径已经不在了", 4000, force: true);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + path + "\"",
                UseShellExecute = true,
            });
            _log.Info($"opened in explorer: {path}");
        }
        catch (Exception ex)
        {
            _log.Warn($"open path failed '{path}': {ex.Message}");
            ShowLocalBubble("打开失败了：" + ex.Message, 5000, force: true);
        }
    }

    /// <summary>Log whether the window (and therefore the pet) is fully inside the work area.</summary>
    private void LogLayoutBounds(string when)
    {
        var (x, y, w, h) = PhysicalRect();
        var workBottom = _monitor.Bottom - (_panelOpen || _cleanupOpen ? 0 : 0);
        var petBottom = y + h;
        var petDip = h / _monitor.Scale;
        var cardDip = _panelOpen ? _panelHeight : 0;
        var petDip2 = _visual.PetHeight;
        var needed = cardDip + petDip2 + 36;          // card + pet + bubble/margins
        var fits = petDip >= needed - 2;
        _log.Info($"layout[{when}]: window={x},{y} {w}x{h} (bottom={petBottom}, workBottom={workBottom}) " +
                  $"card={cardDip:0}DIP pet={petDip2:0}DIP windowDip={petDip:0} needed={needed:0} " +
                  $"inside={(petBottom <= workBottom ? "YES" : "NO")} fitsCardAndPet={(fits ? "YES" : "NO ← PET CLIPPED")}");
    }

    // ---------- P3: drag & drop ----------
    private void OnDragEnter(object sender, DragEventArgs e) => AcceptDrag(e);

    private void OnDragOver(object sender, DragEventArgs e) => AcceptDrag(e);

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (!_dropEnabled) return;
        ShowLocalBubble("松手就交给我（我会把路径复制好）", 1500);
    }

    private void AcceptDrag(DragEventArgs e)
    {
        if (!_dropEnabled) return;
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        var hasText = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);
        if (!hasFiles && !hasText)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        ShowLocalBubble("松手，我帮你把路径复制好", 1200);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!_dropEnabled) return;
        e.Handled = true;
        try
        {
            var files = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? Interaction.DropTargets.FromFileDrop(e.Data.GetData(DataFormats.FileDrop) as string[])
                : Array.Empty<string>();
            var text = e.Data.GetDataPresent(DataFormats.UnicodeText)
                ? e.Data.GetData(DataFormats.UnicodeText) as string
                : (e.Data.GetDataPresent(DataFormats.Text) ? e.Data.GetData(DataFormats.Text) as string : null);
            HandleDrop(files, text);
        }
        catch (Exception ex)
        {
            _log.Warn("drop failed: " + ex.Message);
            ShowLocalBubble("这一下没接住，再试一次？", 4000, force: true);
        }
    }

    /// <summary>The drop payload, independent of WPF so --probe-drop can exercise it.</summary>
    private void HandleDrop(IReadOnlyList<string> files, string? text)
    {
        var clipboard = Interaction.DropTargets.ClipboardText(files, text);
        var preview = Interaction.DropTargets.Preview(files, text);
        // Files go on the clipboard as FileDrop (so Explorer can paste/copy them) AND as text
        // (so editors and DSH get the path). Text-only drops just get the text.
        if (files.Count > 0)
        {
            _lastDroppedFiles.Clear();
            _lastDroppedFiles.AddRange(files);
        }
        var copied = Interaction.ClipboardWriter.TryCopy(files, clipboard, out var error);
        _log.Info($"drop handled: files={files.Count} textLen={text?.Length ?? 0} preview='{preview}' copied={copied}" +
                  (copied ? "" : $" clipboardError='{error}'"));
        if (copied)
        {
            var how = files.Count > 0
                ? $"已复制文件 {preview}：粘到文件夹=复制，粘到文本框=路径（同目录会提示同名，可用右键菜单「复制到…」）"
                : $"已复制 {preview}，去按 Ctrl+V 就行";
            ShowLocalBubble(how, 6000, force: true);
            _tray?.ShowTaskHint("小鲸娘：已复制到剪贴板", clipboard.Length > 200 ? clipboard[..200] : clipboard, 6000);
        }
        else
        {
            // Say the truth: claiming success while the clipboard is empty is worse than failing.
            ShowLocalBubble($"没复制成功（{error}），再拖一次？", 6000, force: true);
        }
    }

    private void ToggleDropZone()
    {
        _dropEnabled = !_dropEnabled;
        _ui.DropZone = _dropEnabled;
        _ui.Save(_opts.StatePath);
        AllowDrop = _dropEnabled;
        _log.Info($"drop zone {(_dropEnabled ? "on" : "off")}");
        ShowLocalBubble(_dropEnabled ? "可以把文件拖到我身上" : "拖放区关掉了", 3000, force: true);
    }

    /// <summary>
    /// Copy the files the user last dropped into a folder they pick, renaming on conflict.
    /// Explorer cannot be intercepted, so pasting into the SAME folder always shows a clash
    /// dialog; this path does the copy ourselves and picks a free name instead.
    /// </summary>
    private void CopyLastDroppedTo()
    {
        if (_lastDroppedFiles.Count == 0)
        {
            ShowLocalBubble("先拖一个文件给我，再选「复制拖入的文件到…」", 4000, force: true);
            return;
        }
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "复制到哪个文件夹？" };
            bool? picked;
            try { picked = dialog.ShowDialog(); }
            catch (Exception dialogError)
            {
                _log.Warn("folder dialog failed: " + dialogError.Message);
                ShowLocalBubble("选文件夹的对话框打不开：" + dialogError.Message, 6000, force: true);
                return;
            }
            if (picked != true) return;
            if (string.IsNullOrWhiteSpace(dialog.FolderName)) return;

            var plan = Interaction.FileCopyHelper.Plan(_lastDroppedFiles, dialog.FolderName);
            var copied = Interaction.FileCopyHelper.Execute(plan, out var error);
            _log.Info($"copy-to-folder: folder='{dialog.FolderName}' copied={copied}/{plan.Count} error='{error}'");
            if (copied == 0)
                ShowLocalBubble("没复制成功：" + (error.Length > 0 ? error : "未知原因"), 6000, force: true);
            else
                ShowLocalBubble($"已复制 {copied} 个到「{Path.GetFileName(dialog.FolderName.TrimEnd('\\'))}」" +
                                (plan.Any(p => !p.Destination.EndsWith(Path.GetFileName(p.Source), StringComparison.OrdinalIgnoreCase)) ? "（重名已自动加编号）" : ""),
                                6000, force: true);
        }
        catch (Exception ex)
        {
            _log.Warn("copy-to-folder failed: " + ex.Message);
            ShowLocalBubble("复制失败了：" + ex.Message, 6000, force: true);
        }
    }

    // ---------- P3: quick launcher ----------
    private void ShowLauncher()
    {
        try
        {
            _launchers ??= Interaction.LauncherStore.Load(LauncherConfigPath);
            // 手改 JSON 出错时不再静默变回默认值：告诉用户备份在哪，原文件没被动过。
            if (Interaction.LauncherStore.LastRecoveryBackup is { } brokenLauncherConfig)
            {
                _log.Warn($"launcher config unreadable, kept a copy: {brokenLauncherConfig}");
                ShowLocalBubble($"快捷启动配置读不懂（多半是手改少了个逗号），已另存备份：{Path.GetFileName(brokenLauncherConfig)}", 8000, force: true);
            }
            if (_launcherWindow is { IsVisible: true })
            {
                _launcherWindow.Activate();
                return;
            }
            _launcherWindow = new LauncherWindow(_launchers, LauncherConfigPath, "小鲸娘 · 快捷启动",
                msg => ShowLocalBubble(msg, 4000, force: true));
            var workDipRight = (_monitor.Right - _monitor.Left) / _monitor.Scale;
            var workDipBottom = (_monitor.Bottom - _monitor.Top) / _monitor.Scale;
            // Positioned before Show(): see PopupFrame for the wrong-position flash this fixes.
            _launcherWindow.ShowBottomRight(workDipRight, workDipBottom);
            _log.Info($"launcher opened: {_launchers.Visible().Count} items, config={LauncherConfigPath}");
        }
        catch (Exception ex)
        {
            _log.Warn("launcher failed: " + ex.Message);
            ShowLocalBubble("快捷启动打不开：" + ex.Message, 5000, force: true);
        }
    }

    /// <summary>Nudge when the user has been working without a real break for a long time.</summary>
    // ---------- optional extras ----------

    private bool FocusActive => Interaction.FocusPolicy.IsFocusActive(_pomodoro.Running, _focusQuiet);

    private string FollowSpeedLabel => _followSpeed switch
    {
        <= 90 => "慢",
        <= 200 => "中",
        _ => "快",
    };

    private void CycleFollowSpeed()
    {
        _followSpeed = _followSpeed switch { <= 90 => 160, <= 200 => 320, _ => 80 };
        _ui.FollowSpeed = _followSpeed;
        Persist();
        _log.Info($"cursor follow speed = {_followSpeed} px/s");
        ShowLocalBubble($"跟随速度：{FollowSpeedLabel}（{_followSpeed} 像素/秒）", 3000, force: true);
    }

    private void ToggleCursorFollow()
    {
        _cursorFollow = !_cursorFollow;
        _ui.CursorFollow = _cursorFollow;
        Persist();
        _log.Info($"cursor follow {(_cursorFollow ? "on" : "off")}");
        if (_cursorFollow) { _lastFollowTickMs = Environment.TickCount64; _followTimer.Start(); } else { _followTimer.Stop(); }
    }

    private void ToggleFocusQuiet()
    {
        _focusQuiet = !_focusQuiet;
        _ui.FocusQuiet = _focusQuiet;
        Persist();
        _log.Info($"focus quiet {(_focusQuiet ? "on" : "off")}");
        ShowLocalBubble(_focusQuiet ? "专注时会安静（重要提醒照旧）" : "专注时也会说话", 3500, force: true);
    }

    /// <summary>
    /// Follow-cursor easter egg: only when the user has been idle for a while and the cursor is far
    /// away. The decision itself is a pure function (CursorFollowPolicy) so it is testable.
    /// </summary>
    private void UpdateCursorFollow()
    {
        if (!_cursorFollow) return;
        try
        {
            if (!Win32.GetCursorPos(out var cursor)) return;
            var (x, y, w, h) = PhysicalRect();
            var nowMs = Environment.TickCount64;
            var dtMs = _lastFollowTickMs == 0 ? 50 : Math.Clamp(nowMs - _lastFollowTickMs, 20, 200);
            _lastFollowTickMs = nowMs;
            var stepPx = Math.Clamp((int)Math.Round(_followSpeed * dtMs / 1000.0), 1, 12);
            var decision = Interaction.CursorFollowPolicy.Decide(
                enabled: true,
                paused: _paused,
                dragging: _dragging,
                cardOpen: AnyCardOpen,
                idleSeconds: _idleOverrideSeconds >= 0 ? _idleOverrideSeconds : Win32.IdleSeconds(),
                petLeft: x, petTop: y, petWidth: w, petHeight: h,
                cursorX: cursor.X, cursorY: cursor.Y,
                workLeft: _monitor.Left, workTop: _monitor.Top,
                workRight: _monitor.Right, workBottom: _monitor.Bottom,
                stepPx: stepPx);

            var verbose = _followDecisionsLogged < 40 || Environment.TickCount64 - _lastFollowLogMs > 30_000;
            if (!decision.Move)
            {
                if (verbose)
                {
                    _followDecisionsLogged++;
                    _lastFollowLogMs = Environment.TickCount64;
                    _log.Info($"FOLLOW blocked reason={decision.Why} idle={Win32.IdleSeconds()}s speed={_followSpeed} rect={Rect()}");
                }
                return;
            }

            Win32.SetWindowPos(_hwnd, IntPtr.Zero, x + decision.Dx, y + decision.Dy, 0, 0,
                Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            if (verbose)
            {
                _followDecisionsLogged++;
                _lastFollowLogMs = Environment.TickCount64;
                _log.Info($"FOLLOW moving dx={decision.Dx} dy={decision.Dy} speed={_followSpeed} pxs={stepPx} rect={Rect()}");
            }
            if (Environment.TickCount64 - _lastFollowPersistMs > 5000)
            {
                _lastFollowPersistMs = Environment.TickCount64;
                Persist();
            }
        }
        catch (Exception ex)
        {
            _log.Diag("cursor-follow", ex.Message);
        }
    }


    private void CheckSedentary()
    {
        if (!_sedentaryAlerts) return;
        try
        {
            var nudge = _sedentary.Sample(Win32.IdleSeconds(), Environment.TickCount64, DateTime.Now.Hour);
            if (nudge is null) return;
            _log.Info($"sedentary nudge #{nudge.NudgeCount}: active {nudge.ActiveMinutes} min");
            ShowLocalBubble($"已经坐了 {nudge.ActiveMinutes} 分钟啦，起来动一动、喝口水？", 8000, force: true);
            _tray?.ShowTaskHint("小鲸娘：该活动一下了", $"连续坐了 {nudge.ActiveMinutes} 分钟，站起来走两步吧", 8000);
            _player.Play("waving", Environment.TickCount64, restart: true);
            _visual.SetFrame(_player.Track.Row, _player.FrameColumn);
        }
        catch (Exception ex)
        {
            _log.Diag("sedentary", ex.Message);
        }
    }

    private void ToggleSedentaryAlerts()
    {
        _sedentaryAlerts = !_sedentaryAlerts;
        _ui.SedentaryAlerts = _sedentaryAlerts;
        _ui.Save(_opts.StatePath);
        if (!_sedentaryAlerts) _sedentary.Reset();
        _log.Info($"sedentary alerts {(_sedentaryAlerts ? "on" : "off")}");
        ShowLocalBubble(_sedentaryAlerts ? "坐太久我会提醒你起来动动" : "久坐提醒关掉了", 3000, force: true);
    }

    /// <summary>Record copied text locally so the user can get it back later.</summary>
    private void PollClipboardHistory()
    {
        if (!_clipHistoryEnabled) return;
        var now = Environment.TickCount64;
        if (now - _lastClipPollMs < 2000) return;
        _lastClipPollMs = now;

        if (!Win32.ClipboardHasText()) { PollClipboardImage(); return; }   // cheap, non-blocking probe first
        string? text = null;
        try
        {
            text = System.Windows.Clipboard.GetText();
        }
        catch
        {
            return;   // another app holds the clipboard; try again later
        }

        if (string.IsNullOrEmpty(text) || text == _lastClipText) return;
        _lastClipText = text;
        _clipHistory ??= Interaction.ClipboardHistoryStore.Load(ClipHistoryPath);
        if (_clipHistory.Add(text, DateTime.Now))
        {
            _clipHistory.Save(ClipHistoryPath);
            _log.Diag("cliphistory", $"recorded {text.Length} chars");
        }
    }


    /// <summary>
    /// Image clipboard history: encode the clipboard bitmap to PNG once, store the file path, and
    /// de-duplicate by content hash so a static image copied twice does not duplicate.
    /// </summary>
    private void PollClipboardImage()
    {
        var now = Environment.TickCount64;
        if (now - _lastClipImagePollMs < 2500) return;
        _lastClipImagePollMs = now;
        if (!Win32.ClipboardHasImage()) return;

        System.Windows.Media.Imaging.BitmapSource? image = null;
        try { image = System.Windows.Clipboard.GetImage(); } catch { return; }
        if (image is null || image.PixelWidth <= 0) return;
        // Tiny bitmaps are almost always programmatic/probe noise (icons, color swatches), not
        // something a person copied; recording them only clutters the history.
        if (image.PixelWidth < 24 || image.PixelHeight < 24)
        {
            _log.Diag("cliphistory", $"skip tiny image {image.PixelWidth}x{image.PixelHeight}");
            return;
        }

        byte[] bytes;
        try
        {
            using var stream = new MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            encoder.Save(stream);
            bytes = stream.ToArray();
        }
        catch (Exception ex)
        {
            _log.Diag("cliphistory", "image encode failed: " + ex.Message);
            return;
        }

        if (bytes.Length > 8 * 1024 * 1024) { _log.Diag("cliphistory", $"image too large ({bytes.Length} bytes)"); return; }
        var hash = HashBytes(bytes);
        if (hash == _lastClipImageHash) return;
        _lastClipImageHash = hash;

        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_opts.StatePath) ?? ".", "clipboard-images");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"img-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            File.WriteAllBytes(file, bytes);
            _clipHistory ??= Interaction.ClipboardHistoryStore.Load(ClipHistoryPath);
            if (_clipHistory.AddImage(file, image.PixelWidth, image.PixelHeight, DateTime.Now))
            {
                _clipHistory.Save(ClipHistoryPath);
                _log.Diag("cliphistory", $"recorded image {image.PixelWidth}x{image.PixelHeight} {bytes.Length} bytes");
            }
            else
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch (Exception ex)
        {
            _log.Diag("cliphistory", "image save failed: " + ex.Message);
        }
    }

    private static long HashBytes(byte[] data)
    {
        long hash = 1469598103934665603L;
        foreach (var b in data) { hash ^= b; hash *= 1099511628211L; }
        return hash;
    }
    private void ShowClipboardHistory()
    {
        try
        {
            _clipHistory ??= Interaction.ClipboardHistoryStore.Load(ClipHistoryPath);
            if (_clipWindow is { IsVisible: true })
            {
                _clipWindow.Activate();
                return;
            }
            _clipWindow = new ClipboardWindow(_clipHistory,
                () => _clipHistory?.Save(ClipHistoryPath),
                msg => ShowLocalBubble(msg, 4000, force: true));
            var workDipRight = (_monitor.Right - _monitor.Left) / _monitor.Scale;
            var workDipBottom = (_monitor.Bottom - _monitor.Top) / _monitor.Scale;
            // Placed before it is visible: see PopupFrame for the wrong-position flash this fixes.
            PopupFrame.ShowBottomRight(_clipWindow, workDipRight, workDipBottom, 20, 400);
            _log.Info($"clipboard history opened: {_clipHistory.Entries.Count} entries");
        }
        catch (Exception ex)
        {
            _log.Warn("clipboard history failed: " + ex.Message);
            ShowLocalBubble("剪贴板历史打不开：" + ex.Message, 5000, force: true);
        }
    }

    private void ToggleClipboardHistory()
    {
        _clipHistoryEnabled = !_clipHistoryEnabled;
        _ui.ClipboardHistory = _clipHistoryEnabled;
        _ui.Save(_opts.StatePath);
        _log.Info($"clipboard history {(_clipHistoryEnabled ? "on" : "off")}");
        ShowLocalBubble(_clipHistoryEnabled ? "复制过的文字我都会记着（只存本机）" : "剪贴板历史关掉了", 3000, force: true);
    }

    /// <summary>Analyse a directory (read-only) and show the report window.</summary>
    private async Task AnalyzeAndShowAsync(string path)
    {
        try
        {
            ShowLocalBubble("正在看看里面有什么…（只读）", 2500, force: true);
            _keepList ??= Interaction.KeepList.Load(KeepListPath);
            _ignoreList ??= Interaction.IgnoreList.Load(IgnoreListPath);
            var keep = _keepList;
            var ignore = _ignoreList;
            var report = await Task.Run(() =>
            {
                // Evidence of "in use": which processes are running and where they live.
                var running = Performance.AppUsageIndex.Running();
                return Cleanup.DirectoryAnalyzer.Analyze(path, 24, 12_000,
                    new Cleanup.AnalysisContext(running, keep, Ignore: ignore));
            }).ConfigureAwait(true);
            if (report is null)
            {
                ShowLocalBubble("这个目录打不开了", 4000, force: true);
                return;
            }
            _log.Info($"analysis: root='{report.Root}' total={Fmt.Bytes(report.Total)} items={report.Items.Count} " +
                      $"reclaimable={Fmt.Bytes(Cleanup.DirectoryAnalyzer.Reclaimable(report))}");
            foreach (var item in report.Items.Take(8))
                _log.Diag("analysis-item", $"{item.Name} = {Fmt.Bytes(item.Bytes)} [{Cleanup.DirectoryAnalyzer.VerdictLabel(item.Verdict)}] {item.Reason}");

            _analysisWindow?.Close();
            ShowAnalysis(report, keep, ignore);
            var workDipRight = (_monitor.Right - _monitor.Left) / _monitor.Scale;
            var workDipBottom = (_monitor.Bottom - _monitor.Top) / _monitor.Scale;
            if (_analysisWindow is not null)
                PopupFrame.ShowBottomRight(_analysisWindow, workDipRight, workDipBottom, 20, 460);
        }
        catch (Exception ex)
        {
            _log.Warn("analysis failed: " + ex.Message);
            ShowLocalBubble("分析失败了：" + ex.Message, 5000, force: true);
        }
    }

    private void ShowAnalysis(Cleanup.AnalysisReport report, Interaction.KeepList keep, Interaction.IgnoreList ignore)
    {
        _analysisWindow?.Close();
        _analysisWindow = new AnalysisWindow(report,
            msg => ShowLocalBubble(msg, 4000, force: true),
            deeper => _ = AnalyzeAndShowAsync(deeper),
            keep,
            () => keep.Save(KeepListPath),
            ignore,
            () => ignore.Save(IgnoreListPath),
            deeper => _ = DeepScanAsync(deeper),
            target => _ = CleanCachesAsync(target));
        var workDipRight = (_monitor.Right - _monitor.Left) / _monitor.Scale;
        var workDipBottom = (_monitor.Bottom - _monitor.Top) / _monitor.Scale;
        PopupFrame.ShowBottomRight(_analysisWindow, workDipRight, workDipBottom, 20, 460);
    }

    /// <summary>Dig 3 levels down and list every cache we can safely remove.</summary>
    private async Task DeepScanAsync(string path)
    {
        try
        {
            ShowLocalBubble("正在往下挖缓存…（只读）", 2500, force: true);
            _keepList ??= Interaction.KeepList.Load(KeepListPath);
            _ignoreList ??= Interaction.IgnoreList.Load(IgnoreListPath);
            var keep = _keepList;
            var ignore = _ignoreList;
            var found = await Task.Run(() =>
            {
                var running = Performance.AppUsageIndex.Running();
                var ctx = new Cleanup.AnalysisContext(running, keep, Ignore: ignore);
                return Cleanup.DirectoryAnalyzer.DeepScan(path, 3, 160, 9000, ctx);
            }).ConfigureAwait(true);

            var total = found.Aggregate(0UL, (sum, i) => sum + i.Bytes);
            _log.Info($"deep scan: root='{path}' found={found.Count} total={Fmt.Bytes(total)}");
            foreach (var item in found.Take(10))
                _log.Diag("deep", $"{item.Path} = {Fmt.Bytes(item.Bytes)}");

            if (found.Count == 0)
            {
                ShowLocalBubble("往下挖了 3 层，没找到可以直接删的缓存", 5000, force: true);
                return;
            }
            var report = new Cleanup.AnalysisReport($"{path}（自动深挖 3 层）", total, found, found.Count, false);
            ShowLocalBubble($"挖到 {found.Count} 个可删缓存，共 {Fmt.Bytes(total)}", 4000, force: true);
            ShowAnalysis(report, keep, ignore);
        }
        catch (Exception ex)
        {
            _log.Warn("deep scan failed: " + ex.Message);
            ShowLocalBubble("深挖失败了：" + ex.Message, 5000, force: true);
        }
    }

    /// <summary>Delete the analysed SafeCache items after an explicit confirmation.</summary>
    private async Task CleanCachesAsync(Cleanup.AnalysisReport report)
    {
        var items = report.Items.Where(i => i.Verdict == Cleanup.CleanVerdict.SafeCache).ToList();
        if (items.Count == 0)
        {
            ShowLocalBubble("这一页没有可安全清理的项", 4000, force: true);
            return;
        }
        var total = items.Aggregate(0UL, (sum, i) => sum + i.Bytes);
        var preview = string.Join("\n", items.Take(8).Select(i => "· " + i.Name + "（" + Fmt.Bytes(i.Bytes) + "）"));
        if (items.Count > 8) preview += $"\n… 还有 {items.Count - 8} 项";
        var answer = System.Windows.MessageBox.Show(
            $"将把 {items.Count} 项移入回收站（约 {Fmt.Bytes(total)}）：\n\n{preview}\n\n只清缓存/日志/旧版本，程序与账号配置不动。",
            "小鲸娘 · 确认清理", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var result = await Task.Run(() => Cleanup.CacheCleaner.Delete(items)).ConfigureAwait(true);
            _log.Info($"clean caches: deleted={result.Deleted} refused={result.Refused} freed={result.FreedBytes}");
            foreach (var message in result.Messages.Take(5)) _log.Warn("clean refused: " + message);
            var note = result.Refused > 0 ? $"，{result.Refused} 项被拒绝（见日志）" : "";
            var undo = Cleanup.RecycleBin.CanUndelete() ? "（回收站可还原）" : "（永久删除，无法还原）";
            ShowLocalBubble($"已清理 {result.Deleted} 项{note}，释放约 {Fmt.Bytes(result.FreedBytes)}{undo}", 6000, force: true);
            _analysisWindow?.Close();
            _bigDirsScannedAtMs = 0;
            _ = ScanBigDirectoriesAsync();
        }
        catch (Exception ex)
        {
            _log.Warn("clean caches failed: " + ex.Message);
            ShowLocalBubble("清理失败：" + ex.Message, 6000, force: true);
        }
    }

    /// <summary>
    /// --probe-dsh-api: ask the running DSH which of the endpoints we depend on still answer.
    ///
    /// Run this after a DSH/plugin update: it prints one line per dependency, so a broken
    /// integration is visible instead of a feature silently going dark.
    /// </summary>
    // ---------- P4: onboarding / no-DSH guide / plugin helper ----------

    private void ShowOnboarding()
    {
        try
        {
            if (_onboardingWindow is { IsVisible: true }) { _onboardingWindow.Activate(); return; }
            _onboardingWindow = new OnboardingWindow(ApplyOnboarding, () =>
            {
                var presence = DshLocator.Probe(_log, out var detail);
                return $"DSH 状态：{presence}（{detail}）";
            }, _cursorFollow, _followSpeed);
            PlaceWindowBottomRight(_onboardingWindow);
            _log.Info("onboarding window opened");
        }
        catch (Exception ex)
        {
            _log.Warn("onboarding failed: " + ex.Message);
            ShowLocalBubble("引导窗口打不开：" + ex.Message, 5000, force: true);
        }
    }

    private void ApplyOnboarding(OnboardingResult result)
    {
        _longTaskAlerts = result.LongTaskAlerts;
        _osTaskAlerts = result.OsTaskAlerts;
        _sedentaryAlerts = result.SedentaryAlerts;
        _ui.LongTaskAlerts = _longTaskAlerts;
        _ui.OsTaskAlerts = _osTaskAlerts;
        _ui.SedentaryAlerts = _sedentaryAlerts;
        _ui.Onboarded = true;
        Persist();
        _log.Info($"onboarding done: longTask={_longTaskAlerts} osTask={_osTaskAlerts} sedentary={_sedentaryAlerts} autostart={result.AutoStart}");
        if (result.AutoStart && !_autoStartEnabled) ToggleAutoStart();
        ShowLocalBubble("设置好了，右键我可以打开更多功能", 5000, force: true);
    }

    private void ShowDshGuide()
    {
        try
        {
            if (_dshGuideWindow is { IsVisible: true }) { _dshGuideWindow.Activate(); return; }
            _dshGuideWindow = new DshGuideWindow(
                () =>
                {
                    var presence = DshLocator.Probe(_log, out var detail);
                    return $"DSH 状态：{presence}（{detail}）";
                },
                path =>
                {
                    _ui.DshExePath = path;
                    Persist();
                    _dsh = new DshLocator(_log, path, false);
                    _log.Info($"DSH exe path set by user: {path}");
                    return "已记住位置：" + path;
                },
                BringDshToFront);
            PlaceWindowBottomRight(_dshGuideWindow);
            _ui.DshGuideShown = true;
            Persist();
            _log.Info("DSH guide window opened");
        }
        catch (Exception ex)
        {
            _log.Warn("DSH guide failed: " + ex.Message);
            ShowLocalBubble("打不开引导窗口：" + ex.Message, 5000, force: true);
        }
    }

    private void ShowPluginHelper()
    {
        try
        {
            if (_pluginWindow is { IsVisible: true }) { _pluginWindow.Activate(); return; }
            _pluginWindow = new PluginHelperWindow(
                () => Monitoring.PluginHelper.Scan(Cleanup.CleanupCatalog.DshHome),
                OpenPath);
            PlaceWindowBottomRight(_pluginWindow);
            _log.Info("plugin helper window opened");
        }
        catch (Exception ex)
        {
            _log.Warn("plugin helper failed: " + ex.Message);
            ShowLocalBubble("插件体检打不开：" + ex.Message, 5000, force: true);
        }
    }

    private void PlaceWindowBottomRight(Window window)
    {
        var scale = _monitor.Scale <= 0 ? 1.0 : _monitor.Scale;
        var workRightDip = _monitor.Right / scale;
        var workBottomDip = _monitor.Bottom / scale;
        // Placed before it is visible: see PopupFrame for the wrong-position flash this fixes.
        var (margin, fallback) = window switch
        {
            OnboardingWindow => (24.0, 420.0),
            DshGuideWindow => (24.0, 380.0),
            PluginHelperWindow => (24.0, 460.0),
            _ => (20.0, 400.0),
        };
        PopupFrame.ShowBottomRight(window, workRightDip, workBottomDip, margin, fallback);
    }

    private async Task ShowOnboardingAfterStartupAsync()
    {
        await Task.Delay(_opts.ShowOnboarding ? 400 : 2500);
        if (_ui.Onboarded && !_opts.ShowOnboarding) return;
        ShowOnboarding();
    }


    private void TogglePanel()
    {
        if (_panelOpen)
        {
            _visual.Panel.Visibility = Visibility.Collapsed;
            _panelOpen = false;
            ApplyExpandedHeight();
            _panelHeight = 0;
            _log.Info("monitor panel closed");
        }
        else
        {
            CloseCardsExcept("panel");
            _petOriginOverride = PetScreenOrigin();
            // Keep the card (and therefore the window) inside the work area: with the new
            // token/directory sections it grew tall enough to push the pet off-screen.
            var workDip = (_monitor.Bottom - _monitor.Top) / _monitor.Scale;
            // The card must never take the space the pet needs: cap at (work area - base window
            // height - margin), and at 52% of the work area as a second bound.
            var cardRoom = workDip - _baseHeight - 70;
            _visual.Panel.SetMaxHeight(Math.Max(200, Math.Min(workDip * 0.52, cardRoom)));
            _log.Diag("panel cap", $"workDip={workDip:0} base={_baseHeight:0} cardRoom={cardRoom:0} cap={Math.Max(200, Math.Min(workDip * 0.52, cardRoom)):0}");
            _visual.Panel.Update(_lastSnapshot);
            _visual.Panel.Visibility = Visibility.Visible;
            UpdateLayout();
            _panelHeight = _visual.Panel.ActualHeight > 0 ? _visual.Panel.ActualHeight : 168;
            _panelOpen = true;
            _monitorService.SetInterval(_lowMemory.MonitorIntervalMs(panelOpen: true));
            _ = ScanBigDirectoriesAsync();
            ApplyExpandedHeight();
            _log.Info($"monitor panel opened (height={_panelHeight:0})");
        }
        Persist();
    }
    /// <summary>The cleanup card. Scanning is read-only; deleting needs a second click per row.</summary>
    private void ToggleCleanup()
    {
        if (_cleanupOpen)
        {
            _visual.Cleanup.Visibility = Visibility.Collapsed;
            _cleanupOpen = false;
            ApplyExpandedHeight();
            _cleanupHeight = 0;
            _log.Info("cleanup card closed");
            Persist();
            return;
        }
        CloseCardsExcept("cleanup");
        // Remember where she is BEFORE the card becomes visible: laying out a tall card
        // inside the still-collapsed window pushes the pet down inside it, and the
        // placement decision would then read that pushed-down position.
        _petOriginOverride = PetScreenOrigin();
        _visual.Cleanup.RestoreDesiredListHeight();   // a fresh open starts from the user's size
        _visual.Cleanup.Visibility = Visibility.Visible;
        UpdateLayout();
        _cleanupHeight = _visual.Cleanup.ActualHeight > 0 ? _visual.Cleanup.ActualHeight : 230;
        _cleanupOpen = true;
        ApplyExpandedHeight();
        _log.Info($"cleanup card opened (height={_cleanupHeight:0})");
        Persist();
        _ = ScanCleanupAsync();
    }
    private void CloseCardsExcept(string keep)
    {
        if (keep != "panel" && _panelOpen) { _visual.Panel.Visibility = Visibility.Collapsed; _panelOpen = false; _panelHeight = 0; }
        if (keep != "cleanup" && _cleanupOpen)
        {
            // Closing the card is the user's way of saying "stop": let a running batch end after
            // the target it is on instead of deleting into a hidden card for minutes.
            CancelCleanupBatch("cleanup card closed");
            _visual.Cleanup.Visibility = Visibility.Collapsed;
            _cleanupOpen = false;
            _cleanupHeight = 0;
        }
    }
    private bool AnyCardOpen => _menuOpen || _panelOpen || _cleanupOpen;
    /// <summary>Measure every whitelisted target on a worker thread; nothing is modified.</summary>
    private async Task ScanCleanupAsync()
    {
        if (_cleanupScanning) return;
        _cleanupScanning = true;
        try
        {
            var targets = CleanupCatalog.Build(_cleanupTempAgeDays);
            _log.Info($"cleanup scan started ({targets.Count} targets, parallel)");
            _visual.Cleanup.SetBusy("正在扫描…（只读，不会删除）");
            // Sequential scanning took >25s because JetBrains alone holds 54k files.
            // Scans are independent and read-only, so they run concurrently.
            var workers = Math.Clamp(Environment.ProcessorCount, 4, 8);
            var scans = await Task.Run(() =>
            {
                var order = targets.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);
                return targets                    .AsParallel()                    .WithDegreeOfParallelism(workers)                    .Select(t => (Target: t, Scan: CleanupScanner.Scan(t, _log)))                    .ToList()                    .OrderBy(s => order[s.Target.Id])                    .ToList();
            }).ConfigureAwait(true);
            _visual.Cleanup.SetScans(scans);
            _log.Info("cleanup scan: " + _visual.Cleanup.DescribeScans(scans));
            _lastCleanupScans = scans;
        }
        catch (Exception ex)
        {
            _log.Error("cleanup scan failed: " + ex);
            _visual.Cleanup.SetBusy("扫描失败：" + ex.Message);
        }
        finally { _cleanupScanning = false; }
    }
    private IReadOnlyList<(CleanupTarget Target, CleanupScan Scan)> _lastCleanupScans = Array.Empty<(CleanupTarget, CleanupScan)>();
    private int _cleanupTempAgeDays = CleanupCatalog.DefaultTempAgeDays;
    private bool _sizeDirty;
    private long _lastSizeChangeMs;
    /// <summary>
    /// "一键清理放心项": only Safe-tier targets the scan actually found, cleaned one after
    /// another. Caution / admin / hands-off rows are never included — that is the point
    /// of the tier, and it is asserted by --selftest.
    /// </summary>
    private async Task RunSafeBatchAsync()
    {
        // Gate: v0.24.4 had none here (the scan did), so a second click while a batch was running
        // started a second pass over the SAME targets - two shell deletes on one folder at once -
        // and the progress line was overwritten by whichever pass wrote last.
        if (_cleanupBatchRunning)
        {
            _log.Warn("cleanup safe-batch ignored: a batch is already running");
            _visual.Cleanup.SetBusy("上一轮清理还没结束，稍等一下…");
            return;
        }

        var batch = CleanupCatalog.SafeBatch(_cleanupTempAgeDays);
        var present = batch.Where(t =>
        {
            var hit = _lastCleanupScans.FirstOrDefault(s => s.Target.Id == t.Id);
            return hit.Target is not null && (hit.Scan.Exists || hit.Scan.Bytes > 0);
        }).ToList();
        _log.Info($"cleanup safe-batch requested: {batch.Count} safe targets, {present.Count} present");
        if (present.Count == 0)
        {
            _visual.Cleanup.SetBusy("放心项里没有发现可清理的内容。");
            return;
        }

        _cleanupBatchRunning = true;
        _cleanupBatchCts = new CancellationTokenSource();
        var ct = _cleanupBatchCts.Token;
        long bytes = 0;
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        var cancelled = false;
        try
        {
            for (var i = 0; i < present.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                var target = present[i];
                _visual.Cleanup.SetBusy($"正在清理放心项 {i + 1}/{present.Count}：{target.Label}…");
                var result = await Task.Run(() => CleanupRunner.Clean(target, _log), ct).ConfigureAwait(true);
                bytes += result.Bytes;
                deleted += result.Deleted;
                skipped += result.Skipped;
                if (result.Error is not null) failed++;
                _log.Info($"safe-batch [{i + 1}/{present.Count}] {target.Id}: bytes={result.Bytes} deleted={result.Deleted} skipped={result.Skipped} error={result.Error ?? "none"}");
                await Task.Delay(150, ct).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            // v0.24.4 had no catch at all and the event handler discarded the Task (`_ = ...`),
            // so a single exception left the card stuck on "正在清理放心项 i/N…" for ever.
            _log.Error("cleanup safe-batch failed: " + ex);
            _visual.Cleanup.SetBusy("清理出错（已经停下）：" + ex.Message);
            return;
        }
        finally
        {
            _cleanupBatchRunning = false;
            _cleanupBatchCts?.Dispose();
            _cleanupBatchCts = null;
        }

        _log.Info($"cleanup safe-batch done: bytes={bytes} deleted={deleted} skipped={skipped} failedTargets={failed} cancelled={cancelled}");
        if (cancelled)
            ShowLocalBubble($"清理已停下，已经回收 {Fmt.Bytes((ulong)Math.Max(0, bytes))}", 3500, force: true);
        else
            ShowLocalBubble($"放心项清理完成，回收 {Fmt.Bytes((ulong)Math.Max(0, bytes))}", 4000, force: true);
        await ScanCleanupAsync();
        _visual.Cleanup.SetBusy($"放心项完成：回收 {Fmt.Bytes((ulong)Math.Max(0, bytes))}，{deleted} 项已进回收站"
                                + (skipped > 0 ? $"，{skipped} 项占用中被跳过" : "")
                                + (failed > 0 ? $"，{failed} 项失败" : "")
                                + (cancelled ? "（中途取消）" : ""));
    }

    /// <summary>
    /// Stop a running safe-batch between targets. Cancellation never interrupts a shell delete
    /// half way: the token is only checked before each target, so no folder is left half deleted.
    /// </summary>
    private void CancelCleanupBatch(string reason)
    {
        var cts = _cleanupBatchCts;
        if (cts is null || cts.IsCancellationRequested) return;
        _log.Warn($"cleanup safe-batch cancel requested ({reason})");
        cts.Cancel();
    }
    /// <summary>
    /// The "定期" half of the big cleanup: every N days (default 14) a read-only scan
    /// runs in the background and she mentions how much could be reclaimed. Nothing is
    /// deleted until the user opens the card and confirms.
    /// </summary>
    private async Task PeriodicCleanupReminderAsync()
    {
        await Task.Delay(20_000).ConfigureAwait(true);   // let her settle in first
        if (_cleanupScanning) return;
        _log.Info($"periodic cleanup scan due (last={_ui.LastCleanupScanUtc ?? "never"}, every {_ui.CleanupReminderDays} days)");
        await ScanCleanupAsync();
        var safe = _lastCleanupScans.Where(s => s.Target.SafeRegularly && s.Scan.Bytes > 0).Sum(s => s.Scan.Bytes);
        var total = _lastCleanupScans.Where(s => s.Target.Executable).Sum(s => s.Scan.Bytes);
        _ui.LastCleanupScanUtc = DateTime.UtcNow.ToString("o");
        _ui.Save(_opts.StatePath);
        if (total <= 0)
        {
            _log.Info("periodic cleanup scan: nothing reclaimable");
            return;
        }
        _log.Info($"periodic cleanup scan: reclaimable={total} safeBatch={safe}");
        ShowLocalBubble($"C 盘可回收 {Fmt.Bytes((ulong)total)}（放心项 {Fmt.Bytes((ulong)safe)}），右键我看「C 盘清理」", 9000, force: true);
        _tray?.ShowCleanupHint(total, safe);
    }
    /// <summary>Execute one whitelisted target. Runs on a worker thread, reports back on the UI thread.</summary>
    private async Task CleanTargetAsync(CleanupTarget target)
    {
        _log.Info($"cleanup execute requested: {target.Id} ({target.Label}, tier={target.Tier}, method={target.Method})");
        // A row click during a batch would run a second delete on a folder the batch is already
        // working on; refuse instead of racing.
        if (_cleanupBatchRunning)
        {
            _log.Warn("cleanup single target ignored: a safe-batch is running");
            _visual.Cleanup.SetBusy("正在清理放心项，等这一轮结束再点这一行吧…");
            return;
        }
        if (!CleanupCatalog.MayExecute(target))
        {
            _visual.Cleanup.SetResult(new CleanupResult(target.Id, 0, 0, 0, "该目标只报告、不执行", null));
            return;
        }
        ShowLocalBubble($"开始清理{target.Label}…", 2500, force: true);
        var result = await Task.Run(() => CleanupRunner.Clean(target, _log)).ConfigureAwait(true);
        _visual.Cleanup.SetResult(result);
        _log.Info($"cleanup result {result.TargetId}: bytes={result.Bytes} deleted={result.Deleted} skipped={result.Skipped} error={result.Error ?? "none"} cmd={(result.CommandOutput ?? "").Trim()}");
        if (result.Error is null)
            ShowLocalBubble($"清理完成，回收 {Fmt.Bytes((ulong)Math.Max(0, result.Bytes))}", 3500, force: true);
        else
            ShowLocalBubble($"{target.Label}：{result.Error}", 4000, force: true);
        _ = ScanCleanupAsync();
     // refresh the table after a cleanup
    }
    /// <summary>
    /// Ask Windows for administrator rights and let the elevated copy do the cleanup.
    /// The elevated process receives a target ID from the fixed catalog — never a path —
    /// and writes a JSON result that we read back, because the two processes share nothing.
    /// </summary>
    private async Task RunElevatedCleanAsync(CleanupTarget target)
    {
        if (!CleanupCatalog.MayExecuteElevated(target))
        {
            _visual.Cleanup.SetResult(new CleanupResult(target.Id, 0, 0, 0, "该项不在可提权清理的白名单内", null));
            return;
        }
        if (Elevation.IsElevated)
        {
            // Already elevated (rare, but then no second prompt is needed).
            var direct = await Task.Run(() => CleanupRunner.CleanElevated(target, _log)).ConfigureAwait(true);
            _visual.Cleanup.SetResult(direct);
            _ = ScanCleanupAsync();
            return;
        }
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            _visual.Cleanup.SetResult(new CleanupResult(target.Id, 0, 0, 0, "无法定位程序自身路径", null));
            return;
        }
        var resultPath = Path.Combine(_opts.BuildDir, "verify", $"elevated-{target.Id}.json");
        try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
        var args = $"--elevated-clean --target {target.Id} --result \"{resultPath}\"";
        _log.Info($"elevated cleanup requested: {target.Id}; raising the UAC prompt ({args})");
        ShowLocalBubble("请在弹窗里同意管理员授权…", 8000, force: true);
        var timeoutMs = target.Command == "dism-cleanup" ? 60 * 60 * 1000 : 10 * 60 * 1000;
        var launch = await Task.Run(() =>
        {
            var success = Elevation.TryRunElevated(exe, args, timeoutMs, out var exitCode, out var error);
            return (Success: success, ExitCode: exitCode, Error: error);
        }).ConfigureAwait(true);
        _log.Info($"elevated cleanup launch: success={launch.Success} exit={launch.ExitCode} error={launch.Error ?? "none"}");
        var elevated = Elevation.ReadResult(resultPath);
        if (elevated is null)
        {
            var message = launch.Error ?? (launch.Success ? $"提权进程退出码 {launch.ExitCode}，但没有留下结果文件" : "提权未能完成");
            _visual.Cleanup.SetResult(new CleanupResult(target.Id, 0, 0, 0, message, null));
            _log.Warn($"elevated cleanup produced no result: {message}");
            ShowLocalBubble($"{target.Label}：{message}", 6000, force: true);
            return;
        }
        var result = new CleanupResult(elevated.TargetId, elevated.Bytes, elevated.Deleted, elevated.Skipped, elevated.Error, elevated.Output);
        _visual.Cleanup.SetResult(result);
        _log.Info($"elevated cleanup result {elevated.TargetId}: elevated={elevated.WasElevated} ran={elevated.Ran} bytes={elevated.Bytes} deleted={elevated.Deleted} skipped={elevated.Skipped} error={elevated.Error ?? "none"} output={(elevated.Output ?? "").Trim()}");
        ShowLocalBubble(elevated.Error is null            ? $"管理员清理完成，回收 {Fmt.Bytes((ulong)Math.Max(0, elevated.Bytes))}"            : $"{target.Label}：{elevated.Error}", 5000, force: true);
        _ = ScanCleanupAsync();
    }
    /// <summary>
    /// Card resize. Geometry is COALESCED through a 40 ms timer instead of being applied
    /// on every mouse move: resizing a layered WPF window dozens of times per second made
    /// the card visibly flicker.
    /// </summary>
    private void OnCleanupResized()
    {
        _sizeDirty = true;
        _lastSizeChangeMs = Environment.TickCount64;
        if (!_geometryTimer.IsEnabled) _geometryTimer.Start();
    }
    private void OnGeometryTick()
    {
        _geometryTimer.Stop();
        ApplyWindowWidth();
        ApplyExpandedHeight();
    }
    /// <summary>Follow the user's card width, keeping the right edge where it was.</summary>
    private void ApplyWindowWidth()
    {
        var target = Math.Max(_visual.PetWidth, Math.Max(MonitorPanel.CardWidth, _visual.Cleanup.PreferredWidth + 10)) + 6;
        if (Math.Abs(target - Width) < 0.5) return;
        var petBefore = PetScreenOrigin();        Width = target;        RestorePetOrigin(petBefore);
    }
    /// <summary>Where the pet's top-left corner currently is, in physical pixels.</summary>
    private (int X, int Y) PetScreenOrigin()
    {
        try
        {
            var p = _visual.PetImage.PointToScreen(new Point(0, 0));
            return ((int)Math.Round(p.X), (int)Math.Round(p.Y));
        }
        catch { return (int.MinValue, int.MinValue); }
    }
    /// <summary>
    /// Move the window so the pet returns to a recorded screen position. This is what
    /// keeps the pet still while a panel opens, grows, or switches sides.
    /// </summary>
    private void RestorePetOrigin((int X, int Y) target)
    {
        if (target.X == int.MinValue || _hwnd == IntPtr.Zero) return;
        var now = PetScreenOrigin();
        if (now.X == int.MinValue) return;
        var dx = target.X - now.X;
        var dy = target.Y - now.Y;
        if (dx == 0 && dy == 0) return;
        var (x, y, w, h) = PhysicalRect();
        var m = _monitor;
        var nx = Math.Clamp(x + dx, m.Left, Math.Max(m.Left, m.Right - w));
        var ny = Math.Clamp(y + dy, m.Top, Math.Max(m.Top, m.Bottom - h));
        MovePhysical(nx, ny);
    }
    /// <summary>
    /// Pick the side and alignment for the card. MUST be called while the pet is still at
    /// the position the user left it: an earlier version decided after the layout change,
    /// read the already-shifted position, and therefore always chose "above" and pushed
    /// the pet down the screen.
    /// </summary>
    private void PlaceCardAroundPet(double cardDip, (int X, int Y) pet)
    {
        if (!AnyCardOpen) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        if (pet.X == int.MinValue) return;
        var petW = (int)Math.Round(_visual.PetWidth * dpi.DpiScaleX);
        var petH = (int)Math.Round(_visual.PetHeight * scale);
        var spaceAbove = (double)(pet.Y - _monitor.Top);
        var spaceBelow = (double)(_monitor.Bottom - (pet.Y + petH));
        var need = cardDip * scale;
        var fitsAbove = spaceAbove >= need + 8;
        var fitsBelow = spaceBelow >= need + 8;
        var below = !fitsAbove && (fitsBelow || spaceBelow > spaceAbove);
        _visual.SetCardSide(below);
        var petCenter = pet.X + petW / 2;
        var goLeft = petCenter < (_monitor.Left + _monitor.Right) / 2;
        _visual.SetCardAlignment(goLeft);
        // Logging on EVERY geometry frame meant a synchronous file write ~60 times a second
        // while dragging, which is a jank source. Only log when the decision changes.
        if (_lastPlacementKey != $"{below}|{goLeft}")
        {
            _lastPlacementKey = $"{below}|{goLeft}";
            _log.Info($"card placement: pet=({pet.X},{pet.Y}) spaceAbove={spaceAbove / scale:0} spaceBelow={spaceBelow / scale:0} " +                      $"needCard={cardDip:0} DIP fitsAbove={fitsAbove} fitsBelow={fitsBelow} -> side={(below ? "below" : "above")} align={(goLeft ? "left" : "right")}");
        }
    }
    private string? _lastPlacementKey;
    /// <summary>Where the pet was before a card became visible (see _petOriginOverride).</summary>
    private (int X, int Y)? _petOriginOverride;
    /// <summary>
    /// One place that owns the window's size: the card block (menu/panel/cleanup) plus the
    /// pet. Order matters — capture the pet, decide the side, size within the space that
    /// side actually has, then put the pet back exactly where it was.
    /// </summary>
    private void ApplyExpandedHeight()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
        var collapsed = _baseHeight;
        // 1a. Cards grow asynchronously (the panel fills its directory rows after the scan).
        // Using the stale height made the window too short and clipped the pet INSIDE it.
        // DesiredHeight is the content's own size, unaffected by the current (short) window.
        if (_panelOpen || _cleanupOpen) UpdateLayout();
        if (_panelOpen)
        {
            var panelWanted = _visual.Panel.DesiredHeight;
            if (panelWanted > 1) _panelHeight = Math.Round(panelWanted);
        }
        if (_cleanupOpen)
        {
            var cleanupWanted = _visual.Cleanup.ActualHeight;
            if (cleanupWanted > 1) _cleanupHeight = Math.Round(cleanupWanted);
        }
        // 1. the pet position BEFORE anything moves
        var petBefore = _petOriginOverride ?? PetScreenOrigin();
        _petOriginOverride = null;
        // 2. decide above/below and left/right from that position
        PlaceCardAroundPet(Math.Max(1, RequestedExtra()), petBefore);
        // 3. re-measure the card (a border drag changes its height; a cached value made
        //    dragging look like it did nothing)
        if (_cleanupOpen)
        {
            UpdateLayout();
            var measured = _visual.Cleanup.ActualHeight;
            if (measured > 1) _cleanupHeight = Math.Round(measured);
        }        double RequestedExtra() => (_menuOpen ? _menuHeight : 0) + (_panelOpen ? _panelHeight : 0) + (_cleanupOpen ? _cleanupHeight : 0);
        var requested = RequestedExtra();
        // 4. the ceiling is the space the CHOSEN SIDE has, not the whole screen
        var petPhysH = (int)Math.Round(_visual.PetHeight * scale);        double spaceOnSide;
        if (petBefore.Y != int.MinValue)
        {            spaceOnSide = _visual.CardBelow                ? (_monitor.Bottom - (petBefore.Y + petPhysH)) / scale                : (petBefore.Y - _monitor.Top) / scale;
        }
        else
        {
            // The pet origin is momentarily unavailable: bound by the window's own edge
            // rather than assuming the whole screen is free (that let the window grow
            // past the work area).
            var (_, currentTop, _, _) = PhysicalRect();            spaceOnSide = _visual.CardBelow ? (_monitor.Bottom - currentTop) / scale : (currentTop - _monitor.Top) / scale;
            _log.Warn($"pet origin unavailable; bounding the card by the window edge ({spaceOnSide:0} DIP)");
        }
        var workDip = Math.Max(320, (_monitor.Bottom - _monitor.Top) / scale);
        var ceiling = Math.Max(140, Math.Min(workDip - 2 * _opts.MarginY, spaceOnSide - _opts.MarginY));
        var allowed = Math.Max(0, Math.Min(requested, ceiling));
        if (_cleanupOpen && requested - allowed > 1)
        {
            // A TEMPORARY cap: the user's own list height is restored as soon as the card
            // has room again, so dragging never "eats" the size they asked for.
            var shrunk = _visual.Cleanup.ShrinkList(requested - allowed);
            if (shrunk > 0.5)
            {
                UpdateLayout();
                var measured = _visual.Cleanup.ActualHeight;
                if (measured > 1) _cleanupHeight = Math.Round(measured);
                _log.Info($"cards do not fit beside the pet: capped the list at {_visual.Cleanup.EffectiveListHeight:0} DIP (desired {_visual.Cleanup.ListHeight:0}, ceiling {ceiling:0})");
            }            requested = RequestedExtra();            allowed = Math.Max(0, Math.Min(requested, ceiling));
        }
        // 5. apply the size, then put the pet back
        _extraHeight = allowed;
        var targetHeight = collapsed + allowed;
        // 5b. FINAL hard bound. Enforcing the ceiling only on the card's share left a hole:
        // the pet origin can be read from an already-shifted window, and then the whole
        // window could end up taller than the work area (1102 > 1030) with the pet cut off.
        var maxHeightDip = Math.Max(200, workDip - 2 * _opts.MarginY);
        if (targetHeight > maxHeightDip)
        {
            var overflow = targetHeight - maxHeightDip;
            if (_cleanupOpen)
            {
                var reclaimed = _visual.Cleanup.ShrinkList(overflow);
                UpdateLayout();
                var measured = _visual.Cleanup.ActualHeight;
                if (measured > 1) _cleanupHeight = Math.Round(measured);
                _log.Info($"hard bound: window {targetHeight:0} > work area {maxHeightDip:0} DIP, reclaimed {reclaimed:0} DIP from the list");
            }            targetHeight = collapsed + Math.Max(0, RequestedExtra());
            if (targetHeight > maxHeightDip) targetHeight = maxHeightDip;
            _extraHeight = Math.Max(0, targetHeight - collapsed);
        }
        if (Math.Abs(targetHeight - Height) > 0.5) Height = targetHeight;
        UpdateLayout();        RestorePetOrigin(petBefore);
        // 5c. and the position is clamped to the work area unconditionally
        var (wx, wy, ww, wh) = PhysicalRect();
        var cx = Math.Clamp(wx, _monitor.Left, Math.Max(_monitor.Left, _monitor.Right - ww));
        var cy = Math.Clamp(wy, _monitor.Top, Math.Max(_monitor.Top, _monitor.Bottom - wh));
        if (cx != wx || cy != wy) MovePhysical(cx, cy);
        // No per-frame LogRect() here: it writes a file on every resize frame.
    }
    private void OnSnapshot(Snapshot snapshot)
    {
        _lastSnapshot = snapshot;
        if (!_panelOpen) return;        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_panelOpen) _visual.Panel.Update(snapshot);
            EnsurePanelWiring();
            CheckMemoryTiers(snapshot);
            CheckDiskTier(snapshot);
            _visual.Panel.SetUsage(UsageStats.Today());
            var osTasks = _taskMonitor.Observe(snapshot.Families, Environment.TickCount64);
            if (osTasks.Count > 0) OnOsTasksFinished(osTasks);
        }), DispatcherPriority.Background);
    }
    public void RequestShow(string reason)
    {
        if (!IsVisible)
        {
            Show();
            ShowLocalBubble("我在这儿！", 2500, force: true);
            _log.Info($"shown ({reason})");
        }
        else
        {
            ShowLocalBubble("我在这儿！", 2000, force: true);
            _log.Info($"already visible ({reason})");
        }
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        _tray?.SetVisibleState(true);
    }
    private void ToggleVisible()
    {
        if (IsVisible)
        {
            // The menu is a separate top-level window (no Owner), so hiding the pet left it
            // floating on screen with _menuOpen still true. AnyCardOpen then stayed true for
            // ever, permanently disabling click-through and follow-cursor until the user happened
            // to click outside the orphaned menu.
            if (_menuOpen) CloseMenu();
            Hide();
            Persist();
            _tray?.SetVisibleState(false);
            _tray?.ShowHiddenHint();
            _log.Info($"hidden (recover with: tray left click, {_hotkeyName}, or launching WhaleDesk again)");
        }
        else
        {            RequestShow("toggle");
        }
    }
    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused) _frameTimer.Stop();
        else _frameTimer.Start();
        _tray?.SetPausedState(_paused);
        Persist();
        _log.Info($"animation {(_paused ? "paused" : "resumed")}");
        ShowLocalBubble(_paused ? "那我先歇会儿" : "开工！", 2000, force: true);
    }
    /// <summary>
    /// Leave on purpose: persist, drop the tray icon, close. Without this the only exit was
    /// the tray menu, which users did not find (and a hard kill left the "unclean exit"
    /// marker behind, so she greeted the next start with "I fell over").
    /// </summary>
    public void QuitGracefully(string reason)
    {
        _log.Info($"quitting on purpose ({reason})");
        try { _tray?.Dispose(); } catch { }
        FinishAndClose();
    }

    /// <summary>Write the closing diagnostics, then close.</summary>
    public void FinishAndClose()
    {
        // Never leave a worker thread deleting files after the window is gone.
        CancelCleanupBatch("shutting down");
        Diagnostics = new[]
        {            $"rect={Rect()}",            $"monitor={_monitor.Device} anchor={_ui.Anchor ?? "none"}",            $"exstyle={Win32.Describe(Win32.GetExStyle(_hwnd))}",            $"clickThrough={_clickThrough}",            $"track={_player.TrackName} frame={_player.FrameIndex}",            $"state={_stateSummary}",            $"bubble={_visual.BubbleText}",            $"paused={_paused} hotkey={_hotkeyRegistered}",            $"foreground=0x{Win32.GetForegroundWindow():X}",
        };
        foreach (var d in Diagnostics) _log.Info("final: " + d);
        Persist();
        Close();        System.Windows.Application.Current?.Shutdown();
    }
    private string Rect()
    {
        var (x, y, w, h) = PhysicalRect();
        return w <= 0 ? "?" : $"{x},{y},{x + w},{y + h}";
    }}
