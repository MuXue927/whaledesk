using System.IO;
using System.Text.Json;

namespace WhaleDesk.Monitoring;

/// <summary>One DSH plugin found on disk.</summary>
internal sealed record InstalledPlugin(string Name, string Version, string Path, bool IsDependency);

/// <summary>
/// Read-only inventory of the DSH plugins installed on this machine.
///
/// Why read-only: the plugin-manager HTTP endpoints answer 401 without a login, so the pet cannot
/// install or remove plugins. What it CAN do is tell the user what is installed, whether the two
/// plugins it depends on are present, and where to look - which is exactly what "did my plugins
/// survive the DSH update?" needs.
/// </summary>
internal static class PluginHelper
{
    /// <summary>Plugins the pet's features rely on (name → what breaks without it).</summary>
    public static readonly (string Name, string Why)[] Dependencies =
    {
        ("@linxin666/dsh-pet", "提供 /api/pet/*：会话阶段/气泡/互动。缺了她会变成纯桌面宠物"),
        ("@linxin666/dsh-usage", "写 token 账本：今日 token/成本。缺了统计显示「暂无记账」"),
    };

    /// <summary>Scan &lt;dshHome&gt;\profiles\*\node_modules for plugin packages.</summary>
    public static IReadOnlyList<InstalledPlugin> Scan(string dshHome)
    {
        var found = new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var profiles = Path.Combine(dshHome, "profiles");
            if (!Directory.Exists(profiles)) return Array.Empty<InstalledPlugin>();

            foreach (var profile in Directory.EnumerateDirectories(profiles))
            {
                var modules = Path.Combine(profile, "node_modules");
                if (!Directory.Exists(modules)) continue;

                // scoped packages (@scope/name)
                foreach (var scope in SafeDirs(modules))
                {
                    if (!Path.GetFileName(scope).StartsWith("@", StringComparison.Ordinal)) continue;
                    foreach (var package in SafeDirs(scope)) Record(package, found);
                }
                // unscoped packages
                foreach (var package in SafeDirs(modules))
                {
                    if (Path.GetFileName(package).StartsWith("@", StringComparison.Ordinal)) continue;
                    Record(package, found);
                }
            }
        }
        catch
        {
            // a broken profile must never break the helper
        }

        return found.Values
            .OrderBy(p => p.IsDependency ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.EnumerateDirectories(path).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static void Record(string packageDirectory, Dictionary<string, InstalledPlugin> found)
    {
        try
        {
            var manifest = Path.Combine(packageDirectory, "package.json");
            if (!File.Exists(manifest)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = doc.RootElement;
            if (!root.TryGetProperty("name", out var nameElement)) return;
            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!name.Contains("dsh", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("@linxin666", StringComparison.OrdinalIgnoreCase)) return;

            var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? "?" : "?";
            var isDependency = Dependencies.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            found[name] = new InstalledPlugin(name, version, packageDirectory, isDependency);
        }
        catch
        {
            // unreadable package.json: skip it
        }
    }

    /// <summary>Missing dependencies with the reason why they matter (empty when all present).</summary>
    public static IReadOnlyList<(string Name, string Why)> Missing(IReadOnlyList<InstalledPlugin> installed) =>
        Dependencies
            .Where(d => installed.All(p => !string.Equals(p.Name, d.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
}
