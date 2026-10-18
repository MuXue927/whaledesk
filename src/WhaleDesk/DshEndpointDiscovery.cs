using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace WhaleDesk;

/// <summary>
/// Finds the loopback base URL of the RUNNING DSH instance.
///
/// Why this exists: DSH does not always serve its HTTP API on 19387. On 0.2.1-rc.2 the port was
/// observed to be dynamic (64688 in one session), which left the pet permanently "offline" while
/// everything else looked fine. Hardcoding a port made her a standalone pet instead of a DSH
/// companion, so the base URL is discovered instead:
///   1. the remembered/configured URL (fast path),
///   2. the historical default 19387,
///   3. every other loopback TCP listener - the one that answers /api/pet/state is DSH.
///
/// The probe is cheap (a few hundred ms with a short timeout) and only runs at startup, or again
/// when polling has been failing for a while (DSH restarted on a new port).
/// </summary>
internal static class DshEndpointDiscovery
{
    public static async Task<string?> FindAsync(string? remembered, int probeMs = 450, CancellationToken ct = default)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var normalized = url!.TrimEnd('/');
            if (seen.Add(normalized)) candidates.Add(normalized);
        }

        Add(remembered);
        Add(PetStateClient.DefaultBase);
        Add("http://127.0.0.1:64688");

        // Every other loopback listener: the one answering the pet API is the right one.
        try
        {
            var ports = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => IPAddress.IsLoopback(endpoint.Address))
                .Select(endpoint => endpoint.Port)
                .Where(port => port > 0)
                .Distinct()
                .OrderBy(port => port)
                .ToList();
            foreach (var port in ports) Add($"http://127.0.0.1:{port}");
        }
        catch
        {
            // network information unavailable: the fixed candidates still work
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(probeMs) };
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) return null;
            try
            {
                using var response = await http.GetAsync($"{candidate}/api/pet/state", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    (root.TryGetProperty("sessionActive", out _) || root.TryGetProperty("phase", out _)))
                {
                    return candidate;
                }
            }
            catch
            {
                // not DSH (or closed): try the next candidate
            }
        }
        return null;
    }
}
