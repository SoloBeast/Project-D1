using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Api.Authorization;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.RefundReplacement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

public abstract class RefundReplacementControllerBase : ControllerBase
{
    protected RefundReplacementActor RequireRefundReplacementActor()
    {
        var userIdValue = User.FindFirstValue("user_id");
        if (!long.TryParse(userIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            throw new UnauthorizedAppException();
        }

        var branchIds = User.FindAll(AuthorizationCodes.BranchClaim)
            .Select(claim => long.TryParse(
                claim.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var branchId)
                ? branchId
                : (long?)null)
            .Where(branchId => branchId.HasValue)
            .Select(branchId => branchId!.Value)
            .Distinct()
            .ToHashSet();

        var hasGlobalAccess = User.HasClaim(
            AuthorizationCodes.PermissionClaim,
            AuthorizationCodes.GlobalAccess);

        return new RefundReplacementActor(userId, branchIds, hasGlobalAccess);
    }
}

/// <summary>
/// Customer-facing refund/replacement endpoints. Eligibility and submission are
/// bound to the delivery the authenticated customer actually owns; the backend
/// enforces the configured post-delivery window and never trusts client
/// timestamps, order ids or the device clock.
/// </summary>
[ApiController]
[Route("api/v1/deliveries/{deliveryId:guid}/refund-replacement")]
[Tags("Customer refund/replacement requests")]
[Produces("application/json")]
public sealed class CustomerRefundReplacementController(
    IRefundReplacementService refundReplacementService) : RefundReplacementControllerBase
{
    [HttpGet("eligibility")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementRequestOwn)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementEligibilityResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementEligibilityResult>>> GetEligibility(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementEligibilityResult>.Ok(
            await refundReplacementService.GetEligibilityAsync(
                RequireRefundReplacementActor(), deliveryId, cancellationToken)));

    /// <summary>
    /// Stage 2.1: JSON submission carries no evidence, so the service rejects
    /// it whenever the authoritative evidence rule requires a photo. Photo
    /// tickets must use the multipart submission below, which binds validated
    /// evidence atomically with the ticket.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementRequestOwn)]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> Submit(
        Guid deliveryId,
        [FromBody] SubmitRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.SubmitAsync(
                RequireRefundReplacementActor(),
                request with { DeliveryId = deliveryId },
                cancellationToken)));

    /// <summary>
    /// Stage 2.1: multipart submission binding optional proof evidence to the
    /// ticket in a single atomic transaction. Field values are advisory only:
    /// ownership, delivery state, source and the evidence requirement are all
    /// re-derived server-side, and the file is validated exactly like a
    /// post-creation upload.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementRequestOwn)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(RefundReplacementController.MaximumTransportUploadSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = RefundReplacementController.MaximumTransportUploadSize)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> SubmitMultipart(
        Guid deliveryId,
        [FromForm] SubmitRefundReplacementForm request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RefundReplacementType>(request.Type, ignoreCase: true, out var type))
        {
            throw new ValidationAppException("The request type is invalid.", "type");
        }

        Guid? milkTestId = null;
        if (!string.IsNullOrWhiteSpace(request.MilkTestId))
        {
            if (!Guid.TryParse(request.MilkTestId, out var parsedMilkTestId))
            {
                throw new ValidationAppException("The doorstep test reference is invalid.", "milkTestId");
            }

            milkTestId = parsedMilkTestId;
        }

        SubmitRefundReplacementImage? image = null;
        if (request.Image is not null)
        {
            await using var content = request.Image.OpenReadStream();
            image = new SubmitRefundReplacementImage(
                content,
                request.Image.FileName,
                request.Image.ContentType,
                request.Image.Length);
            var result = await refundReplacementService.SubmitAsync(
                RequireRefundReplacementActor(),
                new SubmitRefundReplacementRequest(deliveryId, type, request.Reason ?? string.Empty, request.Remarks, milkTestId),
                cancellationToken,
                image);
            return Ok(ApiResponse<RefundReplacementRequestResult>.Ok(result));
        }

        return Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.SubmitAsync(
                RequireRefundReplacementActor(),
                new SubmitRefundReplacementRequest(deliveryId, type, request.Reason ?? string.Empty, request.Remarks, milkTestId),
                cancellationToken)));
    }
}

