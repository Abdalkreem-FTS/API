using API.Authentication;

namespace API.Diagnostics;

/// <summary>
/// Wiring for the per-stage timings. Everything here is off unless <c>Diagnostics:Enabled</c> is
/// true, so the normal request path keeps no stopwatches and allocates no accessors.
/// </summary>
public static class DiagnosticsExtensions
{
    public const string EnabledKey = "Diagnostics:Enabled";

    public static bool DiagnosticsEnabled(this IConfiguration configuration) =>
        configuration.GetValue(EnabledKey, false);

    public static IServiceCollection AddRequestTimings(this IServiceCollection services, Type storeType)
    {
        services.AddSingleton<RequestTimings>();

        // Needed so the store decorator can attribute its time to the request that caused it.
        services.AddHttpContextAccessor();

        services.AddSingleton(storeType);

        services.AddSingleton<ITokenRevocationStore>(provider => new TimingTokenRevocationStore(
            (ITokenRevocationStore)provider.GetRequiredService(storeType),
            provider.GetRequiredService<RequestTimings>(),
            provider.GetRequiredService<IHttpContextAccessor>()));

        return services;
    }

    /// <summary>
    /// Has to run before authentication, so the total it measures includes the work the bearer
    /// handler does.
    /// </summary>
    public static WebApplication UseRequestTimings(this WebApplication app)
    {
        app.UseMiddleware<RequestTimingsMiddleware>();

        return app;
    }

    public static WebApplication MapDiagnostics(this WebApplication app)
    {
        var diagnostics = app.MapGroup("/diagnostics");

        // Lets a load test confirm it is measuring the strategy it thinks it is. Without this, a
        // leftover process from an earlier run answering on the same port is indistinguishable from
        // the one the test just started.
        diagnostics.MapGet("/info", (IConfiguration configuration) => Results.Ok(new
        {
            Strategy = configuration.GetValue("TokenRevocation:Strategy", TokenRevocationStrategy.Denylist).ToString(),
            ProcessId = Environment.ProcessId,
            TokenLifetimeMinutes = configuration.GetValue("Jwt:ExpiryMinutes", 15),
        }));

        diagnostics.MapGet("/timings", (RequestTimings timings) => Results.Ok(timings.Snapshot()));

        // Called between the warm-up and the measured phase of a load test, so warm-up requests do
        // not end up in the percentiles.
        diagnostics.MapPost("/timings/reset", (RequestTimings timings) =>
        {
            timings.Reset();

            return Results.NoContent();
        });

        return app;
    }
}
