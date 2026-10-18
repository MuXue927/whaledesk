using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WhaleDesk.Recovery;

internal enum DshActivationOutcome
{
    AlreadyOpen,
    Started,
    RestoredFromTray,
    RunningNoWindow,
    NotStarted,
    Failed,
}

internal sealed record DshActivationResult(
    DshActivationOutcome Outcome,
    string UserMessage,
    string Mechanism,
    string Detail,
    string? ExePath,
    int ProcessCountBefore,
    int ProcessCountAfter,
    long ElapsedMs);

/// <summary>
/// Opens or restores DeepSeek Harness without changing its persistent state.
///
/// The important constraint is that DSH is Electron: calling ShowWindow on a
/// tray-hidden main window is immediately undone by Electron's own hidden state.
/// This class therefore asks DSH to show itself (second-instance / --updated),
/// falls back to its tray icon, and only uses Win32 focus helpers on a window
/// that is already visible.
/// </summary>
internal sealed class DshLocator
{
    private const string DshProcessName = "DeepSeek Harness";
    private const string DshTitleFragment = "DeepSeek Harness";
    private const string DshMainWindowClass = "Chrome_WidgetWin_1";
    private const string KnownExePath = @"D:\Software\DeepSeek Harness\DeepSeek Harness.exe";
    private const int HiddenWindowWaitMs = 8_000;
    private const int ColdStartWindowWaitMs = 90_000;
    private const int ProcessAppearanceWaitMs = 15_000;

    private readonly Log _log;
    private readonly string? _rememberedPath;
    private readonly bool _probeMode;
    private readonly object _gate = new();
    private Task<DshActivationResult>? _inflight;

    public DshLocator(Log log, string? rememberedPath, bool probeMode)
    {
        _log = log;
        _rememberedPath = rememberedPath;
        _probeMode = probeMode;
    }

