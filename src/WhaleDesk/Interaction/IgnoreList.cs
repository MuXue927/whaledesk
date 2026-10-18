using System.IO;
using System.Text.Json;

namespace WhaleDesk.Interaction;

/// <summary>
/// "Never mention this folder again" list, separate from the keep list:
///   keep   = "this is mine, do not suggest touching it" (still shown),
///   ignore = "stop showing it at all".
/// </summary>
internal sealed class IgnoreList
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<string> Entries { get; private set; } = new();

    public static IgnoreList Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
                if (list is not null) return new IgnoreList { Entries = list };
            }
        }
        catch { /* corrupt file must not break analysis */ }
        return new IgnoreList();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(Entries, Json));
        }
        catch { /* keep working in memory */ }
    }

    /// <summary>Match by full path prefix or by plain folder name.</summary>
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

    public bool Add(string pathOrName)
    {
        if (Contains(pathOrName)) return false;
        Entries.Add(pathOrName);
        return true;
    }

    public bool Remove(string pathOrName) =>
        Entries.RemoveAll(e => string.Equals(e, pathOrName, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(Path.GetFileName(pathOrName.TrimEnd('\\', '/')), e, StringComparison.OrdinalIgnoreCase)) > 0;
}
