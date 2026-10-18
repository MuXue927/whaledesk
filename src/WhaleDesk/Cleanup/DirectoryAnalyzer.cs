using System.IO;

namespace WhaleDesk.Cleanup;

/// <summary>What can be done with one item, from "delete freely" to "never touch".</summary>
internal enum CleanVerdict
{
    /// <summary>A cache/logs/old-version folder: the app recreates it. Safe to delete.</summary>
    SafeCache,
    /// <summary>An installed program or its data: remove it through the app's own uninstaller/settings.</summary>
    CleanInApp,
    /// <summary>Windows itself.</summary>
    SystemFiles,
    /// <summary>The user's own files. Show size, never suggest deleting.</summary>
    PersonalFiles,
    /// <summary>No rule matched: report it, say we do not know.</summary>
    Unknown,
    /// <summary>Evidence says the software is used or was touched recently: keep it.</summary>
    InUseApp,
    /// <summary>The user explicitly marked this as "keep".</summary>
    Kept,
}

internal sealed record AnalysisItem(string Name, string Path, ulong Bytes, CleanVerdict Verdict, string Reason, bool Partial)
{
    /// <summary>Directories whose verdict is not final are worth drilling into.</summary>
    public bool Drillable => Verdict is CleanVerdict.CleanInApp or CleanVerdict.Unknown or CleanVerdict.PersonalFiles or CleanVerdict.InUseApp or CleanVerdict.Kept;
}

/// <summary>Signals the analyzer may use beyond the folder name itself.</summary>
internal sealed record AnalysisContext(
    IReadOnlyList<Performance.InUseEvidence>? RunningProcesses = null,
    Interaction.KeepList? Keep = null,
    string? CurrentPath = null,
    Interaction.IgnoreList? Ignore = null);

internal sealed record AnalysisReport(
    string Root,
    ulong Total,
    IReadOnlyList<AnalysisItem> Items,
    int ScannedChildren,
    bool Truncated);

/// <summary>
/// Explains what is inside a big directory and whether it is safe to remove.
///
/// The hard part the user raised: every machine has different programs and folders, so a
/// name list of "known junk" can never be complete. The way out is to classify by
/// OWNERSHIP and REBUILDABILITY instead of by product name:
///
///   * caches / logs / old versions  -> the app recreates them (SafeCache)
///   * installed programs            -> must go through the app's uninstaller (CleanInApp)
///   * Windows                       -> never (SystemFiles)
///   * the user's own documents      -> show the size, never suggest deleting (PersonalFiles)
///   * anything else                 -> say "I do not know" instead of guessing (Unknown)
///
/// Every verdict comes with the evidence that produced it, so the user can check the reasoning
/// and disagree. The analysis is read-only: nothing here deletes anything.
/// </summary>
internal static class DirectoryAnalyzer
{
    /// <summary>Sub-folder/file names that are caches, logs or throwaway data by convention.</summary>
    private static readonly string[] CacheWords =
    {
        "cache", "caches", "cached", "cachestorage", "cache2", "gpucache", "shadercache",
        "code cache", "service worker", "dawncache", "dawnwebgpucache", "graphitecache",
        "temp", "tmp", "logs", "log", "crashpad", "crashdumps", "dumps", "dbghelp",
        "_cacache", "npm-cache", "yarn-cache", "pnpm-store", "packages", "obj", "bin",
        ".vs", ".gradle", ".m2", ".nuget", "cargo", "go-build", "zig-cache", "electron",
        "pending", "updater", "update-cache", "webcache", "blob_storage", "blocklist",
    };

    private static readonly string[] CacheExtensions = { ".tmp", ".temp", ".log", ".dmp", ".etl", ".bak", ".old", ".crdownload", ".part" };

    /// <summary>Folders that belong to Windows itself.</summary>
    private static readonly string[] SystemNames =
    {
        "windows", "winsxs", "system32", "syswow64", "servicing", "assembly", "driverstore",
        "$windows.~bt", "$windows.~ws", "windows.old", "system volume information",
    };

    /// <summary>Folders holding the user's own content.</summary>
    private static readonly string[] PersonalNames =
    {
        "documents", "desktop", "pictures", "photos", "videos", "music", "downloads",
        "onedrive", "dropbox", "坚果云", "wechat files", "微信", "favorites", "contacts",
    };

    /// <summary>Installed programs live here (or are a direct child of Program Files).</summary>
    private static readonly string[] InstalledParents = { "program files", "program files (x86)", "programdata" };

    /// <summary>Vendor data folders live under AppData; their caches are one level deeper.</summary>
    private static bool UnderAppData(string path) =>
        path.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("\\ProgramData", StringComparison.OrdinalIgnoreCase);

