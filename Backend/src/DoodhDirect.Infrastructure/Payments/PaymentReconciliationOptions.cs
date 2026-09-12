using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Payments;

public sealed class PaymentReconciliationOptions
{
    public const string SectionName = "Payments:Reconciliation";

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 60;

    [Range(1, 500)]
    public int BatchSize { get; init; } = 50;

    [Range(1, 1440)]
    public int CandidateAgeMinutes { get; init; } = 15;

    [Range(1, 60)]
    public int WebhookProcessingStaleMinutes { get; init; } = 5;
}
