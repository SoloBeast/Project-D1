using System.Security.Cryptography;
using System.Text;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class OtpService(
    DoodhDirectDbContext dbContext,
    IMsg91OtpProvider otpProvider,
    IIndiaTimeProvider timeProvider,
    ITokenService tokenService,
    IOptions<IdentityOptions> options,
    INotificationEventWriter notificationEventWriter) : IOtpService
{
    private readonly IdentityOptions _options = options.Value;

    public async Task<SendOtpResult> SendAsync(SendOtpRequest request, CancellationToken cancellationToken)
    {
        var destination = NormalizeMobileDestination(Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile)));
        var now = timeProvider.Now;
        var windowStart = now.AddMinutes(-_options.OtpRateLimitWindowMinutes);
        var requestCount = await dbContext.OtpChallenges
            .CountAsync(x => x.Destination == destination && x.Purpose == request.Purpose && x.CreatedAt >= windowStart, cancellationToken);
        if (requestCount >= _options.OtpRequestsPerWindow)
        {
            await WriteAuditAsync(null, "AUTH_OTP_RATE_LIMITED", destination, request.IpAddress, null, request.Purpose.ToString(), cancellationToken);
            throw new RateLimitAppException();
        }

        var challenge = new OtpChallenge(
            destination,
            request.Purpose,
            now,
            now.AddMinutes(_options.OtpLifetimeMinutes),
            _options.OtpMaxAttempts,
            request.IpAddress);

        var providerResult = await otpProvider.SendAsync(
            new Msg91OtpSendRequest(destination, request.Purpose.ToString()),
            cancellationToken);
        challenge.AttachProviderRequestId(providerResult.ReqId);

        dbContext.OtpChallenges.Add(challenge);
        dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_REQUESTED", "OtpChallenge", challenge.PublicId.ToString(), null, null, request.IpAddress, null, request.Purpose.ToString(), now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SendOtpResult(providerResult.ReqId);
    }

    public async Task<SendOtpResult> RetryAsync(RetryOtpRequest request, CancellationToken cancellationToken)
    {
        var destination = NormalizeMobileDestination(Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile)));
        var now = timeProvider.Now;
        var windowStart = now.AddMinutes(-_options.OtpRateLimitWindowMinutes);
        var requestCount = await dbContext.OtpChallenges
            .CountAsync(x => x.Destination == destination && x.Purpose == request.Purpose && x.CreatedAt >= windowStart, cancellationToken);
        if (requestCount >= _options.OtpRequestsPerWindow)
        {
            await WriteAuditAsync(null, "AUTH_OTP_RATE_LIMITED", destination, request.IpAddress, null, request.Purpose.ToString(), cancellationToken);
            throw new RateLimitAppException();
        }

        var challenge = await dbContext.OtpChallenges
            .Where(x => x.Destination == destination && x.Purpose == request.Purpose)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (challenge is null || challenge.ReqId is null || !challenge.CanAttempt(now))
            throw new BusinessRuleException("There is no active OTP request to resend. Please request a new OTP.");

        var providerResult = await otpProvider.RetryAsync(
            new Msg91OtpRetryRequest(challenge.ReqId),
            cancellationToken);

        dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_RESENT", "OtpChallenge", challenge.PublicId.ToString(), null, null, request.IpAddress, null, request.Purpose.ToString(), now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SendOtpResult(providerResult.ReqId);
    }

    public async Task<SendOtpResult> SendEmailOtpAsync(string email, string? ipAddress, CancellationToken cancellationToken)
    {
        var destination = NormalizeEmail(email)
            ?? throw new ValidationAppException("A valid email address is required.", nameof(email));
        return await SendCoreAsync(destination, OtpPurpose.EmailVerification, ipAddress, cancellationToken);
    }

    public async Task<SendOtpResult> RetryEmailOtpAsync(string email, string? ipAddress, CancellationToken cancellationToken)
    {
        var destination = NormalizeEmail(email)
            ?? throw new ValidationAppException("A valid email address is required.", nameof(email));
        return await SendCoreAsync(destination, OtpPurpose.EmailVerification, ipAddress, cancellationToken);
    }

    private async Task<SendOtpResult> SendCoreAsync(string destination, OtpPurpose purpose, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = timeProvider.Now;
        var windowStart = now.AddMinutes(-_options.OtpRateLimitWindowMinutes);
        var requestCount = await dbContext.OtpChallenges
            .CountAsync(x => x.Destination == destination && x.Purpose == purpose && x.CreatedAt >= windowStart, cancellationToken);
        if (requestCount >= _options.OtpRequestsPerWindow)
        {
            await WriteAuditAsync(null, "AUTH_OTP_RATE_LIMITED", destination, ipAddress, null, purpose.ToString(), cancellationToken);
            throw new RateLimitAppException();
        }

        // Retry reuses the latest still-active challenge's reqId; if none is active a
        // fresh challenge (and a new reqId) is created, keeping retry semantics intact.
        var latest = await dbContext.OtpChallenges
            .Where(x => x.Destination == destination && x.Purpose == purpose)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && latest.ReqId is not null && latest.CanAttempt(now))
        {
            var retryResult = await otpProvider.RetryAsync(
                new Msg91OtpRetryRequest(latest.ReqId),
                cancellationToken);
            dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_RESENT", "OtpChallenge", latest.PublicId.ToString(), null, null, ipAddress, null, purpose.ToString(), now));
            await dbContext.SaveChangesAsync(cancellationToken);
            return new SendOtpResult(retryResult.ReqId);
        }

        var challenge = new OtpChallenge(
            destination,
            purpose,
            now,
            now.AddMinutes(_options.OtpLifetimeMinutes),
            _options.OtpMaxAttempts,
            ipAddress);

        var providerResult = await otpProvider.SendAsync(
            new Msg91OtpSendRequest(destination, purpose.ToString()),
            cancellationToken);
        challenge.AttachProviderRequestId(providerResult.ReqId);

        dbContext.OtpChallenges.Add(challenge);
        dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_REQUESTED", "OtpChallenge", challenge.PublicId.ToString(), null, null, ipAddress, null, purpose.ToString(), now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SendOtpResult(providerResult.ReqId);
    }

    public async Task<OtpVerificationResult> VerifyAsync(VerifyOtpRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.Now;
        var destination = NormalizeMobileDestination(Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile)));
        var code = Require(request.Code, "OTP code is required.", nameof(request.Code));
        var reqId = Require(request.ReqId, "The OTP request id is required.", nameof(request.ReqId));
        if (string.IsNullOrWhiteSpace(request.Device.DeviceIdentifier))
            throw new ValidationAppException("Device identifier is required.", nameof(request.Device.DeviceIdentifier));
        var challenge = await dbContext.OtpChallenges
            .Where(x => x.Destination == destination && x.Purpose == request.Purpose)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (challenge is null || !challenge.CanAttempt(now))
        {
            await WriteAuditAsync(null, "AUTH_OTP_FAILED", destination, request.Device.IpAddress, request.Device.UserAgent, "Missing, expired, consumed, or attempts exhausted", cancellationToken);
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");
        }

        // The client-returned reqId must match the id persisted on the challenge so a
        // verification can never be replayed against another user's OTP request.
        if (!string.Equals(challenge.ReqId, reqId, StringComparison.Ordinal))
        {
            await WriteAuditAsync(null, "AUTH_OTP_FAILED", destination, request.Device.IpAddress, request.Device.UserAgent, "ReqId mismatch", cancellationToken);
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");
        }

        // MSG91 is the authoritative verifier of the OTP. A rejection (wrong code,
        // expired request, etc.) surfaces as a provider rejection; record the failed
        // attempt so the local attempt limit stays accurate.
        Msg91OtpVerifyResult verifyResult;
        Msg91OtpValidationResult validationResult;
        try
        {
            verifyResult = await otpProvider.VerifyAsync(
                new Msg91OtpVerifyRequest(challenge.ReqId!, destination, code, request.Purpose.ToString()),
                cancellationToken);
            validationResult = await otpProvider.ValidateAccessTokenAsync(
                new Msg91OtpAccessTokenRequest(verifyResult.AccessToken, destination, request.Purpose.ToString()),
                cancellationToken);
        }
        catch (OtpProviderRejectedException)
        {
            challenge.RecordFailedAttempt();
            dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_FAILED", "OtpChallenge", challenge.PublicId.ToString(), null, null, request.Device.IpAddress, request.Device.UserAgent, "Invalid code", now));
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        // The provider-attested identifier returned with the verified access token
        // must match the canonical challenge identity. This prevents a code minted
        // for one mobile number being used to authenticate another representation.
        var attestedIdentifier = NormalizeIdentifier(validationResult.VerifiedIdentifier);
        var expectedIdentifier = NormalizeIdentifier(challenge.Destination);
        if (string.IsNullOrEmpty(attestedIdentifier) || !string.Equals(attestedIdentifier, expectedIdentifier, StringComparison.Ordinal))
        {
            challenge.RecordFailedAttempt();
            dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_FAILED", "OtpChallenge", challenge.PublicId.ToString(), null, null, request.Device.IpAddress, request.Device.UserAgent, "Provider identity mismatch", now));
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new OtpProviderRejectedException("The OTP verification could not be confirmed.");
        }

        // The mobile OTP has been provider-verified and the challenge is now consumed so the
        // single-use code can never be replayed. The server decides the outcome: an existing
        // active user is authenticated normally, while a Registration-purpose verification on a
        // mobile with no account reports an onboarding-required outcome instead of silently
        // creating a customer here (creation happens only through the dedicated completion op).
        challenge.Consume(now);
        var mobileLookup = MobileLookupValues(destination);
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => mobileLookup.Count > 0 && mobileLookup.Contains(x.Mobile), cancellationToken);

        if (user is null && request.Purpose == OtpPurpose.Registration)
        {
            // Case 2: the verified mobile belongs to no user — the client is eligible for
            // customer onboarding. Persist the consumed challenge (ConsumedAt + ReqId) so the
            // completion operation can prove this OTP was verified here, then hand the verified
            // destination and reqId back to the client. No account and no session are created by
            // generic OTP verification.
            await dbContext.SaveChangesAsync(cancellationToken);
            return new OtpVerificationResult(true, null, destination, challenge.ReqId);
        }

        if (user is null || !user.IsActive)
        {
            await WriteAuditAsync(user?.Id, "AUTH_OTP_DENIED", destination, request.Device.IpAddress, request.Device.UserAgent, user is null ? "Account not found" : "Inactive account", cancellationToken);
            throw new UnauthorizedAppException("Authentication failed.");
        }

        var session = new UserSession(user.Id, HashDevice(request.Device.DeviceIdentifier), request.Device.DeviceName, request.Device.Platform, request.Device.IpAddress, request.Device.UserAgent, now);
        dbContext.UserSessions.Add(session);
        user.RecordLogin(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        var authUser = await user.ToAuthUserResultAsync(dbContext, cancellationToken);
        var tokens = tokenService.Create(
            user,
            session,
            authUser.Roles,
            authUser.Permissions,
            authUser.BranchIds,
            now);
        dbContext.RefreshTokens.Add(new RefreshToken(user.Id, tokenService.HashRefreshToken(tokens.RefreshToken), tokens.RefreshTokenExpiresAt, session.Id, now));
        dbContext.AddAuditLog(new AuditLog(user.Id, "AUTH_OTP_LOGIN", "UserSession", session.PublicId.ToString(), null, null, request.Device.IpAddress, request.Device.UserAgent, request.Purpose.ToString(), now));

        notificationEventWriter.Add(new NotificationEventRequest(
            user.Id,
            NotificationEventTypes.AuthenticationSucceeded,
            $"authentication:{session.PublicId:N}:succeeded",
            new Dictionary<string, string>
            {
                ["message"] = "You signed in successfully."
            },
            "/"));
        await dbContext.SaveChangesAsync(cancellationToken);
        return new OtpVerificationResult(false, new AuthSessionResult(authUser, tokens), null, null);
    }

    public async Task<AuthUserResult> VerifyMobileChangeOtpAsync(VerifyOtpRequest request, long userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.Now;
        var destination = NormalizeMobileDestination(Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile)));
        var code = Require(request.Code, "OTP code is required.", nameof(request.Code));
        var reqId = Require(request.ReqId, "The OTP request id is required.", nameof(request.ReqId));
        if (request.Purpose != OtpPurpose.EmailVerification)
            throw new ValidationAppException("The OTP purpose is invalid.", nameof(request.Purpose));

        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive || !string.Equals(user.PendingMobile, destination, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");

        var challenge = await dbContext.OtpChallenges
            .Where(x => x.Destination == destination && x.Purpose == OtpPurpose.EmailVerification)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (challenge is null || !challenge.CanAttempt(now) || !string.Equals(challenge.ReqId, reqId, StringComparison.Ordinal))
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");

        await VerifyAndConsumeAsync(challenge, destination, code, "AUTH_MOBILE_VERIFY_FAILED", request.Device, now, cancellationToken);
        var lookup = MobileLookupValues(destination);
        if (await dbContext.Users.AnyAsync(x => x.Id != userId && lookup.Contains(x.Mobile), cancellationToken))
            throw new ConflictException("That mobile number is already in use.");
        user.ConfirmPendingMobileChange();
        dbContext.AddAuditLog(new AuditLog(user.Id, "AUTH_MOBILE_VERIFIED", "User", user.PublicId.ToString(), null, null, request.Device.IpAddress, request.Device.UserAgent, destination, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await user.ToAuthUserResultAsync(dbContext, cancellationToken);
    }

    public async Task<AuthUserResult> VerifyEmailOtpAsync(VerifyEmailChangeRequest request, long userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.Now;
        var code = Require(request.Code, "OTP code is required.", nameof(request.Code));
        var reqId = Require(request.ReqId, "The OTP request id is required.", nameof(request.ReqId));

        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive)
            throw new UnauthorizedAppException();

        var pendingEmail = user.PendingEmail;
        if (string.IsNullOrWhiteSpace(pendingEmail))
            throw new BusinessRuleException("There is no pending email to verify.");

        var challenge = await dbContext.OtpChallenges
            .Where(x => x.Destination == pendingEmail && x.Purpose == OtpPurpose.EmailVerification)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (challenge is null || !challenge.CanAttempt(now))
        {
            await WriteAuditAsync(user.Id, "AUTH_EMAIL_VERIFY_FAILED", pendingEmail, request.Device.IpAddress, request.Device.UserAgent, "Missing, expired, consumed, or attempts exhausted", cancellationToken);
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");
        }
        if (!string.Equals(challenge.ReqId, reqId, StringComparison.Ordinal))
        {
            await WriteAuditAsync(user.Id, "AUTH_EMAIL_VERIFY_FAILED", pendingEmail, request.Device.IpAddress, request.Device.UserAgent, "ReqId mismatch", cancellationToken);
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");
        }

        await VerifyAndConsumeAsync(challenge, pendingEmail, code, "AUTH_EMAIL_VERIFY_FAILED", request.Device, now, cancellationToken);

        if (await dbContext.Users.AnyAsync(x => x.Id != userId && x.Email == pendingEmail, cancellationToken))
            throw new ConflictException("An account already exists for this email.");

        user.ConfirmPendingEmailChange(now);
        dbContext.AddAuditLog(new AuditLog(user.Id, "AUTH_EMAIL_VERIFIED", "User", user.PublicId.ToString(), null, null, request.Device.IpAddress, request.Device.UserAgent, pendingEmail, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await user.ToAuthUserResultAsync(dbContext, cancellationToken);
    }

    public async Task VerifyResetOtpAsync(VerifyResetOtpRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.Now;
        var destination = NormalizeMobileDestination(request.Destination);
        var code = Require(request.Code, "OTP code is required.", nameof(request.Code));
        var reqId = Require(request.ReqId, "The OTP request id is required.", nameof(request.ReqId));

        var challenge = await dbContext.OtpChallenges
            .Where(x => x.Destination == destination && x.Purpose == OtpPurpose.PasswordReset)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (challenge is null || !challenge.CanAttempt(now))
        {
            await WriteAuditAsync(null, "AUTH_RESET_VERIFY_FAILED", destination, request.Device.IpAddress, request.Device.UserAgent, "Missing, expired, consumed, or attempts exhausted", cancellationToken);
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");
        }
        if (!string.Equals(challenge.ReqId, reqId, StringComparison.Ordinal))
        {
            await WriteAuditAsync(null, "AUTH_RESET_VERIFY_FAILED", destination, request.Device.IpAddress, request.Device.UserAgent, "ReqId mismatch", cancellationToken);
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");
        }

        // Consumes the challenge so the OTP cannot be replayed, then the reset
        // endpoint re-verifies the same reqId against the consumed challenge to
        // authorize the password change.
        await VerifyAndConsumeAsync(challenge, destination, code, "AUTH_RESET_VERIFY_FAILED", request.Device, now, cancellationToken);
        await WriteAuditAsync(null, "AUTH_RESET_OTP_VERIFIED", challenge.PublicId.ToString(), request.Device.IpAddress, request.Device.UserAgent, null, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task VerifyAndConsumeAsync(
        OtpChallenge challenge,
        string destination,
        string code,
        string failureAction,
        DeviceInfo device,
        DateTime now,
        CancellationToken cancellationToken)
    {
        Msg91OtpVerifyResult verifyResult;
        Msg91OtpValidationResult validationResult;
        try
        {
            verifyResult = await otpProvider.VerifyAsync(
                new Msg91OtpVerifyRequest(challenge.ReqId!, destination, code, challenge.Purpose.ToString()),
                cancellationToken);
            validationResult = await otpProvider.ValidateAccessTokenAsync(
                new Msg91OtpAccessTokenRequest(verifyResult.AccessToken, destination, challenge.Purpose.ToString()),
                cancellationToken);
        }
        catch (OtpProviderRejectedException)
        {
            challenge.RecordFailedAttempt();
            dbContext.AddAuditLog(new AuditLog(null, failureAction, "OtpChallenge", challenge.PublicId.ToString(), null, null, device.IpAddress, device.UserAgent, "Invalid code", now));
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }

        var attestedIdentifier = NormalizeIdentifier(validationResult.VerifiedIdentifier);
        var expectedIdentifier = NormalizeIdentifier(destination);
        if (string.IsNullOrEmpty(attestedIdentifier) || !string.Equals(attestedIdentifier, expectedIdentifier, StringComparison.Ordinal))
        {
            challenge.RecordFailedAttempt();
            dbContext.AddAuditLog(new AuditLog(null, failureAction, "OtpChallenge", challenge.PublicId.ToString(), null, null, device.IpAddress, device.UserAgent, "Provider identity mismatch", now));
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new OtpProviderRejectedException("The OTP verification could not be confirmed.");
        }

        challenge.Consume(now);
    }

    private async Task WriteAuditAsync(long? userId, string action, string entityId, string? ipAddress, string? userAgent, string? reason, CancellationToken cancellationToken)
    {
        dbContext.AddAuditLog(new AuditLog(userId, action, "OtpChallenge", entityId, null, null, ipAddress, userAgent, reason, timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Require(string? value, string message, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ValidationAppException(message, field) : value.Trim();

    private static string? NormalizeEmail(string? value) =>
        string.IsNullOrWhiteSpace(value) || !value.Contains('@') ? null : value.Trim().ToLowerInvariant();

    private static string NormalizeDestination(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains('@') ? normalized : normalized;
    }

    /// <summary>
    /// Canonicalizes a mobile destination to the shared E.164 form (<c>+91XXXXXXXXXX</c>)
    /// while leaving email destinations untouched, so challenge destinations and lookups
    /// use one consistent identity format.
    /// </summary>
    private static string NormalizeMobileDestination(string value)
    {
        var canonical = IndiaMobileNumber.Canonicalize(value);
        return canonical ?? value.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Candidate identities for a canonical mobile destination: the E.164 form and the
    /// 10-digit national form, so legacy rows stored without a country code continue
    /// to resolve without a data migration.
    /// </summary>
    private static IReadOnlyList<string> MobileLookupValues(string destination)
    {
        var canonical = IndiaMobileNumber.Canonicalize(destination);
        if (canonical is null) return Array.Empty<string>();
        var national = IndiaMobileNumber.ToNational(canonical);
        return national is null || national == canonical
            ? new[] { canonical }
            : new[] { canonical, national };
    }

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Contains('@')) return value.Trim().ToLowerInvariant();
        return IndiaMobileNumber.Canonicalize(value);
    }

    private static string HashDevice(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal static class IdentityMappings
{
    public static AuthUserResult ToAuthUserResult(this User user)
    {
        var activeAssignments = user.UserRoles
            .Where(x => x.Role.IsActive)
            .ToArray();
        var roles = activeAssignments
            .Select(x => x.Role.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var permissions = activeAssignments
            .SelectMany(x => x.Role.RolePermissions)
            .Select(x => x.Permission.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var branchIds = activeAssignments
            .Where(x => x.BranchId.HasValue)
            .Select(x => x.BranchId!.Value)
            .Distinct()
            .ToArray();

        return new AuthUserResult(
            user.PublicId,
            user.DisplayName,
            user.Email,
            user.Mobile,
            user.HasVerifiedEmail,
            user.HasPassword,
            user.PendingEmail,
            user.PendingMobile,
            roles,
            permissions,
            branchIds);
    }

    /// <summary>
    /// Resolves the display metadata (code and name) for every branch on the user's
    /// assignments so sessions carry authoritative branch labels without the client
    /// needing a privileged branch-catalogue endpoint. The pure synchronous mapping
    /// above remains available for permission-only checks that must not hit the database.
    /// </summary>
    public static async Task<AuthUserResult> ToAuthUserResultAsync(
        this User user,
        DoodhDirectDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var result = user.ToAuthUserResult();
        return await result.ResolveBranchDetailsAsync(dbContext, cancellationToken);
    }

    public static async Task<AuthUserResult> ResolveBranchDetailsAsync(
        this AuthUserResult result,
        DoodhDirectDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var branchIds = result.BranchIds;
        if (branchIds.Count == 0)
            return result with { BranchDetails = Array.Empty<AuthUserBranchResult>() };

        var ids = branchIds.ToArray();
        var branches = await dbContext.Branches
            .Where(x => ids.Contains(x.Id))
            .OrderBy(x => x.Id)
            .Select(x => new AuthUserBranchResult(x.Id, x.Code, x.Name))
            .ToListAsync(cancellationToken);
        return result with { BranchDetails = branches };
    }
}
