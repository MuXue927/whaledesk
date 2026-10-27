using System.IO;

namespace WhaleDesk;

/// <summary>Command line options. Paths default to the repository layout.</summary>
internal sealed class CliOptions
{
    public string Mode { get; private set; } = "help";
    public string Root { get; private set; } = DefaultRoot();
    public double PetHeight { get; private set; } = 160;
    public int LiveSeconds { get; private set; } = 10;
    public double MarginX { get; private set; } = 16;
    public double MarginY { get; private set; } = 10;
    public bool ProbeHitTest { get; private set; }
    public bool ProbeInteract { get; private set; }
    /// <summary>Force the first-run wizard (also shown automatically on first start).</summary>
    public bool ShowOnboarding { get; private set; }
    /// <summary>Never show the first-run wizard (screenshots / automated starts).</summary>
    public bool NoOnboard { get; private set; }
    public bool ProbePlugins { get; private set; }
    /// <summary>Override the DSH loopback base URL (tests / non-default ports).</summary>
    public string? DshBase { get; private set; }
    /// <summary>
    /// Testing only: keep the configured base even when it stops answering. --probe-hotplug needs
    /// this to watch the SAME endpoint die and come back; with discovery on, a second live DSH on
    /// the machine is found and the probe would silently measure that one instead.
    /// </summary>
    public bool NoDiscovery { get; private set; }
    /// <summary>End-to-end recovery test against a stub server (DSH restart / plugin reload).</summary>
    public bool ProbeHotplug { get; private set; }
    /// <summary>Log the cursor-follow / focus-quiet policies without moving anything.</summary>
    public bool ProbeExtras { get; private set; }
    public bool ProbeFollow { get; private set; }
    public bool ProbeUpdate { get; private set; }
    /// <summary>Print which command the command-based cleanups (npm/pnpm) would use.</summary>
    public bool ProbeCleanupTools { get; private set; }
    public bool ProbeClipboardImage { get; private set; }
    public bool ProbeClipboard { get; private set; }
    public bool ProbeFocus { get; private set; }
    public bool ProbeDshGuide { get; private set; }
    public bool ProbeMenu { get; private set; }
    /// <summary>--probe-menu-monitor N: move the pet to monitor N, open the menu, verify bounds.</summary>
    public bool ProbeMenuMonitor { get; private set; }
    public int ProbeMenuMonitorIndex { get; private set; } = 1;
    public bool ProbeMoveLeft { get; private set; }
    public bool ProbeMonitors { get; private set; }
    public bool ProbeHide { get; private set; }
    public bool ProbeMonitorPanel { get; private set; }
    public bool ProbeCleanupPanel { get; private set; }
    /// <summary>Simulate a drop of a path (verifies clipboard + bubble without a real drag).</summary>
    /// <summary>Probe every DSH loopback endpoint and report which features still work.</summary>
    public bool ProbeDshApi { get; private set; }
    public bool ProbeDrop { get; private set; }
    /// <summary>Open the quick-launch palette briefly and report what it built.</summary>
    public bool ProbeLauncher { get; private set; }
    /// <summary>Open the search palette briefly and report what it ranked.</summary>
    public bool ProbePalette { get; private set; }
    /// <summary>Place every popup on every monitor and report whether it landed inside.</summary>
    /// <summary>Run one Safe-tier cleanup pass and report what it reclaimed.</summary>
    public bool ProbeAutoCleanup { get; private set; }
    /// <summary>Place every popup window on every monitor and report whether it stayed inside.</summary>
    public bool ProbePopupPlacement { get; private set; }
    /// <summary>Exercise the clipboard snippet store + panel wiring headlessly.</summary>
    public bool ProbeSnippets { get; private set; }
    /// <summary>Print what the task-board watcher reads from the live ledger.</summary>
    public bool ProbeTaskBoard { get; private set; }
    /// <summary>Query npm for the profile plugins and print which ones are behind.</summary>
    public bool ProbePluginUpdates { get; private set; }
    /// <summary>Run the space analyzer on a path and print the verdicts.</summary>
    public bool ProbeAnalyze { get; private set; }
    public string? ProbeAnalyzePath { get; private set; }
    public string? ProbeDropPath { get; private set; }
    public string? ElevatedTarget { get; private set; }
    public string? ElevatedResultPath { get; private set; }
    public bool ProbeOverflow { get; private set; }
    public bool ProbePlacement { get; private set; }
    public bool ProbeResize { get; private set; }
    public bool ProbeEdges { get; private set; }
    public bool ProbeSpawn { get; private set; }
    /// <summary>Ask a running instance to exit (used by the installer to unlock the files).</summary>
    public bool Quit { get; private set; }
    /// <summary>Testing only: start even when another instance already owns the single-instance
    /// mutex. Without this, automated probes cannot run while the user's pet is open.</summary>
    public bool AllowMultiple { get; private set; }
    public bool ProbeDsh { get; private set; }
    public bool NoTray { get; private set; }
    public bool ResetPosition { get; private set; }
    public double Scale { get; private set; } = 1.0;
    public string? BubbleText { get; private set; }

