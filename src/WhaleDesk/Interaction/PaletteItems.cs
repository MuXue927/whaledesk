namespace WhaleDesk.Interaction;

/// <summary>
/// One runnable entry the palette can show. <see cref="SearchText"/> is what gets matched: it can
/// hold aliases the user never sees (English name, pinyin initials typed manually, keywords).
/// </summary>
internal sealed record PaletteItem(string Id, string Title, string Subtitle, string SearchText, int Weight, Action Run);

/// <summary>
/// Builds palette items out of the things the pet already owns - quick-launch entries, clipboard
/// history, snippets, built-in commands - plus the calculator. No IO and no UI here: the window
/// feeds the stores in, so every list stays testable and the providers can be reused later by the
/// quick-launch panel.
/// </summary>
internal static class PaletteItems
{
    public const int WeightCommand = 120;
    public const int WeightSnippet = 110;
    public const int WeightLauncher = 100;
    public const int WeightClipboard = 80;
    public const int WeightCalculator = 150;

    /// <summary>Built-in pet commands. <c>Id</c> is stable and also the usage key.</summary>
    public static IEnumerable<PaletteItem> FromCommands(IEnumerable<(string Id, string Title, string Hint, Action Run)> commands)
    {
        foreach (var (id, title, hint, run) in commands)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || run is null) continue;
            yield return new PaletteItem("cmd:" + id, title, hint ?? "", title + " " + (hint ?? ""), WeightCommand, run);
        }
    }

    public static IEnumerable<PaletteItem> FromLaunchers(LauncherStore? store, Action<Launcher> run)
    {
        if (store is null) yield break;
        foreach (var launcher in store.Visible())
        {
            var name = launcher.Name ?? launcher.Target ?? "";
            if (name.Length == 0) continue;
            yield return new PaletteItem(
                "launcher:" + name + "|" + launcher.Target,
                name,
                launcher.Target ?? "",
                name + " " + (launcher.Target ?? ""),
                WeightLauncher,
                () => run(launcher));
        }
    }

    public static IEnumerable<PaletteItem> FromClipboard(ClipboardHistoryStore? store, Action<string> copy, int limit = 40)
    {
        if (store is null) yield break;
        var taken = 0;
        foreach (var entry in store.Entries)
        {
            if (taken++ >= limit) break;
            if (entry.IsImage || string.IsNullOrWhiteSpace(entry.Text)) continue;
            var preview = entry.Preview;
            yield return new PaletteItem(
                "clip:" + entry.CopiedAt.Ticks,
                preview,
                "剪贴板历史",
                entry.Text,
                WeightClipboard,
                () => copy(entry.Text));
        }
    }

    public static IEnumerable<PaletteItem> FromSnippets(SnippetStore? store, Action<Snippet, string> copyText,
        Action<Snippet>? copyImage = null)
    {
        if (store is null) yield break;
        foreach (var snippet in store.Items)
        {
            if (snippet.IsImage)
            {
                // Image snippets can only be offered when the caller knows how to put an image
                // back on the clipboard.
                if (copyImage is null) continue;
                yield return new PaletteItem(
                    "snippet:" + snippet.Name,
                    snippet.Name,
                    "片段 · [图片]",
                    snippet.Name + " 图片 image",
                    WeightSnippet,
                    () => copyImage(snippet));
                continue;
            }
            var expanded = SnippetStore.Expand(snippet.Text, DateTime.Now);
            yield return new PaletteItem(
                "snippet:" + snippet.Name,
                snippet.Name,
                "片段 · " + Shorten(expanded, 60),
                snippet.Name + " " + expanded,
                WeightSnippet,
                () => copyText(snippet, expanded));
        }
    }

    /// <summary>
    /// A calculator result for a query that is a valid arithmetic expression, otherwise null.
    /// The formatted result is what Enter copies to the clipboard.
    /// </summary>
    public static PaletteItem? Calculator(string? query, Action<string> copy)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        // Require an operator so a bare number does not shadow a real result.
        if (!query.Any(c => c is '+' or '-' or '*' or '/' or '%')) return null;
        if (!SearchEngine.TryEvaluate(query, out var value)) return null;
        var text = FormatResult(value);
        return new PaletteItem("calc:" + query.Trim(), "= " + text, "回车复制这个结果", query, WeightCalculator,
            () => copy(text));
    }

    public static string FormatResult(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15
            ? ((long)Math.Round(value)).ToString()
            : value.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    private static string Shorten(string? text, int max)
    {
        var one = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }
}
