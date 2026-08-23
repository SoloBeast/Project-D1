using System.Globalization;
using System.Text.Json;
using DoodhDirect.Application.Customer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.Customer;

public sealed class GoogleAddressLocationLookup(
    HttpClient httpClient,
    IOptions<AddressGeocodingOptions> options,
    ILogger<GoogleAddressLocationLookup> logger) : IAddressLocationLookup
{
    private readonly AddressGeocodingOptions options = options.Value;

    public async Task<AddressLookupResult?> ReverseGeocodeAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken)
    {
        if (!this.options.IsGoogle
            || string.IsNullOrWhiteSpace(this.options.ApiKey)
            || !Uri.TryCreate(this.options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            logger.LogWarning("Address geocoding is unavailable because the provider configuration is incomplete.");
            return null;
        }

        var builder = new UriBuilder(baseUri);
        var query = builder.Query.TrimStart('?');
        var parameters = string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
        parameters.Add($"latlng={Uri.EscapeDataString(string.Format(CultureInfo.InvariantCulture, "{0},{1}", latitude, longitude))}");
        parameters.Add($"key={Uri.EscapeDataString(this.options.ApiKey)}");
        builder.Query = string.Join('&', parameters);

        try
        {
            using var response = await httpClient.GetAsync(builder.Uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Address geocoding provider returned HTTP status {StatusCode}.", response.StatusCode);
                return null;
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
            return ParseResult(document.RootElement, latitude, longitude);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Address geocoding provider timed out.");
            return null;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Address geocoding provider request failed.");
            return null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Address geocoding provider returned malformed JSON.");
            return null;
        }
    }

    private static AddressLookupResult? ParseResult(
        JsonElement root,
        decimal latitude,
        decimal longitude)
    {
        if (!root.TryGetProperty("status", out var status)
            || !string.Equals(status.GetString(), "OK", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return null;
        }

        var result = results[0];
        var components = result.TryGetProperty("address_components", out var addressComponents)
            && addressComponents.ValueKind == JsonValueKind.Array
            ? addressComponents.EnumerateArray().ToArray()
            : [];

        string? Component(params string[] types) => components
            .Where(component => component.TryGetProperty("types", out var componentTypes)
                && componentTypes.ValueKind == JsonValueKind.Array
                && componentTypes.EnumerateArray().Any(type => types.Contains(type.GetString(), StringComparer.Ordinal)))
            .Select(component => component.TryGetProperty("long_name", out var value) ? value.GetString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        var formattedAddress = result.TryGetProperty("formatted_address", out var formatted)
            ? formatted.GetString()
            : null;
        var route = Component("route");
        var streetNumber = Component("street_number");
        var addressLine1 = formattedAddress
            ?? string.Join(" ", new[] { streetNumber, route }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var location = result.TryGetProperty("geometry", out var geometry)
            && geometry.TryGetProperty("location", out var coordinates)
            ? coordinates
            : default;
        var resolvedLatitude = ReadDecimal(location, "lat") ?? latitude;
        var resolvedLongitude = ReadDecimal(location, "lng") ?? longitude;

        return new AddressLookupResult(
            NullIfEmpty(addressLine1),
            Component("sublocality", "sublocality_level_1", "neighborhood", "locality"),
            Component("locality", "postal_town", "administrative_area_level_2"),
            Component("administrative_area_level_1"),
            Component("postal_code"),
            resolvedLatitude,
            resolvedLongitude,
            Component("point_of_interest", "establishment"),
            Component("country"));
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
