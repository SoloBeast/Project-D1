using System.Security.Cryptography;
using DoodhDirect.Application.Identity;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.OtpProvider;

/// <summary>
/// Resolves the current MSG91 provider settings from SystemConfiguration at call
/// time. The AuthKey is decrypted only when a request is being made and is never
/// cached or logged. Fails closed (IsConfigured = false) when the key cannot be
/// decrypted or any required value is missing.
/// </summary>
public sealed class Msg91ProviderSettingsProvider(
    DoodhDirectDbContext dbContext,
    OtpProviderSecretProtector secretProtector) : IMsg91ProviderSettingsProvider
{
    public async Task<Msg91ProviderSettings> GetAsync(CancellationToken cancellationToken)
    {
        var provider = await ReadStringAsync(OtpProviderConfigurationKeys.Provider, cancellationToken);
        var enabled = await ReadBoolAsync(OtpProviderConfigurationKeys.Enabled, cancellationToken);
        var widgetId = await ReadStringAsync(OtpProviderConfigurationKeys.WidgetId, cancellationToken);
        var protectedAuthKey = await ReadStringAsync(OtpProviderConfigurationKeys.AuthKey, cancellationToken);
        var environment = await ReadStringAsync(OtpProviderConfigurationKeys.Environment, cancellationToken);

        string? authKey = null;
        if (!string.IsNullOrWhiteSpace(protectedAuthKey))
        {
            try
            {
                authKey = secretProtector.Unprotect(protectedAuthKey);
            }
            catch (CryptographicException)
            {
                authKey = null;
            }
        }

        var isConfigured =
            string.Equals(provider, OtpProviderConfiguration.ProviderMsg91, StringComparison.OrdinalIgnoreCase)
            && enabled
            && !string.IsNullOrWhiteSpace(widgetId)
            && !string.IsNullOrWhiteSpace(authKey);

        return new Msg91ProviderSettings(widgetId, authKey, environment, isConfigured);
    }

    private async Task<string?> ReadStringAsync(string key, CancellationToken cancellationToken) =>
        await dbContext.SystemConfigurations
            .AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => (string?)x.Value)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<bool> ReadBoolAsync(string key, CancellationToken cancellationToken)
    {
        var value = await ReadStringAsync(key, cancellationToken);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
