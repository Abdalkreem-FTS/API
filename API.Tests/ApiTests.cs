using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace API.Tests;

public class ApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Weatherforecast_WithoutAToken_Returns401()
    {
        var response = await _client.GetAsync("/weatherforecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Weatherforecast_WithAToken_Returns200()
    {
        var token = await LoginAsync("alice", "Password123!");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/weatherforecast");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Hello alice, you are authorized.", body);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsAToken()
    {
        var token = await LoginAsync("alice", "Password123!");

        Assert.NotEmpty(token.AccessToken);
        Assert.Equal("Bearer", token.TokenType);
    }

    [Theory]
    [InlineData("alice", "wrong-password")]
    [InlineData("nobody", "Password123!")]
    public async Task Login_WithBadCredentials_Returns401(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(username, password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Validate_ReportsWhetherATokenIsGood()
    {
        var token = await LoginAsync("alice", "Password123!");

        var good = await ValidateAsync(token.AccessToken);
        Assert.True(good.IsValid);

        var edited = token.AccessToken[..^1] + (token.AccessToken[^1] == 'A' ? 'B' : 'A');
        var bad = await ValidateAsync(edited);

        Assert.False(bad.IsValid);
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public async Task Root_IsPublic()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<TokenResponse> LoginAsync(string username, string password)
    {
        var response = await _client.PostAsJsonAsync("/auth/login", new LoginRequest(username, password));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<TokenValidation> ValidateAsync(string token)
    {
        var response = await _client.PostAsJsonAsync("/auth/validate", new ValidateRequest(token));

        return (await response.Content.ReadFromJsonAsync<TokenValidation>())!;
    }
}
