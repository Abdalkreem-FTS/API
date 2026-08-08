using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace API.Tests;

public class WeatherForecastEndpointTests(WebApplicationFactory<Program> factory) : ControllerTestsBase(factory)
{
    [Fact]
    public async Task Get_WithoutAToken_Returns401()
    {
        var response = await Client.GetAsync("/api/weatherforecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithAToken_Returns200()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.GetAsync("/api/weatherforecast");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"Hello {Username}, you are authorized.", body);
    }

    [Fact]
    public async Task Get_WithATamperedSignature_Returns401()
    {
        var token = await LoginAsync();
        var parts = token.AccessToken.Split('.');
        var tampered = $"{parts[0]}.{parts[1]}.{parts[2]}tampered";

        var response = await ClientWithToken(tampered).GetAsync("/api/weatherforecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("aaa.bbb.ccc")]
    public async Task Get_WithRubbishInTheHeader_Returns401(string token)
    {
        var response = await ClientWithToken(token).GetAsync("/api/weatherforecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_WithAnExpiredToken_Returns401AndSaysSo()
    {
        var response = await ClientWithToken(ExpiredToken()).GetAsync("/api/weatherforecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.True(response.Headers.Contains("x-token-expired"));
    }

    [Fact]
    public async Task Get_WithoutAToken_ReturnsAProblemDetailsBodyRatherThanAnEmptyOne()
    {
        var response = await Client.GetAsync("/api/weatherforecast");

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.Status);
        Assert.Equal("Unauthorized", problem.Title);
    }

    private string ExpiredToken()
    {
        var host = HostJwtOptions;
        var issuedAt = DateTime.UtcNow.AddMinutes(-10);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = host.Issuer,
            Audience = host.Audience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Name, Username)]),
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = DateTime.UtcNow.AddMinutes(-5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(host.SecurityKey)),
                SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
