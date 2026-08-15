using DoodhDirect.Application.Customer;
using Microsoft.Extensions.Logging;

namespace DoodhDirect.Infrastructure.Customer;

public sealed class UnconfiguredAddressLocationLookup(
    ILogger<UnconfiguredAddressLocationLookup> logger) : IAddressLocationLookup
{
    public Task<AddressLookupResult?> ReverseGeocodeAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Address lookup requested for coordinates {Latitude}, {Longitude}, but no geocoding provider is configured.",
            latitude,
            longitude);
        return Task.FromResult<AddressLookupResult?>(null);
    }
}
