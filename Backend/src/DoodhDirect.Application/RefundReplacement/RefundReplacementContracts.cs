using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.RefundReplacement;

namespace DoodhDirect.Application.RefundReplacement;

/// <summary>
/// SystemConfiguration keys backing the refund/replacement request policy.
/// </summary>
public static class RefundReplacementConfigurationKeys
{
    public const string Prefix = "RefundReplacement.";
    public const string WindowHours = Prefix + "WindowHours";
}

/// <summary>
/// Refund/replacement request window configuration contract backed by
/// SystemConfiguration storage. The window is expressed in whole hours and is
/// server-authoritative: the deadline for a normal request is computed by the
/// backend as Delivery.CompletedAt + WindowHours (India-local wall clock).
/// </summary>
public sealed record RefundReplacementConfigurationResult(
    int WindowHours,
    string Status);

public sealed record UpdateRefundReplacementConfigurationRequest(
    int? WindowHours);

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
    bool IsMilkTestRejectedFlow);

public sealed record RefundReplacementImageResult(
    Guid ImageId,
    string FileName,
    string ContentType,
    long FileSize,
    DateTime UploadedAt);

/// <summary>
/// Full detail for one refund/replacement request (customer "my requests" and
/// staff detail views share this shape).
/// </summary>
public sealed record RefundReplacementRequestResult(
    Guid RequestId,
    string RequestNumber,
    long OrderId,
    Guid OrderPublicId,
    string OrderNumber,
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
    IReadOnlyCollection<RefundReplacementImageResult> Images);

/// <summary>
/// Row for the branch-scoped staff list. Images are summarized by count so the
/// list stays light while the detail view streams the actual files.
/// </summary>
public sealed record RefundReplacementListItem(
    Guid RequestId,
    string RequestNumber,
    long OrderId,
    Guid OrderPublicId,
    string OrderNumber,
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
    string Reason,
    string? Remarks,
    DateTime SubmittedAt,
    DateTime? Deadline,
    bool ProofImageRequired,
    int ImageCount);

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

public sealed record DecideRefundReplacementRequest(string? Remarks);

public interface IRefundReplacementService
{
    /// <summary>
    /// Server-authoritative eligibility for a delivery. Enforces ownership, the
    /// delivered/completed state, the milk-test-rejection special flow, the
    /// configured post-delivery window, and the one-active-request rule.
    /// </summary>
    Task<RefundReplacementEligibilityResult> GetEligibilityAsync(
        RefundReplacementActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Submits a new request. Re-validates everything inside a serializable
    /// transaction (ownership, delivery state, window/deadline, milk-test linkage,
    /// duplicate active request) and allocates a unique request number. Never
    /// trusts client OrderId, timestamps or device time.
    /// </summary>
    Task<RefundReplacementRequestResult> SubmitAsync(
        RefundReplacementActor actor,
        SubmitRefundReplacementRequest request,
        CancellationToken cancellationToken);

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

    Task<RefundReplacementRequestResult> ApproveAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
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
