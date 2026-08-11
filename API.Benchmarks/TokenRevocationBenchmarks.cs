using API.Authentication;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;

namespace API.Benchmarks;

[MemoryDiagnoser]
[ThreadingDiagnoser]
public class TokenRevocationBenchmarks
{
    private const int Concurrency = 100;

    private const string Connection = "localhost:6379";

    [Params(TokenRevocationStrategy.Denylist, TokenRevocationStrategy.Allowlist)]
    public TokenRevocationStrategy Strategy { get; set; }

    private RedisCache _cache = null!;

    private ITokenRevocationStore _store = null!;

    private DateTimeOffset _expiresAt;

    private string _loggingIn = null!;

    private string _stillLoggedIn = null!;

    private string _loggingOut = null!;

    private string[] _loggingInTogether = null!;

    private string[] _stillLoggedInTogether = null!;

    private string[] _loggingOutTogether = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _cache = new RedisCache(Options.Create(new RedisCacheOptions
        {
            Configuration = Connection,
            InstanceName = "benchmark:",
        }));

        _store = Strategy is TokenRevocationStrategy.Allowlist
            ? new AllowlistTokenRevocationStore(_cache)
            : new DenylistTokenRevocationStore(_cache);

        _expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        _loggingIn = Guid.NewGuid().ToString();
        _stillLoggedIn = Guid.NewGuid().ToString();
        _loggingOut = Guid.NewGuid().ToString();

        _loggingInTogether = NewTokenIds();
        _stillLoggedInTogether = NewTokenIds();
        _loggingOutTogether = NewTokenIds();

        await _cache.GetAsync("warm-up");

        await _store.IssueAsync(_stillLoggedIn, _expiresAt);
        await _store.IssueAsync(_loggingOut, _expiresAt);

        await Task.WhenAll(_stillLoggedInTogether.Select(Issue));
        await Task.WhenAll(_loggingOutTogether.Select(Issue));
    }

    [GlobalCleanup]
    public void Cleanup() => _cache.Dispose();

    [Benchmark]
    public Task Login() => Issue(_loggingIn);

    [Benchmark]
    public Task<bool> CheckOnEveryRequest() => _store.IsRevokedAsync(_stillLoggedIn);

    [Benchmark]
    public Task Logout() => Revoke(_loggingOut);

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public Task LoginConcurrently() => Task.WhenAll(_loggingInTogether.Select(Issue));

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public Task CheckOnEveryRequestConcurrently() =>
        Task.WhenAll(_stillLoggedInTogether.Select(tokenId => _store.IsRevokedAsync(tokenId)));

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public Task LogoutConcurrently() => Task.WhenAll(_loggingOutTogether.Select(Revoke));

    private Task Issue(string tokenId) => _store.IssueAsync(tokenId, _expiresAt);

    private Task Revoke(string tokenId) => _store.RevokeAsync(tokenId, _expiresAt);

    private static string[] NewTokenIds() =>
        [.. Enumerable.Range(0, Concurrency).Select(_ => Guid.NewGuid().ToString())];
}
