using System.Net.Http.Headers;
using System.Net.Http.Json;
using API.Authentication;
using API.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace API.Tests;

public abstract class ControllerTestsBase(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    protected const string Username = "alice";
    protected const string Password = "Password123!";

    private WebApplicationFactory<Program> Factory { get; } = factory;

    protected HttpClient Client { get; } = factory.CreateClient();

    protected async Task<TokenResponse> LoginAsync(string username = Username, string password = Password)
    {
        var response = await Client.PostAsJsonAsync("/api/tokens", new LoginRequest(username, password));

        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    protected async Task<HttpClient> AuthenticatedClientAsync(string username = Username, string password = Password)
    {
        var token = await LoginAsync(username, password);

        return ClientWithToken(token.AccessToken);
    }

    protected HttpClient ClientWithToken(string accessToken)
    {
        var client = Factory.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(JwtTokenGenerator.TokenType, accessToken);

        return client;
    }

    protected JwtOptions HostJwtOptions => Factory.Services.GetRequiredService<IOptionsMonitor<JwtOptions>>().CurrentValue;
}
