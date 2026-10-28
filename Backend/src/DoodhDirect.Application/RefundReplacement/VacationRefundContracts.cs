using DoodhDirect.Domain.RefundReplacement;

namespace DoodhDirect.Application.RefundReplacement;

/// <summary>
/// One included occurrence of a consolidated vacation-refund request:
/// individually identifiable with its frozen allocation.
/// </summary>
public sealed record VacationRefundOccurrenceResult(
    Guid OccurrencePublicId,
    DateOnly ScheduledDate,
    decimal Quantity,
    decimal AllocationAmount);

/// <summary>
/// An excluded occurrence with the machine-readable reason support uses to
/// resolve it explicitly (settled elsewhere, unresolved ticket overlap,
/// ambiguous settlement, ineligible record, or already included).
/// </summary>
public sealed record VacationRefundExcludedOccurrence(
    Guid OccurrencePublicId,
    DateOnly ScheduledDate,
    string Reason);

/// <summary>
/// Consolidated support view of a vacation-refund request: subscription and
/// customer reference, vacation reason, per-occurrence detail, frozen pricing
/// basis, final amount, 0% deduction, and review/settlement status.
/// </summary>
public sealed record VacationRefundRequestResult(
    Guid RequestId,
    string RequestNumber,
    Guid SubscriptionPublicId,
    long CustomerId,
    string CustomerName,
    string CustomerMobile,
    long BranchId,
    string BranchCode,
    string BranchName,
    string Reason,
    VacationRefundStatus Status,
    int OccurrenceCount,
    decimal GrossPerOccurrence,
    decimal TotalAmount,
    decimal DeductionPercentage,
    decimal PayableAmountSnapshot,
    int TotalEntitlementSnapshot,
    string Currency,
    DateTime SubmittedAt,
    long? DecidedByUserId,
    string? DecidedByName,
    DateTime? DecidedAt,
    string? DecisionRemarks,
    long? CompletedByUserId,
    string? CompletedByName,
    DateTime? CompletedAt,
    string? CompletionRemarks,
    IReadOnlyList<VacationRefundOccurrenceResult> Occurrences,
    /// <summary>
    /// Server-derived origin indicator: true when the authoritative owning
    /// account is a non-customer (employee/owner/administrator) account.
    /// Never accepted from client input; staff UI gating only — execution
    /// revalidates inside the service guard.
    /// </summary>
    bool IsEmployeeOriginated = false);

/// <summary>
/// Outcome of one end-of-subscription vacation-refund evaluation. Either a
/// consolidated request was created, processing was deferred (term not over
/// or final-day outcomes unresolved), or the subscription was recorded as
/// processed with no eligible occurrences.
/// </summary>
public sealed record VacationRefundProcessResult(
    Guid SubscriptionPublicId,
    bool RequestCreated,
    Guid? RequestPublicId,
    int IncludedCount,
    IReadOnlyList<VacationRefundExcludedOccurrence> Excluded,
    string? DeferredReason,
    bool AlreadyProcessed);

/// <summary>
/// Customer-facing vacation-refund state for one subscription. When no
/// request exists yet after the term has ended, the counts and the blocked
/// reason tell the customer whether evaluation is still pending or is held
/// up (for example by an unresolved overlapping ticket or a missing
/// backing payment) instead of showing an empty history with no context.
/// </summary>
public sealed record VacationRefundSubscriptionStatus(
    Guid SubscriptionPublicId,
    bool TermEnded,
    bool Processed,
    VacationRefundRequestResult? Request,
    int VacationSkippedCount,
    int UnresolvedInTermCount,
    bool HasBlockingTicket,
    bool HasBackingPayment,
    string? BlockedReason);

public sealed record DecideVacationRefundRequest(string? Remarks);

public interface IVacationRefundService
{
    /// <summary>
    /// Processes every naturally completed subscription still awaiting
    /// vacation-refund evaluation. Restart- and repeat-safe: each
    /// subscription is evaluated at most once to a durable outcome.
    /// </summary>
    Task<int> ProcessDueAsync(CancellationToken cancellationToken);

    Task<VacationRefundProcessResult> ProcessSubscriptionAsync(
        Guid subscriptionPublicId,
        CancellationToken cancellationToken);

    Task<VacationRefundRequestResult> GetForStaffAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken);

    Task<VacationRefundRequestResult> GetForCustomerAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Read-only customer-facing state for one owned subscription: the
    /// created request when present, otherwise the pending/blocked posture
    /// so an end-of-term vacation refund that has not appeared yet is
    /// explained instead of silently missing. Never processes or mutates.
    /// </summary>
    Task<VacationRefundSubscriptionStatus> GetStatusForCustomerAsync(
        RefundReplacementActor actor,
        Guid subscriptionPublicId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<VacationRefundRequestResult>> ListForStaffAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<VacationRefundRequestResult>> ListForCustomerAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken);

    Task<VacationRefundRequestResult> ApproveAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideVacationRefundRequest request,
        CancellationToken cancellationToken);

    Task<VacationRefundRequestResult> RejectAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideVacationRefundRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Authorized settlement point: transitions the Approved request to
    /// Completed and credits the frozen allocations to the wallet. Never
    /// credits on creation or approval alone.
    /// </summary>
    Task<VacationRefundRequestResult> CompleteAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideVacationRefundRequest request,
        CancellationToken cancellationToken);
}
