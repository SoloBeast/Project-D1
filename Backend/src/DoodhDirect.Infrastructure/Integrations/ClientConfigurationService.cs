using DoodhDirect.Application.Integrations;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DoodhDirect.Infrastructure.Integrations;

/// <summary>
/// Resolves configuration values that are safe to return to authenticated Flutter clients.
/// This surface intentionally exposes ONLY values that are public by design (currently the
/// Google Maps Web Client Key, which is restricted by HTTP referrer rather than secrecy).
/// Server-only secrets (Google Maps server-side key, SMTP credentials, Razorpay secrets,
/// MSG91 AuthKey, database credentials, JWT signing key) are never returned here.
/// </summary>
public sealed class ClientConfigurationService(
    DoodhDirectDbContext dbContext,
    IConfiguration configuration) : IClientConfigurationService
{
    public async Task<string?> GetGoogleMapsWebClientKeyAsync(CancellationToken cancellationToken)
    {
        var stored = await dbContext.SystemConfigurations
            .AsNoTracking()
            .Where(x => x.Key == IntegrationConfigurationKeys.GoogleMapsWebClientKey)
            .Select(x => (string?)x.Value)
            .SingleOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored.Trim();
        }

        var fallback = configuration["AddressGeocoding:WebClientApiKey"];
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }
}
