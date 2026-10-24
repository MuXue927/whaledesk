using System.IO;
using System.Text.Json;

namespace WhaleDesk.Interaction;

/// <summary>How often and how recently one palette entry was used.</summary>
internal sealed class UsageEntry
{
    public int Count { get; set; }
    public long LastMs { get; set; }
}

/// <summary>
/// Remembers which palette entries the user actually picks, so the ranking can put them first.
///
/// The boost is deliberately bounded (0..1.5) and multiplied into the match score: on the pet's
/// palette the same rule as PowerToys Run applies - habit may reorder comparable results, but it
/// must never lift a weak match over an exact one. That rule has a selftest.
/// </summary>
internal sealed class UsageStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private const int MaxEntries = 300;

    public Dictionary<string, UsageEntry> Items { get; private set; } = new(StringComparer.Ordinal);

    public static UsageStore Load(string path)
    {
        var store = new UsageStore();
        try
        {
            if (!File.Exists(path)) return store;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, UsageEntry>>(File.ReadAllText(path));
            if (parsed is not null) store.Items = new Dictionary<string, UsageEntry>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            // usage history is an optimisation; never let a corrupt file break the palette
        }
        return store;
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Keep the file small: the most recent 300 entries are plenty for ranking.
            if (Items.Count > MaxEntries)
            {
                foreach (var stale in Items.OrderByDescending(kv => kv.Value.LastMs).Skip(MaxEntries).ToList())
                    Items.Remove(stale.Key);
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    public void Record(string id, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (!Items.TryGetValue(id, out var entry))
        {
            entry = new UsageEntry();
            Items[id] = entry;
        }
        entry.Count++;
        entry.LastMs = now.Ticks / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>Frequency and recency folded into one 0..1.5 factor.</summary>
    public double Boost(string id, DateTime now) => Boost(Items, id, now);

    /// <summary>Pure version, used by the selftest.</summary>
    public static double Boost(IReadOnlyDictionary<string, UsageEntry> items, string id, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(id) || !items.TryGetValue(id, out var entry)) return 0;
        // count 1 -> 0.25, count 5 -> 0.63, count 20 -> 0.87: useful but never saturating.
        var frequency = entry.Count / (entry.Count + 3.0) * 0.9;
        var ageDays = Math.Max(0, (now.Ticks / TimeSpan.TicksPerMillisecond - entry.LastMs) / 86_400_000.0);
        var recency = ageDays switch
        {
            < 1 => 0.6,
            < 3 => 0.4,
            < 7 => 0.2,
            < 14 => 0.1,
            _ => 0,
        };
        return Math.Clamp(frequency + recency, 0, 1.5);
    }
}
