using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Dairy;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Dairy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/dairy")]
[Tags("Dairy operations")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.DairyRead)]
public sealed class DairyController(IDairyService dairyService) : ControllerBase
{
    [HttpGet("branches/{branchId:long}/dashboard")]
    [ProducesResponseType(typeof(ApiResponse<DairyDashboardResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<DairyDashboardResult>>> GetDashboard(
        long branchId,
        [FromQuery] DateOnly? productionDate,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<DairyDashboardResult>.Ok(await dairyService.GetDashboardAsync(
            RequireActor(), branchId, productionDate, cancellationToken)));

    [HttpPost("branches/{branchId:long}/production")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DairyManage)]
    [ProducesResponseType(typeof(ApiResponse<MilkProductionResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<MilkProductionResult>>> RecordProduction(
        long branchId,
        [FromBody] RecordMilkProductionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await dairyService.RecordProductionAsync(RequireActor(), branchId, request, cancellationToken);
        return CreatedAtAction(nameof(GetBatch), new { batchId = result.Batch.PublicId }, ApiResponse<MilkProductionResult>.Ok(result));
    }

    [HttpGet("branches/{branchId:long}/production")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MilkProductionResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MilkProductionResult>>>> GetProductionHistory(
        long branchId,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<MilkProductionResult>>.Ok(
            await dairyService.GetProductionHistoryAsync(RequireActor(), branchId, fromDate, toDate, cancellationToken)));

    [HttpGet("branches/{branchId:long}/batches")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MilkBatchResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MilkBatchResult>>>> GetBatches(
        long branchId,
        [FromQuery] MilkBatchStatus? status,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<MilkBatchResult>>.Ok(
            await dairyService.GetBatchesAsync(RequireActor(), branchId, status, cancellationToken)));

    [HttpGet("batches/{batchId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<MilkBatchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MilkBatchResult>>> GetBatch(
        Guid batchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<MilkBatchResult>.Ok(await dairyService.GetBatchAsync(
            RequireActor(), batchId, cancellationToken)));

    [HttpGet("branches/{branchId:long}/availability")]
    [ProducesResponseType(typeof(ApiResponse<MilkAvailabilityResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MilkAvailabilityResult>>> GetAvailability(
        long branchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<MilkAvailabilityResult>.Ok(await dairyService.GetAvailabilityAsync(
            RequireActor(), branchId, cancellationToken)));

    [HttpPost("batches/{batchId:guid}/usage")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.DairyManage)]
    [ProducesResponseType(typeof(ApiResponse<MilkUsageResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<MilkUsageResult>>> RecordUsage(
        Guid batchId,
        [FromBody] RecordMilkUsageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await dairyService.RecordUsageAsync(RequireActor(), batchId, request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<MilkUsageResult>.Ok(result));
    }

    [HttpGet("branches/{branchId:long}/usage")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MilkUsageResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<MilkUsageResult>>>> GetUsageHistory(
        long branchId,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<MilkUsageResult>>.Ok(
            await dairyService.GetUsageHistoryAsync(RequireActor(), branchId, fromDate, toDate, cancellationToken)));

    private DairyActor RequireActor()
    {
        var userIdValue = User.FindFirstValue("user_id");
        if (!long.TryParse(userIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
            throw new UnauthorizedAppException();

        var branchIds = User.FindAll(AuthorizationCodes.BranchClaim)
            .Select(claim => long.TryParse(claim.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var branchId)
                ? branchId
                : (long?)null)
            .Where(branchId => branchId.HasValue)
            .Select(branchId => branchId!.Value)
            .Distinct()
            .ToArray();
        var hasGlobalAccess = User.HasClaim(
            AuthorizationCodes.PermissionClaim,
            AuthorizationCodes.GlobalAccess);
        return new DairyActor(userId, branchIds, hasGlobalAccess);
    }
}
