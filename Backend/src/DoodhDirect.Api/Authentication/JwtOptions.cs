using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Api.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    [Required, MinLength(3)]
    public string Issuer { get; init; } = string.Empty;

    [Required, MinLength(3)]
    public string Audience { get; init; } = string.Empty;

    [Required, MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;

    [Range(1, 60)]
    public int AccessTokenMinutes { get; init; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; init; } = 30;
}
