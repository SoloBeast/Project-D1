using System.Security.Cryptography;
using DoodhDirect.Application.Integrations;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DoodhDirect.Infrastructure.Integrations;

/// <summary>
/// Resolves the effective integration settings at call time. A database value
/// (SystemConfiguration, key prefix <c>Integration.</c>) overrides the
/// appsettings/environment fallback whenever it is present and non-empty.
/// Sensitive values are decrypted only when a call is being made and are never
/// cached or logged. Fails closed (IsConfigured = false) when a secret cannot be
/// decrypted or any required value is missing.
/// </summary>
public sealed class IntegrationSettingsProvider(
    DoodhDirectDbContext dbContext,
    IConfiguration configuration,
    IntegrationSecretProtector secretProtector) : IIntegrationSettingsProvider
{
    private const string DefaultGoogleMapsBaseUrl = "https://maps.googleapis.com/maps/api/geocode/json";

    public async Task<EmailDeliverySettings> GetEmailAsync(CancellationToken cancellationToken)
    {
        var fromAddress = ReadFallback(
            await ReadStringAsync(IntegrationConfigurationKeys.EmailFromAddress, cancellationToken),
            configuration["Email:FromAddress"]);
        var fromName = ReadFallback(
            await ReadStringAsync(IntegrationConfigurationKeys.EmailFromName, cancellationToken),
            configuration["Email:FromName"]);
        var host = ReadFallback(
            await ReadStringAsync(IntegrationConfigurationKeys.EmailHost, cancellationToken),
            configuration["Email:Host"]);
        var port = ReadPort(
            await ReadStringAsync(IntegrationConfigurationKeys.EmailPort, cancellationToken),
            configuration["Email:Port"],
            587);
        var userName = ReadFallback(
            await ReadStringAsync(IntegrationConfigurationKeys.EmailUserName, cancellationToken),
            configuration["Email:UserName"]);
        var useSsl = ReadBool(
            await ReadStringAsync(IntegrationConfigurationKeys.EmailUseSsl, cancellationToken),
            configuration["Email:UseSsl"],
            true);

        var password = await ReadSecretAsync(
            IntegrationConfigurationKeys.EmailPassword,
            configuration["Email:Password"],
            cancellationToken);

        var isConfigured =
            !string.IsNullOrWhiteSpace(fromAddress)
            && !string.IsNullOrWhiteSpace(host)
            && port is > 0 and <= 65535;

        return new EmailDeliverySettings(
            fromAddress,
            fromName,
            host,
            port,
            userName,
            password,
            useSsl,
            isConfigured);
    }

    public async Task<RazorpayRuntimeSettings> GetRazorpayAsync(CancellationToken cancellationToken)
    {
        var keyId = ReadFallback(
            await ReadStringAsync(IntegrationConfigurationKeys.RazorpayKeyId, cancellationToken),
            configuration["Payments:RazorpayKeyId"]);
        var keySecret = await ReadSecretAsync(
            IntegrationConfigurationKeys.RazorpayKeySecret,
            configuration["Payments:RazorpayKeySecret"],
            cancellationToken);
        var webhookSecret = await ReadSecretAsync(
            IntegrationConfigurationKeys.RazorpayWebhookSecret,
            configuration["Payments:RazorpayWebhookSecret"],
            cancellationToken);

        return new RazorpayRuntimeSettings(
            keyId,
            keySecret,
            webhookSecret,
            !string.IsNullOrWhiteSpace(keyId) && !string.IsNullOrWhiteSpace(keySecret));
    }

    public async Task<GoogleMapsRuntimeSettings> GetGoogleMapsAsync(CancellationToken cancellationToken)
    {
        var apiKey = await ReadSecretAsync(
            IntegrationConfigurationKeys.GoogleMapsApiKey,
            configuration["AddressGeocoding:ApiKey"],
            cancellationToken);
        var baseUrl = ReadFallback(
            await ReadStringAsync(IntegrationConfigurationKeys.GoogleMapsBaseUrl, cancellationToken),
            configuration["AddressGeocoding:BaseUrl"],
            DefaultGoogleMapsBaseUrl);

        var isConfigured =
            !string.IsNullOrWhiteSpace(apiKey)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            && (baseUri.Scheme == Uri.UriSchemeHttps || baseUri.Scheme == Uri.UriSchemeHttp);

        return new GoogleMapsRuntimeSettings(apiKey, baseUrl, isConfigured);
    }

    public async Task<string?> GetInviteUrlBaseAsync(CancellationToken cancellationToken) =>
        ReadFallback(
            await ReadStringAsync(IntegrationConfigurationKeys.InviteUrlBase, cancellationToken),
            configuration["Integrations:InviteUrlBase"]);

    private async Task<string?> ReadSecretAsync(
        string key,
        string? fallback,
        CancellationToken cancellationToken)
    {
        var stored = await ReadStringAsync(key, cancellationToken);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                return secretProtector.Unprotect(stored);
            }
            catch (CryptographicException)
            {
                // Treat an undecryptable stored secret as absent so the caller fails
                // closed instead of crashing with the persisted ciphertext.
                return null;
            }
        }

        return ReadFallback(stored, fallback);
    }

    private async Task<string?> ReadStringAsync(string key, CancellationToken cancellationToken) =>
        await dbContext.SystemConfigurations
            .AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => (string?)x.Value)
            .SingleOrDefaultAsync(cancellationToken);

    private static string? ReadFallback(string? primary, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary))
        {
            return primary.Trim();
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }

    private static string ReadFallback(string? primary, string? fallback, string defaultValue) =>
        ReadFallback(primary, fallback) ?? defaultValue;

    private static int ReadPort(string? primary, string? fallback, int defaultValue)
    {
        if (int.TryParse(primary, out var primaryPort) && primaryPort is > 0 and <= 65535)
        {
            return primaryPort;
        }

        if (int.TryParse(fallback, out var fallbackPort) && fallbackPort is > 0 and <= 65535)
        {
            return fallbackPort;
        }

        return defaultValue;
    }

    private static bool ReadBool(string? primary, string? fallback, bool defaultValue)
    {
        if (bool.TryParse(primary, out var primaryBool))
        {
            return primaryBool;
        }

        if (bool.TryParse(fallback, out var fallbackBool))
        {
            return fallbackBool;
        }

        return defaultValue;
    }
}
