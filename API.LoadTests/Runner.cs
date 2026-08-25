using API.Diagnostics;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace API.LoadTests;

/// <summary>
/// Everything a single measured run needs: reset Redis, build the sessions, drive the load, and
/// collect the client-side stats, the server-side breakdown and the keyspace either side.
/// </summary>
public sealed class Runner(LoadTestOptions options, RedisAdmin redis, string apiUrl)
{
    private readonly TimingsClient _timings = new(apiUrl);

    private TokenPool? _pool;

    public async Task<RunResult> RunAsync(
        string label,
        int? rateOverride = null,
        TimeSpan? durationOverride = null,
        bool rebuildSessions = true,
        bool writeReports = true,
        bool warmUp = true)
    {
        var pool = rebuildSessions || _pool is null ? await BuildSessionsAsync(rateOverride) : _pool;

        // Warm-up is a discarded session of its own. NBomber's built-in warm-up phase would land in
        // the server-side histograms, and those can only be reset from outside between sessions.
        if (warmUp && options.WarmUp > TimeSpan.Zero)
        {
            NBomberRunner
                .RegisterScenarios(Scenarios.Create(apiUrl, options, pool, rateOverride, options.WarmUp))
                .WithTestSuite("token-revocation")
                .WithTestName($"{label}-warmup")
                .WithoutReports()
                .Run();
        }

        await ResetTimingsAsync(pool);

        var before = await redis.SnapshotAsync();

        var sampler = new KeyspaceSampler(redis, options.SampleInterval);

        using var sampling = new CancellationTokenSource();

        var series = sampler.RunAsync(sampling.Token);

        // Tokens are minted once; a run longer than the lifetime would spend most of itself being
        // rejected for expiry. Refreshed at half the lifetime so nothing in use is ever close to it.
        var lifetime = TimeSpan.FromMinutes(options.TokenLifetimeMinutes);

        // Enough logout tokens for one refresh window, with margin. Sizing this off the whole run
        // would mint a fresh queue of thousands every window and throw almost all of it away.
        var logoutsPerWindow = (int)(options.EffectiveLogoutRate * (lifetime / 2).TotalSeconds * 1.5) + 16;

        var refresh = (durationOverride ?? options.Duration) > lifetime / 2
            ? pool.RefreshAsync(MintAsync, lifetime / 2, logoutsPerWindow, sampling.Token)
            : Task.CompletedTask;

        var runner = NBomberRunner
            .RegisterScenarios(Scenarios.Create(apiUrl, options, pool, rateOverride, durationOverride))
            .WithTestSuite("token-revocation")
            .WithTestName(label);

        // A ramp writes a report per step, which is a lot of folders for something read as one table.
        runner = writeReports
            ? runner
                .WithReportFolder(Path.Combine("LoadTestReports", label))
                .WithReportFormats(ReportFormat.Txt, ReportFormat.Md, ReportFormat.Csv)
            : runner.WithoutReports();

        var stats = runner.Run();

        await sampling.CancelAsync();

        await series;
        await refresh;

        var after = await redis.SnapshotAsync();

        return new RunResult(
            label,
            [.. stats.ScenarioStats.Select(scenario => Summarise(scenario, options, rateOverride))],
            await ReadTimingsAsync(),
            before,
            after,
            await redis.AverageKeyBytesAsync(),
            sampler.Samples);
    }

    private async Task<TokenPool> BuildSessionsAsync(int? rateOverride)
    {
        // A leftover keyspace from an earlier run would be counted as this run's memory.
        await redis.FlushAsync();

        using var sessions = new SessionBuilder(options);

        var logoutPool = LogoutPoolSize(rateOverride);

        var usable = options.WorkingSet + logoutPool;

        // The usable tokens are real sessions too, so they come off the fabricated count and the
        // total lands on what was asked for.
        var (issued, revoked) = await sessions.FabricateAsync(Math.Max(0, options.Sessions - usable));

        Console.WriteLine($"  sessions: fabricated {issued:N0} ({revoked:N0} already logged out), minting {usable:N0} usable");

        var minted = await sessions.MintAsync(usable, showProgress: true);

        return _pool = new TokenPool(minted[..options.WorkingSet], minted[options.WorkingSet..]);
    }

    /// <summary>Fresh tokens for the refresher, minted the same way the initial pool was.</summary>
    private async Task<string[]> MintAsync(int count)
    {
        using var sessions = new SessionBuilder(options);

        return await sessions.MintAsync(count);
    }

    private int LogoutPoolSize(int? rateOverride) =>
        rateOverride is { } rate && options.Mode is LoadMode.Logout
            ? (int)(rate * (options.Duration.TotalSeconds + options.WarmUp.TotalSeconds) * 1.5) + 16
            : options.LogoutPoolSize;

    private async Task ResetTimingsAsync(TokenPool pool)
    {
        using var http = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(30) };

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/weatherforecast");

        request.Headers.Add("Authorization", $"Bearer {pool.WorkingSet[0]}");

        try
        {
            // Warms the connection pool and the JIT on the request path. Not asserted on, because
            // the failure cases deliberately break the thing this request depends on.
            await http.SendAsync(request);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Nothing to warm if the API cannot serve the request; the run reports why.
        }

        await _timings.ResetAsync();
    }

    private async Task<IReadOnlyList<StageTimings>> ReadTimingsAsync()
    {
        try
        {
            return await _timings.ReadAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"  could not read /diagnostics/timings: {exception.Message}");

            return [];
        }
    }

    private static ScenarioResult Summarise(ScenarioStats scenario, LoadTestOptions options, int? rateOverride)
    {
        var ok = scenario.Ok;

        var statusCodes = ok.StatusCodes
            .Concat(scenario.Fail.StatusCodes)
            .GroupBy(code => code.StatusCode)
            .ToDictionary(group => group.Key, group => group.Sum(code => code.Count));

        return new ScenarioResult(
            scenario.ScenarioName,
            Scenarios.RequestedRate(options, scenario.ScenarioName, rateOverride),
            ok.Request.RPS,
            ok.Latency.Percent50,
            ok.Latency.Percent95,
            ok.Latency.Percent99,
            ok.Request.Count,
            scenario.Fail.Request.Count,
            statusCodes);
    }
}
