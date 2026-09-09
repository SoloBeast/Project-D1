using System.Security.Cryptography;
using System.Text;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
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

    [Fact]
    public async Task PasswordLogin_LegacyNationalMobile_CanonicalLoginSucceeds()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string legacyMobile = "9876543210";
        var user = new User(UserType.Customer);
        user.SetProfile("Legacy Customer");
        user.SetContact(legacyMobile, null);
        user.SetPasswordHash(harness.PasswordHasher.Hash("correct-password"));
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Authentication.LoginAsync(
            new PasswordLoginRequest("+919876543210", "correct-password", Device),
            CancellationToken.None);

        Assert.Equal(legacyMobile, result.User.Mobile);
    }

    [Fact]
    public async Task PasswordLogin_CanonicalMobile_NationalFormLoginSucceeds()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string canonicalMobile = "+919876543211";
        var user = new User(UserType.Customer);
        user.SetProfile("Canonical Customer");
        user.SetContact(canonicalMobile, null);
        user.SetPasswordHash(harness.PasswordHasher.Hash("correct-password"));
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Authentication.LoginAsync(
            new PasswordLoginRequest("9876543211", "correct-password", Device),
            CancellationToken.None);

        Assert.Equal(canonicalMobile, result.User.Mobile);
    }

    [Fact]
    public async Task ForgotPassword_LegacyNationalMobile_ResolvesAndSendsOtpToCanonical()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string legacyMobile = "9876543212";
        var user = new User(UserType.Customer);
        user.SetProfile("Legacy Customer");
        user.SetContact(legacyMobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Authentication.ForgotPasswordAsync(
            new ForgotPasswordRequest("+919876543212", "127.0.0.1"),
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.ReqId));
        Assert.Equal("+919876543212", harness.Delivery.LastDestination);
    }

    [Fact]
    public async Task ForgotPassword_ActiveCustomerWithCanonicalMobile_SendsOtpToCanonical()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string canonicalMobile = "+919876543213";
        var user = new User(UserType.Customer);
        user.SetProfile("Canonical Customer");
        user.SetContact(canonicalMobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Authentication.ForgotPasswordAsync(
            new ForgotPasswordRequest(canonicalMobile, "127.0.0.1"),
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.ReqId));
        Assert.Equal(canonicalMobile, harness.Delivery.LastDestination);
        Assert.Equal(OtpPurpose.PasswordReset.ToString(), harness.Delivery.LastPurpose);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_REQUESTED");
    }

    [Fact]
    public async Task ForgotPassword_TenDigitIndianMobile_ResolvesToCanonicalAndSendsOtp()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string canonicalMobile = "+919876543214";
        var user = new User(UserType.Customer);
        user.SetProfile("Ten Digit Customer");
        user.SetContact(canonicalMobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Authentication.ForgotPasswordAsync(
            new ForgotPasswordRequest("9876543214", "127.0.0.1"),
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.ReqId));
        Assert.Equal(canonicalMobile, harness.Delivery.LastDestination);
    }

    [Fact]
    public async Task ForgotPassword_UnknownMobile_ThrowsUnauthorizedWithoutSendingOtp()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.ForgotPasswordAsync(
                new ForgotPasswordRequest("+919999999999", "127.0.0.1"),
                CancellationToken.None));

        Assert.Null(harness.Delivery.LastDestination);
        Assert.DoesNotContain(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_REQUESTED");
    }

    [Fact]
    public async Task ForgotPassword_InactiveCustomer_ThrowsUnauthorizedWithoutSendingOtp()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string canonicalMobile = "+919876543215";
        var user = new User(UserType.Customer);
        user.SetProfile("Inactive Customer");
        user.SetContact(canonicalMobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();
        user.Deactivate();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.ForgotPasswordAsync(
                new ForgotPasswordRequest(canonicalMobile, "127.0.0.1"),
                CancellationToken.None));

        Assert.Null(harness.Delivery.LastDestination);
        Assert.DoesNotContain(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_REQUESTED");
    }

    [Fact]
    public async Task ForgotPassword_ArchivedCustomer_ThrowsUnauthorizedWithoutSendingOtp()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string canonicalMobile = "+919876543216";
        var user = new User(UserType.Customer);
        user.SetProfile("Archived Customer");
        user.SetContact(canonicalMobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();
        user.Archive(new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Unspecified));
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.ForgotPasswordAsync(
                new ForgotPasswordRequest(canonicalMobile, "127.0.0.1"),
                CancellationToken.None));

        Assert.Null(harness.Delivery.LastDestination);
        Assert.DoesNotContain(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_REQUESTED");
    }

    [Fact]
    public async Task ForgotPassword_DoesNotRequireSessionOrToken_AndCreatesNone()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string canonicalMobile = "+919876543217";
        var user = new User(UserType.Customer);
        user.SetProfile("Guest Customer");
        user.SetContact(canonicalMobile, null);
        user.SetPasswordHash(harness.PasswordHasher.Hash("correct-password"));
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Authentication.ForgotPasswordAsync(
            new ForgotPasswordRequest("9876543217", "127.0.0.1"),
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.ReqId));
        Assert.Equal("+919876543217", harness.Delivery.LastDestination);
        Assert.Equal(0, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(0, await harness.Db.RefreshTokens.CountAsync());
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
    public async Task RegistrationOtp_OnUnknownMobile_RequiresOnboardingWithoutCreatingAccount()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000001";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        var result = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        // Case 2: the provider-verified mobile belongs to no account. Generic verification must
        // report the onboarding-required outcome WITHOUT creating an account or session — the
        // customer is created only by the dedicated completion operation.
        Assert.True(result.RequiresOnboarding);
        Assert.Null(result.Session);
        Assert.Equal(mobile, result.VerifiedMobile);
        Assert.Equal(harness.Delivery.LastReqId, result.ReqId);
        Assert.Equal(0, await harness.Db.Users.CountAsync());
        Assert.NotNull((await harness.Db.OtpChallenges.SingleAsync()).ConsumedAt);
        Assert.DoesNotContain(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_LOGIN");
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
            await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
                harness.Otp.VerifyAsync(
                    new VerifyOtpRequest(mobile, "000000", OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
                    CancellationToken.None));
        }

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
                CancellationToken.None));
        Assert.Equal(2, (await harness.Db.OtpChallenges.SingleAsync()).FailedAttempts);
        Assert.True(await harness.Db.AuditLogs.CountAsync(x => x.Action == "AUTH_OTP_FAILED") >= 3);
        // The provider must never be called once the local attempt limit is exhausted.
        Assert.Equal(2, harness.Delivery.VerifyCallCount);
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
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
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

    [Fact]
    public async Task OtpRetry_ReusesStoredReqIdWithoutCreatingDuplicateChallenge()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000005";
        var sendResult = await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        var retryResult = await harness.Otp.RetryAsync(
            new RetryOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        Assert.Equal(sendResult.ReqId, retryResult.ReqId);
        Assert.Equal(1, await harness.Db.OtpChallenges.CountAsync());
        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal(sendResult.ReqId, challenge.ReqId);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_RESENT");
    }

    [Fact]
    public async Task OtpVerification_RejectsReqIdMismatchAndAudits()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000006";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, "req-wrong", Device),
                CancellationToken.None));

        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(0, challenge.FailedAttempts);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "AUTH_OTP_FAILED" && x.Reason == "ReqId mismatch");
    }

    [Fact]
    public async Task OtpVerification_RejectsWhenProviderAttestsADifferentIdentifier()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000007";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        // The provider attests a DIFFERENT number than the challenge was created for —
        // a code minted for one mobile must never authenticate another.
        harness.Delivery.AttestedIdentifierOverride = "+919999999999";

        var exception = await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
                CancellationToken.None));

        Assert.Equal("The OTP verification could not be confirmed.", exception.Message);
        Assert.Equal(mobile, harness.Delivery.LastAccessTokenDestination);
        Assert.Equal(OtpPurpose.Registration.ToString(), harness.Delivery.LastAccessTokenPurpose);
        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(1, challenge.FailedAttempts);
        Assert.Equal(0, await harness.Db.Users.CountAsync());
    }

    [Fact]
    public async Task CustomerLoginOtp_AuthenticatesExistingCustomerWithoutCreatingDuplicate()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000008";

        // Seed an existing active customer directly (Registration auto-create was removed:
        // accounts now come from the dedicated onboarding completion operation).
        var user = new User(UserType.Customer);
        user.SetProfile("OTP Customer");
        user.SetContact(mobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Login, "127.0.0.1"),
            CancellationToken.None);
        var loginResult = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Login, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        Assert.False(loginResult.RequiresOnboarding);
        Assert.NotNull(loginResult.Session);
        Assert.Equal(mobile, loginResult.Session!.User.Mobile);
        Assert.Contains(AuthorizationCodes.Customer, loginResult.Session.User.Roles);
        Assert.Equal(1, await harness.Db.Users.CountAsync());
        Assert.Equal(1, await harness.Db.OtpChallenges.CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.CountAsync(x => x.Action == "AUTH_OTP_LOGIN"));
    }

    [Fact]
    public async Task SendOtp_StoresProviderReqIdAndAuditsRequested()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000009";

        var result = await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal(result.ReqId, challenge.ReqId);
        Assert.Equal(mobile, challenge.Destination);
        Assert.Equal(OtpPurpose.Registration, challenge.Purpose);
        Assert.Null(challenge.ConsumedAt);
        Assert.Equal(0, challenge.FailedAttempts);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_REQUESTED");
    }

    [Fact]
    public async Task WrongOtp_IsRejected_RecordsAttemptAndAuditsInvalidCode()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000010";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, null),
            CancellationToken.None);

        await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, "000000", OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
                CancellationToken.None));

        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal(1, challenge.FailedAttempts);
        Assert.Null(challenge.ConsumedAt);
        Assert.Null(harness.Delivery.LastAccessToken);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "AUTH_OTP_FAILED" && x.Reason == "Invalid code");
    }

    [Fact]
    public async Task ProviderUnavailable_OnVerify_SurfacesUnavailableWithoutRecordingAttempt()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000011";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, null),
            CancellationToken.None);

        harness.Delivery.ThrowUnavailableOnVerify = true;

        await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
                CancellationToken.None));

        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal(0, challenge.FailedAttempts);
        Assert.Null(challenge.ConsumedAt);
    }

    [Fact]
    public async Task OtpVerification_RejectsDifferentPurpose_WithoutRecordingAttempt()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000012";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, null),
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Login, harness.Delivery.LastReqId!, Device),
                CancellationToken.None));

        Assert.Equal(0, await harness.Db.OtpChallenges.CountAsync(x => x.Purpose == OtpPurpose.Login));
        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal(0, challenge.FailedAttempts);
        Assert.Null(challenge.ConsumedAt);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "AUTH_OTP_FAILED" && x.Reason!.Contains("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtpVerification_WithNoChallenge_RejectsAndAudits()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000013";

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, "123456", OtpPurpose.Registration, "req-1", Device),
                CancellationToken.None));

        Assert.Equal(0, await harness.Db.OtpChallenges.CountAsync());
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x =>
            x.Action == "AUTH_OTP_FAILED" && x.Reason!.Contains("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtpRetry_WithNoActiveChallenge_ThrowsBusinessRule()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000014";

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Otp.RetryAsync(
                new RetryOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task OtpRetry_DoesNotResetFailedAttempts()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000015";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, null),
            CancellationToken.None);
        await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            harness.Otp.VerifyAsync(
                new VerifyOtpRequest(mobile, "000000", OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
                CancellationToken.None));

        await harness.Otp.RetryAsync(
            new RetryOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);

        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal(1, challenge.FailedAttempts);
        Assert.Equal(1, await harness.Db.OtpChallenges.CountAsync());
    }

    [Fact]
    public async Task OtpVerification_NormalizesPhoneIdentifierBeforeComparing()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+91 9999 000016";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, null),
            CancellationToken.None);

        // The provider attests a digits-only identifier (e.g. "919999000016") while the
        // submitted form has spaces; both normalize to the canonical E.164 identity.
        var result = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        Assert.True(result.RequiresOnboarding);
        Assert.Equal("+919999000016", result.VerifiedMobile);
        Assert.Null(result.Session);
        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal("+919999000016", challenge.Destination);
        Assert.NotNull(challenge.ConsumedAt);
    }

    [Fact]
    public async Task SetPassword_OnAccountWithoutPassword_Succeeds()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000020";

        // Registration OTP verification no longer auto-creates accounts, so seed a passwordless
        // account directly to exercise SetPasswordAsync on an account that has none.
        var user = new User(UserType.Customer);
        user.SetProfile("Passwordless Customer");
        user.SetContact(mobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        await harness.Authentication.SetPasswordAsync(
            user.Id,
            new SetPasswordRequest("new-password-123"),
            CancellationToken.None);

        var updated = await harness.Db.Users.SingleAsync();
        Assert.True(updated.HasPassword);
        Assert.True(harness.PasswordHasher.Verify(updated.PasswordHash!, "new-password-123"));
    }

    [Fact]
    public async Task SetPassword_WhenPasswordAlreadySet_ThrowsBusinessRule()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "has-password@example.com", null, "existing-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users.Where(x => x.Email == "has-password@example.com").Select(x => x.Id).SingleAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Authentication.SetPasswordAsync(userId, new SetPasswordRequest("another-password"), CancellationToken.None));
    }

    [Fact]
    public async Task ChangePassword_WithCorrectCurrentPassword_SucceedsAndRevokesOtherSessions()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "change-pw@example.com", null, "old-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users.Where(x => x.Email == "change-pw@example.com").Select(x => x.Id).SingleAsync();
        var currentSessionPublicId = (await harness.Db.UserSessions.Where(x => x.UserId == userId).SingleAsync()).PublicId;

        // A second device session exists that must be revoked.
        var secondSession = new UserSession(userId, "second-device-hash", "Second device", "test", "127.0.0.2", "DoodhDirect.Tests", harness.Clock.Now);
        harness.Db.UserSessions.Add(secondSession);
        await harness.Db.SaveChangesAsync();

        await harness.Authentication.ChangePasswordAsync(
            userId,
            currentSessionPublicId,
            new ChangePasswordRequest("old-password", "new-password-456"),
            CancellationToken.None);

        var user = await harness.Db.Users.SingleAsync();
        Assert.True(harness.PasswordHasher.Verify(user.PasswordHash!, "new-password-456"));
        var sessions = await harness.Db.UserSessions.ToListAsync();
        Assert.NotNull(sessions.Single(x => x.PublicId != currentSessionPublicId).RevokedAt);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_PASSWORD_CHANGED");
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrentPassword_ThrowsUnauthorized()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "wrong-pw@example.com", null, "real-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users.Where(x => x.Email == "wrong-pw@example.com").Select(x => x.Id).SingleAsync();
        var sessionPublicId = (await harness.Db.UserSessions.Where(x => x.UserId == userId).SingleAsync()).PublicId;

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.ChangePasswordAsync(
                userId,
                sessionPublicId,
                new ChangePasswordRequest("wrong-password", "new-password-789"),
                CancellationToken.None));
    }

    [Fact]
    public async Task ForgotPassword_SendsOtpToCanonicalMobileThroughPasswordResetPurpose()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "forgot@example.com", "+919999000021", "some-password", Device),
            CancellationToken.None);
        var result = await harness.Authentication.ForgotPasswordAsync(
            new ForgotPasswordRequest("9999000021", "127.0.0.1"),
            CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.ReqId));
        Assert.Equal("+919999000021", harness.Delivery.LastDestination);
        Assert.Equal(OtpPurpose.PasswordReset.ToString(), harness.Delivery.LastPurpose);
        Assert.Equal(result.ReqId, harness.Delivery.LastReqId);
        Assert.Contains(await harness.Db.OtpChallenges.ToListAsync(), x =>
            x.Destination == "+919999000021" &&
            x.Purpose == OtpPurpose.PasswordReset &&
            x.ReqId == result.ReqId);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_REQUESTED");
    }

    [Fact]
    public async Task ForgotPassword_UnknownMobile_ThrowsUnauthorized()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.ForgotPasswordAsync(
                new ForgotPasswordRequest("9999000099", "127.0.0.1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task VerifyResetOtp_ValidCode_ConsumesChallengeWithoutSession()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "reset-valid@example.com", "+919999000024", "current-password", Device),
            CancellationToken.None);
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919999000024", OtpPurpose.PasswordReset, "127.0.0.1"),
            CancellationToken.None);

        await harness.Otp.VerifyResetOtpAsync(
            new VerifyResetOtpRequest("+919999000024", harness.Delivery.LastCode!, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.NotNull(challenge.ConsumedAt);
        // Registration already created one session; verifying a reset OTP must not add another.
        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
    }

    [Fact]
    public async Task VerifyResetOtp_InvalidCode_ThrowsUnauthorizedAndAudits()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "reset-invalid@example.com", "+919999000025", "current-password", Device),
            CancellationToken.None);
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919999000025", OtpPurpose.PasswordReset, "127.0.0.1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            harness.Otp.VerifyResetOtpAsync(
                new VerifyResetOtpRequest("+919999000025", "000000", harness.Delivery.LastReqId!, Device),
                CancellationToken.None));

        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.Equal(1, challenge.FailedAttempts);
        Assert.Null(challenge.ConsumedAt);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_RESET_VERIFY_FAILED");
    }

    [Fact]
    public async Task ResetPassword_AfterVerifiedOtp_ResetsPasswordAndCreatesSession()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "reset-complete@example.com", "+919999000022", "old-password", Device),
            CancellationToken.None);
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919999000022", OtpPurpose.PasswordReset, "127.0.0.1"),
            CancellationToken.None);
        await harness.Otp.VerifyResetOtpAsync(
            new VerifyResetOtpRequest("+919999000022", harness.Delivery.LastCode!, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        var result = await harness.Authentication.ResetPasswordAsync(
            new ResetPasswordRequest("+919999000022", harness.Delivery.LastReqId!, "reset-new-password", Device),
            CancellationToken.None);

        var updatedUser = await harness.Db.Users.SingleAsync();
        Assert.True(harness.PasswordHasher.Verify(updatedUser.PasswordHash!, "reset-new-password"));
        Assert.Equal(2, await harness.Db.UserSessions.CountAsync());
        Assert.False(string.IsNullOrWhiteSpace(result.Tokens.AccessToken));
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_PASSWORD_RESET");
    }

    [Fact]
    public async Task ResetPassword_ReusingVerifiedReqId_ThrowsUnauthorized()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "reset-replay@example.com", "+919999000026", "old-password", Device),
            CancellationToken.None);
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919999000026", OtpPurpose.PasswordReset, "127.0.0.1"),
            CancellationToken.None);
        var reqId = harness.Delivery.LastReqId!;
        await harness.Otp.VerifyResetOtpAsync(
            new VerifyResetOtpRequest("+919999000026", harness.Delivery.LastCode!, reqId, Device),
            CancellationToken.None);

        await harness.Authentication.ResetPasswordAsync(
            new ResetPasswordRequest("+919999000026", reqId, "reset-new-password", Device),
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.ResetPasswordAsync(
                new ResetPasswordRequest("+919999000026", reqId, "reset-replayed-password", Device),
                CancellationToken.None));

        var user = await harness.Db.Users.SingleAsync();
        Assert.True(harness.PasswordHasher.Verify(user.PasswordHash!, "reset-new-password"));
        Assert.False(harness.PasswordHasher.Verify(user.PasswordHash!, "reset-replayed-password"));
        var challenge = await harness.Db.OtpChallenges.SingleAsync();
        Assert.NotNull(challenge.PasswordResetConsumedAt);
    }

    [Fact]
    public async Task ResetPassword_WithoutVerifiedOtp_ThrowsUnauthorized()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "reset-unverified@example.com", "+919999000023", "old-password", Device),
            CancellationToken.None);
        await harness.Otp.SendAsync(
            new SendOtpRequest("+919999000023", OtpPurpose.PasswordReset, "127.0.0.1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.Authentication.ResetPasswordAsync(
                new ResetPasswordRequest("+919999000023", harness.Delivery.LastReqId!, "new-password", Device),
                CancellationToken.None));
    }

    [Fact]
    public async Task RequestMobileChange_MobileAlreadyInUse_ThrowsConflict()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("First", null, "+919999000001", "some-password", Device),
            CancellationToken.None);
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Second", null, "+919999000002", "some-password", Device),
            CancellationToken.None);
        var secondUserId = await harness.Db.Users
            .Where(x => x.Mobile == "+919999000002")
            .Select(x => x.Id)
            .SingleAsync();

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Authentication.RequestMobileChangeAsync(
                secondUserId,
                new RequestMobileChangeRequest("9999000001", "127.0.0.1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task VerifyMobileChangeOtp_MobileAlreadyInUse_ThrowsConflict()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("First", null, "+919999000003", "some-password", Device),
            CancellationToken.None);
        var firstUserId = await harness.Db.Users
            .Where(x => x.Mobile == "+919999000003")
            .Select(x => x.Id)
            .SingleAsync();

        var requested = await harness.Authentication.RequestMobileChangeAsync(
            firstUserId,
            new RequestMobileChangeRequest("9999000004", "127.0.0.1"),
            CancellationToken.None);

        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Second", null, "+919999000004", "some-password", Device),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Otp.VerifyMobileChangeOtpAsync(
                new VerifyOtpRequest("+919999000004", harness.Delivery.LastCode!, OtpPurpose.EmailVerification, requested.ReqId, Device),
                firstUserId,
                CancellationToken.None));

        var firstUser = await harness.Db.Users.SingleAsync(x => x.Id == firstUserId);
        Assert.Equal("+919999000003", firstUser.Mobile);
        Assert.Equal("+919999000004", firstUser.PendingMobile);
    }

    [Fact]
    public async Task RequestMobileChange_SameAsCurrent_ThrowsBusinessRule()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", null, "+919999000005", "some-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users
            .Where(x => x.Mobile == "+919999000005")
            .Select(x => x.Id)
            .SingleAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Authentication.RequestMobileChangeAsync(
                userId,
                new RequestMobileChangeRequest("9999000005", "127.0.0.1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task VerifyMobileChangeOtp_UnusedMobileCommitsMobileAndClearsPending()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", null, "+919999000006", "some-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users
            .Where(x => x.Mobile == "+919999000006")
            .Select(x => x.Id)
            .SingleAsync();

        var requested = await harness.Authentication.RequestMobileChangeAsync(
            userId,
            new RequestMobileChangeRequest("9999000007", "127.0.0.1"),
            CancellationToken.None);

        var result = await harness.Otp.VerifyMobileChangeOtpAsync(
            new VerifyOtpRequest("+919999000007", harness.Delivery.LastCode!, OtpPurpose.EmailVerification, requested.ReqId, Device),
            userId,
            CancellationToken.None);

        var user = await harness.Db.Users.SingleAsync(x => x.Id == userId);
        Assert.Equal("+919999000007", user.Mobile);
        Assert.Null(user.PendingMobile);
        Assert.Equal("+919999000007", result.Mobile);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_MOBILE_VERIFIED");
    }

    [Fact]
    public async Task RequestMobileChange_LegacyNationalMobileAlreadyInUse_ThrowsConflict()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", null, "+919999000008", "some-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users
            .Where(x => x.Mobile == "+919999000008")
            .Select(x => x.Id)
            .SingleAsync();

        var legacyUser = new User(UserType.Customer);
        legacyUser.SetProfile("Legacy mobile owner");
        legacyUser.SetContact("9999000009", null);
        legacyUser.SetPasswordHash("test-hash:some-password");
        harness.Db.Users.Add(legacyUser);
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Authentication.RequestMobileChangeAsync(
                userId,
                new RequestMobileChangeRequest("+919999000009", "127.0.0.1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task RequestEmailChange_StagesPendingEmailAndSendsOtp()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "old@example.com", null, "some-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users.Where(x => x.Email == "old@example.com").Select(x => x.Id).SingleAsync();

        var result = await harness.Authentication.RequestEmailChangeAsync(
            userId,
            new RequestEmailChangeRequest("NEW@example.com", "127.0.0.1"),
            CancellationToken.None);

        Assert.Equal("new@example.com", result.PendingEmail);
        Assert.False(string.IsNullOrWhiteSpace(result.ReqId));
        var user = await harness.Db.Users.SingleAsync();
        Assert.Equal("new@example.com", user.PendingEmail);
        Assert.Equal("old@example.com", user.Email);
        Assert.Equal("new@example.com", harness.Delivery.LastDestination);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_EMAIL_CHANGE_REQUESTED");
    }

    [Fact]
    public async Task RequestEmailChange_SameAsCurrent_ThrowsBusinessRule()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "same@example.com", null, "some-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users.Where(x => x.Email == "same@example.com").Select(x => x.Id).SingleAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Authentication.RequestEmailChangeAsync(
                userId,
                new RequestEmailChangeRequest("SAME@example.com", "127.0.0.1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task RequestEmailChange_EmailAlreadyInUse_ThrowsConflict()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "first@example.com", null, "some-password", Device),
            CancellationToken.None);
        await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "second@example.com", null, "some-password", Device),
            CancellationToken.None);
        var secondUserId = await harness.Db.Users.Where(x => x.Email == "second@example.com").Select(x => x.Id).SingleAsync();

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Authentication.RequestEmailChangeAsync(
                secondUserId,
                new RequestEmailChangeRequest("first@example.com", "127.0.0.1"),
                CancellationToken.None));
    }

    [Fact]
    public async Task VerifyEmailChange_ValidOtp_CommitsEmailAndClearsPending()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "verify@example.com", null, "some-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users.Where(x => x.Email == "verify@example.com").Select(x => x.Id).SingleAsync();
        var requested = await harness.Authentication.RequestEmailChangeAsync(
            userId,
            new RequestEmailChangeRequest("new-verify@example.com", "127.0.0.1"),
            CancellationToken.None);

        var result = await harness.Otp.VerifyEmailOtpAsync(
            new VerifyEmailChangeRequest(harness.Delivery.LastCode!, requested.ReqId, Device),
            userId,
            CancellationToken.None);

        var user = await harness.Db.Users.SingleAsync();
        Assert.Equal("new-verify@example.com", user.Email);
        Assert.Null(user.PendingEmail);
        Assert.NotNull(user.EmailVerifiedAt);
        Assert.True(result.EmailVerified);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_EMAIL_VERIFIED");
    }

    [Fact]
    public async Task VerifyEmailChange_InvalidOtp_ThrowsUnauthorizedAndDoesNotCommit()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        var registered = await harness.Authentication.RegisterAsync(
            new RegisterRequest("Customer", "verify-fail@example.com", null, "some-password", Device),
            CancellationToken.None);
        var userId = await harness.Db.Users.Where(x => x.Email == "verify-fail@example.com").Select(x => x.Id).SingleAsync();
        var requested = await harness.Authentication.RequestEmailChangeAsync(
            userId,
            new RequestEmailChangeRequest("fail@example.com", "127.0.0.1"),
            CancellationToken.None);

        await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            harness.Otp.VerifyEmailOtpAsync(
                new VerifyEmailChangeRequest("000000", requested.ReqId, Device),
                userId,
                CancellationToken.None));

        var user = await harness.Db.Users.SingleAsync();
        Assert.Equal("verify-fail@example.com", user.Email);
        Assert.Equal("fail@example.com", user.PendingEmail);
        Assert.Null(user.EmailVerifiedAt);
        Assert.Equal(1, (await harness.Db.OtpChallenges.SingleAsync()).FailedAttempts);
    }

    [Fact]
    public async Task OtpVerify_LegacyNationalMobile_ReconcilesToCanonicalUser()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string legacyMobile = "9876543213";
        var user = new User(UserType.Customer);
        user.SetProfile("Legacy OTP Customer");
        user.SetContact(legacyMobile, null);
        var customerRole = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.Customer);
        user.AssignRole(customerRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        const string canonicalMobile = "+919876543213";
        await harness.Otp.SendAsync(
            new SendOtpRequest(canonicalMobile, OtpPurpose.Login, "127.0.0.1"),
            CancellationToken.None);

        var result = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(canonicalMobile, harness.Delivery.LastCode!, OtpPurpose.Login, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        Assert.False(result.RequiresOnboarding);
        Assert.NotNull(result.Session);
        Assert.Equal(legacyMobile, result.Session!.User.Mobile);
        Assert.Equal(canonicalMobile, (await harness.Db.OtpChallenges.SingleAsync()).Destination);
    }

    [Fact]
    public async Task EmployeeLoginOtp_AuthenticatesExistingDeliveryStaff_WithRoleHome()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000030";

        var staffRole = new Role(AuthorizationCodes.DeliveryStaff, "Delivery Staff");
        harness.Db.Roles.Add(staffRole);
        await harness.Db.SaveChangesAsync();
        var user = new User(UserType.Employee);
        user.SetProfile("Delivery Staff OTP User");
        user.SetContact(mobile, null);
        user.AssignRole(staffRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Login, "127.0.0.1"),
            CancellationToken.None);
        var result = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Login, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        Assert.False(result.RequiresOnboarding);
        Assert.NotNull(result.Session);
        Assert.Equal(mobile, result.Session!.User.Mobile);
        Assert.Contains(AuthorizationCodes.DeliveryStaff, result.Session.User.Roles);
        Assert.Equal(1, await harness.Db.Users.CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.CountAsync(x => x.Action == "AUTH_OTP_LOGIN"));
    }

    [Fact]
    public async Task AdminLoginOtp_AuthenticatesExistingSystemAdministrator_WithRoleHome()
    {
        await using var harness = await AuthenticationHarness.CreateAsync();
        const string mobile = "+919999000031";

        var adminRole = new Role(AuthorizationCodes.SystemAdmin, "System Administrator");
        harness.Db.Roles.Add(adminRole);
        await harness.Db.SaveChangesAsync();
        var user = new User(UserType.SystemAdministrator);
        user.SetProfile("System Admin OTP User");
        user.SetContact(mobile, null);
        user.AssignRole(adminRole);
        harness.Db.Users.Add(user);
        await harness.Db.SaveChangesAsync();

        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Login, "127.0.0.1"),
            CancellationToken.None);
        var result = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Login, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        Assert.False(result.RequiresOnboarding);
        Assert.NotNull(result.Session);
        Assert.Equal(mobile, result.Session!.User.Mobile);
        Assert.Contains(AuthorizationCodes.SystemAdmin, result.Session.User.Roles);
        Assert.DoesNotContain(AuthorizationCodes.Customer, result.Session.User.Roles);
        Assert.Equal(1, await harness.Db.Users.CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.CountAsync(x => x.Action == "AUTH_OTP_LOGIN"));
    }

    [Fact]
    public async Task CompleteOtpRegistration_CreatesCustomerSessionAndAudit()
    {
        await using var harness = await OtpRegistrationSqliteHarness.CreateAsync();
        const string mobile = "+919999000040";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);
        var verified = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        Assert.True(verified.RequiresOnboarding);
        Assert.Null(verified.Session);
        Assert.Equal(0, await harness.Db.Users.CountAsync());

        var session = await harness.Authentication.CompleteOtpRegistrationAsync(
            new CompleteOtpRegistrationRequest(mobile, verified.ReqId!, "StrongPass!1", Device),
            CancellationToken.None);

        Assert.Equal(mobile, session.User.Mobile);
        Assert.Contains(AuthorizationCodes.Customer, session.User.Roles);
        Assert.True(session.User.HasPassword);

        var user = await harness.Db.Users.SingleAsync(x => x.Mobile == mobile);
        Assert.Equal(UserType.Customer, user.UserType);
        Assert.True(user.HasPassword);
        Assert.True(harness.Hasher.Verify(user.PasswordHash!, "StrongPass!1"));
        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(1, await harness.Db.RefreshTokens.CountAsync());
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "AUTH_OTP_ONBOARDED");
    }

    [Fact]
    public async Task CompleteOtpRegistration_DuplicateMobile_CannotCreateSecondAccount()
    {
        await using var harness = await OtpRegistrationSqliteHarness.CreateAsync();
        const string mobile = "+919999000041";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);
        var verified = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        var first = await harness.Authentication.CompleteOtpRegistrationAsync(
            new CompleteOtpRegistrationRequest(mobile, verified.ReqId!, "StrongPass!1", Device),
            CancellationToken.None);
        Assert.NotNull(first);

        // The OTP is single-use and the mobile now has an account, so a second completion with
        // the same reqId must be refused instead of creating a duplicate customer.
        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Authentication.CompleteOtpRegistrationAsync(
                new CompleteOtpRegistrationRequest(mobile, verified.ReqId!, "AnotherPass!1", Device),
                CancellationToken.None));
        Assert.Equal("An account already exists for this mobile number. Please sign in instead.", exception.Message);

        Assert.Equal(1, await harness.Db.Users.CountAsync());
        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
    }

    [Fact]
    public async Task CompleteOtpRegistration_ServerDecidesCustomerTypeAndRole()
    {
        await using var harness = await OtpRegistrationSqliteHarness.CreateAsync();
        const string mobile = "+919999000042";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);
        var verified = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);

        // The completion request exposes only mobile + reqId + password + device — there is no
        // field a client could use to choose an account type or role.
        var session = await harness.Authentication.CompleteOtpRegistrationAsync(
            new CompleteOtpRegistrationRequest(mobile, verified.ReqId!, "StrongPass!1", Device),
            CancellationToken.None);

        Assert.Contains(AuthorizationCodes.Customer, session.User.Roles);
        Assert.DoesNotContain(AuthorizationCodes.DeliveryStaff, session.User.Roles);
        Assert.DoesNotContain(AuthorizationCodes.SystemAdmin, session.User.Roles);
        Assert.DoesNotContain(AuthorizationCodes.Owner, session.User.Roles);

        var created = await harness.Db.Users
            .Include(x => x.UserRoles).ThenInclude(x => x.Role)
            .SingleAsync();
        Assert.Equal(UserType.Customer, created.UserType);
        Assert.Equal(new[] { AuthorizationCodes.Customer }, created.UserRoles.Select(x => x.Role.Code).ToArray());
    }

    [Fact]
    public async Task ConcurrentOtpOnboarding_ExactlyOneCustomerIsCreated()
    {
        await using var harness = await OtpRegistrationSqliteHarness.CreateAsync();
        const string mobile = "+919999000043";
        await harness.Otp.SendAsync(
            new SendOtpRequest(mobile, OtpPurpose.Registration, "127.0.0.1"),
            CancellationToken.None);
        var verified = await harness.Otp.VerifyAsync(
            new VerifyOtpRequest(mobile, harness.Delivery.LastCode!, OtpPurpose.Registration, harness.Delivery.LastReqId!, Device),
            CancellationToken.None);
        var request = new CompleteOtpRegistrationRequest(mobile, verified.ReqId!, "StrongPass!1", Device);

        // EF Core DbContext is not thread-safe, so each contender uses its own context over a
        // separate connection into the same shared-cache in-memory SQLite database. The unique
        // filtered User.Mobile index is the backstop that lets exactly one completion win.
        var context1 = harness.CreateContext();
        var context2 = harness.CreateContext();
        var successes = 0;
        Exception? failure = null;
        var gate = new object();

        await Task.WhenAll(RunAsync(context1), RunAsync(context2));

        async Task RunAsync(DoodhDirectDbContext context)
        {
            try
            {
                await harness.CreateAuthenticationService(context)
                    .CompleteOtpRegistrationAsync(request, CancellationToken.None);
                lock (gate) { successes++; }
            }
            catch (Exception ex)
            {
                lock (gate) { failure = ex; }
            }
            finally
            {
                await context.DisposeAsync();
            }
        }

        Assert.Equal(1, successes);
        Assert.NotNull(failure);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.Users.CountAsync());
        Assert.Equal(1, await harness.Db.UserSessions.CountAsync());
        Assert.Equal(1, await harness.Db.RefreshTokens.CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.CountAsync(x => x.Action == "AUTH_OTP_ONBOARDED"));
    }
}

