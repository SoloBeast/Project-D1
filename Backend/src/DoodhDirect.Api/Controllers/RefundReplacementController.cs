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

    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.RefundReplacementRequestOwn)]
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
    private const long MaximumTransportUploadSize = 10L * 1024L * 1024L;

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
        [FromBody] DecideRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementRequestResult>.Ok(
            await refundReplacementService.ApproveAsync(
                RequireRefundReplacementActor(), requestId, request, cancellationToken)));

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
