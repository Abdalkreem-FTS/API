using System.Net;
using System.Net.Http.Json;
using API.Contracts;
using Microsoft.IdentityModel.JsonWebTokens;

namespace API.Tests;

public class TokenEndpointTests(TestApiFactory factory) : ControllerTestsBase(factory)
{
    [Fact]
    public async Task Post_WithCorrectCredentials_ReturnsAToken()
    {
        var token = await LoginAsync();

        Assert.NotEmpty(token.AccessToken);
        Assert.Equal("Bearer", token.TokenType);
        Assert.True(token.ExpiresAt > DateTime.UtcNow);
    }

    [Theory]
    [InlineData(Username, "wrong-password")]
    [InlineData("nobody", Password)]
    public async Task Post_WithBadCredentials_Returns401(string username, string password)
    {
        var response = await Client.PostAsJsonAsync("/api/tokens", new LoginRequest(username, password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_ReturnsATokenCarryingTheRegisteredClaims()
    {
        var token = await LoginAsync();

        var jwt = new JsonWebToken(token.AccessToken);

        Assert.Equal(Username, jwt.GetClaim(JwtRegisteredClaimNames.Name).Value);
        Assert.NotEmpty(jwt.Subject);
        Assert.NotEmpty(jwt.Id);
        Assert.Contains(jwt.Claims, claim => claim is { Type: "role", Value: "admin" });
    }
}