internal sealed class OtpRegistrationSqliteHarness : IAsyncDisposable
{
    private OtpRegistrationSqliteHarness(
        SqliteConnection connection,
        string connectionString,
        DoodhDirectDbContext db,
        TestClock clock,
        TestPasswordHasher hasher,
        TestTokenService tokens,
        CapturingOtpDelivery delivery,
        IdentityOptions identityOptions,
        AuthenticationService authentication,
        OtpService otp)
    {
        Connection = connection;
        ConnectionString = connectionString;
        Db = db;
        Clock = clock;
        Hasher = hasher;
        Tokens = tokens;
        Delivery = delivery;
        IdentityOptions = identityOptions;
        Authentication = authentication;
        Otp = otp;
    }

    public SqliteConnection Connection { get; }
    public string ConnectionString { get; }
    public DoodhDirectDbContext Db { get; }
    public TestClock Clock { get; }
    public TestPasswordHasher Hasher { get; }
    public TestTokenService Tokens { get; }
    public CapturingOtpDelivery Delivery { get; }
    public IdentityOptions IdentityOptions { get; }
    public AuthenticationService Authentication { get; }
    public OtpService Otp { get; }

    /// <summary>
    /// Uses a SQLite shared-cache in-memory database because the InMemory provider does not
    /// support the serializable transaction that CompleteOtpRegistrationAsync executes inside.
    /// </summary>
    public static async Task<OtpRegistrationSqliteHarness> CreateAsync(
        int otpLifetimeMinutes = 5,
        int otpMaxAttempts = 5,
        int otpRequestsPerWindow = 3)
    {
        var clock = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified));
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = $"authentication-otp-tests-{Guid.NewGuid():N}",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 10
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(connection, sqlite => sqlite.CommandTimeout(10))
            .Options;
        var db = new DoodhDirectDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var permission = new Permission(AuthorizationCodes.ProfileReadOwn, "Read own profile");
        var role = new Role(AuthorizationCodes.Customer, "Customer");
        db.Permissions.Add(permission);
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var hasher = new TestPasswordHasher();
        var tokens = new TestTokenService();
        var delivery = new CapturingOtpDelivery();
        var identityOptions = new IdentityOptions
        {
            OtpLifetimeMinutes = otpLifetimeMinutes,
            OtpMaxAttempts = otpMaxAttempts,
            OtpRequestsPerWindow = otpRequestsPerWindow,
            OtpRateLimitWindowMinutes = 15,
            PasswordIterations = 10_000
        };

        var notificationEventWriter = new TestNotificationEventWriter(db, clock);
        var otp = new OtpService(
            db,
            delivery,
            clock,
            tokens,
            Options.Create(identityOptions),
            notificationEventWriter);
        var authentication = new AuthenticationService(
            db,
            hasher,
            tokens,
            clock,
            notificationEventWriter,
            otp);

        return new OtpRegistrationSqliteHarness(
            connection,
            connectionString,
            db,
            clock,
            hasher,
            tokens,
            delivery,
            identityOptions,
            authentication,
            otp);
    }

    /// <summary>Creates a fresh context over a new connection to the same shared in-memory database.</summary>
    public DoodhDirectDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(ConnectionString, sqlite => sqlite.CommandTimeout(10))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    public AuthenticationService CreateAuthenticationService(DoodhDirectDbContext db) => new(
        db,
        Hasher,
        Tokens,
        Clock,
        new TestNotificationEventWriter(db, Clock),
        new OtpService(
            db,
            Delivery,
            Clock,
            Tokens,
            Options.Create(IdentityOptions),
            new TestNotificationEventWriter(db, Clock)));

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await Connection.DisposeAsync();
    }
}

