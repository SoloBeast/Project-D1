using System.Security.Cryptography;
using System.Text;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class AuthenticationServiceTests
{
    private static readonly DeviceInfo Device = new(
        "test-device-1",
        "Integration test device",
        "test",
        "127.0.0.1",
        "DoodhDirect.Tests");

    [Fact]
    public async Task Registration_CreatesCustomerSessionRefreshTokenAndAudit()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();

        var result = await harness.Authentication.RegisterAsync(
            new RegisterRequest(" Test Customer ", "TEST@EXAMPLE.COM", null, "correct-password", Device),
            CancellationToken.None);

        Assert.Equal("test@example.com", result.User.Email);
        Assert.Contains(AuthorizationCodes.Customer, result.User.Roles);
        Assert.Contains(AuthorizationCodes.ProfileReadOwn, result.User.Permissions);
        Assert.False(string.IsNullOrWhiteSpace(result.Tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.Tokens.RefreshToken));
        Assert.Equal(1, await harness.Db.Users.CountAsync());
        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(1, await harness.Db.RefreshTokens.CountAsync());
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "REGISTRATION");
    }

    [Fact]
    public async Task PasswordLogin_WithInvalidPassword_IsRejectedAndAudited()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "customer@example.com", null, "correct-password", Device),
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.LoginAsync(
                new PasswordLoginRequest("customer@example.com", "wrong-password", Device),
                CancellationToken.None));

        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "AUTH_LOGIN_FAILED" && x.Reason == "Invalid credentials");
    }

    [Fact]
    public async Task PasswordLogin_ForInactiveAccount_IsRejectedAndAudited()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "inactive@example.com", null, "correct-password", Device),
            CancellationToken.None);
        var user = await harness.Db.Users.SingleAsync();
        user.Deactivate();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.LoginAsync(
                new PasswordLoginRequest("inactive@example.com", "correct-password", Device),
                CancellationToken.None));

        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "AUTH_LOGIN_DENIED" && x.Reason == "Inactive account");
    }

    [Fact]
    public async Task Refresh_RotatesTokenAndPersistsReplacementAudit()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "refresh@example.com", null, "correct-password", Device),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await harness.Authentication.RefreshAsync(
            registered.Tokens.RefreshToken,
            Device,
            CancellationToken.None);

        Assert.NotEqual(registered.Tokens.RefreshToken, refreshed.Tokens.RefreshToken);
        var tokens = await harness.Db.RefreshTokens.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].RevokedAt);
        Assert.Equal(harness.Tokens.HashRefreshToken(refreshed.Tokens.RefreshToken), tokens[0].ReplacedByTokenHash);
        Assert.Null(tokens[1].RevokedAt);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_REFRESH_ROTATED");
    }

    [Fact]
    public async Task ReusingRotatedRefreshToken_RevokesEntireSessionAndIsAudited()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "reuse@example.com", null, "correct-password", Device),
            CancellationToken.None);
        await harness.Authentication.RefreshAsync(
            registered.Tokens.RefreshToken,
            Device,
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.RefreshAsync(
                registered.Tokens.RefreshToken,
                Device,
                CancellationToken.None));

        var session = await harness.Db.UserSessions.SingleAsync();
        Assert.NotNull(session.RevokedAt);
        Assert.Equal("REFRESH_TOKEN_REUSE", session.RevocationReason);
        Assert.All(await harness.Db.RefreshTokens.ToListAsync(), token => Assert.NotNull(token.RevokedAt));
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_REFRESH_REUSE");
    }

    [Fact]
    public async Task Logout_RevokesSessionAndActiveRefreshTokens()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "logout@example.com", null, "correct-password", Device),
            CancellationToken.None);
        var user = await harness.Db.Users.SingleAsync();
        var session = await harness.Db.UserSessions.SingleAsync();

        await harness.Authentication.LogoutAsync(session.PublicId, user.Id, CancellationToken.None);

        Assert.False(session.IsActive);
        Assert.Equal("USER_LOGOUT", session.RevocationReason);
        Assert.All(await harness.Db.RefreshTokens.ToListAsync(), token => Assert.NotNull(token.RevokedAt));
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_LOGOUT");
        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.RefreshAsync(registered.Tokens.RefreshToken, Device, CancellationToken.None));
    }
}

public sealed class OtpServiceTests
{
    private static readonly DeviceInfo Device = new(
        "otp-device-1",
        "OTP test device",
        "test",
        "127.0.0.1",
        "DoodhDirect.Tests");

