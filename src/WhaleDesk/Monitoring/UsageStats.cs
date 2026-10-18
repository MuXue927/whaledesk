using System.IO;
using System.Text.Json;

namespace WhaleDesk.Monitoring;

internal sealed record UsageTotals(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long Calls,
    double Cost)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens;
}

/// <summary>
/// Token / cost statistics, read from the ledger the installed `dsh-usage` plugin keeps at
/// ~/.dsh/dsh-usage/usage-ledger.json.
///
/// That file is the authoritative source: DSH's HTTP API answers 401 without auth for every
/// usage endpoint, while the ledger is plain JSON on disk. Its shape is:
///   { "days": { "2026-10-17": { "&lt;account&gt;": { "&lt;model&gt;": {
///       "inputTokens": .., "outputTokens": .., "cacheReadTokens": ..,
///       "cacheWriteTokens": .., "reasoningTokens": .., "calls": .., "cost": .. } } } } }
///
/// The parser is a pure function over the JSON text so --selftest can pin it.
/// </summary>
internal static class UsageStats
{
    public static UsageTotals ParseLedger(string json, DateOnly day)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("days", out var days)) return new UsageTotals(0, 0, 0, 0, 0);
            var key = day.ToString("yyyy-MM-dd");
            if (!days.TryGetProperty(key, out var accounts)) return new UsageTotals(0, 0, 0, 0, 0);

            long input = 0, output = 0, cacheRead = 0, calls = 0;
            double cost = 0;
            foreach (var account in accounts.EnumerateObject())
            {
                foreach (var model in account.Value.EnumerateObject())
                {
                    input += ReadLong(model.Value, "inputTokens");
                    output += ReadLong(model.Value, "outputTokens");
                    cacheRead += ReadLong(model.Value, "cacheReadTokens");
                    calls += ReadLong(model.Value, "calls");
                    if (model.Value.TryGetProperty("cost", out var c) && c.TryGetDouble(out var d)) cost += d;
                }
            }
            return new UsageTotals(input, output, cacheRead, calls, cost);
        }
        catch
        {
            return new UsageTotals(0, 0, 0, 0, 0);
        }
    }

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    public static string LedgerPath => Path.Combine(Cleanup.CleanupCatalog.DshHome, "dsh-usage", "usage-ledger.json");

    private static DateTime _cachedAtUtc = DateTime.MinValue;
    private static UsageTotals _cached = new(0, 0, 0, 0, 0);

    /// <summary>Today's totals, cached for a few seconds (the ledger is small but this runs often).</summary>
    public static UsageTotals Today(int cacheSeconds = 10)
    {
        var now = DateTime.UtcNow;
        if ((now - _cachedAtUtc).TotalSeconds < cacheSeconds) return _cached;
        _cachedAtUtc = now;
        try
        {
            if (!File.Exists(LedgerPath)) return _cached = new UsageTotals(0, 0, 0, 0, 0);
            var json = File.ReadAllText(LedgerPath);
            _cached = ParseLedger(json, DateOnly.FromDateTime(DateTime.Now));
        }
        catch
        {
            _cached = new UsageTotals(0, 0, 0, 0, 0);
        }
        return _cached;
    }
}
