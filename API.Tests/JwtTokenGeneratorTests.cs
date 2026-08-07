using System.IdentityModel.Tokens.Jwt;

namespace API.Tests;

public class JwtTokenGeneratorTests
{
    private const string Key = "test-signing-key-that-is-long-enough-for-hs256";
    private const string OtherKey = "a-completely-different-key-also-long-enough!!";

    [Fact]
    public void ValidateToken_AcceptsATokenWeJustCreated()
    {
        var generator = Create();

        var token = generator.GenerateToken("alice");
        var result = generator.ValidateToken(token.AccessToken);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void GenerateToken_PutsTheUsernameInTheToken()
    {
        var token = Create().GenerateToken("alice");
        
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);

        Assert.Equal("alice", jwt.Claims.Single(claim => claim.Type == "name").Value);
        Assert.Equal("Bearer", token.TokenType);
        Assert.True(token.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public void ValidateToken_RejectsAnExpiredToken()
    {
        var generator = Create(expiryMinutes: -1);

        var result = generator.ValidateToken(generator.GenerateToken("alice").AccessToken);

        Assert.False(result.IsValid);
        Assert.Equal("The token has expired.", result.Error);
    }

    [Fact]
    public void ValidateToken_RejectsATokenThatWasEdited()
    {
        var generator = Create();
        var token = generator.GenerateToken("alice").AccessToken;

        var edited = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        var result = generator.ValidateToken(edited);

        Assert.False(result.IsValid);
        Assert.Equal("The signature is not valid - the token was altered.", result.Error);
    }

    [Fact]
    public void ValidateToken_RejectsATokenSignedWithADifferentKey()
    {
        var theirToken = Create(key: OtherKey).GenerateToken("alice").AccessToken;

        Assert.False(Create().ValidateToken(theirToken).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("aaa.bbb.ccc")]
    public void ValidateToken_RejectsRubbish(string token)
    {
        var result = Create().ValidateToken(token);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    private static JwtTokenGenerator Create(int expiryMinutes = 60, string key = Key) =>
        new(new JwtOptions("test-issuer", "test-audience", key, expiryMinutes));
}
