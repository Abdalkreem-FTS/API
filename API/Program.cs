using System.Security.Claims;
using API.Authentication;
using API.Contracts;
using API.Diagnostics;
using API.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<JwtTokenGenerator>();

builder.Services.AddStackExchangeRedisCache(redis =>
{
    redis.Configuration = builder.Configuration.GetConnectionString("Redis")
                          ?? throw new InvalidOperationException("The 'Redis' connection string is required for token revocation.");

    redis.InstanceName = "api:";
});

var revocation = builder.Configuration.GetValue("TokenRevocation:Strategy", TokenRevocationStrategy.Denylist);

var storeType = revocation switch
{
    TokenRevocationStrategy.Allowlist => typeof(AllowlistTokenRevocationStore),
    _ => typeof(DenylistTokenRevocationStore),
};

// Load tests turn this on to get a per-stage breakdown of where a request's time goes. Left off,
// nothing below is registered and the request path is unchanged.
var diagnostics = builder.Configuration.DiagnosticsEnabled();

if (diagnostics)
{
    builder.Services.AddRequestTimings(storeType);
}
else
{
    builder.Services.AddSingleton(typeof(ITokenRevocationStore), storeType);
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptionsMonitor<JwtOptions>, ILoggerFactory, IServiceProvider>((bearer, jwt, loggers, provider) =>
    {
        bearer.TokenValidationParameters = JwtTokenGenerator.CreateValidationParameters(jwt.CurrentValue);

        bearer.Events = JwtBearerEventHandlers.Create(
            loggers.CreateLogger(JwtBearerEventHandlers.LoggerCategory),
            diagnostics ? provider.GetRequiredService<RequestTimings>() : null);
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (diagnostics)
{
    // Before authentication, so the measured total covers the bearer handler too.
    app.UseRequestTimings();
}

app.UseAuthentication();
app.UseAuthorization();

if (diagnostics)
{
    app.MapDiagnostics();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var api = app.MapGroup("/api");

api.MapPost("/tokens", async (
    LoginRequest request,
    JwtTokenGenerator tokens,
    ITokenRevocationStore revoked,
    CancellationToken cancellationToken) =>
{
    var user = Users.Find(request.Username, request.Password);

    if (user is null)
    {
        return Results.Unauthorized();
    }

    var (tokenId, response) = tokens.GenerateToken(user);

    await revoked.IssueAsync(tokenId, response.ExpiresAt, cancellationToken);

    return Results.Ok(response);
});

api.MapDelete("/tokens", async (
    ClaimsPrincipal user,
    ITokenRevocationStore revoked,
    CancellationToken cancellationToken) =>
{
    var tokenId = user.FindFirstValue(JwtRegisteredClaimNames.Jti);
    var expiry = user.FindFirstValue(JwtRegisteredClaimNames.Exp);

    if (tokenId is null || !long.TryParse(expiry, out var expiresAtUnixSeconds))
    {
        return Results.Problem("The token is missing the claims needed to revoke it.", statusCode: StatusCodes.Status400BadRequest);
    }

    await revoked.RevokeAsync(tokenId, DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds), cancellationToken);

    return Results.NoContent();

}).RequireAuthorization();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching",
};

api.MapGet("/weatherforecast", (ClaimsPrincipal user) => new
{
    message = $"Hello {user.Identity?.Name}, you are authorized.",
    forecast = Enumerable.Range(1, 5).Select(day => new WeatherForecast
    (
        Date: DateOnly.FromDateTime(DateTime.Now.AddDays(day)),
        TemperatureC: Random.Shared.Next(-20, 55),
        Summary: summaries[Random.Shared.Next(summaries.Length)]
    )),
}).RequireAuthorization();

app.Run();
