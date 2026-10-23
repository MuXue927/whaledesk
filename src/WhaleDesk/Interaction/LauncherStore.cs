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
        var unreadable = false;
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
            // 用户是拿记事本手改这个文件的（启动器窗口自带「编辑配置」按钮），少个逗号属于正常
            // 编辑失误而不是"配置损坏"。v0.24.4 在这里直接 fall through 到默认值，并且紧接着
            // Save() 把默认值写回原文件 —— 用户手写的那份就永久没了。现在：留备份、不覆盖。
            unreadable = true;
        }
        var store = Defaults();
        if (unreadable)
        {
            Quarantine(path);
            return store;   // 不写盘：用户还能打开原文件把逗号补上
        }
        try { store.Save(path); } catch { /* read-only location: still usable in memory */ }
        return store;
    }

    /// <summary>
    /// 最近一次 Load() 因为文件读不懂而另存的备份路径（null = 没有发生）。
    /// 调用方可以据此提示用户，而不是让配置"悄悄变回默认值"。
    /// </summary>
    public static string? LastRecoveryBackup { get; private set; }

    private static void Quarantine(string path)
    {
        try
        {
            var backup = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: true);
            LastRecoveryBackup = backup;
        }
        catch
        {
            LastRecoveryBackup = null;
        }
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
