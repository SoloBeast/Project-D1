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
    public string? CustomerNumber { get; private set; }
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public DateOnly? DateOfBirth { get; private set; }
    public string? Gender { get; private set; }
    public string? AlternateMobile { get; private set; }

    public void AssignCustomerNumber(string customerNumber)
    {
        if (string.IsNullOrWhiteSpace(customerNumber))
        {
            throw new ArgumentException("A customer number is required.", nameof(customerNumber));
        }

        if (CustomerNumber is not null)
        {
            throw new InvalidOperationException("The customer number has already been assigned.");
        }

        CustomerNumber = customerNumber.Trim();
    }

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

/// <summary>
/// A customer's bank account for wallet withdrawals. One row per customer
/// (<c>UserId</c> unique). The account number is stored only in
/// DataProtection-encrypted form (<c>ProtectedAccountNumber</c>); the last
/// four digits (<c>AccountLast4</c>) are stored in plain text solely for
/// masked display. Full details are never returned by general profile reads.
/// </summary>
public sealed class CustomerBankDetail : AuditableEntity
{
    private CustomerBankDetail() { }

    public CustomerBankDetail(
        long userId,
        string accountHolderName,
        string bankName,
        string protectedAccountNumber,
        string accountLast4,
        string ifsc)
    {
        UserId = userId;
        Update(accountHolderName, bankName, protectedAccountNumber, accountLast4, ifsc);
    }

    public long UserId { get; private set; }
    public string AccountHolderName { get; private set; } = string.Empty;
    public string BankName { get; private set; } = string.Empty;
    public string ProtectedAccountNumber { get; private set; } = string.Empty;
    public string AccountLast4 { get; private set; } = string.Empty;
    public string Ifsc { get; private set; } = string.Empty;

    public User User { get; private set; } = null!;

    public void Update(
        string accountHolderName,
        string bankName,
        string protectedAccountNumber,
        string accountLast4,
        string ifsc)
    {
        AccountHolderName = Required(accountHolderName, nameof(accountHolderName));
        BankName = Required(bankName, nameof(bankName));
        ProtectedAccountNumber = Required(protectedAccountNumber, nameof(protectedAccountNumber));
        AccountLast4 = Required(accountLast4, nameof(accountLast4));
        Ifsc = Required(ifsc, nameof(ifsc));
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
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
