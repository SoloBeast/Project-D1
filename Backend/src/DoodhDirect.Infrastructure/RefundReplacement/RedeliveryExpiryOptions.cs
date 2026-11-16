using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Polling configuration for the expired re-delivery notice sweeper.
/// Follows the <c>RefundReplacement:DeliveryFailTimeout</c> options
/// convention: a bounded periodic poll over persisted
/// <c>CustomerChoiceAt + 1 hour</c> deadlines (no in-memory timers, no job
/// infrastructure). Notice-only: the worker never converts, approves, or
/// moves money.
/// </summary>
public sealed class RedeliveryExpiryOptions
{
    public const string SectionName = "RefundReplacement:RedeliveryExpiry";

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 60;

    [Range(1, 500)]
    public int BatchSize { get; init; } = 50;
}
