using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.RefundReplacement;

public enum RefundReplacementType
{
    Refund,
    Replacement
}

/// <summary>
/// D1-B: the customer's explicit remedy selection on a delivery-fail ticket.
/// Null until the customer chooses; immutable once recorded.
/// </summary>
public enum CustomerChoice
{
    Refund,
    ReDelivery
}

public enum RefundReplacementStatus
{
    Pending,
    /// <summary>
    /// D1-A: a delivery-fail ticket waiting for the customer to choose Refund
    /// or Re-Delivery before <see cref="RefundReplacementRequest.Deadline"/>.
    /// No timeout processing exists yet; approval/rejection remain compatible.
    /// </summary>
    AwaitingCustomerChoice,
    Approved,
    Rejected,
    Completed,
    /// <summary>
    /// D1-D slice 5.2: terminal non-delivery resolution. The approved remedy
    /// was attempted but can never be fulfilled (the re-delivery replacement
    /// failed and the customer was made whole through the linked refund-only
    /// remedy instead). This is never a successful fulfillment: <see
    /// cref="Completed"/> keeps meaning the remedy itself succeeded. A
    /// <c>Failed</c> ticket never blocks new requests and is ignored by the
    /// timeout worker exactly like every other terminal status.
    /// </summary>
    Failed,
    /// <summary>
    /// D1-D slice 7.1: terminal subscription-cancellation resolution. The
    /// subscription was cancelled while an approved re-delivery replacement
    /// was still pending, so the replacement was voided (never delivered)
    /// and the customer was made whole through the frozen entitlement
    /// refund instead. This is never a successful fulfillment: <see
    /// cref="Completed"/> keeps meaning the remedy itself succeeded, and
    /// <see cref="Failed"/> keeps meaning the replacement was attempted
    /// and failed (second-failure refund-only path). A <c>Cancelled</c>
    /// ticket never blocks new requests and is ignored by the timeout
    /// worker exactly like every other terminal status.
    /// </summary>
    Cancelled
}

/// <summary>
/// Origin of a refund/replacement request.
/// <see cref="MilkTestRejected"/> is the special flow opened immediately when a
/// customer rejects a performed milk test during delivery; it is exempt from the
/// normal post-delivery request window and does not require a proof image.
/// </summary>
public enum RefundReplacementSource
{
    CustomerPostDelivery,
    MilkTestRejected,
    /// <summary>
    /// D1-A: a ticket created when delivery staff marks a subscription delivery
    /// as failed because of customer fault (reason + proof image captured).
    /// </summary>
    DeliveryFailed
}

public sealed class RefundReplacementRequest : AuditableEntity
{
    private RefundReplacementRequest() { }

