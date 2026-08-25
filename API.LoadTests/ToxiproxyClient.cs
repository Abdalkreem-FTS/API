using System.Net;
using System.Net.Http.Json;

namespace API.LoadTests;

/// <summary>
/// Puts a measured amount of latency between the API and Redis. On loopback a round trip costs
/// well under a millisecond, which makes the Redis check look free; a managed instance one
/// availability zone away costs 0.5-2ms, and that is the difference between the revocation
/// check being a rounding error and being most of the request budget.
/// </summary>
public sealed class ToxiproxyClient(string baseUrl)
{
    private const string ProxyName = "redis";

    private const string ToxicName = "latency";

    private const string StallToxicName = "stall";

    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl) };

    public async Task<bool> IsRunningAsync()
    {
        try
        {
            return (await _http.GetAsync("/version")).IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>Removes any latency toxic, so the proxy passes traffic straight through.</summary>
    public Task ClearLatencyAsync() => ClearToxicAsync(ToxicName);

    private async Task ClearToxicAsync(string toxic)
    {
        var response = await _http.DeleteAsync($"/proxies/{ProxyName}/toxics/{toxic}");

        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound or HttpStatusCode.OK))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Pulls the connection between the API and Redis, which is the difference that matters most:
    /// the allowlist logs everyone out, the denylist honours every revoked token until it expires.
    /// </summary>
    public async Task SetProxyEnabledAsync(bool enabled)
    {
        var response = await _http.PostAsJsonAsync($"/proxies/{ProxyName}", new { enabled });

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Accepts the connection and then never answers, which is worse than a refusal.</summary>
    public async Task SetStallAsync(int timeoutMs)
    {
        await ClearToxicAsync(StallToxicName);

        if (timeoutMs <= 0)
        {
            return;
        }

        var response = await _http.PostAsJsonAsync($"/proxies/{ProxyName}/toxics", new
        {
            name = StallToxicName,
            type = "timeout",
            stream = "upstream",
            toxicity = 1.0,
            attributes = new { timeout = timeoutMs },
        });

        response.EnsureSuccessStatusCode();
    }

    public async Task ResetAsync()
    {
        await ClearToxicAsync(ToxicName);
        await ClearToxicAsync(StallToxicName);
        await SetProxyEnabledAsync(true);
    }

    public async Task SetLatencyAsync(int latencyMs, int jitterMs)
    {
        // Toxics are additive, so the old one has to go before the new one lands.
        await ClearLatencyAsync();

        if (latencyMs <= 0 && jitterMs <= 0)
        {
            return;
        }

        // "downstream" delays the bytes coming back from Redis, which is where a client waits.
        var response = await _http.PostAsJsonAsync($"/proxies/{ProxyName}/toxics", new
        {
            name = ToxicName,
            type = "latency",
            stream = "downstream",
            toxicity = 1.0,
            attributes = new { latency = latencyMs, jitter = jitterMs },
        });

        response.EnsureSuccessStatusCode();
    }
}
