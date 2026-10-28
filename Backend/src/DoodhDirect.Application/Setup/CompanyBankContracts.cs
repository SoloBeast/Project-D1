namespace DoodhDirect.Application.Setup;

/// <summary>
/// Company bank details for manual bank-transfer refund payouts. Reads are
/// masked: the full account number is never returned by configuration APIs.
/// The decrypted number is available only through
/// <see cref="ICompanyBankService.GetDecryptedForExportAsync"/>, which is
/// reserved for the audited export workflow (Stage 3) and has no controller
/// endpoint of its own.
/// </summary>
public sealed record CompanyBankDetailResult(
    Guid PublicId,
    string AccountHolderName,
    string BankName,
    string MaskedAccountNumber,
    string AccountLast4,
    string Ifsc,
    bool IsConfigured,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Full company bank destination for the export workflow. Served only to the
/// export service; never returned by configuration APIs, never logged.
/// </summary>
public sealed record CompanyBankDetailFull(
    string AccountHolderName,
    string BankName,
    string AccountNumber,
    string Ifsc);

/// <summary>
/// Stage 3: company source-account snapshot for freezing onto export
/// batches. Carries the encrypted account number (never the plaintext)
/// plus masked metadata; the export service decrypts only when rendering
/// the authorized CSV download.
/// </summary>
public sealed record CompanyBankDetailExportSnapshot(
    string AccountHolderName,
    string BankName,
    string ProtectedAccountNumber,
    string AccountLast4,
    string Ifsc);

public sealed record UpsertCompanyBankDetailRequest(
    string AccountHolderName,
    string BankName,
    string? AccountNumber,
    string Ifsc);

public interface ICompanyBankService
{
    Task<CompanyBankDetailResult?> GetAsync(CancellationToken cancellationToken);

    Task<CompanyBankDetailResult> UpdateAsync(
        UpsertCompanyBankDetailRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the decrypted company account for bank-file generation.
    /// Every call is audit-logged by the caller-facing export workflow; the
    /// returned number must never be logged or exposed outside the file.
    /// </summary>
    Task<CompanyBankDetailFull> GetDecryptedForExportAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stage 3: resolves the freezable company-account snapshot for a new
    /// export batch. Audit-logged like a decrypted view; the protected
    /// number must only be persisted on batch/item snapshots and decrypted
    /// for the authorized CSV download.
    /// </summary>
    Task<CompanyBankDetailExportSnapshot> GetSnapshotForExportAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
}
