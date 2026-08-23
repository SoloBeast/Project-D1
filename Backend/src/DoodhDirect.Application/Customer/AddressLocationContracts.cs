namespace DoodhDirect.Application.Customer;

public sealed record AddressLookupResult(
    string? AddressLine1,
    string? Locality,
    string? City,
    string? State,
    string? PinCode,
    decimal Latitude,
    decimal Longitude,
    string? Landmark = null,
    string? Country = null);

public interface IAddressLocationLookup
{
    Task<AddressLookupResult?> ReverseGeocodeAsync(
        decimal latitude,
        decimal longitude,
        CancellationToken cancellationToken);
}
