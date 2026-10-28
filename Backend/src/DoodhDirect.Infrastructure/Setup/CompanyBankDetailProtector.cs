using Microsoft.AspNetCore.DataProtection;

namespace DoodhDirect.Infrastructure.Setup;

/// <summary>
/// Protects the company bank account number at rest with ASP.NET Core
/// DataProtection. The purpose string is deliberately independent from
/// <c>DoodhDirect.CustomerBankDetail.v1</c>: customer and company account
/// numbers are different trust domains, and a protector for one must never
/// decrypt the other. Only the account number is encrypted; holder name,
/// bank name, IFSC and last-4 stay in plain text for masked display.
/// Key loss fails closed (Unprotect throws); no hard-coded fallback key.
/// </summary>
public sealed class CompanyBankDetailProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "DoodhDirect.CompanyBankDetail.v1");

    public string Protect(string accountNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNumber);
        return _protector.Protect(accountNumber.Trim());
    }

    public string Unprotect(string protectedAccountNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedAccountNumber);
        return _protector.Unprotect(protectedAccountNumber);
    }
}