    [Fact]
    public async Task RegistrationOtp_CreatesCustomerSessionAndAudit()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000001";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        var result = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, Device),
            CancellationToken.None);

        Assert.Equal(mobile, result.User.Mobile);
        Assert.Contains(AuthorizationCodes.Customer, result.User.Roles);
        Assert.NotNull((await harness.Db.OtpChallenges.SingleAsync()).ConsumedAt);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_LOGIN");
    }

    [Fact]
    public async Task OtpVerification_StopsAfterMaximumFailedAttempts()
    {
        await using var harness = await AuthenticationHarness.CreateAsync(otpMaxAttempts: 2);
        const string mobile = "+919999000002";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, null),
            CancellationToken.None);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
                harness.Otp.VerifyAsync(
                    new VerifyOtpRequest(mobile, "000000", OtpPurpose.Registration, Device),
                    CancellationToken.None));
        }

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, Device),
                CancellationToken.None));
        Assert.Equal(2, (await harness.Db.OtpChallenges.SingleAsync()).FailedAttempts);
        Assert.True(await harness.Db.AuditLogs.CountAsync(x => x.Action == "AUTH_OTP_FAILED") >= 3);
    }

    [Fact]
    public async Task ExpiredOtp_IsRejectedAndAudited()
    {
        await using var harness = await AuthenticationHarness.CreateAsync(otpLifetimeMinutes: 1);
        const string mobile = "+919999000003";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, null),
            CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, Device),
                CancellationToken.None));

        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "AUTH_OTP_FAILED" && x.Reason!.Contains("expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtpRequests_AreRateLimitedWithinConfiguredWindow()
    {
        await using var harness = await AuthenticationHarness.CreateAsync(otpRequestsPerWindow: 2);
        const string mobile = "+919999000004";
        var request = new SendOtpRequest(mobile, OtpPurpose.Login, "127.0.0.1");
        await harness.Otp.SendAsync(request, CancellationToken.None);
        await harness.Otp.SendAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<RateLimitAppException>(() =>
            harness.Otp.SendAsync(request, CancellationToken.None));

        Assert.Equal(2, await harness.Db.OtpChallenges.CountAsync());
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_RATE_LIMITED");
    }
}

internal sealed class AuthenticationHarness : IAsyncDisposable
{
    private AuthenticationHarness(
        DoodhDirectDbContext db,
        TestClock clock,
        TestTokenService tokens,
        CapturingOtpDelivery delivery,
        AuthenticationService authentication,
        OtpService otp)
    {
        Db = db;
        Clock = clock;
        Tokens = tokens;
        Delivery = delivery;
        Authentication = authentication;
        Otp = otp;
    }

    public DoodhDirectDbContext Db { get; }
    public TestClock Clock { get; }
    public TestTokenService Tokens { get; }
    public CapturingOtpDelivery Delivery { get; }
    public AuthenticationService Authentication { get; }
    public OtpService Otp { get; }

    public static async Task<AuthenticationHarness> CreateAsync(
        int otpLifetimeMinutes = 5,
        int otpMaxAttempts = 5,
        int otpRequestsPerWindow = 3)
    {
        var dbOptions = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"identity-tests-{Guid.NewGuid():N}")
            .Options;
        var db = new DoodhDirectDbContext(dbOptions);
        var permission = new Permission(AuthorizationCodes.ProfileReadOwn, "Read own profile");
        var role = new Role(AuthorizationCodes.Customer, "Customer");
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        await db.SaveChangesAsync();

        var clock = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified));
        var hasher = new TestPasswordHasher();
        var tokens = new TestTokenService();
        var delivery = new CapturingOtpDelivery();
        var notificationEventWriter = new TestNotificationEventWriter(db, clock);
        var identityOptions = Options.Create(new IdentityOptions
        {
            OtpLifetimeMinutes = otpLifetimeMinutes,
            OtpMaxAttempts = otpMaxAttempts,
            OtpRequestsPerWindow = otpRequestsPerWindow,
            OtpRateLimitWindowMinutes = 15,
            PasswordIterations = 10_000
        });

        return new AuthenticationHarness(
            db,
            clock,
            tokens,
            delivery,
            new AuthenticationService(
                db,
                hasher,
                tokens,
                clock,
                notificationEventWriter),
            new OtpService(
                db,
                hasher,
                delivery,
                clock,
                tokens,
                identityOptions,
                notificationEventWriter));
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

internal sealed class TestClock(DateTime now) : IClock, IIndiaTimeProvider
{
    public DateTime Now { get; private set; } = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);

    public DateTime UtcNow => ToUtc(Now);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public DateOnly CurrentDate => Today;

    public DateTime CurrentDateTime => Now;

    public DateTime ToUtc(DateTime indiaLocal) => DateTime.SpecifyKind(
        indiaLocal.AddHours(-5).AddMinutes(-30),
        DateTimeKind.Utc);

    public string FormatDateTime(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff");

    public string FormatDate(DateOnly value) => value.ToString("yyyy-MM-dd");

    public DateTime ParseApplicationDateTime(string value) => DateTime.SpecifyKind(
        DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
        DateTimeKind.Unspecified);

    public void Advance(TimeSpan duration) => Now = Now.Add(duration);
}

internal sealed class TestPasswordHasher : IPasswordHasher
{
    public string Hash(string value) => $"test-hash:{value}";

    public bool Verify(string hash, string value) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(Hash(value)));
}

internal sealed class TestTokenService : ITokenService
{
    private int _sequence;

    public TokenPair Create(
        User user,
        UserSession session,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions,
        IReadOnlyCollection<long> branchIds,
        DateTime now)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        return new TokenPair(
            $"access-token-{sequence}",
            $"refresh-token-{sequence}",
            now.AddMinutes(15),
            now.AddDays(30));
    }

    public string HashRefreshToken(string token) => $"hashed:{token}";
}

internal sealed class CapturingOtpDelivery : IOtpDeliveryService
{
    public string? LastDestination { get; private set; }
    public string? LastCode { get; private set; }

    public Task SendAsync(string destination, string code, CancellationToken cancellationToken)
    {
        LastDestination = destination;
        LastCode = code;
        return Task.CompletedTask;
    }
}
