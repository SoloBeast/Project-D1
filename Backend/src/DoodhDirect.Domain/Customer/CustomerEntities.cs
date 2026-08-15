using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Customer;

public sealed class CustomerProfile : AuditableEntity
{
    private CustomerProfile() { }

    public CustomerProfile(long userId)
    {
        UserId = userId;
    }

    public long UserId { get; private set; }
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public DateOnly? DateOfBirth { get; private set; }
    public string? Gender { get; private set; }
    public string? AlternateMobile { get; private set; }

    public User User { get; private set; } = null!;

    public void Update(string? firstName, string? lastName, DateOnly? dateOfBirth, string? gender, string? alternateMobile)
    {
        FirstName = Normalize(firstName);
        LastName = Normalize(lastName);
        DateOfBirth = dateOfBirth;
        Gender = Normalize(gender);
        AlternateMobile = Normalize(alternateMobile);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class CustomerAddress : AuditableEntity
{
    private CustomerAddress() { }

    public CustomerAddress(long userId, string label, string addressLine1, string locality, string city, string state, string pinCode, string contactName, string contactMobile, decimal latitude, decimal longitude)
    {
        UserId = userId;
        Update(label, addressLine1, null, locality, city, state, pinCode, null, null, contactName, contactMobile, null, latitude, longitude);
    }

    public long UserId { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public string AddressLine1 { get; private set; } = string.Empty;
    public string? AddressLine2 { get; private set; }
    public string Locality { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string PinCode { get; private set; } = string.Empty;
    public string? Landmark { get; private set; }
    public string? DeliveryInstructions { get; private set; }
    public string ContactName { get; private set; } = string.Empty;
    public string ContactMobile { get; private set; } = string.Empty;
    public decimal Latitude { get; private set; }
    public decimal Longitude { get; private set; }
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; } = true;

    public User User { get; private set; } = null!;

    public void Update(string label, string addressLine1, string? addressLine2, string locality, string city, string state, string pinCode, string? landmark, string? deliveryInstructions, string contactName, string contactMobile, bool? isDefault, decimal latitude, decimal longitude)
    {
        Label = label.Trim();
        AddressLine1 = addressLine1.Trim();
        AddressLine2 = Normalize(addressLine2);
        Locality = locality.Trim();
        City = city.Trim();
        State = state.Trim();
        PinCode = pinCode.Trim();
        Landmark = Normalize(landmark);
        DeliveryInstructions = Normalize(deliveryInstructions);
        ContactName = contactName.Trim();
        ContactMobile = contactMobile.Trim();
        Latitude = latitude;
        Longitude = longitude;
        if (isDefault.HasValue)
        {
            IsDefault = isDefault.Value;
        }
    }

    public void Deactivate() 
    {
        IsActive = false;
        IsDefault = false;
    }

    public void SetDefault() => IsDefault = true;
    public void ClearDefault() => IsDefault = false;

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
