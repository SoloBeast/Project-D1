using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.OtpProvider;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class OtpProviderConfigurationServiceTests
{
    [Fact]
    public async Task GetAsync_WhenUnconfigured_ReturnsNotConfiguredWithoutAuthKey()
    {
        var db = CreateDb();
        var service = CreateService(db);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(OtpProviderConfiguration.ProviderMsg91, result.Provider);
        Assert.False(result.Enabled);
        Assert.Null(result.WidgetId);
        Assert.Null(result.Environment);
        Assert.False(result.Configured);
        Assert.Equal("Not Configured", result.Status);
    }

    [Fact]
    public async Task GetAsync_ReturnsConfiguredStateWithoutAuthKey()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(db);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Equal(OtpProviderConfiguration.ProviderMsg91, result.Provider);
        Assert.True(result.Enabled);
        Assert.Equal("widget-1", result.WidgetId);
        Assert.Equal(OtpProviderConfiguration.EnvironmentTest, result.Environment);
        Assert.True(result.Configured);
        Assert.Equal("Configured", result.Status);
    }

    [Fact]
    public async Task UpdateAsync_StoresProtectedAuthKeyAndNeverPersistsPlaintext()
    {
        var db = CreateDb();
        var protector = CreateProtector();
        var service = CreateService(db, protector: protector);

        await service.UpdateAsync(
            new UpdateOtpProviderConfigurationRequest(true, "widget-1", "authkey-1", "Test"),
            42,
            "127.0.0.1",
            "update-agent",
            CancellationToken.None);

        var storedAuthKey = db.SystemConfigurations.Single(
            x => x.Key == OtpProviderConfigurationKeys.AuthKey);
        Assert.NotEqual("authkey-1", storedAuthKey.Value);
        Assert.Equal("authkey-1", protector.Unprotect(storedAuthKey.Value));
        Assert.True(storedAuthKey.IsSensitive);
        Assert.Equal("sensitive", storedAuthKey.ValueType);

        var widget = db.SystemConfigurations.Single(
            x => x.Key == OtpProviderConfigurationKeys.WidgetId);
        Assert.Equal("widget-1", widget.Value);
        Assert.False(widget.IsSensitive);

        var enabled = db.SystemConfigurations.Single(
            x => x.Key == OtpProviderConfigurationKeys.Enabled);
        Assert.Equal("true", enabled.Value);
    }

    [Theory]
    [InlineData(true, true, OtpProviderConfigurationService.ActionConfigUpdated)]
    [InlineData(true, false, OtpProviderConfigurationService.ActionDisabled)]
    [InlineData(false, true, OtpProviderConfigurationService.ActionEnabled)]
    public async Task UpdateAsync_AuditsActionWithoutLoggingSecrets(
        bool beforeEnabled,
        bool afterEnabled,
        string expectedAction)
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db, enabled: beforeEnabled);
        var service = CreateService(db);

        await service.UpdateAsync(
            new UpdateOtpProviderConfigurationRequest(afterEnabled, null, null, null),
            7,
            "10.0.0.1",
            "audit-agent",
            CancellationToken.None);

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal(expectedAction, audit.Action);
        Assert.Equal("OtpProviderConfiguration", audit.EntityType);
        Assert.Equal("MSG91", audit.EntityId);
        Assert.Equal(7, audit.UserId);
        Assert.Equal("10.0.0.1", audit.IPAddress);
        Assert.Equal("audit-agent", audit.UserAgent);
        Assert.DoesNotContain("authkey", audit.OldValueJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authkey", audit.NewValueJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_RejectsEmptyWidgetId()
    {
        var service = CreateService(CreateDb());

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            service.UpdateAsync(
                new UpdateOtpProviderConfigurationRequest(true, "  ", null, null),
                1,
                null,
                null,
                CancellationToken.None));

        Assert.Equal("widgetId", exception.Field);
    }

    [Fact]
    public async Task UpdateAsync_RejectsEmptyAuthKey()
    {
        var service = CreateService(CreateDb());

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            service.UpdateAsync(
                new UpdateOtpProviderConfigurationRequest(true, null, "", null),
                1,
                null,
                null,
                CancellationToken.None));

        Assert.Equal("authKey", exception.Field);
    }

    [Fact]
    public async Task UpdateAsync_RejectsInvalidEnvironment()
    {
        var service = CreateService(CreateDb());

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            service.UpdateAsync(
                new UpdateOtpProviderConfigurationRequest(true, null, null, "Staging"),
                1,
                null,
                null,
                CancellationToken.None));

        Assert.Equal("environment", exception.Field);
    }

    [Fact]
    public async Task UpdateAsync_NormalizesEnvironmentCase()
    {
        var db = CreateDb();
        var service = CreateService(db);

        await service.UpdateAsync(
            new UpdateOtpProviderConfigurationRequest(false, "widget-1", "authkey-1", "production"),
            1,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(
            OtpProviderConfiguration.EnvironmentProduction,
            db.SystemConfigurations.Single(
                x => x.Key == OtpProviderConfigurationKeys.Environment).Value);
    }

    [Fact]
    public async Task TestAsync_WhenNotConfigured_ThrowsBusinessRule()
    {
        var service = CreateService(CreateDb());

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Contains("not fully configured", exception.Message);
    }

    [Fact]
    public async Task TestAsync_WhenDisabled_ThrowsBusinessRule()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db, enabled: false);
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Contains("disabled", exception.Message);
    }

    [Fact]
    public async Task TestAsync_WhenProviderUnavailable_PropagatesWithoutAuditing()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(
            db,
            provider: new ThrowingProvider(new OtpProviderUnavailableException("provider down")));

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Equal("provider down", exception.Message);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task TestAsync_WhenProviderRejects_PropagatesRejectionWithDiagnosticWithoutAuditing()
    {
        // §10: the provider answered but rejected the request (invalid/expired
        // authkey or widget id). The rejection must surface as-is (422), carrying
        // the provider diagnostic, rather than being wrapped as an availability error.
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(
            db,
            provider: new ThrowingProvider(new OtpProviderRejectedException(
                "The OTP service rejected the request (HTTP 400). Provider message: invalid authkey.")));

        var exception = await Assert.ThrowsAsync<OtpProviderRejectedException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Contains("invalid authkey", exception.Message);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task TestAsync_WhenProviderThrowsGeneric_WrapsInProviderUnavailable()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(
            db,
            provider: new ThrowingProvider(new InvalidOperationException("boom")));

        var exception = await Assert.ThrowsAsync<OtpProviderUnavailableException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Contains("could not be reached", exception.Message);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task TestAsync_Success_SendsConfigurationOtpAndAuditsTested()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var provider = new CapturingMsg91Provider();
        var service = CreateService(db, provider: provider);

        var result = await service.TestAsync(
            9,
            "192.168.1.1",
            "test-agent",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Configuration verified. A test OTP was sent successfully.", result.Message);
        Assert.NotNull(provider.LastSendRequest);
        Assert.Equal("0000000000", provider.LastSendRequest!.Destination);
        Assert.Equal("configuration-test", provider.LastSendRequest.Purpose);

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal(OtpProviderConfigurationService.ActionTested, audit.Action);
        Assert.Equal(9, audit.UserId);
        Assert.Equal("192.168.1.1", audit.IPAddress);
        Assert.Equal("test-agent", audit.UserAgent);
        Assert.Null(audit.OldValueJson);
        Assert.Null(audit.NewValueJson);
    }

    private static DoodhDirectDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"otp-provider-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    private static OtpProviderSecretProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());

    private static TestClock CreateClock() =>
        new(new DateTime(2026, 9, 1, 11, 30, 0));

    private static OtpProviderConfigurationService CreateService(
        DoodhDirectDbContext db,
        OtpProviderSecretProtector? protector = null,
        IIndiaTimeProvider? timeProvider = null,
        IMsg91OtpProvider? provider = null) =>
        new(
            db,
            timeProvider ?? CreateClock(),
            protector ?? CreateProtector(),
            provider ?? new CapturingMsg91Provider());

    private static async Task SeedConfiguredAsync(DoodhDirectDbContext db, bool enabled = true)
    {
        var protector = CreateProtector();
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.Provider,
            OtpProviderConfiguration.ProviderMsg91,
            "string",
            "OTP provider (MSG91)."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.Enabled,
            enabled ? "true" : "false",
            "bool",
            "Whether the OTP provider is enabled for sending identity OTPs."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.WidgetId,
            "widget-1",
            "string",
            "MSG91 OTP widget identifier."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.Environment,
            OtpProviderConfiguration.EnvironmentTest,
            "string",
            "MSG91 environment (Test or Production)."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            OtpProviderConfigurationKeys.AuthKey,
            protector.Protect("authkey-1"),
            "sensitive",
            "MSG91 auth key (protected, never exposed).",
            true));
        await db.SaveChangesAsync();
    }

    private sealed class CapturingMsg91Provider : IMsg91OtpProvider
    {
        public Msg91OtpSendRequest? LastSendRequest { get; private set; }

        public Task<Msg91OtpSendResult> SendAsync(
            Msg91OtpSendRequest request,
            CancellationToken cancellationToken)
        {
            LastSendRequest = request;
            return Task.FromResult(new Msg91OtpSendResult("req-test"));
        }

        public Task<Msg91OtpSendResult> RetryAsync(
            Msg91OtpRetryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Msg91OtpVerifyResult> VerifyAsync(
            Msg91OtpVerifyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Msg91OtpValidationResult> ValidateAccessTokenAsync(
            Msg91OtpAccessTokenRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    private sealed class ThrowingProvider(Exception exception) : IMsg91OtpProvider
    {
        public Task<Msg91OtpSendResult> SendAsync(
            Msg91OtpSendRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<Msg91OtpSendResult>(exception);

        public Task<Msg91OtpSendResult> RetryAsync(
            Msg91OtpRetryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Msg91OtpVerifyResult> VerifyAsync(
            Msg91OtpVerifyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<Msg91OtpValidationResult> ValidateAccessTokenAsync(
            Msg91OtpAccessTokenRequest request,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
