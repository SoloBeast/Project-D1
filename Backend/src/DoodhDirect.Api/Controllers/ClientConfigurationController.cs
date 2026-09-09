using DoodhDirect.Application.Common;
using DoodhDirect.Application.Integrations;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Client-safe configuration endpoint for authenticated Flutter clients.
/// Only intentionally public values are returned here — currently the Google Maps
/// Web Client Key used by the Flutter Web client. Server-only secrets (Google Maps
/// server-side key, SMTP credentials, Razorpay secrets, MSG91 AuthKey, database
/// credentials, JWT signing key) are NEVER exposed through this surface.
/// Authentication is required (global fallback policy); no specific permission is
/// needed because any signed-in user (customer/employee/owner) may load maps.
/// </summary>
[ApiController]
[Route("api/v1/client-config")]
[Tags("Client configuration")]
[Produces("application/json")]
public sealed class ClientConfigurationController(
    IClientConfigurationService clientConfigurationService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<ClientConfigurationResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<ClientConfigurationResult>>> Get(
        CancellationToken cancellationToken)
    {
        var webClientKey = await clientConfigurationService.GetGoogleMapsWebClientKeyAsync(cancellationToken);
        return Ok(ApiResponse<ClientConfigurationResult>.Ok(
            new ClientConfigurationResult(webClientKey)));
    }
}
