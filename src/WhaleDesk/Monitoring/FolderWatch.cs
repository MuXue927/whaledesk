using System.IO;
using WhaleDesk.Monitoring;

namespace WhaleDesk.Monitoring;

internal sealed record CompletedFile(string Path, string Name, long Bytes);

/// <summary>
/// "A download / build artifact finished" detector.
///
/// Polling for a file whose size is still growing cannot tell you when it is done, so a file
/// only counts once its size has stayed the same for a few seconds. Tiny files are ignored
/// (browser temp files, thumbnails, .crdownload leftovers).
///
/// Pure over the samples it is fed, so --selftest can pin it.
/// </summary>
internal sealed class FolderWatch
{
    /// <summary>How long the size must stay unchanged before the file counts as finished.</summary>
    public int StableMs { get; set; } = 4_000;

    /// <summary>Only files written this recently count as "just finished" (default 10 min).</summary>
    public int FreshMinutes { get; set; } = 10;

    /// <summary>Ignore anything smaller than this (default 1 MB).</summary>
    public long MinBytes { get; set; } = 1_000_000;

    private readonly Dictionary<string, (long Size, long Since)> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CompletedFile> Sample(IEnumerable<(string Path, long Bytes, DateTime LastWriteUtc)> files, long now)
    {
        var done = new List<CompletedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, bytes, lastWriteUtc) in files)
        {
            seen.Add(path);
            if (bytes < MinBytes) continue;
            if (_reported.Contains(path)) continue;   // baseline or already announced
            // Only announce something written recently: a stale file is not "just downloaded".
            if (DateTime.UtcNow - lastWriteUtc.ToUniversalTime() > TimeSpan.FromMinutes(FreshMinutes)) continue;

            if (_candidates.TryGetValue(path, out var previous))
            {
                if (previous.Size != bytes)
                {
                    _candidates[path] = (bytes, now);   // still growing
                    continue;
                }
                if (now - previous.Since >= StableMs && _reported.Add(path))
                    done.Add(new CompletedFile(path, Path.GetFileName(path), bytes));
            }
            else
            {
                _candidates[path] = (bytes, now);
            }
        }

        // forget files that disappeared so the dictionaries cannot grow forever
        foreach (var path in _candidates.Keys.Where(p => !seen.Contains(p)).ToList()) _candidates.Remove(path);
        if (_reported.Count > 500) _reported.Clear();
        return done;
    }

    /// <summary>Remember a path without ever announcing it (startup baseline).</summary>
    public void MarkKnown(string path) => _reported.Add(path);

    /// <summary>Files in a folder, top level only (enough for Downloads / build folders).</summary>
    public static IEnumerable<(string Path, long Bytes, DateTime LastWriteUtc)> Enumerate(string folder)
    {
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<(string, long, DateTime)>();
            files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<(string, long, DateTime)>();
        }

        var list = new List<(string, long, DateTime)>();
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                list.Add((file, info.Length, info.LastWriteTimeUtc));
            }
            catch { /* vanished */ }
        }
        return list;
    }
}