internal sealed class AuthenticationHarness : IAsyncDisposable
{
    private AuthenticationHarness(
        DoodhDirectDbContext db,
        TestClock clock,
        TestTokenService tokens,
        TestPasswordHasher passwordHasher,
        CapturingOtpDelivery delivery,
        AuthenticationService authentication,
        OtpService otp)
    {
        Db = db;
        Clock = clock;
        Tokens = tokens;
        PasswordHasher = passwordHasher;
        Delivery = delivery;
        Authentication = authentication;
        Otp = otp;
    }

    public DoodhDirectDbContext Db { get; }
    public TestClock Clock { get; }
    public TestTokenService Tokens { get; }
    public TestPasswordHasher PasswordHasher { get; }
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

        var otp = new OtpService(
            db,
            delivery,
            clock,
            tokens,
            identityOptions,
            notificationEventWriter);

        return new AuthenticationHarness(
            db,
            clock,
            tokens,
            hasher,
            delivery,
            new AuthenticationService(
                db,
                hasher,
                tokens,
                clock,
                notificationEventWriter,
                otp),
            otp);
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

internal sealed class CapturingOtpDelivery : IMsg91OtpProvider
{
    public const string TestCode = "123456";

    public string? LastDestination { get; private set; }
    public string? LastCode { get; private set; }
    public string? LastReqId { get; private set; }
    public string? LastPurpose { get; private set; }
    public string? LastAccessToken { get; private set; }
    public string? LastAccessTokenDestination { get; private set; }
    public string? LastAccessTokenPurpose { get; private set; }

