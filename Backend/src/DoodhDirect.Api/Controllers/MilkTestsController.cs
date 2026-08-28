using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Api.Authorization;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

public abstract class MilkTestControllerBase : ControllerBase
{
    protected MilkTestActor RequireMilkTestActor()
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

        return new MilkTestActor(userId, branchIds, hasGlobalAccess);
    }
}

[ApiController]
[Route("api/v1/deliveries/{deliveryId:guid}/milk-test")]
[Tags("Customer doorstep testing")]
[Produces("application/json")]
public sealed class CustomerDeliveryMilkTestsController(IMilkTestService milkTestService)
    : MilkTestControllerBase
{
    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsRequestOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerMilkTestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerMilkTestResult>>> RequestTest(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerMilkTestResult>.Ok(await milkTestService.RequestAsync(
            RequireMilkTestActor(), deliveryId, cancellationToken)));

    [HttpGet]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsReadOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerMilkTestResult?>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerMilkTestResult?>>> Get(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerMilkTestResult?>.Ok(await milkTestService.GetForCustomerAsync(
            RequireMilkTestActor(), deliveryId, cancellationToken)));
}

[ApiController]
[Route("api/v1/delivery/{deliveryId:guid}/milk-test")]
[Tags("Delivery staff doorstep testing")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsOperateAssigned)]
public sealed class DeliveryStaffMilkTestsController(IMilkTestService milkTestService)
    : MilkTestControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<StaffMilkTestResult?>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StaffMilkTestResult?>>> Get(
        Guid deliveryId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<StaffMilkTestResult?>.Ok(await milkTestService.GetForStaffAsync(
            RequireMilkTestActor(), deliveryId, cancellationToken)));
}

[ApiController]
[Route("api/v1/milk-tests")]
[Tags("Doorstep testing")]
[Produces("application/json")]
public sealed class MilkTestsController(IMilkTestService milkTestService) : MilkTestControllerBase
{
    private const long MaximumTransportUploadSize = 50L * 1024L * 1024L;

    [HttpPost("{milkTestId:guid}/images")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsOperateAssigned)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumTransportUploadSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumTransportUploadSize)]
    [ProducesResponseType(typeof(ApiResponse<MilkTestImageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MilkTestImageResult>>> UploadImage(
        Guid milkTestId,
        [FromForm] MilkTestImageUploadForm request,
        CancellationToken cancellationToken)
    {
        if (request.Image is null)
        {
            throw new ValidationAppException("An image is required.", "image");
        }

        await using var content = request.Image.OpenReadStream();
        var result = await milkTestService.UploadImageAsync(
            RequireMilkTestActor(),
            milkTestId,
            content,
            request.Image.FileName,
            request.Image.ContentType,
            cancellationToken);
        return Ok(ApiResponse<MilkTestImageResult>.Ok(result));
    }

    [HttpDelete("{milkTestId:guid}/images/{imageId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<StaffMilkTestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StaffMilkTestResult>>> DeleteImage(
        Guid milkTestId,
        Guid imageId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<StaffMilkTestResult>.Ok(await milkTestService.DeleteImageAsync(
            RequireMilkTestActor(), milkTestId, imageId, cancellationToken)));

    [HttpPut("{milkTestId:guid}/images/{imageId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsOperateAssigned)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumTransportUploadSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumTransportUploadSize)]
    [ProducesResponseType(typeof(ApiResponse<MilkTestImageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MilkTestImageResult>>> ReplaceImage(
        Guid milkTestId,
        Guid imageId,
        [FromForm] MilkTestImageUploadForm request,
        CancellationToken cancellationToken)
    {
        if (request.Image is null)
        {
            throw new ValidationAppException("An image is required.", "image");
        }

        await using var content = request.Image.OpenReadStream();
        var result = await milkTestService.ReplaceImageAsync(
            RequireMilkTestActor(),
            milkTestId,
            imageId,
            content,
            request.Image.FileName,
            request.Image.ContentType,
            cancellationToken);
        return Ok(ApiResponse<MilkTestImageResult>.Ok(result));
    }

    [HttpPost("{milkTestId:guid}/images/{imageId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsDecideOwn)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumTransportUploadSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumTransportUploadSize)]
    [ProducesResponseType(typeof(ApiResponse<MilkTestImageResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MilkTestImageResult>>> ReplaceImageAsCustomer(
        Guid milkTestId,
        Guid imageId,
        [FromForm] MilkTestImageUploadForm request,
        CancellationToken cancellationToken)
    {
        if (request.Image is null)
        {
            throw new ValidationAppException("An image is required.", "image");
        }

        await using var content = request.Image.OpenReadStream();
        var result = await milkTestService.ReplaceImageAsync(
            RequireMilkTestActor(),
            milkTestId,
            imageId,
            content,
            request.Image.FileName,
            request.Image.ContentType,
            cancellationToken);
        return Ok(ApiResponse<MilkTestImageResult>.Ok(result));
    }

    [HttpPost("{milkTestId:guid}/complete")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsOperateAssigned)]
    [ProducesResponseType(typeof(ApiResponse<StaffMilkTestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<StaffMilkTestResult>>> Complete(
        Guid milkTestId,
        [FromBody] CompleteMilkTestRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<StaffMilkTestResult>.Ok(await milkTestService.CompleteAsync(
            RequireMilkTestActor(), milkTestId, request, cancellationToken)));

    [HttpPost("{milkTestId:guid}/confirm")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsDecideOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerMilkTestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerMilkTestResult>>> Confirm(
        Guid milkTestId,
        [FromBody] DecideMilkTestRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerMilkTestResult>.Ok(await milkTestService.ConfirmAsync(
            RequireMilkTestActor(), milkTestId, request, cancellationToken)));

    [HttpPost("{milkTestId:guid}/reject")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.MilkTestsDecideOwn)]
    [ProducesResponseType(typeof(ApiResponse<CustomerMilkTestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<CustomerMilkTestResult>>> Reject(
        Guid milkTestId,
        [FromBody] DecideMilkTestRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<CustomerMilkTestResult>.Ok(await milkTestService.RejectAsync(
            RequireMilkTestActor(), milkTestId, request, cancellationToken)));

    [HttpGet("{milkTestId:guid}/images/{imageId:guid}/content")]
    [Authorize(Policy = AuthorizationPolicyNames.AnyMilkTestImageContent)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenImage(
        Guid milkTestId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var media = await milkTestService.OpenImageAsync(
            RequireMilkTestActor(), milkTestId, imageId, cancellationToken);
        Response.ContentLength = media.FileSize;
        return File(media.Content, media.ContentType, enableRangeProcessing: true);
    }
}

public sealed class MilkTestImageUploadForm
{
    public IFormFile? Image { get; init; }
}
