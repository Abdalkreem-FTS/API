using StackExchange.Redis;

namespace API.LoadTests;

/// <summary>
/// A direct, unproxied connection used to reset the keyspace and to read the numbers that actually
/// separate the two strategies: how many keys are live and what they cost in memory.
/// </summary>
public sealed class RedisAdmin : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _connection;

    private RedisAdmin(ConnectionMultiplexer connection) => _connection = connection;

    public static async Task<RedisAdmin> ConnectAsync(string configuration)
    {
        var options = ConfigurationOptions.Parse(configuration);

        // FLUSHDB, INFO and CONFIG SET are admin commands, which the client refuses by default.
        options.AllowAdmin = true;

        return new RedisAdmin(await ConnectionMultiplexer.ConnectAsync(options));
    }

    private IServer Server => _connection.GetServer(_connection.GetEndPoints()[0]);

    public async Task FlushAsync()
    {
        foreach (var endpoint in _connection.GetEndPoints())
        {
            await _connection.GetServer(endpoint).FlushDatabaseAsync();
        }
    }

    public async Task<KeyspaceSnapshot> SnapshotAsync()
    {
        var memory = (await Server.InfoAsync("memory"))
            .SelectMany(group => group)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        return new KeyspaceSnapshot(
            await Server.DatabaseSizeAsync(),
            Number(memory, "used_memory"),
            Fraction(memory, "mem_fragmentation_ratio"));
    }

    /// <summary>
    /// Redis's own accounting for a sample of keys, which is a far better per-session figure than
    /// subtracting a hand-guessed baseline from <c>used_memory</c>.
    /// </summary>
    public async Task<double> AverageKeyBytesAsync(int sample = 200)
    {
        var keys = Server.Keys(pageSize: sample).Take(sample).ToArray();

        if (keys.Length == 0)
        {
            return 0;
        }

        var database = _connection.GetDatabase();

        var sizes = await Task.WhenAll(keys.Select(async key =>
            (long?)await database.ExecuteAsync("MEMORY", "USAGE", key.ToString()) ?? 0));

        return sizes.Average();
    }

    /// <summary>
    /// Caps memory at what is already in use, so the next write is the one that fails. Pass 0 to
    /// lift the cap again.
    /// </summary>
    public async Task SetMaxMemoryAsync(long bytes) =>
        await Server.ConfigSetAsync("maxmemory", bytes.ToString());

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static long Number(Dictionary<string, string> memory, string key) =>
        memory.TryGetValue(key, out var value) && long.TryParse(value, out var parsed) ? parsed : 0;

    private static double Fraction(Dictionary<string, string> memory, string key) =>
        memory.TryGetValue(key, out var value) && double.TryParse(value, out var parsed) ? parsed : 0;
}

public readonly record struct KeyspaceSnapshot(long Keys, long UsedMemoryBytes, double FragmentationRatio)
{
    public string UsedMemory => Format(UsedMemoryBytes);

    public static string Format(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (double)(1024 * 1024 * 1024):F2} GB",
        >= 1024 * 1024 => $"{bytes / (double)(1024 * 1024):F2} MB",
        >= 1024 => $"{bytes / 1024.0:F2} KB",
        _ => $"{bytes} B",
    };
}
