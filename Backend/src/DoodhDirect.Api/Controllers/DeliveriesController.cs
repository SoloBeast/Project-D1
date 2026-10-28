using System.Globalization;
using DoodhDirect.Application.Abstractions;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// D1-A multipart form for failing a subscription delivery with staff proof.
/// </summary>
public sealed class FailDeliveryWithProofForm
{
    public string Reason { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public IFormFile? Proof { get; set; }
}

public abstract class DeliveryControllerBase : ControllerBase
{
    protected DeliveryActor RequireActor()
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
            .ToArray();

        var hasGlobalAccess = User.HasClaim(
            AuthorizationCodes.PermissionClaim,
            AuthorizationCodes.GlobalAccess);

        return new DeliveryActor(userId, branchIds, hasGlobalAccess);
    }
}

[ApiController]
[Route("api/v1/deliveries")]
[Tags("Customer deliveries")]
[Produces("application/json")]
public sealed class CustomerDeliveriesController(IDeliveryService deliveryService) : DeliveryControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CustomerDeliveryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<CustomerDeliveryResult>>>> GetMine(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<CustomerDeliveryResult>>.Ok(
            await deliveryService.GetForCustomerAsync(RequireActor().UserId, cancellationToken)));

    [HttpGet("{deliveryId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerDeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerDeliveryResult>>> Get(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerDeliveryResult>.Ok(
            await deliveryService.GetForCustomerAsync(
                RequireActor().UserId,
                deliveryId,
                cancellationToken)));
}

