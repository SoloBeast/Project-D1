using DoodhDirect.Application.Integrations;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.Integrations;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ClientConfigurationServiceTests
{
    [Fact]
    public async Task GetGoogleMapsWebClientKey_WhenStoredPlain_ReturnsStoredValue()
    {
        var db = CreateDb();
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.GoogleMapsWebClientKey,
            "AIza-web-client-key",
            "string",
            "Google Maps web client key used by the Flutter Web client (client-visible)."));
        await db.SaveChangesAsync();
        var service = CreateService(db, fallbackWebClientKey: null);

        var key = await service.GetGoogleMapsWebClientKeyAsync(CancellationToken.None);

        Assert.Equal("AIza-web-client-key", key);
    }

    [Fact]
    public async Task GetGoogleMapsWebClientKey_WhenStoredValueIsWhitespace_FallsBackToConfiguration()
    {
        var db = CreateDb();
        db.SystemConfigurations.Add(new SystemConfiguration(
            IntegrationConfigurationKeys.GoogleMapsWebClientKey,
            "   ",
            "string",
            "Google Maps web client key used by the Flutter Web client (client-visible)."));
        await db.SaveChangesAsync();
        var service = CreateService(db, fallbackWebClientKey: "AIza-fallback-key");

        var key = await service.GetGoogleMapsWebClientKeyAsync(CancellationToken.None);

        Assert.Equal("AIza-fallback-key", key);
    }

    [Fact]
    public async Task GetGoogleMapsWebClientKey_WhenNotStored_UsesConfigurationFallback()
    {
        var db = CreateDb();
        var service = CreateService(db, fallbackWebClientKey: "AIza-fallback-key");

        var key = await service.GetGoogleMapsWebClientKeyAsync(CancellationToken.None);

        Assert.Equal("AIza-fallback-key", key);
    }

    [Fact]
    public async Task GetGoogleMapsWebClientKey_WhenNotConfiguredAnywhere_ReturnsNull()
    {
        var db = CreateDb();
        var service = CreateService(db, fallbackWebClientKey: null);

        var key = await service.GetGoogleMapsWebClientKeyAsync(CancellationToken.None);

        Assert.Null(key);
    }

    private static DoodhDirectDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"client-config-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    private static ClientConfigurationService CreateService(
        DoodhDirectDbContext db,
        string? fallbackWebClientKey)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AddressGeocoding:WebClientApiKey"] = fallbackWebClientKey
            })
            .Build();
        return new ClientConfigurationService(db, configuration);
    }
}
