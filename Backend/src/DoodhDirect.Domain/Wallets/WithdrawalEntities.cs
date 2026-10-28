using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;

namespace DoodhDirect.Domain.Wallets;

/// <summary>
/// Lifecycle of a customer wallet-withdrawal request. The request is itself
/// the support ticket for the withdrawal workflow: it is created in
/// <see cref="WithdrawalRequestStatus.Pending"/>, moves to
/// <see cref="WithdrawalRequestStatus.Completed"/> only when staff
/// confirm the bank transfer through the established workflow, and returns
/// to a terminal <see cref="WithdrawalRequestStatus.Rejected"/> (with a
/// compensating ledger credit) when the transfer is refused or definitively
/// fails. Terminal states are immutable.
/// </summary>
public enum WithdrawalRequestStatus
{
    Pending,
    Completed,
    Rejected
}

public sealed class WithdrawalRequest : AuditableEntity
{
    private WithdrawalRequest() { }

    public WithdrawalRequest(
        long customerId,
        string requestNumber,
        decimal amount,
        string currency,
        long bankDetailId,
        string accountHolderNameSnapshot,
        string bankNameSnapshot,
        string ifscSnapshot,
        string accountLast4Snapshot,
        string idempotencyKey)
    {
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));
        if (bankDetailId <= 0) throw new ArgumentOutOfRangeException(nameof(bankDetailId));
        CustomerId = customerId;
        RequestNumber = Required(requestNumber, nameof(requestNumber));
        Amount = NormalizePositiveAmount(amount);
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
        BankDetailId = bankDetailId;
        AccountHolderNameSnapshot = Required(accountHolderNameSnapshot, nameof(accountHolderNameSnapshot));
        BankNameSnapshot = Required(bankNameSnapshot, nameof(bankNameSnapshot));
        IfscSnapshot = Required(ifscSnapshot, nameof(ifscSnapshot));
        AccountLast4Snapshot = Required(accountLast4Snapshot, nameof(accountLast4Snapshot));
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey));
        Status = WithdrawalRequestStatus.Pending;
    }

    public long CustomerId { get; private set; }
    public string RequestNumber { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public long BankDetailId { get; private set; }
    public string AccountHolderNameSnapshot { get; private set; } = string.Empty;
    public string BankNameSnapshot { get; private set; } = string.Empty;
    public string IfscSnapshot { get; private set; } = string.Empty;
    public string AccountLast4Snapshot { get; private set; } = string.Empty;
    public WithdrawalRequestStatus Status { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public long? DecidedByUserId { get; private set; }
    public DateTime? DecidedAt { get; private set; }
    public string? DecisionRemarks { get; private set; }
    public string? TransferReference { get; private set; }
    public bool ReversalCredited { get; private set; }

    public User Customer { get; private set; } = null!;
    public CustomerBankDetail BankDetail { get; private set; } = null!;
    public User? DecidedByUser { get; private set; }

    /// <summary>
    /// Marks the withdrawal paid after staff confirm the bank transfer.
    /// Moves no money: the debit happened atomically at submission. A bank
    /// transfer reference (UTR) is required so a request is never marked
    /// paid merely because it was reviewed.
    /// </summary>
    public void Complete(long staffUserId, DateTime decidedAt, string? remarks, string? transferReference)
    {
        EnsurePending();
        if (staffUserId <= 0) throw new ArgumentOutOfRangeException(nameof(staffUserId));
        TransferReference = RequiredTransferReference(transferReference);
        Status = WithdrawalRequestStatus.Completed;
        DecidedByUserId = staffUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Normalize(remarks);
    }

    /// <summary>
    /// Rejects the withdrawal so the caller can post the compensating
    /// reversal credit. The reversal itself is recorded via
    /// <see cref="MarkReversalCredited"/>.
    /// </summary>
    public void Reject(long staffUserId, DateTime decidedAt, string? remarks)
    {
        EnsurePending();
        if (staffUserId <= 0) throw new ArgumentOutOfRangeException(nameof(staffUserId));
        Status = WithdrawalRequestStatus.Rejected;
        DecidedByUserId = staffUserId;
        DecidedAt = decidedAt;
        DecisionRemarks = Normalize(remarks);
    }

    public void MarkReversalCredited()
    {
        if (Status != WithdrawalRequestStatus.Rejected)
        {
            throw new InvalidOperationException("A reversal can only be recorded for a rejected withdrawal.");
        }

        if (ReversalCredited)
        {
            throw new InvalidOperationException("The reversal credit has already been recorded.");
        }

        ReversalCredited = true;
    }

    private void EnsurePending()
    {
        if (Status != WithdrawalRequestStatus.Pending)
        {
            throw new InvalidOperationException($"A withdrawal in status '{Status}' cannot transition; terminal states are final.");
        }
    }

    private static decimal NormalizePositiveAmount(decimal amount)
    {
        var rounded = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (rounded <= 0 || rounded != amount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        return rounded;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequiredTransferReference(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length < 6 || normalized.Length > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A bank transfer reference (UTR) between 6 and 100 characters is required to mark a withdrawal paid.");
        }

        return normalized;
    }
}