    /// <summary>
    /// When set, ValidateAccessTokenAsync attests this identifier instead of the
    /// destination the OTP was sent to (used to simulate a provider identity mismatch).
    /// </summary>
    public string? AttestedIdentifierOverride { get; set; }

    /// <summary>
    /// When set, VerifyAsync surfaces a transport-level failure (provider unavailable).
    /// </summary>
    public bool ThrowUnavailableOnVerify { get; set; }

    /// <summary>
    /// Number of times the provider's VerifyAsync was actually invoked — proves that
    /// locally-blocked attempts (expiry, exhausted attempts) never reach MSG91.
    /// </summary>
    public int VerifyCallCount { get; private set; }

    private readonly Dictionary<string, (string Code, string Destination)> _requests = new(StringComparer.Ordinal);
    private int _sequence;

    public Task<Msg91OtpSendResult> SendAsync(Msg91OtpSendRequest request, CancellationToken cancellationToken)
    {
        LastDestination = request.Destination;
        LastCode = TestCode;
        LastPurpose = request.Purpose;
        LastReqId = $"req-{Interlocked.Increment(ref _sequence)}";
        _requests[LastReqId!] = (TestCode, request.Destination);
        return Task.FromResult(new Msg91OtpSendResult(LastReqId!));
    }

    public Task<Msg91OtpSendResult> RetryAsync(Msg91OtpRetryRequest request, CancellationToken cancellationToken)
    {
        LastReqId = request.ReqId;
        return Task.FromResult(new Msg91OtpSendResult(request.ReqId));
    }

