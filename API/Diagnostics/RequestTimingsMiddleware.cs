using System.Diagnostics;

namespace API.Diagnostics;

public sealed class RequestTimingsMiddleware(RequestDelegate next, RequestTimings timings)
{
    /// <summary>Set by <see cref="TimingTokenRevocationStore"/>: total time this request spent in Redis.</summary>
    public const string StoreKey = "diagnostics:store-elapsed";

    /// <summary>Set by the bearer events: how long validating the token took.</summary>
    public const string ValidateKey = "diagnostics:validate-elapsed";

    /// <summary>Stamped when the bearer handler first sees the request, so validation can be timed.</summary>
    public const string ValidateStartedKey = "diagnostics:validate-started";

    public async Task InvokeAsync(HttpContext context)
    {
        // The diagnostics endpoints would otherwise report on the requests that read them.
        if (context.Request.Path.StartsWithSegments("/diagnostics"))
        {
            await next(context);

            return;
        }

        var started = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        finally
        {
            var total = Stopwatch.GetElapsedTime(started);

            var validate = context.Items[ValidateKey] as TimeSpan? ?? TimeSpan.Zero;
            var store = context.Items[StoreKey] as TimeSpan? ?? TimeSpan.Zero;

            timings.Record(RequestTimings.Total, total);

            // Clamped because the three are measured by different stopwatches on overlapping
            // scopes, so rounding can push the remainder just below zero on a very fast request.
            var other = total - validate - store;

            timings.Record(RequestTimings.Other, other > TimeSpan.Zero ? other : TimeSpan.Zero);
        }
    }
}
