using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Polling configuration for the end-of-subscription vacation-refund worker.
/// Follows the delivery-fail timeout options convention: a bounded periodic
/// poll over persisted subscription state (no in-memory timers, no job
/// infrastructure), so restarts and missed runs recover on the next cycle.
/// </summary>
public sealed class VacationRefundOptions
{
    public const string SectionName = "RefundReplacement:VacationRefund";

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 300;
}
