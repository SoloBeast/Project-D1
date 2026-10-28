using Microsoft.AspNetCore.DataProtection;

namespace DoodhDirect.Infrastructure.Customer;

/// <summary>
/// Protects customer bank account numbers at rest with ASP.NET Core
/// DataProtection, following the <c>NotificationTokenProtector</c> /
/// <c>IntegrationSecretProtector</c> pattern with an independent purpose
/// string. Only the account number is encrypted; holder name, bank name,
/// IFSC and last-4 stay in plain text for display and staff verification.
///
/// Key-management gap (reported, not worked around): the default key ring is
/// ephemeral unless the host persists DataProtection keys. If keys are lost,
/// <see cref="Unprotect"/> throws and callers fail closed — bank details
/// become unreadable rather than silently wrong. No hard-coded fallback key
/// is introduced here.
/// </summary>
public sealed class BankDetailProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "DoodhDirect.CustomerBankDetail.v1");

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
