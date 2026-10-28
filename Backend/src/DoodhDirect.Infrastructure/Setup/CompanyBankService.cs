using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Setup;

/// <summary>
/// Single-company bank-detail configuration for manual bank-transfer refund
/// payouts. Reads are masked; the decrypted number is served only through
/// <see cref="GetDecryptedForExportAsync"/>, which exists for the audited
/// export workflow and has no ordinary settings API. Every mutation and
/// every decrypted view is audit-logged with masked details only — the full
/// account number never appears in audit JSON, logs, or error messages.
/// </summary>
public sealed class CompanyBankService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    CompanyBankDetailProtector protector) : ICompanyBankService
{
    public const string ActionUpdated = "COMPANY_BANK.UPDATED";
    public const string ActionViewedForExport = "COMPANY_BANK.VIEWED_FOR_EXPORT";

    private const string EntityType = "CompanyBankDetail";
    private const string EntityId = "Company";

    private static readonly Regex IfscPattern = new("^[A-Z]{4}0[A-Z0-9]{6}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BankAccountPattern = new("^[0-9]{9,18}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<CompanyBankDetailResult?> GetAsync(CancellationToken cancellationToken)
    {
        var detail = await dbContext.CompanyBankDetails
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        return detail is null ? null : ToResult(detail);
    }

    public async Task<CompanyBankDetailResult> UpdateAsync(
        UpsertCompanyBankDetailRequest request,
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (actorUserId <= 0) throw new ValidationAppException("The actor identity is invalid.", nameof(actorUserId));

        var holder = request.AccountHolderName?.Trim() ?? string.Empty;
        var bank = request.BankName?.Trim() ?? string.Empty;
        var ifsc = request.Ifsc?.Trim().ToUpperInvariant() ?? string.Empty;
        var account = request.AccountNumber?.Trim() ?? string.Empty;
        Validate(holder, bank, account, ifsc);

        CompanyBankDetailResult? result = null;
        await ExecuteInTransactionAsync(async () =>
        {
            var detail = await dbContext.CompanyBankDetails.SingleOrDefaultAsync(cancellationToken);
            var before = detail is null ? null : MaskedSnapshot(detail);

            if (detail is null)
            {
                if (string.IsNullOrEmpty(account))
                {
                    throw new ValidationAppException("Account number is required.", nameof(request.AccountNumber));
                }

                detail = new CompanyBankDetail(holder, bank, protector.Protect(account), Last4(account), ifsc);
                detail.SetCreated(timeProvider.Now);
                dbContext.CompanyBankDetails.Add(detail);
            }
            else if (string.IsNullOrEmpty(account))
            {
                detail.Update(holder, bank, detail.ProtectedAccountNumber, detail.AccountLast4, ifsc);
                detail.SetUpdated(timeProvider.Now);
            }
            else
            {
                detail.Update(holder, bank, protector.Protect(account), Last4(account), ifsc);
                detail.SetUpdated(timeProvider.Now);
            }

            dbContext.AddAuditLog(new AuditLog(
                actorUserId,
                ActionUpdated,
                EntityType,
                EntityId,
                before is null ? null : JsonSerializer.Serialize(before),
                JsonSerializer.Serialize(MaskedSnapshot(detail)),
                ipAddress,
                userAgent,
                "Company bank details updated.",
                timeProvider.Now));

            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToResult(detail);
        }, cancellationToken);

        return result!;
    }

    public async Task<CompanyBankDetailFull> GetDecryptedForExportAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (actorUserId <= 0) throw new ValidationAppException("The actor identity is invalid.", nameof(actorUserId));

        var detail = await dbContext.CompanyBankDetails
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("The company bank details are not configured.");

        string accountNumber;
        try
        {
            accountNumber = protector.Unprotect(detail.ProtectedAccountNumber);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The company bank details are currently unavailable.", exception);
        }

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionViewedForExport,
            EntityType,
            EntityId,
            null,
            null,
            ipAddress,
            userAgent,
            "Company bank details resolved for bank-file generation.",
            timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CompanyBankDetailFull(
            detail.AccountHolderName,
            detail.BankName,
            accountNumber,
            detail.Ifsc);
    }

    public async Task<CompanyBankDetailExportSnapshot> GetSnapshotForExportAsync(
        long actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (actorUserId <= 0) throw new ValidationAppException("The actor identity is invalid.", nameof(actorUserId));

        var detail = await dbContext.CompanyBankDetails
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("The company bank details are not configured.");

        dbContext.AddAuditLog(new AuditLog(
            actorUserId,
            ActionViewedForExport,
            EntityType,
            EntityId,
            null,
            null,
            ipAddress,
            userAgent,
            "Company bank details resolved for export-batch snapshot.",
            timeProvider.Now));
        await dbContext.SaveChangesAsync(cancellationToken);

        return new CompanyBankDetailExportSnapshot(
            detail.AccountHolderName,
            detail.BankName,
            detail.ProtectedAccountNumber,
            detail.AccountLast4,
            detail.Ifsc);
    }

    private static CompanyBankDetailResult ToResult(CompanyBankDetail detail) => new(
        detail.PublicId,
        detail.AccountHolderName,
        detail.BankName,
        $"···· {detail.AccountLast4}",
        detail.AccountLast4,
        detail.Ifsc,
        true,
        detail.CreatedAt,
        detail.UpdatedAt);

    private static object MaskedSnapshot(CompanyBankDetail detail) => new
    {
        detail.AccountHolderName,
        detail.BankName,
        MaskedAccountNumber = $"···· {detail.AccountLast4}",
        detail.AccountLast4,
        detail.Ifsc
    };

    private static string Last4(string accountNumber)
    {
        var digits = new string(accountNumber.Where(char.IsDigit).ToArray());
        var source = digits.Length >= 4 ? digits : accountNumber;
        return source[^4..];
    }

    private static void Validate(string holder, string bank, string account, string ifsc)
    {
        if (string.IsNullOrWhiteSpace(holder) || holder.Length > 100)
        {
            throw new ValidationAppException("Account holder name is required and must be at most 100 characters.", "accountHolderName");
        }

        if (string.IsNullOrWhiteSpace(bank) || bank.Length > 100)
        {
            throw new ValidationAppException("Bank name is required and must be at most 100 characters.", "bankName");
        }

        if (!string.IsNullOrEmpty(account) && !BankAccountPattern.IsMatch(account))
        {
            throw new ValidationAppException("Account number must be 9 to 18 digits.", "accountNumber");
        }

        if (!IfscPattern.IsMatch(ifsc))
        {
            throw new ValidationAppException("IFSC must look like ABCD0123456: four letters, zero, then six letters or digits.", "ifsc");
        }
    }

    private async Task ExecuteInTransactionAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