    public RefundReplacementRequest(
        long? orderId,
        long deliveryId,
        long customerId,
        long branchId,
        long? milkTestId,
        RefundReplacementType type,
        RefundReplacementSource source,
        string reason,
        string? remarks,
        DateTime submittedAt,
        DateTime? deadline,
        long? subscriptionDeliveryId = null,
        DateTime? failedAt = null,
        int? deliveryFailWindowHoursSnapshot = null,
        decimal? deliveryFailDeductionPercentageSnapshot = null,
        string? failureReasonSnapshot = null,
        RefundReplacementStatus initialStatus = RefundReplacementStatus.Pending)
    {
        if (orderId is <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        if (subscriptionDeliveryId is <= 0) throw new ArgumentOutOfRangeException(nameof(subscriptionDeliveryId));
        if ((orderId.HasValue ? 1 : 0) + (subscriptionDeliveryId.HasValue ? 1 : 0) != 1)
        {
            throw new ArgumentException(
                "A request must reference exactly one business source: a one-time order or a subscription delivery.");
        }
        if (deliveryId <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryId));
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (branchId <= 0) throw new ArgumentOutOfRangeException(nameof(branchId));
        if (milkTestId is <= 0) throw new ArgumentOutOfRangeException(nameof(milkTestId));
        if (source == RefundReplacementSource.MilkTestRejected && milkTestId is null)
        {
            throw new ArgumentException(
                "A milk-test reference is required for requests originating from a milk-test rejection.",
                nameof(milkTestId));
        }
        if (source == RefundReplacementSource.DeliveryFailed && milkTestId is not null)
        {
            throw new ArgumentException(
                "A staff failure ticket cannot reference a milk test.",
                nameof(milkTestId));
        }
        if (source != RefundReplacementSource.MilkTestRejected && deadline is null)
        {
            throw new ArgumentException(
                "A deadline is required for post-delivery requests.",
                nameof(deadline));
        }
        if (subscriptionDeliveryId.HasValue)
        {
            if (failedAt is null)
            {
                throw new ArgumentException(
                    "A failure timestamp is required for subscription-delivery tickets.",
                    nameof(failedAt));
            }
            if (deadline is null)
            {
                throw new ArgumentException(
                    "A customer-choice deadline is required for subscription-delivery tickets.",
                    nameof(deadline));
            }
            if (deliveryFailWindowHoursSnapshot is null or <= 0)
            {
                throw new ArgumentException(
                    "A delivery-fail window snapshot is required for subscription-delivery tickets.",
                    nameof(deliveryFailWindowHoursSnapshot));
            }
            if (deliveryFailDeductionPercentageSnapshot is null
                || deliveryFailDeductionPercentageSnapshot < 0
                || deliveryFailDeductionPercentageSnapshot > 100)
            {
                throw new ArgumentException(
                    "A delivery-fail deduction snapshot (0-100) is required for subscription-delivery tickets.",
                    nameof(deliveryFailDeductionPercentageSnapshot));
            }
        }
        else if (failedAt is not null
            || deliveryFailWindowHoursSnapshot is not null
            || deliveryFailDeductionPercentageSnapshot is not null
            || failureReasonSnapshot is not null)
        {
            throw new ArgumentException(
                "Delivery-fail snapshots apply only to subscription-delivery tickets.");
        }
        if (initialStatus is not (RefundReplacementStatus.Pending or RefundReplacementStatus.AwaitingCustomerChoice))
        {
            throw new ArgumentException(
                "A request must open as Pending or AwaitingCustomerChoice.",
                nameof(initialStatus));
        }
        if (initialStatus == RefundReplacementStatus.AwaitingCustomerChoice && subscriptionDeliveryId is null)
        {
            throw new ArgumentException(
                "Only subscription-delivery tickets can await customer choice.",
                nameof(initialStatus));
        }
        EnsureIndiaLocal(submittedAt, nameof(submittedAt));
        if (deadline is not null) EnsureIndiaLocal(deadline.Value, nameof(deadline));
        if (failedAt is not null) EnsureIndiaLocal(failedAt.Value, nameof(failedAt));

        OrderId = orderId;
        SubscriptionDeliveryId = subscriptionDeliveryId;
        DeliveryId = deliveryId;
        CustomerId = customerId;
        BranchId = branchId;
        MilkTestId = milkTestId;
        Type = type;
        Source = source;
        Reason = Required(reason, nameof(reason));
        Remarks = Optional(remarks);
        SubmittedAt = submittedAt;
        Deadline = deadline;
        FailedAt = failedAt;
        DeliveryFailWindowHoursSnapshot = deliveryFailWindowHoursSnapshot;
        DeliveryFailDeductionPercentageSnapshot = deliveryFailDeductionPercentageSnapshot is null
            ? null
            : decimal.Round(deliveryFailDeductionPercentageSnapshot.Value, 2, MidpointRounding.AwayFromZero);
        FailureReasonSnapshot = string.IsNullOrWhiteSpace(failureReasonSnapshot)
            ? null
            : failureReasonSnapshot.Trim();
        Status = initialStatus;
    }

