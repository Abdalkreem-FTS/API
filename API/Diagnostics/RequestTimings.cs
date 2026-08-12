namespace API.Diagnostics;

/// <summary>
/// Splits a request into the stages that matter for the revocation question, so a single opaque
/// "2.5ms per request" can be read as how much of it the Redis check actually costs.
/// <para>
/// Raw samples are kept rather than bucketed, because the whole point is exact tail percentiles and
/// a coarse histogram would blur the tail that is being measured. Off unless
/// <c>Diagnostics:Enabled</c> says otherwise.
/// </para>
/// </summary>
public sealed class RequestTimings
{
    /// <summary>The whole request, from the first middleware to the last.</summary>
    public const string Total = "request.total";

    /// <summary>Reading the bearer header and validating the signature, before revocation is considered.</summary>
    public const string Validate = "auth.validate";

    /// <summary>The revocation lookup on the authenticated path. One Redis round trip either strategy.</summary>
    public const string RevocationCheck = "store.check";

    /// <summary>Recording a new token. A Redis round trip for the allowlist, nothing for the denylist.</summary>
    public const string Issue = "store.issue";

    /// <summary>Revoking a token. A SET for the denylist, a DEL for the allowlist.</summary>
    public const string Revoke = "store.revoke";

    /// <summary>
    /// What is left of the request once validation and the store are taken out: Kestrel, routing,
    /// authorization, serialisation. Measured per request rather than subtracted from percentiles,
    /// because percentiles of different stages do not belong to the same request.
    /// </summary>
    public const string Other = "request.other";

    private const int Capacity = 500_000;

    private static readonly string[] AllStages = [Total, Validate, RevocationCheck, Issue, Revoke, Other];

    // Populated once and never mutated, so concurrent reads need no synchronisation of their own.
    private readonly Dictionary<string, Samples> _stages =
        AllStages.ToDictionary(stage => stage, _ => new Samples());

    public void Record(string stage, TimeSpan elapsed) => _stages[stage].Add(elapsed.Ticks);

    public IReadOnlyList<StageTimings> Snapshot() =>
    [
        .. AllStages
            .Select(stage => _stages[stage].Summarise(stage))
            .Where(summary => summary.Count > 0),
    ];

    public void Reset()
    {
        foreach (var samples in _stages.Values)
        {
            samples.Clear();
        }
    }

    private sealed class Samples
    {
        private readonly long[] _ticks = new long[Capacity];

        private int _written;

        private int _observed;

        public void Add(long ticks)
        {
            Interlocked.Increment(ref _observed);

            var index = Interlocked.Increment(ref _written) - 1;

            if (index < _ticks.Length)
            {
                _ticks[index] = ticks;
            }
        }

        public StageTimings Summarise(string stage)
        {
            var count = Math.Min(Volatile.Read(ref _written), _ticks.Length);

            if (count == 0)
            {
                return new StageTimings(stage, 0, 0, 0, 0, 0, 0, 0);
            }

            var sorted = _ticks[..count];

            Array.Sort(sorted);

            return new StageTimings(
                stage,
                count,
                Volatile.Read(ref _observed),
                Milliseconds(sorted.Average()),
                Milliseconds(Percentile(sorted, 0.50)),
                Milliseconds(Percentile(sorted, 0.95)),
                Milliseconds(Percentile(sorted, 0.99)),
                Milliseconds(sorted[^1]));
        }

        public void Clear()
        {
            Volatile.Write(ref _written, 0);
            Volatile.Write(ref _observed, 0);
        }

        private static double Percentile(long[] sorted, double percentile) =>
            sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

        private static double Milliseconds(double ticks) => ticks / TimeSpan.TicksPerMillisecond;
    }
}

public sealed record StageTimings(
    string Stage,
    int Count,
    int Observed,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs);
