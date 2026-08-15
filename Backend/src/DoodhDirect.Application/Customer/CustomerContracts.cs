using DoodhDirect.Domain.Customer;

namespace DoodhDirect.Application.Customer;

public sealed record CustomerProfileResult(
    Guid PublicId,
    string? FirstName,
    string? LastName,
    DateOnly? DateOfBirth,
    string? Gender,
    string? AlternateMobile);

public sealed record CustomerAddressResult(
    Guid PublicId,
    string Label,
    string AddressLine1,
    string? AddressLine2,
    string Locality,
    string City,
    string State,
    string PinCode,
    string? Landmark,
    string? DeliveryInstructions,
    string ContactName,
    string ContactMobile,
    decimal Latitude,
    decimal Longitude,
    bool IsDefault,
    bool IsActive);

public sealed record UpdateCustomerProfileRequest(
    string? FirstName,
    string? LastName,
    DateOnly? DateOfBirth,
    string? Gender,
    string? AlternateMobile);

public sealed record UpsertCustomerAddressRequest(
    string Label,
    string AddressLine1,
    string? AddressLine2,
    string Locality,
    string City,
    string State,
    string PinCode,
    string? Landmark,
    string? DeliveryInstructions,
    string ContactName,
    string ContactMobile,
    decimal? Latitude,
    decimal? Longitude,
    bool IsDefault);

public interface ICustomerService
{
    Task<CustomerProfileResult> GetProfileAsync(long userId, CancellationToken cancellationToken);
    Task<CustomerProfileResult> UpdateProfileAsync(long userId, UpdateCustomerProfileRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerAddressResult>> GetAddressesAsync(long userId, CancellationToken cancellationToken);
    Task<CustomerAddressResult> GetAddressAsync(long userId, Guid addressPublicId, CancellationToken cancellationToken);
    Task<CustomerAddressResult> CreateAddressAsync(long userId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken);
    Task<CustomerAddressResult> UpdateAddressAsync(long userId, Guid addressPublicId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken);
    Task DeactivateAddressAsync(long userId, Guid addressPublicId, CancellationToken cancellationToken);

    Task<CustomerProfileResult> GetProfileByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken);
    Task<CustomerProfileResult> UpdateProfileByCustomerIdAsync(Guid customerId, UpdateCustomerProfileRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerAddressResult>> GetAddressesByCustomerIdAsync(Guid customerId, CancellationToken cancellationToken);
    Task<CustomerAddressResult> GetAddressByCustomerIdAsync(Guid customerId, Guid addressPublicId, CancellationToken cancellationToken);
    Task<CustomerAddressResult> CreateAddressByCustomerIdAsync(Guid customerId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken);
    Task<CustomerAddressResult> UpdateAddressByCustomerIdAsync(Guid customerId, Guid addressPublicId, UpsertCustomerAddressRequest request, CancellationToken cancellationToken);
    Task DeactivateAddressByCustomerIdAsync(Guid customerId, Guid addressPublicId, CancellationToken cancellationToken);
}

public static class CustomerMappings
{
    public static CustomerProfileResult ToResult(this CustomerProfile profile) => new(
        profile.PublicId, profile.FirstName, profile.LastName, profile.DateOfBirth,
        profile.Gender, profile.AlternateMobile);

    public static CustomerAddressResult ToResult(this CustomerAddress address) => new(
        address.PublicId, address.Label, address.AddressLine1, address.AddressLine2,
        address.Locality, address.City, address.State, address.PinCode, address.Landmark,
        address.DeliveryInstructions, address.ContactName, address.ContactMobile,
        address.Latitude, address.Longitude, address.IsDefault, address.IsActive);
}
