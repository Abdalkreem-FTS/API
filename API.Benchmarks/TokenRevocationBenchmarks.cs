using BenchmarkDotNet.Attributes;

namespace API.Benchmarks;

// One token at a time, which is what a single request costs.
[MemoryDiagnoser]
[ThreadingDiagnoser]
// [IterationSetup] has to know how many tokens the coming iteration will consume, so the
// invocation count is pinned instead of being discovered by a pilot run. The unroll factor
// of 1 keeps one invocation per loop step, so the count is exact.
[InvocationCount(Invocations, 1)]
[IterationCount(20)]
public class TokenRevocationBenchmarks : TokenRevocationBenchmarkBase
{
    // Sized so an iteration clears BenchmarkDotNet's 100ms recommendation. The Login row
    // for the denylist cannot get there at any count, because it never leaves the process.
    private const int Invocations = 1024;

    private string[] _loggingIn = null!;

    private string[] _stillLoggedIn = null!;

    private string[] _loggingOut = null!;

    private int _index;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        await ConnectAsync();

        // Reading is not destructive, so this pool is issued once and reused for the
        // whole run.
        _stillLoggedIn = NewTokenIds(Invocations);

        await Task.WhenAll(_stillLoggedIn.Select(IssueAsync));
    }

    [GlobalCleanup]
    public void Cleanup() => Disconnect();

    [IterationSetup(Target = nameof(Login))]
    public void PrepareLogin()
    {
        _loggingIn = NewTokenIds(Invocations);
        _index = 0;
    }

    [IterationSetup(Target = nameof(CheckOnEveryRequest))]
    public void PrepareCheck() => _index = 0;

    [IterationSetup(Target = nameof(Logout))]
    public void PrepareLogout()
    {
        _loggingOut = NewTokenIds(Invocations);
        _index = 0;

        // Each id has to be live before it is revoked. This is the step whose absence
        // let the allowlist revoke keys that were already gone.
        Task.WhenAll(_loggingOut.Select(IssueAsync)).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task Login() => IssueAsync(_loggingIn[_index++]);

    [Benchmark]
    public Task<bool> CheckOnEveryRequest() => Store.IsRevokedAsync(_stillLoggedIn[_index++]);

    [Benchmark]
    public Task Logout() => RevokeAsync(_loggingOut[_index++]);
}
