using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Deliveries;

public sealed class DeliveryOptions
{
    public const string SectionName = "Deliveries";

    [Range(4, 8)]
    public int OtpCodeLength { get; init; } = 6;

    [Range(1, 60)]
    public int OtpExpiryMinutes { get; init; } = 10;

    [Range(1, 10)]
    public int OtpMaximumAttempts { get; init; } = 5;

    [Range(1, 1440)]
    public int MaximumLocationAgeMinutes { get; init; } = 15;

    [Range(0, 60)]
    public int MaximumLocationFutureSkewMinutes { get; init; } = 5;

    [Range(10, 1000)]
    public int MaximumLocationsPerDelivery { get; init; } = 200;

    [Range(1, 365)]
    public int LocationRetentionDays { get; init; } = 30;

    [Range(1, 365)]
    public int SubscriptionGenerationWindowDays { get; init; } = 31;
}
