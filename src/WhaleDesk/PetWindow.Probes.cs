using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WhaleDesk.Cleanup;
using WhaleDesk.Interaction;
using WhaleDesk.Monitoring;
using WhaleDesk.Performance;
using WhaleDesk.Recovery;

namespace WhaleDesk;

/// <summary>
/// Probe / diagnostic entry points, split out of PetWindow so the main file stays navigable.
/// Behaviour is unchanged: everything here is only reached through the --probe-* switches.
/// </summary>
internal sealed partial class PetWindow
{

    private async Task RunPluginProbeAsync()
    {
        await Task.Delay(600);
        try
        {
            var plugins = Monitoring.PluginHelper.Scan(Cleanup.CleanupCatalog.DshHome);
            var missing = Monitoring.PluginHelper.Missing(plugins);
            _log.Info($"PLUGIN PROBE installed={plugins.Count} missing={missing.Count}");
            foreach (var plugin in plugins)
                _log.Info($"PLUGIN PROBE {(plugin.IsDependency ? "[依赖]" : "[其它]")} {plugin.Name} {plugin.Version}");
            foreach (var m in missing) _log.Warn($"PLUGIN PROBE MISSING {m.Name} — {m.Why}");
              ShowPluginHelper();
              _log.Info("PLUGIN PROBE window held open for the screenshot");
              await Task.Delay(20000);
        }
        catch (Exception ex)
        {
            _log.Warn("PLUGIN PROBE failed: " + ex.Message);
        }
    }