    public Task<DshActivationResult> BringToFrontAsync(IProgress<string> progress)
    {
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false }) return _inflight;

            var task = Task.Run(() => ActivateCore(progress));
            _inflight = task;
            _ = task.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_inflight, task)) _inflight = null;
                }
            }, TaskScheduler.Default);
            return task;
        }
    }

    private DshActivationResult ActivateCore(IProgress<string> progress)
    {
        var total = Stopwatch.StartNew();
        var launches = new List<Process>();
        var processBefore = SnapshotProcesses();
        var windowsBefore = SnapshotWindows();
        LogSnapshot("before", processBefore, windowsBefore);

        try
        {
            if (TryFindVisibleMainWindow(out var visibleHwnd, out var visibleDetail))
            {
                _log.Info($"DSH PROBE H0 existing visible {visibleDetail}");
                var steps = Win32.BringToFront(visibleHwnd);
                Thread.Sleep(250);
                var nowVisible = Win32.IsWindowVisible(visibleHwnd);
                var foreground = Win32.GetForegroundWindow() == visibleHwnd;
                _log.Info($"DSH PROBE H0 bring-to-front steps={steps} visible={nowVisible} foreground={foreground}");
                progress.Report("DSH 已经打开了");
                return Result(
                    DshActivationOutcome.AlreadyOpen,
                    "DSH 已经打开了",
                    "H0-existing-visible",
                    $"{visibleDetail}; steps={steps}; foreground={foreground}",
                    _rememberedPath,
                    processBefore.Count,
                    SnapshotProcesses().Count,
                    total);
            }

            var exePath = LocateExe();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                _log.Warn("DSH PROBE locator failed: no executable path found");
                progress.Report("DSH 没能启动起来");
                return Result(
                    DshActivationOutcome.NotStarted,
                    "DSH 没能启动起来；没有找到 DSH 可执行文件",
                    "locator",
                    "no exe path found",
                    null,
                    processBefore.Count,
                    SnapshotProcesses().Count,
                    total);
            }

            _log.Info($"DSH PROBE locator exe='{exePath}'");
            var wasRunning = processBefore.Count > 0;
            progress.Report("正在启动…");

            if (wasRunning)
            {
                if (_probeMode)
                {
                    // H1: probe-only evidence path.
                    var h1 = TryLaunch(exePath, "--updated", "H1");
                    if (h1.Process is not null) launches.Add(h1.Process);
                    var h1Visible = WaitForVisibleWindow(
                        TimeSpan.FromMilliseconds(HiddenWindowWaitMs),
                        "H1",
                        out var h1Hwnd,
                        out var h1Detail);
                    LogLaunchState("H1", h1);
                    if (h1Visible)
                    {
                        progress.Report("DSH 已经打开了");
                        return Result(
                            DshActivationOutcome.RestoredFromTray,
                            "DSH 已经打开了",
                            "H1-relaunch--updated",
                            $"{h1Detail}; launchPid={h1.Pid?.ToString() ?? "none"}",
                            exePath,
                            processBefore.Count,
                            SnapshotProcesses().Count,
                            total);
                    }
                }
                else
                {
                    _log.Info("DSH PROBE H1 skipped outside probe mode; --updated is not used for user activation");
                }

                // H2: DSH's documented second-instance path calls window.show().
                var h2 = TryLaunch(exePath, null, "H2");
                if (h2.Process is not null) launches.Add(h2.Process);
                var h2Visible = WaitForVisibleWindow(
                    TimeSpan.FromMilliseconds(HiddenWindowWaitMs),
                    "H2",
                    out var h2Hwnd,
                    out var h2Detail);
                LogLaunchState("H2", h2);
                if (h2Visible)
                {
                    progress.Report("DSH 已经打开了");
                    return Result(
                        DshActivationOutcome.RestoredFromTray,
                        "DSH 已经打开了",
                        "H2-relaunch-second-instance",
                        $"{h2Detail}; launchPid={h2.Pid?.ToString() ?? "none"}",
                        exePath,
                        processBefore.Count,
                        SnapshotProcesses().Count,
                        total);
                }

                // H3: point at the application's own tray icon, including the overflow flyout.
                var invoked = InvokeTrayIconWithTimeout(TimeSpan.FromSeconds(8), out var trayDetail);
                if (invoked)
                {
                    var trayVisible = WaitForVisibleWindow(
                        TimeSpan.FromMilliseconds(HiddenWindowWaitMs),
                        "H3",
                        out var trayHwnd,
                        out var trayWindowDetail);
                    if (trayVisible)
                    {
                        progress.Report("DSH 已经打开了");
                        return Result(
                            DshActivationOutcome.RestoredFromTray,
                            "DSH 已经打开了",
                            "H3-tray-icon",
                            $"{trayDetail}; {trayWindowDetail}",
                            exePath,
                            processBefore.Count,
                            SnapshotProcesses().Count,
                            total);
                    }
                }
                else
                {
                    _log.Warn($"DSH PROBE H3 result=failed detail={trayDetail}");
                }

                var processAfterHidden = SnapshotProcesses();
                LogSnapshot("after-hidden-failure", processAfterHidden, SnapshotWindows());
                progress.Report("DSH 在运行，但窗口没显示出来（可能在托盘）");
                return Result(
                    DshActivationOutcome.RunningNoWindow,
                    "DSH 在运行，但窗口没显示出来；请点它的托盘图标",
                    "H1+H2+H3-all-failed",
                    $"processesBefore={processBefore.Count} processesAfter={processAfterHidden.Count}; {trayDetail}",
                    exePath,
                    processBefore.Count,
                    processAfterHidden.Count,
                    total);
            }

            LaunchObservation? activeLaunch = null;
            var activeMechanism = "H2-cold-no-args";
            var activeProcessDetail = "";
            var remainingWaitMs = ColdStartWindowWaitMs;

            // One-shot: if a cold start produces a hidden window, launch once more so DSH
            // shows it itself. Verified behaviour: the user's SECOND click always worked,
            // because by then DSH was running and the relaunch took over.
            var handoffFired = false;
            void HandoffOnHidden()
            {
                if (handoffFired) return;
                handoffFired = true;
                _log.Info("DSH PROBE cold: window exists but is hidden -> firing a hand-off launch " +
                          "(the running instance then takes second-instance -> focusOwner -> window.show())");
                var handoff = TryLaunch(exePath, null, "H2-cold-handoff");
                if (handoff.Process is not null) launches.Add(handoff.Process);
            }

            if (_probeMode)
            {
                // Probe-only H1: capture the real --updated behavior without using it
                // for the user's activation path.
                var h1Cold = TryLaunch(exePath, "--updated", "H1-cold");
                if (h1Cold.Process is not null) launches.Add(h1Cold.Process);
                var h1ProcessSeen = WaitForProcessAppearance(
                    TimeSpan.FromMilliseconds(ProcessAppearanceWaitMs),
                    "H1-cold",
                    out var h1ProcessDetail);
                LogLaunchState("H1-cold", h1Cold);
                if (h1ProcessSeen)
                {
                    progress.Report("已启动，正在加载界面…");
                    var h1ColdVisible = WaitForVisibleWindow(
                        TimeSpan.FromMilliseconds(5_000),
                        "H1-cold-early",
                        out var h1ColdHwnd,
                        out var h1ColdDetail,
                        () => progress.Report("DSH 已启动，仍在加载界面…"));
                    if (h1ColdVisible)
                    {
                        progress.Report("DSH 已经打开了");
                        return Result(
                            DshActivationOutcome.Started,
                            "DSH 已经打开了",
                            "H1-cold--updated",
                            $"{h1ProcessDetail}; {h1ColdDetail}",
                            exePath,
                            processBefore.Count,
                            SnapshotProcesses().Count,
                            total);
                    }

                    if (SnapshotProcesses().Count > 0)
                    {
                        activeLaunch = h1Cold;
                        activeMechanism = "H1-cold--updated";
                        activeProcessDetail = h1ProcessDetail;
                        remainingWaitMs = ColdStartWindowWaitMs - 5_000;
                    }
                    else
                    {
                        LogLaunchState("H1-cold-after-early-exit", h1Cold);
                        _log.Warn("DSH PROBE H1-cold exited before a window; falling back to H2-cold direct launch");
                    }
                }
            }
            else
            {
                _log.Info("DSH PROBE H1-cold skipped outside probe mode; starting DSH normally");
            }

            if (activeMechanism == "H2-cold-no-args")
            {
                var h2Cold = TryLaunch(exePath, null, "H2-cold");
                if (h2Cold.Process is not null) launches.Add(h2Cold.Process);
                var h2ProcessSeen = WaitForProcessAppearance(
                    TimeSpan.FromMilliseconds(ProcessAppearanceWaitMs),
                    "H2-cold",
                    out var h2ProcessDetail);
                LogLaunchState("H2-cold", h2Cold);
                if (!h2ProcessSeen)
                {
                    progress.Report("DSH 没能启动起来");
                    return Result(
                        DshActivationOutcome.NotStarted,
                        Restricted
                            ? $"DSH 没能启动起来：我运行在受限权限下（{SpawnDiagnostics.CurrentIntegrity()}），不能启动别的程序；请从资源管理器双击 run-whaledesk.cmd 重新启动我"
                            : "DSH 没能启动起来",
                        "H2-cold-process-not-seen",
                        $"{h2ProcessDetail}; launchError={h2Cold.Error ?? "none"}",
                        exePath,
                        processBefore.Count,
                        SnapshotProcesses().Count,
                        total);
                }

                progress.Report("已启动，正在加载界面…");
                var h2EarlyVisible = WaitForVisibleWindow(
                    TimeSpan.FromMilliseconds(5_000),
                    "H2-cold-early",
                    out _,
                    out var h2EarlyDetail,
                    hiddenWindowNotice: HandoffOnHidden);
                if (h2EarlyVisible)
                {
                    progress.Report("DSH 已经打开了");
                    return Result(
                        DshActivationOutcome.Started,
                        "DSH 已经打开了",
                        "H2-cold-no-args",
                        $"{h2ProcessDetail}; {h2EarlyDetail}",
                        exePath,
                        processBefore.Count,
                        SnapshotProcesses().Count,
                        total);
                }

                if (SnapshotProcesses().Count == 0)
                {
                    LogLaunchState("H2-cold-after-early-exit", h2Cold);
                    progress.Report("DSH 没能启动起来");
                    return Result(
                        DshActivationOutcome.NotStarted,
                        Restricted
                            ? $"DSH 没能启动起来：我运行在受限权限下（{SpawnDiagnostics.CurrentIntegrity()}），不能启动别的程序；请从资源管理器双击 run-whaledesk.cmd 重新启动我"
                            : "DSH 没能启动起来",
                        "H2-cold-process-exited",
                        $"{h2ProcessDetail}; process exited before a window appeared",
                        exePath,
                        processBefore.Count,
                        SnapshotProcesses().Count,
                        total);
                }

                activeLaunch = h2Cold;
                activeProcessDetail = h2ProcessDetail;
                remainingWaitMs = ColdStartWindowWaitMs - 5_000;
            }
            var coldVisible = WaitForVisibleWindow(
                TimeSpan.FromMilliseconds(remainingWaitMs),
                activeMechanism,
                out var coldHwnd,
                out var coldDetail,
                () => progress.Report("DSH 已启动，仍在加载界面…"),
                hiddenWindowNotice: HandoffOnHidden);
            if (coldVisible)
            {
                progress.Report("DSH 已经打开了");
                return Result(
                    DshActivationOutcome.Started,
                    "DSH 已经打开了",
                    activeMechanism,
                    $"{activeProcessDetail}; {coldDetail}",
                    exePath,
                    processBefore.Count,
                    SnapshotProcesses().Count,
                    total);
            }

            LogLaunchState(activeMechanism + "-after-window-timeout", activeLaunch);

            // If the process exists but no window appeared, the tray icon may now
            // exist. Try it once, then report the distinct process-vs-window failure.
            var coldTrayInvoked = InvokeTrayIconWithTimeout(TimeSpan.FromSeconds(8), out var coldTrayDetail);
            if (coldTrayInvoked)
            {
                var coldTrayVisible = WaitForVisibleWindow(
                    TimeSpan.FromMilliseconds(HiddenWindowWaitMs),
                    "H3-cold",
                    out var coldTrayHwnd,
                    out var coldTrayWindowDetail);
                if (coldTrayVisible)
                {
                    progress.Report("DSH 已经打开了");
                    return Result(
                        DshActivationOutcome.Started,
                        "DSH 已经打开了",
                        "H3-cold-tray-icon",
                    $"{activeProcessDetail}; {coldTrayDetail}; {coldTrayWindowDetail}",
                        exePath,
                        processBefore.Count,
                        SnapshotProcesses().Count,
                        total);
                }
            }

            var processAfterCold = SnapshotProcesses();
            LogSnapshot("after-cold-failure", processAfterCold, SnapshotWindows());
            if (processAfterCold.Count == 0)
            {
                progress.Report("DSH 没能启动起来");
                return Result(
                    DshActivationOutcome.NotStarted,
                    "DSH 没能启动起来",
                activeMechanism + "-process-exited",
                $"{activeProcessDetail}; process exited before a window appeared",
                    exePath,
                    processBefore.Count,
                    processAfterCold.Count,
                    total);
            }

            progress.Report("DSH 在运行，但窗口没显示出来（可能在托盘）");
            return Result(
                DshActivationOutcome.RunningNoWindow,
                "DSH 在运行，但窗口没显示出来；请点它的托盘图标",
                activeMechanism + "-window-timeout",
                $"{activeProcessDetail}; process count after={processAfterCold.Count}; window waited {remainingWaitMs}ms; {coldTrayDetail}",
                exePath,
                processBefore.Count,
                processAfterCold.Count,
                total);
        }
        catch (Exception ex)
        {
            _log.Error("DSH PROBE unexpected failure: " + ex);
            progress.Report("DSH 唤起失败");
            return Result(
                DshActivationOutcome.Failed,
                "DSH 唤起失败，详情已写入日志",
                "exception",
                ex.ToString(),
                null,
                processBefore.Count,
                SnapshotProcesses().Count,
                total);
        }
        finally
        {
            foreach (var process in launches)
            {
                try { process.Dispose(); } catch { }
            }
        }
    }

    /// <summary>What DSH looks like right now (checked continuously, not only on a click).</summary>
    public enum DshPresence
    {
        Unknown,
        NotRunning,
        /// <summary>Processes exist but no window yet (booting) or the window is being torn
        /// down while DSH exits. Transient: never announced, so a full quit is not mistaken
        /// for "hidden to tray".</summary>
        Starting,
        Hidden,
        Visible,
    }

    /// <summary>
    /// Cheap presence probe: process count + top-level window visibility. Runs every couple of
    /// seconds so the pet's messages follow reality (the user reported a stale "DSH 已经打开了"
    /// after DSH had been closed).
    /// </summary>
    public static DshPresence Probe(Log log, out string detail)
    {
        // Window first: when a window is found the (more expensive) process snapshot is not
        // needed, which is what lets this run twice a second.
        var hwnd = Win32.FindMainWindow(DshTitleFragment, out var hidden, out var windowDetail);
        if (hwnd != IntPtr.Zero && !hidden && Win32.IsWindowVisible(hwnd))
        {
            detail = $"window visible {windowDetail}";
            return DshPresence.Visible;
        }
        if (hwnd != IntPtr.Zero)
        {
            detail = $"window hidden {windowDetail}";
            return DshPresence.Hidden;
        }

        int processes;
        try { processes = Process.GetProcessesByName(DshProcessName).Length; }
        catch (Exception ex) { detail = "process query failed: " + ex.Message; return DshPresence.Unknown; }
        if (processes == 0)
        {
            detail = "no process, no window";
            return DshPresence.NotRunning;
        }
        detail = $"processes={processes} but no window (booting or exiting)";
        return DshPresence.Starting;
    }

    /// <summary>
    /// Ask DSH to close its window: that runs DSH's own handler, which hides the window to the
    /// tray (main.js: on Windows close = mainWindow.hide()). Reliable, because DSH does the
    /// hiding itself — forcing SW_HIDE from outside gets undone.
    /// </summary>
    public static bool HideWindow(Log log, out string detail)
    {
        var hwnd = Win32.FindMainWindow(DshTitleFragment, out var hidden, out var windowDetail);
        if (hwnd == IntPtr.Zero)
        {
            detail = "no DSH window found";
            log.Warn("hide-DSH: " + detail);
            return false;
        }
        if (hidden || !Win32.IsWindowVisible(hwnd))
        {
            detail = "already hidden";
            log.Info("hide-DSH: window is already hidden");
            return true;
        }

        var posted = Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(700);
        var nowHidden = !Win32.IsWindowVisible(hwnd);
        detail = $"posted WM_CLOSE={posted} hiddenNow={nowHidden} ({windowDetail})";
        log.Info("hide-DSH: " + detail);
        return nowHidden;
    }

    private DshActivationResult Result(

        DshActivationOutcome outcome,
        string userMessage,
        string mechanism,
        string detail,
        string? exePath,
        int processCountBefore,
        int processCountAfter,
        Stopwatch total)
    {
        total.Stop();
        _log.Info(
            $"DSH PROBE result outcome={outcome} mechanism={mechanism} elapsedMs={total.ElapsedMilliseconds} " +
            $"processes={processCountBefore}->{processCountAfter} exe='{exePath ?? "none"}' detail={detail}");
        return new DshActivationResult(
            outcome,
            userMessage,
            mechanism,
            detail,
            exePath,
            processCountBefore,
            processCountAfter,
            total.ElapsedMilliseconds);
    }

    private bool TryFindVisibleMainWindow(out IntPtr hwnd, out string detail)
    {
        hwnd = Win32.FindMainWindow(DshTitleFragment, out var wasHidden, out detail);
        if (hwnd == IntPtr.Zero) return false;
        if (!Win32.IsWindowVisible(hwnd))
        {
            detail += $" hiddenState={wasHidden}";
            return false;
        }

        detail += $" minimized={Win32.IsIconic(hwnd)}";
        return true;
    }

    private bool WaitForVisibleWindow(
        TimeSpan timeout,
        string phase,
        out IntPtr hwnd,
        out string detail,
        Action? longWaitNotice = null,
        Action? hiddenWindowNotice = null)
    {
        var started = Stopwatch.StartNew();
        var lastProgressNotice = 0L;
        hwnd = IntPtr.Zero;
        detail = "not found";

        while (started.Elapsed < timeout)
        {
            var candidate = Win32.FindMainWindow(DshTitleFragment, out var wasHidden, out var candidateDetail);
            if (candidate != IntPtr.Zero && Win32.IsWindowVisible(candidate))
            {
                var steps = Win32.BringToFront(candidate);
                Thread.Sleep(180);
                hwnd = candidate;
                detail =
                    $"{candidateDetail} windowVisibleAfterMs={started.ElapsedMilliseconds} " +
                    $"bringSteps={steps} foreground={Win32.GetForegroundWindow() == candidate}";
                _log.Info($"DSH PROBE {phase} window visible after {started.ElapsedMilliseconds}ms {detail}");
                return true;
            }

            // A cold start can come up with its window HIDDEN (DSH restores its "closed to
            // tray" state). Detecting that lets the caller fire the one thing that actually
            // un-hides it: another launch, which the running instance handles as
            // second-instance -> focusOwner -> window.show().
            if (candidate != IntPtr.Zero && hiddenWindowNotice is not null) hiddenWindowNotice();

            if (longWaitNotice is not null &&
                started.ElapsedMilliseconds - lastProgressNotice >= 15_000)
            {
                lastProgressNotice = started.ElapsedMilliseconds;
                longWaitNotice();
                _log.Info(
                    $"DSH PROBE {phase} still waiting at {started.ElapsedMilliseconds}ms " +
                    $"candidate='{candidateDetail}' processCount={SnapshotProcesses().Count}");
            }

            Thread.Sleep(200);
        }

        var finalCandidate = Win32.FindMainWindow(DshTitleFragment, out _, out var finalDetail);
        detail = $"timeout={timeout.TotalMilliseconds:0}ms candidate={finalDetail}";
        _log.Warn($"DSH PROBE {phase} {detail}");
        return false;
    }

    private bool WaitForProcessAppearance(TimeSpan timeout, string phase, out string detail)
    {
        var started = Stopwatch.StartNew();
        var lastCount = 0;
        while (started.Elapsed < timeout)
        {
            var processes = SnapshotProcesses();
            if (processes.Count > 0)
            {
                detail = $"process visible after {started.ElapsedMilliseconds}ms count={processes.Count} {DescribeProcesses(processes)}";
                _log.Info($"DSH PROBE {phase} {detail}");
                return true;
            }

            if (processes.Count != lastCount)
            {
                lastCount = processes.Count;
                _log.Info($"DSH PROBE {phase} processCount={lastCount}");
            }
            Thread.Sleep(100);
        }

        var finalCount = SnapshotProcesses().Count;
        detail = $"process not seen after {timeout.TotalMilliseconds:0}ms finalCount={finalCount}";
        _log.Warn($"DSH PROBE {phase} {detail}");
        return false;
    }

    /// <summary>
    /// A restricted (Low integrity) process must not even try: the child would die with
    /// 0x80000003 (Electron) or 0xc0000142 (Explorer), which is exactly the failure users
    /// reported. Bring-to-front (H0) and the tray icon (H3) do not need process creation and
    /// keep working.
    /// </summary>
    private bool _restrictedChecked;
    private bool _restricted;

    private bool Restricted
    {
        get
        {
            if (!_restrictedChecked)
            {
                _restricted = SpawnDiagnostics.IsRestricted();
                _restrictedChecked = true;
                if (_restricted)
                    _log.Warn($"DSH PROBE restricted token: integrity={SpawnDiagnostics.CurrentIntegrity()} " +
                              "- process creation is skipped; tell the user to relaunch the pet from Explorer");
            }
            return _restricted;
        }
    }
    private LaunchObservation TryLaunch(string exePath, string? arguments, string phase)
    {
        var workingDirectory = Path.GetDirectoryName(exePath) ?? "";
        _log.Info(
            $"DSH PROBE {phase} launch exe='{exePath}' args='{arguments ?? ""}' " +
            $"workingDirectory='{workingDirectory}'");
        if (Restricted)
            return new LaunchObservation(phase, null, null, null,
                $"skipped: this process runs with a restricted (Low integrity) token ({SpawnDiagnostics.CurrentIntegrity()}), " +
                "so a normal child cannot start - relaunch the pet from File Explorer");

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments ?? "",
                WorkingDirectory = workingDirectory,
                // CreateProcess directly. ShellExecute from the pet's GUI process
                // produced an Electron bootstrap that exited with STATUS_BREAKPOINT
                // before it could hand the request to the running DSH instance.
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            var process = Process.Start(startInfo);
            if (process is null)
            {
                _log.Warn($"DSH PROBE {phase} launch returned no process");
                return new LaunchObservation(phase, null, null, null, "Process.Start returned null");
            }

            var startTime = SafeStartTime(process);
            _log.Info($"DSH PROBE {phase} launch returned pid={process.Id} startTime='{startTime ?? "unknown"}'");

            // A restricted parent (sandbox / job object / debugger-like environment) can make
            // an Electron child die at startup with STATUS_BREAKPOINT (0x80000003). That was
            // observed in a Codex sub-process sandbox while the same launch returned exit 0
            // from a plain PowerShell parent, so it is an environment property, not the app's.
            // Handing the create to Explorer escapes the parent's restrictions.
            var earlyExit = TryDetectEarlyFailure(process, exePath, phase, out var shellNote);
            if (earlyExit is not null)
                return new LaunchObservation(phase, process, process.Id, startTime, shellNote);

            return new LaunchObservation(phase, process, process.Id, startTime, null);
        }
        catch (Exception ex)
        {
            _log.Error($"DSH PROBE {phase} launch failed: {ex}");
            return new LaunchObservation(phase, null, null, null, ex.Message);
        }
    }

    /// <summary>
    /// If the freshly started process dies within a moment with a non-zero exit code, the
    /// parent environment blocked it (STATUS_BREAKPOINT is the usual Electron symptom).
    /// Retry once through the shell (Explorer), which creates the process outside this
    /// parent's sandbox/job. Returns null when the launch looks healthy: still running
    /// (cold start) or exited 0 (the running instance took over via its single-instance lock).
    /// </summary>
    private string? TryDetectEarlyFailure(Process process, string exePath, string phase, out string? note)
    {
        note = null;
        try
        {
            if (!process.WaitForExit(1_500)) return null;   // alive: cold start in progress
            if (process.ExitCode == 0) return null;         // clean hand-off to the running instance

            var hex = $"0x{unchecked((uint)process.ExitCode):X8}";
            _log.Warn($"DSH PROBE {phase} direct launch died early exitCode={process.ExitCode} ({hex}); " +
                      "retrying through the shell (Explorer) to escape this parent's restrictions");

            var shellInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + exePath + "\"",
                UseShellExecute = false,
            };
            var shell = Process.Start(shellInfo);
            Thread.Sleep(600);
            var shellAlive = shell is not null && !SafeHasExited(shell);
            note = $"direct-exit={process.ExitCode}({hex}) shellRetry={(shell is null ? "started-null" : "started")} " +
                   $"shellAlive={shellAlive}";
            _log.Info($"DSH PROBE {phase} shell retry {note}");
            shell?.Dispose();
            return note;
        }
        catch (Exception ex)
        {
            note = "early-failure check failed: " + ex.Message;
            _log.Warn($"DSH PROBE {phase} {note}");
            return null;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; } catch { return false; }
    }

    private void LogLaunchState(string phase, LaunchObservation? launch)
    {
        if (launch?.Process is null)
        {
            _log.Warn($"DSH PROBE {phase} launch state unavailable: {launch?.Error ?? "no process"}");
            return;
        }

        try
        {
            var exited = launch.Process.HasExited;
            var exitCode = exited ? launch.Process.ExitCode.ToString() : "running";
            _log.Info(
                $"DSH PROBE {phase} launched-process pid={launch.Pid} startTime='{launch.StartTime ?? "unknown"}' " +
                $"hasExited={exited} exitCode={exitCode}");
        }
        catch (Exception ex)
        {
            _log.Warn($"DSH PROBE {phase} could not read launched-process state: {ex.Message}");
        }
    }

    private string? LocateExe()
    {
        if (!string.IsNullOrWhiteSpace(_rememberedPath) && File.Exists(_rememberedPath))
        {
            _log.Info($"DSH PROBE locator source=remembered path='{_rememberedPath}'");
            return _rememberedPath;
        }

        if (File.Exists(KnownExePath))
        {
            _log.Info($"DSH PROBE locator source=known path='{KnownExePath}'");
            return KnownExePath;
        }

        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "DeepSeek Harness",
            "DeepSeek Harness.exe");
        if (File.Exists(localPrograms))
        {
            _log.Info($"DSH PROBE locator source=local-programs path='{localPrograms}'");
            return localPrograms;
        }

        var fromUninstall = FindFromUninstallRegistry();
        if (!string.IsNullOrWhiteSpace(fromUninstall) && File.Exists(fromUninstall))
        {
            _log.Info($"DSH PROBE locator source=uninstall-registry path='{fromUninstall}'");
            return fromUninstall;
        }

        foreach (var shortcut in ShortcutCandidates())
        {
            if (!File.Exists(shortcut)) continue;
            var target = ResolveShortcut(shortcut);
            if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
            {
                _log.Info($"DSH PROBE locator source=start-menu-shortcut shortcut='{shortcut}' target='{target}'");
                return target;
            }
            _log.Warn($"DSH PROBE locator shortcut='{shortcut}' target='{target ?? "unresolved"}'");
        }

        return null;
    }

    private string? FindFromUninstallRegistry()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var subKey = uninstall.OpenSubKey(subKeyName);
                    if (subKey is null) continue;
                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var displayIcon = ExtractExecutablePath(subKey.GetValue("DisplayIcon") as string);
                    if (displayIcon is not null && File.Exists(displayIcon)) return displayIcon;

                    var installLocation = subKey.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        var candidate = Path.Combine(installLocation.Trim().Trim('"'), "DeepSeek Harness.exe");
                        if (File.Exists(candidate)) return candidate;
                    }

                    var uninstallString = subKey.GetValue("UninstallString") as string;
                    var uninstallExe = ExtractExecutablePath(uninstallString);
                    if (uninstallExe is not null)
                    {
                        var directory = Path.GetDirectoryName(uninstallExe);
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            var candidate = Path.Combine(directory, "DeepSeek Harness.exe");
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"DSH PROBE locator registry subkey '{subKeyName}' failed: {ex.Message}");
                }
            }
        }

        return null;
    }

    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith('"'))
        {
            var endQuote = text.IndexOf('"', 1);
            if (endQuote > 1) return text[1..endQuote];
        }

        var comma = text.IndexOf(',');
        if (comma > 0) text = text[..comma];
        return text.Trim().Trim('"');
    }

    private static IEnumerable<string> ShortcutCandidates()
    {
        var names = new[]
        {
            "DeepSeek Harness.lnk",
            "DeepSeek Harness 客户端.lnk",
        };
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        };
        foreach (var root in roots)
        {
            foreach (var name in names) yield return Path.Combine(root, name);
        }
    }

    private static string? ResolveShortcut(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });
            if (shortcut is null) return null;
            return shortcut.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private bool InvokeTrayIconWithTimeout(TimeSpan timeout, out string detail)
    {
        var result = false;
        var resultDetail = "tray invocation timed out";
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WhaleDesk DSH tray UIA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(timeout) || dispatcher is null)
        {
            detail = $"tray STA dispatcher did not start within {timeout.TotalSeconds:0}s";
            _log.Warn("DSH PROBE H3 " + detail);
            return false;
        }

        try
        {
            var operation = dispatcher.InvokeAsync(() =>
            {
                try { result = TryInvokeTrayIcon(out resultDetail); }
                catch (Exception ex) { resultDetail = "STA tray dispatcher failed: " + ex.Message; }
            });
            if (!operation.Task.Wait(timeout))
            {
                detail = $"tray invocation did not finish within {timeout.TotalSeconds:0}s";
                _log.Warn("DSH PROBE H3 " + detail);
                return false;
            }

            detail = resultDetail;
            return result;
        }
        finally
        {
            try { dispatcher.InvokeShutdown(); } catch { }
            thread.Join(1_000);
        }
    }

    private bool TryInvokeTrayIcon(out string detail)
    {
        try
        {
            var desktop = AutomationElement.RootElement;
            if (TryFindTrayButtonByName(out var namedButton, out var namedDetail))
            {
                _log.Info($"DSH PROBE H3 direct named tray button found {namedDetail}");
                var clicked = InvokeAutomationElement(namedButton!, out var clickDetail);
                detail = $"direct-name {namedDetail}; {clickDetail}";
                _log.Info($"DSH PROBE H3 direct-name result={clicked} detail={detail}");
                return clicked;
            }

            var taskbars = new List<AutomationElement>();
            foreach (var className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                var found = desktop.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, className));
                foreach (AutomationElement taskbar in found) taskbars.Add(taskbar);
            }

            if (taskbars.Count == 0)
            {
                detail = "no Shell_TrayWnd or Shell_SecondaryTrayWnd found";
                _log.Warn("DSH PROBE H3 " + detail);
                return false;
            }

            var directMisses = new List<string>();
            foreach (var taskbar in taskbars)
            {
                var className = taskbar.Current.ClassName;
                if (TryFindTrayButton(taskbar, out var direct, out var directDetail))
                {
                    _log.Info($"DSH PROBE H3 direct tray button found taskbar='{className}' {directDetail}");
                    var clicked = InvokeAutomationElement(direct!, out var clickDetail);
                    detail = $"direct taskbar='{className}' {directDetail}; {clickDetail}";
                    _log.Info($"DSH PROBE H3 direct result={clicked} detail={detail}");
                    return clicked;
                }

                directMisses.Add($"taskbar='{className}' {directDetail}");
            }

            _log.Info($"DSH PROBE H3 direct tray button not found: {string.Join("; ", directMisses)}");
            var overflowMisses = new List<string>();
            foreach (var taskbar in taskbars)
            {
                var className = taskbar.Current.ClassName;
                if (!TryFindOverflowButton(taskbar, out var overflow, out var overflowDetail))
                {
                    overflowMisses.Add($"taskbar='{className}' {overflowDetail}");
                    continue;
                }

                _log.Info($"DSH PROBE H3 overflow button found taskbar='{className}' {overflowDetail}");
                var overflowInvoked = InvokeAutomationElement(overflow!, out var overflowClickDetail);
                _log.Info($"DSH PROBE H3 overflow invoked={overflowInvoked} detail={overflowClickDetail}");
                if (!overflowInvoked) continue;

                Thread.Sleep(700);
                foreach (var searchRoot in OverflowSearchRoots(desktop, taskbars))
                {
                    if (!TryFindTrayButton(searchRoot, out var overflowTray, out var overflowTrayDetail)) continue;
                    var clicked = InvokeAutomationElement(overflowTray!, out var clickDetail);
                    detail = $"overflow {overflowDetail}; tray {overflowTrayDetail}; {clickDetail}";
                    _log.Info($"DSH PROBE H3 overflow-tray result={clicked} detail={detail}");
                    return clicked;
                }

                detail = $"overflow {overflowDetail}; DSH icon not found in opened flyout";
                _log.Warn("DSH PROBE H3 " + detail);
                return false;
            }

            detail = $"{string.Join("; ", directMisses)}; overflow buttons not found: {string.Join("; ", overflowMisses)}";
            _log.Warn("DSH PROBE H3 " + detail);
            return false;
        }
        catch (Exception ex)
        {
            detail = "UIAutomation exception: " + ex.Message;
            _log.Warn("DSH PROBE H3 " + detail);
            return false;
        }
    }

    private static bool TryFindTrayButtonByName(out AutomationElement? button, out string detail)
    {
        button = null;
        try
        {
            var condition = new PropertyCondition(AutomationElement.NameProperty, DshTitleFragment);
            button = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition);
            if (button is null)
            {
                detail = "name='DeepSeek Harness' not found";
                return false;
            }

            detail =
                $"name='{button.Current.Name}' automationId='{button.Current.AutomationId}' " +
                $"controlType='{button.Current.ControlType.ProgrammaticName}'";
            return true;
        }
        catch (Exception ex)
        {
            detail = "named button search failed: " + ex.Message;
            return false;
        }
    }

    private static IEnumerable<AutomationElement> OverflowSearchRoots(
        AutomationElement desktop,
        IReadOnlyCollection<AutomationElement> taskbars)
    {
        var overflowWindow = desktop.FindAll(
            TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"));
        foreach (AutomationElement window in overflowWindow) yield return window;
        foreach (var taskbar in taskbars) yield return taskbar;
    }

    private static bool TryFindTrayButton(
        AutomationElement root,
        out AutomationElement? button,
        out string detail)
    {
        button = null;
        try
        {
            var buttons = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var candidates = new List<string>();
            foreach (AutomationElement candidate in buttons)
            {
                string name;
                string automationId;
                try
                {
                    name = candidate.Current.Name ?? "";
                    automationId = candidate.Current.AutomationId ?? "";
                }
                catch
                {
                    continue;
                }

                if (name.Contains(DshTitleFragment, StringComparison.OrdinalIgnoreCase))
                {
                    button = candidate;
                    detail = $"name='{name}' automationId='{automationId}'";
                    return true;
                }

                if (candidates.Count < 8 && (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(automationId)))
                    candidates.Add($"{name}/{automationId}");
            }

            detail = $"buttons={buttons.Count} sample=[{string.Join(", ", candidates)}]";
            return false;
        }
        catch (Exception ex)
        {
            detail = "button search failed: " + ex.Message;
            return false;
        }
    }

    private static bool TryFindOverflowButton(
        AutomationElement taskbar,
        out AutomationElement? button,
        out string detail)
    {
        button = null;
        try
        {
            var buttons = taskbar.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var candidates = new List<string>();
            foreach (AutomationElement candidate in buttons)
            {
                string name;
                string automationId;
                try
                {
                    name = candidate.Current.Name ?? "";
                    automationId = candidate.Current.AutomationId ?? "";
                }
                catch
                {
                    continue;
                }

                if (name.Contains("显示隐藏", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("隐藏的图标", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("hidden icon", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("show hidden", StringComparison.OrdinalIgnoreCase) ||
                    automationId.Contains("Overflow", StringComparison.OrdinalIgnoreCase) ||
                    automationId.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase))
                {
                    button = candidate;
                    detail = $"name='{name}' automationId='{automationId}'";
                    return true;
                }

                if (candidates.Count < 12 && (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(automationId)))
                    candidates.Add($"{name}/{automationId}");
            }

            detail = $"buttons={buttons.Count} sample=[{string.Join(", ", candidates)}]";
            return false;
        }
        catch (Exception ex)
        {
            detail = "overflow search failed: " + ex.Message;
            return false;
        }
    }

    private static bool InvokeAutomationElement(AutomationElement element, out string detail)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern))
            {
                ((InvokePattern)invokePattern).Invoke();
                detail = "InvokePattern.Invoke";
                return true;
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern))
            {
                ((TogglePattern)togglePattern).Toggle();
                detail = "TogglePattern.Toggle";
                return true;
            }

            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty)
            {
                detail = "no invoke pattern and empty bounding rectangle";
                return false;
            }

            Win32.GetCursorPos(out var original);
            var x = (int)Math.Round(bounds.Left + bounds.Width / 2);
            var y = (int)Math.Round(bounds.Top + bounds.Height / 2);
            Win32.SetCursorPos(x, y);
            Win32.MouseEvent(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            Win32.MouseEvent(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            Win32.SetCursorPos(original.X, original.Y);
            detail = $"mouse click at {x},{y}";
            return true;
        }
        catch (Exception ex)
        {
            detail = "invoke failed: " + ex.Message;
            return false;
        }
    }

    private static List<ProcessObservation> SnapshotProcesses()
    {
        var result = new List<ProcessObservation>();
        foreach (var process in Process.GetProcessesByName(DshProcessName))
        {
            try
            {
                var startTime = SafeStartTime(process);
                var state = "unknown";
                try { state = process.HasExited ? "exited" : "running"; }
                catch { }
                result.Add(new ProcessObservation(process.Id, startTime ?? "unknown", state));
            }
            finally
            {
                process.Dispose();
            }
        }

        return result.OrderBy(p => p.Pid).ToList();
    }

    private static string? SafeStartTime(Process process)
    {
        try { return process.StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff"); }
        catch { return null; }
    }

    private static string DescribeProcesses(IEnumerable<ProcessObservation> processes) =>
        "[" + string.Join(", ", processes.Select(p => $"pid={p.Pid} start='{p.StartTime}' state={p.State}")) + "]";

    private List<WindowObservation> SnapshotWindows()
    {
        var result = new List<WindowObservation>();
        EnumWindowsProc callback = (hwnd, _) =>
        {
            var titleBuffer = new StringBuilder(512);
            var classBuffer = new StringBuilder(256);
            GetWindowTextW(hwnd, titleBuffer, titleBuffer.Capacity);
            GetClassNameW(hwnd, classBuffer, classBuffer.Capacity);
            var title = titleBuffer.ToString();
            if (title.Contains(DshTitleFragment, StringComparison.OrdinalIgnoreCase))
            {
                GetWindowThreadProcessId(hwnd, out var processId);
                result.Add(new WindowObservation(
                    hwnd,
                    classBuffer.ToString(),
                    title,
                    Win32.IsWindowVisible(hwnd),
                    Win32.IsIconic(hwnd),
                    processId));
            }
            return true;
        };
        try { EnumWindows(callback, IntPtr.Zero); } catch { }
        GC.KeepAlive(callback);
        return result
            .OrderByDescending(w => w.ClassName == DshMainWindowClass)
            .ThenByDescending(w => w.Visible)
            .ToList();
    }

    private void LogSnapshot(
        string phase,
        IReadOnlyCollection<ProcessObservation> processes,
        IReadOnlyCollection<WindowObservation> windows)
    {
        _log.Info($"DSH PROBE snapshot {phase} processes={processes.Count} {DescribeProcesses(processes)}");
        _log.Info(
            $"DSH PROBE snapshot {phase} windows={windows.Count} [" +
            string.Join(", ", windows.Select(w =>
                $"hwnd=0x{w.Hwnd.ToInt64():X} pid={w.Pid} class='{w.ClassName}' visible={w.Visible} minimized={w.Minimized} title='{w.Title}'")) +
            "]");
    }

    private sealed record ProcessObservation(int Pid, string StartTime, string State);
    private sealed record WindowObservation(
        IntPtr Hwnd,
        string ClassName,
        string Title,
        bool Visible,
        bool Minimized,
        uint Pid);
    private sealed record LaunchObservation(
        string Phase,
        Process? Process,
        int? Pid,
        string? StartTime,
        string? Error);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "EnumWindows")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
