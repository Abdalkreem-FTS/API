using API.Authentication;
using API.Models;
using Microsoft.IdentityModel.JsonWebTokens;

namespace API.Tests;

public class JwtTokenGeneratorTests
{
    private const string Key = "test-signing-key-that-is-long-enough-for-hs256";

    private static readonly User Alice = new(
        Guid.Parse("8f14e45f-ceea-467a-9575-2c1a1a4a2d1b"),
        "alice",
        "Password123!",
        ["admin", "user"]);

    [Fact]
    public void GenerateToken_WritesTheRegisteredClaims()
    {
        var jwt = new JsonWebToken(Create().GenerateToken(Alice).Response.AccessToken);

        Assert.Equal(Alice.Id.ToString(), jwt.Subject);
        Assert.NotEmpty(jwt.Id);
        Assert.Equal(Alice.Username, jwt.GetClaim(JwtRegisteredClaimNames.Name).Value);
        Assert.Equal("test-issuer", jwt.Issuer);
        Assert.Contains("test-audience", jwt.Audiences);
    }

    [Fact]
    public void GenerateToken_WritesEveryRole()
    {
        var jwt = new JsonWebToken(Create().GenerateToken(Alice).Response.AccessToken);

        var roles = jwt.Claims
            .Where(claim => claim.Type == JwtTokenGenerator.RoleClaimType)
            .Select(claim => claim.Value);

        Assert.Equal(["admin", "user"], roles);
    }

    [Fact]
    public void GenerateToken_WritesIssuedAtAndExpiry()
    {
        var (_, response) = Create(expiryMinutes: 30).GenerateToken(Alice);
        var jwt = new JsonWebToken(response.AccessToken);

        Assert.True(jwt.IssuedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.True((response.ExpiresAt - jwt.ValidTo).Duration() < TimeSpan.FromSeconds(2));
        Assert.True(response.ExpiresAt > DateTime.UtcNow.AddMinutes(29));
    }

    [Fact]
    public void GenerateToken_GivesEachTokenItsOwnJti()
    {
        var generator = Create();

        var first = new JsonWebToken(generator.GenerateToken(Alice).Response.AccessToken).Id;
        var second = new JsonWebToken(generator.GenerateToken(Alice).Response.AccessToken).Id;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GenerateToken_ReturnsTheJtiItWroteIntoTheToken()
    {
        var (tokenId, response) = Create().GenerateToken(Alice);

        Assert.Equal(new JsonWebToken(response.AccessToken).Id, tokenId);
    }

    [Fact]
    public void CreateValidationParameters_ReadsTheShortClaimNamesWeWrite()
    {
        var parameters = JwtTokenGenerator.CreateValidationParameters(Options());

        Assert.Equal(JwtRegisteredClaimNames.Name, parameters.NameClaimType);
        Assert.Equal(JwtTokenGenerator.RoleClaimType, parameters.RoleClaimType);
    }

    private static JwtTokenGenerator Create(int expiryMinutes = 60) => new(new TestOptionsMonitor<JwtOptions>(Options(expiryMinutes)));

    private static JwtOptions Options(int expiryMinutes = 60) => new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SecurityKey = Key,
        ExpiryMinutes = expiryMinutes,
    };
}