[ApiController]
[Route("api/v1/delivery")]
[Tags("Delivery staff")]
[Produces("application/json")]
public sealed class DeliveryStaffController(
    IDeliveryService deliveryService,
    IIndiaTimeProvider timeProvider) : DeliveryControllerBase
{
    [HttpGet("my-today")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeliveryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DeliveryResult>>>> GetToday(
        [FromQuery] DateOnly? date,
        [FromQuery] DeliveryStatus? status,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DeliveryResult>>.Ok(
            await deliveryService.GetTodayForStaffAsync(
                RequireActor(),
                date ?? timeProvider.Today,
                status,
                cancellationToken)));

    [HttpGet("{deliveryId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Get(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(
            await deliveryService.GetForOperationsAsync(
                RequireActor(), deliveryId, requireAssignment: true, cancellationToken)));

    [HttpPost("{deliveryId:guid}/pickup")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> PickUp(
        Guid deliveryId,
        [FromBody] DeliveryNotesRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.PickUpAsync(
            RequireActor(), deliveryId, request, cancellationToken)));

    [HttpPost("{deliveryId:guid}/start")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Start(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.StartAsync(
            RequireActor(), deliveryId, cancellationToken)));

    [HttpPost("{deliveryId:guid}/arrive")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Arrive(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.ArriveAsync(
            RequireActor(), deliveryId, cancellationToken)));

    [HttpPost("{deliveryId:guid}/verify-otp")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> VerifyOtp(
        Guid deliveryId,
        [FromBody] VerifyDeliveryOtpRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.VerifyOtpAsync(
            RequireActor(), deliveryId, request, cancellationToken)));

    [HttpPost("{deliveryId:guid}/complete")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Complete(
        Guid deliveryId,
        [FromBody] DeliveryNotesRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.CompleteAsync(
            RequireActor(), deliveryId, request, cancellationToken)));

    [HttpPost("{deliveryId:guid}/fail")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Fail(
        Guid deliveryId,
        [FromBody] FailDeliveryRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.FailAsync(
            RequireActor(), deliveryId, request, cancellationToken)));

    /// <summary>
    /// D1-A: fails a subscription delivery with the required staff proof image
    /// (multipart). The failure, proof, remedy ticket, snapshots, deadline and
    /// audit commit in one transaction; the customer is notified to choose
    /// Refund or Re-Delivery. One-time order deliveries keep the JSON
    /// <c>fail</c> endpoint (proof is not supported there).
    /// </summary>
    [HttpPost("{deliveryId:guid}/fail-with-proof")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> FailWithProof(
        Guid deliveryId,
        [FromForm] FailDeliveryWithProofForm request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Proof is null || request.Proof.Length == 0)
        {
            throw new ValidationAppException("A proof image is required to fail a subscription delivery.", "proof");
        }

        await using var content = new MemoryStream();
        await request.Proof.CopyToAsync(content, cancellationToken);
        content.Position = 0;
        var proof = new FailDeliveryProof(
            content,
            request.Proof.FileName,
            string.IsNullOrWhiteSpace(request.Proof.ContentType)
                ? "application/octet-stream"
                : request.Proof.ContentType,
            request.Proof.Length);
        var result = await deliveryService.FailAsync(
            RequireActor(),
            deliveryId,
            new FailDeliveryRequest(request.Reason, request.Remarks, request.Latitude, request.Longitude),
            cancellationToken,
            proof);
        return Ok(ApiResponse<DeliveryResult>.Ok(result));
    }

    [HttpPost("{deliveryId:guid}/location")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesTrackAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryLocationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryLocationResult>>> RecordLocation(
        Guid deliveryId,
        [FromBody] DeliveryLocationRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryLocationResult>.Ok(await deliveryService.RecordLocationAsync(
            RequireActor(), deliveryId, request, cancellationToken)));
}

[ApiController]
[Route("api/v1/delivery-management")]
[Tags("Delivery management")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesReadBranch)]
public sealed class DeliveryManagementController(IDeliveryService deliveryService) : DeliveryControllerBase
{
    [HttpPost("materialize")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesAssignBranch)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryMaterializationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryMaterializationResult>>> Materialize(
        [FromQuery] DateOnly throughDate,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryMaterializationResult>.Ok(await deliveryService.MaterializeEligibleAsync(
            RequireActor(), throughDate, cancellationToken)));

    [HttpPost("fetch-subscriptions")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesAssignBranch)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryMaterializationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryMaterializationResult>>> FetchSubscriptions(
        [FromQuery] DateOnly throughDate,
        [FromQuery] SubscriptionDeliverySlot? slot,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryMaterializationResult>.Ok(await deliveryService.FetchSubscriptionDeliveriesAsync(
            RequireActor(), throughDate, slot, cancellationToken)));

    [HttpGet("branches/{branchId:long}")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeliveryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DeliveryResult>>>> GetBranch(
        long branchId,
        [FromQuery] DateOnly? date,
        [FromQuery] DeliveryStatus? status,
        [FromQuery] DeliverySourceType? sourceType,
        [FromQuery] SubscriptionDeliverySlot? slot,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DeliveryResult>>.Ok(await deliveryService.GetForBranchAsync(
            RequireActor(), branchId, date, status, sourceType, slot, cancellationToken)));

    /// Lists past-due scheduled subscription occurrences of a branch that were
    /// never operationalized (no delivery row): the visits were never
    /// attempted, so a branch manager can confirm each miss here. Skipped
    /// (vacation/manual), replacement, failed, delivered and cancelled
    /// occurrences are never listed.
    /// </summary>
    [HttpGet("branches/{branchId:long}/missed-occurrences")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MissedOccurrenceResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MissedOccurrenceResult>>>> GetMissedOccurrences(
        long branchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<MissedOccurrenceResult>>.Ok(await deliveryService.GetMissedOccurrencesAsync(
            RequireActor(), branchId, cancellationToken)));

    /// <summary>
    /// Records a manager-confirmed miss for a past-due scheduled subscription
    /// occurrence with no operational delivery. A failed operational record
    /// plus the remedy ticket (frozen zero deduction: no service was provided)
    /// are created through the existing delivery-fail workflow; the customer
    /// still chooses the remedy and nothing is marked delivered here.
    /// </summary>
    [HttpPost("branches/{branchId:long}/missed-occurrences/{occurrenceId:guid}/fail")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesAssignBranch)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> FailMissedOccurrence(
        long branchId,
        Guid occurrenceId,
        [FromBody] FailMissedOccurrenceRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.FailMissedOccurrenceAsync(
            RequireActor(), branchId, occurrenceId, request, cancellationToken)));

    [HttpGet("branches/{branchId:long}/employees")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeliveryEmployeeResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DeliveryEmployeeResult>>>> GetEmployees(
        long branchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DeliveryEmployeeResult>>.Ok(await deliveryService.GetEmployeesAsync(
            RequireActor(), branchId, cancellationToken)));

    [HttpGet("{deliveryId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Get(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.GetForOperationsAsync(
            RequireActor(), deliveryId, requireAssignment: false, cancellationToken)));

    [HttpGet("{deliveryId:guid}/batch-allocations")]
    [ProducesResponseType(typeof(ApiResponse<DeliveryBatchAllocationsResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryBatchAllocationsResult>>> GetBatchAllocations(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryBatchAllocationsResult>.Ok(await deliveryService.GetBatchAllocationsAsync(
            RequireActor(), deliveryId, cancellationToken)));

    [HttpPut("{deliveryId:guid}/batch-allocations")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesAssignBranch)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> SaveBatchAllocations(
        Guid deliveryId,
        [FromBody] SaveDeliveryBatchAllocationsRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.SaveBatchAllocationsAsync(
            RequireActor(), deliveryId, request, cancellationToken)));

    [HttpPost("{deliveryId:guid}/assign")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesAssignBranch)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Assign(
        Guid deliveryId,
        [FromBody] AssignDeliveryRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.AssignAsync(
            RequireActor(), deliveryId, request, cancellationToken)));

    [HttpPost("bulk-assign")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesAssignBranch)]
    [ProducesResponseType(typeof(ApiResponse<BulkAssignDeliveriesResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BulkAssignDeliveriesResult>>> BulkAssign(
        [FromBody] BulkAssignDeliveriesRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BulkAssignDeliveriesResult>.Ok(await deliveryService.BulkAssignAsync(
            RequireActor(), request, cancellationToken)));
}
