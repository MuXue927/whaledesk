using System.IO;
using System.Text.Json;

namespace WhaleDesk.Interaction;

/// <summary>
/// One clipboard item. Text keeps the original shape; images store a PNG path on disk instead of
/// inline data so the JSON stays small. <see cref="Kind"/> defaults to "text" so histories written
/// by older versions still load.
/// </summary>
internal sealed record ClipEntry(string Text, DateTime CopiedAt)
{
    public string Kind { get; init; } = "text";

    /// <summary>Absolute PNG path for image entries.</summary>
    public string? ImagePath { get; init; }

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    public bool IsImage => Kind == "image";

    public string Preview
    {
        get
        {
            if (IsImage) return $"[图片] {ImageWidth}×{ImageHeight}";
            var flat = Text.Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length > 80 ? flat[..80] + "…" : flat;
        }
    }

    public string TimeLabel => CopiedAt.ToString("HH:mm");
}

/// <summary>
/// Clipboard history, kept locally in the pet's state folder.
///
/// Text is stored inline (capped), images are written as PNG files next to the history JSON and
/// only the path is recorded. The newest entry is de-duplicated instead of repeated, both lists
/// are capped, and objects that are too large are ignored so the state folder cannot grow without
/// bound. Removing/clearing entries deletes the PNG files too.
/// </summary>
internal sealed class ClipboardHistoryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public int Capacity { get; set; } = 60;
    public int MaxImages { get; set; } = 10;
    public int MaxTextLength { get; set; } = 20_000;

    public List<ClipEntry> Entries { get; private set; } = new();

    public static ClipboardHistoryStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(path));
                if (list is not null) return new ClipboardHistoryStore { Entries = list };
            }
        }
        catch { /* a corrupt history must never break startup */ }
        return new ClipboardHistoryStore();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(Entries, Json));
        }
        catch { /* read-only state folder: keep working in memory */ }
    }

    /// <summary>Records copied text. Returns false when it was empty, too large, or an exact repeat.</summary>
    public bool Add(string? text, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Length > MaxTextLength) return false;
        if (Entries.Count > 0 && !Entries[0].IsImage && Entries[0].Text == text) return false;

        // An older identical entry moves back to the top instead of appearing twice.
        Entries.RemoveAll(e => !e.IsImage && e.Text == text);
        Entries.Insert(0, new ClipEntry(text, at));
        PruneText();
        return true;
    }

    /// <summary>Records an image that has already been written to <paramref name="pngPath"/>.</summary>
    public bool AddImage(string pngPath, int width, int height, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(pngPath)) return false;
        if (Entries.Count > 0 && Entries[0].IsImage && Entries[0].ImagePath == pngPath) return false;

        Entries.RemoveAll(e => e.IsImage && e.ImagePath == pngPath);
        Entries.Insert(0, new ClipEntry(string.Empty, at)
        {
            Kind = "image",
            ImagePath = pngPath,
            ImageWidth = width,
            ImageHeight = height,
        });
        PruneImages();
        return true;
    }

    private void PruneText()
    {
        var texts = Entries.Where(e => !e.IsImage).ToList();
        if (texts.Count <= Capacity) return;
        foreach (var extra in texts.Skip(Capacity)) Entries.Remove(extra);
    }

    private void PruneImages()
    {
        var images = Entries.Where(e => e.IsImage).ToList();
        if (images.Count <= MaxImages) return;
        foreach (var extra in images.Skip(MaxImages))
        {
            Entries.Remove(extra);
            TryDelete(extra.ImagePath);
        }
    }

    public void Remove(ClipEntry entry)
    {
        var removed = Entries.RemoveAll(e =>
            e.IsImage == entry.IsImage &&
            e.CopiedAt == entry.CopiedAt &&
            e.Kind == entry.Kind &&
            e.ImagePath == entry.ImagePath &&
            e.Text == entry.Text);
        if (removed > 0) TryDelete(entry.ImagePath);
    }

    public void Clear()
    {
        foreach (var image in Entries.Where(e => e.IsImage)) TryDelete(image.ImagePath);
        Entries.Clear();
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
