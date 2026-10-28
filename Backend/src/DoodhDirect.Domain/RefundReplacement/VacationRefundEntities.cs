using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.RefundReplacement;

/// <summary>
/// Lifecycle of a consolidated end-of-subscription vacation-refund request.
/// Creating the request never moves money: wallet credit happens only when an
/// Approved request is completed through the authorized settlement path.
/// </summary>
public enum VacationRefundStatus
{
    Pending,
    Approved,
    Rejected,
    Completed
}

/// <summary>
/// One consolidated Refund request per naturally completed prepaid
/// subscription, containing the eligible vacation-skipped delivery
/// occurrences as durable <see cref="VacationRefundOccurrence"/> entries.
///
/// The existing per-delivery <see cref="RefundReplacementRequest"/> model is
/// delivery-scoped (exactly one order or subscription-delivery source), so it
/// cannot hold a multi-occurrence consolidated request without compromising
/// its per-delivery rules. This dedicated subscription-level request reuses
/// the same review vocabulary (Pending → Approved → Completed/Rejected) and
/// the same frozen-allocation settlement mechanics, but links every included
/// occurrence individually so support can review it without repricing.
/// </summary>
public sealed class VacationRefundRequest : AuditableEntity
{
    /// <summary>Customer-facing reason recorded on every vacation refund.</summary>
    public const string VacationReason = "Customer vacation";

    /// <summary>Vacation refunds always carry a 0% deduction.</summary>
    public const decimal VacationDeductionPercentage = 0m;

    private VacationRefundRequest() { }