    public Task<Msg91OtpVerifyResult> VerifyAsync(Msg91OtpVerifyRequest request, CancellationToken cancellationToken)
    {
        VerifyCallCount++;
        if (ThrowUnavailableOnVerify)
            throw new OtpProviderUnavailableException("MSG91 is temporarily unavailable.");

        if (!_requests.TryGetValue(request.ReqId, out var entry) ||
            !string.Equals(entry.Code, request.Code, StringComparison.Ordinal))
        {
            // Wrong or unknown code — MSG91 rejects; surface as a provider rejection.
            throw new OtpProviderRejectedException("The OTP verification could not be confirmed.");
        }

        LastAccessToken = $"access-token-{request.ReqId}";
        return Task.FromResult(new Msg91OtpVerifyResult(LastAccessToken));
    }

    public Task<Msg91OtpValidationResult> ValidateAccessTokenAsync(
        Msg91OtpAccessTokenRequest request,
        CancellationToken cancellationToken)
    {
        LastAccessToken = request.AccessToken;
        LastAccessTokenDestination = request.Destination;
        LastAccessTokenPurpose = request.Purpose;

        if (AttestedIdentifierOverride is not null)
            return Task.FromResult(new Msg91OtpValidationResult(AttestedIdentifierOverride));

        // Mirror MSG91's data.message shape: for email destinations the identifier is
        // the normalized email; for mobile destinations it is digits-only.
        var identifier = request.Destination.Contains('@')
            ? request.Destination.Trim().ToLowerInvariant()
            : new string(request.Destination.Where(c => c is >= '0' and <= '9').ToArray());
        return Task.FromResult(new Msg91OtpValidationResult(identifier));
    }
}
