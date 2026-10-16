using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// D1-D slice 5.2: the single implementation of the frozen delivery-fail
/// refund, shared by every successful refund-resolution path (the D1-C
/// automatic timeout processor and the staff completion of a refund-only
/// second-failure ticket).
///
/// Money rules (D1-C, unchanged — never edit the formula here):
/// <list type="bullet">
/// <item>Gross = Round(Payable / TotalEntitlement, 2, AwayFromZero) from the
/// frozen <c>Subscription.PayableAmount</c> (charge-inclusive). Product
/// prices, tax/charge masters and mappings are never read or modified.</item>
/// <item>Deduction uses ONLY the ticket's frozen
/// <c>DeliveryFailDeductionPercentageSnapshot</c>, never current config.</item>
/// <item>Cumulative credits are capped so they never exceed the frozen
/// payable: final = min(gross - deduction, payment.Amount -
/// payment.RefundedAmount). The final entitlement absorbs any rounding
/// remainder.</item>
/// <item>The wallet credit is a ledger transaction with the deterministic key
/// <c>delivery-fail:{requestPublicId:N}:refund</c>; the same key identifies
/// the <c>Refund</c> row. No payment gateway is called: the refund is
/// recorded locally on the existing Payment/Refund aggregate and paid out as
/// wallet credit. A committed credit/row with this key is reused, never
/// duplicated — even across worker restarts or staff/timeout races.</item>
/// </list>
///
/// Callers run this inside the ticket's serializable transaction and commit
/// the ticket transition, the money movement, and the original-ticket
/// resolution atomically ("nothing happened" or "everything happened").
/// </summary>
internal sealed record DeliveryFailRefundAmounts(
    decimal Gross,
    decimal Deduction,
    decimal Final,
    decimal Remaining);

internal static class DeliveryFailRefund
{
    public static string IdempotencyKeyFor(Guid ticketPublicId) =>
        $"delivery-fail:{ticketPublicId:N}:refund";

    /// <summary>
    /// Frozen D1-C refund math. Pure: reads only frozen snapshots and the
    /// payment's persisted counters.
    /// </summary>
    public static DeliveryFailRefundAmounts Calculate(
        decimal payableAmount,
        int totalEntitlement,
        decimal deductionPercentage,
        decimal paymentAmount,
        decimal paymentRefundedAmount)
    {
        var gross = decimal.Round(
            payableAmount / totalEntitlement,
            2,
            MidpointRounding.AwayFromZero);
        var deduction = decimal.Round(
            gross * deductionPercentage / 100m,
            2,
            MidpointRounding.AwayFromZero);
        var final = gross - deduction;

        var remaining = paymentAmount - paymentRefundedAmount;
        if (final > remaining)
        {
            final = remaining;
        }

        return new DeliveryFailRefundAmounts(gross, deduction, final, remaining);
    }

    /// <summary>
    /// The payment that can back a delivery-fail refund: the latest
    /// successful (or partially refunded) payment for this subscription and
    /// customer. Tracked with refunds: the caller mutates it inside its own
    /// transaction.
    /// </summary>
    public static Task<Payment?> FindPaymentAsync(
        DoodhDirectDbContext dbContext,
        long subscriptionId,
        long customerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.Payments
            .Include(x => x.Refunds)
            .Where(x => x.SubscriptionId == subscriptionId
                && x.CustomerId == customerId
                && (x.Status == PaymentStatus.Success || x.Status == PaymentStatus.PartiallyRefunded))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Credits the frozen share to the wallet (creating it on demand).
    /// A committed credit with this key is reused, never duplicated.
    /// </summary>
    public static async Task CreditWalletAsync(
        DoodhDirectDbContext dbContext,
        long customerId,
        long subscriptionId,
        long paymentId,
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
            subscriptionId: subscriptionId));
    }

    /// <summary>
    /// Records the refund locally on the existing Payment/Refund aggregate
    /// without touching any gateway. A pre-existing refund row with this key
    /// is authoritative and never duplicated.
    /// </summary>
    public static void RecordRefund(
        Payment payment,
        decimal amount,
        string idempotencyKey,
        string reason,
        long requestedByUserId,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(payment);
        var existing = payment.Refunds.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey);
        if (existing is not null)
        {
            if (existing.Status != RefundStatus.Succeeded)
            {
                throw new ConflictException(
                    "A refund with the same reference is already in progress.");
            }

            return;
        }

        Refund refund = Mutate(() => payment.StartRefund(
            amount,
            reason,
            idempotencyKey,
            requestedByUserId));
        Mutate(() => refund.Succeed(now));
        Mutate(() => payment.CompleteRefund(amount));
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

    private static T Mutate<T>(Func<T> operation)
    {
        try
        {
            return operation();
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
