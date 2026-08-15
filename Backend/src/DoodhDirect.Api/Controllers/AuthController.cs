using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoodhDirect.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
[Tags("Authentication")]
[Produces("application/json")]
public sealed class AuthController(
    IAuthenticationService authenticationService,
    IOtpService otpService) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<AuthSessionResult>>> Register(
        [FromBody] RegisterApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.RegisterAsync(
            new RegisterRequest(
                request.DisplayName,
                request.Email,
                request.Mobile,
                request.Password,
                ToDeviceInfo(request.Device)),
            cancellationToken);
        return Ok(ApiResponse<AuthSessionResult>.Ok(result));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthSessionResult>>> Login(
        [FromBody] PasswordLoginApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.LoginAsync(
            new PasswordLoginRequest(
                request.Login,
                request.Password,
                ToDeviceInfo(request.Device)),
            cancellationToken);
        return Ok(ApiResponse<AuthSessionResult>.Ok(result));
    }

    [HttpPost("send-otp")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<object>>> SendOtp(
        [FromBody] SendOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        await otpService.SendAsync(
            new SendOtpRequest(
                request.Mobile,
                request.Purpose,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "OTP request accepted."));
    }

    [HttpPost("verify-otp")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthSessionResult>>> VerifyOtp(
        [FromBody] VerifyOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await otpService.VerifyAsync(
            new VerifyOtpRequest(
                request.Mobile,
                request.Code,
                request.Purpose,
                ToDeviceInfo(request.Device)),
            cancellationToken);
        return Ok(ApiResponse<AuthSessionResult>.Ok(result));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthSessionResult>>> Refresh(
        [FromBody] RefreshApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.RefreshAsync(
            request.RefreshToken,
            ToDeviceInfo(request.Device),
            cancellationToken);
        return Ok(ApiResponse<AuthSessionResult>.Ok(result));
    }

    [HttpPost("logout")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<object>>> Logout(
        CancellationToken cancellationToken)
    {
        var userId = RequireLongClaim("user_id");
        var sessionId = RequireGuidClaim("session_id");
        await authenticationService.LogoutAsync(sessionId, userId, cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Logged out."));
    }

    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<AuthUserResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthUserResult>>> Me(
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.GetCurrentUserAsync(
            RequireLongClaim("user_id"),
            cancellationToken);
        return Ok(ApiResponse<AuthUserResult>.Ok(result));
    }

    private DeviceInfo ToDeviceInfo(DeviceApiRequest device) => new(
        device.DeviceIdentifier,
        device.DeviceName,
        device.Platform,
        HttpContext.Connection.RemoteIpAddress?.ToString(),
        Request.Headers.UserAgent.ToString());

    private long RequireLongClaim(string claimType)
    {
        var value = User.FindFirstValue(claimType);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new UnauthorizedAppException();
    }

    private Guid RequireGuidClaim(string claimType)
    {
        var value = User.FindFirstValue(claimType);
        return Guid.TryParse(value, out var result)
            ? result
            : throw new UnauthorizedAppException();
    }
}

public sealed record DeviceApiRequest(
    [Required, MaxLength(200)] string DeviceIdentifier,
    [MaxLength(160)] string? DeviceName,
    [MaxLength(40)] string? Platform);

public sealed record RegisterApiRequest(
    [Required, MaxLength(160)] string DisplayName,
    [EmailAddress, MaxLength(320)] string? Email,
    [MaxLength(20)] string? Mobile,
    [Required, MinLength(8), MaxLength(200)] string Password,
    [Required] DeviceApiRequest Device);

public sealed record PasswordLoginApiRequest(
    [Required, MaxLength(320)] string Login,
    [Required, MaxLength(200)] string Password,
    [Required] DeviceApiRequest Device);

public sealed record SendOtpApiRequest(
    [Required, MaxLength(20)] string Mobile,
    OtpPurpose Purpose);

public sealed record VerifyOtpApiRequest(
    [Required, MaxLength(20)] string Mobile,
    [Required, StringLength(6, MinimumLength = 6)] string Code,
    OtpPurpose Purpose,
    [Required] DeviceApiRequest Device);

public sealed record RefreshApiRequest(
    [Required, MaxLength(1000)] string RefreshToken,
    [Required] DeviceApiRequest Device);
