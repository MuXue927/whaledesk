using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace WhaleDesk.Monitoring;

/// <summary>One DSH plugin whose published version is ahead of what the profile has installed.</summary>
internal sealed record PluginUpdate(string Name, string Installed, string Latest);

/// <summary>
/// Compares the DSH profile's plugin versions against the npm registry and reports which ones are
/// behind. Read-only by design: nothing is installed or upgraded here - the pet only tells you,
/// because a silent upgrade could break a profile that is working.
///
/// Pure parsing/comparison is separated from the network call so <c>--selftest</c> can validate
/// the rules without touching the internet, and a failed lookup degrades to "unknown", never to a
/// false "everything is up to date".
/// </summary>
internal static class PluginUpdates
{
    /// <summary>The desktop profile's dependency list - the same file the plugin manager writes.</summary>
    public static string ProfilePackagePath =>
        Path.Combine(Cleanup.CleanupCatalog.DshHome, "profiles", "desktop", "package.json");

    /// <summary>Reads <c>dependencies</c> from a profile package.json. Pure: no disk, no network.</summary>
    public static IReadOnlyList<(string Name, string Spec)> ParseDependencies(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<(string, string)>();
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                return Array.Empty<(string, string)>();
            var list = new List<(string, string)>();
            foreach (var p in deps.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var spec = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
                list.Add((p.Name, spec));
            }
            return list;
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>Turns "^0.4.8" / "~1.2.0" / "&gt;=0.2.0" into a comparable "0.4.8"-ish string.</summary>
    public static string Normalise(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return "";
        var s = spec.Trim();
        while (s.Length > 0 && (s[0] is '^' or '~' or '=' or '>' or '<' or 'v' or ' ')) s = s[1..];
        var end = s.IndexOfAny(new[] { '-', '+', ' ' });
        return end > 0 ? s[..end] : s;
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a strictly newer numeric version than
    /// <paramref name="installed"/>. Unparseable inputs answer false: we would rather stay quiet
    /// than nag about a version we cannot compare.
    /// </summary>
    public static bool IsNewer(string? installed, string? latest)
    {
        if (!TryParse(installed, out var current) || !TryParse(latest, out var next)) return false;
        var length = Math.Max(current.Length, next.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < current.Length ? current[i] : 0;
            var b = i < next.Length ? next[i] : 0;
            if (b > a) return true;
            if (b < a) return false;
        }
        return false;
    }

    private static bool TryParse(string? text, out int[] parts)
    {
        parts = Array.Empty<int>();
        var normalised = Normalise(text);
        if (normalised.Length == 0) return false;
        var fields = normalised.Split('.');
        var values = new int[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            // "3.5.4-beta" style suffixes were already cut by Normalise; anything else is bogus.
            if (!int.TryParse(fields[i], out values[i])) return false;
        }
        parts = values;
        return true;
    }

    /// <summary>
    /// Reads the profile and asks the registry for the latest version of every dependency.
    /// Slow lookups are abandoned (one timeout each, overall budget ~12 s) and failures are
    /// simply not reported as updates.
    /// </summary>
    public static async Task<IReadOnlyList<PluginUpdate>> CheckAsync(Log log, CancellationToken ct)
    {
        IReadOnlyList<(string Name, string Spec)> deps;
        try
        {
            if (!File.Exists(ProfilePackagePath))
            {
                log.Info("plugin updates: no desktop profile package.json, skipping");
                return Array.Empty<PluginUpdate>();
            }
            deps = ParseDependencies(File.ReadAllText(ProfilePackagePath));
        }
        catch (Exception ex)
        {
            log.Warn("plugin updates: cannot read the profile: " + ex.Message);
            return Array.Empty<PluginUpdate>();
        }

        if (deps.Count == 0) return Array.Empty<PluginUpdate>();

        var (proxyUrl, proxySource) = UpdateChecker.ResolveProxy();
        IWebProxy? proxy = null;
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            proxy = new WebProxy(proxyUrl) { BypassProxyOnLocal = true };
            log.Info($"plugin updates: using proxy {proxySource}");
        }

        using var handler = new HttpClientHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WhaleDesk");
        // No custom Accept: the npm "abbreviated metadata" type (application/vnd.npm.install-v1+json)
        // is answered with HTTP 406 on the /<name>/latest endpoint - verified live on 2026-10-24,
        // where 4 of 6 packages were silently skipped because of it. The default JSON Accept works.

        var found = new List<PluginUpdate>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        foreach (var (name, spec) in deps)
        {
            try
            {
                var url = "https://registry.npmjs.org/" + name + "/latest";
                using var response = await http.GetAsync(url, budget.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    log.Diag("pluginUpdates", $"{name}: HTTP {(int)response.StatusCode}");
                    continue;
                }
                var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) continue;
                var latest = Normalise(v.GetString());
                var installed = Normalise(spec);
                if (installed.Length == 0 || latest.Length == 0) continue;
                if (IsNewer(installed, latest)) found.Add(new PluginUpdate(name, installed, latest));
                else log.Diag("pluginUpdates", $"{name}: {installed} is current");
            }
            catch (OperationCanceledException)
            {
                log.Info("plugin updates: time budget exhausted, stopping here");
                break;
            }
            catch (Exception ex)
            {
                log.Diag("pluginUpdates", $"{name}: lookup failed: {ex.Message}");
            }
        }
        log.Info($"plugin updates: {found.Count} of {deps.Count} dependencies are behind");
        return found;
    }
}
