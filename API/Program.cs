using System.Security.Claims;
using API.Authentication;
using API.Contracts;
using API.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<JwtTokenGenerator>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptionsMonitor<JwtOptions>>((bearer, jwt) =>
    {
        bearer.TokenValidationParameters = JwtTokenGenerator.CreateValidationParameters(jwt.CurrentValue);
        bearer.Events = JwtBearerEventHandlers.Create();
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var api = app.MapGroup("/api");

api.MapPost("/tokens", (LoginRequest request, JwtTokenGenerator tokens) =>
{
    var user = Users.Find(request.Username, request.Password);

    return user is null
        ? Results.Unauthorized()
        : Results.Ok(tokens.GenerateToken(user));
});

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
