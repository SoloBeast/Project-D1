using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Configuration;

/// <summary>
/// The company's own bank account used as the source of manual bank-transfer
/// refunds. Single active row per deployment (see <see cref="SingletonGuard"/>):
/// payout files always reference the account that was active when the batch
/// was generated, via a snapshot stored on the batch itself. The account
/// number is stored only in DataProtection-encrypted form
/// (<c>ProtectedAccountNumber</c>); the last four digits are stored in plain
/// text solely for masked display. Full details are never returned by
/// ordinary configuration reads; the export workflow resolves them through
/// a narrowly authorized service method that audit-logs every view.
/// </summary>
public sealed class CompanyBankDetail : AuditableEntity
{
    private CompanyBankDetail() { }

    public CompanyBankDetail(
        string accountHolderName,
        string bankName,
        string protectedAccountNumber,
        string accountLast4,
        string ifsc)
    {
        SingletonGuard = 0;
        Update(accountHolderName, bankName, protectedAccountNumber, accountLast4, ifsc);
    }

    /// <summary>
    /// Always zero. A unique index on this column guarantees at most one
    /// company bank-detail row exists, so exports can never observe two
    /// competing "active" source accounts.
    /// </summary>
    public int SingletonGuard { get; private set; }

    public string AccountHolderName { get; private set; } = string.Empty;
    public string BankName { get; private set; } = string.Empty;
    public string ProtectedAccountNumber { get; private set; } = string.Empty;
    public string AccountLast4 { get; private set; } = string.Empty;
    public string Ifsc { get; private set; } = string.Empty;

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
