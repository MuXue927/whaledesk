using System.IO;
using System.Text.Json;

namespace WhaleDesk.Interaction;

/// <summary>One named snippet the user pinned for reuse: either text (with placeholders) or an image.</summary>
internal sealed class Snippet
{
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Absolute PNG path for image snippets (a copy kept in the pet's state folder).</summary>
    public string? ImagePath { get; set; }
    public long CreatedAtMs { get; set; }

    public bool IsImage => !string.IsNullOrEmpty(ImagePath);
}

/// <summary>
/// Named clipboard snippets ("templates") that sit above the history: unlike history entries they
/// are kept deliberately and survive the history being cleared.
///
/// Text snippets can carry placeholders expanded on paste: <c>{date}</c> / <c>{time}</c>.
/// Image snippets keep their own PNG copy under the pet's state folder, so clearing the clipboard
/// history (which deletes history PNGs) does not break them.
/// </summary>
internal sealed class SnippetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private const int MaxSnippets = 50;
    private const int MaxTextLength = 20_000;

    public List<Snippet> Items { get; private set; } = new();

    public static SnippetStore Load(string path)
    {
        var store = new SnippetStore();
        try
        {
            if (!File.Exists(path)) return store;
            var parsed = JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(path));
            if (parsed is not null)
            {
                store.Items = parsed
                    .Where(s => !string.IsNullOrWhiteSpace(s.Text) || !string.IsNullOrEmpty(s.ImagePath))
                    .ToList();
            }
        }
        catch
        {
            // A hand-edited or corrupt file must not stop the app from opening the history.
        }
        return store;
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // snippets are a convenience; a read-only state folder must not break the pet
        }
    }

    /// <summary>Adds (or replaces, by name) a text snippet. Returns the stored snippet.</summary>
    public Snippet Add(string name, string text, DateTime now)
    {
        var cleanName = CleanName(name, text);
        var cleanText = text ?? "";
        if (cleanText.Length > MaxTextLength) cleanText = cleanText[..MaxTextLength];

        var existing = Find(cleanName);
        if (existing is not null)
        {
            existing.Text = cleanText;
            existing.ImagePath = null;   // a name now refers to text
            return existing;
        }
        var snippet = new Snippet { Name = cleanName, Text = cleanText, CreatedAtMs = Stamp(now) };
        Insert(snippet);
        return snippet;
    }

    /// <summary>
    /// Adds (or replaces, by name) an image snippet: the PNG is copied into
    /// <paramref name="imageDir"/> so it survives the clipboard history being pruned or cleared.
    /// Returns null when the source file cannot be read or copied.
    /// </summary>
    public Snippet? AddImage(string name, string sourcePath, string imageDir, DateTime now)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
            Directory.CreateDirectory(imageDir);
            var cleanName = string.IsNullOrWhiteSpace(name) ? "图片片段" : CleanName(name, "");
            var target = Path.Combine(imageDir, $"snippet-{Stamp(now)}-{Guid.NewGuid().ToString("N")[..6]}.png");
            File.Copy(sourcePath, target, overwrite: false);

            var existing = Find(cleanName);
            if (existing is not null)
            {
                var old = existing.ImagePath;
                existing.ImagePath = target;
                existing.Text = "";
                DeleteQuietly(old);
                return existing;
            }
            var snippet = new Snippet { Name = cleanName, Text = "", ImagePath = target, CreatedAtMs = Stamp(now) };
            Insert(snippet);
            return snippet;
        }
        catch
        {
            return null;
        }
    }

    public void Remove(Snippet snippet, bool deleteImageFile = true)
    {
        if (!Items.Remove(snippet)) return;
        if (deleteImageFile) DeleteQuietly(snippet.ImagePath);
    }

    /// <summary>Expands <c>{date}</c> / <c>{time}</c>; everything else is copied verbatim.</summary>
    public static string Expand(string text, DateTime now) =>
        (text ?? "")
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HH:mm"), StringComparison.OrdinalIgnoreCase);

    /// <summary>Default name when the caller has none: the first non-empty line, trimmed.</summary>
    public static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "片段";
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.Length > 40 ? trimmed[..40] : trimmed;
        }
        return "片段";
    }

    private Snippet? Find(string name) =>
        Items.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    private void Insert(Snippet snippet)
    {
        Items.Add(snippet);
        if (Items.Count > MaxSnippets)
        {
            var oldest = Items.OrderBy(s => s.CreatedAtMs).First();
            Items.Remove(oldest);
            DeleteQuietly(oldest.ImagePath);
        }
    }

    private static string CleanName(string? name, string fallbackText)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length == 0) clean = FirstLine(fallbackText);
        if (clean.Length > 40) clean = clean[..40];
        return clean.Length == 0 ? "片段" : clean;
    }

    private static long Stamp(DateTime now) => now.Ticks / TimeSpan.TicksPerMillisecond;

    private static void DeleteQuietly(string? path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
        catch { /* the PNG stays behind; harmless */ }
    }
}
