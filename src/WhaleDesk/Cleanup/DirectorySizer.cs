using System.IO;
using WhaleDesk;

namespace WhaleDesk.Cleanup;

/// <summary>One big directory on the system drive, ready to be shown and opened.</summary>
internal sealed record BigDirectory(string Path, string Label, ulong Bytes, bool Partial = false);

/// <summary>
/// Measures which directories eat the most space, so the user can look (and decide) instead of
/// guessing. Read-only: it never deletes anything.
///
/// Reparse points are never followed (same rule as the cleanup scanner), inaccessible folders
/// are skipped instead of failing, and the whole scan runs against a time budget because
/// C:\Windows alone can hold hundreds of thousands of files.
/// </summary>
internal static class DirectorySizer
{
    /// <summary>Exact size of a directory tree; reparse points are not followed.</summary>
    public static ulong SizeOf(string path) => Measure(path, 0, out _);

    /// <summary>
    /// Size with an optional deadline (0 = no limit). When the deadline hits, the measured
    /// bytes are returned as a LOWER BOUND and partial=true, so a 50 GB folder full of files
    /// cannot stall the whole card.
    /// </summary>
    public static ulong Measure(string path, long deadlineMs, out bool partial)
    {
        partial = false;
        ulong total = 0;
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            foreach (var file in info.EnumerateFiles("*", options))
            {
                if (deadlineMs > 0 && Environment.TickCount64 > deadlineMs) { partial = true; break; }
                try { total += (ulong)file.Length; }
                catch { /* vanished while scanning */ }
            }
        }
        catch
        {
            // access denied on the root itself: report what we managed to measure
        }
        return total;
    }

    /// <summary>
    /// Candidates, heaviest-likely first, because the time budget is consumed in order.
    /// "C:\Users" is not listed: its AppData folders are measured individually instead.
    /// </summary>
    public static IReadOnlyList<(string Path, string Label)> Candidates()
    {
        var list = new List<(string, string)>();
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        void Add(string path, string label)
        {
            if (Directory.Exists(path)) list.Add((path, label));
        }

        Add(Path.Combine(drive, "Windows"), "Windows");
        Add(Path.Combine(drive, "Program Files"), "Program Files");
        Add(Path.Combine(drive, "Program Files (x86)"), "Program Files (x86)");
        Add(Path.Combine(drive, "ProgramData"), "ProgramData");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            Add(Path.Combine(profile, "AppData", "Local"), "AppData\\Local");
            Add(Path.Combine(profile, "AppData", "Roaming"), "AppData\\Roaming");
            Add(CleanupCatalog.DshHome, ".dsh");
            try
            {
                foreach (var dir in new DirectoryInfo(profile).GetDirectories())
                {
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (dir.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (dir.Name.Equals("AppData", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add((dir.FullName, dir.Name));
                }
            }
            catch { }
        }

        try
        {
            foreach (var dir in new DirectoryInfo(drive).GetDirectories())
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (list.Any(c => c.Item1.Equals(dir.FullName, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add((dir.FullName, dir.Name));
            }
        }
        catch { }

        return list;
    }
    /// <summary>
    /// Measure the biggest directories. Each directory gets a slice of the budget so one huge
    /// folder cannot stall the card; a capped measurement is reported as a lower bound.
    /// </summary>
    public static IReadOnlyList<BigDirectory> Scan(int max = 10, int budgetMs = 10_000, Log? log = null)
    {
        var candidates = Candidates();
        var results = new BigDirectory?[candidates.Count];
        var started = Environment.TickCount64;
        var perDirMs = Math.Max(1_500, budgetMs / 3);

        Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var now = Environment.TickCount64;
            if (now - started > budgetMs) return;
            var deadline = Math.Min(started + budgetMs, now + perDirMs);
            var (path, label) = candidates[i];
            var bytes = Measure(path, deadline, out var partial);
            results[i] = new BigDirectory(path, label, bytes, partial);
        });

        var list = results
            .Where(r => r is not null && r.Bytes > 0)
            .Select(r => r!)
            .OrderByDescending(r => r.Bytes)
            .Take(max)
            .ToList();
        log?.Info($"directory scan: {list.Count} of {candidates.Count} in {Environment.TickCount64 - started}ms" +
                  (list.Any(d => d.Partial) ? " (some capped: lower bounds)" : ""));
        return list;
    }
}