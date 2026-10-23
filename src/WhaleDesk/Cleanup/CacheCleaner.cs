using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace WhaleDesk.Cleanup;

/// <summary>What happened when the analyzed caches were removed.</summary>
internal sealed record CacheCleanResult(int Deleted, int Refused, ulong FreedBytes, IReadOnlyList<string> Messages);

/// <summary>
/// Deletes the items the space analyzer marked as safe caches - and nothing else.
///
/// Safety rules, all enforced per item at delete time (not just trusted from the report):
///   1. the path must still exist and must NOT be a reparse point,
///   2. re-classifying it must still say SafeCache - and the re-check runs on the FULL path,
///      so the analyzer's path-level guards participate (names alone are not evidence),
///   3. it must live under a user/app data root (%LOCALAPPDATA%, %APPDATA%, %TEMP%),
///   4. it must not be a whole application folder: not a protected root (Programs,
///      Program Files, Packages, a personal folder...), not inside one, and it must not
///      contain an executable entry point or a project file in its direct children,
///   5. deletion goes to the RECYCLE BIN, so a mistake is recoverable.
///
/// Anything failing a rule is refused and reported, never silently skipped.
/// </summary>
internal static class CacheCleaner
{
    private static readonly string[] AllowedRoots =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.GetTempPath(),
    };

    /// <summary>An application ships an entry point: seeing one means "this is the app", not "this is junk".</summary>
    private static readonly string[] EntryExtensions = { ".exe", ".dll", ".cmd", ".bat", ".lnk" };

    /// <summary>Project/app manifests: same conclusion, one level cheaper to spot.</summary>
    private static readonly string[] EntryMarkers = { "package.json", "*.csproj", "*.appxmanifest" };

    /// <summary>Ancestor names that make a folder "the installed app", whatever it is called itself.</summary>
    private static readonly string[] AppAncestorNames = { "program files", "program files (x86)", "programs" };

    public static bool IsAllowedRoot(string path)
    {
        foreach (var root in AllowedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Safety rule 4: the target must not be a whole application folder. Judged by evidence
    /// (protected path, app ancestor, executable/project file inside), never by the item name.
    /// </summary>
    internal static bool LooksLikeApplicationRoot(string path, out string reason)
    {
        reason = string.Empty;

        // 4a. 已知的应用根/受保护目录，以及它们内部的任何位置。
        if (DirectoryAnalyzer.IsProtectedPath(path, out var protectedReason))
        {
            reason = protectedReason;
            return true;
        }

        // 4b. 祖先目录名就是装着程序的目录（%APPDATA% 下也可能出现这样的结构）。
        try
        {
            var dir = new DirectoryInfo(path);
            for (var depth = 0; depth < 3; depth++)
            {
                var parent = dir.Parent;
                if (parent is null) break;
                var ancestor = parent.Name.ToLowerInvariant();
                if (AppAncestorNames.Any(n => ancestor == n))
                {
                    reason = $"位于「{parent.Name}」目录下：那是装程序的地方，不是缓存";
                    return true;
                }
                dir = parent;
            }
        }
        catch
        {
            // unreadable parents: the other checks still apply
        }

        // 4c. 目标目录的直接子项里躺着可执行入口或项目文件：这是应用/项目本体。
        return HasExecutableEntryPoint(path, out reason);
    }

    /// <summary>
    /// Part 4c on its own: does this directory hold an executable entry point (or a project/app
    /// manifest) in its DIRECT children? internal so the self test can assert the evidence rule
    /// itself - its probe trees live under a protected path, where 4a answers first.
    /// </summary>
    internal static bool HasExecutableEntryPoint(string path, out string reason)
    {
        reason = string.Empty;
        if (!Directory.Exists(path)) return false;

        try
        {
            var inspected = 0;
            foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos())
            {
                if (++inspected > 4000) break;   // 超大目录不再扫，避免把清理卡住
                if (child is DirectoryInfo) continue;
                var lower = child.Name.ToLowerInvariant();
                if (EntryExtensions.Any(ext => lower.EndsWith(ext, StringComparison.Ordinal)))
                {
                    reason = $"目录里直接有可执行入口「{child.Name}」：看起来是应用目录，不是缓存，拒绝删除";
                    return true;
                }
                foreach (var marker in EntryMarkers)
                {
                    var hit = marker.StartsWith("*.", StringComparison.Ordinal)
                        ? lower.EndsWith(marker[1..], StringComparison.Ordinal)
                        : lower == marker;
                    if (hit)
                    {
                        reason = $"目录里有「{child.Name}」：看起来是应用/项目目录，不是缓存，拒绝删除";
                        return true;
                    }
                }
            }
        }
        catch
        {
            // cannot enumerate: report no evidence
        }

        return false;
    }

    /// <summary>Delete one item if every safety rule holds.</summary>
    public static bool TryDelete(AnalysisItem item, out ulong freed, out string reason)
    {
        freed = 0;
        reason = string.Empty;
        try
        {
            var path = item.Path;
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                reason = "已经不在了";
                return false;
            }
            if (!IsAllowedRoot(path))
            {
                reason = "不在允许清理的范围内（只清 AppData 与临时目录）";
                return false;
            }
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "是链接（重解析点），不处理";
                    return false;
                }
            }

            // Rule 4: never a whole application folder (protected root/ancestor, or an
            // executable entry point / project file sitting right inside it).
            if (LooksLikeApplicationRoot(path, out var appReason))
            {
                reason = appReason;
                return false;
            }

            // Rule 2: re-verify at delete time: the verdict handed in may be stale. This time
            // the FULL path goes in, so the analyzer's path-level guards (Packages, Programs,
            // personal folders...) apply - re-checking the bare name proved nothing.
            var parentPath = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? string.Empty;
            var verdict = DirectoryAnalyzer.Classify(item.Name, Directory.Exists(path), parentPath, null, out var recheckReason);
            if (verdict != CleanVerdict.SafeCache)
            {
                reason = string.IsNullOrWhiteSpace(recheckReason)
                    ? "再次核对后不是可删类型"
                    : $"再次核对后不是可删类型（{recheckReason}）";
                return false;
            }

            var bytes = item.Bytes;
            if (Directory.Exists(path))
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }
            else
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }

            freed = bytes;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>Delete a batch, reporting each refusal.</summary>
    public static CacheCleanResult Delete(IReadOnlyList<AnalysisItem> items)
    {
        var deleted = 0;
        var refused = 0;
        ulong freed = 0;
        var messages = new List<string>();

        foreach (var item in items)
        {
            if (TryDelete(item, out var bytes, out var reason))
            {
                deleted++;
                freed += bytes;
            }
            else
            {
                refused++;
                messages.Add($"{item.Name}: {reason}");
            }
        }
        return new CacheCleanResult(deleted, refused, freed, messages);
    }
}
