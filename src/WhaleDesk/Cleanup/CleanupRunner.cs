using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WhaleDesk.Cleanup;

internal sealed record CleanupScan(string TargetId, bool Exists, long Bytes, int Files, int Skipped, string? Error);

/// <summary>
/// Measures a target without touching it. Reparse points are never followed: a
/// junction inside a cache directory can point anywhere, and a recursive walk that
/// follows it would report (and later delete) files far outside the target.
/// </summary>
internal static class CleanupScanner
{
    public static CleanupScan Scan(CleanupTarget target, Log log)
    {
        if (target.SkipMeasure)
            return new CleanupScan(target.Id, false, 0, 0, 0, "需用 DISM 分析（管理员）");

        if (target.Method == CleanupMethod.EmptyRecycleBin)
        {
            var (bytes, items) = RecycleBin.Query();
            return new CleanupScan(target.Id, items > 0, bytes, items, 0, null);
        }

        // A single-file target (MEMORY.DMP).
        if (File.Exists(target.Path))
        {
            long size = 0;
            try { size = new FileInfo(target.Path).Length; } catch { }
            return new CleanupScan(target.Id, true, size, 1, 0, null);
        }

        // Measuring is read-only, so it is allowed even for a target that can never be
        // executed (the Windows Update cache, which lives under C:\Windows): the user
        // should still SEE how much it holds. ReportOnly targets are declared by the
        // catalog, never by user input, so this cannot be pointed at an arbitrary path.
        if (target.Method != CleanupMethod.ReportOnly && CleanupCatalog.IsForbiddenTarget(target.Path))
            return new CleanupScan(target.Id, false, 0, 0, 0, "该路径被安全规则排除");

        try
        {
            if (target.Method == CleanupMethod.RecyclePattern)
            {
                if (!Directory.Exists(target.Path)) return new CleanupScan(target.Id, false, 0, 0, 0, null);
                long bytes = 0;
                var count = 0;
                foreach (var file in Directory.EnumerateFiles(target.Path, target.Pattern ?? "*", SearchOption.TopDirectoryOnly))
                {
                    try { bytes += new FileInfo(file).Length; count++; } catch { }
                }
                return new CleanupScan(target.Id, true, bytes, count, 0, null);
            }

            if (!Directory.Exists(target.Path)) return new CleanupScan(target.Id, false, 0, 0, 0, null);

            long total = 0;
            var files = 0;
            var walk = CleanupScanner.Walk(target.Path, target.MinAgeDays, log);
            foreach (var file in walk.Files)
            {
                total += file.Length;
                files++;
            }
            return new CleanupScan(target.Id, true, total, files, walk.Skipped, null);
        }
        catch (Exception ex)
        {
            return new CleanupScan(target.Id, Directory.Exists(target.Path), 0, 0, 0, ex.Message);
        }
    }

    /// <summary>Guarded walk over a target's files (age filter + reparse-point skip).</summary>
    internal static GuardedWalk Walk(string root, int minAgeDays, Log log)
    {
        var walk = new GuardedWalk();
        walk.Prepare(root, minAgeDays, log);
        return walk;
    }
}

/// <summary>
/// A guarded directory walk. <see cref="Skipped"/> fills in as <see cref="Files"/> is
/// enumerated (an iterator cannot carry a ref parameter, hence the small holder).
/// </summary>
internal sealed class GuardedWalk
{
    public int Skipped;
    private string _root = "";
    private int _minAgeDays;
    private Log _log = null!;

    public void Prepare(string root, int minAgeDays, Log log)
    {
        _root = root;
        _minAgeDays = minAgeDays;
        _log = log;
        Skipped = 0;
    }

    /// <summary>Files under the root, skipping reparse points and (optionally) recent ones.</summary>
    public IEnumerable<FileInfo> Files => WalkFiles();