    public string RequestNumber { get; private set; } = string.Empty;
    public long? OrderId { get; private set; }
    /// <summary>
    /// D1-A: set only for subscription-delivery tickets. Exactly one of
    /// <see cref="OrderId"/> / <see cref="SubscriptionDeliveryId"/> is set.
    /// The public business reference stays <c>Delivery.DeliveryNumber</c>.
    /// </summary>
    public long? SubscriptionDeliveryId { get; private set; }
    public long DeliveryId { get; private set; }
    public long CustomerId { get; private set; }
    public long BranchId { get; private set; }
    public long? MilkTestId { get; private set; }
    public RefundReplacementType Type { get; private set; }
    public RefundReplacementSource Source { get; private set; }
    public RefundReplacementStatus Status { get; private set; }
    /// <summary>
    /// D1-B: null while awaiting choice. Recording a choice also aligns
    /// <see cref="Type"/> (Refund / Replacement), retiring the D1-A placeholder.
    /// </summary>
    public CustomerChoice? CustomerChoice { get; private set; }
    public DateTime? CustomerChoiceAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public string Reason { get; private set; } = string.Empty;
    public string? Remarks { get; private set; }
    public DateTime SubmittedAt { get; private set; }
    public DateTime? Deadline { get; private set; }
    /// <summary>
    /// D1-A: the authoritative delivery-failed timestamp. The customer-choice
    /// deadline is <c>FailedAt + DeliveryFailWindowHoursSnapshot</c>.
    /// </summary>
    public DateTime? FailedAt { get; private set; }
    public int? DeliveryFailWindowHoursSnapshot { get; private set; }
    public decimal? DeliveryFailDeductionPercentageSnapshot { get; private set; }
    public string? FailureReasonSnapshot { get; private set; }
    public long? DecidedByUserId { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? DecisionRemarks { get; private set; }
    public long? CompletedByUserId { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? CompletionRemarks { get; private set; }

    /// <summary>
    /// Requests opened from a milk-test rejection are exempt from the proof
    /// image requirement (the milk test itself is the evidence).
    /// </summary>
    public bool ProofImageRequired => Source != RefundReplacementSource.MilkTestRejected;

    public Order? Order { get; private set; }
    public SubscriptionDelivery? SubscriptionDelivery { get; private set; }
    public Delivery Delivery { get; private set; } = null!;
    public MilkTest? MilkTest { get; private set; }
    public User Customer { get; private set; } = null!;
    public Branch Branch { get; private set; } = null!;
    public User? DecidedByUser { get; private set; }
    public User? CompletedByUser { get; private set; }
    /// <summary>
    /// D1-D: replacement occurrences routed from this request. Additive
    /// navigation only; D1-A/B/C lifecycle behavior is unchanged.
    /// </summary>
    public ICollection<SubscriptionDelivery> ReplacementDeliveries { get; private set; } = [];
    public ICollection<RefundReplacementImage> Images { get; private set; } = [];

    public void AssignRequestNumber(string requestNumber)
    {
        if (string.IsNullOrWhiteSpace(requestNumber))
        {
            throw new ArgumentException("A request number is required.", nameof(requestNumber));
        }

        if (!string.IsNullOrEmpty(RequestNumber))
        {
            throw new InvalidOperationException("The request number has already been assigned.");
        }

        RequestNumber = requestNumber.Trim();
    }

    public void AddImage(RefundReplacementImage image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        if (image.RequestId != Id)
        {
            throw new InvalidOperationException("The image belongs to a different request.");
        }

        Images.Add(image);
    }

    public void RemoveImage(Guid publicId)
    {
        var image = Images.FirstOrDefault(x => x.PublicId == publicId);
        if (image is null)
        {
            throw new InvalidOperationException("The image does not belong to this request.");
        }

        Images.Remove(image);
    }

    public void Approve(long decidedByUserId, DateTime decidedAt, string? remarks)
    {
        if (decidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(decidedByUserId));
        EnsureIndiaLocal(decidedAt, nameof(decidedAt));
        EnsurePending("approved");

        DecidedByUserId = decidedByUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Approved;
    }

    public void Reject(long decidedByUserId, DateTime decidedAt, string? remarks)
    {
        if (decidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(decidedByUserId));
        EnsureIndiaLocal(decidedAt, nameof(decidedAt));
        EnsurePending("rejected");

        DecidedByUserId = decidedByUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Rejected;
    }

    public void Complete(long completedByUserId, DateTime completedAt, string? remarks)
    {
        if (completedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(completedByUserId));
        EnsureIndiaLocal(completedAt, nameof(completedAt));
        if (Status != RefundReplacementStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved request can be completed.");
        }

        CompletedByUserId = completedByUserId;
        CompletedAt = completedAt;
        CompletionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Completed;
    }

    /// <summary>
    /// D1-D slice 5.2: closes an approved ticket whose remedy failed and will
    /// never be fulfilled, without claiming success. The resolution actor and
    /// timestamp reuse the <c>CompletedBy*</c> columns (they record who closed
    /// the ticket and when, for every terminal fulfillment outcome); the
    /// status itself carries the non-delivery meaning. Allowed only from
    /// <see cref="RefundReplacementStatus.Approved"/>, exactly like <see
    /// cref="Complete"/> — the two outcomes are mutually exclusive.
    /// </summary>
    public void MarkRemedyFailed(long resolvedByUserId, DateTime resolvedAt, string? remarks)
    {
        if (resolvedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(resolvedByUserId));
        EnsureIndiaLocal(resolvedAt, nameof(resolvedAt));
        if (Status != RefundReplacementStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved request can be closed as a failed remedy.");
        }

        CompletedByUserId = resolvedByUserId;
        CompletedAt = resolvedAt;
        CompletionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Failed;
    }

    /// <summary>
    /// D1-D slice 7.1: closes an approved re-delivery ticket whose pending
    /// replacement was voided by subscription cancellation and refunded
    /// without ever being delivered. The resolution actor and timestamp
    /// reuse the <c>CompletedBy*</c> columns (they record who closed the
    /// ticket and when, for every terminal fulfillment outcome); the status
    /// itself carries the cancellation meaning. Allowed only from <see
    /// cref="RefundReplacementStatus.Approved"/>, exactly like <see
    /// cref="Complete"/> and <see cref="MarkRemedyFailed"/> — the three
    /// outcomes are mutually exclusive.
    /// </summary>
    public void MarkSubscriptionCancelled(long resolvedByUserId, DateTime resolvedAt, string? remarks)
    {
        if (resolvedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(resolvedByUserId));
        EnsureIndiaLocal(resolvedAt, nameof(resolvedAt));
        if (Status != RefundReplacementStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved request can be closed for subscription cancellation.");
        }

        CompletedByUserId = resolvedByUserId;
        CompletedAt = resolvedAt;
        CompletionRemarks = Optional(remarks);
        Status = RefundReplacementStatus.Cancelled;
    }

    /// <summary>
    /// D1-B: records the customer's explicit remedy choice. Requires a ticket
    /// awaiting choice with no recorded resolution and a live choice deadline.
    /// The persisted deadline (not current configuration) is authoritative;
    /// the future timeout processor contends on the same transition so exactly
    /// one resolution can win (guarded by the row-version concurrency token).
    /// </summary>
    public void RecordCustomerChoice(CustomerChoice choice, DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (Status != RefundReplacementStatus.AwaitingCustomerChoice)
        {
            throw new InvalidOperationException("The ticket is not awaiting customer choice.");
        }
        if (CustomerChoice.HasValue)
        {
            throw new InvalidOperationException("The customer choice has already been recorded.");
        }
        if (Deadline is null || indiaLocalNow >= Deadline.Value)
        {
            throw new InvalidOperationException("The customer choice window has expired.");
        }

        CustomerChoice = choice;
        CustomerChoiceAt = indiaLocalNow;
        Type = choice == global::DoodhDirect.Domain.RefundReplacement.CustomerChoice.Refund
            ? RefundReplacementType.Refund
            : RefundReplacementType.Replacement;
    }

    /// <summary>
    /// D1-C: records the automatic timeout resolution. Requires a ticket still
    /// awaiting choice with no recorded resolution whose persisted deadline has
    /// passed (exclusive: <c>now >= Deadline</c>). Sets the choice to Refund and
    /// aligns <see cref="Type"/> exactly like an explicit customer Refund choice;
    /// the caller then moves the ticket through the existing approve/complete
    /// flow and credits the wallet inside the same transaction. Contends on the
    /// same row-version token as <see cref="RecordCustomerChoice"/> so exactly
    /// one resolution (customer choice or timeout) can win.
    /// </summary>
    public void RecordAutomaticTimeoutRefund(DateTime indiaLocalNow)
    {
        EnsureIndiaLocal(indiaLocalNow, nameof(indiaLocalNow));
        if (Status != RefundReplacementStatus.AwaitingCustomerChoice)
        {
            throw new InvalidOperationException("The ticket is not awaiting customer choice.");
        }
        if (CustomerChoice.HasValue)
        {
            throw new InvalidOperationException("The customer choice has already been recorded.");
        }
        if (Deadline is null || indiaLocalNow < Deadline.Value)
        {
            throw new InvalidOperationException("The customer choice window has not expired.");
        }

        CustomerChoice = global::DoodhDirect.Domain.RefundReplacement.CustomerChoice.Refund;
        CustomerChoiceAt = indiaLocalNow;
        Type = RefundReplacementType.Refund;
    }

    /// <summary>
    /// D1-A race guard: links the rejecting milk test to a delivery-fail ticket
    /// that was created first by the staff failure path, keeping one ticket.
    /// </summary>
    public void AttachMilkTest(long milkTestId)
    {
        if (milkTestId <= 0) throw new ArgumentOutOfRangeException(nameof(milkTestId));
        if (MilkTestId.HasValue)
        {
            throw new InvalidOperationException("A milk test is already linked to this request.");
        }
        if (Source == RefundReplacementSource.DeliveryFailed)
        {
            throw new InvalidOperationException("A staff failure ticket cannot reference a milk test.");
        }

        MilkTestId = milkTestId;
    }

    private void EnsurePending(string operation)
    {
        if (Status is not (RefundReplacementStatus.Pending or RefundReplacementStatus.AwaitingCustomerChoice))
        {
            throw new InvalidOperationException($"Only a pending request can be {operation}.");
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
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                parameterName);
        }
    }
}

public sealed class RefundReplacementImage : PublicEntity
{
    private RefundReplacementImage() { }

    public RefundReplacementImage(
        long requestId,
        string storageKey,
        string fileName,
        string contentType,
        long fileSize,
        long uploadedByUserId,
        DateTime uploadedAt)
    {
        if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (fileSize <= 0) throw new ArgumentOutOfRangeException(nameof(fileSize));
        if (uploadedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(uploadedByUserId));
        EnsureIndiaLocal(uploadedAt, nameof(uploadedAt));

        RequestId = requestId;
        StorageKey = Required(storageKey, nameof(storageKey));
        FileName = Required(fileName, nameof(fileName));
        ContentType = Required(contentType, nameof(contentType)).ToLowerInvariant();
        FileSize = fileSize;
        UploadedByUserId = uploadedByUserId;
        UploadedAt = uploadedAt;
    }

    public long RequestId { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public long FileSize { get; private set; }
    public long UploadedByUserId { get; private set; }
    public DateTime UploadedAt { get; private set; }

    public RefundReplacementRequest Request { get; private set; } = null!;
    public User UploadedByUser { get; private set; } = null!;

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "The timestamp must be India-local with an unspecified DateTime kind.",
                parameterName);
        }
    }
}
