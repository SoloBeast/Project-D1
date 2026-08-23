using System.ComponentModel.DataAnnotations;

namespace DoodhDirect.Infrastructure.Customer;

public sealed class AddressGeocodingOptions
{
    public const string SectionName = "AddressGeocoding";

    public string Provider { get; init; } = "Google";

    public string? ApiKey { get; init; }

    public string BaseUrl { get; init; } = "https://maps.googleapis.com/maps/api/geocode/json";

    [Range(1, 60)]
    public int TimeoutSeconds { get; init; } = 10;

    public bool IsGoogle => string.Equals(Provider, "Google", StringComparison.OrdinalIgnoreCase);
}