    private IEnumerable<FileInfo> WalkFiles()
    {
        var pending = new Stack<string>();
        pending.Push(_root);
        var cutoff = _minAgeDays > 0 ? DateTime.UtcNow.AddDays(-_minAgeDays) : DateTime.MinValue;

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subdirs;
            FileInfo[] files;
            try
            {
                var info = new DirectoryInfo(dir);
                subdirs = info.GetDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Select(d => d.FullName).ToArray();
                files = info.GetFiles();
            }
            catch (Exception ex)
            {
                Skipped++;
                _log.Warn($"scan: cannot enumerate {dir}: {ex.Message}");
                continue;
            }

            foreach (var sub in subdirs) pending.Push(sub);
            foreach (var file in files)
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { Skipped++; continue; }
                if (cutoff != DateTime.MinValue && file.LastWriteTimeUtc > cutoff) { Skipped++; continue; }
                yield return file;
            }
        }
    }
}

/// <summary>
/// Executes a cleanup. Everything goes to the RECYCLE BIN (FOF_ALLOWUNDO) and the
/// shell is asked to stay silent, so a locked file is skipped instead of raising a
/// dialog in the user's face. Official commands (npm/pnpm) are never replaced by
/// hand-deleting their stores.
/// </summary>
internal static class CleanupRunner
{
    public static CleanupResult Clean(CleanupTarget target, Log log)
    {
        if (target.Method == CleanupMethod.EmptyRecycleBin)
        {
            // No path to guard: the target IS the recycle bin, and the user had to
            // click this exact row twice before it can run.
            var (bytes, items) = RecycleBin.Query();
            var emptied = RecycleBin.Empty(log);
            log.Info($"cleanup recycle-bin: emptied={emptied} was {bytes} bytes / {items} items");
            return new CleanupResult(target.Id, bytes, emptied ? items : 0, emptied ? 0 : items,
                emptied ? null : "清空回收站失败或被取消", null);
        }

        if (!CleanupCatalog.MayExecute(target))
            return new CleanupResult(target.Id, 0, 0, 0, "该目标只报告、不清理", null);

        if (!CleanupCatalog.IsAllowedPath(target.Path) || CleanupCatalog.IsForbiddenTarget(target.Path))
            return new CleanupResult(target.Id, 0, 0, 0, "路径不在允许范围内，拒绝执行", null);

        return CleanUnchecked(target, log, hardDelete: false);
    }

    /// <summary>
    /// The ELEVATED path: only ever called for an id in <see cref="CleanupCatalog.MayExecuteElevated"/>.
    /// The path still comes from the catalog, never from a command line.
    /// </summary>
    internal static CleanupResult CleanElevated(CleanupTarget target, Log log)
    {
        if (target.Command == "dism-cleanup")
        {
            // The official component-store cleanup. Long running (minutes), which is why
            // the elevated launch waits on it.
            return RunCommand("dism.exe", new[] { "/Online", "/Cleanup-Image", "/StartComponentCleanup" }, target.Id, log, 3_600_000);
        }
        return CleanUnchecked(target, log, hardDelete: false);
    }

