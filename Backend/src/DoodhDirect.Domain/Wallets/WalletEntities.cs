using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;

namespace DoodhDirect.Domain.Wallets;

public enum WalletTransactionType
{
    TopUp,
    OrderDebit,
    RefundCredit,
    PromotionalCredit,
    AdminAdjustment
}

public sealed class Wallet : AuditableEntity
{
    private Wallet() { }

    public Wallet(long customerId, string currency)
    {
        if (customerId <= 0) throw new ArgumentOutOfRangeException(nameof(customerId));

        CustomerId = customerId;
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
    }

    public long CustomerId { get; private set; }
    public decimal Balance { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public byte[] RowVersion { get; private set; } = [];

    public User Customer { get; private set; } = null!;
    public ICollection<WalletTransaction> Transactions { get; private set; } = [];

    public WalletTransaction Credit(
        WalletTransactionType type,
        decimal amount,
        string idempotencyKey,
        string description,
        DateTime occurredAtUtc,
        long? paymentId = null,
        long? orderId = null,
        long? performedByUserId = null)
    {
        if (type is WalletTransactionType.OrderDebit)
        {
            throw new ArgumentException("Order debit is not a credit transaction type.", nameof(type));
        }

        var roundedAmount = NormalizePositiveAmount(amount);
        return Apply(
            type,
            roundedAmount,
            idempotencyKey,
            description,
            occurredAtUtc,
            paymentId,
            orderId,
            performedByUserId);
    }

    public WalletTransaction Adjust(
        decimal signedAmount,
        long performedByUserId,
        string idempotencyKey,
        string reason,
        DateTime occurredAtUtc)
    {
        if (performedByUserId <= 0) throw new ArgumentOutOfRangeException(nameof(performedByUserId));

        var roundedAmount = decimal.Round(signedAmount, 2, MidpointRounding.AwayFromZero);
        if (roundedAmount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(signedAmount));
        }

        return Apply(
            WalletTransactionType.AdminAdjustment,
            roundedAmount,
            idempotencyKey,
            reason,
            occurredAtUtc,
            null,
            null,
            performedByUserId);
    }

    public WalletTransaction DebitOrder(
        decimal amount,
        long orderId,
        long paymentId,
        string idempotencyKey,
        string description,
        DateTime occurredAtUtc)
    {
        if (orderId <= 0) throw new ArgumentOutOfRangeException(nameof(orderId));
        if (paymentId <= 0) throw new ArgumentOutOfRangeException(nameof(paymentId));

        return Apply(
            WalletTransactionType.OrderDebit,
            -NormalizePositiveAmount(amount),
            idempotencyKey,
            description,
            occurredAtUtc,
            paymentId,
            orderId,
            null);
    }

    private WalletTransaction Apply(
        WalletTransactionType type,
        decimal signedAmount,
        string idempotencyKey,
        string description,
        DateTime occurredAtUtc,
        long? paymentId,
        long? orderId,
        long? performedByUserId)
    {
        if (occurredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(occurredAtUtc));
        }

        if (type == WalletTransactionType.AdminAdjustment && performedByUserId is null)
        {
            throw new ArgumentException("An administrator is required for an adjustment.", nameof(performedByUserId));
        }

        var balanceBefore = Balance;
        var balanceAfter = decimal.Round(balanceBefore + signedAmount, 2, MidpointRounding.AwayFromZero);
        if (balanceAfter < 0)
        {
            throw new InvalidOperationException("Wallet balance is insufficient for this operation.");
        }

        var transaction = new WalletTransaction(
            Id,
            type,
            balanceBefore,
            signedAmount,
            balanceAfter,
            Currency,
            idempotencyKey,
            description,
            occurredAtUtc,
            paymentId,
            orderId,
            performedByUserId);

        Balance = balanceAfter;
        Transactions.Add(transaction);
        return transaction;
    }

    private static decimal NormalizePositiveAmount(decimal amount)
    {
        var roundedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (roundedAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        return roundedAmount;
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}

public sealed class WalletTransaction : PublicEntity
{
    private WalletTransaction() { }

    internal WalletTransaction(
        long walletId,
        WalletTransactionType type,
        decimal balanceBefore,
        decimal amount,
        decimal balanceAfter,
        string currency,
        string idempotencyKey,
        string description,
        DateTime occurredAtUtc,
        long? paymentId,
        long? orderId,
        long? performedByUserId)
    {
        if (walletId < 0) throw new ArgumentOutOfRangeException(nameof(walletId));
        if (amount == 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (balanceBefore < 0 || balanceAfter < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(balanceAfter));
        }
        if (decimal.Round(balanceBefore + amount, 2, MidpointRounding.AwayFromZero) != balanceAfter)
        {
            throw new ArgumentException("Wallet transaction balances do not reconcile.", nameof(balanceAfter));
        }
        if (occurredAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(occurredAtUtc));
        }

        WalletId = walletId;
        Type = type;
        BalanceBefore = balanceBefore;
        Amount = amount;
        BalanceAfter = balanceAfter;
        Currency = Required(currency, nameof(currency)).ToUpperInvariant();
        IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey));
        Description = Required(description, nameof(description));
        OccurredAtUtc = occurredAtUtc;
        PaymentId = paymentId;
        OrderId = orderId;
        PerformedByUserId = performedByUserId;
    }

    public long WalletId { get; private set; }
    public WalletTransactionType Type { get; private set; }
    public decimal BalanceBefore { get; private set; }
    public decimal Amount { get; private set; }
    public decimal BalanceAfter { get; private set; }
    public string Currency { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public DateTime OccurredAtUtc { get; private set; }
    public long? PaymentId { get; private set; }
    public long? OrderId { get; private set; }
    public long? PerformedByUserId { get; private set; }

    public Wallet Wallet { get; private set; } = null!;
    public Payment? Payment { get; private set; }
    public Order? Order { get; private set; }
    public User? PerformedByUser { get; private set; }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();
}
