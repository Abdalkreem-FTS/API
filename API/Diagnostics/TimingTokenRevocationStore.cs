using System.Diagnostics;
using API.Authentication;

namespace API.Diagnostics;

/// <summary>
/// Times the store without the store knowing about it, and adds each call to the current request's
/// running total so the middleware can work out what the rest of the pipeline cost.
/// </summary>
public sealed class TimingTokenRevocationStore(
    ITokenRevocationStore inner,
    RequestTimings timings,
    IHttpContextAccessor accessor) : ITokenRevocationStore
{
    public Task IssueAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        MeasureAsync(RequestTimings.Issue, () => inner.IssueAsync(tokenId, expiresAt, cancellationToken));

    public Task RevokeAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        MeasureAsync(RequestTimings.Revoke, () => inner.RevokeAsync(tokenId, expiresAt, cancellationToken));

    public async Task<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            return await inner.IsRevokedAsync(tokenId, cancellationToken);
        }
        finally
        {
            Record(RequestTimings.RevocationCheck, started);
        }
    }

    private async Task MeasureAsync(string stage, Func<Task> operation)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await operation();
        }
        finally
        {
            Record(stage, started);
        }
    }

    private void Record(string stage, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);

        timings.Record(stage, elapsed);

        if (accessor.HttpContext is { } context)
        {
            var spent = context.Items[RequestTimingsMiddleware.StoreKey] as TimeSpan? ?? TimeSpan.Zero;

            context.Items[RequestTimingsMiddleware.StoreKey] = spent + elapsed;
        }
    }
}