    private static CleanupResult RunCommand(string file, string[] args, string targetId, Log log, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return new CleanupResult(targetId, 0, 0, 0, "无法启动命令", null);
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(true); } catch { }
                return new CleanupResult(targetId, 0, 0, 0, "命令超时", Tail(output));
            }
            log.Info($"command {file} exited {proc.ExitCode}");
            return new CleanupResult(targetId, 0, 0, 0, proc.ExitCode == 0 ? null : $"命令退出码 {proc.ExitCode}", Tail(output));
        }
        catch (Exception ex)
        {
            return new CleanupResult(targetId, 0, 0, 0, ex.Message, null);
        }
    }

    private static string Tail(string text) => text.Length > 600 ? text[^600..] : text;

    /// <summary>
    /// The real worker. Guarded by <see cref="Clean"/>; called directly only by the
    /// rehearsal mode, which runs on a synthetic tree inside the workspace.
    /// </summary>
    internal static CleanupResult CleanUnchecked(CleanupTarget target, Log log, bool hardDelete)
    {
        if (target.Method == CleanupMethod.OfficialCommand)
            return RunOfficialCommand(target, log);

        // A single-file target (MEMORY.DMP, a dump file): recycle just that file.
        if (File.Exists(target.Path))
        {
            long size = 0;
            try { size = new FileInfo(target.Path).Length; } catch { }
            var ok = hardDelete
                ? HardDelete(new[] { target.Path }, log, out _)
                : RecycleBin.TryRecycle(new[] { target.Path }, log, out _);
            return new CleanupResult(target.Id, size, ok ? 1 : 0, ok ? 0 : 1, ok ? null : "文件无法删除（可能被占用）", null);
        }

        var scan = CleanupScanner.Scan(target, log);
        if (!scan.Exists) return new CleanupResult(target.Id, 0, 0, 0, "目录不存在", null);

        long bytes = 0;
        var deleted = 0;
        var skipped = 0;

        if (target.Method == CleanupMethod.RecyclePattern)
        {
            var files = Directory.Exists(target.Path)
                ? Directory.EnumerateFiles(target.Path, target.Pattern ?? "*", SearchOption.TopDirectoryOnly).ToList()
                : new List<string>();
            foreach (var file in files)
            {
                try { bytes += new FileInfo(file).Length; } catch { }
            }
            var ok = hardDelete ? HardDelete(files, log, out var failed) : RecycleBin.TryRecycle(files, log, out failed);
            deleted = files.Count - failed;
            skipped += failed;
            if (!ok && failed == files.Count) return new CleanupResult(target.Id, 0, 0, files.Count, "回收站调用失败", null);
            return new CleanupResult(target.Id, bytes, deleted, skipped, null, null);
        }

        // Recycle the direct children of the target: a directory goes to the bin with
        // its contents, so one shell call replaces thousands of file operations.
        if (target.MinAgeDays > 0)
        {
            var guard = 0;
            var walk = CleanupScanner.Walk(target.Path, target.MinAgeDays, log);
            foreach (var file in walk.Files)
            {
                try { bytes += file.Length; } catch { }
                var one = new[] { file.FullName };
                if (hardDelete ? HardDelete(one, log, out _) : RecycleBin.TryRecycle(one, log, out _)) deleted++;
                else skipped++;
                if (++guard % 200 == 0) log.Info($"cleanup {target.Id}: {guard} files processed");
            }
            skipped += walk.Skipped;
            RemoveEmptyDirectories(target.Path, log);
        }
        else
        {
            var entries = new List<string>();
            try
            {
                foreach (var dir in Directory.GetDirectories(target.Path))
                {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    entries.Add(dir);
                }
                entries.AddRange(Directory.GetFiles(target.Path));
            }
            catch (Exception ex)
            {
                return new CleanupResult(target.Id, 0, deleted, skipped, "无法枚举: " + ex.Message, null);
            }

            foreach (var entry in entries)
            {
                try
                {
                    if (File.Exists(entry)) bytes += new FileInfo(entry).Length;
                    else bytes += DirSize(entry);
                }
                catch { }
            }

            const int batch = 120;
            for (var i = 0; i < entries.Count; i += batch)
            {
                var slice = entries.Skip(i).Take(batch).ToList();
                if (hardDelete ? HardDelete(slice, log, out var failed) : RecycleBin.TryRecycle(slice, log, out failed))
                {
                    deleted += slice.Count - failed;
                    skipped += failed;
                }
                else
                {
                    skipped += slice.Count;
                }
            }
        }

        var error = skipped > 0 ? $"{skipped} 项被跳过（占用中或无法访问）" : null;
        return new CleanupResult(target.Id, bytes, deleted, skipped, error, null);
    }

    /// <summary>Rehearsal-only deletion (no recycle bin), used to verify the selection logic.</summary>
    private static bool HardDelete(IEnumerable<string> paths, Log log, out int failed)
    {
        failed = 0;
        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                log.Warn($"rehearsal delete failed for {path}: {ex.Message}");
            }
        }
        return failed == 0;
    }

    private static CleanupResult RunOfficialCommand(CleanupTarget target, Log log)
    {
        var (file, args) = target.Command switch
        {
            "npm-cache-clean" => ResolveNpm(),
            "pnpm-store-prune" => ResolvePnpm(),
            _ => (null, null),
        };
        if (file is null || args is null)
            return new CleanupResult(target.Id, 0, 0, 0, target.Id == "npm-cache"
                ? "找不到 npm：未安装 Node.js 或不在 PATH。请在终端运行：npm cache clean --force"
                : "找不到 pnpm：未安装或不在 PATH。请在终端运行：pnpm store prune", null);

        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return new CleanupResult(target.Id, 0, 0, 0, "无法启动命令", null);
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(120_000)) { try { proc.Kill(true); } catch { } return new CleanupResult(target.Id, 0, 0, 0, "命令超时", output); }
            var tail = output.Length > 400 ? output[^400..] : output;
            log.Info($"cleanup {target.Id}: {file} {string.Join(" ", args)} exited {proc.ExitCode}");
            return new CleanupResult(target.Id, 0, 0, 0, proc.ExitCode == 0 ? null : $"命令退出码 {proc.ExitCode}", tail.Replace("\r", " ").Replace("\n", " "));
        }
        catch (Exception ex)
        {
            return new CleanupResult(target.Id, 0, 0, 0, ex.Message, null);
        }
    }

    /// <summary>
    /// Locate npm/pnpm. Order: PATH (a normal user has Node there) → DSH runtime → well-known
    /// install dirs. The old version only looked inside the DSH runtime, so a system-wide Node
    /// install (e.g. D:\Software\nodejs) made "npm 缓存" fail with "找不到对应的命令行工具".
    /// </summary>
    private static (string? File, string[]? Args) ResolveNpm()
    {
        var node = FindTool("node.exe");
        var npmCli = FindNpmCli(node);
        if (node is not null && npmCli is not null)
            return (node, new[] { npmCli, "cache", "clean", "--force" });

        // npm.cmd (global install / fnm / nvm / volta shims) — run it through cmd.exe.
        var npmCmd = FindTool("npm.cmd");
        if (npmCmd is not null)
            return (ComSpec, new[] { "/d", "/s", "/c", $"\"{npmCmd}\" cache clean --force" });

        return (null, null);
    }

    private static (string? File, string[]? Args) ResolvePnpm()
    {
        var node = FindTool("node.exe");
        var pnpmMjs = FindRuntimeTool("pnpm", Path.Combine("pnpm", "bin", "pnpm.mjs"));
        if (node is not null && pnpmMjs is not null)
            return (node, new[] { pnpmMjs, "store", "prune" });

        var pnpmCmd = FindTool("pnpm.cmd");
        if (pnpmCmd is not null)
            return (ComSpec, new[] { "/d", "/s", "/c", $"\"{pnpmCmd}\" store prune" });

        return (null, null);
    }

    /// <summary>Diagnostics for --probe-cleanup-tools: what command would be used, without running it.</summary>
    internal static string DescribeResolvedTools()
    {
        var (npmFile, npmArgs) = ResolveNpm();
        var (pnpmFile, pnpmArgs) = ResolvePnpm();
        return $"npm={(npmFile is null ? "NOT FOUND" : npmFile + " " + string.Join(" ", npmArgs ?? Array.Empty<string>()))} | " +
               $"pnpm={(pnpmFile is null ? "NOT FOUND" : pnpmFile + " " + string.Join(" ", pnpmArgs ?? Array.Empty<string>()))} | " +
               $"pathDirs={string.Join(";", PathDirs())}";
    }

    private static string ComSpec => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    /// <summary>npm-cli.js lives next to node.exe for a normal install; also probe PATH dirs and %APPDATA%\npm.</summary>
    private static string? FindNpmCli(string? node)
    {
        var candidates = new List<string>();
        if (node is not null)
        {
            var dir = Path.GetDirectoryName(node)!;
            candidates.Add(Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js"));
            candidates.Add(Path.Combine(dir, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"));
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(appData, "npm", "node_modules", "npm", "bin", "npm-cli.js"));
        foreach (var dir in PathDirs())
            candidates.Add(Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js"));
        foreach (var candidate in candidates)
        {
            try { if (File.Exists(Path.GetFullPath(candidate))) return Path.GetFullPath(candidate); }
            catch { }
        }
        return null;
    }

    /// <summary>Find an executable by name: PATH first, then common Node dirs and the DSH runtime.</summary>
    private static string? FindTool(string fileName)
    {
        foreach (var dir in PathDirs())
        {
            var candidate = Path.Combine(dir, fileName);
            try { if (File.Exists(candidate)) return candidate; } catch { }
        }

        if (fileName.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
        {
            var runtimeNode = FindRuntimeTool("node", Path.Combine("bin", "node.exe")) ?? FindRuntimeTool("node", "node.exe");
            if (runtimeNode is not null) return runtimeNode;
        }

        foreach (var common in CommonNodeDirs())
        {
            var candidate = Path.Combine(common, fileName);
            try { if (File.Exists(candidate)) return candidate; } catch { }
        }
        return null;
    }

    private static IEnumerable<string> PathDirs()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = raw.Trim('"');
            if (dir.Length > 0) yield return dir;
        }
    }

    private static IEnumerable<string> CommonNodeDirs()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(programFiles, "nodejs");
        yield return Path.Combine(localAppData, "Programs", "nodejs");
        yield return Path.Combine(appData, "npm");
        yield return Path.Combine(localAppData, "Volta", "bin");
        yield return Path.Combine(appData, "fnm");
    }

    private static string? FindRuntimeTool(string folder, string relative)
    {
        var home = Path.Combine(CleanupCatalog.DshHome, "dsh-runtimes");
        if (!Directory.Exists(home)) return null;
        try
        {
            foreach (var runtime in Directory.GetDirectories(home))
            {
                var candidate = Path.Combine(runtime, "dependencies", folder, relative);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return null;
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    total += new FileInfo(file).Length;
                }
                catch { }
            }
        }
        catch { }
        return total;
    }

    private static void RemoveEmptyDirectories(string root, Log log)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch { }
            }
        }
        catch (Exception ex) { log.Warn("cleanup: could not prune empty directories: " + ex.Message); }
    }
}

