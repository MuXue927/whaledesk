using System.IO;
using System.Windows;

namespace WhaleDesk;

internal static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        CliOptions opts;
        try
        {
            opts = CliOptions.Parse(args);

            // Handled before anything else: --quit must work on its own (no --live needed),
            // because the installer uses it to stop the running pet before replacing files.
            if (opts.Quit)
            {
                var asked = SingleInstance.SignalQuit();
                Console.Error.WriteLine(asked
                    ? "asked the running pet to quit"
                    : "no running pet found");
                return asked ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(CliOptions.Help);
            return 2;
        }

        if (opts.Mode == "help")
        {
            Console.WriteLine(CliOptions.Help);
            return 0;
        }

        var log = new Log(opts.LogPath);
        log.Info($"WhaleDesk {typeof(App).Assembly.GetName().Version} start mode={opts.Mode} root={opts.Root}");
        log.Diag("runtime", $"{Environment.Version} on {Environment.OSVersion} 64bit={Environment.Is64BitProcess}");

        try
        {
            if (!File.Exists(opts.SpecPath)) { log.Error($"missing spec: {opts.SpecPath}"); return 3; }
            if (!File.Exists(opts.AtlasPath)) { log.Error($"missing atlas: {opts.AtlasPath}"); return 3; }

            var spec = PetSpec.Load(opts.SpecPath);
            var atlas = AtlasLoader.Load(opts.AtlasPath, spec);
            log.Diag("atlas", $"{atlas.Atlas.PixelWidth}x{atlas.Atlas.PixelHeight} dpi={atlas.Atlas.DpiX} cell={atlas.CellW}x{atlas.CellH} cols={atlas.Columns} rows={atlas.Rows}");
            log.Diag("tracks", spec.Tracks.Select(t => $"{t.Row}:{t.Name}({t.Frames.Length}f,{(t.Loop ? "loop" : "once")})"));

            switch (opts.Mode)
            {
                case "selftest":
                    return SelfTest.Run(opts, spec, atlas, log);

                case "rehearsal":
                    return Cleanup.Rehearsal.Run(Path.Combine(opts.Root, "build", "verify"), log);

                // The elevated half of the admin cleanup. Windows starts this process
                // after the user accepts the UAC prompt; it is told a target ID, never a
                // path, and refuses anything outside the admin whitelist.
                case "elevated-clean":
                    return Cleanup.Elevation.RunElevatedClean(opts.ElevatedTarget ?? "", opts.ElevatedResultPath ?? "", log);

                case "render":
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    try { OffscreenRenderer.Run(opts, spec, atlas, log); }
                    finally { app.Shutdown(); }
                    log.Info("offscreen render finished (no window was created)");
                    return 0;
                }

                case "live":
                {
                    // One pet per session. A second launch no longer just gives up: it
                    // asks the running instance to show itself, because "hidden with no
                    // way back" was a dead end for the user.
                    using var single = new SingleInstance();
                    if (!opts.AllowMultiple && !single.IsFirst)
                    {
                        var signalled = SingleInstance.SignalRunningInstance();
                        log.Info($"another WhaleDesk instance is running; show-request signalled={signalled}");
                        Console.Error.WriteLine(signalled
                            ? "WhaleDesk is already running - asked it to show the pet (check the screen corner you left her on)."
                            : "WhaleDesk is already running but did not answer. Click the tray icon, press Ctrl+Alt+W, or end the WhaleDesk process in Task Manager.");
                        return signalled ? 0 : 5;
                    }

                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var window = new PetWindow(opts, spec, atlas, log);
                    if (!opts.AllowMultiple)
                        single.ListenForShowRequest(
                            () => window.Dispatcher.BeginInvoke(new Action(() => window.RequestShow("second launch")), System.Windows.Threading.DispatcherPriority.Normal),
                            log,
                            () => window.Dispatcher.BeginInvoke(new Action(() => window.QuitGracefully("quit request")), System.Windows.Threading.DispatcherPriority.Normal));
                    // A desktop pet must never die from one bad callback: log, keep
                    // running, and only give up if the failures are relentless.
                    var errorCount = 0;
                    app.DispatcherUnhandledException += (_, e) =>
                    {
                        errorCount++;
                        log.Error($"unhandled UI exception #{errorCount}: {e.Exception}");
                        e.Handled = true;
                        if (errorCount > 5)
                        {
                            log.Error("too many UI exceptions - closing the pet window");
                            window.FinishAndClose();
                        }
                    };
                    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                        log.Error("unhandled domain exception: " + e.ExceptionObject);
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(opts.LiveSeconds) };
                    timer.Tick += (_, _) => { timer.Stop(); window.FinishAndClose(); };
                    if (opts.LiveSeconds > 0)
                    {
                        timer.Start();
                        log.Info($"showing window for {opts.LiveSeconds}s (probeHitTest={opts.ProbeHitTest})");
                    }
                    else
                    {
                        log.Info($"running until closed (probeHitTest={opts.ProbeHitTest})");
                    }
                    app.Run(window);
                    log.Info("live window closed");
                    return 0;
                }

                default:
                    Console.WriteLine(CliOptions.Help);
                    return 0;
            }
        }
        catch (Exception ex)
        {
            log.Error("fatal: " + ex);
            return 1;
        }
    }
}
