using System.Security.Claims;
using System.Text;
using API.Contracts;
using API.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace API.Authentication;

public sealed class JwtTokenGenerator(IOptionsMonitor<JwtOptions> options)
{
    public const string TokenType = "Bearer";
    public const string RoleClaimType = "role";

    private static readonly JsonWebTokenHandler Handler = new();

    public static TokenValidationParameters CreateValidationParameters(JwtOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,

        ValidateAudience = true,
        ValidAudience = options.Audience,

        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(options),

        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,

        NameClaimType = JwtRegisteredClaimNames.Name,
        RoleClaimType = RoleClaimType
    };

    public (string TokenId, TokenResponse Response) GenerateToken(User user)
    {
        var jwt = options.CurrentValue;

        var issuedAt = DateTime.UtcNow;
        var expiresAt = issuedAt.AddMinutes(jwt.ExpiryMinutes);
        var tokenId = Guid.NewGuid().ToString();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, tokenId),
            new(JwtRegisteredClaimNames.Name, user.Username)
        };

        claims.AddRange(user.Roles.Select(role => new Claim(RoleClaimType, role)));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),

            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expiresAt,

            SigningCredentials = new SigningCredentials(SigningKey(jwt), SecurityAlgorithms.HmacSha256),
        };

        return (tokenId, new TokenResponse(Handler.CreateToken(descriptor), TokenType, expiresAt));
    }

    private static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SecurityKey));
}
