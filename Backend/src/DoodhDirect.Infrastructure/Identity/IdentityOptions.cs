using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class IdentityOptions
{
    public const string SectionName = "Authentication:Identity";

    [Range(1, 30)]
    public int OtpLifetimeMinutes { get; init; } = 5;

    [Range(1, 10)]
    public int OtpMaxAttempts { get; init; } = 5;

    [Range(1, 20)]
    public int OtpRequestsPerWindow { get; init; } = 3;

    [Range(1, 60)]
    public int OtpRateLimitWindowMinutes { get; init; } = 15;

    [Range(10_000, 2_000_000)]
    public int PasswordIterations { get; init; } = 120_000;
}
