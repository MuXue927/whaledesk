using System.IO;
using System.Text.Json;

namespace WhaleDesk.Interaction;

/// <summary>
/// The user's own "keep these" list: apps/folders they want preserved even when unused.
///
/// Inference from process activity can only see what is running right now; someone may keep a
/// game or an old editor installed on purpose. This list is the explicit escape hatch, edited
/// from the analysis window (the ★ on a row) and stored as plain JSON.
/// </summary>
internal sealed class KeepList
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<string> Entries { get; private set; } = new();

    public static KeepList Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
                if (list is not null) return new KeepList { Entries = list };
            }
        }
        catch
        {
            // a corrupt keep list must never block the analyzer
        }
        return new KeepList();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 原子写：进程被杀或断电时不能留下半截 JSON，否则 Load 的 catch 把半截文件当成空集合，
            // 用户的保留清单一次性归零。UiState 早就用 .tmp + Move 了，这里照做。
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Entries, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // read-only location: keep working in memory
        }
    }

    /// <summary>Match by full path prefix or by plain folder name (case-insensitive).</summary>
    public bool Contains(string pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return false;
        foreach (var entry in Entries)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            if (string.Equals(entry, pathOrName, StringComparison.OrdinalIgnoreCase)) return true;
            if (pathOrName.StartsWith(entry.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(Path.GetFileName(pathOrName.TrimEnd('\\', '/')), entry, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Adds the entry if missing; returns true when the list changed.</summary>
    public bool Add(string pathOrName)
    {
        if (Contains(pathOrName)) return false;
        Entries.Add(pathOrName);
        return true;
    }

    public bool Remove(string pathOrName)
    {
        return Entries.RemoveAll(e => string.Equals(e, pathOrName, StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(Path.GetFileName(pathOrName.TrimEnd('\\', '/')), e, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}