    /// <summary>
    /// --probe-hotplug: end-to-end recovery test.
    ///
    /// Phases: (1) the configured DSH base is dead -> the pet must detect offline;
    /// (2) a stub server appears -> the pet must reconnect on its own;
    /// (3) the stub dies -> offline again;
    /// (4) the stub answers with unknown/partial fields (a plugin update changing the shape) ->
    ///     the pet must keep working instead of crashing.
    /// </summary>
    private async Task RunHotplugProbeAsync()
    {
        await Task.Delay(500);
        var baseUrl = _opts.DshBase ?? PetStateClient.DefaultBase;
        _log.Info($"HOTPLUG PROBE base={baseUrl} clientBase={_state.BaseUrl}");
        var port = 19487;
        try { port = new Uri(baseUrl).Port; } catch { }
        using var stub = new Monitoring.StubPetServer(port) { Log = m => _log.Diag("hotplug", m) };

        var offline = await WaitForAsync(() => _state.ConsecutiveFailures >= 3, 9000);
        _log.Info($"HOTPLUG PROBE phase1 offline={offline} failures={_state.ConsecutiveFailures} err={_state.LastError}");

        stub.Start("{\"animation\":\"idle\",\"bubble\":\"stub online\",\"phase\":\"idle\",\"sessionActive\":true,\"sessions\":[{\"sessionId\":\"stub\",\"phase\":\"idle\"}]}");
        _log.Info($"HOTPLUG PROBE stub listening={stub.Listening} port={stub.Port}");
        try
        {
            var manual = await _state.FetchAsync();
            _log.Info($"HOTPLUG PROBE manual-fetch ok={manual is not null} failures={_state.ConsecutiveFailures} err={_state.LastError}");
        }
        catch (Exception ex) { _log.Warn("HOTPLUG PROBE manual-fetch threw: " + ex.Message); }
        var recovered = false;
        for (var k = 0; k < 12; k++)
        {
            await Task.Delay(1000);
            _log.Info($"HOTPLUG PROBE wait k={k} failures={_state.ConsecutiveFailures} err={_state.LastError}");
            if (_state.ConsecutiveFailures == 0) { recovered = true; break; }
        }
        _log.Info($"HOTPLUG PROBE stub hits={stub.Hits} errors={stub.Errors}");
        await Task.Delay(400);
        _log.Info($"HOTPLUG PROBE phase2 recovered={recovered} sessionActive={_dshSessionActive} failures={_state.ConsecutiveFailures}");

        stub.Stop();
        var offlineAgain = await WaitForAsync(() => _state.ConsecutiveFailures >= 3, 9000);
        _log.Info($"HOTPLUG PROBE phase3 offline-again={offlineAgain} failures={_state.ConsecutiveFailures}");

        stub.Start("{\"unknownField\":123,\"sessions\":[{\"phase\":\"tool\",\"bubble\":\"partial\"}],\"bubble\":\"partial state\"}");
        var tolerant = await WaitForAsync(() => _state.ConsecutiveFailures == 0, 25000);
        await Task.Delay(500);
        _log.Info($"HOTPLUG PROBE phase4 tolerant={tolerant} failures={_state.ConsecutiveFailures} summary={_stateSummary}");
        stub.Stop();

        var verdict = offline && recovered && offlineAgain && tolerant;
        _log.Info($"HOTPLUG PROBE verdict={(verdict ? "PASS" : "FAIL")} offline={offline} recovered={recovered} offlineAgain={offlineAgain} tolerant={tolerant}");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(120);
        }
        return condition();
    }

    /// <summary>--probe-extras: log the follow-cursor and focus-quiet policies (no windows moved).</summary>
    private async Task RunExtrasProbeAsync()
    {
        await Task.Delay(500);
        var far = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 60,
            100, 100, 300, 300, 1500, 700, 0, 0, 1920, 1030);
        var near = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 60,
            100, 100, 300, 300, 230, 230, 0, 0, 1920, 1030);
        var busy = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 3,
            100, 100, 300, 300, 1500, 700, 0, 0, 1920, 1030);
        var edge = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 60,
            0, 400, 300, 300, 5, 400, 0, 0, 1920, 1030);
        _log.Info($"EXTRAS PROBE follow far={far.Move}({far.Dx},{far.Dy})/{far.Why} " +
                  $"near={near.Move}/{near.Why} busy={busy.Move}/{busy.Why} edge={edge.Move}/{edge.Why}");

        var x = 100;
        var y = 100;
        var steps = 0;
        for (var i = 0; i < 6; i++)
        {
            var d = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 60, x, y, 300, 300, 1500, 700, 0, 0, 1920, 1030);
            if (!d.Move) break;
            x += d.Dx;
            y += d.Dy;
            steps++;
        }
        _log.Info($"EXTRAS PROBE simulated steps={steps} end=({x},{y})");

        var quietIdle = !Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.IdleChatter, true);
        var quietEgg = !Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.EasterEgg, true);
        var allowReminder = Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.Reminder, true);
        var allowWhenNotFocus = Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.IdleChatter, false);
        _log.Info($"EXTRAS PROBE focus idle-suppressed={quietIdle} egg-suppressed={quietEgg} " +
                  $"reminder-allowed={allowReminder} normal-chatter={allowWhenNotFocus} " +
                  $"strip=\"{Interaction.FocusPolicy.Decorate("tool · 1m", true)}\"");
        _log.Info($"EXTRAS PROBE verdict={(far.Move && !near.Move && !busy.Move && !edge.Move && quietIdle && quietEgg && allowReminder && allowWhenNotFocus && steps == 6 ? "PASS" : "FAIL")}");
    }

    /// <summary>
    /// Discover the running DSH loopback base URL. DSH 0.2.1-rc.2 was observed on a dynamic port,
    /// so a hardcoded 19387 made the pet permanently offline.
    /// </summary>
    private async Task DiscoverDshEndpointAsync(bool initial = false)
    {
        var now = Environment.TickCount64;
        if (!initial && now - _lastDiscoveryMs < 10_000) return;
        _lastDiscoveryMs = now;
        try
        {
            var found = await DshEndpointDiscovery.FindAsync(_dshBase).ConfigureAwait(true);
            if (found is null)
            {
                _log.Warn("DSH endpoint discovery: no candidate answered /api/pet/state");
                return;
            }
            if (string.Equals(found, _dshBase, StringComparison.OrdinalIgnoreCase))
            {
                if (initial) _log.Info($"DSH endpoint confirmed: {found}");
                return;
            }
            _dshBase = found;
            _ui.DshBaseUrl = found;
            Persist();
            _state = new PetStateClient(found);
            _log.Info($"DSH endpoint discovered: {found} (remembered)");
        }
        catch (Exception ex)
        {
            _log.Warn("DSH endpoint discovery failed: " + ex.Message);
        }
    }

    /// <summary>--probe-follow: actually drive the follow loop and prove the window really moves.</summary>
    private async Task RunFollowProbeAsync()
    {
        await Task.Delay(600);
        var wasFollow = _cursorFollow;
        var wasSpeed = _followSpeed;
        _cursorFollow = true;
        _followSpeed = 320;
        _idleOverrideSeconds = 60;   // pretend the user is away
        _lastFollowTickMs = 0;
        try
        {
            var (sx, sy, sw, sh) = PhysicalRect();
            var (px, py, _, _) = PhysicalRect();
            for (var i = 0; i < 20; i++)
            {
                UpdateCursorFollow();
                await Task.Delay(60);
            }
            var (ex, ey, _, _) = PhysicalRect();
            _log.Info($"FOLLOW PROBE start=({sx},{sy}) end=({ex},{ey}) delta=({ex - sx},{ey - sy}) speed={_followSpeed} cursor=({px},{py})");
            var moved = Math.Abs(ex - sx) + Math.Abs(ey - sy) > 20;
            _log.Info($"FOLLOW PROBE verdict={(moved ? "PASS" : "FAIL")} moved={moved}");
        }
        finally
        {
            _cursorFollow = wasFollow;
            _followSpeed = wasSpeed;
            _idleOverrideSeconds = -1;
        }
    }

    private async Task RunDshApiProbeAsync()
    {
        await Task.Delay(400);
        var endpoints = new (string Path, string Feature, bool Get)[]
        {
            ("/api/pet/state", "会话状态/阶段/气泡（长任务提醒、状态浮标）", true),
            ("/api/pet/pets", "宠物规格（pet-spec 兜底）", true),
            ("/api/plugin-manager/plugins", "插件管理器（插件列表/安装）", true),
            ("/api/skin-center/skins", "皮肤中心（官方素材）", true),
        };
        var discovered = await DshEndpointDiscovery.FindAsync(_dshBase).ConfigureAwait(true);
        var probeBase = discovered ?? _state.BaseUrl;
        foreach (var (path, feature, isGet) in endpoints)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = isGet
                    ? await client.GetAsync(probeBase + path).ConfigureAwait(true)
                    : null;
                if (response is null) continue;
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                var verdict = response.IsSuccessStatusCode
                    ? (body.Length > 0 ? "OK" : "EMPTY")
                    : ((int)response.StatusCode == 401 ? "AUTH (needs login)" : "MISSING");
                _log.Info($"DSH API PROBE {path,-32} {verdict,-14} {body.Length,7} bytes  -> {feature}");
            }
            catch (Exception ex)
            {
                _log.Warn($"DSH API PROBE {path,-32} DOWN          {ex.Message}  -> {feature}");
            }
        }
        // The usage ledger is a file, not an endpoint; report it too.
        var ledger = Monitoring.UsageStats.LedgerPath;
        _log.Info($"DSH API PROBE usage-ledger {ledger} exists={System.IO.File.Exists(ledger)} -> token 统计");
    }

    /// <summary>
    /// --probe-menu-monitor N: move the pet onto monitor N, open the menu and verify the menu's
    /// PHYSICAL rectangle really lands inside that monitor's work area. This is the regression
    /// test for "menu invisible on monitor 2" (mixed-DPI coordinate bug).
    /// </summary>
    private async Task RunMenuMonitorProbeAsync()
    {
        await Task.Delay(700);
        try
        {
            PickMonitor(_opts.ProbeMenuMonitorIndex);
        }
        catch (Exception ex)
        {
            _log.Warn("MENU2 PROBE move failed: " + ex.Message);
        }
        await Task.Delay(1200);
        OpenMenu();
        await Task.Delay(1200);
        try
        {
            var (px, py, pw, ph) = PhysicalRect();
            _log.Info($"MENU2 PROBE monitor={_monitor.Device} scale={_monitor.Scale:0.##} pet={px},{py} {pw}x{ph}");
            var handle = _menuWindow is null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(_menuWindow).Handle;
            if (handle != IntPtr.Zero && Win32.GetWindowRect(handle, out var r))
            {
                var inside = r.Left >= _monitor.Left && r.Top >= _monitor.Top &&
                             r.Right <= _monitor.Right && r.Bottom <= _monitor.Bottom;
                _log.Info($"MENU2 PROBE menuRect={r.Left},{r.Top}-{r.Right},{r.Bottom} " +
                          $"work={_monitor.Left},{_monitor.Top}-{_monitor.Right},{_monitor.Bottom} inside={(inside ? "YES" : "NO ← BUG")}");
            }
            else
            {
                _log.Warn("MENU2 PROBE menu window has no handle (menu did not show)");
            }
        }
        finally
        {
            _menuWindow?.Close();
        }
        await Task.Delay(300);
    }

    /// <summary>--probe-analyze: run the analyzer headlessly and log the verdicts.</summary>
    private async Task RunAnalyzeProbeAsync()
    {
        await Task.Delay(600);
        var target = _opts.ProbeAnalyzePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _keepList ??= Interaction.KeepList.Load(KeepListPath);
        var probeRunning = Performance.AppUsageIndex.Running();
        _log.Info($"ANALYZE PROBE runningEvidence={probeRunning.Count}" +
                  (probeRunning.Count > 0 ? " e.g. " + string.Join(" | ", probeRunning.Take(3).Select(r => r.ProcessName + "=>" + r.ExecutablePath)) : "（读不到进程路径，可能被权限限制）"));
        var report = Cleanup.DirectoryAnalyzer.Analyze(target, 24, 12_000,
            new Cleanup.AnalysisContext(probeRunning, _keepList));
        if (report is null) { _log.Warn($"ANALYZE PROBE: cannot read '{target}'"); return; }
        _log.Info($"ANALYZE PROBE root='{report.Root}' total={Fmt.Bytes(report.Total)} items={report.Items.Count} " +
                  $"reclaimable={Fmt.Bytes(Cleanup.DirectoryAnalyzer.Reclaimable(report))}");
        foreach (var item in report.Items)
            _log.Info($"ANALYZE PROBE  {Fmt.Bytes(item.Bytes),-10} [{Cleanup.DirectoryAnalyzer.VerdictLabel(item.Verdict)}] {item.Name} — {item.Reason}");
        _ignoreList ??= Interaction.IgnoreList.Load(IgnoreListPath);
        ShowAnalysis(report, _keepList, _ignoreList);
        _log.Info("ANALYZE PROBE window held open for the screenshot");
        await Task.Delay(20000);
    }

    /// <summary>--probe-launcher: open the palette, report its contents, close it.</summary>
    private async Task RunLauncherProbeAsync()
    {
        await Task.Delay(700);
        ShowLauncher();
        await Task.Delay(1500);
        var visible = _launcherWindow is { IsVisible: true };
        _log.Info($"LAUNCHER PROBE visible={visible} items={_launchers?.Visible().Count ?? -1} config={LauncherConfigPath}");
        if (_launchers is not null)
            _log.Info("LAUNCHER PROBE entries=" + string.Join(" | ", _launchers.Visible().Select(l => $"{l.Name}=>{l.Target}" + (string.IsNullOrEmpty(l.WorkingDirectory) ? "" : $" (cwd={l.WorkingDirectory})"))));
        _launcherWindow?.Close();
        await Task.Delay(300);
    }

    /// <summary>--probe-drop: feed a payload through the same code path a real drag uses.</summary>
    private async Task RunDropProbeAsync()
    {
        await Task.Delay(700);
        var one = _opts.ProbeDropPath;
        var two = Path.Combine(Path.GetTempPath(), "whaledesk drop test.txt");
        var files = one is null
            ? new[] { two, two + ".2" }
            : new[] { one };
        _log.Info($"DROP PROBE files=[{string.Join(" | ", files)}] dropEnabled={_dropEnabled}");
        HandleDrop(files, null);
        await Task.Delay(600);
        if (one is not null)
        {
            // File-only probe: keep the FileDrop on the clipboard so an EXTERNAL process can
            // verify it (a text drop right after would overwrite the file list).
            _log.Info("DROP PROBE file-only mode: skipping the text drop");
            return;
        }
        var textProbe = "从别处拖过来的文字";
        HandleDrop(Array.Empty<string>(), textProbe);
        await Task.Delay(300);
        _log.Info("DROP PROBE done; same-process readback follows");
        try
        {
            var backText = System.Windows.Clipboard.GetText();
            var backFiles = System.Windows.Clipboard.GetFileDropList();
            var dropList = string.Join(" | ", backFiles.Cast<string>());
            _log.Info($"DROP PROBE clipTextMatches={(backText == textProbe)} clipFiles=[{dropList}]");
        }
        catch (Exception ex)
        {
            _log.Warn("DROP PROBE clipboard read failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- probes
    private bool TryFindOpaqueScreenPoint(out Point screen, out string cellDetail, out int cellX, out int cellY)
    {        screen = default;        cellDetail = "none";        cellX = cellY = -1;
        var row = _player.Track.Row;
        var cell = _atlas.FindAlwaysOpaque(row, _player.Track.Frames);
        if (cell is null) return false;        (cellX, cellY) = cell.Value;        cellDetail = $"cell=({cellX},{cellY}) of {_atlas.CellW}x{_atlas.CellH} track={_player.TrackName}({_player.Track.Frames.Length}f row={row})";
        var dip = new Point((cellX + 0.5) / _atlas.CellW * _visual.PetWidth, (cellY + 0.5) / _atlas.CellH * _visual.PetHeight);        screen = _visual.PetImage.PointToScreen(dip);
        return true;
    }
    private async Task RunHitTestProbeAsync()
    {
        var hadCursor = Win32.GetCursorPos(out var original);
        await Task.Delay(700).ConfigureAwait(true);
        _log.Info($"PROBE foreground at t=0.7s = {Win32.DescribeWindow(Win32.GetForegroundWindow())}");
        if (TryFindOpaqueScreenPoint(out var hit, out var cellDetail, out var cellX, out var cellY))
        {
            var x = (int)Math.Round(hit.X);
            var y = (int)Math.Round(hit.Y);
            Win32.SetCursorPos(x, y);
            await Task.Delay(200).ConfigureAwait(true);
            UpdateClickThroughNow();
            var mapped = IsScreenPointOverPet(x, y);
            var maskSays = _atlas.IsOpaque(_player.Track.Row, _player.FrameColumn, (cellX + 0.5) / _atlas.CellW, (cellY + 0.5) / _atlas.CellH);
            _log.Info($"PROBE over-opaque-pixel {cellDetail} cursor=({x},{y}) maskOpaque={maskSays} mappedOverPet={mapped} (expect True) clickThrough={_clickThrough} exstyle={Win32.Describe(Win32.GetExStyle(_hwnd))}");
        }
        else _log.Warn("PROBE could not locate a pixel that is opaque in every frame of the current track");
        var (wx, wy, ww, wh) = PhysicalRect();
        if (ww > 0)
        {
            var x = wx + 3;
            var y = wy + wh - 3;
            Win32.SetCursorPos(x, y);
            await Task.Delay(200).ConfigureAwait(true);
            UpdateClickThroughNow();
            var mapped = IsScreenPointOverPet(x, y);
            _log.Info($"PROBE over-empty-area cursor=({x},{y}) mappedOverPet={mapped} (expect False) clickThrough={_clickThrough} (expect True) exstyle={Win32.Describe(Win32.GetExStyle(_hwnd))}");
        }
        if (hadCursor) Win32.SetCursorPos(original.X, original.Y);
        _log.Info($"PROBE foreground at end = {Win32.DescribeWindow(Win32.GetForegroundWindow())}");
        _log.Info("PROBE done");
    }
    private async Task RunInteractProbeAsync()
    {
        var hadCursor = Win32.GetCursorPos(out var original);
        await Task.Delay(900).ConfigureAwait(true);
        if (!TryFindOpaqueScreenPoint(out var hit, out var detail, out _, out _))
        {
            _log.Warn("INTERACT PROBE: no persistently opaque pixel found");
            return;
        }
        var x = (int)Math.Round(hit.X);
        var y = (int)Math.Round(hit.Y);
        _log.Info($"INTERACT PROBE target {detail} cursor=({x},{y})");
        Win32.SetCursorPos(x, y);
        await Task.Delay(250).ConfigureAwait(true);
        UpdateClickThroughNow();
        if (!_clickThrough && IsScreenPointOverPet(x, y))
        {
            Win32.MouseEvent(Win32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(60).ConfigureAwait(true);
            Win32.MouseEvent(Win32.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(600).ConfigureAwait(true);
            _log.Info($"INTERACT PROBE (real input) click sent; bubble='{_visual.BubbleText}'");
        }
        else
        {
            _log.Warn($"INTERACT PROBE (real input) skipped: clickThrough={_clickThrough} mapped={IsScreenPointOverPet(x, y)} - the physical cursor is not under our control while the user is working");
        }        BeginDragAt(x, y);
        await CompleteDragAsync();
        await Task.Delay(500).ConfigureAwait(true);
        _log.Info($"INTERACT PROBE drive-click: petting path ran, bubble='{_visual.BubbleText}'");
        var anchorBefore = _ui.Anchor;
        var (bx, by, _, _) = PhysicalRect();        BeginDragAt(x, y);
        for (var step = 1; step <= 22; step++)
        {
            UpdateDragAt(x - step * 45, y - step * 14);
            await Task.Delay(15).ConfigureAwait(true);
        }
        await CompleteDragAsync();
        await Task.Delay(250).ConfigureAwait(true);
        var (ax, ay, aw, ah) = PhysicalRect();
        var expectedLeft = _monitor.Left + (int)Math.Round(_opts.MarginX * _monitor.Scale);
        _log.Info($"INTERACT PROBE drive-drag: rect {bx},{by} -> {ax},{ay} movedPx={Math.Abs(ax - bx)} anchorBefore={anchorBefore} anchorAfter={_ui.Anchor} expectedLeftPx={expectedLeft} actualLeftPx={ax} snapped={Math.Abs(ax - expectedLeft) < 3} monitor={_monitor.Device}");        ResetPosition();
        await Task.Delay(250).ConfigureAwait(true);
        var (rx2, ry2, _, _) = PhysicalRect();
        _log.Info($"INTERACT PROBE reset: rect {rx2},{ry2} anchor={_ui.Anchor} monitor={_monitor.Device}");
        if (hadCursor) Win32.SetCursorPos(original.X, original.Y);
        _log.Info("INTERACT PROBE done");
    }
    private async Task RunMenuProbeAsync()
    {
        await Task.Delay(1200).ConfigureAwait(true);

        OpenMenu();
        await Task.Delay(800).ConfigureAwait(true);
        _log.Info($"MENU PROBE open={_menuOpen} menuHeight={_menuHeight:0} windowHeight={Height:0} rect={Rect()}");
        await Task.Delay(3500).ConfigureAwait(true);
        CloseMenu();

        _log.Info($"MENU PROBE closed={!_menuOpen} windowHeight={Height:0} rect={Rect()}");
    }
    private async Task RunMoveLeftProbeAsync()
    {
        await Task.Delay(900).ConfigureAwait(true);
        if (!TryFindOpaqueScreenPoint(out var hit, out var detail, out _, out _))
        {
            _log.Warn("MOVE PROBE: no persistently opaque pixel found");
            return;
        }
        var x = (int)Math.Round(hit.X);
        var y = (int)Math.Round(hit.Y);
        var (bx, by, _, _) = PhysicalRect();        BeginDragAt(x, y);
        for (var step = 1; step <= 22; step++)
        {
            UpdateDragAt(x - step * 45, y - step * 14);
            await Task.Delay(15).ConfigureAwait(true);
        }
        await CompleteDragAsync();
        await Task.Delay(400).ConfigureAwait(true);
        var (ax, ay, _, _) = PhysicalRect();
        _log.Info($"MOVE PROBE {detail} rect {bx},{by} -> {ax},{ay} anchor={_ui.Anchor} monitor={_monitor.Device}");
        Persist();
    }
    /// <summary>
    /// Reproduces the reported bug: hide her, then prove the recovery paths answer.
    /// The hotkey path is exercised by posting the very WM_HOTKEY the OS would send,
    /// so the global key capture itself is the only part left to RegisterHotKey's
    /// own success flag. The window is left hidden at the end so a second launch can
    /// be checked from outside.
    /// </summary>
    private async Task RunHideProbeAsync()
    {
        await Task.Delay(1500).ConfigureAwait(true);
        ToggleVisible();
        await Task.Delay(1200).ConfigureAwait(true);
        _log.Info($"HIDE PROBE after hide: IsVisible={IsVisible}");
        Win32.PostMessage(_hwnd, Win32.WM_HOTKEY, new IntPtr(HotkeyId), IntPtr.Zero);
        await Task.Delay(1200).ConfigureAwait(true);
        _log.Info($"HIDE PROBE after hotkey message: IsVisible={IsVisible} (expect True)");
        ToggleVisible();
        await Task.Delay(900).ConfigureAwait(true);
        _log.Info($"HIDE PROBE final: IsVisible={IsVisible} (hidden again on purpose)");
    }
    /// <summary>
    /// Walks the pet across every monitor, holding on each one long enough to be
    /// screenshotted, and leaves her on the last one.
    /// </summary>
    private async Task RunMonitorProbeAsync()
    {
        var all = Monitors.All(refresh: true);
        _log.Info($"MONITOR PROBE: {all.Count} monitor(s)");
        for (var i = 0; i < all.Count; i++)
        {
            var m = all[i];            DockToAnchor(m, "right");
            await Task.Delay(1200).ConfigureAwait(true);
            var (x, y, w, h) = PhysicalRect();
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var inside = x >= m.Left && y >= m.Top && x + w <= m.Right && y + h <= m.Bottom;
            _log.Info($"MONITOR PROBE [{i}] {m.Label} {m.Detail} -> rect {x},{y} {w}x{h} realScale={scale:0.##} insideWorkArea={inside} expectedCorner={m.Right - w - (int)Math.Round(_opts.MarginX * scale)}");
            await Task.Delay(1600).ConfigureAwait(true);
        }
        _log.Info($"MONITOR PROBE holding on {_monitor.Device} for the screenshot");
    }
    /// <summary>Opens the monitoring card, logs a real sample, and leaves it open for a screenshot.</summary>
    private async Task RunMonitorPanelProbeAsync()
    {
        await Task.Delay(1500).ConfigureAwait(true);
        TogglePanel();
        await Task.Delay(3200).ConfigureAwait(true);   // let a few samples and one GPU rebuild land
        var s = _lastSnapshot;
        _log.Info($"MONITOR PROBE cpu={s.CpuPercent:0.0}% mem={Fmt.Bytes(s.MemAvailable)} free of {Fmt.Bytes(s.MemTotal)} ({s.MemLoadPercent}%) " +                  $"gpu={(s.GpuAvailable ? $"{s.GpuPercent:0.0}%" : "unavailable")} vram={Fmt.Bytes(s.GpuDedicatedBytes)} " +                  $"disk={s.DiskName.TrimEnd('\\')} free={Fmt.Bytes(s.DiskFree)}/{Fmt.Bytes(s.DiskTotal)} ({s.DiskFreePercent:0.0}%) busy={s.DiskBusyPercent:0.0}% " +                  $"netDown={Fmt.Rate(s.NetDownBps)} netUp={Fmt.Rate(s.NetUpBps)} " +                  $"top={string.Join(", ", s.TopProcesses.Take(3).Select(p => $"{p.Name} {p.CpuPercent:0.0}%"))} error={s.Error ?? "none"}");
        _log.Info("MONITOR PROBE panel held open for the screenshot");
    }
    /// <summary>Opens the cleanup card, runs a REAL scan, and logs the table. Deletes nothing.</summary>
    private async Task RunCleanupProbeAsync()
    {
        await Task.Delay(1200).ConfigureAwait(true);
        ToggleCleanup();
        var waited = 0;
        while (_cleanupScanning && waited < 90_000)
        {
            await Task.Delay(250).ConfigureAwait(true);            waited += 250;
        }
        await Task.Delay(400).ConfigureAwait(true);
        _log.Info($"CLEANUP PROBE open={_cleanupOpen} height={_cleanupHeight:0} scanWaitedMs={waited}");
        _log.Info("CLEANUP PROBE scan: " + _visual.Cleanup.DescribeScans(_lastCleanupScans));
        _log.Info($"CLEANUP PROBE summary: {_visual.Cleanup.SummaryText}");
        _log.Info("CLEANUP PROBE card held open for the screenshot (nothing was deleted)");
    }
    /// <summary>
    /// Reproduces the "pet showing only half" bug: force the window taller than the work
    /// area (by dragging the list to its maximum, or by stacking two cards) and check that
    /// the work-area ceiling keeps the whole window — and therefore the pet — on screen.
    /// </summary>
    private async Task RunOverflowProbeAsync()
    {
        await Task.Delay(1500).ConfigureAwait(true);
        ToggleCleanup();
        var waited = 0;
        while (_cleanupScanning && waited < 60_000) { await Task.Delay(250).ConfigureAwait(true); waited += 250; }
        await Task.Delay(400).ConfigureAwait(true);
        LogOverflow("cleanup card open");
        _visual.Cleanup.SetListHeight(720);
          // what dragging the list grip down does
        await Task.Delay(700).ConfigureAwait(true);
        LogOverflow("list dragged to 720 DIP");
        _log.Info($"OVERFLOW list height after the ceiling: {_visual.Cleanup.ListHeight:0} DIP");
        _visual.Cleanup.SetCardWidth(600);
           // dragging the width grip
        await Task.Delay(700).ConfigureAwait(true);
        LogOverflow("card widened to 600 DIP");
        _log.Info($"OVERFLOW card width after resize: {_visual.Cleanup.PreferredWidth:0} DIP, window width {Width:0} DIP");
        OpenMenu();
                                  // this used to stack on top of the card
        await Task.Delay(900).ConfigureAwait(true);
        LogOverflow("menu opened over the card");
        _log.Info($"OVERFLOW menuOpen={_menuOpen} cleanupOpen={_cleanupOpen} (the guard closes the other card)");
        OpenCleanupCard();
        await Task.Delay(700).ConfigureAwait(true);
        LogOverflow("cleanup card reopened");
        _log.Info("OVERFLOW PROBE holding for the screenshot");
    }
    private void LogOverflow(string label)
    {
        var (x, y, w, h) = PhysicalRect();
        var m = _monitor;
        var inside = x >= m.Left && y >= m.Top && x + w <= m.Right && y + h <= m.Bottom;
        _log.Info($"OVERFLOW [{label}] rect={x},{y} {w}x{h} work=({m.Left},{m.Top})-({m.Right},{m.Bottom}) " +                  $"insideWorkArea={inside} PET_FULLY_VISIBLE={inside} extra={_extraHeight:0} DIP");
    }
    /// <summary>
    /// Verifies "the panel is fully visible wherever she is": puts the pet in each corner
    /// in turn, opens the cleanup card, and checks the pet did not move and the whole
    /// window stayed inside the work area.
    /// </summary>
    private async Task RunPlacementProbeAsync()
    {
        await Task.Delay(1200).ConfigureAwait(true);
        var m = _monitor;
        // Keep the probe's placements inside the work area; the window is ~438 px wide, so
        // "m.Right - 420" put it 18 px off the right edge (a probe bug, not an app bug).
        var winW = (int)Math.Round(Width * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var winH = (int)Math.Round(Height * VisualTreeHelper.GetDpi(this).DpiScaleY);
        var spots = new (string Name, int X, int Y)[]
        {            ("top-left", m.Left + 30, m.Top + 30),            ("top-right", m.Right - winW - 30, m.Top + 30),            ("bottom-left", m.Left + 30, m.Bottom - winH - 30),            ("centre", m.Left + (m.Right - m.Left - winW) / 2, m.Top + (m.Bottom - m.Top - winH) / 2),
        };
        foreach (var spot in spots)
        {
            // Close first, move, then open: this is the real user path (she is dragged
            // somewhere new and the panel is opened there) and it forces re-placement.
            if (_cleanupOpen) ToggleCleanup();
            await Task.Delay(200).ConfigureAwait(true);
            MovePhysical(spot.X, spot.Y);
            await Task.Delay(250).ConfigureAwait(true);
            var (px, py) = PetScreenOrigin();
            ToggleCleanup();
            var waited = 0;
            while (_cleanupScanning && waited < 60_000) { await Task.Delay(250).ConfigureAwait(true); waited += 250; }
            await Task.Delay(600).ConfigureAwait(true);
            var (ax, ay) = PetScreenOrigin();
            var (x, y, w, h) = PhysicalRect();
            var inside = x >= m.Left && y >= m.Top && x + w <= m.Right && y + h <= m.Bottom;
            var petInside = ax >= m.Left && ay >= m.Top;
            _log.Info($"PLACEMENT [{spot.Name}] petBefore=({px},{py}) petAfter=({ax},{ay}) petMoved={Math.Abs(ax - px) + Math.Abs(ay - py)}px " +                      $"cardSide={(_visual.CardBelow ? "below" : "above")} rect={x},{y} {w}x{h} work=({m.Left},{m.Top})-({m.Right},{m.Bottom}) " +                      $"insideWorkArea={inside} FULLY_VISIBLE={inside && petInside}");
        }
        MovePhysical(m.Left + 30, m.Top + 30);
        await Task.Delay(300).ConfigureAwait(true);
        if (!_cleanupOpen) ToggleCleanup();
        await Task.Delay(600).ConfigureAwait(true);
        _log.Info("PLACEMENT PROBE holding at top-left for the screenshot (every panel resize handle is on the card border)");
    }
    /// <summary>
    /// Drives the resize maths directly with synthetic cursor positions, so the two
    /// reported bugs become measurable: no cursor motion must change nothing, and a
    /// given cursor travel must change the size by exactly that travel / DPI.
    /// </summary>
    private async Task RunResizeProbeAsync()
    {
        await Task.Delay(1200).ConfigureAwait(true);
        ToggleCleanup();
        var waited = 0;
        while (_cleanupScanning && waited < 60_000) { await Task.Delay(250).ConfigureAwait(true); waited += 250; }
        await Task.Delay(400).ConfigureAwait(true);
        // --- jitter: hold the button, never move the cursor ---
        _visual.Cleanup.BeginResize(horizontal: true, outwardSign: -1);
        var start = _visual.Cleanup.ResizeStartCursor;
        var width0 = _visual.Cleanup.PreferredWidth;
        for (var i = 0; i < 15; i++)
        {
            _visual.Cleanup.UpdateResize(start.X, start.Y);
            await Task.Delay(25).ConfigureAwait(true);
        }
        var width1 = _visual.Cleanup.PreferredWidth;
        _log.Info($"RESIZE jitter: width {width0:0.0} -> {width1:0.0} DIP after 15 no-motion updates, drift={width1 - width0:0.000} (expect 0)");
        // --- linearity: 10 px steps must give equal size steps ---
        var dpi = VisualTreeHelper.GetDpi(this);
        var steps = new List<double>();
        for (var step = 10; step <= 100; step += 10)
        {
            _visual.Cleanup.UpdateResize(start.X - step, start.Y);   // left edge: leftwards = wider
            await Task.Delay(20).ConfigureAwait(true);            steps.Add(_visual.Cleanup.PreferredWidth);
        }
        var deltas = steps.Zip(steps.Skip(1), (a, b) => Math.Round(b - a, 3)).ToList();
        var expected = Math.Round(10 / dpi.DpiScaleX, 3);
        var spread = deltas.Count == 0 ? 0 : deltas.Max() - deltas.Min();
        _log.Info($"RESIZE linear: expected {expected:0.###} DIP per 10px (dpi={dpi.DpiScaleX:0.##}); actual steps [{string.Join(", ", deltas)}] spread={spread:0.###} DIP");
        // --- vertical resize, same two checks ---
        _visual.Cleanup.SetListHeight(300);
        _visual.Cleanup.BeginResize(horizontal: false, outwardSign: -1);
        var vstart = _visual.Cleanup.ResizeStartCursor;
        var h0 = _visual.Cleanup.ListHeight;
        for (var i = 0; i < 15; i++) { _visual.Cleanup.UpdateResize(vstart.X, vstart.Y); await Task.Delay(25).ConfigureAwait(true); }
        var h1 = _visual.Cleanup.ListHeight;
        _visual.Cleanup.UpdateResize(vstart.X, vstart.Y - 50);
        await Task.Delay(60).ConfigureAwait(true);
        var h2 = _visual.Cleanup.ListHeight;
        _log.Info($"RESIZE vertical: no-motion drift={h1 - h0:0.000} (expect 0); -50px upward -> {h0:0} -> {h2:0} DIP (expect +{50 / dpi.DpiScaleY:0.##})");
        _log.Info("RESIZE PROBE done (card left open for the screenshot)");
    }
    /// <summary>
    /// Verifies the resize cursor band in DIP relative to the card: the band must reach
    /// OUTSIDE every edge, and must not swallow the scrollbar (right) or the footer (bottom).
    /// </summary>
    private async Task RunEdgeProbeAsync()
    {
        await Task.Delay(1200).ConfigureAwait(true);
        ToggleCleanup();
        var waited = 0;
        while (_cleanupScanning && waited < 60_000) { await Task.Delay(250).ConfigureAwait(true); waited += 250; }
        await Task.Delay(600).ConfigureAwait(true);
        var w = _visual.Cleanup.ActualWidth;
        var h = _visual.Cleanup.ActualHeight;
        var probes = new (string Label, double X, double Y, string Expect)[]
        {            ("outside-left-8", -8, h / 2, "left"),            ("outside-left-15", -15, h / 2, "left"),            ("on-left-edge", 0, h / 2, "left"),            ("inside-left-4", 4, h / 2, "left"),            ("inside-left-20", 20, h / 2, "none"),            ("outside-right-4", w + 4, h / 2, "right"),            ("on-right-edge", w, h / 2, "right"),            ("inside-right-1", w - 1, h / 2, "right"),            ("inside-right-12", w - 12, h / 2, "none (scrollbar stays grabbable)"),            ("outside-top-8", w / 2, -8, "top"),            ("on-top-edge", w / 2, 0, "top"),            ("inside-top-8", w / 2, 8, "top"),            ("inside-top-30", w / 2, 30, "none"),            ("outside-bottom-6", w / 2, h + 6, "bottom"),            ("inside-bottom-3", w / 2, h - 3, "bottom"),            ("inside-bottom-12", w / 2, h - 12, "none (footer stays clickable)"),            ("card-middle", w / 2, h / 2, "none"),
        };
        var pass = 0;
        var fail = 0;
        foreach (var probe in probes)
        {
            var edge = _visual.Cleanup.EdgeAt(new Point(probe.X, probe.Y)) ?? "none";
            var ok = probe.Expect.StartsWith(edge, StringComparison.Ordinal) || (probe.Expect.StartsWith("none") && edge == "none");
            if (ok) pass++; else fail++;
            _log.Info($"EDGE [{(ok ? "PASS" : "FAIL")}] {probe.Label} at ({probe.X:0},{probe.Y:0}) -> {edge} (expect {probe.Expect})");
        }
        _log.Info($"EDGE probe: {pass} pass / {fail} fail  (card {w:0}x{h:0} DIP, band {CleanupPanel.EdgeBandInside:0} in / {CleanupPanel.EdgeBandOutside:0} out)");
        // an outside-press drag must resize, and must not need the cursor to be inside
        _visual.Cleanup.BeginResize(horizontal: true, outwardSign: -1);
        var start = _visual.Cleanup.ResizeStartCursor;
        var before = _visual.Cleanup.PreferredWidth;
        _visual.Cleanup.UpdateResize(start.X - 50, start.Y);
        await Task.Delay(120).ConfigureAwait(true);
        _log.Info($"EDGE drag-from-outside: width {before:0} -> {_visual.Cleanup.PreferredWidth:0} DIP after -50px (expected +40)");
        _visual.Cleanup.EndResize();
    }
    private async Task RunDshProbeAsync()
    {
        await Task.Delay(1200).ConfigureAwait(true);
        var heartbeatCount = 0;
        var firstTick = Environment.TickCount64;
        var lastTick = firstTick;
        var maxGapMs = 0L;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        heartbeat.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            maxGapMs = Math.Max(maxGapMs, now - lastTick);
            lastTick = now;
            heartbeatCount++;
        };
        heartbeat.Start();
        _log.Info("DSH PROBE start action=BringDshToFront heartbeat=100ms");
        var result = await BringDshToFrontAsync();
        await Task.Delay(700).ConfigureAwait(true);
        heartbeat.Stop();
        var elapsed = Math.Max(0, lastTick - firstTick);
        _log.Info(
            $"DSH PROBE UI heartbeat ticks={heartbeatCount} elapsedMs={elapsed} maxGapMs={maxGapMs} " +
            $"responsive={(maxGapMs < 500 ? "YES" : "NO")} outcome={result?.Outcome.ToString() ?? "exception"}");
        _log.Info(
            $"DSH PROBE done mechanism={result?.Mechanism ?? "none"} elapsedMs={result?.ElapsedMs ?? 0} " +
            $"processes={(result is null ? "?" : $"{result.ProcessCountBefore}->{result.ProcessCountAfter}")}");
    }
    /// <summary>
    /// Diagnostic probe: is THIS process allowed to start children at all?
    /// A child DSH died with STATUS_BREAKPOINT (0x80000003) and the Explorer fallback with
    /// STATUS_DLL_INIT_FAILED (0xc0000142) — both are process-creation failures, so the
    /// question is whether this process's token/environment is restricted.
    /// </summary>
    private async Task RunSpawnProbeAsync()
    {
        await Task.Delay(700).ConfigureAwait(true);
        try
        {
        _log.Info("SPAWN PROBE trust: " + SpawnDiagnostics.DescribeTrust());
        _log.Info("SPAWN PROBE environment: " + SpawnDiagnostics.DescribeEnvironment());
        _log.Info($"SPAWN PROBE memory: {Fmt.Bytes(_lastSnapshot.MemAvailable)} free of {Fmt.Bytes(_lastSnapshot.MemTotal)}; " +
                  $"processes={System.Diagnostics.Process.GetProcesses().Length}");
        try
        {
            var reportRoot = System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(_opts.StatePath) ?? ".") ?? ".";
            var reportPath = System.IO.Path.Combine(reportRoot, "build", "spawn-probe.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(reportPath)!);
            System.IO.File.WriteAllText(reportPath,
                $"when={DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"trust={SpawnDiagnostics.DescribeTrust()}{Environment.NewLine}" +
                $"environment={SpawnDiagnostics.DescribeEnvironment()}{Environment.NewLine}" +
                $"control-child={SpawnDiagnostics.RunControlChild("/c exit 7")}{Environment.NewLine}" +
                $"dsh-processes={System.Diagnostics.Process.GetProcessesByName("DeepSeek Harness").Length}{Environment.NewLine}");
            _log.Info("SPAWN PROBE report -> " + reportPath);
        }
        catch (Exception ex) { _log.Warn("SPAWN PROBE report failed: " + ex.Message); }
        _log.Info("SPAWN PROBE " + SpawnDiagnostics.RunControlChild("/c exit 7"));

        var dshRunning = System.Diagnostics.Process.GetProcessesByName("DeepSeek Harness").Length > 0;
        if (dshRunning)
        {
            _log.Info("SPAWN PROBE dsh-child: skipped (a DSH process already exists, so a launch would only hand off)");
        }
        else
        {
            const string dshExe = @"D:\Software\DeepSeek Harness\DeepSeek Harness.exe";
            _log.Info("SPAWN PROBE dsh-child: launching directly to observe its exit code");
            try
            {
                var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dshExe,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(dshExe) ?? "",
                    UseShellExecute = false,
                });
                if (child is null)
                {
                    _log.Warn("SPAWN PROBE dsh-child: Start returned null");
                }
                else
                {
                    var exited = child.WaitForExit(25_000);
                    var code = exited ? child.ExitCode : 0;
                    var hex = exited ? $"0x{unchecked((uint)code):X8}" : "-";
                    _log.Info($"SPAWN PROBE dsh-child: hasExited={exited} exitCode={(exited ? code.ToString() : "still-running")} hex={hex}");
                    _log.Info("SPAWN PROBE dsh-child: windows now = " +
                              string.Join(" | ", new[] { "DeepSeek Harness" }.Select(_ => Win32.FindMainWindow("DeepSeek Harness", out var hid, out var det) is var h && h != IntPtr.Zero
                                  ? $"{det} hidden={hid}" : "none")));
                }
            }
            catch (Exception ex)
            {
                _log.Warn("SPAWN PROBE dsh-child: threw " + ex);
            }
        }
        _log.Info("SPAWN PROBE done");
        }
        catch (Exception ex)
        {
            _log.Error("SPAWN PROBE threw: " + ex);
        }
    }
    private void UpdateClickThroughNow()
    {
        if (AnyCardOpen || _dragging) { SetClickThrough(false); return; }
        SetClickThrough(!IsCursorOverPet());
    }

    /// <summary>--probe-clipboard: open the clipboard-history window and hold it for a screenshot.</summary>
    private async Task RunClipboardProbeAsync()
    {
        await Task.Delay(900);
        ShowClipboardHistory();
        _log.Info("CLIPBOARD PROBE window held open for the screenshot");
        await Task.Delay(8000);
    }

    /// <summary>--probe-dsh-guide: open the "DSH not found" guide and hold it for a screenshot.</summary>
    private async Task RunDshGuideProbeAsync()
    {
        await Task.Delay(900);
        ShowDshGuide();
        _log.Info("DSH GUIDEPROBE window held open for the screenshot");
        await Task.Delay(8000);
    }

    /// <summary>--probe-focus: start a pomodoro so the status strip shows focus mode, then hold.</summary>
    private async Task RunFocusProbeAsync()
    {
        await Task.Delay(1000);
        try
        {
            _pomodoro.Configure(25, 5);
            _pomodoro.StartFocus(Environment.TickCount64);
            _focusQuiet = true;
            _ui.FocusQuiet = true;
            Persist();
            ShowLocalBubble("专注开始了，这段时间我安静陪你", 6000, force: true);
            _log.Info($"FOCUS PROBE running={_pomodoro.Running} remaining={_pomodoro.RemainingSeconds(Environment.TickCount64)}s");
        }
        catch (Exception ex)
        {
            _log.Warn("FOCUS PROBE failed: " + ex.Message);
        }
        await Task.Delay(9000);
    }

}
