using Microsoft.AspNetCore.DataProtection;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Protects manual-bank beneficiary account numbers at rest with ASP.NET Core
/// DataProtection, following the <c>BankDetailProtector</c> /
/// <c>CompanyBankDetailProtector</c> pattern with an independent purpose
/// string. Only the snapshot account number is encrypted; holder name, bank
/// name, IFSC and last-4 stay in plain text for masked display. Never reuse
/// the customer, company or integration-secret purposes: a beneficiary
/// snapshot is frozen per ticket and must survive independently of the
/// customer's live bank details and the company's payout account.
/// </summary>
public sealed class ManualBankBeneficiaryProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(
        "DoodhDirect.RefundBeneficiary.v1");

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
