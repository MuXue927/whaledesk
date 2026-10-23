using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WhaleDesk;

/// <summary>Auto update check: passive GitHub Releases lookup (bubble + menu entry, no silent install).</summary>
internal sealed partial class PetWindow
{
    /// <summary>
    /// Check GitHub Releases for a newer version. Passive: a bubble plus a menu entry, never a
    /// silent install. --probe-update exercises the same code path.
    /// </summary>
    private async Task CheckUpdateAsync(bool silent)
    {
        _lastUpdateCheckMs = Environment.TickCount64;
        var current = typeof(PetWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var result = await UpdateChecker.CheckAsync(UpdateRepo, current).ConfigureAwait(true);
        if (result.Info is null)
        {
            if (result.Error is null)
            {
                _log.Info($"update check: up to date ({current}) proxy={result.ProxyUsed} attempts={result.Attempts}");
                if (!silent) ShowLocalBubble("已经是最新版本啦", 4000, force: true);
            }
            else
            {
                _log.Warn($"update check failed: {result.Error} | proxy={result.ProxyUsed} | attempts={result.Attempts} | detail={result.Detail}");
                if (!silent) ShowLocalBubble($"{result.Error}（不影响使用，可右键 → 打开发布页）", 6000, force: true);
            }
            return;
        }

        _updateAvailableTag = result.Info.LatestTag;
        _log.Info($"update available: {result.Info.LatestTag} (current {current}) {result.Info.ReleaseUrl} proxy={result.ProxyUsed} attempts={result.Attempts}");
        ShowLocalBubble($"有新版本 {result.Info.LatestTag} 啦，右键 → 检查更新 可打开下载页", 9000, force: true);
        if (!silent) OpenReleasePage(result.Info.ReleaseUrl);
    }

    private void OpenReleasePage(string url)
    {
        // 这个 URL 来自 GitHub API 响应，而更新检查是主动支持系统代理的：代理一旦被劫持，
        // "打开哪个地址"就等于由响应内容决定。只允许 https://github.com/<本仓库>/… ，
        // 其余一律回退到固定模板页面。
        var safe = SafeReleaseUrl(url);
        if (!string.Equals(safe, url, StringComparison.OrdinalIgnoreCase))
            _log.Warn("release url rejected, opening the canonical page instead: " + url);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(safe) { UseShellExecute = true });
            _log.Info("opened release page: " + safe);
        }
        catch (Exception ex)
        {
            _log.Warn("could not open release page: " + ex.Message);
        }
    }

    /// <summary>Only our own repository over https may be opened.</summary>
    private static string SafeReleaseUrl(string? url)
    {
        var canonical = $"https://github.com/{UpdateChecker.DefaultRepo}/releases";
        if (string.IsNullOrWhiteSpace(url)) return canonical;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return canonical;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return canonical;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return canonical;
        if (!uri.AbsolutePath.StartsWith("/" + UpdateChecker.DefaultRepo + "/", StringComparison.OrdinalIgnoreCase)) return canonical;
        return uri.ToString();
    }

    /// <summary>--probe-update: run one live check and print the outcome.</summary>
    private async Task RunUpdateProbeAsync()
    {
        await Task.Delay(600);
        var current = typeof(PetWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _log.Info($"UPDATE PROBE current={current} repo={UpdateRepo}");
        var result = await UpdateChecker.CheckAsync(UpdateRepo, current, timeoutMs: 12000).ConfigureAwait(true);
        _log.Info($"UPDATE PROBE proxy={result.ProxyUsed} attempts={result.Attempts}");
        _log.Info(result.Error is not null
            ? $"UPDATE PROBE result=unreachable error={result.Error} detail={result.Detail}"
            : result.Info is null
                ? "UPDATE PROBE result=up-to-date"
                : $"UPDATE PROBE result=newer tag={result.Info.LatestTag} url={result.Info.ReleaseUrl}");
    }

    /// <summary>--probe-clipboard-image: put a synthetic bitmap on the clipboard and prove it is stored.</summary>
    private async Task RunClipboardImageProbeAsync()
    {
        await Task.Delay(600);
        string? file = null;
        try
        {
            const int side = 64;
            var pixels = new byte[side * side * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 40;
                pixels[i + 1] = 120;
                pixels[i + 2] = 220;
                pixels[i + 3] = 255;
            }
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(side, side, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, side * 4);
            using var stream = new MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            encoder.Save(stream);

            var dir = Path.Combine(Path.GetDirectoryName(_opts.StatePath) ?? ".", "clipboard-images");
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, $"probe-{DateTime.Now:HHmmss-fff}.png");
            File.WriteAllBytes(file, stream.ToArray());

            _clipHistory ??= Interaction.ClipboardHistoryStore.Load(ClipHistoryPath);
            var added = _clipHistory.AddImage(file, side, side, DateTime.Now);
            _clipHistory.Save(ClipHistoryPath);
            var entry = _clipHistory.Entries.FirstOrDefault(e => e.IsImage && e.ImagePath == file);
            var exists = entry is not null && File.Exists(file);
            _log.Info($"CLIPIMAGE PROBE added={added} entry={entry is not null} fileExists={exists} preview={entry?.Preview}");
            _log.Info($"CLIPIMAGE PROBE verdict={(added && exists ? "PASS" : "FAIL")} (clipboard untouched)");

            if (entry is not null) { _clipHistory.Remove(entry); _clipHistory.Save(ClipHistoryPath); }
            if (file is not null && File.Exists(file)) File.Delete(file);
            _log.Info("CLIPIMAGE PROBE cleaned up");
        }
        catch (Exception ex)
        {
            _log.Warn("CLIPIMAGE PROBE failed: " + ex.Message);
        }
    }
}
