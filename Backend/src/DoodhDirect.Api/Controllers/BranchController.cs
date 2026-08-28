using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Branches;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Administrative branch management: list, get, create, update, activate, and
/// deactivate branch records. Branch numbers are allocated server-side from the
/// centralized <c>BRANCH</c> numbering series and are never supplied by the client.
/// </summary>
[ApiController]
[Route("api/v1/admin/branches")]
[Tags("Branches")]
[Produces("application/json")]
public sealed class BranchController(IBranchService branchService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.BranchesRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<BranchResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<BranchResult>>>> List(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<BranchResult>>.Ok(
            await branchService.ListAsync(cancellationToken)));

    [HttpGet("{branchId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.BranchesRead)]
    [ProducesResponseType(typeof(ApiResponse<BranchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BranchResult>>> Get(
        Guid branchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BranchResult>.Ok(
            await branchService.GetAsync(branchId, cancellationToken)));

    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.BranchesManage)]
    [ProducesResponseType(typeof(ApiResponse<BranchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BranchResult>>> Create(
        [FromBody] UpsertBranchApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BranchResult>.Ok(
            await branchService.CreateAsync(
                RequireUserId(),
                request.ToApplicationRequest(),
                cancellationToken)));

    [HttpPut("{branchId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.BranchesManage)]
    [ProducesResponseType(typeof(ApiResponse<BranchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BranchResult>>> Update(
        Guid branchId,
        [FromBody] UpsertBranchApiRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BranchResult>.Ok(
            await branchService.UpdateAsync(
                RequireUserId(),
                branchId,
                request.ToApplicationRequest(),
                cancellationToken)));

    [HttpPost("{branchId:guid}/activate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.BranchesManage)]
    [ProducesResponseType(typeof(ApiResponse<BranchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BranchResult>>> Activate(
        Guid branchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BranchResult>.Ok(
            await branchService.SetActiveAsync(
                RequireUserId(), branchId, true, cancellationToken)));

    [HttpPost("{branchId:guid}/deactivate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.BranchesManage)]
    [ProducesResponseType(typeof(ApiResponse<BranchResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BranchResult>>> Deactivate(
        Guid branchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BranchResult>.Ok(
            await branchService.SetActiveAsync(
                RequireUserId(), branchId, false, cancellationToken)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}

public sealed record UpsertBranchApiRequest(
    [Required, MaxLength(50)] string Code,
    [Required, MaxLength(200)] string Name,
    [MaxLength(300)] string? AddressLine1,
    [MaxLength(300)] string? AddressLine2,
    [MaxLength(150)] string? Locality,
    [Required, MaxLength(100)] string City,
    [Required, MaxLength(100)] string State,
    [MaxLength(10)] string? PinCode,
    decimal Latitude,
    decimal Longitude,
    decimal? ServiceRadiusKm)
{
    public UpsertBranchRequest ToApplicationRequest() =>
        new(Code, Name, AddressLine1, AddressLine2, Locality, City, State, PinCode, Latitude, Longitude, ServiceRadiusKm);
}
