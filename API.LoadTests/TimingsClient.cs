using System.Net.Http.Json;
using System.Text.Json;
using API.Diagnostics;

namespace API.LoadTests;

/// <summary>
/// Reads the API's per-stage breakdown. A client-side p50 of 2.5ms is one opaque number covering
/// the load generator, Kestrel, signature validation, the Redis round trip and serialisation; this
/// is what says how much of it the revocation check is.
/// </summary>
public sealed class TimingsClient(string apiUrl)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http = new() { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Called after the warm-up, so warm-up requests stay out of the percentiles.</summary>
    public async Task ResetAsync() =>
        (await _http.PostAsync("/diagnostics/timings/reset", content: null)).EnsureSuccessStatusCode();

    public async Task<IReadOnlyList<StageTimings>> ReadAsync() =>
        await _http.GetFromJsonAsync<List<StageTimings>>("/diagnostics/timings", Json) ?? [];
}
