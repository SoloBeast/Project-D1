using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Integrations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Integration configuration (SMTP email delivery, Razorpay runtime credentials,
/// Google Maps runtime credentials). Read and Manage are restricted to
/// OWNER and SYSTEM_ADMIN via the SetupIntegrationsRead / SetupIntegrationsManage
/// permissions, which are only granted to those roles.
/// Secrets are write-only: GET never returns stored passwords or keys.
/// </summary>
[ApiController]
[Route("api/v1/admin/setup/integrations")]
[Tags("Integration setup")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.SetupIntegrationsRead)]
public sealed class IntegrationsController(
    IIntegrationConfigurationService configurationService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IntegrationConfigurationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IntegrationConfigurationResult>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IntegrationConfigurationResult>.Ok(
            await configurationService.GetAsync(cancellationToken)));

    [HttpPut]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupIntegrationsManage)]
    [ProducesResponseType(typeof(ApiResponse<IntegrationConfigurationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IntegrationConfigurationResult>>> Update(
        [FromBody] UpdateIntegrationConfigurationRequest request,
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IntegrationConfigurationResult>.Ok(
            await configurationService.UpdateAsync(
                request,
                RequireUserId(),
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString(),
                cancellationToken)));

    [HttpPost("test")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupIntegrationsManage)]
    [ProducesResponseType(typeof(ApiResponse<IntegrationTestResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<IntegrationTestResult>>> Test(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<IntegrationTestResult>.Ok(
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
