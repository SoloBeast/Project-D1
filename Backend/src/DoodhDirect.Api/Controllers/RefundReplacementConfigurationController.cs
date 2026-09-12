using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.RefundReplacement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Refund/replacement request window configuration. Read and Manage are
/// restricted to OWNER and SYSTEM_ADMIN via the SetupRefundReplacementRead /
/// SetupRefundReplacementManage permissions, which are only granted to those
/// roles. The window is expressed in whole hours and is server-authoritative.
/// </summary>
[ApiController]
[Route("api/v1/admin/setup/refund-replacement")]
[Tags("Refund/replacement setup")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.SetupRefundReplacementRead)]
public sealed class RefundReplacementConfigurationController(
    IRefundReplacementConfigurationService configurationService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementConfigurationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementConfigurationResult>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementConfigurationResult>.Ok(
            await configurationService.GetAsync(cancellationToken)));

    [HttpPut]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupRefundReplacementManage)]
    [ProducesResponseType(typeof(ApiResponse<RefundReplacementConfigurationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<RefundReplacementConfigurationResult>>> Update(
        [FromBody] UpdateRefundReplacementConfigurationRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<RefundReplacementConfigurationResult>.Ok(
            await configurationService.UpdateAsync(
                request,
                RequireUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken)));

    private long RequireUserId()
    {
        var value = User.FindFirstValue("user_id");
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : throw new UnauthorizedAppException();
    }
}
