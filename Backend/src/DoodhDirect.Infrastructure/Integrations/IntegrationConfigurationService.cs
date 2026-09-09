using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Integrations;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Integrations;

/// <summary>
/// Admin configuration service for third-party integrations (SMTP email delivery,
/// Razorpay, Google Maps) and the invitation-link base URL. Settings are persisted
/// in SystemConfiguration under the <c>Integration.</c> key prefix and are read at
/// call time by <see cref="IIntegrationSettingsProvider"/>, so the backend picks the
/// latest values whenever a feature needs them.
///
/// Semantics per request field (all fields optional):
///   null       -> leave the database override unchanged;
///   blank      -> clear the database override so the appsettings/environment
///                 fallback applies again;
///   non-blank  -> write (or replace) the database override.
///
/// Sensitive values (SMTP password, Razorpay secrets, Google Maps key) are protected
/// (encrypted at rest) via <see cref="IntegrationSecretProtector"/> and are NEVER
/// returned by any read API; only their Configured flags are exposed. Every mutation
/// is audited with non-secret metadata only.
/// </summary>
public sealed class IntegrationConfigurationService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    IntegrationSecretProtector secretProtector,
    IIntegrationSettingsProvider settingsProvider,
    IEmailSender emailSender) : IIntegrationConfigurationService
{
    public const string ActionConfigUpdated = "INTEGRATION.CONFIG_UPDATED";
    public const string ActionTested = "INTEGRATION.TESTED";

    private const string EntityType = "IntegrationConfiguration";
    private const string EntityId = "Integrations";

    public async Task<IntegrationConfigurationResult> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken);
        return await ToResultAsync(settings, cancellationToken);
    }

    public async Task<IntegrationConfigurationResult> UpdateAsync(
        UpdateIntegrationConfigurationRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await LoadAsync(cancellationToken);
        var before = Snapshot(settings);
        var now = timeProvider.Now;

        // Email (SMTP) delivery.
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.EmailFromAddress, request.EmailFromAddress, "string", "From address for transactional emails.", now, cancellationToken);
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.EmailFromName, request.EmailFromName, "string", "Display name for transactional emails.", now, cancellationToken);
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.EmailHost, request.EmailHost, "string", "SMTP relay host.", now, cancellationToken);
        if (request.EmailPort.HasValue)
        {
            if (request.EmailPort.Value is < 1 or > 65535)
            {
                throw new ValidationAppException("SMTP port must be between 1 and 65535.", nameof(request.EmailPort));
            }

            await SetValueAsync(
                settings,
                IntegrationConfigurationKeys.EmailPort,
                request.EmailPort.Value.ToString(CultureInfo.InvariantCulture),
                "int",
                "SMTP relay port.",
                now,
                cancellationToken);
        }

        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.EmailUserName, request.EmailUserName, "string", "SMTP user name (optional).", now, cancellationToken);
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.EmailPassword, request.EmailPassword, "sensitive", "SMTP password (protected, never exposed).", now, cancellationToken);
        if (request.EmailUseSsl.HasValue)
        {
            await SetValueAsync(
                settings,
                IntegrationConfigurationKeys.EmailUseSsl,
                request.EmailUseSsl.Value ? "true" : "false",
                "bool",
                "Whether the SMTP relay uses SSL/TLS.",
                now,
                cancellationToken);
        }

        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.InviteUrlBase, request.InviteUrlBase, "string", "Public base URL used to build absolute invitation links.", now, cancellationToken);

        // Razorpay.
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.RazorpayKeyId, request.RazorpayKeyId, "string", "Razorpay key id.", now, cancellationToken);
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.RazorpayKeySecret, request.RazorpayKeySecret, "sensitive", "Razorpay key secret (protected, never exposed).", now, cancellationToken);
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.RazorpayWebhookSecret, request.RazorpayWebhookSecret, "sensitive", "Razorpay webhook secret (protected, never exposed).", now, cancellationToken);

        // Google Maps (server-side reverse geocoding).
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.GoogleMapsApiKey, request.GoogleMapsApiKey, "sensitive", "Google Maps API key (protected, never exposed).", now, cancellationToken);
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.GoogleMapsBaseUrl, request.GoogleMapsBaseUrl, "string", "Google Maps geocoding endpoint URL.", now, cancellationToken);
        await ApplyStringSettingAsync(settings, IntegrationConfigurationKeys.GoogleMapsWebClientKey, request.GoogleMapsWebClientKey, "string", "Google Maps web client key used by the Flutter Web client (client-visible).", now, cancellationToken);

        var after = Snapshot(settings);

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionConfigUpdated,
            EntityType,
            EntityId,
            JsonSerializer.Serialize(before),
            JsonSerializer.Serialize(after),
            ipAddress,
            userAgent,
            "Integration configuration updated.",
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetAsync(cancellationToken);
    }

    public async Task<IntegrationTestResult> TestAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var email = await settingsProvider.GetEmailAsync(cancellationToken);
        if (!email.IsConfigured || string.IsNullOrWhiteSpace(email.FromAddress))
        {
            throw new BusinessRuleException(
                "SMTP email is not fully configured. Save a from address and SMTP host before testing.");
        }

        try
        {
            var sendResult = await emailSender.SendAsync(
                new EmailMessage(
                    email.FromAddress,
                    "DoodhDirect configuration test",
                    "This is a test email confirming that SMTP delivery is configured correctly for DoodhDirect.",
                    "<p>This is a test email confirming that SMTP delivery is configured correctly for <b>DoodhDirect</b>.</p>"),
                cancellationToken);

            if (!sendResult.Sent)
            {
                throw new BusinessRuleException(
                    "The SMTP test could not be sent because SMTP is not configured.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BusinessRuleException)
        {
            throw;
        }
        catch (SmtpException)
        {
            throw new BusinessRuleException(
                "The SMTP server could not be reached or rejected the request. Verify the host, port, user name, password, and SSL settings.");
        }
        catch (FormatException)
        {
            throw new BusinessRuleException(
                "The SMTP from address is invalid. Verify the from address is a well-formed email address.");
        }
        catch (Exception)
        {
            throw new BusinessRuleException(
                "The SMTP test failed. Verify the relay settings and that outbound port is reachable from this server.");
        }

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionTested,
            EntityType,
            EntityId,
            null,
            null,
            ipAddress,
            userAgent,
            "Integration (SMTP email) configuration test.",
            timeProvider.Now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return new IntegrationTestResult(
            true,
            $"Configuration verified. A test email was sent to {email.FromAddress}.");
    }

    private async Task<Dictionary<string, SystemConfiguration>> LoadAsync(
        CancellationToken cancellationToken) =>
        await dbContext.SystemConfigurations
            .AsNoTracking()
            .Where(x => x.Key.StartsWith("Integration."))
            .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, cancellationToken);

    private async Task ApplyStringSettingAsync(
        Dictionary<string, SystemConfiguration> settings,
        string key,
        string? requestValue,
        string valueType,
        string description,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (requestValue is null)
        {
            // Field was not provided: leave the database override untouched.
            return;
        }

        if (string.IsNullOrWhiteSpace(requestValue))
        {
            // Field was provided blank: clear the override so the
            // appsettings/environment fallback applies again.
            await ClearSettingAsync(settings, key, cancellationToken);
            return;
        }

        var value = requestValue.Trim();
        if (string.Equals(valueType, "sensitive", StringComparison.OrdinalIgnoreCase))
        {
            value = secretProtector.Protect(value);
        }

        await SetValueAsync(settings, key, value, valueType, description, now, cancellationToken);
    }

    private async Task ClearSettingAsync(
        Dictionary<string, SystemConfiguration> settings,
        string key,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SystemConfigurations
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is not null)
        {
            dbContext.SystemConfigurations.Remove(entity);
            settings.Remove(key);
        }
    }

    private async Task SetValueAsync(
        Dictionary<string, SystemConfiguration> settings,
        string key,
        string value,
        string valueType,
        string description,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SystemConfigurations
            .SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (entity is null)
        {
            entity = new SystemConfiguration(
                key,
                value,
                valueType,
                description,
                string.Equals(valueType, "sensitive", StringComparison.OrdinalIgnoreCase));
            dbContext.SystemConfigurations.Add(entity);
        }
        else
        {
            // UpdateValue -> SetUpdated requires an India-local wall-clock value
            // (unspecified kind); timeProvider.Now already is that value.
            entity.UpdateValue(value, now);
        }

        // Keep the in-memory snapshot in sync so the audit after-state reflects the
        // persisted state immediately (including rows created on this request).
        settings[key] = entity;
    }

    private static object Snapshot(IReadOnlyDictionary<string, SystemConfiguration> settings) => new
    {
        email = new
        {
            fromAddress = ReadString(settings, IntegrationConfigurationKeys.EmailFromAddress),
            fromName = ReadString(settings, IntegrationConfigurationKeys.EmailFromName),
            host = ReadString(settings, IntegrationConfigurationKeys.EmailHost),
            port = ReadPort(settings),
            userName = ReadString(settings, IntegrationConfigurationKeys.EmailUserName),
            useSsl = ReadBool(settings, IntegrationConfigurationKeys.EmailUseSsl),
            passwordConfigured = HasValue(settings, IntegrationConfigurationKeys.EmailPassword)
        },
        inviteUrlBase = ReadString(settings, IntegrationConfigurationKeys.InviteUrlBase),
        razorpay = new
        {
            keyId = ReadString(settings, IntegrationConfigurationKeys.RazorpayKeyId),
            keySecretConfigured = HasValue(settings, IntegrationConfigurationKeys.RazorpayKeySecret),
            webhookSecretConfigured = HasValue(settings, IntegrationConfigurationKeys.RazorpayWebhookSecret)
        },
        googleMaps = new
        {
            baseUrl = ReadString(settings, IntegrationConfigurationKeys.GoogleMapsBaseUrl),
            apiKeyConfigured = HasValue(settings, IntegrationConfigurationKeys.GoogleMapsApiKey),
            webClientKey = ReadString(settings, IntegrationConfigurationKeys.GoogleMapsWebClientKey)
        }
    };

    private async Task<IntegrationConfigurationResult> ToResultAsync(
        IReadOnlyDictionary<string, SystemConfiguration> settings,
        CancellationToken cancellationToken)
    {
        var email = await settingsProvider.GetEmailAsync(cancellationToken);
        var razorpay = await settingsProvider.GetRazorpayAsync(cancellationToken);
        var googleMaps = await settingsProvider.GetGoogleMapsAsync(cancellationToken);

        return new IntegrationConfigurationResult(
            EmailFromAddress: ReadString(settings, IntegrationConfigurationKeys.EmailFromAddress),
            EmailFromName: ReadString(settings, IntegrationConfigurationKeys.EmailFromName),
            EmailHost: ReadString(settings, IntegrationConfigurationKeys.EmailHost),
            EmailPort: ReadPort(settings),
            EmailUserName: ReadString(settings, IntegrationConfigurationKeys.EmailUserName),
            EmailUseSsl: ReadBool(settings, IntegrationConfigurationKeys.EmailUseSsl),
            EmailPasswordConfigured: !string.IsNullOrWhiteSpace(email.Password),
            EmailIsConfigured: email.IsConfigured,
            InviteUrlBase: ReadString(settings, IntegrationConfigurationKeys.InviteUrlBase),
            RazorpayKeyId: ReadString(settings, IntegrationConfigurationKeys.RazorpayKeyId),
            RazorpayKeySecretConfigured: !string.IsNullOrWhiteSpace(razorpay.KeySecret),
            RazorpayWebhookSecretConfigured: !string.IsNullOrWhiteSpace(razorpay.WebhookSecret),
            RazorpayIsConfigured: razorpay.IsConfigured,
            GoogleMapsBaseUrl: ReadString(settings, IntegrationConfigurationKeys.GoogleMapsBaseUrl),
            GoogleMapsApiKeyConfigured: !string.IsNullOrWhiteSpace(googleMaps.ApiKey),
            GoogleMapsIsConfigured: googleMaps.IsConfigured,
            GoogleMapsWebClientKey: ReadString(settings, IntegrationConfigurationKeys.GoogleMapsWebClientKey));
    }

    private static string? ReadString(
        IReadOnlyDictionary<string, SystemConfiguration> settings,
        string key) =>
        settings.TryGetValue(key, out var entity) ? entity.Value : null;

    private static bool ReadBool(
        IReadOnlyDictionary<string, SystemConfiguration> settings,
        string key) =>
        string.Equals(ReadString(settings, key), "true", StringComparison.OrdinalIgnoreCase);

    private static int ReadPort(IReadOnlyDictionary<string, SystemConfiguration> settings)
    {
        var value = ReadString(settings, IntegrationConfigurationKeys.EmailPort);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : 587;
    }

    private static bool HasValue(
        IReadOnlyDictionary<string, SystemConfiguration> settings,
        string key) =>
        !string.IsNullOrWhiteSpace(ReadString(settings, key));
}
