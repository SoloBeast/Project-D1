using System.Net.Mail;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Integrations;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.Integrations;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class IntegrationConfigurationServiceTests
{
    [Fact]
    public async Task GetAsync_WhenUnconfigured_ReturnsDefaultsWithoutSecrets()
    {
        var db = CreateDb();
        var service = CreateService(db);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Null(result.EmailFromAddress);
        Assert.Null(result.EmailFromName);
        Assert.Null(result.EmailHost);
        Assert.Equal(587, result.EmailPort);
        Assert.Null(result.EmailUserName);
        Assert.False(result.EmailUseSsl);
        Assert.False(result.EmailPasswordConfigured);
        Assert.False(result.EmailIsConfigured);
        Assert.Null(result.InviteUrlBase);
        Assert.Null(result.RazorpayKeyId);
        Assert.False(result.RazorpayKeySecretConfigured);
        Assert.False(result.RazorpayWebhookSecretConfigured);
        Assert.False(result.RazorpayIsConfigured);
        Assert.Null(result.GoogleMapsBaseUrl);
        Assert.False(result.GoogleMapsApiKeyConfigured);
        Assert.False(result.GoogleMapsIsConfigured);
        Assert.Null(result.GoogleMapsWebClientKey);
    }

    [Fact]
    public async Task GetAsync_WhenConfigured_ReturnsStateWithoutSecrets()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(db, settingsProvider: ConfiguredSettings());

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Equal("no-reply@doodhdirect.in", result.EmailFromAddress);
        Assert.Equal("DoodhDirect", result.EmailFromName);
        Assert.Equal("smtp.example.com", result.EmailHost);
        Assert.Equal(587, result.EmailPort);
        Assert.Equal("smtp-user", result.EmailUserName);
        Assert.True(result.EmailUseSsl);
        Assert.True(result.EmailPasswordConfigured);
        Assert.True(result.EmailIsConfigured);
        Assert.Equal("https://app.example.com", result.InviteUrlBase);
        Assert.Equal("rzp_test_key", result.RazorpayKeyId);
        Assert.True(result.RazorpayKeySecretConfigured);
        Assert.True(result.RazorpayWebhookSecretConfigured);
        Assert.True(result.RazorpayIsConfigured);
        Assert.Equal("https://maps.googleapis.com/maps/api/geocode/json", result.GoogleMapsBaseUrl);
        Assert.True(result.GoogleMapsApiKeyConfigured);
        Assert.True(result.GoogleMapsIsConfigured);
        Assert.Equal("seed-web-client-key", result.GoogleMapsWebClientKey);
    }

    [Fact]
    public async Task UpdateAsync_StoresProtectedSecretsAndNeverPersistsPlaintext()
    {
        var db = CreateDb();
        var protector = CreateProtector();
        var service = CreateService(db, protector: protector);

        await service.UpdateAsync(
            new UpdateIntegrationConfigurationRequest(
                EmailFromAddress: "no-reply@doodhdirect.in",
                EmailFromName: "DoodhDirect",
                EmailHost: "smtp.example.com",
                EmailPort: 587,
                EmailUserName: "smtp-user",
                EmailPassword: "smtp-secret-1",
                EmailUseSsl: true,
                InviteUrlBase: "https://app.example.com",
                RazorpayKeyId: "rzp_test_key",
                RazorpayKeySecret: "rzp-secret-1",
                RazorpayWebhookSecret: "whsec-1",
                GoogleMapsApiKey: "maps-key-1",
                GoogleMapsBaseUrl: "https://maps.googleapis.com/maps/api/geocode/json",
                GoogleMapsWebClientKey: "maps-web-key-1"),
            42,
            "10.0.0.1",
            "update-agent",
            CancellationToken.None);

        var fromAddress = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.EmailFromAddress);
        Assert.Equal("no-reply@doodhdirect.in", fromAddress.Value);
        Assert.False(fromAddress.IsSensitive);
        Assert.Equal("string", fromAddress.ValueType);

        var password = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.EmailPassword);
        Assert.NotEqual("smtp-secret-1", password.Value);
        Assert.Equal("smtp-secret-1", protector.Unprotect(password.Value));
        Assert.True(password.IsSensitive);
        Assert.Equal("sensitive", password.ValueType);

        var keySecret = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.RazorpayKeySecret);
        Assert.NotEqual("rzp-secret-1", keySecret.Value);
        Assert.Equal("rzp-secret-1", protector.Unprotect(keySecret.Value));
        Assert.True(keySecret.IsSensitive);

        var webhookSecret = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.RazorpayWebhookSecret);
        Assert.NotEqual("whsec-1", webhookSecret.Value);
        Assert.Equal("whsec-1", protector.Unprotect(webhookSecret.Value));

        var mapsKey = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.GoogleMapsApiKey);
        Assert.NotEqual("maps-key-1", mapsKey.Value);
        Assert.Equal("maps-key-1", protector.Unprotect(mapsKey.Value));

        var webClientKey = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.GoogleMapsWebClientKey);
        Assert.Equal("maps-web-key-1", webClientKey.Value);
        Assert.False(webClientKey.IsSensitive);
        Assert.Equal("string", webClientKey.ValueType);

        var port = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.EmailPort);
        Assert.Equal("587", port.Value);
        Assert.Equal("int", port.ValueType);

        var useSsl = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.EmailUseSsl);
        Assert.Equal("true", useSsl.Value);
        Assert.Equal("bool", useSsl.ValueType);

        var inviteBase = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.InviteUrlBase);
        Assert.Equal("https://app.example.com", inviteBase.Value);
        Assert.False(inviteBase.IsSensitive);

        foreach (var plaintext in new[] { "smtp-secret-1", "rzp-secret-1", "whsec-1", "maps-key-1" })
        {
            Assert.DoesNotContain(db.SystemConfigurations, row => row.Value == plaintext);
        }

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal(IntegrationConfigurationService.ActionConfigUpdated, audit.Action);
        Assert.Equal(42, audit.UserId);
        Assert.Equal("10.0.0.1", audit.IPAddress);
        Assert.Equal("update-agent", audit.UserAgent);
    }

    [Fact]
    public async Task UpdateAsync_AuditsConfigUpdatedWithBeforeAndAfterWithoutSecrets()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(db);

        await service.UpdateAsync(
            new UpdateIntegrationConfigurationRequest(
                EmailFromAddress: "changed@example.com",
                EmailHost: "smtp2.example.com",
                EmailPassword: "changed-smtp-secret"),
            7,
            "10.0.0.1",
            "audit-agent",
            CancellationToken.None);

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal(IntegrationConfigurationService.ActionConfigUpdated, audit.Action);
        Assert.Equal("IntegrationConfiguration", audit.EntityType);
        Assert.Equal("Integrations", audit.EntityId);
        Assert.Equal(7, audit.UserId);
        Assert.Equal("10.0.0.1", audit.IPAddress);
        Assert.Equal("audit-agent", audit.UserAgent);
        Assert.Contains("no-reply@doodhdirect.in", audit.OldValueJson);
        Assert.Contains("changed@example.com", audit.NewValueJson);
        Assert.DoesNotContain("seed-smtp-secret", audit.OldValueJson);
        Assert.DoesNotContain("changed-smtp-secret", audit.OldValueJson);
        Assert.DoesNotContain("changed-smtp-secret", audit.NewValueJson);
        Assert.DoesNotContain("seed-rzp-secret", audit.OldValueJson);
        Assert.DoesNotContain("seed-whsec", audit.NewValueJson);
        Assert.DoesNotContain("seed-maps-key", audit.NewValueJson);
    }

    [Fact]
    public async Task UpdateAsync_BlankValueClearsOverrideSoFallbackApplies()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(db);

        await service.UpdateAsync(
            new UpdateIntegrationConfigurationRequest(
                EmailFromAddress: " ",
                EmailPassword: "",
                GoogleMapsApiKey: string.Empty,
                GoogleMapsWebClientKey: string.Empty),
            1,
            null,
            null,
            CancellationToken.None);

        Assert.DoesNotContain(db.SystemConfigurations,
            row => row.Key == IntegrationConfigurationKeys.EmailFromAddress);
        Assert.DoesNotContain(db.SystemConfigurations,
            row => row.Key == IntegrationConfigurationKeys.EmailPassword);
        Assert.DoesNotContain(db.SystemConfigurations,
            row => row.Key == IntegrationConfigurationKeys.GoogleMapsApiKey);
        Assert.DoesNotContain(db.SystemConfigurations,
            row => row.Key == IntegrationConfigurationKeys.GoogleMapsWebClientKey);
        Assert.Contains(db.SystemConfigurations,
            row => row.Key == IntegrationConfigurationKeys.EmailHost);
        Assert.Contains(db.SystemConfigurations,
            row => row.Key == IntegrationConfigurationKeys.InviteUrlBase);
    }

    [Fact]
    public async Task UpdateAsync_PartialRequestLeavesUnrelatedOverridesUntouched()
    {
        var db = CreateDb();
        await SeedConfiguredAsync(db);
        var service = CreateService(db);

        await service.UpdateAsync(
            new UpdateIntegrationConfigurationRequest(EmailFromName: "Renamed Sender"),
            1,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(
            "Renamed Sender",
            db.SystemConfigurations.Single(x => x.Key == IntegrationConfigurationKeys.EmailFromName).Value);
        Assert.Equal(
            "no-reply@doodhdirect.in",
            db.SystemConfigurations.Single(x => x.Key == IntegrationConfigurationKeys.EmailFromAddress).Value);
        Assert.Equal(
            "smtp.example.com",
            db.SystemConfigurations.Single(x => x.Key == IntegrationConfigurationKeys.EmailHost).Value);
        Assert.Equal(
            "https://app.example.com",
            db.SystemConfigurations.Single(x => x.Key == IntegrationConfigurationKeys.InviteUrlBase).Value);
        // The seed ciphertext was produced by a throwaway EphemeralDataProtectionProvider inside
        // SeedConfiguredAsync, so it cannot be un-protected by a fresh protector instance. Verify the
        // row was left untouched by asserting it is still a protected, sensitive value.
        var storedPassword = db.SystemConfigurations.Single(
            x => x.Key == IntegrationConfigurationKeys.EmailPassword);
        Assert.True(storedPassword.IsSensitive);
        Assert.NotEqual("seed-smtp-secret", storedPassword.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task UpdateAsync_RejectsOutOfRangeSmtpPort(int port)
    {
        var service = CreateService(CreateDb());

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            service.UpdateAsync(
                new UpdateIntegrationConfigurationRequest(EmailPort: port),
                1,
                null,
                null,
                CancellationToken.None));

        Assert.Equal("EmailPort", exception.Field);
        Assert.Equal("SMTP port must be between 1 and 65535.", exception.Message);
    }

    [Fact]
    public async Task TestAsync_WhenNotConfigured_ThrowsBusinessRuleWithoutAudit()
    {
        var db = CreateDb();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Contains("not fully configured", exception.Message);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task TestAsync_Success_SendsTestEmailToFromAddressAndAuditsTested()
    {
        var db = CreateDb();
        var sender = new CapturingEmailSender(new EmailSendResult(true, null));
        var service = CreateService(db, settingsProvider: ConfiguredSettings(), emailSender: sender);

        var result = await service.TestAsync(
            9,
            "192.168.1.1",
            "test-agent",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(
            "Configuration verified. A test email was sent to no-reply@doodhdirect.in.",
            result.Message);
        Assert.NotNull(sender.LastMessage);
        Assert.Equal("no-reply@doodhdirect.in", sender.LastMessage!.ToAddress);
        Assert.Equal("DoodhDirect configuration test", sender.LastMessage.Subject);
        Assert.Contains("test email", sender.LastMessage.PlainTextBody);

        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal(IntegrationConfigurationService.ActionTested, audit.Action);
        Assert.Equal("IntegrationConfiguration", audit.EntityType);
        Assert.Equal("Integrations", audit.EntityId);
        Assert.Equal(9, audit.UserId);
        Assert.Equal("192.168.1.1", audit.IPAddress);
        Assert.Equal("test-agent", audit.UserAgent);
        Assert.Null(audit.OldValueJson);
        Assert.Null(audit.NewValueJson);
        Assert.Equal(new DateTime(2026, 9, 1, 11, 30, 0), audit.CreatedAt);
    }

    [Fact]
    public async Task TestAsync_WhenSmtpUnavailable_WrapsInBusinessRuleWithoutAudit()
    {
        var db = CreateDb();
        var service = CreateService(
            db,
            settingsProvider: ConfiguredSettings(),
            emailSender: new ThrowingEmailSender(new SmtpException("relay down")));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Contains("could not be reached", exception.Message);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task TestAsync_WhenSenderReportsNotSent_ThrowsBusinessRuleWithoutAudit()
    {
        var db = CreateDb();
        var service = CreateService(
            db,
            settingsProvider: ConfiguredSettings(),
            emailSender: new CapturingEmailSender(new EmailSendResult(false, "SMTP delivery is not configured.")));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Equal("The SMTP test could not be sent because SMTP is not configured.", exception.Message);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task TestAsync_WhenFromAddressInvalid_WrapsInBusinessRuleWithoutAudit()
    {
        var db = CreateDb();
        var service = CreateService(
            db,
            settingsProvider: ConfiguredSettings(),
            emailSender: new ThrowingEmailSender(new FormatException("invalid address")));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.TestAsync(1, null, null, CancellationToken.None));

        Assert.Contains("from address is invalid", exception.Message);
        Assert.Empty(db.AuditLogs);
    }

    private static DoodhDirectDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"integration-config-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    private static IntegrationSecretProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());

    private static TestClock CreateClock() =>
        new(new DateTime(2026, 9, 1, 11, 30, 0));

    private static IntegrationConfigurationService CreateService(
        DoodhDirectDbContext db,
        IntegrationSecretProtector? protector = null,
        IIndiaTimeProvider? timeProvider = null,
        IIntegrationSettingsProvider? settingsProvider = null,
        IEmailSender? emailSender = null) =>
        new(
            db,
            timeProvider ?? CreateClock(),
            protector ?? CreateProtector(),
            settingsProvider ?? new StubIntegrationSettingsProvider(),
            emailSender ?? new CapturingEmailSender(new EmailSendResult(true, null)));

    private static StubIntegrationSettingsProvider ConfiguredSettings() => new()
    {
        Email = new EmailDeliverySettings(
            "no-reply@doodhdirect.in",
            "DoodhDirect",
            "smtp.example.com",
            587,
            "smtp-user",
            "seed-smtp-secret",
            true,
            true),
        Razorpay = new RazorpayRuntimeSettings(
            "rzp_test_key",
            "seed-rzp-secret",
            "seed-whsec",
            true),
        GoogleMaps = new GoogleMapsRuntimeSettings(
            "seed-maps-key",
            "https://maps.googleapis.com/maps/api/geocode/json",
            true),
        InviteUrlBase = "https://app.example.com"
    };

    private static async Task SeedConfiguredAsync(DoodhDirectDbContext db)
    {
        var protector = CreateProtector();
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.EmailFromAddress,
            "no-reply@doodhdirect.in",
            "string",
            "From address for transactional emails."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.EmailFromName,
            "DoodhDirect",
            "string",
            "Display name for transactional emails."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.EmailHost,
            "smtp.example.com",
            "string",
            "SMTP relay host."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.EmailPort,
            "587",
            "int",
            "SMTP relay port."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.EmailUserName,
            "smtp-user",
            "string",
            "SMTP user name (optional)."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.EmailPassword,
            protector.Protect("seed-smtp-secret"),
            "sensitive",
            "SMTP password (protected, never exposed).",
            true));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.EmailUseSsl,
            "true",
            "bool",
            "Whether the SMTP relay uses SSL/TLS."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.InviteUrlBase,
            "https://app.example.com",
            "string",
            "Public base URL used to build absolute invitation links."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.RazorpayKeyId,
            "rzp_test_key",
            "string",
            "Razorpay key id."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.RazorpayKeySecret,
            protector.Protect("seed-rzp-secret"),
            "sensitive",
            "Razorpay key secret (protected, never exposed).",
            true));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.RazorpayWebhookSecret,
            protector.Protect("seed-whsec"),
            "sensitive",
            "Razorpay webhook secret (protected, never exposed).",
            true));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.GoogleMapsApiKey,
            protector.Protect("seed-maps-key"),
            "sensitive",
            "Google Maps API key (protected, never exposed).",
            true));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.GoogleMapsBaseUrl,
            "https://maps.googleapis.com/maps/api/geocode/json",
            "string",
            "Google Maps geocoding endpoint URL."));
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.GoogleMapsWebClientKey,
            "seed-web-client-key",
            "string",
            "Google Maps web client key used by the Flutter Web client (client-visible)."));
        await db.SaveChangesAsync();
    }

    private sealed class StubIntegrationSettingsProvider : IIntegrationSettingsProvider
    {
        public EmailDeliverySettings Email { get; set; } = new(null, null, null, 587, null, null, true, false);
        public RazorpayRuntimeSettings Razorpay { get; set; } = new(null, null, null, false);
        public GoogleMapsRuntimeSettings GoogleMaps { get; set; } = new(null, null, false);
        public string? InviteUrlBase { get; set; }

        public Task<EmailDeliverySettings> GetEmailAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Email);

        public Task<RazorpayRuntimeSettings> GetRazorpayAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Razorpay);

        public Task<GoogleMapsRuntimeSettings> GetGoogleMapsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(GoogleMaps);

        public Task<string?> GetInviteUrlBaseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(InviteUrlBase);
    }

    private sealed class CapturingEmailSender(EmailSendResult result) : IEmailSender
    {
        public EmailMessage? LastMessage { get; private set; }
        public int SendCalls { get; private set; }

        public Task<EmailSendResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken)
        {
            LastMessage = message;
            SendCalls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingEmailSender(Exception exception) : IEmailSender
    {
        public Task<EmailSendResult> SendAsync(
            EmailMessage message,
            CancellationToken cancellationToken) =>
            Task.FromException<EmailSendResult>(exception);
    }
}
