using System.Security.Claims;
using API;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? throw new InvalidOperationException("The 'Jwt' section is missing from configuration.");

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<JwtTokenGenerator>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => options.TokenValidationParameters = JwtTokenGenerator.CreateValidationParameters(jwtOptions));

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();


// Public endpoints

app.MapGet("/", () => new
{
    message = "Minimal ASP.NET Web API with JWT authentication.",
    howToStart = "POST /auth/login with {\"username\":\"alice\",\"password\":\"Password123!\"}, "
                 + "then call /weatherforecast with 'Authorization: Bearer <accessToken>'.",
});

app.MapPost("/auth/login", (LoginRequest request, JwtTokenGenerator tokens) =>
{
    var user = Users.Find(request.Username, request.Password);
    
    return user is null ? Results.Unauthorized() : Results.Ok(tokens.GenerateToken(user.Username));
});

app.MapPost("/auth/validate", (ValidateRequest request, JwtTokenGenerator tokens) => tokens.ValidateToken(request.Token));


// Protected endpoint

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching",
};

app.MapGet("/weatherforecast", (ClaimsPrincipal user) => new
{
    message = $"Hello {user.FindFirstValue("name")}, you are authorized.",
    forecast = Enumerable.Range(1, 5).Select(day => new WeatherForecast
    (
        Date: DateOnly.FromDateTime(DateTime.Now.AddDays(day)),
        TemperatureC: Random.Shared.Next(-20, 55),
        Summary: summaries[Random.Shared.Next(summaries.Length)]
    ))
}).RequireAuthorization();

app.Run();