    public VacationRefundRequest(
        long subscriptionId,
        long customerId,
        long branchId,
        decimal grossPerOccurrence,
        decimal totalAmount,
        int occurrenceCount,
        decimal payableAmountSnapshot,
        int totalEntitlementSnapshot,
        string currency,
        long paymentId,
        string calculationBasisJson,
        DateTime submittedAt)
    {
        if (subscriptionId <= 0) throw new ArgumentOutOfRangeException(nameof(subscriptionId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (grossPerOccurrence <= 0) throw new ArgumentOutOfRangeException(nameof(grossPerOccurrence));
        if (totalAmount <= 0) throw new ArgumentOutOfRangeException(nameof(totalAmount));
        if (occurrenceCount <= 0) throw new ArgumentOutOfRangeException(nameof(occurrenceCount));
        if (payableAmountSnapshot <= 0) throw new ArgumentOutOfRangeException(nameof(payableAmountSnapshot));
        if (totalEntitlementSnapshot <= 0) throw new ArgumentOutOfRangeException(nameof(totalEntitlementSnapshot));
        if (paymentId <= 0) throw new ArgumentOutOfRangeException(nameof(paymentId));
        EnsureIndiaLocal(submittedAt, nameof(submittedAt));

        SubscriptionId = subscriptionId;
        CustomerId = customerId;
        BranchId = branchId;
        GrossPerOccurrence = grossPerOccurrence;
        TotalAmount = totalAmount;
        OccurrenceCount = occurrenceCount;
        PayableAmountSnapshot = payableAmountSnapshot;
        TotalEntitlementSnapshot = totalEntitlementSnapshot;
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
        PaymentId = paymentId;
        CalculationBasisJson = Required(calculationBasisJson, nameof(calculationBasisJson));
        Reason = VacationReason;
        DeductionPercentage = VacationDeductionPercentage;
        Status = VacationRefundStatus.Pending;
        SubmittedAt = submittedAt;
    }

    public string RequestNumber { get; private set; } = string.Empty;
    public long SubscriptionId { get; private set; }
    public long CustomerId { get; private set; }
    public long BranchId { get; private set; }
    public VacationRefundStatus Status { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    /// <summary>Always 0 for vacation refunds; persisted so support can verify without repricing.</summary>
    public decimal DeductionPercentage { get; private set; }
    /// <summary>Frozen per-occurrence share: Round(Payable / TotalEntitlement, 2).</summary>
    public decimal GrossPerOccurrence { get; private set; }
    /// <summary>Final refundable total across included occurrences (capped, remainder-safe).</summary>
    public decimal TotalAmount { get; private set; }
    public int OccurrenceCount { get; private set; }
    public decimal PayableAmountSnapshot { get; private set; }
    public int TotalEntitlementSnapshot { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    /// <summary>The backing prepaid payment at creation time (authoritative settlement target).</summary>
    public long PaymentId { get; private set; }
    /// <summary>
    /// Frozen calculation basis: included occurrence IDs with allocations,
    /// excluded occurrence IDs with reasons, payment remaining, and the 0%
    /// deduction decision. Support reviews the request from this snapshot.
    /// </summary>
    public string CalculationBasisJson { get; private set; } = string.Empty;
    public DateTime SubmittedAt { get; private set; }
    public long? DecidedByUserId { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? DecisionRemarks { get; private set; }
    public long? CompletedByUserId { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? CompletionRemarks { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public Subscription Subscription { get; private set; } = null!;
    public ICollection<VacationRefundOccurrence> Occurrences { get; private set; } = [];

    public void AssignRequestNumber(string requestNumber)
    {
        RequestNumber = Required(requestNumber, nameof(requestNumber));
    }

    public void Approve(long decidedByUserId, DateTime decidedAt, string? remarks)
    {
        if (decidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(decidedByUserId));
        EnsureIndiaLocal(decidedAt, nameof(decidedAt));
        EnsurePending("approved");

        DecidedByUserId = decidedByUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Optional(remarks);
        Status = VacationRefundStatus.Approved;
    }

    public void Reject(long decidedByUserId, DateTime decidedAt, string? remarks)
    {
        if (decidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(decidedByUserId));
        EnsureIndiaLocal(decidedAt, nameof(decidedAt));
        EnsurePending("rejected");

        DecidedByUserId = decidedByUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Optional(remarks);
        Status = VacationRefundStatus.Rejected;
    }

    /// <summary>
    /// Lifecycle transition only — the authorized settlement (wallet credits
    /// plus refund row) is executed by the service around this call inside the
    /// same transaction. Never moves money by itself.
    /// </summary>
    public void Complete(long completedByUserId, DateTime completedAt, string? remarks)
    {
        if (completedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(completedByUserId));
        EnsureIndiaLocal(completedAt, nameof(completedAt));
        if (Status != VacationRefundStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved request can be completed.");
        }

        CompletedByUserId = completedByUserId;
        CompletedAt = completedAt;
        CompletionRemarks = Optional(remarks);
        Status = VacationRefundStatus.Completed;
    }

    private void EnsurePending(string action)
    {
        if (Status != VacationRefundStatus.Pending)
        {
            throw new InvalidOperationException($"Only a pending request can be {action}.");
        }
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("Timestamp must be an India-local wall-clock value.", parameterName);
        }
    }
}

/// <summary>
/// Durable link between a consolidated vacation-refund request and one
/// included vacation-skipped occurrence, carrying its frozen allocation.
/// An occurrence belongs to at most one vacation-refund request ever, so
/// conflicting requests cannot overlap on the same occurrence.
/// </summary>
public sealed class VacationRefundOccurrence : Entity
{
    private VacationRefundOccurrence() { }

    public VacationRefundOccurrence(
        long vacationRefundRequestId,
        long subscriptionDeliveryId,
        DateOnly scheduledDate,
        decimal quantity,
        decimal allocationAmount)
    {
        if (vacationRefundRequestId < 0) throw new ArgumentOutOfRangeException(nameof(vacationRefundRequestId));
        if (subscriptionDeliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(subscriptionDeliveryId));
        if (allocationAmount < 0) throw new ArgumentOutOfRangeException(nameof(allocationAmount));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));

        VacationRefundRequestId = vacationRefundRequestId;
        SubscriptionDeliveryId = subscriptionDeliveryId;
        ScheduledDate = scheduledDate;
        Quantity = quantity;
        AllocationAmount = allocationAmount;
    }

    public long VacationRefundRequestId { get; private set; }
    public long SubscriptionDeliveryId { get; private set; }
    public DateOnly ScheduledDate { get; private set; }
    public decimal Quantity { get; private set; }
    /// <summary>Frozen wallet-credit allocation for this occurrence (0% deduction, remainder-safe).</summary>
    public decimal AllocationAmount { get; private set; }

    public VacationRefundRequest VacationRefundRequest { get; private set; } = null!;
    public SubscriptionDelivery SubscriptionDelivery { get; private set; } = null!;
}