    public string StatePath => Path.Combine(Root, "state", "ui-state.json");
    public string TrayIconPath => Path.Combine(Root, "assets", "tray.ico");

    public string AtlasPath => Path.Combine(Root, "assets", "atlas.png");
    public string SpecPath => Path.Combine(Root, "assets", "pet-spec.json");
    public string BuildDir => Path.Combine(Root, "build");
    public string LogPath => Path.Combine(BuildDir, "whaledesk.log");

    private static string DefaultRoot()
    {
        // bin/Debug/net9.0-windows -> ../../../../.. = whaledesk/
        var dir = AppContext.BaseDirectory;
        var probe = dir;
        for (var i = 0; i < 8 && probe is not null; i++)
        {
            if (Directory.Exists(Path.Combine(probe, "assets")) &&
                File.Exists(Path.Combine(probe, "assets", "pet-spec.json")))
                return probe;
            probe = Path.GetDirectoryName(probe.TrimEnd(Path.DirectorySeparatorChar));
        }
        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
    }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
            switch (a)
            {
                case "--render": o.Mode = "render"; break;
                case "--live": o.Mode = "live"; break;
                case "--selftest": o.Mode = "selftest"; break;
                case "--rehearsal": o.Mode = "rehearsal"; break;
                case "--elevated-clean": o.Mode = "elevated-clean"; break;
                case "--target": o.ElevatedTarget = Next(); break;
                case "--result": o.ElevatedResultPath = Next(); break;
                case "--help" or "-h": o.Mode = "help"; break;
                case "--root": o.Root = Path.GetFullPath(Next()); break;
                case "--pet-height": o.PetHeight = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--seconds": o.LiveSeconds = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--margin-x": o.MarginX = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--margin-y": o.MarginY = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--scale": o.Scale = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--bubble": o.BubbleText = Next(); break;
                case "--probe-hittest": o.ProbeHitTest = true; break;
                case "--probe-interact": o.ProbeInteract = true; break;
                case "--onboard": o.ShowOnboarding = true; break;
        case "--no-onboard": o.NoOnboard = true; break;
        case "--probe-hotplug": o.ProbeHotplug = true; break;
        case "--probe-extras": o.ProbeExtras = true; break;
        case "--probe-follow": o.ProbeFollow = true; break;
        case "--probe-update": o.ProbeUpdate = true; break;
        case "--probe-cleanup-tools": o.ProbeCleanupTools = true; break;
        case "--probe-clipboard": o.ProbeClipboard = true; break;
        case "--probe-clipboard-image": o.ProbeClipboardImage = true; break;
        case "--probe-focus": o.ProbeFocus = true; break;
        case "--probe-dsh-guide": o.ProbeDshGuide = true; break;
        case "--dsh-base":
            if (i + 1 < args.Length) { o.DshBase = args[++i]; }
            break;
        case "--no-discovery": o.NoDiscovery = true; break;
        case "--probe-plugins": o.ProbePlugins = true; break;
        case "--probe-menu": o.ProbeMenu = true; break;
        case "--probe-menu-monitor":
            o.ProbeMenuMonitor = true;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var mi)) { o.ProbeMenuMonitorIndex = mi; i++; }
            break;
                case "--probe-move-left": o.ProbeMoveLeft = true; break;
                case "--probe-monitors": o.ProbeMonitors = true; break;
                case "--probe-hide": o.ProbeHide = true; break;
        case "--probe-launcher": o.ProbeLauncher = true; break;
        case "--probe-palette": o.ProbePalette = true; break;
        case "--probe-plugin-updates": o.ProbePluginUpdates = true; break;
        case "--probe-taskboard": o.ProbeTaskBoard = true; break;
        case "--probe-snippets": o.ProbeSnippets = true; break;
        case "--probe-popup-placement": o.ProbePopupPlacement = true; break;
        case "--probe-auto-cleanup": o.ProbeAutoCleanup = true; break;
        case "--probe-dsh-api": o.ProbeDshApi = true; break;
        case "--probe-analyze":
            o.ProbeAnalyze = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) o.ProbeAnalyzePath = args[++i];
            break;
        case "--probe-drop":
            o.ProbeDrop = true;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                o.ProbeDropPath = args[++i];
            }
            break;
                case "--probe-monitor-panel": o.ProbeMonitorPanel = true; break;
                case "--probe-cleanup-panel": o.ProbeCleanupPanel = true; break;
                case "--probe-overflow": o.ProbeOverflow = true; break;
                case "--probe-placement": o.ProbePlacement = true; break;
                case "--probe-resize": o.ProbeResize = true; break;
                case "--probe-edges": o.ProbeEdges = true; break;
                case "--probe-spawn": o.ProbeSpawn = true; break;
                case "--quit": o.Quit = true; break;
                case "--allow-multiple": o.AllowMultiple = true; break;
                case "--probe-dsh": o.ProbeDsh = true; break;
                case "--no-tray": o.NoTray = true; break;
                case "--reset-position": o.ResetPosition = true; break;
                default: throw new ArgumentException($"unknown argument: {a}");
            }
        }
        return o;
    }

    public static string Help => """
        WhaleDesk (P1 skeleton)

          --selftest              validate atlas/spec invariants and exit (no UI at all)
          --render                offscreen render to build/*.png (no window is ever shown)
          --live [--seconds N]    show the real transparent window; N seconds then exit,
                                  0 (or a negative value) runs until the app is closed
                 [--probe-hittest]  move the cursor and log the click-through state
                 [--probe-interact] synthetic click + drag, then log position/snap
                 [--probe-menu]     open the in-window menu (for a screenshot)
                 [--probe-move-left] drag to the left edge and keep it (persistence test)
                 [--probe-monitors]  dock on every monitor and log the physical rect
                 [--probe-hide]      hide, then exercise the hotkey path (regression: bug report)
                 [--probe-monitor-panel] open the monitoring card and log a real sample
                 [--probe-cleanup-panel] open the cleanup card and log a real scan (deletes nothing)
                 [--probe-dsh]      exercise open/start DSH and log every candidate mechanism
                 [--probe-spawn]    log this process integrity/token and a control child spawn
          --quit                  ask a running pet to exit (used by the installer)
          --no-discovery          testing only: never look for a different DSH port
          --allow-multiple        testing only: start even if another pet is running
          --no-tray               do not create the tray icon
          --reset-position        ignore the saved position and dock bottom-right
          --pet-height N          pet height in DIP (default 160)
          --scale N               extra offscreen scale, e.g. 1.5 (DPI check)
          --margin-x/-y N         distance from the bottom-right corner (default 16 / 10)
          --root PATH             project root (defaults to the one containing assets/)
        """;
}
