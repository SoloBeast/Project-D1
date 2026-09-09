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
    [ProducesResponseType(typeof(ApiResponse<SendOtpResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<SendOtpResult>>> SendOtp(
        [FromBody] SendOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await otpService.SendAsync(
            new SendOtpRequest(
                request.Mobile,
                request.Purpose,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);
        return Ok(ApiResponse<SendOtpResult>.Ok(result, "OTP request accepted."));
    }

    [HttpPost("retry-otp")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<SendOtpResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<SendOtpResult>>> RetryOtp(
        [FromBody] SendOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await otpService.RetryAsync(
            new RetryOtpRequest(
                request.Mobile,
                request.Purpose,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);
        return Ok(ApiResponse<SendOtpResult>.Ok(result, "OTP request accepted."));
    }

    [HttpPost("verify-otp")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<OtpVerificationResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<OtpVerificationResult>>> VerifyOtp(
        [FromBody] VerifyOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await otpService.VerifyAsync(
            new VerifyOtpRequest(
                request.Mobile,
                request.Code,
                request.Purpose,
                request.ReqId,
                ToDeviceInfo(request.Device)),
            cancellationToken);
        return Ok(ApiResponse<OtpVerificationResult>.Ok(result));
    }

    [HttpPost("otp/complete-onboarding")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<AuthSessionResult>>> CompleteOnboarding(
        [FromBody] CompleteOtpOnboardingApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.CompleteOtpRegistrationAsync(
            new CompleteOtpRegistrationRequest(
                request.Mobile,
                request.ReqId,
                request.NewPassword,
                ToDeviceInfo(request.Device)),
            cancellationToken);
        return Ok(ApiResponse<AuthSessionResult>.Ok(result, "Account created. You are signed in."));
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

    [HttpPost("password/set")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<object>>> SetPassword(
        [FromBody] SetPasswordApiRequest request,
        CancellationToken cancellationToken)
    {
        await authenticationService.SetPasswordAsync(
            RequireLongClaim("user_id"),
            new SetPasswordRequest(request.NewPassword),
            cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Password set."));
    }

    [HttpPost("password/change")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<object>>> ChangePassword(
        [FromBody] ChangePasswordApiRequest request,
        CancellationToken cancellationToken)
    {
        await authenticationService.ChangePasswordAsync(
            RequireLongClaim("user_id"),
            RequireGuidClaim("session_id"),
            new ChangePasswordRequest(request.CurrentPassword, request.NewPassword),
            cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "Password changed. Other sessions have been signed out."));
    }

    [HttpPost("password/forgot")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<SendOtpResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ApiResponse<SendOtpResult>>> ForgotPassword(
        [FromBody] ForgotPasswordApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.ForgotPasswordAsync(
            new ForgotPasswordRequest(request.Mobile, HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);
        return Ok(ApiResponse<SendOtpResult>.Ok(result, "Password reset OTP sent."));
    }

    [HttpPost("password/reset/verify-otp")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<object>>> VerifyResetOtp(
        [FromBody] VerifyResetOtpApiRequest request,
        CancellationToken cancellationToken)
    {
        await otpService.VerifyResetOtpAsync(
            new VerifyResetOtpRequest(
                request.Destination,
                request.Code,
                request.ReqId,
                ToDeviceInfo(request.Device)),
            cancellationToken);
        return Ok(ApiResponse<object>.Ok(new { }, "OTP verified."));
    }

    [HttpPost("password/reset")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<AuthSessionResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthSessionResult>>> ResetPassword(
        [FromBody] ResetPasswordApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.ResetPasswordAsync(
            new ResetPasswordRequest(
                request.Mobile,
                request.ReqId,
                request.NewPassword,
                ToDeviceInfo(request.Device)),
            cancellationToken);
        return Ok(ApiResponse<AuthSessionResult>.Ok(result, "Password reset."));
    }

    [HttpPost("email/request-change")]
    [ProducesResponseType(typeof(ApiResponse<EmailChangeRequestedResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiResponse<EmailChangeRequestedResult>>> RequestEmailChange(
        [FromBody] RequestEmailChangeApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.RequestEmailChangeAsync(
            RequireLongClaim("user_id"),
            new RequestEmailChangeRequest(request.NewEmail, HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);
        return Ok(ApiResponse<EmailChangeRequestedResult>.Ok(result, "Verification OTP sent to the new email."));
    }

    [HttpPost("mobile/request-change")]
    [ProducesResponseType(typeof(ApiResponse<MobileChangeRequestedResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<MobileChangeRequestedResult>>> RequestMobileChange(
        [FromBody] RequestMobileChangeApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authenticationService.RequestMobileChangeAsync(
            RequireLongClaim("user_id"),
            new RequestMobileChangeRequest(request.NewMobile, HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken);
        return Ok(ApiResponse<MobileChangeRequestedResult>.Ok(result, "Verification OTP sent to the new mobile number."));
    }

    [HttpPost("mobile/verify")]
    [ProducesResponseType(typeof(ApiResponse<AuthUserResult>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiResponse<AuthUserResult>>> VerifyMobileChange(
        [FromBody] VerifyMobileChangeApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await otpService.VerifyMobileChangeOtpAsync(
            new VerifyOtpRequest(request.Mobile, request.Code, OtpPurpose.EmailVerification, request.ReqId, ToDeviceInfo(request.Device)),
            RequireLongClaim("user_id"),
            cancellationToken);
        return Ok(ApiResponse<AuthUserResult>.Ok(result, "Mobile number verified."));
    }

    [HttpPost("email/verify")]
    [ProducesResponseType(typeof(ApiResponse<AuthUserResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ApiResponse<AuthUserResult>>> VerifyEmailChange(
        [FromBody] VerifyEmailChangeApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await otpService.VerifyEmailOtpAsync(
            new VerifyEmailChangeRequest(
                request.Code,
                request.ReqId,
                ToDeviceInfo(request.Device)),
            RequireLongClaim("user_id"),
            cancellationToken);
        return Ok(ApiResponse<AuthUserResult>.Ok(result, "Email verified."));
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
    [Required, MaxLength(64)] string ReqId,
    [Required] DeviceApiRequest Device);

public sealed record CompleteOtpOnboardingApiRequest(
    [Required, MaxLength(20)] string Mobile,
    [Required, MaxLength(64)] string ReqId,
    [Required, MinLength(8), MaxLength(200)] string NewPassword,
    [Required] DeviceApiRequest Device);

public sealed record RefreshApiRequest(
    [Required, MaxLength(1000)] string RefreshToken,
    [Required] DeviceApiRequest Device);

public sealed record SetPasswordApiRequest(
    [Required, MinLength(8), MaxLength(200)] string NewPassword);

public sealed record ChangePasswordApiRequest(
    [Required, MaxLength(200)] string CurrentPassword,
    [Required, MinLength(8), MaxLength(200)] string NewPassword);

public sealed record ForgotPasswordApiRequest(
    [Required, MaxLength(20)] string Mobile);

public sealed record VerifyResetOtpApiRequest(
    [Required, MaxLength(320)] string Destination,
    [Required, StringLength(6, MinimumLength = 6)] string Code,
    [Required, MaxLength(64)] string ReqId,
    [Required] DeviceApiRequest Device);

public sealed record ResetPasswordApiRequest(
    [Required, MaxLength(20)] string Mobile,
    [Required, MaxLength(64)] string ReqId,
    [Required, MinLength(8), MaxLength(200)] string NewPassword,
    [Required] DeviceApiRequest Device);

public sealed record RequestEmailChangeApiRequest(
    [Required, EmailAddress, MaxLength(320)] string NewEmail);

public sealed record RequestMobileChangeApiRequest(
    [Required, MaxLength(20)] string NewMobile);

public sealed record VerifyMobileChangeApiRequest(
    [Required, MaxLength(20)] string Mobile,
    [Required, StringLength(6, MinimumLength = 6)] string Code,
    [Required, MaxLength(64)] string ReqId,
    [Required] DeviceApiRequest Device);

public sealed record VerifyEmailChangeApiRequest(
    [Required, StringLength(6, MinimumLength = 6)] string Code,
    [Required, MaxLength(64)] string ReqId,
    [Required] DeviceApiRequest Device);
