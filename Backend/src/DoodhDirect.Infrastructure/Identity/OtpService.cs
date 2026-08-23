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
    IPasswordHasher passwordHasher,
    IOtpDeliveryService delivery,
    IIndiaTimeProvider timeProvider,
    ITokenService tokenService,
    IOptions<IdentityOptions> options,
    INotificationEventWriter notificationEventWriter) : IOtpService
{
    private readonly IdentityOptions _options = options.Value;

    public async Task SendAsync(SendOtpRequest request, CancellationToken cancellationToken)
    {
        var destination = Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile));
        var now = timeProvider.Now;
        var windowStart = now.AddMinutes(-_options.OtpRateLimitWindowMinutes);
        var requestCount = await dbContext.OtpChallenges
            .CountAsync(x => x.Destination == destination && x.Purpose == request.Purpose && x.CreatedAt >= windowStart, cancellationToken);
        if (requestCount >= _options.OtpRequestsPerWindow)
        {
            await WriteAuditAsync(null, "AUTH_OTP_RATE_LIMITED", destination, request.IpAddress, null, request.Purpose.ToString(), cancellationToken);
            throw new RateLimitAppException();
        }

        var code = RandomNumberGenerator.GetInt32(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var challenge = new OtpChallenge(
            destination,
            request.Purpose,
            passwordHasher.Hash(code),
            now,
            now.AddMinutes(_options.OtpLifetimeMinutes),
            _options.OtpMaxAttempts,
            request.IpAddress);
        await delivery.SendAsync(destination, code, cancellationToken);
        dbContext.OtpChallenges.Add(challenge);
        dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_REQUESTED", "OtpChallenge", challenge.PublicId.ToString(), null, null, request.IpAddress, null, request.Purpose.ToString(), now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthSessionResult> VerifyAsync(VerifyOtpRequest request, CancellationToken cancellationToken)
    {
        var now = timeProvider.Now;
        var destination = Require(request.Mobile, "Mobile number is required.", nameof(request.Mobile));
        var code = Require(request.Code, "OTP code is required.", nameof(request.Code));
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

        if (!passwordHasher.Verify(challenge.CodeHash, code))
        {
            challenge.RecordFailedAttempt();
            dbContext.AddAuditLog(new AuditLog(null, "AUTH_OTP_FAILED", "OtpChallenge", challenge.PublicId.ToString(), null, null, request.Device.IpAddress, request.Device.UserAgent, "Invalid code", now));
            await dbContext.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAppException("The OTP is invalid or has expired.");
        }

        challenge.Consume(now);
        var registeredUser = false;
        var user = await dbContext.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role).ThenInclude(x => x.RolePermissions).ThenInclude(x => x.Permission)
            .SingleOrDefaultAsync(x => x.Mobile == destination, cancellationToken);
        if (user is null && request.Purpose == OtpPurpose.Registration)
        {
            var customerRole = await dbContext.Roles
                .Include(x => x.RolePermissions)
                    .ThenInclude(x => x.Permission)
                .SingleOrDefaultAsync(
                    x => x.Code == AuthorizationCodes.Customer,
                    cancellationToken)
                ?? throw new InvalidOperationException(
                    "The canonical CUSTOMER role has not been seeded.");

            user = new User(UserType.Customer);
            user.SetContact(destination, null);
            user.AssignRole(customerRole);
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync(cancellationToken);
            registeredUser = true;
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

        var authUser = user.ToAuthUserResult();
        var tokens = tokenService.Create(
            user,
            session,
            authUser.Roles,
            authUser.Permissions,
            authUser.BranchIds,
            now);
        dbContext.RefreshTokens.Add(new RefreshToken(user.Id, tokenService.HashRefreshToken(tokens.RefreshToken), tokens.RefreshTokenExpiresAt, session.Id, now));
        dbContext.AddAuditLog(new AuditLog(user.Id, "AUTH_OTP_LOGIN", "UserSession", session.PublicId.ToString(), null, null, request.Device.IpAddress, request.Device.UserAgent, request.Purpose.ToString(), now));
        if (registeredUser)
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

    private async Task WriteAuditAsync(long? userId, string action, string entityId, string? ipAddress, string? userAgent, string? reason, CancellationToken cancellationToken)
    {
        dbContext.AddAuditLog(new AuditLog(userId, action, "OtpChallenge", entityId, null, null, ipAddress, userAgent, reason, timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Require(string? value, string message, string field) =>
        string.IsNullOrWhiteSpace(value) ? throw new ValidationAppException(message, field) : value.Trim();

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
            roles,
            permissions,
            branchIds);
    }
}
