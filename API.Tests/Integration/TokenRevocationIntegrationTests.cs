using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using API.Authentication;
using API.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace API.Tests.Integration;

[Collection(RedisCollection.Name)]
[Trait("Category", "Integration")]
public class TokenRevocationIntegrationTests(RedisFixture redis)
{
    public static TheoryData<TokenRevocationStrategy> Strategies =>
    [
        TokenRevocationStrategy.Denylist,
        TokenRevocationStrategy.Allowlist,
    ];

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task LoggingOut_StopsTheTokenWorking(TokenRevocationStrategy strategy)
    {
        var (client, _) = await AuthenticatedClientAsync(strategy);

        var before = await client.GetAsync("/api/weatherforecast");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        var logout = await client.DeleteAsync("/api/tokens");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var after = await client.GetAsync("/api/weatherforecast");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task LoggingOut_MakesTheRejectionSayItWasRevoked(TokenRevocationStrategy strategy)
    {
        var (client, _) = await AuthenticatedClientAsync(strategy);

        await client.DeleteAsync("/api/tokens");

        var response = await client.GetAsync("/api/weatherforecast");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.NotNull(problem);
        Assert.Equal("This token has been revoked.", problem.Detail);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task LoggingOut_LeavesTheUsersOtherTokensAlone(TokenRevocationStrategy strategy)
    {
        var first = await LoginAsync(strategy);
        var second = await LoginAsync(strategy);

        await ClientWithToken(strategy, first.AccessToken).DeleteAsync("/api/tokens");

        var response = await ClientWithToken(strategy, second.AccessToken).GetAsync("/api/weatherforecast");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task LoggingOut_WithoutAToken_Returns401(TokenRevocationStrategy strategy)
    {
        var response = await redis.FactoryFor(strategy).CreateClient().DeleteAsync("/api/tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Strategies))]
    public async Task LoggingOut_Twice_IsRejectedTheSecondTime(TokenRevocationStrategy strategy)
    {
        var (client, _) = await AuthenticatedClientAsync(strategy);

        await client.DeleteAsync("/api/tokens");

        var response = await client.DeleteAsync("/api/tokens");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoggingIn_WritesNothingToTheDenylist()
    {
        var token = await LoginAsync(TokenRevocationStrategy.Denylist);

        Assert.False(await KeyExistsAsync(DenylistKey(token)));
    }

    [Fact]
    public async Task LoggingOut_WritesTheJtiToTheDenylist()
    {
        var (client, token) = await AuthenticatedClientAsync(TokenRevocationStrategy.Denylist);

        await client.DeleteAsync("/api/tokens");

        Assert.True(await KeyExistsAsync(DenylistKey(token)));
    }

    [Fact]
    public async Task LoggingOut_SetsADenylistTtlThatOutlivesTheTokenAndNothingMore()
    {
        var (client, token) = await AuthenticatedClientAsync(TokenRevocationStrategy.Denylist);

        await client.DeleteAsync("/api/tokens");

        await AssertTtlOutlivesTheTokenAsync(DenylistKey(token), token);
    }

    [Fact]
    public async Task LoggingIn_WritesTheJtiToTheAllowlist()
    {
        var token = await LoginAsync(TokenRevocationStrategy.Allowlist);

        Assert.True(await KeyExistsAsync(AllowlistKey(token)));
    }

    [Fact]
    public async Task LoggingIn_SetsAnAllowlistTtlThatOutlivesTheTokenAndNothingMore()
    {
        var token = await LoginAsync(TokenRevocationStrategy.Allowlist);

        await AssertTtlOutlivesTheTokenAsync(AllowlistKey(token), token);
    }

    [Fact]
    public async Task LoggingOut_DeletesTheJtiFromTheAllowlist()
    {
        var (client, token) = await AuthenticatedClientAsync(TokenRevocationStrategy.Allowlist);

        await client.DeleteAsync("/api/tokens");

        Assert.False(await KeyExistsAsync(AllowlistKey(token)));
    }

    [Fact]
    public async Task LosingTheAllowlistEntry_StopsTheTokenWorking()
    {
        var (client, token) = await AuthenticatedClientAsync(TokenRevocationStrategy.Allowlist);

        await redis.Connection.GetDatabase().KeyDeleteAsync(AllowlistKey(token));

        var response = await client.GetAsync("/api/weatherforecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string DenylistKey(TokenResponse token) => $"api:revoked-token:{new JsonWebToken(token.AccessToken).Id}";

    private static string AllowlistKey(TokenResponse token) => $"api:active-token:{new JsonWebToken(token.AccessToken).Id}";

    private Task<bool> KeyExistsAsync(string key) => redis.Connection.GetDatabase().KeyExistsAsync(key);

    private async Task AssertTtlOutlivesTheTokenAsync(string key, TokenResponse token)
    {
        var ttl = await redis.Connection.GetDatabase().KeyTimeToLiveAsync(key);

        Assert.NotNull(ttl);

        var remaining = new JsonWebToken(token.AccessToken).ValidTo - DateTime.UtcNow;

        Assert.InRange(
            ttl.Value,
            remaining + TimeSpan.FromSeconds(50),
            remaining + TimeSpan.FromSeconds(70));
    }

    private async Task<TokenResponse> LoginAsync(TokenRevocationStrategy strategy)
    {
        var response = await redis.FactoryFor(strategy)
            .CreateClient()
            .PostAsJsonAsync("/api/tokens", new LoginRequest("alice", "Password123!"));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private HttpClient ClientWithToken(TokenRevocationStrategy strategy, string accessToken)
    {
        var client = redis.FactoryFor(strategy).CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    private async Task<(HttpClient Client, TokenResponse Token)> AuthenticatedClientAsync(TokenRevocationStrategy strategy)
    {
        var token = await LoginAsync(strategy);

        return (ClientWithToken(strategy, token.AccessToken), token);
    }
}
