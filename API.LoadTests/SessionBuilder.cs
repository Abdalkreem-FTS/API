using API.Authentication;
using API.Models;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;

namespace API.LoadTests;

/// <summary>
/// Builds the sessions a run needs: a large population that only has to exist in Redis, and a
/// smaller set of tokens the load actually sends.
/// <para>
/// Tokens are minted in this process with the same key the API validates against, rather than
/// fetched over HTTP. Pre-issuing a hundred thousand of them through <c>POST /api/tokens</c> would
/// take longer than the test it is setting up.
/// </para>
/// </summary>
public sealed class SessionBuilder(LoadTestOptions options) : IDisposable
{
    // Concurrent writes per batch. High enough to keep the pipeline full, low enough that a hundred
    // thousand of them are not all in flight at once.
    private const int BatchSize = 2_000;

    private static readonly User Alice = new(
        Guid.Parse("8f14e45f-ceea-467a-9575-2c1a1a4a2d1b"),
        "alice",
        "Password123!",
        ["admin"]);

    private readonly RedisCache _cache = new(Options.Create(new RedisCacheOptions
    {
        Configuration = options.Redis,

        // Has to match what the API uses, or these keys land somewhere the API never looks.
        InstanceName = "api:",
    }));

    private readonly JwtTokenGenerator _tokens = new(new StaticOptionsMonitor<JwtOptions>(options.Jwt));

    /// <summary>
    /// The real implementations, so the keyspace ends up identical to a system that had these
    /// sessions issued and revoked through the API. It is also why the denylist needs no issue pass:
    /// its <c>IssueAsync</c> does not touch Redis at all.
    /// </summary>
    private ITokenRevocationStore Store => options.Strategy is TokenRevocationStrategy.Allowlist
        ? new AllowlistTokenRevocationStore(_cache)
        : new DenylistTokenRevocationStore(_cache);

    /// <summary>
    /// Sessions that only need to exist, to put the keyspace in the state a running system would
    /// already be in. Measuring against an empty Redis is what hides the cost of the allowlist.
    /// </summary>
    public async Task<(int Issued, int Revoked)> FabricateAsync(int sessions)
    {
        var store = Store;

        var loggedOutCount = (int)(sessions * options.LogoutShare);

        using var progress = new ConsoleProgress("preloading sessions ", sessions + loggedOutCount);

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(options.TokenLifetimeMinutes);

        var tokenIds = new string[sessions];

        for (var i = 0; i < tokenIds.Length; i++)
        {
            tokenIds[i] = Guid.NewGuid().ToString();
        }

        await ForEachBatchAsync(tokenIds, id => store.IssueAsync(id, expiresAt), progress);

        // Sessions that ended while their token still had time left. Under the allowlist this takes
        // keys away; under the denylist it is the only thing that puts keys there.
        await ForEachBatchAsync(tokenIds[..loggedOutCount], id => store.RevokeAsync(id, expiresAt), progress);

        return (sessions, loggedOutCount);
    }

    /// <summary>Sessions the load will actually use, so these need a signed token as well as a key.</summary>
    public async Task<string[]> MintAsync(int count, bool showProgress = false)
    {
        if (count == 0)
        {
            return [];
        }

        var store = Store;

        using var progress = showProgress ? new ConsoleProgress("minting tokens     ", count) : null;

        var minted = new (string TokenId, string AccessToken, DateTime ExpiresAt)[count];

        for (var i = 0; i < count; i++)
        {
            var (tokenId, response) = _tokens.GenerateToken(Alice);

            minted[i] = (tokenId, response.AccessToken, response.ExpiresAt);
        }

        await ForEachBatchAsync(
            minted,
            token => store.IssueAsync(token.TokenId, token.ExpiresAt),
            progress);

        return [.. minted.Select(token => token.AccessToken)];
    }

    private static async Task ForEachBatchAsync<T>(T[] items, Func<T, Task> operation, IProgress<int>? progress)
    {
        for (var offset = 0; offset < items.Length; offset += BatchSize)
        {
            var batch = items[offset..Math.Min(offset + BatchSize, items.Length)];

            await Task.WhenAll(batch.Select(operation));

            progress?.Report(batch.Length);
        }
    }

    public void Dispose() => _cache.Dispose();
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
