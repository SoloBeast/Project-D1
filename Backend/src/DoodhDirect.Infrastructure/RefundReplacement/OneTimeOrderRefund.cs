using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Stage 1: the dedicated one-time-order wallet refund, used by the
/// order-backed ticket preview and staff completion paths.
///
/// Money rules (never edit the formula here):
/// <list type="bullet">
/// <item>Gross basis is the frozen <c>Order.PayableAmount</c> (already
/// charge/tax-inclusive). Order items and current tax/charge configuration
/// are never read or recomputed.</item>
/// <item>Deduction uses ONLY the ticket's frozen
/// <c>AppliedDeductionPercentage</c> (shared Admin Centre value or zero when
/// waived), never current configuration and never the subscription
/// per-share (<c>Payable / TotalEntitlement</c>) formula.</item>
/// <item>Eligible paid amount aggregates every <c>Success</c> /
/// <c>PartiallyRefunded</c> payment for the order and customer:
/// sum of <c>Amount - RefundedAmount</c>. Final credit is
/// <c>min(gross - deduction, eligiblePaid)</c> and is allocated
/// deterministically across payments ordered by id.</item>
/// <item>The wallet credit is a ledger transaction with the deterministic key
/// <c>one-time-refund:{requestPublicId:N}:wallet</c>; each affected payment
/// carries its own <c>Refund</c> row keyed
/// <c>one-time-refund:{requestPublicId:N}:payment:{paymentId}</c>.
/// No payment gateway is called: local <c>Refund</c> rows are recorded on the
/// existing payments and paid out as wallet credit. Committed records with
/// these keys are reused, never duplicated.</item>
/// </list>
///
/// Callers run this inside the ticket's serializable transaction and commit
/// the ticket transition, the money movement, audit and outbox atomically
/// ("nothing happened" or "everything happened").
/// </summary>
internal sealed record OneTimeOrderRefundPreview(
    decimal GrossBasis,
    decimal AppliedPercentage,
    decimal DeductionAmount,
    decimal EligiblePaid,
    decimal ProposedRefund);

internal sealed record OneTimeOrderPaymentAllocation(
    Payment Payment,
    decimal Amount);

internal sealed record OneTimeOrderRefundOutcome(
    string OrderNumber,
    decimal GrossBasis,
    decimal AppliedPercentage,
    decimal DeductionAmount,
    decimal EligiblePaid,
    decimal WalletCredit,
    IReadOnlyList<OneTimeOrderPaymentAllocation> Allocations);

internal static class OneTimeOrderRefund
{
    public static string WalletIdempotencyKeyFor(Guid ticketPublicId) =>
        $"one-time-refund:{ticketPublicId:N}:wallet";

    public static string RefundIdempotencyKeyFor(Guid ticketPublicId, long paymentId) =>
        $"one-time-refund:{ticketPublicId:N}:payment:{paymentId}";

    /// <summary>
    /// One-time-order refund math. Pure: reads only the frozen order total
    /// and the ticket's frozen applied percentage.
    /// </summary>
    public static (decimal Deduction, decimal Net) Calculate(
        decimal grossBasis,
        decimal appliedPercentage)
    {
        var deduction = decimal.Round(
            grossBasis * appliedPercentage / 100m,
            2,
            MidpointRounding.AwayFromZero);
        return (deduction, grossBasis - deduction);
    }

    /// <summary>
    /// Every payment that can back a one-time-order refund: successful (or
    /// partially refunded) payments for this order and customer, ordered
    /// deterministically by id. Tracked with refunds: the caller mutates them
    /// inside its own transaction.
    /// </summary>
    public static Task<List<Payment>> FindEligiblePaymentsAsync(
        DoodhDirectDbContext dbContext,
        long orderId,
        long customerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.Payments
            .Include(x => x.Refunds)
            .Where(x => x.OrderId == orderId
                && x.CustomerId == customerId
                && (x.Status == PaymentStatus.Success || x.Status == PaymentStatus.PartiallyRefunded))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Whether any verified (succeeded) refund settlement already exists for
    /// the order, regardless of payment status. Externally settled tickets
    /// complete without creating new money movement.
    /// </summary>
    public static Task<bool> HasVerifiedSettlementAsync(
        DoodhDirectDbContext dbContext,
        long orderId,
        long customerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.Payments.AsNoTracking()
            .Where(x => x.OrderId == orderId && x.CustomerId == customerId
                && x.Refunds.Any(r => r.Status == RefundStatus.Succeeded))
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Deterministically allocates the refund total across eligible payments
    /// (by ascending id), capped by each payment's outstanding balance.
    /// </summary>
    public static IReadOnlyList<OneTimeOrderPaymentAllocation> Allocate(
        decimal total,
        IReadOnlyList<Payment> payments)
    {
        var allocations = new List<OneTimeOrderPaymentAllocation>();
        var remaining = total;
        foreach (var payment in payments)
        {
            if (remaining <= 0)
            {
                break;
            }

            var balance = payment.Amount - payment.RefundedAmount;
            if (balance <= 0)
            {
                continue;
            }

            var portion = balance > remaining ? remaining : balance;
            allocations.Add(new OneTimeOrderPaymentAllocation(payment, portion));
            remaining -= portion;
        }

        return allocations;
    }

    /// <summary>
    /// Credits the refund total to the wallet (creating it on demand).
    /// A committed credit with this key is reused, never duplicated.
    /// </summary>
    public static async Task CreditWalletAsync(
        DoodhDirectDbContext dbContext,
        long customerId,
        long orderId,
        long? paymentId,
        string currency,
        decimal amount,
        string idempotencyKey,
        string description,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var wallet = await dbContext.Wallets
            .SingleOrDefaultAsync(x => x.CustomerId == customerId, cancellationToken);
        if (wallet is null)
        {
            wallet = new Wallet(customerId, currency);
            dbContext.Wallets.Add(wallet);
        }
        else
        {
            // Ledger idempotency: a committed credit with this key is reused,
            // never duplicated — even across worker restarts.
            var existing = await dbContext.WalletTransactions
                .SingleOrDefaultAsync(
                    x => x.WalletId == wallet.Id && x.IdempotencyKey == idempotencyKey,
                    cancellationToken);
            if (existing is not null)
            {
                return;
            }
        }

        Mutate(() => wallet.Credit(
            WalletTransactionType.RefundCredit,
            amount,
            idempotencyKey,
            description,
            now,
            paymentId,
            orderId));
    }

    private static void Mutate(Action operation)
    {
        try
        {
            operation();
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }
    }
}