internal sealed record CleanupResult(string TargetId, long Bytes, int Deleted, int Skipped, string? Error, string? CommandOutput);

/// <summary>Recycle-bin deletion through the shell: silent, no dialogs, undoable.</summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    /// <summary>How much the recycle bin currently holds.</summary>
    public static (long Bytes, int Items) Query()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) != 0) return (0, 0);
            return (info.i64Size, (int)info.i64NumItems);
        }
        catch { return (0, 0); }
    }

    /// <summary>Empty the recycle bin. Silent, but only ever called from an explicit click.</summary>
    public static bool Empty(Log log)
    {
        try
        {
            var rc = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            if (rc != 0) log.Warn($"empty recycle bin returned 0x{rc:X8}");
            return rc == 0;
        }
        catch (Exception ex)
        {
            log.Warn("empty recycle bin failed: " + ex.Message);
            return false;
        }
    }

    public static bool TryRecycle(IEnumerable<string> paths, Log log, out int failed)
    {
        failed = 0;
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return true;

        // pFrom is a double-NUL terminated list.
        var from = string.Join('\0', list) + "\0\0";
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI,
        };
        try
        {
            var rc = SHFileOperation(ref op);
            if (rc != 0 || op.fAnyOperationsAborted)
            {
                failed = list.Count;
                log.Warn($"recycle: shell returned {rc} aborted={op.fAnyOperationsAborted} for {list.Count} item(s)");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            failed = list.Count;
            log.Warn("recycle failed: " + ex.Message);
            return false;
        }
    }
}
