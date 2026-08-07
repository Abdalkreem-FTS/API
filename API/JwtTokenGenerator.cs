using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace API;

public record JwtOptions(string Issuer, string Audience, string SecurityKey, int ExpiryMinutes);

public class JwtTokenGenerator(JwtOptions options)
{
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly SymmetricSecurityKey _key = SigningKey(options);
    private readonly TokenValidationParameters _validationParameters = CreateValidationParameters(options);

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
    };

    private static SymmetricSecurityKey SigningKey(JwtOptions options) => new(Encoding.UTF8.GetBytes(options.SecurityKey));

    public TokenResponse GenerateToken(string username)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(options.ExpiryMinutes);

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: [new Claim("name", username)],
            expires: expiresAt,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new TokenResponse(_handler.WriteToken(token), "Bearer", expiresAt);
    }

    public TokenValidation ValidateToken(string token)
    {
        try
        {
            _handler.ValidateToken(token, _validationParameters, out _);

            return new TokenValidation(true, null);
        }
        catch (SecurityTokenExpiredException)
        {
            return new TokenValidation(false, "The token has expired.");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return new TokenValidation(false, "The signature is not valid - the token was altered.");
        }
        catch (SecurityTokenException exception)
        {
            return new TokenValidation(false, exception.Message);
        }
        catch (ArgumentException)
        {
            return new TokenValidation(false, "This is not a valid JWT.");
        }
    }
}
