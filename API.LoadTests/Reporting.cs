namespace API.LoadTests;

public static class Reporting
{
    public static void Scenarios(RunResult run)
    {
        Console.WriteLine();
        Console.WriteLine($"  {"scenario",-22} {"req/s",8} {"got",8} {"p50",9} {"p95",9} {"p99",9} {"ok",9} {"failed",8}");

        foreach (var scenario in run.Scenarios)
        {
            Console.WriteLine(
                $"  {scenario.Name,-22} {scenario.RequestedRps,8:N0} {scenario.AchievedRps,8:F0} " +
                $"{scenario.P50Ms,7:F2}ms {scenario.P95Ms,7:F2}ms {scenario.P99Ms,7:F2}ms " +
                $"{scenario.Ok,9:N0} {scenario.Failed,8:N0}");
        }

        foreach (var behind in run.Scenarios.Where(scenario => !scenario.RateAchieved && scenario.RequestedRps > 0))
        {
            // Below the requested rate means the generator was the bottleneck, and the latency it
            // reported includes its own queue. Those numbers describe nothing under test.
            Console.WriteLine(
                $"  ! {behind.Name} only reached {behind.AchievedRps:F0} of {behind.RequestedRps:N0} req/s " +
                $"- treat its latency as the generator's, not the API's");
        }
    }

    /// <summary>
    /// Where a request's time actually goes. This is the answer to "is the revocation check
    /// expensive", which no end-to-end percentile can give on its own.
    /// </summary>
    public static void Stages(RunResult run)
    {
        if (run.Timings.Count == 0)
        {
            return;
        }

        var total = run.Timings.FirstOrDefault(stage => stage.Stage == "request.total");

        Console.WriteLine();
        Console.WriteLine($"  {"stage",-16} {"count",9} {"mean",9} {"p50",9} {"p95",9} {"p99",9} {"share",7}");

        foreach (var stage in run.Timings)
        {
            // Only comparable when the stage saw the same requests the total did. A mix run has
            // logins with no revocation check and rejected requests that never reached the store, so
            // dividing their means would produce a share of well over 100% and mean nothing.
            var comparable = total is { MeanMs: > 0 } && stage.Count == total.Count;

            var share = comparable && stage.Stage != "request.total"
                ? $"{stage.MeanMs / total.MeanMs:P1}"
                : "-";

            Console.WriteLine(
                $"  {stage.Stage,-16} {stage.Count,9:N0} {stage.MeanMs,7:F3}ms {stage.P50Ms,7:F3}ms " +
                $"{stage.P95Ms,7:F3}ms {stage.P99Ms,7:F3}ms {share,7}");
        }

        Console.WriteLine(run.Timings.All(stage => stage.Count == total?.Count)
            ? "  share is of mean request.total"
            : "  share suppressed: stages saw different request populations - read shares off a "
              + "single-operation run (--mode request/login/logout), not a mix");
    }

