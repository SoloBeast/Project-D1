using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// OTP provider (MSG91) configuration. Read and Manage are restricted to
/// OWNER and SYSTEM_ADMIN via the SetupOtpProviderRead / SetupOtpProviderManage
/// permissions, which are only granted to those roles.
/// The AuthKey is write-only: GET never returns it.
/// </summary>
[ApiController]
[Route("api/v1/admin/setup/otp-provider")]
[Tags("OTP provider setup")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.SetupOtpProviderRead)]
public sealed class OtpProviderConfigurationController(
    IOtpProviderConfigurationService configurationService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<OtpProviderConfigurationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OtpProviderConfigurationResult>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<OtpProviderConfigurationResult>.Ok(
            await configurationService.GetAsync(cancellationToken)));

    [HttpPut]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupOtpProviderManage)]
    [ProducesResponseType(typeof(ApiResponse<OtpProviderConfigurationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OtpProviderConfigurationResult>>> Update(
        [FromBody] UpdateOtpProviderConfigurationRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<OtpProviderConfigurationResult>.Ok(
            await configurationService.UpdateAsync(
                request,
                RequireUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken)));

    [HttpPost("test")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupOtpProviderManage)]
    [ProducesResponseType(typeof(ApiResponse<OtpProviderTestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<OtpProviderTestResult>>> Test(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<OtpProviderTestResult>.Ok(
            await configurationService.TestAsync(
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
