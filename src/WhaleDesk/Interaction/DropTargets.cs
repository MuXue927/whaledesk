using System.IO;

namespace WhaleDesk.Interaction;

/// <summary>
/// Pure formatting for a drop onto the pet.
///
/// The pet is not a file manager: dropping something gives the user a usable path on the
/// clipboard ("paste it to DSH"), plus a short human-readable confirmation. No file is moved,
/// copied or opened by dropping - dropping is deliberately side-effect free.
/// </summary>
internal static class DropTargets
{
    /// <summary>Exactly what goes on the clipboard: one path per line, CRLF for Windows apps.</summary>
    public static string ClipboardText(IReadOnlyList<string> paths, string? text)
    {
        if (paths.Count > 0) return string.Join("\r\n", paths);
        return text ?? string.Empty;
    }

    /// <summary>Short confirmation for the speech bubble.</summary>
    public static string Preview(IReadOnlyList<string> paths, string? text)
    {
        if (paths.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(text)) return "没拿到内容";
            var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (flat.Length > 24) flat = flat[..24] + "…";
            return $"文字「{flat}」";
        }
        if (paths.Count == 1) return $"「{NameOf(paths[0])}」";
        return $"「{NameOf(paths[0])}」等 {paths.Count} 个";
    }

    public static string NameOf(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    public static bool IsFolder(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    /// <summary>Split a FileDrop list the way the WPF handler gives it to us.</summary>
    public static IReadOnlyList<string> FromFileDrop(string[]? files) =>
        files is null ? Array.Empty<string>() : files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
}
