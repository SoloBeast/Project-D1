using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.OtpProvider;

/// <summary>
/// Admin configuration service for the MSG91 OTP provider. Persists settings in
/// SystemConfiguration. The AuthKey is protected (encrypted at rest) via
/// OtpProviderSecretProtector and is NEVER returned by any read API. Every
/// mutation is audited with non-secret metadata only.
/// </summary>
public sealed class OtpProviderConfigurationService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    OtpProviderSecretProtector secretProtector,
    IMsg91OtpProvider msg91Provider) : IOtpProviderConfigurationService
{
    public const string ActionConfigUpdated = "OTP_PROVIDER.CONFIG_UPDATED";
    public const string ActionEnabled = "OTP_PROVIDER.ENABLED";
    public const string ActionDisabled = "OTP_PROVIDER.DISABLED";
    public const string ActionTested = "OTP_PROVIDER.TESTED";

    private const string EntityType = "OtpProviderConfiguration";
    private const string EntityId = "MSG91";

    public async Task<OtpProviderConfigurationResult> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken);
        var enabled = ReadBool(settings, OtpProviderConfigurationKeys.Enabled);
        var configured =
            HasValue(settings, OtpProviderConfigurationKeys.WidgetId)
            && HasValue(settings, OtpProviderConfigurationKeys.AuthKey);

        return ToResult(
            ReadString(settings, OtpProviderConfigurationKeys.Provider) ?? OtpProviderConfiguration.ProviderMsg91,
            enabled,
            ReadString(settings, OtpProviderConfigurationKeys.WidgetId),
            ReadString(settings, OtpProviderConfigurationKeys.Environment),
            configured);
    }

    public async Task<OtpProviderConfigurationResult> UpdateAsync(
        UpdateOtpProviderConfigurationRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await LoadAsync(cancellationToken);
        var beforeEnabled = ReadBool(settings, OtpProviderConfigurationKeys.Enabled);
        var beforeConfigured =
            HasValue(settings, OtpProviderConfigurationKeys.WidgetId)
            && HasValue(settings, OtpProviderConfigurationKeys.AuthKey);

        var now = timeProvider.Now;
        var provider = ReadString(settings, OtpProviderConfigurationKeys.Provider)
            ?? OtpProviderConfiguration.ProviderMsg91;

        // Provider field is fixed to MSG91 for now; the row is created on first
        // update so the read path can resolve a clean default.
        await EnsureSettingAsync(
            OtpProviderConfigurationKeys.Provider,
            provider,
            "string",
            "OTP provider (MSG91).",
            now,
            cancellationToken);

        var enabled = request.Enabled ?? beforeEnabled;
        await SetValueAsync(
            settings,
            OtpProviderConfigurationKeys.Enabled,
            enabled ? "true" : "false",
            "bool",
            "Whether the OTP provider is enabled for sending identity OTPs.",
            now,
            cancellationToken);

        if (request.WidgetId is not null)
        {
            var normalizedWidgetId = NormalizeRequired(request.WidgetId, "widgetId");
            await SetValueAsync(
                settings,
                OtpProviderConfigurationKeys.WidgetId,
                normalizedWidgetId,
                "string",
                "MSG91 OTP widget identifier.",
                now,
                cancellationToken);
        }

        if (request.Environment is not null)
        {
            var normalizedEnvironment = NormalizeEnvironment(request.Environment);
            await SetValueAsync(
                settings,
                OtpProviderConfigurationKeys.Environment,
                normalizedEnvironment,
                "string",
                "MSG91 environment (Test or Production).",
                now,
                cancellationToken);
        }

        if (request.AuthKey is not null)
        {
            if (string.IsNullOrWhiteSpace(request.AuthKey))
            {
                throw new ValidationAppException(
                    "The MSG91 AuthKey cannot be empty when provided.",
                    "authKey");
            }

            var protectedAuthKey = secretProtector.Protect(request.AuthKey);
            await SetValueAsync(
                settings,
                OtpProviderConfigurationKeys.AuthKey,
                protectedAuthKey,
                "sensitive",
                "MSG91 auth key (protected, never exposed).",
                now,
                cancellationToken);
        }

        var afterConfigured =
            HasValue(settings, OtpProviderConfigurationKeys.WidgetId)
            && HasValue(settings, OtpProviderConfigurationKeys.AuthKey);

        var action = ActionConfigUpdated;
        if (beforeEnabled != enabled)
        {
            action = enabled ? ActionEnabled : ActionDisabled;
        }

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            action,
            EntityType,
            EntityId,
            JsonSerializer.Serialize(new
            {
                enabled = beforeEnabled,
                configured = beforeConfigured
            }),
            JsonSerializer.Serialize(new
            {
                enabled,
                configured = afterConfigured
            }),
            ipAddress,
            userAgent,
            "OTP provider configuration updated.",
            now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResult(
            provider,
            enabled,
            ReadString(settings, OtpProviderConfigurationKeys.WidgetId),
            ReadString(settings, OtpProviderConfigurationKeys.Environment),
            afterConfigured);
    }

    public async Task<OtpProviderTestResult> TestAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var settings = await LoadAsync(cancellationToken);
        var enabled = ReadBool(settings, OtpProviderConfigurationKeys.Enabled);
        var configured =
            HasValue(settings, OtpProviderConfigurationKeys.WidgetId)
            && HasValue(settings, OtpProviderConfigurationKeys.AuthKey);

        if (!configured)
        {
            throw new BusinessRuleException(
                "The OTP provider is not fully configured. Save a widget id and auth key before testing.");
        }

        if (!enabled)
        {
            throw new BusinessRuleException(
                "The OTP provider is disabled. Enable it before testing.");
        }

        string message;
        try
        {
            await msg91Provider.SendAsync(
                new Msg91OtpSendRequest(
                    "0000000000",
                    "configuration-test"),
                cancellationToken);
            message = "Configuration verified. A test OTP was sent successfully.";
        }
        catch (OtpProviderUnavailableException)
        {
            throw;
        }
        catch (OtpProviderRejectedException)
        {
            // The provider answered but rejected the request (invalid/expired
            // authkey or widget id). Surface the rejection with its diagnostic
            // instead of wrapping it as an availability error.
            throw;
        }
        catch (Exception)
        {
            throw new OtpProviderUnavailableException(
                "The OTP service could not be reached. Verify the widget id and auth key are correct and the environment allows this request.");
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
            "OTP provider configuration test.",
            timeProvider.Now));

        await dbContext.SaveChangesAsync(cancellationToken);

        return new OtpProviderTestResult(true, message);
    }

    private async Task<Dictionary<string, SystemConfiguration>> LoadAsync(
        CancellationToken cancellationToken) =>
        await dbContext.SystemConfigurations
            .AsNoTracking()
            .Where(x => x.Key.StartsWith("OtpProvider."))
            .ToDictionaryAsync(x => x.Key, StringComparer.Ordinal, cancellationToken);

    private async Task EnsureSettingAsync(
        string key,
        string value,
        string valueType,
        string description,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.SystemConfigurations
            .AnyAsync(x => x.Key == key, cancellationToken);
        if (!exists)
        {
            dbContext.SystemConfigurations.Add(new SystemConfiguration(
                key,
                value,
                valueType,
                description,
                string.Equals(valueType, "sensitive", StringComparison.OrdinalIgnoreCase)));
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

        // Keep the in-memory snapshot in sync so the response can reflect the
        // persisted state immediately (including rows created on this request).
        settings[key] = entity;
    }

    private static string NormalizeRequired(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException(
                $"The {field} is required and cannot be empty.",
                field);
        }

        return value.Trim();
    }

    private static string NormalizeEnvironment(string? value)
    {
        var normalized = value?.Trim();
        if (string.Equals(normalized, OtpProviderConfiguration.EnvironmentTest, StringComparison.OrdinalIgnoreCase))
        {
            return OtpProviderConfiguration.EnvironmentTest;
        }

        if (string.Equals(normalized, OtpProviderConfiguration.EnvironmentProduction, StringComparison.OrdinalIgnoreCase))
        {
            return OtpProviderConfiguration.EnvironmentProduction;
        }

        throw new ValidationAppException(
            $"Environment must be '{OtpProviderConfiguration.EnvironmentTest}' or '{OtpProviderConfiguration.EnvironmentProduction}'.",
            "environment");
    }

    private static string? ReadString(
        IReadOnlyDictionary<string, SystemConfiguration> settings,
        string key) =>
        settings.TryGetValue(key, out var entity) ? entity.Value : null;

    private static bool ReadBool(
        IReadOnlyDictionary<string, SystemConfiguration> settings,
        string key) =>
        string.Equals(ReadString(settings, key), "true", StringComparison.OrdinalIgnoreCase);

    private static bool HasValue(
        IReadOnlyDictionary<string, SystemConfiguration> settings,
        string key) =>
        !string.IsNullOrWhiteSpace(ReadString(settings, key));

    private static OtpProviderConfigurationResult ToResult(
        string provider,
        bool enabled,
        string? widgetId,
        string? environment,
        bool configured) =>
        new(
            provider,
            enabled,
            widgetId,
            environment,
            configured,
            configured ? "Configured" : "Not Configured");
}
