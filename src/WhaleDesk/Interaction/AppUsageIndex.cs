using System.Diagnostics;
using System.IO;

namespace WhaleDesk.Performance;

/// <summary>
/// Evidence that a piece of software is actually in use, so the space analyzer does not tell the
/// user to uninstall the browser they live in.
///
/// Doing this by product name is hopeless (everyone's folder names differ), but the running
/// system provides better evidence:
///   * a running process whose executable path contains the folder name
///     (folder "Google" matches "C:\Program Files\Google\Chrome\Application\chrome.exe"),
///   * the process's own name,
///   * the folder was written to recently (it is alive, not abandoned).
///
/// This is a heuristic and is presented as such ("看起来在用"), never as a certainty.
/// </summary>
internal sealed record InUseEvidence(string FolderName, string ProcessName, string ExecutablePath);

internal static class AppUsageIndex
{
    /// <summary>Folders whose name appears in the path of a running process.</summary>
    public static IReadOnlyList<InUseEvidence> Running()
    {
        var result = new List<InUseEvidence>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* protected or 32-bit */ }
                if (string.IsNullOrEmpty(path)) continue;
                if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                    continue;   // system processes never own an AppData folder

                result.Add(new InUseEvidence(string.Empty, name, path));
            }
            catch { /* process vanished */ }
            finally { process.Dispose(); }
        }
        return result;
    }

    /// <summary>
    /// Does any running process live under a path that mentions <paramref name="folderName"/>?
    /// Returns the process that matched, or null.
    /// </summary>
    public static InUseEvidence? Match(string folderName, IReadOnlyList<InUseEvidence>? running = null)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return null;
        running ??= Running();
        foreach (var entry in running)
        {
            if (entry.ExecutablePath.Contains("\\" + folderName + "\\", StringComparison.OrdinalIgnoreCase) ||
                entry.ExecutablePath.Contains("\\" + folderName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                entry.ExecutablePath.EndsWith("\\" + folderName, StringComparison.OrdinalIgnoreCase))
            {
                return entry with { FolderName = folderName };
            }
        }
        return null;
    }

    /// <summary>
    /// Recently written to: "in use" in a weaker, time-based sense.
    ///
    /// Checking only the folder's own mtime is useless - writing files deeper down does not
    /// update it. So we sample entries two levels deep (capped) and take the newest timestamp.
    /// </summary>
    public static bool RecentlyTouched(string path, int days = 14)
    {
        var limit = DateTime.UtcNow.AddDays(-days);
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return false;
            if (info.LastWriteTimeUtc > limit) return true;

            var seen = 0;
            foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,
                         MaxRecursionDepth = 2,
                     }))
            {
                if (++seen > 400) break;
                try
                {
                    if (entry.LastWriteTimeUtc > limit) return true;
                }
                catch { }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}