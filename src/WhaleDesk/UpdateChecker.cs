using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhaleDesk;

/// <summary>A newer release found on GitHub.</summary>
internal sealed record UpdateInfo(string LatestTag, string ReleaseUrl, string Notes);

/// <summary>Outcome of one update check, with friendly text for the bubble and detail for the log.</summary>
internal sealed record UpdateCheckResult(
    UpdateInfo? Info,
    string? Error,
    string? Detail,
    string ProxyUsed,
    string Attempts);

/// <summary>
/// "Check for updates" against the GitHub Releases API.
///
/// It is deliberately passive: no download, no silent install. It only tells the user that a new
/// version exists and opens the release page when they ask for it. Everything network-facing is
/// wrapped so an offline / blocked machine just reports "check failed" instead of breaking startup.
///
/// v0.24.3: use the user's proxy explicitly, fall back to the releases HTML page, and keep the raw
/// exception in the log while showing a friendly sentence in the bubble.
/// </summary>
internal static class UpdateChecker
{
    public const string DefaultRepo = "MuXue927/whaledesk";

    private const int MaxJsonHtmlChars = 2_000_000;

    /// <summary>Parses tags like "v1.2.3", "1.2.3" or "release-1.2.3"; returns null for anything else.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag!.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];
        return Version.TryParse(text, out var version) ? version : null;
    }

    public static bool IsNewer(string currentVersion, string? latestTag)
    {
        if (!Version.TryParse(currentVersion, out var current)) return false;
        var latest = ParseTag(latestTag);
        return latest is not null && latest > current;
    }

    /// <summary>Normally a proxy server string from env/registry; normalises "127.0.0.1:7897" to a URL.</summary>
    internal static string? NormaliseProxy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim().Trim('"');
        if (text.Length == 0) return null;
        // Registry may hold "http=host:port;https=host:port"; prefer the https/http entry.
        if (text.Contains(';') || text.Contains('='))
        {
            var chosen = text
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                ?? text
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase));
            if (chosen is not null) text = chosen[(chosen.IndexOf('=') + 1)..];
        }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri.ToString() : null;
    }

    /// <summary>Extracts a version tag from a releases page URL or from HTML containing /releases/tag/&lt;tag&gt;.</summary>
    internal static string? ExtractTag(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text!, @"/releases/tag/([A-Za-z0-9._\-]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Resolves the proxy: explicit override → env vars → Windows Internet Settings.</summary>
    internal static (string? Url, string Source) ResolveProxy(string? proxyOverride = null)
    {
        var explicitUrl = NormaliseProxy(proxyOverride);
        if (explicitUrl is not null) return (explicitUrl, "option");

        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            var url = NormaliseProxy(value);
            if (url is not null) return (url, "env:" + name);
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var enabled = key?.GetValue("ProxyEnable");
            var server = key?.GetValue("ProxyServer") as string;
            if (enabled is int flag && flag != 0)
            {
                var url = NormaliseProxy(server);
                if (url is not null) return (url, "system");
            }
        }
        catch
        {
            // registry unreadable: behave like a direct connection
        }

        return (null, "direct");
    }

    /// <summary>
    /// Runs one check. Never throws: transports errors become friendly Error + technical Detail.
    /// First tries the JSON API, then the HTML releases page (which often works through proxies
    /// that block the API host).
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        string repo,
        string currentVersion,
        int timeoutMs = 15000,
        string? proxyOverride = null,
        CancellationToken ct = default)
    {
        var (proxyUrl, proxySource) = ResolveProxy(proxyOverride);
        var attempts = new System.Text.StringBuilder();
        attempts.Append("proxy=").Append(proxySource).Append(proxyUrl is null ? "" : "(" + proxyUrl + ")");

        IWebProxy? proxy = null;
        if (proxyUrl is not null)
        {
            proxy = new WebProxy(proxyUrl) { BypassProxyOnLocal = false };
        }

        using var handler = new HttpClientHandler
        {
            UseProxy = proxy is not null,
            Proxy = proxy,
            AllowAutoRedirect = true,
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 3000, 60000)),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WhaleDesk-UpdateChecker");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        var detail = new List<string>();

        // 1) JSON API
        try
        {
            attempts.Append(" | api");
            using var response = await http
                .GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (json.Length <= MaxJsonHtmlChars)
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
                    var url = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
                    var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
                    return BuildResult(currentVersion, tag, url, body, proxySource, attempts.ToString(), detail);
                }
            }
            else
            {
                detail.Add($"api HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            detail.Add("api " + ex.GetType().Name + ": " + ex.Message);
        }

        // 2) HTML releases page (works when api.github.com is blocked but github.com is reachable)
        try
        {
            attempts.Append(" | html");
            using var response = await http
                .GetAsync($"https://github.com/{repo}/releases/latest", ct)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var finalUrl = response.RequestMessage?.RequestUri?.ToString();
                var tag = ExtractTag(finalUrl);
                if (tag is null)
                {
                    var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (html.Length <= MaxJsonHtmlChars) tag = ExtractTag(html);
                }
                if (tag is not null)
                {
                    return BuildResult(currentVersion, tag, null, null, proxySource, attempts.ToString(), detail);
                }
            }
            else
            {
                detail.Add($"html HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            detail.Add("html " + ex.GetType().Name + ": " + ex.Message);
        }

        var friendly = FriendlyError(detail);
        return new UpdateCheckResult(
            null,
            friendly,
            detail.Count == 0 ? null : string.Join(" | ", detail),
            proxySource,
            attempts.ToString());
    }

    private static UpdateCheckResult BuildResult(
        string currentVersion, string? tag, string? htmlUrl, string? body,
        string proxySource, string attempts, List<string> detail)
    {
        var releaseUrl = htmlUrl ?? $"https://github.com/{DefaultRepo}/releases";
        var notes = (body ?? string.Empty).Replace("\r", string.Empty).Trim();
        if (notes.Length > 400) notes = notes[..400] + "…";
        if (IsNewer(currentVersion, tag))
        {
            return new UpdateCheckResult(
                new UpdateInfo(tag!.Trim(), releaseUrl, notes), null,
                detail.Count == 0 ? null : string.Join(" | ", detail), proxySource, attempts);
        }
        return new UpdateCheckResult(null, null, null, proxySource, attempts);
    }

    private static string FriendlyError(List<string> detail)
    {
        var all = string.Join(" | ", detail);
        if (all.Contains("403", StringComparison.Ordinal))
            return "GitHub 限流了，请过几分钟再试";
        if (all.Contains("404", StringComparison.Ordinal))
            return "发布仓库不存在（版本检查地址可能有误）";
        if (all.Length == 0 || all.Contains("TaskCanceled", StringComparison.OrdinalIgnoreCase) ||
            all.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return "连接 GitHub 超时（网络受限或代理未生效）";
        return "无法连接 GitHub（网络受限或代理未生效）";
    }
}
