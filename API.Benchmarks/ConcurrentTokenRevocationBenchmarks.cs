using BenchmarkDotNet.Attributes;

namespace API.Benchmarks;

// A hundred tokens in flight at once. StackExchange.Redis multiplexes them onto one
// connection, so this measures how well the strategy pipelines, not a hundred clients
// contending for a server.
[MemoryDiagnoser]
[ThreadingDiagnoser]
[InvocationCount(Invocations, 1)]
[IterationCount(20)]
public class ConcurrentTokenRevocationBenchmarks : TokenRevocationBenchmarkBase
{
    private const int Concurrency = 100;

    // Each invocation is already 100 operations, so this needs far fewer of them than the
    // one-at-a-time benchmarks to fill an iteration.
    private const int Invocations = 64;

    private string[][] _loggingIn = null!;

    private string[] _stillLoggedIn = null!;

    private string[][] _loggingOut = null!;

    private int _index;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        await ConnectAsync();

        _stillLoggedIn = NewTokenIds(Concurrency);

        await Task.WhenAll(_stillLoggedIn.Select(IssueAsync));
    }

    [GlobalCleanup]
    public void Cleanup() => Disconnect();

    [IterationSetup(Target = nameof(LoginConcurrently))]
    public void PrepareLogin()
    {
        _loggingIn = NewBatches();
        _index = 0;
    }

    [IterationSetup(Target = nameof(LogoutConcurrently))]
    public void PrepareLogout()
    {
        _loggingOut = NewBatches();
        _index = 0;

        Task.WhenAll(_loggingOut.SelectMany(batch => batch).Select(IssueAsync)).GetAwaiter().GetResult();
    }

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public Task LoginConcurrently() => Task.WhenAll(_loggingIn[_index++].Select(IssueAsync));

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public Task CheckOnEveryRequestConcurrently() =>
        Task.WhenAll(_stillLoggedIn.Select(tokenId => Store.IsRevokedAsync(tokenId)));

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public Task LogoutConcurrently() => Task.WhenAll(_loggingOut[_index++].Select(RevokeAsync));

    // One fresh batch per invocation, so no invocation revokes an id another one already
    // revoked.
    private static string[][] NewBatches() =>
        [.. Enumerable.Range(0, Invocations).Select(_ => NewTokenIds(Concurrency))];
}
