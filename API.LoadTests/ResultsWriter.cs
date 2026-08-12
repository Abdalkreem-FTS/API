using System.Text.Json;
using System.Text.Json.Serialization;

namespace API.LoadTests;

/// <summary>
/// Writes a run's numbers as JSON next to NBomber's own reports, so a suite script can collect many
/// runs into one report instead of anyone re-reading console output.
/// </summary>
public static class ResultsWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(string path, LoadTestOptions options, IReadOnlyList<RunResult> runs)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var report = new
        {
            mode = options.Mode.ToString(),
            strategy = options.Strategy.ToString(),
            options.Sessions,
            options.LogoutShare,
            options.WorkingSet,
            options.TokenLifetimeMinutes,
            options.RequestsPerSecond,
            options.LoginsPerSecond,
            options.LogoutsPerSecond,
            durationSeconds = options.Duration.TotalSeconds,
            warmUpSeconds = options.WarmUp.TotalSeconds,
            options.RedisLatencyMs,
            options.RedisJitterMs,
            runs = runs.Select(run => new
            {
                run.Label,
                run.Scenarios,
                run.Timings,
                keyspace = new
                {
                    keysBefore = run.Before.Keys,
                    keysAfter = run.After.Keys,
                    usedMemoryBefore = run.Before.UsedMemoryBytes,
                    usedMemoryAfter = run.After.UsedMemoryBytes,
                    run.AverageKeyBytes,
                    fragmentation = run.After.FragmentationRatio,
                    keysMin = run.Series.Count > 0 ? run.Series.Min(sample => sample.Snapshot.Keys) : run.After.Keys,
                    keysMax = run.Series.Count > 0 ? run.Series.Max(sample => sample.Snapshot.Keys) : run.After.Keys,
                },
                series = run.Series.Select(sample => new
                {
                    atSeconds = sample.At.TotalSeconds,
                    sample.Snapshot.Keys,
                    sample.Snapshot.UsedMemoryBytes,
                }),
            }),
        };

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json));

        Console.WriteLine();
        Console.WriteLine($"  results written to {path}");
    }
}
