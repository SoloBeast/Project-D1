using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.RefundReplacement;

namespace DoodhDirect.Application.RefundReplacement;

/// <summary>
/// SystemConfiguration keys backing the refund/replacement request policy.
/// </summary>
public static class RefundReplacementConfigurationKeys
{
    public const string Prefix = "RefundReplacement.";
    public const string WindowHours = Prefix + "WindowHours";
    /// <summary>
    /// D1-A: hours after the delivery-failed timestamp during which the
    /// customer must choose Refund or Re-Delivery. Separate from
    /// <see cref="WindowHours"/>; snapshotted onto each delivery-fail ticket.
    /// </summary>
    public const string DeliveryFailWindowHours = Prefix + "DeliveryFailWindowHours";
    /// <summary>
    /// D1-A: deduction % applied only by the later automatic-timeout refund.
    /// Stored/configured now, snapshotted onto each delivery-fail ticket,
    /// never calculated in this stage.
    /// </summary>
    public const string DeliveryFailDeductionPercentage = Prefix + "DeliveryFailDeductionPercentage";
}

/// <summary>
/// Refund/replacement request window configuration contract backed by
/// SystemConfiguration storage. The window is expressed in whole hours and is
/// server-authoritative: the deadline for a normal request is computed by the
/// backend as Delivery.CompletedAt + WindowHours (India-local wall clock).
/// </summary>
public sealed record RefundReplacementConfigurationResult(
    int WindowHours,
    int DeliveryFailWindowHours,
    decimal DeliveryFailDeductionPercentage,
    string Status);

public sealed record UpdateRefundReplacementConfigurationRequest(
    int? WindowHours,
    int? DeliveryFailWindowHours = null,
    decimal? DeliveryFailDeductionPercentage = null);

public interface IRefundReplacementConfigurationService
{
    Task<RefundReplacementConfigurationResult> GetAsync(CancellationToken cancellationToken);

