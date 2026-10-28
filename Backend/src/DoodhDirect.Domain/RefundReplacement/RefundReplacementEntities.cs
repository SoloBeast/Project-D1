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

/// <summary>
/// Stage 2: explicit persisted settlement method for a Refund ticket.
/// <see cref="Unspecified"/> is the legacy value: every ticket approved
/// before the manual-bank path existed keeps today's wallet behavior and
/// is never silently converted. New Refund-shaped approvals must designate
/// <see cref="Wallet"/> or <see cref="ManualBankTransfer"/> explicitly;
/// designation is exactly-once (see <c>DesignateWalletSettlement</c> /
/// <c>DesignateManualBankSettlement</c>). Non-refund ticket types never
/// carry a method.
/// </summary>
public enum RefundSettlementMethod
{
    Unspecified = 0,
    Wallet = 1,
    ManualBankTransfer = 2
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
    Cancelled,
    /// <summary>
    /// Legacy remediation: terminal supersede resolution. An approved,
    /// order-backed Replacement ticket for a one-time order — a remedy with
    /// no fulfillment workflow — has been superseded by a new supported
    /// Refund ticket. No fulfillment or refund is implied by this status:
    /// <see cref="Completed"/> keeps meaning the remedy itself succeeded,
    /// <see cref="Failed"/> keeps meaning the replacement was attempted
    /// and failed. A <c>Superseded</c> ticket never blocks new requests and
    /// is ignored by the timeout worker exactly like every other terminal
    /// status.
    /// </summary>
    Superseded
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
        RefundReplacementStatus initialStatus = RefundReplacementStatus.Pending,
        bool noServiceProvided = false)
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
            // A customer post-delivery complaint concerns a delivered
            // occurrence — no failure ever happened, so no failure timestamp
            // exists. Failure-origin tickets still require one.
            if (failedAt is null && source != RefundReplacementSource.CustomerPostDelivery)
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
        NoServiceProvided = noServiceProvided;
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
    /// <summary>
    /// Stage 1: support's explicit customer-fault deduction decision for an
    /// order-backed Refund ticket. The frozen applied percentage (the shared
    /// Admin Centre percentage when applied, zero when waived). Null until
    /// the decision is recorded at approval; historical and non-order tickets
    /// keep null. Later configuration changes never alter a frozen value.
    /// </summary>
    public decimal? AppliedDeductionPercentage { get; private set; }
    /// <summary>
    /// Stage 1: true when support waived the customer-fault deduction.
    /// </summary>
    public bool DeductionWaived { get; private set; }
    /// <summary>
    /// Stage 1: required nonblank reason when the deduction was waived.
    /// </summary>
    public string? DeductionWaiverReason { get; private set; }
    /// <summary>
    /// Stage 2: explicit settlement method. Unspecified until support
    /// designates one at approval; legacy tickets keep Unspecified forever
    /// and settle through the established wallet behavior. Exactly-once:
    /// see <see cref="DesignateWalletSettlement"/> and
    /// <see cref="DesignateManualBankSettlement"/>.
    /// </summary>
    public RefundSettlementMethod SettlementMethod { get; private set; } = RefundSettlementMethod.Unspecified;
    /// <summary>
    /// Stage 2: frozen beneficiary snapshot for a ManualBankTransfer ticket.
    /// Captured atomically with the settlement designation at approval from
    /// the customer's saved bank details; later profile edits cannot alter
    /// it. The full account number is stored only in DataProtection-
    /// encrypted form under the dedicated refund-beneficiary purpose; reads
    /// below always use the masked last-4 form.
    /// </summary>
    public string? ManualBankHolderNameSnapshot { get; private set; }
    public string? ManualBankNameSnapshot { get; private set; }
    public string? ManualBankIfscSnapshot { get; private set; }
    public string? ManualBankAccountLast4Snapshot { get; private set; }
    public string? ManualBankProtectedAccountNumber { get; private set; }
    /// <summary>
    /// Stage 2: authoritative frozen transfer amount and currency for a
    /// ManualBankTransfer ticket, computed with the established refund
    /// formula at approval and reserved atomically against the covering
    /// payments. Immutable once designated; <c>null</c> for every other
    /// ticket shape.
    /// </summary>
    public decimal? ManualBankReservedAmount { get; private set; }
    public string? ManualBankReservedCurrency { get; private set; }
    /// <summary>
    /// Stage 2: explicit void metadata for a ManualBankTransfer reservation
    /// released before any transfer reached the bank. Set only by
    /// <see cref="VoidManualBankTransfer"/>; the ticket moves to the
    /// terminal <c>Rejected</c> status while the original approval identity
    /// is preserved. Never set when a transfer may have reached the bank.
    /// </summary>
    public long? ManualBankVoidedByUserId { get; private set; }
    public DateTime? ManualBankVoidedAt { get; private set; }
    public string? ManualBankVoidReason { get; private set; }
    public long? DecidedByUserId { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? DecisionRemarks { get; private set; }
    /// <summary>
    /// Legacy remediation: the staff member who superseded this unsupported
    /// Replacement ticket. Set only by <see cref="MarkSuperseded"/>; the
    /// original <see cref="DecidedByUserId"/> approval identity is preserved.
    /// </summary>
    public long? RemediatedByUserId { get; private set; }
    /// <summary>
    /// Legacy remediation: when the ticket was superseded. Null except on
    /// <see cref="RefundReplacementStatus.Superseded"/> tickets.
    /// </summary>
    public DateTime? RemediatedAt { get; private set; }
    /// <summary>
    /// Legacy remediation: the required nonblank business reason recorded
    /// when the ticket was superseded.
    /// </summary>
    public string? RemediationReason { get; private set; }
    /// <summary>
    /// Legacy remediation: on the replacement Refund ticket, the legacy
    /// ticket it supersedes. Null on every other ticket. Backed by a unique
    /// filtered index so retries cannot create a second Refund ticket for
    /// one superseded request.
    /// </summary>
    public long? SupersedesRequestId { get; private set; }
    public RefundReplacementRequest? SupersedesRequest { get; private set; }
    /// <summary>
    /// Legacy remediation: refund tickets created by superseding this ticket.
    /// At most one row exists, enforced by the unique filtered index.
    /// </summary>
    public ICollection<RefundReplacementRequest> RemediationRefunds { get; private set; } = [];
    public long? CompletedByUserId { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public string? CompletionRemarks { get; private set; }

    /// <summary>
    /// <summary>
    /// Confirmed no-service failure: branch management recorded a past-due
    /// subscription occurrence as missed before any operational delivery
    /// existed, so no visit was ever attempted and no service was provided.
    /// The deduction snapshot is frozen at zero for these tickets and no
    /// ordinary customer-fault deduction applies. Immutable once recorded.
    /// </summary>
    public bool NoServiceProvided { get; private set; }

    /// Stage 2 conditional evidence: a ticket is exempt from the proof image
    /// requirement when it was opened from a rejected doorstep milk test (the
    /// test itself is the evidence), when the authoritative linked delivery
    /// confirms a failed delivery with no product received (a Failed delivery
    /// with a failure timestamp and no completion — a photograph of a product
    /// the customer never received must not be required), or when management
    /// confirmed that no service was provided at all (the unattempted visit
    /// was never operationalized, so there is no doorstep or package to
    /// photograph — the manager attestation plus audit trail is the
    /// evidence). Every other ticket — notably delivered-product quality,
    /// contamination, or visible-damage claims — requires a photo. The
    /// exemption derives only from server-persisted facts (source, order
    /// link, delivery status, the frozen no-service distinction); client
    /// input can never influence it. Attempted subscription-delivery failures
    /// keep their staff-captured proof path unchanged. A null (not yet
    /// loaded) delivery fails safe to required.
    /// </summary>
    public bool ProofImageRequired =>
        Source != RefundReplacementSource.MilkTestRejected && !IsFailedDeliveryWithoutProduct && !NoServiceProvided;

    private bool IsFailedDeliveryWithoutProduct =>
        OrderId.HasValue
        && SubscriptionDeliveryId is null
        && Delivery is { Status: DeliveryStatus.Failed, FailedAt: not null, CompletedAt: null };

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

    /// <summary>
    /// Stage 1/3: records support's explicit apply/waive deduction decision on
    /// an order-backed or subscription-delivery Refund ticket. Must be called
    /// while Pending (order flow) or while the customer's Refund choice is
    /// recorded (subscription delivery-fail flow), before <see cref="Approve"/>.
    /// Applying snapshots the given percentage; waiving snapshots zero and
    /// requires a nonblank reason. Never inferred from delivery failure state.
    /// </summary>
    public void RecordDeductionDecision(bool applyDeduction, decimal percentage, string? waiverReason)
    {
        if (!OrderId.HasValue && !SubscriptionDeliveryId.HasValue)
        {
            throw new InvalidOperationException("A deduction decision applies only to order-backed or subscription-delivery tickets.");
        }
        if (Type != RefundReplacementType.Refund)
        {
            throw new InvalidOperationException("A deduction decision applies only to refund tickets.");
        }
        if (Status != RefundReplacementStatus.Pending
            && Status != RefundReplacementStatus.AwaitingCustomerChoice)
        {
            throw new InvalidOperationException("The deduction decision can only be recorded on a pending request or one with the customer's choice recorded.");
        }
        if (AppliedDeductionPercentage.HasValue)
        {
            throw new InvalidOperationException("The deduction decision has already been recorded.");
        }
        if (applyDeduction)
        {
            if (percentage < 0 || percentage > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(percentage));
            }
            AppliedDeductionPercentage = decimal.Round(percentage, 2, MidpointRounding.AwayFromZero);
            DeductionWaived = false;
            DeductionWaiverReason = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(waiverReason))
            {
                throw new ArgumentException("A waiver reason is required when waiving the deduction.", nameof(waiverReason));
            }
            if (waiverReason.Trim().Length > 1000)
            {
                throw new ArgumentException("The waiver reason cannot exceed 1000 characters.", nameof(waiverReason));
            }
            AppliedDeductionPercentage = 0m;
            DeductionWaived = true;
            DeductionWaiverReason = waiverReason.Trim();
        }
    }

    /// <summary>
    /// Stage 2: designates the established wallet settlement for a Refund
    /// ticket. Exactly-once: requires a Refund type, a pre-decision status
    /// and no prior designation. Called inside the approval transaction;
    /// legacy tickets (never designated) keep the same behavior through
    /// the <c>Unspecified</c> default.
    /// </summary>
    public void DesignateWalletSettlement()
    {
        EnsureDesignatable();
        SettlementMethod = RefundSettlementMethod.Wallet;
    }

    /// <summary>
    /// Stage 2: designates manual bank transfer for a Refund ticket and
    /// freezes the beneficiary snapshot plus the authoritative reserved
    /// amount/currency atomically. Exactly-once with the same guards as
    /// <see cref="DesignateWalletSettlement"/>. The caller reserves the
    /// amount against the covering payments in the same transaction; this
    /// method only freezes the ticket-side record. The protected account
    /// number must already be encrypted with the dedicated
    /// refund-beneficiary Data Protection purpose.
    /// </summary>
    public void DesignateManualBankSettlement(
        string accountHolderName,
        string bankName,
        string ifsc,
        string accountLast4,
        string protectedAccountNumber,
        decimal reservedAmount,
        string reservedCurrency)
    {
        EnsureDesignatable();
        ManualBankHolderNameSnapshot = Required(accountHolderName, nameof(accountHolderName));
        ManualBankNameSnapshot = Required(bankName, nameof(bankName));
        ManualBankIfscSnapshot = Required(ifsc, nameof(ifsc));
        ManualBankAccountLast4Snapshot = Required(accountLast4, nameof(accountLast4));
        ManualBankProtectedAccountNumber = Required(protectedAccountNumber, nameof(protectedAccountNumber));
        if (reservedAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedAmount));
        }
        ManualBankReservedAmount = decimal.Round(reservedAmount, 2, MidpointRounding.AwayFromZero);
        if (ManualBankReservedAmount != reservedAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedAmount));
        }
        ManualBankReservedCurrency = Required(reservedCurrency, nameof(reservedCurrency)).ToUpperInvariant();
        SettlementMethod = RefundSettlementMethod.ManualBankTransfer;
    }

    /// <summary>
    /// Stage 2: explicitly voids a ManualBankTransfer reservation before any
    /// transfer reached the bank. Allowed only from an Approved manual-bank
    /// ticket; the ticket moves to terminal <c>Rejected</c> while the
    /// original approval identity is preserved (the database lifecycle check
    /// for <c>Rejected</c> is satisfied: decided columns set, completed
    /// columns null). The caller fails the pending reservation refund rows
    /// and restores the payments in the same transaction. Never call while
    /// transfer outcome is uncertain or after settlement.
    /// </summary>
    public void VoidManualBankTransfer(long voidedByUserId, DateTime voidedAt, string voidReason)
    {
        if (voidedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(voidedByUserId));
        EnsureIndiaLocal(voidedAt, nameof(voidedAt));
        if (string.IsNullOrWhiteSpace(voidReason))
        {
            throw new ArgumentException("A void reason is required to release a manual bank transfer.", nameof(voidReason));
        }
        if (voidReason.Trim().Length > 1000)
        {
            throw new ArgumentException("The void reason cannot exceed 1000 characters.", nameof(voidReason));
        }
        if (Status != RefundReplacementStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved manual bank transfer can be voided.");
        }
        if (SettlementMethod != RefundSettlementMethod.ManualBankTransfer)
        {
            throw new InvalidOperationException("Only a manual bank transfer can be voided.");
        }
        if (ManualBankVoidedByUserId.HasValue)
        {
            throw new InvalidOperationException("The manual bank transfer has already been voided.");
        }

        ManualBankVoidedByUserId = voidedByUserId;
        ManualBankVoidedAt = voidedAt;
        ManualBankVoidReason = voidReason.Trim();
        Status = RefundReplacementStatus.Rejected;
    }

    private void EnsureDesignatable()
    {
        if (Type != RefundReplacementType.Refund)
        {
            throw new InvalidOperationException("A settlement method applies only to refund tickets.");
        }
        if (Status != RefundReplacementStatus.Pending
            && Status != RefundReplacementStatus.AwaitingCustomerChoice)
        {
            throw new InvalidOperationException("The settlement method can only be designated on a pending request or one with the customer's choice recorded.");
        }
        if (SettlementMethod != RefundSettlementMethod.Unspecified)
        {
            throw new InvalidOperationException("The settlement method has already been designated.");
        }
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
    /// Legacy remediation: supersedes an approved, order-backed Replacement
    /// ticket for a one-time order, which has no fulfillment workflow, so a
    /// supported Refund ticket can be raised for the same delivery. Allowed
    /// only from <see cref="RefundReplacementStatus.Approved"/> on an
    /// order-backed Replacement ticket. The original <c>Type</c>,
    /// <c>Source</c>, approval identity/timestamp and evidence are preserved;
    /// <c>CompletedBy*</c> stay null so no fulfillment or refund is implied.
    /// Requires a nonblank remediation reason plus the remediating actor and
    /// timestamp.
    /// </summary>
    public void MarkSuperseded(
        long remediatedByUserId,
        DateTime remediatedAt,
        string remediationReason)
    {
        if (remediatedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(remediatedByUserId));
        EnsureIndiaLocal(remediatedAt, nameof(remediatedAt));
        if (string.IsNullOrWhiteSpace(remediationReason))
        {
            throw new ArgumentException("A remediation reason is required to supersede a ticket.", nameof(remediationReason));
        }
        if (remediationReason.Trim().Length > 1000)
        {
            throw new ArgumentException("The remediation reason cannot exceed 1000 characters.", nameof(remediationReason));
        }
        if (Status != RefundReplacementStatus.Approved)
        {
            throw new InvalidOperationException("Only an approved request can be superseded.");
        }
        if (!OrderId.HasValue || SubscriptionDeliveryId.HasValue)
        {
            throw new InvalidOperationException("Only an order-backed request can be superseded.");
        }
        if (Type != RefundReplacementType.Replacement)
        {
            throw new InvalidOperationException("Only a replacement request can be superseded.");
        }
        if (RemediatedByUserId.HasValue)
        {
            throw new InvalidOperationException("The request has already been superseded.");
        }

        RemediatedByUserId = remediatedByUserId;
        RemediatedAt = remediatedAt;
        RemediationReason = remediationReason.Trim();
        Status = RefundReplacementStatus.Superseded;
    }

    /// <summary>
    /// Legacy remediation: links a replacement Refund ticket to the legacy
    /// ticket it supersedes. Allowed only on a pending, order-backed Refund
    /// ticket that is not already linked.
    /// </summary>
    public void RecordRemediationLink(long supersedesRequestId)
    {
        if (supersedesRequestId <= 0) throw new ArgumentOutOfRangeException(nameof(supersedesRequestId));
        if (!OrderId.HasValue)
        {
            throw new InvalidOperationException("A remediation link applies only to order-backed tickets.");
        }
        if (Type != RefundReplacementType.Refund)
        {
            throw new InvalidOperationException("A remediation link applies only to refund tickets.");
        }
        if (Status != RefundReplacementStatus.Pending)
        {
            throw new InvalidOperationException("The remediation link can only be recorded on a pending request.");
        }
        if (SupersedesRequestId.HasValue)
        {
            throw new InvalidOperationException("The remediation link has already been recorded.");
        }

        SupersedesRequestId = supersedesRequestId;
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
