using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/admin/setup/tax-charges")]
[Tags("Tax & charges setup")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.SetupTaxChargesRead)]
public sealed class TaxChargesController(IChargeService chargeService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ChargeResult>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ChargeResult>>>> List(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IReadOnlyList<ChargeResult>>.Ok(
            await chargeService.ListAsync(cancellationToken)));

    [HttpGet("{publicId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<ChargeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ChargeResult>>> Get(
        Guid publicId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ChargeResult>.Ok(
            await chargeService.GetAsync(publicId, cancellationToken)));

    [HttpPost]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupTaxChargesManage)]
    [ProducesResponseType(typeof(ApiResponse<ChargeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ChargeResult>>> Create(
        [FromBody] CreateChargeRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ChargeResult>.Ok(
            await chargeService.CreateAsync(
                request,
                RequireUserId(),
                cancellationToken)));

    [HttpPut("{publicId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupTaxChargesManage)]
    [ProducesResponseType(typeof(ApiResponse<ChargeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ChargeResult>>> Update(
        Guid publicId,
        [FromBody] UpdateChargeRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ChargeResult>.Ok(
            await chargeService.UpdateAsync(
                publicId,
                request,
                RequireUserId(),
                cancellationToken)));

    [HttpPost("{publicId:guid}/activate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupTaxChargesManage)]
    [ProducesResponseType(typeof(ApiResponse<ChargeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ChargeResult>>> Activate(
        Guid publicId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ChargeResult>.Ok(
            await chargeService.SetActiveAsync(
                publicId,
                true,
                RequireUserId(),
                cancellationToken)));

    [HttpPost("{publicId:guid}/deactivate")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupTaxChargesManage)]
    [ProducesResponseType(typeof(ApiResponse<ChargeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ChargeResult>>> Deactivate(
        Guid publicId,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ChargeResult>.Ok(
            await chargeService.SetActiveAsync(
                publicId,
                false,
                RequireUserId(),
                cancellationToken)));

    /// <summary>
    /// Dedicated applicability toggle. Switching an item charge back to the global
    /// mode is refused while any product assignment exists (server-side invariant).
    /// </summary>
    [HttpPut("{publicId:guid}/applicability")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupTaxChargesManage)]
    [ProducesResponseType(typeof(ApiResponse<ChargeResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ChargeResult>>> SetApplicability(
        Guid publicId,
        [FromBody] SetChargeApplicabilityRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<ChargeResult>.Ok(
            await chargeService.SetApplicableOnAllAsync(
                publicId,
                request.ApplicableOnAll,
                RequireUserId(),
                cancellationToken)));

    [HttpDelete("{publicId:guid}")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupTaxChargesManage)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<object>>> Delete(
        Guid publicId,
        CancellationToken cancellationToken)
    {
        await chargeService.DeleteAsync(
            publicId,
            RequireUserId(),
            cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { deleted = true }));
    }

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}
