using System.Net.Http;
using System.Text.Json;

namespace WhaleDesk;

internal sealed record PetState(string? Animation, string? Bubble, string? Phase, bool SessionActive, string? Name, int? AffinityPoints);

/// <summary>
/// Reads GET /api/pet/state from the running DSH app. The endpoint is proven to
/// answer without any token on loopback; a failure simply means the pet runs in
/// standalone mode, which is not an error worth surfacing to the user.
/// </summary>
internal sealed class PetStateClient : IDisposable
{
    public const string DefaultBase = "http://127.0.0.1:19387";
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public string BaseUrl => _baseUrl;
    public int ConsecutiveFailures { get; private set; }
    public string? LastError { get; private set; }

    public PetStateClient(string baseUrl = DefaultBase)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
    }

    public async Task<PetState?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var text = await _http.GetStringAsync($"{_baseUrl}/api/pet/state", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            var state = new PetState(
                Str(r, "animation"),
                Str(r, "bubble"),
                Str(r, "phase"),
                r.TryGetProperty("sessionActive", out var sa) && sa.ValueKind == JsonValueKind.True,
                Str(r, "name"),
                r.TryGetProperty("affinity", out var aff) && aff.TryGetProperty("points", out var pts) && pts.TryGetInt32(out var v) ? v : null);
            ConsecutiveFailures = 0;
            LastError = null;
            return state;
        }
        catch (Exception ex)
        {
            ConsecutiveFailures++;
            LastError = ex.Message;
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// POST /api/pet/interact — the same call the in-app pet uses, so desktop
    /// petting feeds the same affinity counter. Returns null on success, otherwise
    /// the server's reason (usually a cooldown refusal).
    /// </summary>
    public async Task<string?> InteractAsync(string kind, CancellationToken ct = default)
    {
        try
        {
            var body = new StringContent($"{{\"kind\":\"{kind}\"}}", System.Text.Encoding.UTF8, "application/json");
            var res = await _http.PostAsync($"{_baseUrl}/api/pet/interact", body, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return null;
            return $"HTTP {(int)res.StatusCode} {Truncate(text)}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160];

    public void Dispose() => _http.Dispose();
}
