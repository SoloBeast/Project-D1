using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// D1-C polling configuration for the delivery-fail timeout worker. Follows
/// the <c>Payments:Reconciliation</c> options convention: a bounded periodic
/// poll over persisted deadlines (no in-memory timers, no job infrastructure).
/// </summary>
public sealed class DeliveryFailTimeoutOptions
{
    public const string SectionName = "RefundReplacement:DeliveryFailTimeout";

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 60;

    [Range(1, 500)]
    public int BatchSize { get; init; } = 50;
}
