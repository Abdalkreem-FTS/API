using System.ComponentModel.DataAnnotations;

namespace API.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    [Required]
    [MinLength(32)] // HMAC-SHA256 needs at least 256 bits of key material, so 32 UTF-8 characters.
    public string SecurityKey { get; init; } = string.Empty;

    [Range(1, 1440)]
    public int ExpiryMinutes { get; init; } = 15;
}