    public static void Keyspace(RunResult run)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  keys   {run.Before.Keys,12:N0} -> {run.After.Keys,-12:N0}  " +
            $"memory {run.Before.UsedMemory,10} -> {run.After.UsedMemory,-10}");

        Console.WriteLine(
            $"  {run.AverageKeyBytes:F0} B/key by Redis's own accounting, " +
            $"fragmentation {run.After.FragmentationRatio:F2}");
    }

    /// <summary>
    /// The keyspace over time. A preloaded keyspace is a snapshot someone stamped into place; this
    /// shows whether the run was long enough for TTL expiry to move it.
    /// </summary>
    public static void Series(RunResult run)
    {
        if (run.Series.Count < 2)
        {
            return;
        }

        var first = run.Series[0].Snapshot.Keys;
        var last = run.Series[^1].Snapshot.Keys;
        var min = run.Series.Min(sample => sample.Snapshot.Keys);
        var max = run.Series.Max(sample => sample.Snapshot.Keys);

        Console.WriteLine();
        Console.WriteLine(
            $"  keyspace over {run.Series[^1].At.TotalSeconds:F0}s: " +
            $"{first:N0} -> {last:N0} (min {min:N0}, max {max:N0}, {last - first:+#,0;-#,0;0} net)");
    }

    /// <summary>
    /// Median across independent runs, with the spread beside it. A difference between the
    /// strategies is only real if it is bigger than this spread, which is the thing a single run
    /// cannot tell you.
    /// </summary>
    public static void Variance(IReadOnlyList<RunResult> runs)
    {
        if (runs.Count < 2)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"===== across {runs.Count} runs =====");
        Console.WriteLine();
        Console.WriteLine($"  {"scenario",-22} {"metric",6} {"median",10} {"min",10} {"max",10} {"spread",8}");

        foreach (var name in runs.SelectMany(run => run.Scenarios).Select(scenario => scenario.Name).Distinct())
        {
            foreach (var (metric, select) in Metrics)
            {
                var values = runs
                    .SelectMany(run => run.Scenarios.Where(scenario => scenario.Name == name))
                    .Select(select)
                    .Order()
                    .ToArray();

                if (values.Length == 0)
                {
                    continue;
                }

                var median = values[values.Length / 2];
                var spread = median > 0 ? (values[^1] - values[0]) / median : 0;

                Console.WriteLine(
                    $"  {name,-22} {metric,6} {median,8:F2}ms {values[0],8:F2}ms {values[^1],8:F2}ms {spread,7:P1}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  spread is (max - min) / median: a gap between strategies smaller than this is noise");
    }

    private static readonly (string Metric, Func<ScenarioResult, double> Select)[] Metrics =
    [
        ("p50", scenario => scenario.P50Ms),
        ("p95", scenario => scenario.P95Ms),
        ("p99", scenario => scenario.P99Ms),
    ];

    public static void Ramp(IReadOnlyList<RunResult> steps)
    {
        Console.WriteLine();
        Console.WriteLine("===== saturation ramp =====");
        Console.WriteLine();
        Console.WriteLine($"  {"req/s",8} {"got",8} {"p50",9} {"p95",9} {"p99",9} {"failed",8}");

        double? previousP99 = null;

        foreach (var scenario in steps.SelectMany(step => step.Scenarios))
        {
            var knee = previousP99 is { } previous && previous > 0 && scenario.P99Ms > previous * 2
                ? "  <- p99 more than doubled"
                : string.Empty;

            Console.WriteLine(
                $"  {scenario.RequestedRps,8:N0} {scenario.AchievedRps,8:F0} " +
                $"{scenario.P50Ms,7:F2}ms {scenario.P95Ms,7:F2}ms {scenario.P99Ms,7:F2}ms " +
                $"{scenario.Failed,8:N0}{knee}");

            previousP99 = scenario.P99Ms;
        }

        var saturated = steps
            .SelectMany(step => step.Scenarios)
            .FirstOrDefault(scenario => !scenario.RateAchieved || scenario.Failed > 0);

        Console.WriteLine();
        Console.WriteLine(saturated is null
            ? "  no saturation point found - the ramp never got there, raise --ramp-to"
            : $"  first step that did not hold: {saturated.RequestedRps:N0} req/s " +
              $"(reached {saturated.AchievedRps:F0}, {saturated.Failed:N0} failed)");
    }

    /// <summary>
    /// Where the time goes at each rate. A single breakdown says the Redis check dominates; this says
    /// whether that stays true as load climbs, or whether something else - queueing in Kestrel, the
    /// thread pool, the one multiplexed Redis connection - takes over as the rate goes up.
    /// </summary>
    public static void RampStages(IReadOnlyList<RunResult> steps)
    {
        Console.WriteLine();
        Console.WriteLine("===== where the time goes, by rate (mean per stage) =====");
        Console.WriteLine();
        Console.WriteLine($"  {"req/s",8} {"total",10} {"validate",10} {"store.check",12} {"other",10} {"check %",8}");

        foreach (var step in steps)
        {
            var rate = step.Scenarios.FirstOrDefault()?.RequestedRps ?? 0;

            var total = Mean(step, "request.total");
            var check = Mean(step, "store.check");

            Console.WriteLine(
                $"  {rate,8:N0} {total,8:F3}ms {Mean(step, "auth.validate"),8:F3}ms " +
                $"{check,10:F3}ms {Mean(step, "request.other"),8:F3}ms " +
                $"{(total > 0 ? check / total : 0),8:P1}");
        }

        Console.WriteLine();
        Console.WriteLine("  a falling check % as the rate climbs means the bottleneck has moved off Redis");
    }

    private static double Mean(RunResult run, string stage) =>
        run.Timings.FirstOrDefault(timing => timing.Stage == stage)?.MeanMs ?? 0;

    public static void FailureCase(string name, RunResult run)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {name} ---");

        foreach (var scenario in run.Scenarios)
        {
            var codes = scenario.StatusCodes.Count == 0
                ? "none"
                : string.Join(", ", scenario.StatusCodes.OrderByDescending(code => code.Value).Select(code => $"{code.Key}={code.Value:N0}"));

            Console.WriteLine($"  {scenario.Name,-22} ok {scenario.Ok,7:N0}  failed {scenario.Failed,7:N0}  [{codes}]");
        }
    }
}