/// <summary>
/// Customer "my requests" list/detail. Ownership is enforced inside the service:
/// another customer's request is indistinguishable from a missing one.
/// </summary>
[ApiController]
[Route("api/v1/customer/refund-replacements")]
[Tags("Customer refund/replacement requests")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementReadOwn)]
public sealed class CustomerRefundReplacementListController(
    IRefundReplacementService refundReplacementService) : RefundReplacementControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RefundReplacementRequestResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RefundReplacementRequestResult>>>> List(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<RefundReplacementRequestResult>>.Ok(
            await refundReplacementService.ListForCustomerAsync(
                RequireRefundReplacementActor(), cancellationToken)));

    [HttpGet("{requestId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult?>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult?>>> Get(
        Guid requestId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult?>.Ok(
            await refundReplacementService.GetForCustomerAsync(
                RequireRefundReplacementActor(), requestId, cancellationToken)));

    /// <summary>
    /// D1-B: records the owning customer's explicit Refund / Re-Delivery choice
    /// on a delivery-fail ticket awaiting choice. Same-choice retries are
    /// idempotent; anything else after resolution, or after the persisted
    /// choice deadline, is rejected. Records no money movement and creates no
    /// replacement delivery — the ticket waits for normal staff approval.
    /// </summary>
    [HttpPost("{requestId:guid}/choose")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementRequestOwn)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> Choose(
        Guid requestId,
        [FromBody] ChooseRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.ChooseAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));
}

