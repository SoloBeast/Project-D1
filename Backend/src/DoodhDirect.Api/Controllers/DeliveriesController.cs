using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Deliveries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

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
public sealed class DeliveryStaffController(IDeliveryService deliveryService) : DeliveryControllerBase
{
    [HttpGet("my-today")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeliveryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DeliveryResult>>>> GetToday(
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DeliveryResult>>.Ok(
            await deliveryService.GetTodayForStaffAsync(
                RequireActor(),
                date ?? DateOnly.FromDateTime(DateTime.UtcNow),
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

    [HttpPost("{deliveryId:guid}/issue-otp")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryOtpIssuedResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryOtpIssuedResult>>> IssueOtp(
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        await deliveryService.IssueOtpAsync(RequireActor(), deliveryId, cancellationToken);
        return Ok(ApiResponse<DeliveryOtpIssuedResult>.Ok(new DeliveryOtpIssuedResult(true)));
    }

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

    [HttpGet("branches/{branchId:long}")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DeliveryResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DeliveryResult>>>> GetBranch(
        long branchId,
        [FromQuery] DateOnly? date,
        [FromQuery] DeliveryStatus? status,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<DeliveryResult>>.Ok(await deliveryService.GetForBranchAsync(
            RequireActor(), branchId, date, status, cancellationToken)));

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

    [HttpPost("{deliveryId:guid}/assign")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DeliveriesAssignBranch)]
    [ProducesResponseType(typeof(ApiResponse<DeliveryResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DeliveryResult>>> Assign(
        Guid deliveryId,
        [FromBody] AssignDeliveryRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DeliveryResult>.Ok(await deliveryService.AssignAsync(
            RequireActor(), deliveryId, request, cancellationToken)));
}

public sealed record DeliveryOtpIssuedResult(bool Issued);
