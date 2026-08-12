using API.LoadTests;

LoadTestOptions options;

try
{
    options = LoadTestOptions.Parse(args);
}
catch (HelpRequestedException)
{
    Console.WriteLine(LoadTestOptions.Usage);

    return 0;
}
catch (Exception exception) when (exception is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"{exception.Message}{Environment.NewLine}{Environment.NewLine}{LoadTestOptions.Usage}");

    return 1;
}

Console.WriteLine($"mode {options.Mode}, strategy {options.Strategy}, {options.Sessions:N0} sessions");

await using var redis = await RedisAdmin.ConnectAsync(options.Redis);

var toxiproxy = new ToxiproxyClient(options.Toxiproxy);
var toxiproxyRunning = await toxiproxy.IsRunningAsync();

var needsToxiproxy = options.RedisLatencyMs > 0 || options.Mode is LoadMode.Failure;

if (needsToxiproxy && !toxiproxyRunning)
{
    Console.Error.WriteLine($"This needs Toxiproxy at {options.Toxiproxy}. Run 'docker compose up -d --wait'.");

    return 1;
}

// With nothing to inject the proxy is not needed, and one less hop is one less thing to explain.
var redisForApi = toxiproxyRunning ? options.RedisProxy : options.Redis;

if (toxiproxyRunning)
{
    await toxiproxy.ResetAsync();
    await toxiproxy.SetLatencyAsync(options.RedisLatencyMs, options.RedisJitterMs);

    Console.WriteLine($"Redis: via Toxiproxy, +{options.RedisLatencyMs}ms latency, {options.RedisJitterMs}ms jitter");
}
else
{
    Console.WriteLine("Redis: direct, no injected latency (Toxiproxy not running)");
}

var (apiUrl, api) = await ApiProcess.StartAsync(options with { RedisProxy = redisForApi });

try
{
    var runner = new Runner(options, redis, apiUrl);

    var results = options.Mode switch
    {
        LoadMode.Ramp => await RampAsync(runner, options),
        LoadMode.Failure => await FailuresAsync(runner, options, redis, toxiproxy),
        _ => await MeasureAsync(runner, options),
    };

    if (options.Output is { } output)
    {
        await ResultsWriter.WriteAsync(output, options, results);
    }
}
finally
{
    if (api is not null)
    {
        await api.DisposeAsync();
    }

    if (toxiproxyRunning)
    {
        await toxiproxy.ResetAsync();
    }

    // Left uncapped, or the next run on this machine starts against a Redis that refuses writes.
    await redis.SetMaxMemoryAsync(0);
}

return 0;

static async Task<IReadOnlyList<RunResult>> MeasureAsync(Runner runner, LoadTestOptions options)
{
    var runs = new List<RunResult>();

    for (var run = 1; run <= options.Repeat; run++)
    {
        var label = $"{options.Strategy}-{options.Mode}-{run}".ToLowerInvariant();

        Console.WriteLine();
        Console.WriteLine($"===== run {run} of {options.Repeat} =====");

        runs.Add(await runner.RunAsync(label));
    }

    foreach (var run in runs)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {run.Label} =====");

        Reporting.Scenarios(run);
        Reporting.Stages(run);
        Reporting.Keyspace(run);
        Reporting.Series(run);
    }

    Reporting.Variance(runs);

    return runs;
}

static async Task<IReadOnlyList<RunResult>> RampAsync(Runner runner, LoadTestOptions options)
{
    var steps = new List<RunResult>();

    var increment = options.RampSteps > 1
        ? (options.RampTo - options.RampFrom) / (double)(options.RampSteps - 1)
        : 0;

    for (var step = 0; step < options.RampSteps; step++)
    {
        var rate = (int)(options.RampFrom + increment * step);

        Console.WriteLine();
        Console.WriteLine($"--- step {step + 1} of {options.RampSteps}: {rate:N0} req/s for {options.RampStep.TotalSeconds:F0}s ---");

        // Sessions are built once: rebuilding between steps would flush the keyspace the ramp is
        // supposed to be pushing against.
        steps.Add(await runner.RunAsync(
            $"{options.Strategy}-ramp-{rate}".ToLowerInvariant(),
            rateOverride: rate,
            durationOverride: options.RampStep,
            rebuildSessions: step == 0,
            writeReports: false));
    }

    Reporting.Ramp(steps);

    // The whole point of a ramp is watching the breakdown move, not just the totals.
    Reporting.RampStages(steps);

    return steps;
}

static async Task<IReadOnlyList<RunResult>> FailuresAsync(
    Runner runner,
    LoadTestOptions options,
    RedisAdmin redis,
    ToxiproxyClient toxiproxy)
{
    var duration = options.RampStep;

    Console.WriteLine();
    Console.WriteLine($"Each case runs {duration.TotalSeconds:F0}s at {options.RequestsPerSecond:N0} req/s.");

    var baseline = await runner.RunAsync("failure-baseline", durationOverride: duration, writeReports: false);


    Reporting.FailureCase("baseline, Redis healthy", baseline);

    // Redis unreachable. The read path is where the two strategies diverge hardest: an allowlist
    // that cannot confirm a token is active treats every token as revoked, so every user is logged
    // out; a denylist that cannot find a revocation entry lets every revoked token through.
    await toxiproxy.SetProxyEnabledAsync(false);

    var unreachable = await runner.RunAsync(
        "failure-unreachable",
        durationOverride: duration,
        rebuildSessions: false,
        writeReports: false,
        warmUp: false);

    Reporting.FailureCase("Redis unreachable", unreachable);

    await toxiproxy.SetProxyEnabledAsync(true);

    // Connections accepted and then never answered, which a client cannot tell from slowness.
    await toxiproxy.SetStallAsync((int)duration.TotalMilliseconds * 2);

    var stalled = await runner.RunAsync(
        "failure-stalled",
        durationOverride: duration,
        rebuildSessions: false,
        writeReports: false,
        warmUp: false);

    Reporting.FailureCase("Redis stalled", stalled);

    await toxiproxy.SetStallAsync(0);

    // Capped at what is already in use, so the next write is the one that fails. Under noeviction
    // that means the allowlist cannot record a login and the denylist cannot record a logout.
    var current = await redis.SnapshotAsync();

    await redis.SetMaxMemoryAsync(current.UsedMemoryBytes);

    var outOfMemory = await runner.RunAsync(
        "failure-oom",
        durationOverride: duration,
        rebuildSessions: false,
        writeReports: false,
        warmUp: false);

    Reporting.FailureCase($"Redis at maxmemory ({current.UsedMemory}, noeviction)", outOfMemory);

    await redis.SetMaxMemoryAsync(0);

    Console.WriteLine();
    Console.WriteLine("How to read this. A 401 would mean the strategy refused the request (fail closed),");
    Console.WriteLine("a 200 that it let the request through (fail open). A 500 means neither was decided:");
    Console.WriteLine("the Redis exception reached the top of the pipeline, so Redis being available is a");
    Console.WriteLine("hard requirement for serving any authenticated request under either strategy.");

    return [baseline, unreachable, stalled, outOfMemory];
}
