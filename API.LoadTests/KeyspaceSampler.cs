namespace API.LoadTests;

/// <summary>
/// Watches the keyspace while the load runs. A preloaded keyspace is a snapshot someone stamped
/// into place; this is what shows it settling, so a run that outlasts the token lifetime can be told
/// apart from one that never got there.
/// </summary>
public sealed class KeyspaceSampler(RedisAdmin redis, TimeSpan interval)
{
    private readonly List<KeyspaceSample> _samples = [];

    public IReadOnlyList<KeyspaceSample> Samples => _samples;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await redis.SnapshotAsync();

                _samples.Add(new KeyspaceSample(DateTimeOffset.UtcNow - started, snapshot));

                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A sampler that takes the run down with it would be worse than a gap in the series.
                await Task.Delay(interval, CancellationToken.None);
            }
        }
    }
}

public readonly record struct KeyspaceSample(TimeSpan At, KeyspaceSnapshot Snapshot);
