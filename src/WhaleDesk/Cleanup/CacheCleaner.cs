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
///   2. re-classifying the name must still say SafeCache,
///   3. it must live under a user/app data root (%LOCALAPPDATA%, %APPDATA%, %TEMP%),
///   4. it must not be a whole application folder (ancestors named Programs/Program Files),
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

    public static bool IsAllowedRoot(string path)
    {
        foreach (var root in AllowedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
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

            // Re-verify at delete time: the verdict handed in may be stale.
            var verdict = DirectoryAnalyzer.Classify(item.Name, Directory.Exists(path), out _);
            if (verdict != CleanVerdict.SafeCache)
            {
                reason = "再次核对后不是可删类型";
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