    Task<RefundReplacementConfigurationResult> UpdateAsync(
        UpdateRefundReplacementConfigurationRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
}

/// <summary>
/// Authenticated actor for refund/replacement operations. <see cref="BranchIds"/>
/// are the branches the actor has been assigned to (from the <c>branch_id</c>
/// claims); <see cref="HasGlobalAccess"/> is true for Owner/System Admin style
/// roles that can act across every branch.
/// </summary>
public sealed record RefundReplacementActor(
    long UserId,
    IReadOnlySet<long> BranchIds,
    bool HasGlobalAccess);

/// <summary>
/// Customer submission for a refund or replacement request. The client never
/// declares the source or any timestamp — the backend derives the source from
/// <see cref="MilkTestId"/> (when a milk-test rejection is being referenced) and
/// computes the deadline from the authoritative delivery completion timestamp and
/// the configured request window.
/// </summary>
public sealed record SubmitRefundReplacementRequest(
    Guid DeliveryId,
    RefundReplacementType Type,
    string Reason,
    string? Remarks,
    Guid? MilkTestId = null);

/// <summary>
/// Stage 2.1: proof-image bytes accompanying a ticket submission. The image is
/// validated and stored atomically with the ticket inside the submission
/// transaction — the same pattern as the delivery-fail ticket proof — so a
/// ticket that requires evidence can never exist without it. Ownership comes
/// from the authenticated actor, never from this payload.
/// </summary>
public sealed record SubmitRefundReplacementImage(
    Stream Content,
    string FileName,
    string ContentType,
    long FileSize);

/// <summary>
/// Server-authoritative eligibility answer for the customer UI. The UI must never
/// compute the window itself; it renders this result and disables/hides entry when
/// <see cref="IsEligible"/> is false.
/// </summary>
public sealed record RefundReplacementEligibilityResult(
    bool IsEligible,
    string? IneligibleReason,
    int WindowHours,
    DateTime? Deadline,
    bool HasActiveRequest,
    bool ProofImageRequired,
    bool IsMilkTestRejectedFlow,
    /// <summary>
    /// Stage 2: true when the authoritative delivery row confirms failure with
    /// no product received, so the UI can explain why no photo is required.
    /// Server-derived; never accepted from client input.
    /// </summary>
    bool IsFailedDeliveryWithoutProduct = false);

public sealed record RefundReplacementImageResult(
    Guid ImageId,
    string FileName,
    string ContentType,
    long FileSize,
    DateTime UploadedAt);

/// <summary>
/// Full detail for one refund/replacement request (customer "my requests" and
/// staff detail views share this shape). D1-D slice 6.1: for an approved
/// re-delivery ticket the replacement fields are populated from the persisted
/// replacement occurrence on every read, not only on the approval response.
/// <c>ReplacementDeliveryNumber</c> is the operational delivery's public
/// DELIVERY-series number and appears only once the replacement has
/// materialized through the normal pipeline; it is never invented.
/// </summary>
public sealed record RefundReplacementRequestResult(
    Guid RequestId,
    string RequestNumber,
    long? OrderId,
    Guid? OrderPublicId,
    string? OrderNumber,
    Guid? SubscriptionDeliveryPublicId,
    DateTime? FailedAt,
    int? DeliveryFailWindowHoursSnapshot,
    decimal? DeliveryFailDeductionPercentageSnapshot,
    string? FailureReasonSnapshot,
    Guid DeliveryId,
    string? DeliveryNumber,
    long CustomerId,
    string CustomerName,
    string CustomerMobile,
    long BranchId,
    string BranchCode,
    string BranchName,
    long? MilkTestId,
    Guid? MilkTestPublicId,
    RefundReplacementType Type,
    RefundReplacementSource Source,
    RefundReplacementStatus Status,
    CustomerChoice? CustomerChoice,
    DateTime? CustomerChoiceAt,
    string Reason,
    string? Remarks,
    DateTime SubmittedAt,
    DateTime? Deadline,
    long? DecidedByUserId,
    string? DecidedByName,
    DateTime? DecidedAt,
    string? DecisionRemarks,
    long? CompletedByUserId,
    string? CompletedByName,
    DateTime? CompletedAt,
    string? CompletionRemarks,
    bool ProofImageRequired,
    IReadOnlyCollection<RefundReplacementImageResult> Images,
    Guid? ReplacementDeliveryPublicId = null,
    DateOnly? ReplacementDate = null,
    string? ReplacementDeliveryNumber = null,
    /// <summary>
    /// Audit remediation: true when this ticket's failed occurrence is itself
    /// a re-delivery replacement, so Refund is the only available remedy and
    /// rejection is refused. Computed from the persisted occurrence link.
    /// </summary>
    bool IsRefundOnly = false,
    /// <summary>
    /// Stage 1/3: support's frozen applied deduction decision for an
    /// order-backed Refund ticket (shared config value or zero when waived)
    /// or a subscription failed-delivery Refund ticket (failure snapshot or
    /// zero when waived). Null until decided and for tickets with no
    /// deduction decision.
    /// </summary>
    decimal? AppliedDeductionPercentage = null,
    /// <summary>
    /// Stage 1/3: true when support waived the customer-fault deduction.
    /// </summary>
    bool DeductionWaived = false,
    /// <summary>
    /// Stage 1/3: required reason when the deduction was waived.
    /// </summary>
    string? DeductionWaiverReason = null,
    /// <summary>
    /// Legacy remediation: on a replacement Refund ticket, the request number
    /// of the superseded legacy ticket. Null on every other ticket.
    /// </summary>
    string? SupersedesRequestNumber = null,
    /// <summary>
    /// Legacy remediation: public id of the superseded legacy ticket.
    /// </summary>
    Guid? SupersedesRequestPublicId = null,
    /// <summary>
    /// Legacy remediation: on a superseded legacy ticket, the request number
    /// of the replacement Refund ticket raised for the same delivery.
    /// </summary>
    string? SupersededByRequestNumber = null,
    /// <summary>
    /// Stage 2.1: the linked delivery's current status, projected from the
    /// authoritative persisted Delivery record (never inferred). Staff UI
    /// gating only — execution revalidates inside its own transaction.
    /// </summary>
    DeliveryStatus DeliveryStatus = DeliveryStatus.ReadyForAssignment,
    /// <summary>
    /// Stage 2: explicit settlement method. Unspecified for legacy tickets
    /// (established wallet behavior) and non-refund shapes.
    /// </summary>
    RefundSettlementMethod SettlementMethod = RefundSettlementMethod.Unspecified,
    /// <summary>
    /// Stage 2: frozen manual-bank transfer amount/currency. Null except on
    /// ManualBankTransfer tickets.
    /// </summary>
    decimal? ReservedAmount = null,
    string? ReservedCurrency = null,
    /// <summary>
    /// Stage 2: masked beneficiary snapshot (never the full account number).
    /// </summary>
    string? BeneficiaryHolderName = null,
    string? BeneficiaryBankName = null,
    string? BeneficiaryIfsc = null,
    string? BeneficiaryAccountLast4 = null,
    /// <summary>
    /// Server-derived origin indicator: true when the authoritative owning
    /// account is a non-customer (employee/owner/administrator) account.
    /// Never accepted from client input; staff UI gating only — execution
    /// revalidates inside the service guard.
    /// </summary>
    bool IsEmployeeOriginated = false);

/// <summary>
/// Row for the branch-scoped staff list. Images are summarized by count so the
/// list stays light while the detail view streams the actual files.
/// </summary>
public sealed record RefundReplacementListItem(
    Guid RequestId,
    string RequestNumber,
    long? OrderId,
    Guid? OrderPublicId,
    string? OrderNumber,
    Guid? SubscriptionDeliveryPublicId,
    DateTime? FailedAt,
    int? DeliveryFailWindowHoursSnapshot,
    decimal? DeliveryFailDeductionPercentageSnapshot,
    string? FailureReasonSnapshot,
    Guid DeliveryId,
    string? DeliveryNumber,
    long CustomerId,
    string CustomerName,
    string CustomerMobile,
    long BranchId,
    string BranchCode,
    string BranchName,
    long? MilkTestId,
    Guid? MilkTestPublicId,
    RefundReplacementType Type,
    RefundReplacementSource Source,
    RefundReplacementStatus Status,
    CustomerChoice? CustomerChoice,
    DateTime? CustomerChoiceAt,
    string Reason,
    string? Remarks,
    DateTime SubmittedAt,
    DateTime? Deadline,
    bool ProofImageRequired,
    int ImageCount,
    DateOnly? ReplacementDate = null,
    /// <summary>
    /// Audit remediation: true when this ticket's failed occurrence is itself
    /// a re-delivery replacement, so Refund is the only available remedy.
    /// </summary>
    bool IsRefundOnly = false,
    /// <summary>
    /// Stage 1: support's frozen applied deduction percentage for an
    /// order-backed Refund ticket. Null until decided and for non-order tickets.
    /// </summary>
    decimal? AppliedDeductionPercentage = null,
    /// <summary>
    /// Stage 1: true when support waived the customer-fault deduction.
    /// </summary>
    bool DeductionWaived = false,
    /// <summary>
    /// Legacy remediation: on a replacement Refund ticket, the request number
    /// of the superseded legacy ticket. Null on every other ticket.
    /// </summary>
    string? SupersedesRequestNumber = null,
    /// <summary>
    /// Stage 2.1: the linked delivery's current status, projected from the
    /// authoritative persisted Delivery record (never inferred). Staff UI
    /// gating only — execution revalidates inside its own transaction.
    /// </summary>
    DeliveryStatus DeliveryStatus = DeliveryStatus.ReadyForAssignment,
    /// <summary>
    /// Stage 2: explicit settlement method. Unspecified for legacy tickets
    /// (established wallet behavior) and non-refund shapes.
    /// </summary>
    RefundSettlementMethod SettlementMethod = RefundSettlementMethod.Unspecified,
    /// <summary>
    /// Server-derived origin indicator: true when the authoritative owning
    /// account is a non-customer (employee/owner/administrator) account.
    /// Never accepted from client input; staff UI gating only — execution
    /// revalidates inside the service guard.
    /// </summary>
    bool IsEmployeeOriginated = false);

public sealed record RefundReplacementListQuery(
    long? BranchId = null,
    RefundReplacementStatus? Status = null,
    int Page = 1,
    int PageSize = 20);

public sealed record RefundReplacementPageResult(
    IReadOnlyCollection<RefundReplacementListItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// D1-B: the customer's explicit remedy choice on a delivery-fail ticket
/// awaiting choice. The ticket is identified by the existing public request
/// reference; the choice value binds from "Refund" / "ReDelivery".
/// </summary>
public sealed record ChooseRefundReplacementRequest(CustomerChoice Choice);

/// <summary>
/// D1-C: outcome of one automatic timeout resolution. All amounts are in the
/// subscription currency; references are public business numbers only.
/// </summary>
public sealed record DeliveryFailTimeoutResult(
    Guid RequestId,
    string RequestNumber,
    string DeliveryNumber,
    decimal GrossRefund,
    decimal DeductionPercentage,
    decimal DeductionAmount,
    decimal WalletCredit);

/// <summary>
/// D1-C: resolves delivery-fail tickets whose persisted customer-choice
/// deadline passed with no choice. Each ticket is processed in its own
/// authoritative transaction: the choice is set to Refund, the ticket is moved
/// through the existing approve/complete flow, and the frozen refund (less the
/// snapshotted deduction) is credited to the wallet. Already-resolved tickets
/// are skipped without side effects so retries and restarts are safe.
/// </summary>
public interface IDeliveryFailTimeoutProcessor
{
    /// <summary>
    /// Processes expired tickets in deadline order, up to the configured batch
    /// size. Returns the number of tickets resolved. A ticket lost to a
    /// concurrent winner (customer choice or another worker) is skipped, never
    /// retried in the same cycle; unexpected failures are logged by the caller.
    /// </summary>
    Task<int> ProcessExpiredTicketsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Processes one ticket by public id. Returns null when the ticket is not
    /// eligible (missing, already resolved, not yet expired, or financially
    /// unresolvable right now); otherwise resolves it and returns the outcome.
    /// </summary>
    Task<DeliveryFailTimeoutResult?> ProcessTicketAsync(
        Guid requestId,
        CancellationToken cancellationToken);
}

public record DecideRefundReplacementRequest(string? Remarks);

/// <summary>
/// Legacy remediation: staff supersede of an approved, order-backed
/// Replacement ticket for a one-time order. The nonblank business reason is
/// required and recorded on the legacy ticket; the operation atomically
/// creates exactly one linked Pending Refund ticket for the same delivery.
/// No wallet credit or financial refund is issued by this operation.
/// </summary>
public sealed record RemediateLegacyReplacementRequest(string? Reason);

/// <summary>
/// Legacy remediation: the superseded legacy ticket paired with the newly
/// created linked Pending Refund ticket. Idempotent retries return the
/// existing pair instead of creating a duplicate.
/// </summary>
public sealed record RemediateLegacyReplacementResult(
    RefundReplacementRequestResult LegacyTicket,
    RefundReplacementRequestResult RefundTicket);

/// <summary>
/// D1-D: staff approval of a refund/replacement request. For delivery-fail
/// tickets with customer choice ReDelivery, staff may confirm an explicit
/// replacement date; when omitted the server proposes the next eligible date
/// (weekday schedule, cutoff, active branch, no date conflict). The customer
/// never picks the date. The occurrence being replaced is always derived from
/// the ticket's own source reference — never from client input.
///
/// Stage 1/3: for an order-backed Refund ticket, <see cref="ApplyDeduction"/>
/// is required — true applies the shared Admin Centre deduction percentage
/// (snapshotted at approval), false waives it to zero and requires
/// <see cref="WaiverReason"/>. The same decision is required when approving
/// a subscription failed-delivery Refund ticket with the customer's Refund
/// choice recorded — true reuses the percentage frozen when the failure was
/// recorded, false waives it to zero with a required reason. A missing value
/// is rejected explicitly and never defaults. Ignored for every other ticket
/// shape.
/// </summary>
public sealed record ApproveRefundReplacementRequest(
    string? Remarks,
    DateOnly? ReplacementDate = null,
    bool? ApplyDeduction = null,
    string? WaiverReason = null,
    /// <summary>
    /// Stage 2: explicit settlement method for Refund-shaped tickets
    /// (required: <c>Wallet</c> preserves today's completion behavior,
    /// <c>ManualBankTransfer</c> bypasses wallet/Razorpay settlement and
    /// freezes a beneficiary snapshot plus payment reservations instead).
    /// Ignored for every other ticket shape. A missing value is rejected
    /// explicitly for Refund-shaped tickets and never defaults.
    /// </summary>
    RefundSettlementMethod? SettlementMethod = null)
    : DecideRefundReplacementRequest(Remarks);

/// <summary>
/// Stage 2: staff confirmation that a manual bank transfer was actually
/// made. The transfer reference (UTR) is required (6–100 characters, same
/// convention as wallet withdrawals); only after this confirmation does
/// the ticket complete and the reserved payment allocations settle.
/// Generation or download of an export file never confirms payment.
/// </summary>
public sealed record ConfirmManualBankTransferRequest(
    string TransferReference,
    string? Remarks)
    : DecideRefundReplacementRequest(Remarks);

/// <summary>
/// Stage 2: explicit release of a manual-bank reservation before any
/// transfer reached the bank (e.g. wrong beneficiary details). Requires a
/// nonblank reason and is audited. The ticket moves to terminal Rejected;
/// funds are never released automatically and never while transfer outcome
/// is uncertain.
/// </summary>
public sealed record ReleaseManualBankTransferRequest(
    string Reason,
    string? Remarks)
    : DecideRefundReplacementRequest(Remarks);

/// <summary>
/// Stage 1: read-only preview of an order-backed Refund ticket's proposed
/// wallet settlement. Informative only: approval snapshots the applied
/// percentage and settlement revalidates inside its own transaction.
/// </summary>
public sealed record OrderRefundPreviewResult(
    Guid RequestId,
    string RequestNumber,
    Guid OrderPublicId,
    string OrderNumber,
    decimal GrossBasis,
    decimal AppliedPercentage,
    decimal DeductionAmount,
    decimal EligiblePaid,
    decimal ProposedRefund,
    string Currency);

/// <summary>
/// D1-D: the server-proposed next eligible replacement date for a re-delivery
/// ticket. Advisory: approval re-validates (or validates a staff-edited date)
/// inside the authoritative transaction.
/// </summary>
public sealed record ReplacementDateProposalResult(Guid RequestId, DateOnly ProposedDate);

public interface IRefundReplacementService
{
    /// <summary>
    /// Server-authoritative eligibility for a delivery. Enforces ownership, the
    /// delivered/completed state, the milk-test-rejection special flow, the
    /// configured post-delivery window, the one-active-request rule, and the
    /// already-refunded rule for one-time orders.
    /// </summary>
    Task<RefundReplacementEligibilityResult> GetEligibilityAsync(
        RefundReplacementActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Submits a new request. Re-validates everything inside a serializable
    /// transaction (ownership, delivery state, window/deadline, milk-test linkage,
    /// duplicate active request, already-completed one-time-order refund)
    /// and allocates a unique request number. Never
    /// trusts client OrderId, timestamps or device time. Stage 2.1: when the
    /// authoritative evidence rule requires a photo, the submission is rejected
    /// unless <paramref name="image"/> carries valid evidence, which is then
    /// stored and attached atomically with the ticket.
    /// </summary>
    Task<RefundReplacementRequestResult> SubmitAsync(
        RefundReplacementActor actor,
        SubmitRefundReplacementRequest request,
        CancellationToken cancellationToken,
        SubmitRefundReplacementImage? image = null);

    Task<IReadOnlyList<RefundReplacementRequestResult>> ListForCustomerAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken);

    Task<RefundReplacementRequestResult?> GetForCustomerAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken);

    Task<RefundReplacementPageResult> ListForStaffAsync(
        RefundReplacementActor actor,
        RefundReplacementListQuery query,
        CancellationToken cancellationToken);

    Task<RefundReplacementRequestResult> GetForStaffAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// D1-B: records the owning customer's Refund / Re-Delivery choice on a
    /// ticket awaiting choice before its persisted choice deadline. Same-choice
    /// retries return the current state without side effects; a different
    /// choice after resolution is rejected. Notifies branch staff and audits.
    /// </summary>
    Task<RefundReplacementRequestResult> ChooseAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ChooseRefundReplacementRequest request,
        CancellationToken cancellationToken);

