using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Branding;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

/// <summary>
/// Public, anonymous branding surface. Startup occurs before Login, and the
/// global fallback authorization policy requires an authenticated user — so
/// every action here explicitly allows anonymous access. Only the safe public
/// branding metadata and the active asset bytes are exposed; storage keys,
/// uploader identity, and audit data are never returned. A 404 is the expected
/// "branding asset not configured" state and keeps client-side fallbacks in
/// place.
/// </summary>
[ApiController]
[Route("api/v1/branding")]
[Tags("Branding")]
[Produces("application/json")]
public sealed class BrandingController(IBrandingService brandingService) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<BrandingResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BrandingResult>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BrandingResult>.Ok(
            await brandingService.GetCurrentBrandingAsync(cancellationToken)));

    /// <summary>Public logo content. Serves the configured bytes when one
    /// exists; 404 otherwise so clients keep their bundled fallback. The
    /// stored storage key/path is never exposed.</summary>
    [HttpGet("logo")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLogo(CancellationToken cancellationToken)
    {
        var media = await brandingService.OpenLogoAsync(cancellationToken);
        if (media is null)
        {
            return NotFound();
        }

        return File(media, cancellationToken);
    }

    /// <summary>Public startup animation content (GIF). Same 404-on-unset
    /// contract as the logo endpoint.</summary>
    [HttpGet("startup-animation")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStartupAnimation(CancellationToken cancellationToken)
    {
        var media = await brandingService.OpenStartupAnimationAsync(cancellationToken);
        if (media is null)
        {
            return NotFound();
        }

        return File(media, cancellationToken);
    }

    private FileStreamResult File(BrandingMediaContent media, CancellationToken cancellationToken)
    {
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentLength = media.FileSize;
        Response.Headers.ETag = media.ETag;
        Response.Headers.LastModified = media.LastModifiedUtc.ToString("R", CultureInfo.InvariantCulture);
        return File(media.Content, media.ContentType, enableRangeProcessing: true);
    }
}

/// <summary>
/// Owner/System Admin management of the global branding assets through the
/// setup surface. Read and Manage are restricted to OWNER and SYSTEM_ADMIN via
/// the SetupBrandingRead / SetupBrandingManage permissions, which are only
/// granted to those roles.
/// </summary>
[ApiController]
[Route("api/v1/admin/setup/branding")]
[Tags("Branding administration")]
[Produces("application/json")]
[Authorize(Policy = "permission:" + AuthorizationCodes.SetupBrandingRead)]
public sealed class BrandingAdministrationController(IBrandingService brandingService) : ControllerBase
{
    // Transport ceiling only: mirrors the Catalogue image endpoints. The
    // stored-asset caps live in BrandingMediaOptions and are enforced by the
    // validator after buffering.
    private const long MaximumBrandingTransportSize = 50L * 1024L * 1024L;

    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<BrandingResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BrandingResult>>> Get(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BrandingResult>.Ok(
            await brandingService.GetCurrentBrandingAsync(cancellationToken)));

    /// <summary>Adds the logo, or replaces the current one atomically.
    /// Requires the branding-management permission.</summary>
    [HttpPut("logo")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupBrandingManage)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumBrandingTransportSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumBrandingTransportSize)]
    [ProducesResponseType(typeof(ApiResponse<BrandingResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BrandingResult>>> UpsertLogo(
        [FromForm] BrandingUploadForm request,
        CancellationToken cancellationToken)
    {
        if (request.File is null)
        {
            throw new ValidationAppException("A logo file is required.", "file");
        }

        await using var content = request.File.OpenReadStream();
        var result = await brandingService.UpsertLogoAsync(
            RequireActor(),
            content,
            request.File.FileName,
            request.File.ContentType,
            cancellationToken);
        return Ok(ApiResponse<BrandingResult>.Ok(result));
    }

    /// <summary>Removes the current logo. The client keeps its bundled
    /// fallback when no logo is configured.</summary>
    [HttpDelete("logo")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupBrandingManage)]
    [ProducesResponseType(typeof(ApiResponse<BrandingResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BrandingResult>>> RemoveLogo(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BrandingResult>.Ok(
            await brandingService.RemoveLogoAsync(RequireActor(), cancellationToken)));

    /// <summary>Adds the startup animation (GIF), or replaces the current one
    /// atomically. Requires the branding-management permission.</summary>
    [HttpPut("startup-animation")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupBrandingManage)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumBrandingTransportSize)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaximumBrandingTransportSize)]
    [ProducesResponseType(typeof(ApiResponse<BrandingResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BrandingResult>>> UpsertStartupAnimation(
        [FromForm] BrandingUploadForm request,
        CancellationToken cancellationToken)
    {
        if (request.File is null)
        {
            throw new ValidationAppException("A startup animation file is required.", "file");
        }

        await using var content = request.File.OpenReadStream();
        var result = await brandingService.UpsertStartupAnimationAsync(
            RequireActor(),
            content,
            request.File.FileName,
            request.File.ContentType,
            cancellationToken);
        return Ok(ApiResponse<BrandingResult>.Ok(result));
    }

    /// <summary>Removes the current startup animation.</summary>
    [HttpDelete("startup-animation")]
    [Authorize(Policy = "permission:" + AuthorizationCodes.SetupBrandingManage)]
    [ProducesResponseType(typeof(ApiResponse<BrandingResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<BrandingResult>>> RemoveStartupAnimation(
        CancellationToken cancellationToken) =>
        Ok(ApiResponse<BrandingResult>.Ok(
            await brandingService.RemoveStartupAnimationAsync(RequireActor(), cancellationToken)));

    private BrandingActor RequireActor()
    {
        var userIdValue = User.FindFirstValue("user_id");
        if (!long.TryParse(userIdValue, NumberStyles.None, CultureInfo.InvariantCulture, out var userId))
        {
            throw new UnauthorizedAppException();
        }

        return new BrandingActor(userId);
    }
}
