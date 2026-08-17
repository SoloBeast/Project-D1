using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Cameras;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

public abstract class CameraControllerBase : ControllerBase
{
    protected CameraActor RequireCameraActor()
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
        return new CameraActor(userId, branchIds, hasGlobalAccess);
    }
}

[ApiController]
[Route("api/v1/cameras/public")]
[Tags("Live dairy cameras")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.CamerasViewPublic)]
public sealed class PublicCamerasController(ICameraService cameraService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<PublicCameraResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PublicCameraResult>>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyCollection<PublicCameraResult>>.Ok(
            await cameraService.GetPublicAsync(cancellationToken)));

    [HttpGet("{cameraId:guid}/stream")]
    [ProducesResponseType(typeof(ApiResponse<PublicCameraStreamResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<PublicCameraStreamResult>>> GetStream(
        Guid cameraId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<PublicCameraStreamResult>.Ok(
            await cameraService.GetPublicStreamAsync(cameraId, cancellationToken)));
}

[ApiController]
[Route("api/v1/admin/cameras")]
[Tags("Camera administration")]
[Produces("application/json")]
public sealed class AdminCamerasController(ICameraService cameraService) : CameraControllerBase
{
    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CamerasRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyCollection<ManagedCameraResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ManagedCameraResult>>>> Get(
        [FromQuery] long? branchId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyCollection<ManagedCameraResult>>.Ok(
            await cameraService.GetManagedAsync(RequireCameraActor(), branchId, cancellationToken)));

    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CamerasManage)]
    [ProducesResponseType(typeof(ApiResponse<ManagedCameraResult>), StatusCodes.Status201Created)]
    public async Task<ActionResult<ApiResponse<ManagedCameraResult>>> Create(
        [FromBody] CreateCameraRequest request,
        CancellationToken cancellationToken)
    {
        var result = await cameraService.CreateAsync(RequireCameraActor(), request, cancellationToken);
        return StatusCode(
            StatusCodes.Status201Created,
            ApiResponse<ManagedCameraResult>.Ok(result, "Camera metadata created."));
    }

    [HttpPatch("{cameraId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.CamerasManage)]
    [ProducesResponseType(typeof(ApiResponse<ManagedCameraResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ManagedCameraResult>>> Update(
        Guid cameraId,
        [FromBody] UpdateCameraRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ManagedCameraResult>.Ok(
            await cameraService.UpdateAsync(RequireCameraActor(), cameraId, request, cancellationToken),
            "Camera metadata updated."));
}
