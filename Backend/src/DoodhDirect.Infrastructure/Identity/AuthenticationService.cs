using System.Data;
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
using Microsoft.EntityFrameworkCore.Storage;

namespace DoodhDirect.Infrastructure.Identity;

public sealed class AuthenticationService(
    DoodhDirectDbContext dbContext,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    IIndiaTimeProvider timeProvider,
    INotificationEventWriter notificationEventWriter,
    IOtpService otpService) : IAuthenticationService
{
    public async Task<AuthSessionResult> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var displayName = Require(request.DisplayName, "Display name is required.", nameof(request.DisplayName));
        var password = Require(request.Password, "Password is required.", nameof(request.Password));
        var email = NormalizeEmail(request.Email);
        var mobile = NormalizeMobile(request.Mobile);
        if (email is null && mobile is null)
            throw new ValidationAppException("Email or mobile is required.", nameof(request.Email));

        await EnsureContactIsAvailableAsync(email, mobile, cancellationToken);
        var now = timeProvider.Now;
        var user = new User(UserType.Customer);
        user.SetProfile(displayName);
        user.SetContact(mobile, email);
        user.SetPasswordHash(passwordHasher.Hash(password));
        var customerRole = await GetCustomerRoleAsync(cancellationToken);
        user.AssignRole(customerRole);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await CreateSessionAsync(user, request.Device, now, "REGISTRATION", cancellationToken);
        return result;
    }

    public async Task<AuthSessionResult> LoginAsync(
        PasswordLoginRequest request,
        CancellationToken cancellationToken)
    {
        var login = Require(request.Login, "Login is required.", nameof(request.Login));
        var normalizedEmail = NormalizeEmail(login);
        var mobileLookup = MobileLookupValues(login);
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => (normalizedEmail != null && x.Email == normalizedEmail) || (mobileLookup.Count > 0 && mobileLookup.Contains(x.Mobile)), cancellationToken);

        if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash) || !passwordHasher.Verify(user.PasswordHash, request.Password))
        {
            await WriteAuditAsync(null, "AUTH_LOGIN_FAILED", "User", login, request.Device.IpAddress, request.Device.UserAgent, "Invalid credentials", cancellationToken);
            throw new UnauthorizedAppException();
        }

        if (!user.IsActive)
        {
            await WriteAuditAsync(user.Id, "AUTH_LOGIN_DENIED", "User", user.PublicId.ToString(), request.Device.IpAddress, request.Device.UserAgent, "Inactive account", cancellationToken);
            throw new UnauthorizedAppException();
        }

        return await CreateSessionAsync(user, request.Device, timeProvider.Now, "PASSWORD_LOGIN", cancellationToken);
    }

    public async Task<AuthSessionResult> RefreshAsync(
        string refreshToken,
        DeviceInfo device,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new UnauthorizedAppException();
        ValidateDevice(device);

        var now = timeProvider.Now;
        var tokenHash = tokenService.HashRefreshToken(refreshToken);
        var storedToken = await dbContext.RefreshTokens
            .Include(x => x.User).ThenInclude(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .Include(x => x.Session)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (storedToken?.Session is not null && storedToken.RevokedAt is not null && storedToken.ReplacedByTokenHash is not null)
        {
            await RevokeSessionAsync(storedToken.Session, now, "REFRESH_TOKEN_REUSE", cancellationToken);
            await WriteAuditAsync(storedToken.UserId, "AUTH_REFRESH_REUSE", "UserSession", storedToken.Session.PublicId.ToString(), device.IpAddress, device.UserAgent, "Previously rotated refresh token presented", cancellationToken);
            throw new UnauthorizedAppException();
        }

        if (storedToken is null || storedToken.Session is null || !storedToken.IsActive(now))
        {
            await WriteAuditAsync(storedToken?.UserId, "AUTH_REFRESH_FAILED", "RefreshToken", tokenHash, device.IpAddress, device.UserAgent, "Invalid, expired, or revoked token", cancellationToken);
            throw new UnauthorizedAppException();
        }

        var session = storedToken.Session;
        if (!session.IsActive || !storedToken.User.IsActive || session.DeviceIdentifierHash != HashDevice(device.DeviceIdentifier))
        {
            if (!session.IsActive || !storedToken.User.IsActive)
                throw new UnauthorizedAppException();

            await WriteAuditAsync(storedToken.UserId, "AUTH_REFRESH_DENIED", "UserSession", session.PublicId.ToString(), device.IpAddress, device.UserAgent, "Device binding mismatch", cancellationToken);
            throw new UnauthorizedAppException();
        }

        var authUser = await storedToken.User.ToAuthUserResultAsync(dbContext, cancellationToken);
        var tokens = tokenService.Create(
            storedToken.User,
            session,
            authUser.Roles,
            authUser.Permissions,
            authUser.BranchIds,
            now);
        storedToken.Revoke(now, tokenService.HashRefreshToken(tokens.RefreshToken));
        session.Touch(now, device.IpAddress);
        dbContext.RefreshTokens.Add(new RefreshToken(storedToken.UserId, tokenService.HashRefreshToken(tokens.RefreshToken), tokens.RefreshTokenExpiresAt, session.Id, now));
        await WriteAuditAsync(storedToken.UserId, "AUTH_REFRESH_ROTATED", "UserSession", session.PublicId.ToString(), device.IpAddress, device.UserAgent, null, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new AuthSessionResult(authUser, tokens);
    }

    public async Task LogoutAsync(Guid sessionPublicId, long userId, CancellationToken cancellationToken)
    {
        var session = await dbContext.UserSessions.SingleOrDefaultAsync(x => x.PublicId == sessionPublicId && x.UserId == userId, cancellationToken);
        if (session is null)
            throw new UnauthorizedAppException();

        var now = timeProvider.Now;
        await RevokeSessionAsync(session, now, "USER_LOGOUT", cancellationToken);
        await WriteAuditAsync(userId, "AUTH_LOGOUT", "UserSession", session.PublicId.ToString(), null, null, "User logout", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthUserResult> GetCurrentUserAsync(long userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null || !user.IsActive)
            throw new UnauthorizedAppException();
        return await user.ToAuthUserResultAsync(dbContext, cancellationToken);
    }

    public async Task SetPasswordAsync(long userId, SetPasswordRequest request, CancellationToken cancellationToken)
    {
        var password = Require(request.NewPassword, "Password is required.", nameof(request.NewPassword));
        var user = await dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive)
            throw new UnauthorizedAppException();
        if (user.HasPassword)
            throw new BusinessRuleException("This account already has a password set.");

        user.SetPasswordHash(passwordHasher.Hash(password));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangePasswordAsync(long userId, Guid currentSessionPublicId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var newPassword = Require(request.NewPassword, "New password is required.", nameof(request.NewPassword));
        var user = await dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive)
            throw new UnauthorizedAppException();
        if (string.IsNullOrWhiteSpace(user.PasswordHash) || !passwordHasher.Verify(user.PasswordHash, request.CurrentPassword))
            throw new UnauthorizedAppException("The current password is incorrect.");

        user.SetPasswordHash(passwordHasher.Hash(newPassword));
        var now = timeProvider.Now;
        var otherSessions = await dbContext.UserSessions
            .Where(x => x.UserId == userId && x.PublicId != currentSessionPublicId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in otherSessions)
            session.Revoke(now, "PASSWORD_CHANGED");

        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(userId, "AUTH_PASSWORD_CHANGED", "User", user.PublicId.ToString(), null, null, "Password changed by user", cancellationToken);
    }

    public async Task<SendOtpResult> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var mobile = NormalizeMobile(Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile)))
            ?? throw new ValidationAppException("A valid mobile number is required.", nameof(request.Mobile));
        var mobileLookup = MobileLookupValues(mobile);
        var user = await dbContext.Users
            .SingleOrDefaultAsync(x => mobileLookup.Contains(x.Mobile), cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive || string.IsNullOrWhiteSpace(user.Mobile))
            throw new UnauthorizedAppException();

        // Password reset is deliberately mobile-only. This explicit purpose routes through
        // the real MSG91 widget provider; SMTP is reserved for email transactions.
        return await otpService.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.PasswordReset, request.IpAddress),
            cancellationToken);
    }

    public async Task<AuthSessionResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var mobile = NormalizeMobile(Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile)))
            ?? throw new ValidationAppException("A valid mobile number is required.", nameof(request.Mobile));
        var reqId = Require(request.ReqId, "The OTP request id is required.", nameof(request.ReqId));
        ValidateDevice(request.Device);

        var mobileLookup = MobileLookupValues(mobile);
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => mobileLookup.Contains(x.Mobile), cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive)
            throw new UnauthorizedAppException();

        // The client can submit only the reqId. The server authorizes the reset from the
        // consumed PasswordReset challenge created for this canonical mobile.
        var now = timeProvider.Now;
        var challenge = await dbContext.OtpChallenges
            .Where(x => x.Destination == mobile && x.Purpose == OtpPurpose.PasswordReset)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (challenge is null ||
            !challenge.CanConsumePasswordReset(now) ||
            !string.Equals(challenge.ReqId, reqId, StringComparison.Ordinal))
            throw new UnauthorizedAppException("The OTP has not been verified or has expired.");

        // Consume the verified reset authorization before changing the password. The
        // tracked entity and transaction make the ReqId one-time for password reset.
        challenge.ConsumePasswordReset(now);
        user.SetPasswordHash(passwordHasher.Hash(request.NewPassword));

        var otherSessions = await dbContext.UserSessions
            .Where(x => x.UserId == user.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var session in otherSessions)
            session.Revoke(now, "PASSWORD_RESET");

        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(user.Id, "AUTH_PASSWORD_RESET", "User", user.PublicId.ToString(), request.Device.IpAddress, request.Device.UserAgent, "Password reset via OTP", cancellationToken);
        return await CreateSessionAsync(user, request.Device, now, "PASSWORD_RESET_LOGIN", cancellationToken);
    }

    public async Task<AuthSessionResult> CompleteOtpRegistrationAsync(
        CompleteOtpRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var reqId = Require(request.ReqId, "The OTP request id is required.", nameof(request.ReqId));
        var password = Require(request.NewPassword, "Password is required.", nameof(request.NewPassword));
        ValidateDevice(request.Device);

        var destination = NormalizeMobile(Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile)))
            ?? throw new ValidationAppException("A valid mobile number is required.", nameof(request.Mobile));
        var mobileLookup = MobileLookupValues(destination);

        var now = timeProvider.Now;
        AuthSessionResult result = null!;

        await ExecuteAtomicAsync(async () =>
        {
            // The client must have verified the Registration OTP first (generic verify-otp with
            // Registration purpose), which consumed the challenge and returned the onboarding
            // outcome. Matching the reqId against the consumed challenge proves that prior
            // provider verification without re-calling MSG91 — an OTP code is single-use, so a
            // consumed challenge can never be replayed to authorize a second account.
            var challenge = await dbContext.OtpChallenges
                .Where(x => x.Destination == destination && x.Purpose == OtpPurpose.Registration)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (challenge is null || challenge.ConsumedAt is null || !string.Equals(challenge.ReqId, reqId, StringComparison.Ordinal))
                throw new UnauthorizedAppException("The OTP has not been verified or has expired.");

            // Race-condition guard: an account may have been created after the OTP was verified
            // (e.g. a second completion). The unique filtered User.Mobile index is the backstop
            // if two completions slip past this check on SQL Server.
            var existing = await dbContext.Users
                .SingleOrDefaultAsync(x => mobileLookup.Contains(x.Mobile), cancellationToken);
            if (existing is not null)
                throw new ConflictException("An account already exists for this mobile number. Please sign in instead.");

            // The server decides the account type and role; the client can never supply them.
            var customerRole = await GetCustomerRoleAsync(cancellationToken);
            var user = new User(UserType.Customer);
            user.SetContact(destination, null);
            user.SetPasswordHash(passwordHasher.Hash(password));
            user.AssignRole(customerRole);
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync(cancellationToken);

            await WriteAuditAsync(user.Id, "AUTH_OTP_ONBOARDED", "User", user.PublicId.ToString(), request.Device.IpAddress, request.Device.UserAgent, "Customer created via verified mobile OTP", cancellationToken);
            result = await CreateSessionAsync(user, request.Device, now, "REGISTRATION", cancellationToken);
        }, cancellationToken);

        return result;
    }

    public async Task<EmailChangeRequestedResult> RequestEmailChangeAsync(long userId, RequestEmailChangeRequest request, CancellationToken cancellationToken)
    {
        var newEmail = NormalizeEmail(request.NewEmail)
            ?? throw new ValidationAppException("A valid email address is required.", nameof(request.NewEmail));
        var user = await dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive)
            throw new UnauthorizedAppException();
        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("The new email is the same as the current email.");
        if (await dbContext.Users.AnyAsync(x => x.Id != userId && x.Email == newEmail, cancellationToken))
            throw new ConflictException("An account already exists for this email.");

        user.RequestPendingEmailChange(newEmail);
        await dbContext.SaveChangesAsync(cancellationToken);

        var sendResult = await otpService.SendEmailOtpAsync(newEmail, request.IpAddress, cancellationToken);
        await WriteAuditAsync(userId, "AUTH_EMAIL_CHANGE_REQUESTED", "User", user.PublicId.ToString(), request.IpAddress, null, $"Pending email staged: {newEmail}", cancellationToken);
        return new EmailChangeRequestedResult(sendResult.ReqId, newEmail);
    }

    public async Task<MobileChangeRequestedResult> RequestMobileChangeAsync(long userId, RequestMobileChangeRequest request, CancellationToken cancellationToken)
    {
        var newMobile = NormalizeMobile(request.NewMobile)
            ?? throw new ValidationAppException("A valid mobile number is required.", nameof(request.NewMobile));
        var user = await dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new UnauthorizedAppException();
        if (!user.IsActive) throw new UnauthorizedAppException();
        if (string.Equals(user.Mobile, newMobile, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("The new mobile number is the same as the current mobile number.");
        var lookup = MobileLookupValues(newMobile);
        if (await dbContext.Users.AnyAsync(x => x.Id != userId && lookup.Contains(x.Mobile), cancellationToken))
            throw new ConflictException("An account already exists for this mobile number.");

        user.RequestPendingMobileChange(newMobile);
        await dbContext.SaveChangesAsync(cancellationToken);
        var sendResult = await otpService.SendAsync(new SendOtpRequest(newMobile, OtpPurpose.EmailVerification, request.IpAddress), cancellationToken);
        await WriteAuditAsync(userId, "AUTH_MOBILE_CHANGE_REQUESTED", "User", user.PublicId.ToString(), request.IpAddress, null, $"Pending mobile staged: {newMobile}", cancellationToken);
        return new MobileChangeRequestedResult(sendResult.ReqId, newMobile);
    }

    private async Task<AuthSessionResult> CreateSessionAsync(User user, DeviceInfo device, DateTime now, string action, CancellationToken cancellationToken)
    {
        ValidateDevice(device);
        var session = new UserSession(user.Id, HashDevice(device.DeviceIdentifier), device.DeviceName, device.Platform, device.IpAddress, device.UserAgent, now);
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
        dbContext.AddAuditLog(new AuditLog(
            user.Id,
            action,
            "UserSession",
            session.PublicId.ToString(),
            null,
            null,
            device.IpAddress,
            device.UserAgent,
            null,
            now));
        if (string.Equals(action, "REGISTRATION", StringComparison.Ordinal))
        {
            notificationEventWriter.Add(new NotificationEventRequest(
                user.Id,
                NotificationEventTypes.RegistrationCompleted,
                $"registration:{user.PublicId:N}:completed",
                new Dictionary<string, string>
                {
                    ["message"] = "Your DoodhDirect registration is complete."
                },
                "/"));
        }

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
        return new AuthSessionResult(authUser, tokens);
    }

    private async Task ExecuteAtomicAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task RevokeSessionAsync(UserSession session, DateTime now, string reason, CancellationToken cancellationToken)
    {
        session.Revoke(now, reason);
        var tokens = await dbContext.RefreshTokens
            .Where(x => x.SessionId == session.Id && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
            token.Revoke(now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Role> GetCustomerRoleAsync(CancellationToken cancellationToken) =>
        await dbContext.Roles
            .Include(x => x.RolePermissions)
                .ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Code == AuthorizationCodes.Customer, cancellationToken)
        ?? throw new InvalidOperationException("The canonical CUSTOMER role has not been seeded.");

    private async Task EnsureContactIsAvailableAsync(string? email, string? mobile, CancellationToken cancellationToken)
    {
        if (email is not null && await dbContext.Users.AnyAsync(x => x.Email == email, cancellationToken))
            throw new ConflictException("An account already exists for this email.");
        if (mobile is not null)
        {
            var mobileLookup = MobileLookupValues(mobile);
            if (mobileLookup.Count > 0 && await dbContext.Users.AnyAsync(x => mobileLookup.Contains(x.Mobile), cancellationToken))
                throw new ConflictException("An account already exists for this mobile number.");
        }
    }

    private async Task WriteAuditAsync(long? userId, string action, string entityType, string entityId, string? ipAddress, string? userAgent, string? reason, CancellationToken cancellationToken)
    {
        dbContext.AddAuditLog(new AuditLog(userId, action, entityType, entityId, null, null, ipAddress, userAgent, reason, timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateDevice(DeviceInfo device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceIdentifier))
            throw new ValidationAppException("Device identifier is required.", nameof(device.DeviceIdentifier));
    }

    private static string Require(string? value, string message, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ValidationAppException(message, field) : value.Trim();

    private static string? NormalizeEmail(string? value) => string.IsNullOrWhiteSpace(value) || !value.Contains('@') ? null : value.Trim().ToLowerInvariant();

    /// <summary>
    /// Canonicalizes a mobile number to the shared E.164 form <c>+91XXXXXXXXXX</c>.
    /// Email addresses are not mobile numbers and return <c>null</c>.
    /// </summary>
    private static string? NormalizeMobile(string? value) =>
        IndiaMobileNumber.Canonicalize(value);

    /// <summary>
    /// Candidate mobile identities for a login value: the canonical E.164 form and
    /// the 10-digit national form. Both are matched against <c>User.Mobile</c> so
    /// legacy rows stored without a country code continue to resolve while new
    /// rows are stored canonically.
    /// </summary>
    private static IReadOnlyList<string> MobileLookupValues(string? value)
    {
        var canonical = IndiaMobileNumber.Canonicalize(value);
        if (canonical is null) return Array.Empty<string>();
        var national = IndiaMobileNumber.ToNational(canonical);
        return national is null || national == canonical
            ? new[] { canonical }
            : new[] { canonical, national };
    }

    private static string HashDevice(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