    Task<RefundReplacementRequestResult> ApproveAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ApproveRefundReplacementRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// D1-D: proposes the next eligible replacement date for a re-delivery
    /// ticket awaiting choice. Staff confirm or edit this date during approval;
    /// approval re-validates inside its own transaction.
    /// </summary>
    Task<ReplacementDateProposalResult> ProposeReplacementDateAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stage 1: read-only, branch-scoped preview of an order-backed Refund
    /// ticket's proposed wallet settlement. <paramref name="applyDeduction"/>
    /// selects the percentage used for that preview (shared configuration or
    /// zero). Never mutates payments, refunds, wallet, ticket, audit or outbox.
    /// </summary>
    Task<OrderRefundPreviewResult> PreviewOrderRefundAsync(
        RefundReplacementActor actor,
        Guid requestId,
        bool applyDeduction,
        CancellationToken cancellationToken);

    Task<RefundReplacementRequestResult> RejectAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        CancellationToken cancellationToken);

    Task<RefundReplacementRequestResult> CompleteAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stage 2: confirms that a manual bank transfer was actually made for
    /// an Approved ManualBankTransfer ticket. Records the transfer
    /// reference (UTR), settles the reserved payment allocations exactly
    /// once (no wallet credit, no gateway refund) and completes the ticket,
    /// all inside one serializable transaction. Replaying a completed
    /// confirmation returns the current state without moving money again.
    /// Export generation/download never calls this; it is wired to the
    /// transfer-result confirmation in the later export stage.
    /// </summary>
    Task<RefundReplacementRequestResult> ConfirmManualBankTransferAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ConfirmManualBankTransferRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stage 2: explicitly releases a manual-bank reservation before any
    /// transfer reached the bank. Fails the pending reservation refund rows,
    /// restores the covering payments and moves the ticket to terminal
    /// Rejected with void metadata, all audited. Never automatic and never
    /// while transfer outcome is uncertain.
    /// </summary>
    Task<RefundReplacementRequestResult> ReleaseManualBankTransferAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ReleaseManualBankTransferRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Legacy remediation: atomically supersedes an approved, order-backed
    /// Replacement ticket (terminal <c>Superseded</c> status, history
    /// preserved) and creates exactly one linked Pending Refund ticket for
    /// the same order, delivery, customer and branch inside one serializable
    /// transaction. Requires <c>REFUND_REPLACEMENT.MANAGE_BRANCH</c> branch
    /// scope. Aborts without mutation when the legacy state changed, another
    /// active ticket exists, or prior settlement is ambiguous. Issues no
    /// wallet credit and creates no financial refund record; the new ticket
    /// follows the standard apply/waive approval and settlement workflow.
    /// Idempotent: a retry returns the existing pair.
    /// </summary>
    Task<RemediateLegacyReplacementResult> RemediateLegacyReplacementAsync(
        RefundReplacementActor actor,
        Guid requestId,
        RemediateLegacyReplacementRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a proof image for a pending normal request. Rejected because of the
    /// milk-test special flow are exempt (<see cref="RefundReplacementRequest.ProofImageRequired"/>).
    /// The stored file is private; access is granted only to the owning customer or
    /// branch-scoped staff via <see cref="OpenImageAsync"/>.
    /// </summary>
    Task<RefundReplacementImageResult> UploadImageAsync(
        RefundReplacementActor actor,
        Guid requestId,
        Stream content,
        string fileName,
        string contentType,
        long fileSize,
        CancellationToken cancellationToken);

    Task<StoredMediaContent> OpenImageAsync(
        RefundReplacementActor actor,
        Guid requestId,
        Guid imageId,
        CancellationToken cancellationToken);
}
