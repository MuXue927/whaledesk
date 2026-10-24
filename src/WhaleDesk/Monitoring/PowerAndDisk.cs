using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WhaleDesk.Monitoring;

/// <summary>Battery state as reported by GetSystemPowerStatus (no WMI, no COM, no dependencies).</summary>
internal readonly record struct BatteryInfo(int Percent, bool OnExternalPower, bool Charging, bool Low)
{
    public string Describe() =>
        $"电池 {Percent}%" + (OnExternalPower ? (Charging ? "（充电中）" : "（交流电）") : "（放电中）");
}

/// <summary>One daily free-space reading for a drive.</summary>
internal readonly record struct DiskSample(string Drive, string Date, long FreeBytes);

/// <summary>
/// Small health extras: battery state, and a slow free-space trend that answers "how many days
/// until this drive is full" instead of just "how full is it now".
///
/// Both are deliberately read-only and cheap: GetSystemPowerStatus is a single syscall, and the
/// trend is one line appended to a small JSON file at most once a day. Everything that can be a
/// pure function is one, so --selftest can validate the maths without touching the real machine.
/// </summary>
internal static class PowerAndDisk
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemPowerStatus")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// <summary>False on a desktop without a battery, or when the API refuses.</summary>
    public static bool TryGetBattery(out BatteryInfo info)
    {
        info = default;
        try
        {
            if (!GetSystemPowerStatus(out var status)) return false;
            // BatteryLifePercent 255 = unknown (desktop / no battery).
            if (status.BatteryLifePercent == 255) return false;
            var percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
            var onAc = status.ACLineStatus == 1;
            var charging = (status.BatteryFlag & 0x08) != 0;
            var low = (status.BatteryFlag & 0x04) != 0 || (percent <= 20 && !onAc);
            info = new BatteryInfo(percent, onAc, charging, low);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Warn only once per low-battery episode: 20% or less, not plugged in. Without the
    /// "already warned" flag the pet used to warn again for every percent the battery dropped
    /// (20, 19, 18...) - a bubble per minute while draining.
    /// </summary>
    public static bool ShouldWarnLowBattery(BatteryInfo battery, bool alreadyWarned) =>
        !alreadyWarned && !battery.OnExternalPower && battery.Percent <= 20;

    /// <summary>The warning re-arms only after plugging in or recovering above 25%.</summary>
    public static bool RearmLowBattery(BatteryInfo battery) =>
        battery.OnExternalPower || battery.Percent > 25;

    // ---------------------------------------------------------------- disk trend

    /// <summary>Keeps the trend file small: 14 daily readings is plenty.</summary>
    private const int MaxSamples = 14;

    public static IReadOnlyList<DiskSample> ParseTrend(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<DiskSample>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<DiskSample>();
            var list = new List<DiskSample>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var drive = e.TryGetProperty("drive", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var date = e.TryGetProperty("date", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : null;
                if (!e.TryGetProperty("free", out var f) || f.ValueKind != JsonValueKind.Number || !f.TryGetInt64(out var free)) continue;
                if (string.IsNullOrEmpty(drive) || string.IsNullOrEmpty(date)) continue;
                list.Add(new DiskSample(drive, date, free));
            }
            return list;
        }
        catch
        {
            return Array.Empty<DiskSample>();
        }
    }

    /// <summary>
    /// Appends today's reading (replacing an earlier reading from the same day), drops anything
    /// older than the sample budget, and saves. Returns the resulting list; never throws.
    /// </summary>
    public static IReadOnlyList<DiskSample> RecordSample(string path, string drive, long freeBytes, DateTime localNow)
    {
        var today = localNow.ToString("yyyy-MM-dd");
        var existing = ParseTrend(SafeRead(path))
            .Where(s => !(s.Drive == drive && s.Date == today))
            .ToList();
        existing.Add(new DiskSample(drive, today, freeBytes));
        var kept = existing
            .OrderBy(s => s.Date, StringComparer.Ordinal)
            .TakeLast(MaxSamples)
            .ToList();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(kept, new JsonSerializerOptions { WriteIndented = false });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // the trend is a nicety; a read-only state folder must not break the pet
        }
        return kept;
    }

    /// <summary>
    /// Net change per day and the projected "days until full", from the samples of one drive.
    /// Returns (0, 0) when there is not enough history to say anything honest.
    /// </summary>
    public static (double PerDayMb, int DaysUntilFull) Estimate(IReadOnlyList<DiskSample> samples, string drive, long currentFreeBytes)
    {
        var mine = samples.Where(s => s.Drive == drive).OrderBy(s => s.Date, StringComparer.Ordinal).ToList();
        if (mine.Count < 2) return (0, 0);
        var first = mine[0];
        var last = mine[^1];
        if (!DateTime.TryParse(first.Date, out var firstDate) || !DateTime.TryParse(last.Date, out var lastDate)) return (0, 0);
        var days = (lastDate - firstDate).TotalDays;
        if (days < 0.5) return (0, 0);

        // Use the live reading as "now" so the projection does not lag a day behind.
        var deltaBytes = currentFreeBytes - first.FreeBytes;
        var perDay = deltaBytes / days;
        var perDayMb = perDay / (1024.0 * 1024.0);
        if (perDay >= 0) return (perDayMb, 0);          // space is not shrinking: nothing to predict
        var daysLeft = (int)Math.Ceiling(currentFreeBytes / -perDay);
        return (perDayMb, Math.Max(0, daysLeft));
    }

    private static string? SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
