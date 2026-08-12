using API.Diagnostics;

namespace API.LoadTests;

public sealed record RunResult(
    string Label,
    IReadOnlyList<ScenarioResult> Scenarios,
    IReadOnlyList<StageTimings> Timings,
    KeyspaceSnapshot Before,
    KeyspaceSnapshot After,
    double AverageKeyBytes,
    IReadOnlyList<KeyspaceSample> Series);

public sealed record ScenarioResult(
    string Name,
    int RequestedRps,
    double AchievedRps,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    long Ok,
    long Failed,
    IReadOnlyDictionary<string, int> StatusCodes)
{
    /// <summary>
    /// Whether the generator kept up. If it did not, the latency being reported is partly the
    /// generator's own queue and belongs to nothing under test.
    /// </summary>
    public bool RateAchieved => AchievedRps >= RequestedRps * 0.95;
}
