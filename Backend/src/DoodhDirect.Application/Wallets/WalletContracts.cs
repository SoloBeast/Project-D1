using DoodhDirect.Domain.Wallets;

namespace DoodhDirect.Application.Wallets;

public sealed record WalletResult(
    Guid PublicId,
    decimal Balance,
    string Currency,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record WalletTransactionResult(
    Guid PublicId,
    WalletTransactionType Type,
    decimal BalanceBefore,
    decimal Amount,
    decimal BalanceAfter,
    string Currency,
    string Description,
    DateTime OccurredAtUtc,
    Guid? PaymentId,
    Guid? OrderId,
    Guid? SubscriptionId = null);

public sealed record WalletTopUpRequest(
    decimal Amount,
    string IdempotencyKey);

public sealed record WalletAdjustmentRequest(
    decimal Amount,
    string Reason,
    string IdempotencyKey,
    string? IpAddress,
    string? UserAgent);

public interface IWalletService
{
    Task<WalletResult> GetAsync(
        long customerId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WalletTransactionResult>> GetTransactionsAsync(
        long customerId,
        CancellationToken cancellationToken);

    Task<WalletTransactionResult> TopUpAsync(
        long customerId,
        WalletTopUpRequest request,
        CancellationToken cancellationToken);

    Task<WalletTransactionResult> AdjustAsync(
        long administratorUserId,
        Guid customerId,
        WalletAdjustmentRequest request,
        CancellationToken cancellationToken);

    Task<WalletTransactionResult> DebitOrderAsync(
        long customerId,
        long orderId,
        long paymentId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<WalletTransactionResult> DebitSubscriptionAsync(
        long customerId,
        long subscriptionId,
        long paymentId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Subscription wallet debits are not supported by this implementation.");

    Task<WalletTransactionResult> CreditRefundAsync(
        long customerId,
        long orderId,
        long paymentId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<WalletTransactionResult> CreditSubscriptionRefundAsync(
        long customerId,
        long subscriptionId,
        long paymentId,
        decimal amount,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Subscription wallet refunds are not supported by this implementation.");
}

public static class WalletMappings
{
    public static WalletResult ToResult(this Wallet wallet) => new(
        wallet.PublicId,
        wallet.Balance,
        wallet.Currency,
        wallet.CreatedAtUtc,
        wallet.UpdatedAtUtc);

    public static WalletTransactionResult ToResult(this WalletTransaction transaction) => new(
        transaction.PublicId,
        transaction.Type,
        transaction.BalanceBefore,
        transaction.Amount,
        transaction.BalanceAfter,
        transaction.Currency,
        transaction.Description,
        transaction.OccurredAtUtc,
        transaction.Payment?.PublicId,
        transaction.Order?.PublicId,
        transaction.Subscription?.PublicId);
}
