using System.IO;
using System.Text.Json;

namespace WhaleDesk;

internal sealed class PetTrack
{
    public string Name = "";
    public int Row;
    public int[] Frames = Array.Empty<int>();
    public int[] Durations = Array.Empty<int>();
    public bool Loop;
    public string? Fallback;
    public int TotalMs => Durations.Sum();
}

/// <summary>
/// The render contract, read from assets/pet-spec.json (a snapshot of
/// GET /api/pet/pets). Track order in the JSON document IS the atlas row order,
/// so it is read with JsonDocument/EnumerateObject, never a Dictionary: a
/// reordered dictionary would silently render the wrong animation per row.
/// </summary>
internal sealed class PetSpec
{
    public string Id = "", DisplayName = "", Renderer = "";
    public string AtlasUrl = "", ManifestUrl = "";
    public int CellWidth, CellHeight, Columns, AtlasRows;
    public int[] Rows = Array.Empty<int>();
    public readonly List<PetTrack> Tracks = new();

    public static PetSpec Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var s = new PetSpec
        {
            Id = Str(r, "id"),
            DisplayName = Str(r, "displayName"),
            Renderer = Str(r, "renderer"),
            AtlasUrl = Str(r, "atlasUrl"),
            ManifestUrl = Str(r, "manifestUrl"),
        };
        var cell = r.GetProperty("cell");
        s.CellWidth = cell.GetProperty("width").GetInt32();
        s.CellHeight = cell.GetProperty("height").GetInt32();
        s.Columns = r.GetProperty("columns").GetInt32();
        s.AtlasRows = r.GetProperty("atlasRows").GetInt32();
        s.Rows = r.GetProperty("rows").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        var row = 0;
        foreach (var prop in r.GetProperty("tracks").EnumerateObject())
        {
            var v = prop.Value;
            var t = new PetTrack
            {
                Name = prop.Name,
                Row = row++,
                Frames = v.GetProperty("frames").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                Durations = v.GetProperty("durations").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                Loop = v.TryGetProperty("loop", out var lp) && lp.ValueKind == JsonValueKind.True,
            };
            if (v.TryGetProperty("fallback", out var fb) && fb.ValueKind == JsonValueKind.String) t.Fallback = fb.GetString();
            s.Tracks.Add(t);
        }
        return s;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public PetTrack Track(string? name) =>
        Tracks.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal)) ?? Tracks[0];

    /// <summary>Every invariant the renderer relies on. A non-empty list means: do not render.</summary>
    public List<string> Validate()
    {
        var p = new List<string>();
        if (Renderer != "sprite2d") p.Add($"renderer is '{Renderer}', expected 'sprite2d'");
        if (Tracks.Count != AtlasRows) p.Add($"tracks({Tracks.Count}) != atlasRows({AtlasRows})");
        if (Rows.Length != AtlasRows) p.Add($"rows({Rows.Length}) != atlasRows({AtlasRows})");
        if (CellWidth <= 0 || CellHeight <= 0) p.Add("cell size is not positive");
        if (Columns <= 0) p.Add("columns is not positive");
        for (var i = 0; i < Tracks.Count && i < Rows.Length; i++)
        {
            var t = Tracks[i];
            if (t.Frames.Length != Rows[i]) p.Add($"row {i} ({t.Name}): frames {t.Frames.Length} != rows[i] {Rows[i]}");
            if (t.Frames.Length != t.Durations.Length) p.Add($"row {i} ({t.Name}): frames {t.Frames.Length} != durations {t.Durations.Length}");
            for (var k = 0; k < t.Frames.Length; k++)
                if (t.Frames[k] != k) p.Add($"row {i} ({t.Name}): frame index {t.Frames[k]} at {k} (expected {k})");
            if (t.Frames.Any(f => f < 0 || f >= Columns)) p.Add($"row {i} ({t.Name}): frame index outside columns({Columns})");
            if (t.Durations.Any(d => d <= 0)) p.Add($"row {i} ({t.Name}): non-positive frame duration");
            if (t.Frames.Length == 0) p.Add($"row {i} ({t.Name}): no frames");
        }
        if (Rows.Any(n => n > Columns)) p.Add("a row declares more frames than there are columns");
        return p;
    }
}
