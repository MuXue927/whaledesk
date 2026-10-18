namespace WhaleDesk.Performance;

/// <summary>
/// Disk-pressure tiers for the system drive.
///
/// Percentage alone is misleading: 15% of a 200 GB drive is 30 GB (fine), while 15% of a 40 GB
/// drive is 6 GB (tight). And a nearly-full drive hurts regardless of size, so the tier is the
/// worse of the two readings.
/// </summary>
internal static class DiskGuard
{
    public static int TierForFreePercent(double freePercent)
    {
        if (freePercent <= 0) return 3;
        if (freePercent < 5) return 3;
        if (freePercent < 10) return 2;
        if (freePercent < 15) return 1;
        return 0;
    }

    public static int TierForFreeBytes(ulong freeBytes)
    {
        const ulong gb = 1_000_000_000UL;
        if (freeBytes < 3 * gb) return 3;
        if (freeBytes < 8 * gb) return 2;
        if (freeBytes < 15 * gb) return 1;
        return 0;
    }

    /// <summary>0 = fine, 1 = getting tight, 2 = low, 3 = critical.</summary>
    public static int Tier(ulong freeBytes, ulong totalBytes)
    {
        if (totalBytes == 0) return 0;
        var percent = freeBytes * 100.0 / totalBytes;
        return Math.Max(TierForFreePercent(percent), TierForFreeBytes(freeBytes));
    }

    public static string Advice(int tier) => tier switch
    {
        3 => "C 盘快满了，建议马上清一清",
        2 => "C 盘空间很低了，我帮你看看谁最占地方？",
        1 => "C 盘有点紧，要清理一下吗",
        _ => "",
    };
}
