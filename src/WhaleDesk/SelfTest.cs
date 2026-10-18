using System.IO;
using System.Text.Json;

namespace WhaleDesk;

/// <summary>
/// Headless contract validation (V0). No window, no Application, no DPI
/// dependency: if this fails, the renderer must not be trusted.
/// </summary>
internal static class SelfTest
{
    public static int Run(CliOptions opts, PetSpec spec, AtlasLoader atlas, Log log)
    {
        var checks = new List<object>();
        var failed = 0;

        void Check(string name, bool ok, string detail)
        {
            checks.Add(new { name, ok, detail });
            if (ok) log.Info($"selftest PASS {name}: {detail}");
            else { failed++; log.Error($"selftest FAIL {name}: {detail}"); }
        }

        var problems = spec.Validate();
        Check("spec-contract", problems.Count == 0,
            problems.Count == 0 ? $"{spec.Tracks.Count} tracks x {spec.Columns} columns, cell {spec.CellWidth}x{spec.CellHeight}"
                                : string.Join(" | ", problems));

        Check("atlas-size",
            atlas.Atlas.PixelWidth == atlas.CellW * atlas.Columns && atlas.Atlas.PixelHeight == atlas.CellH * atlas.Rows,
            $"{atlas.Atlas.PixelWidth}x{atlas.Atlas.PixelHeight} vs expected {atlas.CellW * atlas.Columns}x{atlas.CellH * atlas.Rows}");

        Check("atlas-dpi-normalized", Math.Abs(atlas.Atlas.DpiX - 96) < 0.01 && Math.Abs(atlas.Atlas.DpiY - 96) < 0.01,
            $"{atlas.Atlas.DpiX}x{atlas.Atlas.DpiY} (1 DIP must equal 1 atlas pixel)");

        var empty = new List<string>();
        var bleed = new List<string>();
        var inspected = 0;
        foreach (var t in spec.Tracks)
        {
            foreach (var f in t.Frames)
            {
                inspected++;
                var (opaque, _, minX, minY, maxX, maxY) = atlas.CellStats(t.Row, f);
                if (opaque == 0) { empty.Add($"{t.Name}[{f}]"); continue; }
                if (minX == 0 || minY == 0 || maxX == atlas.CellW - 1 || maxY == atlas.CellH - 1)
                    bleed.Add($"{t.Name}[{f}] bbox=({minX},{minY},{maxX},{maxY})");
            }
        }
        Check("frames-non-empty", empty.Count == 0, empty.Count == 0 ? $"{inspected}/{inspected} cells contain pixels" : string.Join(",", empty));
        Check("no-cell-bleed", bleed.Count == 0, bleed.Count == 0 ? "no sprite reaches its 192x208 cell border" : string.Join("; ", bleed));

        var stats = atlas.CellStats(0, 0);
        Check("mask-has-transparency", stats.opaque > 0 && stats.opaque < stats.total, $"idle[0] opaque={stats.opaque}/{stats.total}");

        Check("hittest-out-of-range",
            !atlas.IsOpaque(0, 0, -0.01, 0.5) && !atlas.IsOpaque(0, 0, 1.01, 0.5) && !atlas.IsOpaque(0, 0, 0.5, -0.01),
            "points outside the cell are never opaque");

        var nonLooping = spec.Tracks.FirstOrDefault(t => !t.Loop && !string.IsNullOrEmpty(t.Fallback));
        if (nonLooping != null)
        {
            var player = new TrackPlayer(spec);
            player.Play(nonLooping.Name, 0, restart: true);
            for (long ms = 0; ms <= nonLooping.TotalMs + 64; ms += 16) player.Tick(ms);
            Check("nonloop-falls-back", player.TrackName == nonLooping.Fallback,
                $"{nonLooping.Name} ({nonLooping.TotalMs}ms) -> {player.TrackName} (expected {nonLooping.Fallback})");
        }
        else Check("nonloop-falls-back", false, "spec has no non-looping track with a fallback");

        var stalled = new TrackPlayer(spec);
        stalled.Play("idle", 0, restart: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        stalled.Tick(10_000_000);
        sw.Stop();
        Check("stall-does-not-spin", sw.ElapsedMilliseconds < 50, $"Tick after 10,000s took {sw.ElapsedMilliseconds}ms");

        // ---------- P2.1: placement math (physical pixels, per monitor) ----------
        var mon = new MonitorEntry(@"\\.\DISPLAY1", true, 0, 0, 1920, 1030, 1.25);
        var secondary = new MonitorEntry(@"\\.\DISPLAY2", false, 2400, 0, 4800, 1300, 1.25);

        var (brX, brY) = Placement.BottomRight(mon, 300, 365, 16, 10);
        Check("dock-bottom-right", brX == 1600 && brY == 652,
            $"got {brX},{brY} (expected 1600,652: 1920-300-20, 1030-365-13)");

        var (lX, lY) = Placement.Edge(mon, "left", 300, 365, 400, 16);
        var (rX, rY) = Placement.Edge(mon, "right", 300, 365, 400, 16);
        Check("dock-edges", lX == 20 && rX == 1600 && lY == 400 && rY == 400,
            $"left={lX},{lY} right={rX},{rY} (margin 16 DIP at 125% = 20 px)");

        Check("anchor-decision",
            Placement.DecideAnchor(mon, 1750) == "right" && Placement.DecideAnchor(mon, 500) == "left",
            "centre-left => left edge, centre-right => right edge");

        var (cX, cY) = Placement.Clamp(mon, -500, -400, 300, 365);
        Check("clamp-into-work-area", cX == 0 && cY == 0, $"clamped to {cX},{cY}");

        Check("usable-check",
            !Placement.IsUsable(mon, -900, -700, 300, 365) && Placement.IsUsable(mon, 1600, 652, 300, 365),
            "an off-screen saved rect is rejected, an on-screen one is accepted");

        var (sX, sY) = Placement.BottomRight(secondary, 300, 365, 16, 10);
        Check("dock-on-secondary-monitor", sX == 4480 && sY == 922,
            $"got {sX},{sY} (expected 4480,922: 4800-300-20, 1300-365-13)");

        // ---------- P2.1: state persistence (physical pixels + monitor device) ----------
        var statePath = Path.Combine(opts.BuildDir, "verify", "state-roundtrip.json");
        new UiState { Monitor = @"\\.\DISPLAY2", X = 4480, Y = 922, Anchor = "right", Paused = true }.Save(statePath);
        var reloaded = UiState.Load(statePath);
        Check("state-roundtrip",
            reloaded.Monitor == @"\\.\DISPLAY2" && reloaded.X == 4480 && reloaded.Y == 922 && reloaded.Anchor == "right" && reloaded.Paused,
            $"monitor={reloaded.Monitor} pos={reloaded.X},{reloaded.Y} anchor={reloaded.Anchor} paused={reloaded.Paused}");
        var missing = UiState.Load(Path.Combine(opts.BuildDir, "verify", "does-not-exist.json"));
        Check("state-missing-file-is-default", missing.X is null && missing.Monitor is null, "a missing state file yields defaults");
        var corruptPath = Path.Combine(opts.BuildDir, "verify", "state-corrupt.json");
        File.WriteAllText(corruptPath, "{ this is not json");
        var recovered = UiState.Load(corruptPath);
        Check("state-corrupt-file-recovers", recovered.X is null, "a corrupt state file must not break startup");
        try { File.Delete(statePath); File.Delete(corruptPath); } catch { }

        // ---------- P2: clickable edge (dilated mask) ----------
        var foundEdge = false;
        for (var y = 1; y < atlas.CellH - 1 && !foundEdge; y++)
        {
            for (var x = 1; x < atlas.CellW - 1 && !foundEdge; x++)
            {
                var u = (x + 0.5) / atlas.CellW;
                var v = (y + 0.5) / atlas.CellH;
                if (!atlas.IsOpaque(0, 0, u, v) && atlas.IsOpaqueDilated(0, 0, u, v)) foundEdge = true;
            }
        }
        Check("mask-dilation-covers-edge", foundEdge, "a transparent pixel just outside the sprite is still clickable");

        // ---------- P2: line bank cooldown ----------
        var bank = new LineBank();
        var first = bank.Pick("pet", 60_000);
        var second = bank.Pick("pet", 60_000);
        Check("linebank-cooldown", first is not null && second is null, $"first='{first}' second={(second is null ? "suppressed" : second)}");
        Check("linebank-unknown-category", new LineBank().Pick("nope") is null, "unknown categories yield no line");

        // ---------- P5: pomodoro (a whole cycle in microseconds) ----------
        var pomo = new Interaction.Pomodoro();
        pomo.Configure(25, 5);
        Check("pomodoro-initial-state", pomo.Phase == Interaction.PomodoroPhase.Idle && !pomo.Running && pomo.RemainingSeconds(0) == 0,
            "starts idle with nothing on the clock");
        var p0 = 1_000_000L;
        pomo.StartFocus(p0);
        Check("pomodoro-start-focus",
            pomo.Phase == Interaction.PomodoroPhase.Focus && pomo.RemainingSeconds(p0) == 25 * 60,
            $"remaining={pomo.RemainingSeconds(p0)}s described='{pomo.Describe(p0)}'");
        Check("pomodoro-no-transition-before-end", pomo.Tick(p0 + 24 * 60_000) is null, "no transition while time is left");
        Check("pomodoro-focus-to-break",
            pomo.Tick(p0 + 25 * 60_000) == Interaction.PomodoroPhase.Break && pomo.CompletedFocusSessions == 1,
            $"phase={pomo.Phase} completed={pomo.CompletedFocusSessions}");
        Check("pomodoro-break-to-idle",
            pomo.Tick(p0 + 30 * 60_000) == Interaction.PomodoroPhase.Idle && !pomo.Running && pomo.CompletedFocusSessions == 1,
            "the break ends the cycle and keeps the session count");
        pomo.Configure(0, 999);
        Check("pomodoro-clamps-config",
            pomo.FocusMinutes == Interaction.Pomodoro.MinMinutes && pomo.BreakMinutes == Interaction.Pomodoro.MaxMinutes,
            $"focus={pomo.FocusMinutes} break={pomo.BreakMinutes} (clamped)");
        var pomo2 = new Interaction.Pomodoro();
        pomo2.Restore(Interaction.PomodoroPhase.Focus, p0 + 60_000, 2);
        Check("pomodoro-restores-after-restart",
            pomo2.Running && pomo2.RemainingSeconds(p0 + 30_000) == 30 && pomo2.CompletedFocusSessions == 2,
            $"remaining={pomo2.RemainingSeconds(p0 + 30_000)}s completed={pomo2.CompletedFocusSessions}");

        // ---------- P5: pomodoro auto duration ----------
        var noon = new DateTime(2026, 10, 17, 14, 0, 0);
        var night = new DateTime(2026, 10, 17, 23, 30, 0);
        var (m1, r1) = Interaction.Pomodoro.ChooseAutoMinutes(noon, dshSessionActive: true, cpuPercent: 10, availableBytes: 8_000_000_000, totalBytes: 16_000_000_000);
        var (m2, r2) = Interaction.Pomodoro.ChooseAutoMinutes(noon, dshSessionActive: false, cpuPercent: 10, availableBytes: 8_000_000_000, totalBytes: 16_000_000_000);
        var (m3, r3) = Interaction.Pomodoro.ChooseAutoMinutes(noon, dshSessionActive: false, cpuPercent: 80, availableBytes: 8_000_000_000, totalBytes: 16_000_000_000);
        var (m4, r4) = Interaction.Pomodoro.ChooseAutoMinutes(night, dshSessionActive: false, cpuPercent: 10, availableBytes: 8_000_000_000, totalBytes: 16_000_000_000);
        var (m5, r5) = Interaction.Pomodoro.ChooseAutoMinutes(noon, dshSessionActive: true, cpuPercent: 10, availableBytes: 1_000_000_000, totalBytes: 16_000_000_000);
        Check("pomodoro-auto-chooses-by-situation",
            m1 == 45 && m2 == 30 && m3 == 25 && m4 == 20 && m5 == 15,
            $"DSH会话={m1}(\"{r1}\") 平常={m2} 高CPU={m3} 深夜={m4} 内存紧={m5}");
        var pomoAuto = new Interaction.Pomodoro();
        pomoAuto.SetPreset(0);
        var (autoMin, autoReason) = pomoAuto.StartFocusAuto(noon, dshSessionActive: true, cpuPercent: 5,
            availableBytes: 9_000_000_000, totalBytes: 16_000_000_000, now: p0);
        Check("pomodoro-auto-applies-the-choice",
            pomoAuto.Running && pomoAuto.FocusMinutes == autoMin && autoMin == 45 && pomoAuto.RemainingSeconds(p0) == 45 * 60,
            $"auto picked {autoMin}min ({autoReason}), remaining={pomoAuto.RemainingSeconds(p0)}s");
        pomoAuto.SetPreset(15);
        var (fixedMin, _) = pomoAuto.StartFocusAuto(noon, dshSessionActive: true, cpuPercent: 5,
            availableBytes: 9_000_000_000, totalBytes: 16_000_000_000, now: p0);
        Check("pomodoro-manual-preset-overrides-auto",
            fixedMin == 15 && pomoAuto.FocusMinutes == 15 && pomoAuto.PresetLabel == "15 分钟",
            $"a manual preset wins ({pomoAuto.PresetLabel})");
        Check("pomodoro-preset-cycles",
            new Interaction.Pomodoro().PresetLabel == "自动",
            "the menu cycle starts at 自动 and goes 15 -> 25 -> 45 -> 60 -> 自动");

        // ---------- P5: easter eggs ----------
        var eggs = new Interaction.EasterEggs();
        var e0 = 5_000_000L;
        Check("egg-no-reaction-on-first-click", eggs.RegisterClick(e0) == Interaction.Reaction.None, "one click is just a click");
        Check("egg-combo1-at-three",
            eggs.RegisterClick(e0 + 100) == Interaction.Reaction.None &&
            eggs.RegisterClick(e0 + 200) == Interaction.Reaction.Combo1,
            $"combo={eggs.ComboCount} -> Combo1");
        eggs.RegisterClick(e0 + 300);
        eggs.RegisterClick(e0 + 400);
        Check("egg-combo2-at-six", eggs.RegisterClick(e0 + 500) == Interaction.Reaction.Combo2, $"combo={eggs.ComboCount} -> Combo2");
        for (var i = 0; i < 5; i++) eggs.RegisterClick(e0 + 600 + i * 10);
        Check("egg-dizzy-at-twelve", eggs.RegisterClick(e0 + 700) == Interaction.Reaction.Dizzy, $"combo={eggs.ComboCount} -> Dizzy");
        Check("egg-combo-resets-after-a-pause",
            eggs.RegisterClick(e0 + 700 + Interaction.EasterEggs.ComboWindowMs + 1) == Interaction.Reaction.None && eggs.ComboCount == 1,
            "a pause starts a new combo");

        var eggs2 = new Interaction.EasterEggs();
        var f0 = 9_000_000L;
        var feedReactions = Enumerable.Range(0, Interaction.EasterEggs.FeedOverloadCount)
            .Select(i => eggs2.RegisterFeed(f0 + i * 1000)).ToList();
        Check("egg-overfed-after-four-feeds",
            feedReactions.Take(3).All(r => r == Interaction.Reaction.None) && feedReactions[3] == Interaction.Reaction.Overfed,
            $"reactions={string.Join(",", feedReactions)}");
        Check("egg-feed-window-expires",
            new Interaction.EasterEggs().RegisterFeed(f0) == Interaction.Reaction.None &&
            new Interaction.EasterEggs().RegisterFeed(f0 + Interaction.EasterEggs.FeedWindowMs + 1) == Interaction.Reaction.None,
            "old feeds fall out of the 60s window");

        var eggs3 = new Interaction.EasterEggs();
        var shakes = new[] { 1, -1, 1, -1 }.Select((s, i) => eggs3.RegisterDragDirection(f0 + i * 100, s)).ToList();
        Check("egg-dizzy-from-shaking",
            shakes.Take(3).All(r => r == Interaction.Reaction.None) && shakes[3] == Interaction.Reaction.Dizzy,
            $"reactions={string.Join(",", shakes)}");
        // Regression: the window used to compute the shake direction with an expression
        // that was always 0, so this egg was dead code. It now takes the raw offset.
        var eggsShake = new Interaction.EasterEggs();
        var offsets = new[] { 0, 40, 0, -40, 40, -40, 45, -45 };
        var shakeReactions = offsets.Select((o, i) => eggsShake.RegisterDragSample(f0 + i * 200, o)).ToList();
        Check("egg-shake-from-raw-offsets",
            shakeReactions.Contains(Interaction.Reaction.Dizzy) && shakeReactions.Last() == Interaction.Reaction.Dizzy,
            $"offsets={string.Join(",", offsets)} reactions={string.Join(",", shakeReactions)} (needs {Interaction.EasterEggs.ShakeReversals} reversals)");
        var eggsTiny = new Interaction.EasterEggs();
        Check("egg-shake-ignores-tiny-moves",
            new[] { 0, 2, 0, -2, 1, -1, 2, -2 }.Select((o, i) => eggsTiny.RegisterDragSample(f0 + i * 200, o))
                .All(r => r == Interaction.Reaction.None),
            $"moves under {Interaction.EasterEggs.ShakeMinStepPx}px must not count as shakes");

        // Regression: the double-click branch returned before the combo counter ran, so a
        // rapid burst only ever registered its first click.
        var eggsBurst = new Interaction.EasterEggs();
        var burst = Enumerable.Range(1, 6)
            .Select(i => Interaction.InteractionInput.OnPress(eggsBurst, clickCount: i == 1 ? 1 : i, now: f0 + i * 150))
            .ToList();
        Check("egg-combo-survives-doubleclick-branch",
            burst[2].Reaction == Interaction.Reaction.Combo1 && burst[5].Reaction == Interaction.Reaction.Combo2 &&
            burst.Count(b => b.Celebrate) == 1,
            $"6 rapid presses -> {string.Join(",", burst.Select(b => b.Reaction))}, celebrations={burst.Count(b => b.Celebrate)} (exactly one, on click 2)");
        Check("egg-longpress-requires-held-not-still-idle",
            Interaction.InteractionInput.IsLongPress(dragging: true, moved: false, cardOpen: false, overPet: true) &&
            !Interaction.InteractionInput.IsLongPress(dragging: true, moved: true, cardOpen: false, overPet: true) &&
            !Interaction.InteractionInput.IsLongPress(dragging: false, moved: false, cardOpen: false, overPet: true) &&
            !Interaction.InteractionInput.IsLongPress(dragging: true, moved: false, cardOpen: true, overPet: true),
            "held + unmoved + no card open + over the pet (the old check required NOT dragging, so it never fired)");
        Check("egg-longpress-is-its-own-reaction",
            new Interaction.EasterEggs().RegisterLongPress(f0) == Interaction.Reaction.LongPress,
            "holding her produces a long-press reaction");
        var eggs4 = new Interaction.EasterEggs();
        eggs4.RegisterPlacement(f0, "bottom-right");
        Check("egg-corner-not-parks-nothing",
            new Interaction.EasterEggs().RegisterPlacement(f0, "none") == Interaction.Reaction.None,
            "the middle of the screen is not a corner");
        Check("egg-corner-settle-fires",
            eggs4.RegisterPlacement(f0 + Interaction.EasterEggs.CornerSettleMs, "bottom-right") == Interaction.Reaction.CornerSettle,
            "parked in the same corner long enough -> settle line");
        Check("egg-corner-settle-fires-once",
            eggs4.RegisterPlacement(f0 + Interaction.EasterEggs.CornerSettleMs + 1000, "bottom-right") == Interaction.Reaction.None &&
            eggs4.RegisterPlacement(f0 + Interaction.EasterEggs.CornerSettleMs + 2000, "bottom-right") == Interaction.Reaction.None,
            "it must not repeat every second while she stays put (that was a real bug)");
        var eggs6 = new Interaction.EasterEggs();
        eggs6.RegisterPlacement(f0, "bottom-right");
        eggs6.RegisterPlacement(f0 + 3000, "bottom-right");
        eggs6.RegisterPlacement(f0 + 4000, "none");
        eggs6.RegisterPlacement(f0 + 5000, "bottom-right");
        Check("egg-corner-re-arms-after-leaving",
            eggs6.RegisterPlacement(f0 + 5000 + Interaction.EasterEggs.CornerSettleMs, "bottom-right") == Interaction.Reaction.CornerSettle,
            "coming back to the corner arms it again");
        Check("egg-hourly-once-per-hour",
            new Interaction.EasterEggs().RegisterClock(f0, new DateTime(2026, 10, 17, 14, 0, 0)) == Interaction.Reaction.HourlyChime,
            "on the hour she chimes");
        var eggs5 = new Interaction.EasterEggs();
        eggs5.RegisterClock(f0, new DateTime(2026, 10, 17, 14, 0, 0));
        Check("egg-hourly-deduped",
            eggs5.RegisterClock(f0 + 1000, new DateTime(2026, 10, 17, 14, 0, 30)) == Interaction.Reaction.None &&
            eggs5.RegisterClock(f0 + 2000, new DateTime(2026, 10, 17, 14, 17, 0)) == Interaction.Reaction.None,
            "not twice in the same hour, and not off the hour");
        Check("egg-late-night-only-between-2-and-5",
            new Interaction.EasterEggs().RegisterLateNight(f0, new DateTime(2026, 10, 17, 3, 20, 0)) == Interaction.Reaction.LateNight &&
            new Interaction.EasterEggs().RegisterLateNight(f0, new DateTime(2026, 10, 17, 23, 0, 0)) == Interaction.Reaction.None,
            "3am triggers, 11pm does not");
        var eggsComeback = new Interaction.EasterEggs();
        eggsComeback.RegisterClick(f0);                       // first contact is not a comeback
        Check("egg-comeback-after-a-long-silence",
            eggsComeback.RegisterClick(f0 + Interaction.EasterEggs.ComebackSilenceMs + 1) == Interaction.Reaction.Comeback &&
            new Interaction.EasterEggs().RegisterClick(f0 + Interaction.EasterEggs.ComebackSilenceMs + 1) == Interaction.Reaction.None,
            "after 20 quiet minutes she greets you; with no history at all there is nothing to come back from");
        Check("egg-corner-classification",
            Interaction.EasterEggs.CornerOf(10, 10, 300, 300, 0, 0, 1920, 1030, 40) == "top-left" &&
            Interaction.EasterEggs.CornerOf(1620, 730, 1910, 1020, 0, 0, 1920, 1030, 40) == "bottom-right" &&
            Interaction.EasterEggs.CornerOf(800, 500, 1100, 800, 0, 0, 1920, 1030, 40) == "none",
            "window rects classify into the four corners");

        // ---------- P5: low-memory policy ----------
        var low = new Performance.LowMemoryPolicy();
        Check("lowmem-normal-intervals",
            low.MonitorIntervalMs(panelOpen: true) == 1000 && low.MonitorIntervalMs(panelOpen: false) == 5000 && low.FrameIntervalMs == 33,
            $"panelOpen={low.MonitorIntervalMs(true)}ms panelClosed={low.MonitorIntervalMs(false)}ms frame={low.FrameIntervalMs}ms");
        low.Enabled = true;
        Check("lowmem-tightens-intervals",
            low.MonitorIntervalMs(panelOpen: true) == 2000 && low.MonitorIntervalMs(panelOpen: false) == 20000 &&
            low.FrameIntervalMs == 66 && low.LowQualityRendering,
            $"panelOpen={low.MonitorIntervalMs(true)}ms panelClosed={low.MonitorIntervalMs(false)}ms frame={low.FrameIntervalMs}ms lowQuality={low.LowQualityRendering}");
        Check("lowmem-auto-enable-only-when-tight",
            Performance.LowMemoryPolicy.ShouldAutoEnable(1_000_000_000, 16_000_000_000, alreadyEnabled: false) &&
            !Performance.LowMemoryPolicy.ShouldAutoEnable(8_000_000_000, 16_000_000_000, alreadyEnabled: false) &&
            !Performance.LowMemoryPolicy.ShouldAutoEnable(1_000_000_000, 16_000_000_000, alreadyEnabled: true),
            $"threshold {Performance.LowMemoryPolicy.AutoEnableFreePercent}% free, never re-enables what is already on");

        // ---------- P5: autostart + self-recovery helpers ----------
        var exePath = @"C:\Users\someone\AppData\Local\Programs\WhaleDesk\WhaleDesk.exe";
        var autostartCmd = Recovery.AutoStart.CommandFor(exePath);
        Check("autostart-command-is-quoted-and-headless",
            autostartCmd == $"\"{exePath}\" --live --seconds 0",
            $"'{autostartCmd}' (quoted for paths with spaces; --seconds 0 means stay running)");
        Check("autostart-value-name-is-stable",
            Recovery.AutoStart.ValueName == "WhaleDesk" && Recovery.AutoStart.RunKeyPath == @"Software\Microsoft\Windows\CurrentVersion\Run",
            "per-user Run key (HKCU) => no administrator rights, no UAC prompt");
        // real registry round-trip, restoring whatever was there before.
        // NOTE: a sandboxed environment can deny writes to HKCU\...\Run. That is an
        // environment restriction, not a code failure — and the app reports it to the user
        // through the same error path, so it is only asserted when the write is permitted.
        var selfExe = Environment.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(selfExe))
        {
            var hadIt = Recovery.AutoStart.IsEnabled(selfExe);
            var enableOk = Recovery.AutoStart.TryEnable(selfExe, out var enableErr);
            var readOk = Recovery.AutoStart.TryRead(out var readCmd, out _);
            var isOn = Recovery.AutoStart.IsEnabled(selfExe);
            if (!hadIt) Recovery.AutoStart.TryDisable(out _);
            var denied = !enableOk && (enableErr?.Contains("denied", StringComparison.OrdinalIgnoreCase) ?? false);
            Check("autostart-registry-round-trip",
                denied || (enableOk && readOk && isOn && (readCmd?.Contains(selfExe, StringComparison.OrdinalIgnoreCase) ?? false)),
                denied
                    ? $"write denied by this environment ('{enableErr}') - reads still work and the UI surfaces the error; verify on a normal launch"
                    : $"write={enableOk} readBack={(readOk ? readCmd : "failed")} enabled={isOn} err={enableErr ?? "none"} (restored to {(hadIt ? "on" : "off")})");
            Check("autostart-read-path-works",
                Recovery.AutoStart.TryRead(out _, out var readError),
                $"reading the Run key is permitted even where writing is not (error={readError ?? "none"})");
        }
        // informational: the live working set when low-memory mode is on/off
        var wsNow = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        Check("p5-working-set-reported", wsNow > 0, $"this process currently uses {wsNow} MB (low-memory mode cuts sampling to 20s and frames to 15fps when idle)");
        var marker = Path.Combine(opts.BuildDir, "verify", "recovery-marker-test");
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
        Check("recovery-detects-clean-state", !Recovery.SelfHeal.WasUncleanExit(marker), "no marker => previous exit was clean");
        Recovery.SelfHeal.MarkRunning(marker, log);
        Check("recovery-detects-dirty-state", Recovery.SelfHeal.WasUncleanExit(marker), "a marker left behind => the last run died");
        Recovery.SelfHeal.MarkCleanExit(marker, log);
        Check("recovery-clears-marker", !Recovery.SelfHeal.WasUncleanExit(marker), "a clean exit removes the marker");
        Check("recovery-taskbar-message-registered", Recovery.SelfHeal.TaskbarCreatedMessage != 0,
            $"RegisterWindowMessage(\"TaskbarCreated\") = 0x{Recovery.SelfHeal.TaskbarCreatedMessage:X}");
        Check("recovery-dsh-offline-threshold",
            Recovery.SelfHeal.DshReachable(0) && Recovery.SelfHeal.DshReachable(2) && !Recovery.SelfHeal.DshReachable(3),
            "3 consecutive failures switch to the slow poll, a success switches back");
        Check("p5-lines-present",
            new[] { "combo1", "combo2", "dizzy", "longpress", "overfed", "corner", "latenight", "hourly", "comeback",
                    "pomodoro-start", "pomodoro-break", "pomodoro-done", "lowmemory-on", "lowmemory-off" }
                .All(LineBank.Has),
            "every new interaction has its own lines");

        // ---------- P3: monitoring ----------
        var engineInstance = "pid_17296_luid_0x00000000_0x0000dc14_phys_0_eng_0_engtype_3d";
        Check("gpu-engine-type-parse",
            Monitoring.PdhQuery.ParseEngineType(engineInstance) == "3d" &&
            Monitoring.PdhQuery.ParseEngineType("pid_1_luid_0xa_0xb_phys_0_eng_1_engtype_VideoDecode") == "VideoDecode" &&
            Monitoring.PdhQuery.ParseEngineType("no-engine-here") is null,
            $"3d / VideoDecode / null from '{engineInstance}'");
        Check("gpu-luid-parse",
            Monitoring.PdhQuery.ParseLuid(engineInstance) == "0x00000000_0x0000dc14",
            "the adapter LUID is recoverable from the instance name");

        var gpuSamples = new (string Instance, double Value)[]
        {
            ("pid_1_luid_0x0_0xdc14_phys_0_eng_0_engtype_3d", 30),
            ("pid_2_luid_0x0_0xdc14_phys_0_eng_0_engtype_3d", 25),
            ("pid_1_luid_0x0_0xdc14_phys_0_eng_1_engtype_Copy", 40),
            ("not-an-instance", 99),
        };
        Check("gpu-aggregate-busiest-engine",
            Math.Abs(Monitoring.PdhQuery.AggregateGpuEngine(gpuSamples) - 55) < 0.001,
            "3d=30+25 beats copy=40 -> 55, and the unparsable instance is ignored");
        Check("gpu-aggregate-caps-at-100",
            Monitoring.PdhQuery.AggregateGpuEngine(new[] { ("a_engtype_3d", 80.0), ("b_engtype_3d", 90.0) }) == 100,
            "summed engine time can exceed 100 and must be clamped");

        var cpuA = new Monitoring.SysInfo.CpuTimes(100, 200, 100);
        var cpuB = new Monitoring.SysInfo.CpuTimes(150, 300, 150);   // idle+50, kernel+100, user+50
        Check("cpu-load-math",
            Math.Abs(Monitoring.SysInfo.CpuLoadPercent(cpuA, cpuB) - 66.6667) < 0.01,
            $"{Monitoring.SysInfo.CpuLoadPercent(cpuA, cpuB):0.00}% (kernel already contains idle)");
        Check("cpu-load-no-divide-by-zero",
            Monitoring.SysInfo.CpuLoadPercent(cpuA, cpuA) == 0,
            "identical samples return 0 instead of NaN");

        Check("format-bytes",
            Monitoring.Fmt.Bytes(1536) == "2 KB" && Monitoring.Fmt.Bytes(15UL << 30).StartsWith("15.0 GB"),
            $"{Monitoring.Fmt.Bytes(1536)} / {Monitoring.Fmt.Bytes(15UL << 30)}");

        using (var monitor = new Monitoring.MonitorService(log))
        {
            monitor.Sample();
            Thread.Sleep(350);
            var live = monitor.Sample();
            Check("live-cpu-in-range", live.CpuPercent is >= 0 and <= 100, $"{live.CpuPercent:0.0}%");
            Check("live-memory-readable",
                live.MemTotal > 0 && live.MemAvailable <= live.MemTotal,
                $"{Monitoring.Fmt.Bytes(live.MemAvailable)} free of {Monitoring.Fmt.Bytes(live.MemTotal)} ({live.MemLoadPercent}%)");
            Check("live-disk-readable", live.DiskTotal > 0,
                $"{live.DiskName.TrimEnd('\\')} {Monitoring.Fmt.Bytes(live.DiskFree)} free of {Monitoring.Fmt.Bytes(live.DiskTotal)} ({live.DiskFreePercent:0.0}%)");
            Check("live-process-list", live.TopProcesses.Count > 0,
                $"{live.TopProcesses.Count} rows, top={live.TopProcesses.FirstOrDefault()?.Name} {live.TopProcesses.FirstOrDefault()?.CpuPercent:0.0}%");
            Check("live-gpu-probe", true,
                live.GpuAvailable
                    ? $"GPU {live.GpuPercent:0.0}%, dedicated {Monitoring.Fmt.Bytes(live.GpuDedicatedBytes)}"
                    : "GPU counters unavailable - degraded, not fatal");
        }

        var report = new
        {
            generatedAt = DateTime.Now.ToString("s"),
            pet = new { spec.Id, spec.DisplayName, spec.Renderer, cell = new { spec.CellWidth, spec.CellHeight }, spec.Columns, spec.AtlasRows },
            atlas = new { width = atlas.Atlas.PixelWidth, height = atlas.Atlas.PixelHeight, dpi = atlas.Atlas.DpiX },
            tracks = spec.Tracks.Select(t => new { t.Row, t.Name, frames = t.Frames.Length, t.Loop, t.Fallback, totalMs = t.TotalMs }),
            failed,
            checks,
        };
        // ---------- OS-level task monitor ----------
        static Monitoring.HogRow Fam(string name, double cpu, ulong io = 0, int count = 1) =>
            new(name, count, 100_000_000UL, cpu, new[] { 1 }, io);

        var tmMusic = new Performance.TaskMonitor { CpuActivePercent = 5, LongTaskMs = 60_000, IdleGraceMs = 30_000 };
        var musicEvents = 0;
        for (var i = 0; i < 120; i++)
            musicEvents += tmMusic.Observe(new[] { Fam("cloudmusic", 1.2) }, 1_000_000 + i * 60_000).Count;
        Check("ostask-idle-app-never-fires", musicEvents == 0, "听歌 2 小时（CPU 1.2%）触发次数: " + musicEvents);

        var tmCpu = new Performance.TaskMonitor { CpuActivePercent = 5, IoActiveBytesPerSecond = 5_000_000, LongTaskMs = 60_000, IdleGraceMs = 30_000 };
        Performance.OsTaskFinished? cpuDone = null;
        for (var i = 0; i < 7; i++)
            foreach (var e in tmCpu.Observe(new[] { Fam("cl", 40) }, 5_000_000 + i * 10_000)) cpuDone = e;
        for (var i = 0; i < 4; i++)
            foreach (var e in tmCpu.Observe(new[] { Fam("cl", 0.3) }, 5_070_000 + i * 10_000)) cpuDone = e;
        Check("ostask-cpu-task-fires",
            cpuDone is { Name: "cl", How: Performance.OsTaskEnd.Stopped } && cpuDone.ActiveMs >= 60_000,
            $"CPU 任务: {cpuDone?.Name} {cpuDone?.ActiveMs}ms {cpuDone?.How}")

        ;
        var tmIo = new Performance.TaskMonitor { CpuActivePercent = 5, IoActiveBytesPerSecond = 5_000_000, LongTaskMs = 60_000, IdleGraceMs = 30_000 };
        Performance.OsTaskFinished? copyDone = null;
        for (var i = 0; i < 9; i++)
            foreach (var e in tmIo.Observe(new[] { Fam("explorer", 0.5, 20_000_000) }, 6_000_000 + i * 10_000)) copyDone = e;
        for (var i = 0; i < 5; i++)     // the copy finished: IO back to normal
            foreach (var e in tmIo.Observe(new[] { Fam("explorer", 0.5, 0) }, 6_090_000 + i * 10_000)) copyDone = e;
        Check("ostask-io-copy-fires",
            copyDone is { Name: "explorer" } && copyDone.PeakIoBytesPerSecond >= 20_000_000,
            $"纯 I/O 复制: {copyDone?.Name} {copyDone?.ActiveMs}ms io={copyDone?.PeakIoBytesPerSecond}B/s");

        // A fast copy (NVMe: 3 GB in 20 s) is shorter than the duration rule, but the user
        // wants to hear about it -> the total-bytes rule must fire.
        var tmFast = new Performance.TaskMonitor
        {
            CpuActivePercent = 5, IoActiveBytesPerSecond = 5_000_000,
            LongTaskMs = 180_000, BigTotalIoBytes = 1_000_000_000, MinTaskMs = 5_000, IdleGraceMs = 30_000,
        };
        Performance.OsTaskFinished? fastDone = null;
        for (var i = 0; i < 6; i++)     // 5 s of work at 400 MB/s => ~2 GB, still far below the 3 min rule
            foreach (var e in tmFast.Observe(new[] { Fam("explorer", 0.4, 400_000_000) }, 10_000_000 + i * 1_000)) fastDone = e;
        for (var i = 0; i < 3; i++)     // copy finished, disk back to idle
            foreach (var e in tmFast.Observe(new[] { Fam("explorer", 0.4, 0) }, 10_010_000 + i * 20_000)) fastDone = e;
        Check("ostask-fast-big-copy-fires",
            fastDone is { Name: "explorer" } && fastDone.TotalIoBytes >= 1_000_000_000,
            $"快复制: {fastDone?.Name} {fastDone?.ActiveMs}ms 总量={fastDone?.TotalIoBytes}B")

        ;
        // A tiny burst (100 MB) must stay silent, otherwise every file save would announce.
        var tmTiny = new Performance.TaskMonitor
        {
            CpuActivePercent = 5, IoActiveBytesPerSecond = 5_000_000,
            LongTaskMs = 180_000, BigTotalIoBytes = 1_000_000_000, MinTaskMs = 5_000, IdleGraceMs = 10_000,
        };
        var tinyEvents = 0;
        for (var i = 0; i < 2; i++) tinyEvents += tmTiny.Observe(new[] { Fam("notepad", 0.4, 50_000_000) }, 11_000_000 + i * 1_000).Count;
        for (var i = 0; i < 3; i++) tinyEvents += tmTiny.Observe(new[] { Fam("notepad", 0.4, 0) }, 11_020_000 + i * 20_000).Count;
        Check("ostask-small-io-burst-silent", tinyEvents == 0, "100 MB 的小读写不提醒: " + tinyEvents);
        var tmShort = new Performance.TaskMonitor { CpuActivePercent = 5, LongTaskMs = 60_000, IdleGraceMs = 10_000 };
        tmShort.Observe(new[] { Fam("tmp", 40) }, 7_000_000);
        var shortEvents = tmShort.Observe(new[] { Fam("tmp", 0) }, 7_020_000).Count;
        Check("ostask-short-task-silent", shortEvents == 0, "20 秒任务不提醒: " + shortEvents);

        var tmSys = new Performance.TaskMonitor { CpuActivePercent = 5, LongTaskMs = 10_000, IdleGraceMs = 5_000 };
        var sysEvents = 0;
        for (var i = 0; i < 6; i++) sysEvents += tmSys.Observe(new[] { Fam("svchost", 90) }, 8_000_000 + i * 10_000).Count;
        for (var i = 0; i < 3; i++) sysEvents += tmSys.Observe(Array.Empty<Monitoring.HogRow>(), 8_060_000 + i * 10_000).Count;
        Check("ostask-ignores-system-daemons", sysEvents == 0, "svchost 90% CPU 也不报: " + sysEvents);
        Check("ostask-monitor-set",
            !Performance.TaskMonitor.IsMonitored("svchost") &&
            !Performance.TaskMonitor.IsMonitored("DeepSeek Harness") &&
            Performance.TaskMonitor.IsMonitored("explorer") &&
            Performance.TaskMonitor.IsMonitored("ffmpeg"),
            "系统进程不报、explorer/ffmpeg 可报");

        // ---------- app in front too long ----------
        var fw = new Interaction.ForegroundWatch { ThresholdMs = 60_000, CooldownMs = 600_000, MaxSampleDeltaMs = 1_000 };
        for (var i = 0; i < 70; i++) fw.Sample("cloudmusic", 9_000_000 + i * 1_000);
        fw.Sample("explorer", 9_070_000);
        var remind = fw.TakeReminder(9_071_000);
        var again = fw.TakeReminder(9_072_000);
        Check("apptoolong-fires-once-with-cooldown",
            remind is { Name: "cloudmusic" } && again is null,
            "前台 69 秒 -> " + (remind?.Name ?? "none") + "; 立刻再取 -> " + (again?.Name ?? "none"));

        var fwShort = new Interaction.ForegroundWatch { ThresholdMs = 60_000, MaxSampleDeltaMs = 1_000 };
        for (var i = 0; i < 30; i++) fwShort.Sample("Code", 9_100_000 + i * 1_000);
        Check("apptoolong-not-yet", fwShort.TakeReminder(9_140_000) is null, "前台 29 秒不提醒");
        // ---------- P1: memory hog aggregation by app family ----------
        var sample = new[]
        {
            new Monitoring.ProcessRow("Code", 100, 1.0, 900_000_000UL),
            new Monitoring.ProcessRow("Code", 101, 3.0, 500_000_000UL),
            new Monitoring.ProcessRow("Code", 102, 0.5, 100_000_000UL),
            new Monitoring.ProcessRow("chrome", 200, 0.0, 300_000_000UL),
            new Monitoring.ProcessRow("svchost", 300, 0.0, 2_000_000_000UL),
        };
        var families = Monitoring.MonitorService.AggregateByName(sample, 6);
        var codeFamily = families.FirstOrDefault(f => f.Name == "Code");
        Check("hog-aggregation-by-name",
            codeFamily is { Count: 3, WorkingSet: 1_500_000_000UL, MaxCpuPercent: 3.0 } &&
            families[0].Name == "svchost",
            $"Code 家族: {codeFamily?.Count} 个 / {codeFamily?.WorkingSet} B / maxCpu={codeFamily?.MaxCpuPercent}; 最大户={families[0].Name}");

        static PetState S(string? phase, string? animation = null) => new(animation, null, phase, true, null, null);
        var twShort = new Interaction.TaskWatch { LongTaskThresholdMs = 60_000 };
        var shortA = twShort.Observe(S("tool"), 1_000_000);
        var shortB = twShort.Observe(S(null), 1_030_000);
        Check("taskwatch-short-turn-silent", shortA is null && shortB is null,
            $"30s 的短任务不提醒 (busy during={twShort.IsBusy})");

        var twDone = new Interaction.TaskWatch { LongTaskThresholdMs = 60_000 };
        twDone.Observe(S("tool"), 1_000_000);
        twDone.Observe(S("thinking"), 1_040_000);
        var finished = twDone.Observe(S(null), 1_090_000);
        Check("taskwatch-long-completed",
            finished is { Outcome: Interaction.TaskOutcome.Completed, DurationMs: 90_000 },
            $"90s 长任务 -> {finished?.Outcome} {finished?.DurationMs}ms");

        var twFail = new Interaction.TaskWatch { LongTaskThresholdMs = 60_000 };
        twFail.Observe(S("tool"), 2_000_000);
        twFail.Observe(S("tool", "failed"), 2_030_000);
        var failedTask = twFail.Observe(S(null), 2_100_000);
        Check("taskwatch-long-failed",
            failedTask is { Outcome: Interaction.TaskOutcome.Failed },
            $"跑到一半出现 failed -> {failedTask?.Outcome} {failedTask?.DurationMs}ms");

        var twReset = new Interaction.TaskWatch { LongTaskThresholdMs = 60_000 };
        twReset.Observe(S("tool"), 3_000_000);
        twReset.Observe(S("review"), 3_020_000);
        twReset.Observe(S("waiting"), 3_050_000);
        var afterSwitches = twReset.Observe(S(null), 3_075_000);
        Check("taskwatch-phase-switch-keeps-clock",
            afterSwitches is { DurationMs: 75_000 },
            "阶段切换不重置计时: " + (afterSwitches?.DurationMs.ToString() ?? "none") + "ms");

        var twOffline = new Interaction.TaskWatch { LongTaskThresholdMs = 10_000 };
        twOffline.Observe(S("tool"), 4_000_000);
        var offlineEnd = twOffline.Observe(null, 4_060_000);
        Check("taskwatch-offline-ends-silently", offlineEnd is null && !twOffline.IsBusy,
            "DSH 断开时结束计时但不猜成败");

        Check("taskwatch-phase-labels",
            Interaction.TaskWatch.PhaseLabel("tool") == "工具中" &&
            Interaction.TaskWatch.PhaseLabel("thinking") == "思考中" &&
            Interaction.TaskWatch.PhaseLabel(null) == "空闲",
            Interaction.TaskWatch.PhaseLabel("tool") + "/" + Interaction.TaskWatch.PhaseLabel("thinking") + "/" + Interaction.TaskWatch.PhaseLabel(null));

        // ---------- P1: memory guard ----------
        Check("memguard-tiers",
            Performance.MemoryGuard.TierForFreePercent(30) == 0 &&
            Performance.MemoryGuard.TierForFreePercent(12) == 1 &&
            Performance.MemoryGuard.TierForFreePercent(7) == 2 &&
            Performance.MemoryGuard.TierForFreePercent(3) == 3,
            "30/12/7/3% -> 0/1/2/3");
        var killOk = Performance.MemoryGuard.CanKill("notepad", 4242, 0, out _);
        var killSelf = Performance.MemoryGuard.CanKill("WhaleDesk", 100, out var selfReason);
        var killDsh = Performance.MemoryGuard.CanKill("DeepSeek Harness", 300, out var dshReason);
        var killShell = Performance.MemoryGuard.CanKill("explorer", 301, out var shellReason);
        var killLowPid = Performance.MemoryGuard.CanKill("whatever", 2, out var lowReason);
        var busyRefused = Performance.MemoryGuard.CanKill("code", 5000, 40, out var busyReason);
        var idleAllowed = Performance.MemoryGuard.CanKill("code", 5000, 0.1, out _);
        var toolBusy = Performance.MemoryGuard.CanKill("Codex", 5001, 2, out var toolReason);
        var toolIdle = Performance.MemoryGuard.CanKill("Codex", 5001, 0.1, out _);
        Check("memguard-refuses-busy",
            !busyRefused && idleAllowed && !toolBusy && toolIdle,
            $"忙的进程拒绝={!busyRefused}（{busyReason}）；闲的允许={idleAllowed}；Codex 跑任务={!toolBusy}（{toolReason}）；Codex 空闲={toolIdle}");
        Check("memguard-refuses-critical",
            killOk && !killSelf && !killDsh && !killShell && !killLowPid,
            $"允许普通进程={killOk}; 自己={selfReason}; DSH={dshReason}; explorer={shellReason}; PID2={lowReason}");
        // ---------- P2: download folder watch ----------
        var fw2 = new Monitoring.FolderWatch { StableMs = 4_000, MinBytes = 1_000_000, FreshMinutes = 60 };
        var fresh = DateTime.UtcNow;
        var growing = fw2.Sample(new[] { ("/x/a.iso", 2_000_000L, fresh) }, 1_000_000).Count;
        var stillGrowing = fw2.Sample(new[] { ("/x/a.iso", 3_000_000L, fresh) }, 1_002_000).Count;
        var stableButYoung = fw2.Sample(new[] { ("/x/a.iso", 3_000_000L, fresh) }, 1_004_000).Count;
        var completedList = fw2.Sample(new[] { ("/x/a.iso", 3_000_000L, fresh) }, 1_009_000);
        var duplicate = fw2.Sample(new[] { ("/x/a.iso", 3_000_000L, fresh) }, 1_012_000).Count;
        var tiny = fw2.Sample(new[] { ("/x/b.tmp", 200_000L, fresh) }, 1_020_000).Count + fw2.Sample(new[] { ("/x/b.tmp", 200_000L, fresh) }, 1_030_000).Count;
        var stale = new Monitoring.FolderWatch { StableMs = 4_000, MinBytes = 1_000_000, FreshMinutes = 10 };
        stale.MarkKnown("/x/old.png");
        stale.Sample(new[] { ("/x/old.png", 5_000_000L, DateTime.UtcNow.AddDays(-120)) }, 2_000_000);
        stale.Sample(new[] { ("/x/old.png", 5_000_000L, DateTime.UtcNow.AddDays(-120)) }, 2_006_000);
        var staleEvents = stale.Sample(new[] { ("/x/a2.bin", 9_000_000L, DateTime.UtcNow.AddDays(-30)) }, 2_020_000).Count;
        Check("folderwatch-reports-once-when-stable",
            growing == 0 && stillGrowing == 0 && stableButYoung == 0 && completedList.Count == 1 && duplicate == 0 && tiny == 0,
            $"增长中=0, 稳定4s后={completedList.Count}, 重复={duplicate}, 小文件={tiny}")

        ;

        // ---------- P2: flaky-link detector ----------
        var hw2 = new Monitoring.HealthWatch { WindowMs = 60_000, FailuresToWarn = 5, CooldownMs = 600_000 };
        var warns = 0;
        for (var i = 0; i < 4; i++) if (hw2.Record(true, 2_000_000 + i * 1_000)) warns++;
        var warnAt5 = hw2.Record(true, 2_004_000);
        var warnAgain = hw2.Record(true, 2_005_000);
        hw2.Record(false, 2_006_000);
        for (var i = 0; i < 4; i++) hw2.Record(true, 2_007_000 + i * 1_000);
        var withinCooldown = hw2.Record(true, 2_012_000);   // burst again, but the notice cooldown has not passed
        var hw3 = new Monitoring.HealthWatch { WindowMs = 60_000, FailuresToWarn = 5, CooldownMs = 1_000 };
        for (var i = 0; i < 4; i++) hw3.Record(true, 3_000_000 + i * 1_000);
        var firstBurst = hw3.Record(true, 3_004_000);
        hw3.Record(false, 3_005_000);
        for (var i = 0; i < 4; i++) hw3.Record(true, 3_100_000 + i * 1_000);
        var afterReset = hw3.Record(true, 3_104_000);
        Check("healthwatch-warns-once-per-burst",
            warns == 0 && warnAt5 && !warnAgain && !withinCooldown && firstBurst && afterReset,
            $"4次不警={warns == 0}, 第5次警={warnAt5}, 冷却内不重复={!warnAgain && !withinCooldown}, 成功后新突发会再警={afterReset}")

        ;

        Check("disktier-percent-and-absolute",
            Performance.DiskGuard.Tier(60_000_000_000UL, 200_000_000_000UL) == 0 &&
            Performance.DiskGuard.Tier(25_000_000_000UL, 200_000_000_000UL) == 1 &&
            Performance.DiskGuard.Tier(15_000_000_000UL, 200_000_000_000UL) == 2 &&
            Performance.DiskGuard.Tier(8_000_000_000UL, 200_000_000_000UL) == 3 &&
            Performance.DiskGuard.Tier(12_000_000_000UL, 40_000_000_000UL) == 1 &&
            Performance.DiskGuard.Tier(2_000_000_000UL, 40_000_000_000UL) == 3,
            "30%/12.5%/7.5%/4%/30%(绝对)/5% -> 0/1/2/3/1/3");

        // ---------- P3: drag & drop formatting ----------
        var oneFile = new[] { @"C:\Users\me\Downloads\report.pdf" };
        var twoFiles = new[] { @"C:\Users\me\a.txt", @"C:\Users\me\b.txt" };
        Check("droptargets-preview",
            Interaction.DropTargets.Preview(oneFile, null) == "「report.pdf」" &&
            Interaction.DropTargets.Preview(twoFiles, null).EndsWith("等 2 个") &&
            Interaction.DropTargets.Preview(Array.Empty<string>(), "hello") == "文字「hello」" &&
            Interaction.DropTargets.Preview(Array.Empty<string>(), null) == "没拿到内容",
            Interaction.DropTargets.Preview(oneFile, null) + " / " + Interaction.DropTargets.Preview(twoFiles, null) + " / " + Interaction.DropTargets.Preview(Array.Empty<string>(), "hello"));
        Check("droptargets-clipboard-text",
            Interaction.DropTargets.ClipboardText(twoFiles, null) == "C:\\Users\\me\\a.txt\r\nC:\\Users\\me\\b.txt" &&
            Interaction.DropTargets.ClipboardText(Array.Empty<string>(), "  hi  ") == "  hi  " &&
            Interaction.DropTargets.NameOf(@"C:\x\y\") == "y",
            "多路径 CRLF 连接 / 文字原样 / 目录名去尾斜杠");

        // ---------- P3: quick launch store ----------
        var qaRoot = Path.Combine(opts.BuildDir, "verify", "qa");
        try
        {
            Directory.CreateDirectory(qaRoot);
            var cfg = Path.Combine(qaRoot, "quick-launch.json");
            var store = Interaction.LauncherStore.Load(cfg);
            var defaultsOk = store.Visible().Count >= 4 && store.Visible().Any(l => l.Name.Contains("资源管理器"));
            store.Add(new Interaction.Launcher("测试项", "notepad.exe", null, null));
            store.Save(cfg);
            var reloadedLaunchers = Interaction.LauncherStore.Load(cfg);
            Check("launcher-store-roundtrip",
                defaultsOk && File.Exists(cfg) && reloadedLaunchers.Visible().Any(l => l.Name == "测试项"),
                "默认 " + store.Visible().Count + " 项、落盘、重载含测试项=" + reloadedLaunchers.Visible().Any(l => l.Name == "测试项"));
        }
        finally
        {
            try { Directory.Delete(qaRoot, true); } catch { }
        }
        // ---------- P3: launcher entries can be removed; shells start in the profile ----------
        var qa2 = Interaction.LauncherStore.Defaults();
        var ps = qa2.Visible().FirstOrDefault(l => l.Target.StartsWith("powershell", StringComparison.OrdinalIgnoreCase));
        var profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = new Interaction.Launcher("待删除", "notepad.exe");
        qa2.Add(temp);
        var before = qa2.Visible().Count;
        qa2.Remove(temp);
        Check("launcher-remove-and-shell-cwd",
            before > qa2.Visible().Count &&
            ps is not null && ps.WorkingDirectory == profilePath,
            $"移除前 {before} 项、移除后 {qa2.Visible().Count} 项; PowerShell 起始目录={ps?.WorkingDirectory ?? "未设置"}");

        // ---------- P3: copy-to-folder auto rename + legacy launcher migration ----------
        var copyRoot = Path.Combine(opts.BuildDir, "verify", "copy");
        try
        {
            var srcDir = Path.Combine(copyRoot, "src");
            var dstDir = Path.Combine(copyRoot, "dst");
            Directory.CreateDirectory(srcDir);
            Directory.CreateDirectory(dstDir);
            var sourceFile = Path.Combine(srcDir, "report.txt");
            File.WriteAllText(sourceFile, "hello");
            File.WriteAllText(Path.Combine(dstDir, "report.txt"), "existing");   // the clash
            var plan = Interaction.FileCopyHelper.Plan(new[] { sourceFile }, dstDir);
            var copiedCount = Interaction.FileCopyHelper.Execute(plan, out var copyError);
            var expected = Path.Combine(dstDir, "report (2).txt");
            Check("filecopy-renames-on-conflict",
                plan.Count == 1 && plan[0].Destination == expected && copiedCount == 1 && File.Exists(expected),
                $"冲突 -> {Path.GetFileName(plan.FirstOrDefault().Destination)} copied={copiedCount} err='{copyError}'");

            var legacy = Path.Combine(copyRoot, "legacy.json");
            File.WriteAllText(legacy, "[{\"Name\":\"PowerShell\",\"Target\":\"powershell.exe\"}]");
            var migrated = Interaction.LauncherStore.Load(legacy);
            var psEntry = migrated.Visible().FirstOrDefault(l => l.Target == "powershell.exe");
            Check("launcher-migrates-legacy-cwd",
                psEntry is not null && psEntry.WorkingDirectory == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "旧配置里的 PowerShell 起始目录 -> " + (psEntry?.WorkingDirectory ?? "null"));
        }
        finally
        {
            try { Directory.Delete(copyRoot, true); } catch { }
        }
        // ---------- P3: space analysis classification ----------
        var cacheVerdict = Cleanup.DirectoryAnalyzer.Classify("Code Cache", true, out _);
        var gpuVerdict = Cleanup.DirectoryAnalyzer.Classify("GPUCache", true, out _);
        var logFileVerdict = Cleanup.DirectoryAnalyzer.Classify("app.log", false, out _);
        var windowsVerdict = Cleanup.DirectoryAnalyzer.Classify("Windows", true, out _);
        var docsVerdict = Cleanup.DirectoryAnalyzer.Classify("Documents", true, out _);
        var pfVerdict = Cleanup.DirectoryAnalyzer.Classify("Program Files", true, out _);
        var unknownVerdict = Cleanup.DirectoryAnalyzer.Classify("MyRandomFolder", true, out _);
        var oldVerVerdict = Cleanup.DirectoryAnalyzer.Classify("app-1.2.3", true, out _);
        Check("analyzer-classifies-by-rebuildability",
            cacheVerdict == Cleanup.CleanVerdict.SafeCache &&
            gpuVerdict == Cleanup.CleanVerdict.SafeCache &&
            logFileVerdict == Cleanup.CleanVerdict.SafeCache &&
            oldVerVerdict == Cleanup.CleanVerdict.SafeCache &&
            windowsVerdict == Cleanup.CleanVerdict.SystemFiles &&
            docsVerdict == Cleanup.CleanVerdict.PersonalFiles &&
            pfVerdict == Cleanup.CleanVerdict.CleanInApp &&
            unknownVerdict == Cleanup.CleanVerdict.Unknown,
            $"缓存={cacheVerdict} 旧版={oldVerVerdict} 系统={windowsVerdict} 个人={docsVerdict} 已装程序={pfVerdict} 未知={unknownVerdict}");

        var analysisRoot = Path.Combine(opts.BuildDir, "verify", "analyze");
        try
        {
            var cacheDir = Path.Combine(analysisRoot, "Cache");
            var keepDir = Path.Combine(analysisRoot, "MyPhotos");
            Directory.CreateDirectory(cacheDir);
            Directory.CreateDirectory(keepDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "blob.bin"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(keepDir, "IMG_0001.jpg"), new byte[8192]);
            var rep = Cleanup.DirectoryAnalyzer.Analyze(analysisRoot, 10, 8000);
            var cacheItem = rep?.Items.FirstOrDefault(i => i.Name == "Cache");
            var keepItem = rep?.Items.FirstOrDefault(i => i.Name == "MyPhotos");
            Check("analyzer-reports-sizes-and-verdicts",
                rep is not null && cacheItem is { Verdict: Cleanup.CleanVerdict.SafeCache } &&
                keepItem is { Verdict: Cleanup.CleanVerdict.Unknown } &&
                Cleanup.DirectoryAnalyzer.Reclaimable(rep) >= 4096,
                $"总={Monitoring.Fmt.Bytes(rep?.Total ?? 0)} Cache={cacheItem?.Verdict} MyPhotos={keepItem?.Verdict} 可回收={Monitoring.Fmt.Bytes(rep is null ? 0UL : Cleanup.DirectoryAnalyzer.Reclaimable(rep))}");
        }
        finally
        {
            try { Directory.Delete(analysisRoot, true); } catch { }
        }

        // ---------- P3: "in use" and "keep" override the analyzer ----------
        var keeplistRoot = Path.Combine(opts.BuildDir, "verify", "keeplist");
        try
        {
            var kl = new Interaction.KeepList();
            kl.Add("Google");
            kl.Add(@"D:\Games\OldGame");
            var keepPath = Path.Combine(keeplistRoot, "keep-list.json");
            kl.Save(keepPath);
            var kl2 = Interaction.KeepList.Load(keepPath);
            Check("keeplist-roundtrip",
                kl2.Contains("Google") && kl2.Contains(@"D:\Games\OldGame\x.exe") && kl2.Contains("Google") && !kl2.Contains("NetEase"),
                "落盘/重载/按名与按路径前缀匹配");

            // An AppData-like tree so the context rules apply.
            var appDataRoot = Path.Combine(keeplistRoot, "AppData", "Local");
            Directory.CreateDirectory(Path.Combine(appDataRoot, "Google", "Chrome"));
            File.WriteAllBytes(Path.Combine(appDataRoot, "Google", "Chrome", "c.bin"), new byte[2048]);
            Directory.CreateDirectory(Path.Combine(appDataRoot, "NetEase"));
            File.WriteAllBytes(Path.Combine(appDataRoot, "NetEase", "a.dat"), new byte[1024]);

            var decided = Cleanup.DirectoryAnalyzer.Analyze(appDataRoot, 10, 5000,
                new Cleanup.AnalysisContext(null, kl2));
            var googleItem = decided?.Items.FirstOrDefault(i => i.Name == "Google");
            Check("analyzer-honours-keep-list",
                googleItem is { Verdict: Cleanup.CleanVerdict.Kept },
                "保留清单里的 Google -> " + (googleItem?.Verdict.ToString() ?? "null"));

            var evidence = new[] { new Performance.InUseEvidence("NetEase", "cloudmusic", @"C:\Program Files\NetEase\CloudMusic\cloudmusic.exe") };
            var inUse = Cleanup.DirectoryAnalyzer.Analyze(appDataRoot, 10, 5000,
                new Cleanup.AnalysisContext(evidence, null));
            var neteaseItem = inUse?.Items.FirstOrDefault(i => i.Name == "NetEase");
            Check("analyzer-detects-in-use-app",
                neteaseItem is { Verdict: Cleanup.CleanVerdict.InUseApp },
                "正在运行的网易云 -> " + (neteaseItem?.Verdict.ToString() ?? "null") +
                (neteaseItem is null ? "" : " / " + neteaseItem.Reason));
        }
        finally
        {
            try { Directory.Delete(keeplistRoot, true); } catch { }
        }
        // ---------- P3: ignore list, deep scan, guarded cleaner ----------
        var deepRoot = Path.Combine(opts.BuildDir, "verify", "deep");
        try
        {
            var appLocal = Path.Combine(deepRoot, "AppData", "Local");
            var vendor = Path.Combine(appLocal, "Vendor");
            Directory.CreateDirectory(Path.Combine(vendor, "Cache"));
            Directory.CreateDirectory(Path.Combine(vendor, "Code Cache"));
            Directory.CreateDirectory(Path.Combine(vendor, "Config"));
            File.WriteAllBytes(Path.Combine(vendor, "Cache", "blob.bin"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(vendor, "Code Cache", "c.bin"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(vendor, "Config", "settings.json"), new byte[512]);

            var deep = Cleanup.DirectoryAnalyzer.DeepScan(appLocal, 3, 40, 5000, null);
            var deepNames = deep.Select(d => d.Name).ToList();
            Check("deepscan-finds-nested-caches",
                deepNames.Contains("Cache") && deepNames.Contains("Code Cache") && !deepNames.Contains("Config") && deep.Count >= 2,
                "深挖结果: " + string.Join(" / ", deepNames));

            var ignoreList = new Interaction.IgnoreList();
            ignoreList.Add(Path.Combine(appLocal, "Vendor"));
            var ignored = Cleanup.DirectoryAnalyzer.Analyze(appLocal, 10, 5000,
                new Cleanup.AnalysisContext(null, null, Ignore: ignoreList));
            Check("ignore-list-hides-folder",
                ignored is not null && ignored.Items.All(i => i.Name != "Vendor"),
                "忽略 Vendor 后条目数=" + (ignored?.Items.Count.ToString() ?? "null"));

            var cleanerRoot = Path.Combine(Path.GetTempPath(), "whaledesk-cleaner-test");
            Directory.CreateDirectory(cleanerRoot);
            File.WriteAllBytes(Path.Combine(cleanerRoot, "keep.txt"), new byte[128]);
            var notCache = new Cleanup.AnalysisItem("Documents", cleanerRoot, 128, Cleanup.CleanVerdict.SafeCache, "forced", false);
            var notCacheDeleted = Cleanup.CacheCleaner.TryDelete(notCache, out _, out var notCacheReason);
            var cacheDir = Path.Combine(cleanerRoot, "Cache");
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(Path.Combine(cacheDir, "junk.bin"), new byte[4096]);
            var cacheItem = new Cleanup.AnalysisItem("Cache", cacheDir, 4096, Cleanup.CleanVerdict.SafeCache, "test", false);
            var cacheDeleted = Cleanup.CacheCleaner.TryDelete(cacheItem, out var cacheFreed, out var cacheReason);
            Check("cleaner-guards-and-deletes-cache",
                !notCacheDeleted && cacheDeleted && !Directory.Exists(cacheDir) && cacheFreed >= 4096,
                $"非缓存拒绝={!notCacheDeleted}({notCacheReason}) 缓存删除={cacheDeleted}({cacheReason}) freed={cacheFreed}");
            try { Directory.Delete(cleanerRoot, true); } catch { }
        }
        finally
        {
            try { Directory.Delete(deepRoot, true); } catch { }
        }
        // ---------- P3: sedentary reminder ----------
        var sed = new Interaction.SedentaryWatch { RemindAfterMinutes = 50, BreakIdleSeconds = 300, SnoozeMinutes = 30, ActiveIdleSeconds = 60 };
        long t0 = 10_000_000;
        Interaction.SedentaryNudge? firstNudge = null;
        for (var minute = 0; minute <= 60; minute++)
        {
            var n = sed.Sample(5, t0 + minute * 60_000L, 14);   // always active, always working hours
            if (n is not null) { firstNudge ??= n; }
        }
        var afterNudge = sed.Sample(5, t0 + 61 * 60_000L, 14);          // still inside snooze
        var afterBreak = sed.Sample(400, t0 + 120 * 60_000L, 14);       // 400 s idle = real break
        var offHours = sed.Sample(5, t0 + 200 * 60_000L, 3);            // 3am: stay quiet
        Check("sedentary-nudges-once-and-resets-on-break",
            firstNudge is { ActiveMinutes: >= 50 } && afterNudge is null && afterBreak is null && offHours is null,
            $"首次提醒 {firstNudge?.ActiveMinutes} 分钟; 冷却期内={afterNudge is null}; 休息后清零={afterBreak is null}; 凌晨不提醒={offHours is null}");

        // ---------- P3: clipboard history ----------
        var clipPath = Path.Combine(opts.BuildDir, "verify", "clip-history.json");
        var hist = new Interaction.ClipboardHistoryStore { Capacity = 5 };
        hist.Add("first", DateTime.Now);
        hist.Add("first", DateTime.Now);          // exact repeat: ignored
        hist.Add("second", DateTime.Now);
        hist.Add("first", DateTime.Now);          // moves back to the top, not duplicated
        for (var i = 0; i < 8; i++) hist.Add("filler-" + i, DateTime.Now);
        var deduped = hist.Entries.Count == 5 && hist.Entries[0].Text == "filler-7" && hist.Entries.Count(e => e.Text == "first") == 0;
        hist.Save(clipPath);
        var hist2 = Interaction.ClipboardHistoryStore.Load(clipPath);
        var roundTripped = hist2.Entries.Count == 5 && hist2.Entries[0].Text == "filler-7";
        hist2.Clear();
        Check("clipboard-history-dedupes-and-caps",
            deduped && roundTripped && hist2.Entries.Count == 0,
            $"去重/置顶/上限 5={deduped} 往返={roundTripped} 清空={hist2.Entries.Count == 0}");
        try { File.Delete(clipPath); } catch { }
        // ---------- P4: plugin inventory scan ----------
        var pluginRoot = Path.Combine(opts.BuildDir, "verify", "plugins");
        try
        {
            var modules = Path.Combine(pluginRoot, "profiles", "desktop", "node_modules");
            void MakePlugin(string scopeOrName, string name, string version)
            {
                var dir = scopeOrName.StartsWith("@") ? Path.Combine(modules, scopeOrName, name) : Path.Combine(modules, name);
                Directory.CreateDirectory(dir);
                var fullName = scopeOrName.StartsWith("@") ? scopeOrName + "/" + name : name;
                File.WriteAllText(Path.Combine(dir, "package.json"),
                    "{\"name\":\"" + fullName + "\",\"version\":\"" + version + "\"}");
            }
            MakePlugin("@linxin666", "dsh-pet", "1.2.3");
            MakePlugin("@linxin666", "dsh-usage", "0.9.1");
            MakePlugin("@linxin666", "dsh-session-archive", "2.0.0");
            MakePlugin("", "lodash", "4.17.21");

            var plugins = Monitoring.PluginHelper.Scan(pluginRoot);
            var pet = plugins.FirstOrDefault(p => p.Name == "@linxin666/dsh-pet");
            var missingAll = Monitoring.PluginHelper.Missing(plugins);
            var onlyPet = plugins.Where(p => p.Name == "@linxin666/dsh-pet").ToList();
            var missingOne = Monitoring.PluginHelper.Missing(onlyPet);
            Check("pluginhelper-scans-and-flags-dependencies",
                plugins.Count == 3 && plugins.All(p => p.Name.Contains("dsh")) &&
                pet is { Version: "1.2.3", IsDependency: true } &&
                missingAll.Count == 0 && missingOne.Count == 1 && missingOne[0].Name == "@linxin666/dsh-usage",
                $"扫描到 {plugins.Count} 个插件；pet={pet?.Version}/{pet?.IsDependency}；缺全部={missingAll.Count}；只装 pet 时缺={missingOne.Count}");
        }
        finally
        {
            try { Directory.Delete(pluginRoot, true); } catch { }
        }
        // ---------- clipboard image history ----------
        var imgRoot = Path.Combine(Path.GetTempPath(), "whaledesk clip img " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(imgRoot);
        try
        {
            var imgStore = new Interaction.ClipboardHistoryStore { Capacity = 3, MaxImages = 2 };
            var files = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var file = Path.Combine(imgRoot, $"img{i}.png");
                File.WriteAllBytes(file, new byte[] { 1, 2, 3, (byte)i });
                files.Add(file);
                imgStore.AddImage(file, 16, 16, DateTime.Now.AddSeconds(i));
            }
            var imageEntries = imgStore.Entries.Where(e => e.IsImage).ToList();
            var previewOk = imageEntries.Any(e => e.Preview.Contains("图片"));
            var prunedOk = !File.Exists(files[0]);
            imgStore.Save(Path.Combine(imgRoot, "history.json"));
            var imgReloaded = Interaction.ClipboardHistoryStore.Load(Path.Combine(imgRoot, "history.json"));
            Check("clipboard-image-store",
                imageEntries.Count == 2 && previewOk && prunedOk &&
                imgReloaded.Entries.Count(e => e.IsImage) == 2 &&
                imgReloaded.Entries.Any(e => e.IsImage && e.ImageWidth == 16),
                $"保留={imageEntries.Count} 预览=「{imageEntries.FirstOrDefault()?.Preview}」 旧文件已删={prunedOk} 重载图片={imgReloaded.Entries.Count(e => e.IsImage)}");
        }
        finally
        {
            try { Directory.Delete(imgRoot, true); } catch { }
        }
        // ---------- auto update: version comparison ----------
        Check("update-version-compare",
            UpdateChecker.IsNewer("0.20.0", "v0.21.0") &&
            !UpdateChecker.IsNewer("0.21.0", "v0.21.0") &&
            !UpdateChecker.IsNewer("0.21.0", "0.20.9") &&
            !UpdateChecker.IsNewer("0.21.0", "not-a-version") &&
            UpdateChecker.ParseTag("v1.2.3")?.ToString() == "1.2.3" &&
            UpdateChecker.ParseTag("v1.2.3-beta")?.ToString() == "1.2.3",
            "v 前缀/无前缀/预发布标签都能比较，乱码标签不触发更新");
        // ---------- P4 extras: cursor-follow policy ----------
        var farDecision = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 60, 100, 100, 300, 300, 1500, 700, 0, 0, 1920, 1030);
        var nearDecision = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 60, 100, 100, 300, 300, 240, 240, 0, 0, 1920, 1030);
        var busyDecision = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 3, 100, 100, 300, 300, 1500, 700, 0, 0, 1920, 1030);
        var offDecision = Interaction.CursorFollowPolicy.Decide(false, false, false, false, 60, 100, 100, 300, 300, 1500, 700, 0, 0, 1920, 1030);
        var edgeDecision = Interaction.CursorFollowPolicy.Decide(true, false, false, false, 60, 0, 400, 300, 300, 2, 400, 0, 0, 1920, 1030);
        Check("cursorfollow-policy",
            farDecision.Move && farDecision.Dx > 0 && Math.Abs(farDecision.Dx) <= 8 &&
            !nearDecision.Move && !busyDecision.Move && !offDecision.Move && !edgeDecision.Move,
            $"远处={farDecision.Move}({farDecision.Dx},{farDecision.Dy}) 近处={nearDecision.Why} 忙碌={busyDecision.Why} 关闭={offDecision.Why} 边缘={edgeDecision.Why}");

        // ---------- P4 extras: focus quiet policy ----------
        Check("focuspolicy-quiet-kinds",
            !Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.IdleChatter, true) &&
            !Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.EasterEgg, true) &&
            Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.Bubble, true) &&
            Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.Reminder, true) &&
            Interaction.FocusPolicy.ShouldSpeak(Interaction.SpeechKind.IdleChatter, false) &&
            Interaction.FocusPolicy.Decorate("tool · 1m", true) == "专注中 · tool · 1m" &&
            Interaction.FocusPolicy.Decorate("tool · 1m", false) == "tool · 1m",
            "专注时闲聊/彩蛋静音，气泡与提醒保留，状态条加前缀");
        // ---------- P2: token usage ledger parsing ----------
        const string ledger =
            "{\"version\":1,\"days\":{" +
            "\"2026-10-17\":{\"acc\":{\"m1\":{\"inputTokens\":1000,\"outputTokens\":200,\"cacheReadTokens\":5000,\"calls\":3,\"cost\":0.5}," +
            "\"m2\":{\"inputTokens\":500,\"outputTokens\":100,\"cacheReadTokens\":1000,\"calls\":2,\"cost\":0.25}}}," +
            "\"2026-10-16\":{\"acc\":{\"m1\":{\"inputTokens\":9,\"outputTokens\":9,\"cacheReadTokens\":9,\"calls\":9,\"cost\":9.0}}}}}";
        var usage = Monitoring.UsageStats.ParseLedger(ledger, new DateOnly(2026, 10, 17));
        var emptyUsage = Monitoring.UsageStats.ParseLedger(ledger, new DateOnly(2026, 1, 1));
        Check("usage-ledger-parsing",
            usage.InputTokens == 1500 && usage.OutputTokens == 300 && usage.CacheReadTokens == 6000 &&
            usage.Calls == 5 && Math.Abs(usage.Cost - 0.75) < 1e-9 && emptyUsage.Calls == 0,
            $"今日 入/出/缓存={usage.InputTokens}/{usage.OutputTokens}/{usage.CacheReadTokens} 调用={usage.Calls} 成本={usage.Cost:0.00}; 空日={emptyUsage.Calls}");

        // ---------- P2: directory size measurement ----------
        var sizeRoot = Path.Combine(opts.BuildDir, "verify", "sizer-test");
        try
        {
            Directory.CreateDirectory(Path.Combine(sizeRoot, "sub"));
            File.WriteAllBytes(Path.Combine(sizeRoot, "a.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(sizeRoot, "sub", "b.bin"), new byte[2000]);
            var measured = Cleanup.DirectorySizer.SizeOf(sizeRoot);
            Check("dirsizer-sums-files", measured == 3000, $"两个文件 1000+2000 -> {measured} 字节");
        }
        finally
        {
            try { Directory.Delete(sizeRoot, true); } catch { }
        }
        var dir = Path.Combine(opts.BuildDir, "verify");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "p1-selftest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        log.Info($"selftest report -> {path}");

        log.Info(failed == 0 ? "SELFTEST OK (all checks passed)" : $"SELFTEST FAILED ({failed} check(s))");
        return failed == 0 ? 0 : 4;
    }
}
