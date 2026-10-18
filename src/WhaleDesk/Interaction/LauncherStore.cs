using System.IO;
using System.Text.Json;

namespace WhaleDesk.Interaction;

internal sealed record Launcher(string Name, string Target, string? Arguments = null, string? WorkingDirectory = null);

/// <summary>
/// User-editable quick-launch list, stored as JSON next to the pet's state.
///
/// Defaults only use targets that exist on every Windows install, so the feature is useful
/// immediately; the file can then be edited by hand (the launcher window has a button that
/// opens it in Notepad).
/// </summary>
internal sealed class LauncherStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<Launcher> Items { get; private set; } = new();

    public static LauncherStore Defaults()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        var docs = Path.Combine(profile, "Documents");
        return new LauncherStore
        {
            Items = new List<Launcher>
            {
                new("资源管理器", "explorer.exe", null, profile),
                new("任务管理器", "taskmgr.exe"),
                new("下载文件夹", Directory.Exists(downloads) ? downloads : profile, null, profile),
                new("文档文件夹", Directory.Exists(docs) ? docs : profile, null, profile),
                new("PowerShell", "powershell.exe", null, profile),
                new("系统设置", "ms-settings:"),
            },
        };
    }

    /// <summary>Load from disk; a missing or corrupt file yields the defaults instead of failing.</summary>
    public static LauncherStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<Launcher>>(File.ReadAllText(path));
                if (list is { Count: > 0 })
                {
                    // Older files predate WorkingDirectory; a shell started without one opens in
                    // the pet's own folder, which surprised the user. Fill it in on load.
                    var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    for (var i = 0; i < list.Count; i++)
                    {
                        var item = list[i];
                        if (string.IsNullOrWhiteSpace(item.WorkingDirectory) &&
                            (item.Target.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
                             item.Target.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ||
                             item.Target.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)))
                        {
                            list[i] = item with { WorkingDirectory = profile };
                        }
                    }
                    return new LauncherStore { Items = list };
                }
            }
        }
        catch
        {
            // fall through to defaults
        }
        var store = Defaults();
        try { store.Save(path); } catch { /* read-only location: still usable in memory */ }
        return store;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(Items, Json));
    }

    /// <summary>Only entries with a name and a target are worth showing.</summary>
    public IReadOnlyList<Launcher> Visible() =>
        Items.Where(i => !string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(i.Target)).ToList();

    public LauncherStore Remove(Launcher launcher)
    {
        Items.RemoveAll(i => i.Name == launcher.Name && i.Target == launcher.Target);
        return this;
    }

    public LauncherStore Add(Launcher launcher)
    {
        Items.Add(launcher);
        return this;
    }
}
