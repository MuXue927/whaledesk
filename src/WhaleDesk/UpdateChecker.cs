using System.Net.Http;
using System.Text.Json;

namespace WhaleDesk;

/// <summary>A newer release found on GitHub.</summary>
internal sealed record UpdateInfo(string LatestTag, string ReleaseUrl, string Notes);

/// <summary>
/// "Check for updates" against the GitHub Releases API.
///
/// It is deliberately passive: no download, no silent install. It only tells the user that a new
/// version exists and opens the release page when they ask for it. Everything network-facing is
/// wrapped so an offline / blocked machine just reports "check failed" instead of breaking startup.
/// </summary>
internal static class UpdateChecker
{
    public const string DefaultRepo = "MuXue927/whaledesk";

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

    /// <summary>Returns (info, null) when a newer release exists, (null, null) when up to date.</summary>
    public static async Task<(UpdateInfo? Info, string? Error)> CheckAsync(
        string repo,
        string currentVersion,
        int timeoutMs = 8000,
        CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1500, 30000)) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WhaleDesk-UpdateChecker");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await http
                .GetAsync($"https://api.github.com/repos/{repo}/releases/latest", ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"HTTP {(int)response.StatusCode}");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
            var body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;

            if (!IsNewer(currentVersion, tag)) return (null, null);

            var notes = (body ?? string.Empty).Replace("\r", string.Empty).Trim();
            if (notes.Length > 400) notes = notes[..400] + "…";
            return (new UpdateInfo(tag!.Trim(), url ?? $"https://github.com/{repo}/releases", notes), null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }
}
