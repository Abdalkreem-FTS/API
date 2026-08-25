using API.Authentication;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;

namespace API.Benchmarks;

public abstract class TokenRevocationBenchmarkBase
{
    private const string Connection = "localhost:6379";

    [Params(TokenRevocationStrategy.Denylist, TokenRevocationStrategy.Allowlist)]
    public TokenRevocationStrategy Strategy { get; set; }

    private RedisCache _cache = null!;

    protected ITokenRevocationStore Store { get; private set; } = null!;

    private DateTimeOffset _expiresAt;

    protected async Task ConnectAsync()
    {
        _cache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            Configuration = Connection,
            InstanceName = "benchmark:",
        }));

        Store = Strategy is TokenRevocationStrategy.Allowlist
            ? new AllowlistTokenRevocationStore(_cache)
            : new DenylistTokenRevocationStore(_cache);

        _expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        // The first call pays for the connection handshake and for jitting the whole
        // Redis path, neither of which belongs in a sample.
        await _cache.GetAsync("warm-up");
    }

    protected void Disconnect() => _cache.Dispose();

    protected Task IssueAsync(string tokenId) => Store.IssueAsync(tokenId, _expiresAt);

    protected Task RevokeAsync(string tokenId) => Store.RevokeAsync(tokenId, _expiresAt);

    // Every measured operation needs a token id of its own. Reusing one id makes an
    // allowlist logout a DEL against a key that the previous invocation already
    // removed, which is the cheapest thing Redis can do and never happens in
    // production, while the denylist keeps paying for a real SET every time.
    protected static string[] NewTokenIds(int count) =>
        [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString())];
}
