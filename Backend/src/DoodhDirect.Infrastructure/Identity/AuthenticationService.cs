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

namespace DoodhDirect.Infrastructure.Identity;

public sealed class AuthenticationService(
    DoodhDirectDbContext dbContext,
    IPasswordHasher passwordHasher,
    ITokenService tokenService,
    IIndiaTimeProvider timeProvider,
    INotificationEventWriter notificationEventWriter) : IAuthenticationService
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
        var normalizedMobile = NormalizeMobile(login);
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => (normalizedEmail != null && x.Email == normalizedEmail) || (normalizedMobile != null && x.Mobile == normalizedMobile), cancellationToken);

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

        var authUser = storedToken.User.ToAuthUserResult();
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
        return user.ToAuthUserResult();
    }

    private async Task<AuthSessionResult> CreateSessionAsync(User user, DeviceInfo device, DateTime now, string action, CancellationToken cancellationToken)
    {
        ValidateDevice(device);
        var session = new UserSession(user.Id, HashDevice(device.DeviceIdentifier), device.DeviceName, device.Platform, device.IpAddress, device.UserAgent, now);
        dbContext.UserSessions.Add(session);
        user.RecordLogin(now);
        await dbContext.SaveChangesAsync(cancellationToken);

        var authUser = user.ToAuthUserResult();
        var tokens = tokenService.Create(
            user,
            session,
            authUser.Roles,
            authUser.Permissions,
            authUser.BranchIds,
            now);
        dbContext.RefreshTokens.Add(new RefreshToken(user.Id, tokenService.HashRefreshToken(tokens.RefreshToken), tokens.RefreshTokenExpiresAt, session.Id, now));
        dbContext.AuditLogs.Add(new AuditLog(
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
        if (mobile is not null && await dbContext.Users.AnyAsync(x => x.Mobile == mobile, cancellationToken))
            throw new ConflictException("An account already exists for this mobile number.");
    }

    private async Task WriteAuditAsync(long? userId, string action, string entityType, string entityId, string? ipAddress, string? userAgent, string? reason, CancellationToken cancellationToken)
    {
        dbContext.AuditLogs.Add(new AuditLog(userId, action, entityType, entityId, null, null, ipAddress, userAgent, reason, timeProvider.Now));
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

    private static string? NormalizeMobile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Any(char.IsDigit) ? normalized : null;
    }

    private static string HashDevice(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