/// <summary>
/// Branch-scoped staff (Support / Dairy Manager) list, detail, decision and
/// completion endpoints. Read endpoints accept either the read or manage
/// permission; decision endpoints require the manage permission. Branch scoping
/// is enforced inside the service via the actor's branch claims.
/// </summary>
[ApiController]
[Route("api/v1/refund-replacements")]
[Tags("Refund/replacement requests")]
[Produces("application/json")]
public sealed class RefundReplacementController(
    IRefundReplacementService refundReplacementService) : RefundReplacementControllerBase
{
    internal const long MaximumTransportUploadSize = 10L * 1024L * 1024L;

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicyNames.AnyRefundReplacementRequestView)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementPageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementPageResult>>> List(
        CancellationToken cancellationToken,
        [FromQuery] long? branchId = null,
        [FromQuery] RefundReplacementStatus? status = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20) =>
        Ok(ApiResponse<RefundReplacementPageResult>.Ok(
            await refundReplacementService.ListForStaffAsync(
                RequireRefundReplacementActor(),
                new RefundReplacementListQuery(branchId, status, page, pageSize),
                cancellationToken)));

    [HttpGet("{requestId:guid}")]
    [Authorize(Policy = AuthorizationPolicyNames.AnyRefundReplacementRequestView)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> Get(
        Guid requestId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.GetForStaffAsync(
                RequireRefundReplacementActor(), requestId, cancellationToken)));

    [HttpPost("{requestId:guid}/approve")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> Approve(
        Guid requestId,
        [FromBody] ApproveRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.ApproveAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    [HttpGet("{requestId:guid}/replacement-date-proposal")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<ReplacementDateProposalResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ReplacementDateProposalResult>>> ProposeReplacementDate(
        Guid requestId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ReplacementDateProposalResult>.Ok(
            await refundReplacementService.ProposeReplacementDateAsync(
                RequireRefundReplacementActor(), requestId, cancellationToken)));

    [HttpPost("{requestId:guid}/reject")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> Reject(
        Guid requestId,
        [FromBody] DecideRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.RejectAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    /// <summary>
    /// Legacy remediation: atomically supersedes an approved, order-backed
    /// Replacement ticket (terminal <c>Superseded</c> status) and creates
    /// exactly one linked Pending Refund ticket for the same delivery.
    /// Issues no wallet credit and creates no financial refund record; the
    /// new ticket follows the standard approval and settlement workflow.
    /// Idempotent: retrying returns the existing pair.
    /// </summary>
    [HttpPost("{requestId:guid}/remediate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<RemediateLegacyReplacementResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RemediateLegacyReplacementResult>>> Remediate(
        Guid requestId,
        [FromBody] RemediateLegacyReplacementRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RemediateLegacyReplacementResult>.Ok(
            await refundReplacementService.RemediateLegacyReplacementAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    /// <summary>
    /// Stage 1: read-only preview of an order-backed Refund ticket's proposed
    /// wallet settlement. The <c>applyDeduction</c> query flag selects the
    /// percentage shown (shared configuration or zero). Never mutates state.
    /// </summary>
    [HttpGet("{requestId:guid}/order-refund-preview")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<OrderRefundPreviewResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OrderRefundPreviewResult>>> PreviewOrderRefund(
        Guid requestId,
        [FromQuery] bool applyDeduction,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<OrderRefundPreviewResult>.Ok(
            await refundReplacementService.PreviewOrderRefundAsync(
                RequireRefundReplacementActor(), requestId, applyDeduction, cancellationToken)));

    [HttpPost("{requestId:guid}/complete")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> Complete(
        Guid requestId,
        [FromBody] DecideRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.CompleteAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    /// <summary>
    /// Stage 2: confirms that a manual bank transfer was actually made.
    /// Records the bank transfer reference (UTR) and settles the reserved
    /// payment allocations exactly once. Replaying a confirmation for an
    /// already-completed ticket returns the confirmed result without
    /// duplicate settlement.
    /// </summary>
    [HttpPost("{requestId:guid}/confirm-manual-bank-transfer")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> ConfirmManualBankTransfer(
        Guid requestId,
        [FromBody] ConfirmManualBankTransferRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.ConfirmManualBankTransferAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    /// <summary>
    /// Stage 2: explicitly releases a manual-bank reservation before any
    /// transfer reached the bank (e.g. wrong beneficiary details). Requires
    /// a nonblank reason and is audited. The ticket moves to terminal
    /// Rejected; funds are never released automatically.
    /// </summary>
    [HttpPost("{requestId:guid}/release-manual-bank-transfer")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementManageBranch)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementRequestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementRequestResult>>> ReleaseManualBankTransfer(
        Guid requestId,
        [FromBody] ReleaseManualBankTransferRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.ReleaseManualBankTransferAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

    [HttpPost("{requestId:guid}/images")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementRequestOwn)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumTransportUploadSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumTransportUploadSize)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementImageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementImageResult>>> UploadImage(
        Guid requestId,
        [FromForm] RefundReplacementImageUploadForm request,
        CancellationToken cancellationToken)
    {
        if (request.Image is null)
        {
            throw new ValidationAppException("An image is required.", "image");
        }

        await using var content = request.Image.OpenReadStream();
        var result = await refundReplacementService.UploadImageAsync(
            RequireRefundReplacementActor(),
            requestId,
            content,
            request.Image.FileName,
            request.Image.ContentType,
            request.Image.Length,
            cancellationToken);
        return Ok(ApiResponse<RefundReplacementImageResult>.Ok(result));
    }

    [HttpGet("{requestId:guid}/images/{imageId:guid}/content")]
    [Authorize(Policy = AuthorizationPolicyNames.AnyRefundReplacementRequestContent)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenImage(
        Guid requestId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var media = await refundReplacementService.OpenImageAsync(
            RequireRefundReplacementActor(), requestId, imageId, cancellationToken);
        Response.ContentLength = media.FileSize;
        return File(media.Content, media.ContentType, enableRangeProcessing: true);
    }
}

public sealed class RefundReplacementImageUploadForm
{
    public IFormFile? Image { get; init; }
}

public sealed class SubmitRefundReplacementForm
{
    public string? Type { get; init; }
    public string? Reason { get; init; }
    public string? Remarks { get; init; }
    public string? MilkTestId { get; init; }
    public IFormFile? Image { get; init; }
}