    private static bool UnderPersonal(string path) =>
        new[] { "\\Documents", "\\Desktop", "\\Pictures", "\\Videos", "\\Music", "\\Downloads" }
            .Any(marker => path.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static CleanVerdict Classify(string name, bool isDirectory, out string reason) =>
        Classify(name, isDirectory, string.Empty, null, out reason);

    /// <summary>
    /// Context matters: the same folder name means different things under AppData (an app's data,
    /// caches one level down) and under Documents (the user's own files).
    /// </summary>
    public static CleanVerdict Classify(string name, bool isDirectory, string parentPath, AnalysisContext? context, out string reason)
    {
        var lower = name.ToLowerInvariant();

        if (context?.Keep is { } keep)
        {
            var fullPath = string.IsNullOrEmpty(context.CurrentPath) ? name : Path.Combine(context.CurrentPath, name);
            if (keep.Contains(fullPath) || keep.Contains(name))
            {
                reason = "你在「保留清单」里标记过：程序和数据都别动（缓存仍可清）";
                return CleanVerdict.Kept;
            }
        }

        foreach (var system in SystemNames)
        {
            if (lower == system || lower.StartsWith(system + "."))
            {
                reason = "Windows 系统文件，不能手动删";
                return CleanVerdict.SystemFiles;
            }
        }

        foreach (var personal in PersonalNames)
        {
            if (lower == personal)
            {
                reason = "你自己的文件/资料，我不会建议删除";
                return CleanVerdict.PersonalFiles;
            }
        }

        if (!isDirectory)
        {
            foreach (var extension in CacheExtensions)
            {
                if (lower.EndsWith(extension, StringComparison.Ordinal))
                {
                    reason = $"临时/日志文件（{extension}），通常可以直接删";
                    return CleanVerdict.SafeCache;
                }
            }
        }

        foreach (var word in CacheWords)
        {
            if (lower.Contains(word, StringComparison.Ordinal))
            {
                if (lower.Contains("node_modules", StringComparison.Ordinal))
                {
                    reason = "项目依赖，删掉能省空间，但项目下次要重新 npm install";
                    return CleanVerdict.SafeCache;
                }
                reason = $"名字含「{word}」：缓存或临时数据，应用会自己重建";
                return CleanVerdict.SafeCache;
            }
        }

        // Electron-style old versions: app-1.2.3, versions, *-old
        if (lower.StartsWith("app-") || lower.EndsWith("-old") || lower == "versions")
        {
            reason = "旧版本/更新残留，通常可以删";
            return CleanVerdict.SafeCache;
        }

        foreach (var parent in InstalledParents)
        {
            if (lower == parent)
            {
                reason = "已安装的程序，请到「设置 → 应用」里卸载，手动删会留注册表垃圾";
                return CleanVerdict.CleanInApp;
            }
        }

        // Specific dev-tool caches that are safe to remove and get rebuilt on demand.
        if (lower is "pnpm" or "pnpm-store" or "uv" or "pypoetry" or "pip" or "npm" or "yarn"
            or "nuget" or "gradle" or "maven" or "cargo" or "go" or "conda" or "miniconda3"
            or "pipx" or "poetry" or "huggingface" or "torch" or "cursor" or "zed" or "wsl")
        {
            if (lower == "wsl")
            {
                reason = "WSL 子系统磁盘：删了你的 Linux 环境就没了，别当缓存清";
                return CleanVerdict.PersonalFiles;
            }
            reason = $"开发工具缓存（{name}）：删掉可省空间，下次用会重新下载/重建";
            return CleanVerdict.SafeCache;
        }

        if (lower is "programs" or "programs (x86)")
        {
            reason = "这里的程序请用「设置 → 应用」卸载，别手删";
            return CleanVerdict.CleanInApp;
        }

        // Any other folder under AppData/ProgramData is some application's data: caches inside
        // are removable, account/config files are not. Point the user INSIDE instead of guessing.
        if (isDirectory && UnderAppData(parentPath))
        {
            var fullPath = string.IsNullOrEmpty(parentPath) ? name : Path.Combine(parentPath, name);
            var evidence = context?.RunningProcesses is { } running ? Performance.AppUsageIndex.Match(name, running) : null;
            if (evidence is not null)
            {
                reason = $"看起来正在用（进程 {evidence.ProcessName}）：程序本身别动；点开可以只清里面的缓存";
                return CleanVerdict.InUseApp;
            }
            if (context is not null && Performance.AppUsageIndex.RecentlyTouched(fullPath))
            {
                reason = "最近两周有改动，估计还在用：别动程序，点进去只清缓存";
                return CleanVerdict.InUseApp;
            }
            reason = "某个应用的本地数据：点开（或点这一行）看里面的缓存/配置，缓存能清、账号配置别删";
            return CleanVerdict.CleanInApp;
        }

        if (isDirectory && UnderPersonal(parentPath))
        {
            reason = "位于你的个人目录下，是你的文件，我不会建议删除";
            return CleanVerdict.PersonalFiles;
        }

        if (lower is "dsh" or ".dsh")
        {
            reason = "DSH 的会话与配置，删了会丢会话；要清理请用 DSH 自己的设置";
            return CleanVerdict.PersonalFiles;
        }

        if (isDirectory)
        {
            reason = "没有匹配到已知规则：可能是你或某个程序自己建的，建议看一眼再决定";
            return CleanVerdict.Unknown;
        }

        reason = "普通文件，没有匹配到清理规则";
        return CleanVerdict.Unknown;
    }

    /// <summary>
    /// Size and classify the children of <paramref name="path"/>. Read-only, bounded by a time
    /// budget so a huge folder cannot freeze the UI.
    /// </summary>
    public static AnalysisReport? Analyze(string path, int maxChildren = 24, int budgetMs = 12_000, AnalysisContext? context = null)
    {
        if (!Directory.Exists(path)) return null;

        var started = Environment.TickCount64;
        var children = new List<(string Name, string FullPath, bool IsDirectory, ulong Bytes, bool Partial)>();

        try
        {
            foreach (var dir in new DirectoryInfo(path).EnumerateDirectories())
            {
                if (Environment.TickCount64 - started > budgetMs) break;
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (context?.Ignore is { } ignoreList && ignoreList.Contains(dir.FullName)) continue;
                var bytes = DirectorySizer.Measure(dir.FullName, Math.Min(started + budgetMs, Environment.TickCount64 + 3000), out var partial);
                children.Add((dir.Name, dir.FullName, true, bytes, partial));
            }
            foreach (var file in new DirectoryInfo(path).EnumerateFiles())
            {
                if (Environment.TickCount64 - started > budgetMs) break;
                try { children.Add((file.Name, file.FullName, false, (ulong)file.Length, false)); }
                catch { /* vanished */ }
            }
        }
        catch
        {
            // unreadable root: report what we managed to measure
        }

        var scanned = children.Count;
        var items = children
            .Where(c => c.Bytes > 0)
            .OrderByDescending(c => c.Bytes)
            .Take(maxChildren)
            .Select(c => new AnalysisItem(
                c.Name, c.FullPath, c.Bytes,
                Classify(c.Name, c.IsDirectory, path, context is null ? null : context with { CurrentPath = path }, out var reason), reason, c.Partial))
            .ToList();

        var total = children.Aggregate(0UL, (sum, c) => sum + c.Bytes);
        return new AnalysisReport(path, total, items, scanned, scanned > maxChildren);
    }

    /// <summary>
    /// Walk a few levels down looking for every SafeCache item, so "what can I actually delete"
    /// is answered in one shot instead of asking the user to drill manually 3-4 times.
    ///
    /// It stops descending into a folder once that folder is itself deletable (everything inside
    /// goes with it), and it honours the ignore list. Bounded by depth, node count and time.
    /// </summary>
    public static IReadOnlyList<AnalysisItem> DeepScan(string root, int depth = 3, int maxNodes = 160, int budgetMs = 9000, AnalysisContext? context = null)
    {
        var found = new List<AnalysisItem>();
        if (!Directory.Exists(root)) return found;

        var started = Environment.TickCount64;
        var queue = new Queue<(string Path, int Level)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < maxNodes && Environment.TickCount64 - started < budgetMs)
        {
            var (dir, level) = queue.Dequeue();
            DirectoryInfo info;
            try { info = new DirectoryInfo(dir); }
            catch { continue; }

            IEnumerable<FileSystemInfo> children;
            try { children = info.EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var child in children)
            {
                if (visited++ >= maxNodes || Environment.TickCount64 - started > budgetMs) break;
                var isDir = child is DirectoryInfo;
                if (isDir && (child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (context?.Ignore is { } ignoreList && ignoreList.Contains(child.FullName)) continue;

                ulong bytes;
                var partial = false;
                try
                {
                    bytes = isDir
                        ? DirectorySizer.Measure(child.FullName, Environment.TickCount64 + 1200, out partial)
                        : (ulong)((FileInfo)child).Length;
                }
                catch { continue; }
                if (bytes == 0) continue;

                var verdict = Classify(child.Name, isDir, dir, context, out var reason);
                if (verdict == CleanVerdict.SafeCache)
                {
                    found.Add(new AnalysisItem(child.Name, child.FullName, bytes, verdict, reason, partial));
                    continue;   // everything below goes with it
                }
                if (isDir && level + 1 < depth)
                {
                    queue.Enqueue((child.FullName, level + 1));
                }
            }
        }

        return found.OrderByDescending(i => i.Bytes).Take(24).ToList();
    }

    /// <summary>Total bytes the user could reclaim if every SafeCache item were deleted.</summary>
    public static ulong Reclaimable(AnalysisReport report) =>
        report.Items.Where(i => i.Verdict == CleanVerdict.SafeCache).Aggregate(0UL, (sum, i) => sum + i.Bytes);

    public static string VerdictLabel(CleanVerdict verdict) => verdict switch
    {
        CleanVerdict.SafeCache => "可删",
        CleanVerdict.CleanInApp => "建议用卸载",
        CleanVerdict.SystemFiles => "别动",
        CleanVerdict.PersonalFiles => "你的文件",
        CleanVerdict.InUseApp => "在用·保留",
        CleanVerdict.Kept => "已标记保留",
        _ => "不确定",
    };
}
