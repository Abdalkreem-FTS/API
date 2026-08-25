using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using API.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace API.Tests.Integration;

[Collection(RedisCollection.Name)]
[Trait("Category", "Integration")]
public class TokenRevocationIntegrationTests(RedisFixture redis)
{
    [Fact]
    public async Task LoggingOut_StopsTheTokenWorking()
    {
        var (client, _) = await AuthenticatedClientAsync();

        var before = await client.GetAsync("/api/weatherforecast");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var logout = await client.DeleteAsync("/api/tokens");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var after = await client.GetAsync("/api/weatherforecast");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task LoggingOut_MakesTheRejectionSayItWasRevoked()
    {
        var (client, _) = await AuthenticatedClientAsync();

        await client.DeleteAsync("/api/tokens");

        var response = await client.GetAsync("/api/weatherforecast");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal("This token has been revoked.", problem.Detail);
    }

    [Fact]
    public async Task LoggingOut_LeavesTheUsersOtherTokensAlone()
    {
        var first = await LoginAsync();
        var second = await LoginAsync();

        await ClientWithToken(first.AccessToken).DeleteAsync("/api/tokens");

        var response = await ClientWithToken(second.AccessToken).GetAsync("/api/weatherforecast");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LoggingOut_WithoutAToken_Returns401()
    {
        var response = await redis.Factory.CreateClient().DeleteAsync("/api/tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoggingOut_Twice_IsRejectedTheSecondTime()
    {
        var (client, _) = await AuthenticatedClientAsync();

        await client.DeleteAsync("/api/tokens");

        var response = await client.DeleteAsync("/api/tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoggingOut_WritesTheJtiUnderTheConfiguredInstancePrefix()
    {
        var (client, accessToken) = await AuthenticatedClientAsync();

        await client.DeleteAsync("/api/tokens");

        var key = $"api:revoked-token:{new JsonWebToken(accessToken).Id}";

        Assert.True(await redis.Connection.GetDatabase().KeyExistsAsync(key));
    }

    [Fact]
    public async Task LoggingOut_SetsATtlThatOutlivesTheTokenAndNothingMore()
    {
        var (client, accessToken) = await AuthenticatedClientAsync();
        var jwt = new JsonWebToken(accessToken);

        await client.DeleteAsync("/api/tokens");

        var ttl = await redis.Connection
            .GetDatabase()
            .KeyTimeToLiveAsync($"api:revoked-token:{jwt.Id}");

        Assert.NotNull(ttl);

        var remaining = jwt.ValidTo - DateTime.UtcNow;

        Assert.InRange(
            ttl.Value,
            remaining + TimeSpan.FromSeconds(50),
            remaining + TimeSpan.FromSeconds(70));
    }

    private async Task<TokenResponse> LoginAsync()
    {
        var response = await redis.Factory
            .CreateClient()
            .PostAsJsonAsync("/api/tokens", new LoginRequest("alice", "Password123!"));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private HttpClient ClientWithToken(string accessToken)
    {
        var client = redis.Factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    private async Task<(HttpClient Client, string AccessToken)> AuthenticatedClientAsync()
    {
        var token = await LoginAsync();

        return (ClientWithToken(token.AccessToken), token.AccessToken);
    }
}
