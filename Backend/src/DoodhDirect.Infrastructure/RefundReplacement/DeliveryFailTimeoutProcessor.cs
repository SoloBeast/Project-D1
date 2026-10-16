using System.Data;
using System.Globalization;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// D1-C automatic timeout resolution for delivery-fail tickets.
///
/// Each candidate ticket is processed in its own serializable transaction:
/// the persisted choice deadline is re-verified, the choice is set to Refund
/// with an automatic-timeout resolution, the ticket moves through the existing
/// approve/complete flow, and the frozen refund (less the snapshotted
/// deduction) is credited to the wallet. Everything commits atomically, so a
/// crash can only leave "nothing happened" (safe to retry) or "everything
/// happened" (idempotent skip on retry) — never "completed without credit".
///
/// Money rules:
/// <list type="bullet">
/// <item>Gross = Round(Payable / TotalEntitlement, 2, AwayFromZero) from the
/// frozen <c>Subscription.PayableAmount</c> (charge-inclusive). Product prices,
/// tax/charge masters and mappings are never read or modified.</item>
/// <item>Deduction uses ONLY the ticket's frozen
/// <c>DeliveryFailDeductionPercentageSnapshot</c>, never current config.</item>
/// <item>Cumulative credits are capped so they never exceed the frozen payable:
/// final = min(gross - deduction, payment.Amount - payment.RefundedAmount).
/// The final entitlement absorbs any rounding remainder.</item>
/// <item>The wallet credit is a ledger transaction with the deterministic key
/// <c>delivery-fail:{requestPublicId:N}:refund</c>; the same key identifies the
/// <c>Refund</c> row. No payment gateway is called: the refund is recorded
/// locally on the existing Payment/Refund aggregate and paid out as wallet
/// credit, which is the approved automatic-refund path.</item>
/// </list>
///
/// Concurrency: contends on the ticket <c>RowVersion</c> token exactly like the
/// customer choice path, plus the unique financial/event keys. A loser (expired
/// customer choice, another worker instance, or a duplicate commit) observes
/// the resolved state and performs no second credit or notification.
/// </summary>
public sealed class DeliveryFailTimeoutProcessor(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    INotificationEventWriter notificationEventWriter,
    IOptions<DeliveryFailTimeoutOptions> timeoutOptions,
    IOptions<PaymentOptions> paymentOptions,
    ILogger<DeliveryFailTimeoutProcessor> logger) : IDeliveryFailTimeoutProcessor
{
    private const string EntityType = "RefundReplacementRequest";
    private const string TimeoutAction = "REFUND_REPLACEMENT.AUTOMATIC_TIMEOUT";
    private const string RefundAction = "REFUND_REPLACEMENT.AUTOMATIC_REFUND";

    public async Task<int> ProcessExpiredTicketsAsync(CancellationToken cancellationToken)
    {
        var batchSize = timeoutOptions.Value.BatchSize;
        var now = timeProvider.Now;
        var candidates = await dbContext.RefundReplacementRequests
            .AsNoTracking()
            .Where(x => x.Status == RefundReplacementStatus.AwaitingCustomerChoice
                && x.CustomerChoice == null
                && x.SubscriptionDeliveryId != null
                && x.Deadline != null
                && x.Deadline <= now)
            .OrderBy(x => x.Deadline)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .Select(x => x.PublicId)
            .ToListAsync(cancellationToken);

        var resolved = 0;
        foreach (var publicId in candidates)
        {
            try
            {
                var outcome = await ProcessTicketAsync(publicId, cancellationToken);
                if (outcome is not null)
                {
                    resolved++;
                    logger.LogInformation(
                        "Delivery-fail ticket {RequestNumber} auto-refunded {WalletCredit:0.00} to wallet for delivery {DeliveryNumber}.",
                        outcome.RequestNumber,
                        outcome.WalletCredit,
                        outcome.DeliveryNumber);
                }
            }
            catch (ConflictException exception)
            {
                // Lost a race with a customer choice or another worker instance;
                // the winner owns the resolution, so there is nothing to retry.
                logger.LogInformation(
                    exception,
                    "Delivery-fail timeout skipped a ticket already resolved by a concurrent operation.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Delivery-fail timeout processing failed for a ticket.");
            }
        }

        return resolved;
    }

    public async Task<DeliveryFailTimeoutResult?> ProcessTicketAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        DeliveryFailTimeoutResult? outcome = null;
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            // Retry safety: the SQL Server execution strategy may re-run this
            // operation after a transient failure. Discard any half-mutated
            // tracked state so every attempt reads authoritative state.
            dbContext.ChangeTracker.Clear();
            outcome = await ResolveAsync(requestId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return outcome;
    }

    private async Task<DeliveryFailTimeoutResult?> ResolveAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var ticket = await dbContext.RefundReplacementRequests
            .Include(x => x.Delivery)
            .Include(x => x.SubscriptionDelivery).ThenInclude(x => x!.Subscription)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        // Idempotent skip: a concurrent winner (customer choice, a previous
        // timeout run after restart, or another worker instance) already
        // resolved the ticket. No second credit, no second notification.
        if (ticket.CustomerChoice.HasValue
            || ticket.Status != RefundReplacementStatus.AwaitingCustomerChoice)
        {
            return null;
        }

        // Only subscription-delivery tickets time out. AwaitingCustomerChoice
        // already implies a subscription source by domain invariant; the extra
        // guard keeps one-time-order tickets out even if that ever changes.
        if (ticket.SubscriptionDeliveryId is null || ticket.SubscriptionDelivery is null)
        {
            return null;
        }

        // The persisted deadline is authoritative; current Admin configuration
        // is never consulted for an existing ticket.
        var now = timeProvider.Now;
        if (ticket.Deadline is null || now < ticket.Deadline.Value)
        {
            return null;
        }

        var subscription = ticket.SubscriptionDelivery.Subscription;
        if (subscription is null)
        {
            logger.LogError(
                "Delivery-fail timeout cannot resolve ticket {RequestNumber}: the subscription is missing.",
                ticket.RequestNumber);
            return null;
        }

        var deductionPercentage = ticket.DeliveryFailDeductionPercentageSnapshot;
        if (deductionPercentage is null)
        {
            logger.LogError(
                "Delivery-fail timeout cannot resolve ticket {RequestNumber}: the deduction snapshot is missing.",
                ticket.RequestNumber);
            return null;
        }

        var payment = await DeliveryFailRefund.FindPaymentAsync(
            dbContext, subscription.Id, ticket.CustomerId, cancellationToken);
        if (payment is null)
        {
            // No successful subscription payment to refund against. Leave the
            // ticket awaiting so a later cycle (or an operator) can resolve it
            // once the payment state is repaired; never invent money.
            logger.LogError(
                "Delivery-fail timeout cannot resolve ticket {RequestNumber}: no successful subscription payment found.",
                ticket.RequestNumber);
            return null;
        }

        // Frozen refund math (D1-C formula, shared implementation): one
        // charge-inclusive share of the frozen payable, less the snapshotted
        // deduction, capped by the payment's remaining refundable balance.
        var amounts = DeliveryFailRefund.Calculate(
            subscription.PayableAmount,
            subscription.TotalEntitlement,
            deductionPercentage.Value,
            payment.Amount,
            payment.RefundedAmount);
        var gross = amounts.Gross;
        var deduction = amounts.Deduction;
        var final = amounts.Final;
        if (amounts.Remaining <= 0)
        {
            logger.LogError(
                "Delivery-fail timeout cannot resolve ticket {RequestNumber}: the subscription payment is already fully refunded.",
                ticket.RequestNumber);
            return null;
        }

        var idempotencyKey = $"delivery-fail:{ticket.PublicId:N}:refund";
        var deliveryNumber = ticket.Delivery.DeliveryNumber ?? ticket.Delivery.ReferenceNumber;
        var previousType = ticket.Type;

        Mutate(() => ticket.RecordAutomaticTimeoutRefund(now));
        var remarks =
            $"Automatic timeout: no customer choice before {ticket.Deadline:yyyy-MM-dd HH:mm}; refund auto-selected and approved.";
        Mutate(() => ticket.Approve(ticket.CustomerId, now, remarks));

        if (final > 0)
        {
            await DeliveryFailRefund.CreditWalletAsync(
                dbContext,
                ticket.CustomerId,
                subscription.Id,
                payment.Id,
                paymentOptions.Value.Currency,
                final,
                idempotencyKey,
                $"Delivery-fail auto-refund {ticket.RequestNumber} for delivery {deliveryNumber}",
                now,
                cancellationToken);
            DeliveryFailRefund.RecordRefund(
                payment,
                final,
                idempotencyKey,
                "Delivery-fail automatic timeout refund to wallet.",
                ticket.CustomerId,
                now);
        }

        Mutate(() => ticket.Complete(ticket.CustomerId, now, remarks));

        // D1-D slice 5.2: when the resolved ticket is the refund-only remedy
        // of a failed re-delivery replacement, resolve the original ReDelivery
        // ticket here — in the same transaction, only because this refund
        // succeeded — with the explicit terminal Failed outcome (never
        // Completed, which keeps meaning successful fulfillment).
        var originalOutcome = await FailedRemedyResolution.TryResolveOriginalAsync(
            dbContext,
            notificationEventWriter,
            ticket,
            final,
            deliveryNumber,
            ticket.CustomerId,
            now,
            cancellationToken);
        if (originalOutcome == OriginalTicketResolutionOutcome.OriginalMissing)
        {
            logger.LogError(
                "Delivery-fail timeout cannot resolve the original ticket for replacement ticket {RequestNumber}: the original request row is missing.",
                ticket.RequestNumber);
        }

        dbContext.AddAuditLog(new AuditLog(
            ticket.CustomerId,
            TimeoutAction,
            EntityType,
            ticket.PublicId.ToString(),
            JsonSerializer.Serialize(new
            {
                ticket.RequestNumber,
                Status = RefundReplacementStatus.AwaitingCustomerChoice.ToString(),
                CustomerChoice = (string?)null,
                Type = previousType.ToString()
            }),
            JsonSerializer.Serialize(new
            {
                ticket.RequestNumber,
                Status = ticket.Status.ToString(),
                CustomerChoice = ticket.CustomerChoice.ToString(),
                ticket.CustomerChoiceAt,
                Resolution = "AutomaticTimeout"
            }),
            null,
            null,
            remarks,
            now));

        dbContext.AddAuditLog(new AuditLog(
            ticket.CustomerId,
            RefundAction,
            EntityType,
            ticket.PublicId.ToString(),
            JsonSerializer.Serialize(new { ticket.RequestNumber, Refunded = false }),
            JsonSerializer.Serialize(new
            {
                ticket.RequestNumber,
                DeliveryNumber = deliveryNumber,
                GrossRefund = gross,
                DeductionPercentage = deductionPercentage.Value,
                DeductionAmount = deduction,
                WalletCredit = final,
                SubscriptionPayable = subscription.PayableAmount,
                subscription.TotalEntitlement
            }),
            null,
            null,
            $"Automatic timeout refund of {final.ToString("0.00", CultureInfo.InvariantCulture)} " +
            $"credited to wallet (gross {gross.ToString("0.00", CultureInfo.InvariantCulture)}, " +
            $"deduction {deduction.ToString("0.00", CultureInfo.InvariantCulture)}).",
            now));

        // Buffered into the same transaction (outbox): the customer is notified
        // only after the financial operation is durably committed, and the
        // unique EventKey keeps redelivery exactly-once.
        notificationEventWriter.Add(new NotificationEventRequest(
            ticket.CustomerId,
            NotificationEventTypes.RefundReplacementAutoRefunded,
            $"refund-replacement:{ticket.PublicId:N}:auto-refund",
            new Dictionary<string, string>
            {
                ["requestId"] = ticket.PublicId.ToString(),
                ["requestNumber"] = ticket.RequestNumber,
                ["deliveryNumber"] = deliveryNumber,
                ["grossRefund"] = gross.ToString("0.00", CultureInfo.InvariantCulture),
                ["deductionPercentage"] = deductionPercentage.Value.ToString("0.##", CultureInfo.InvariantCulture),
                ["deductionAmount"] = deduction.ToString("0.00", CultureInfo.InvariantCulture),
                ["walletCredit"] = final.ToString("0.00", CultureInfo.InvariantCulture),
                ["message"] =
                    $"Delivery {deliveryNumber} was automatically refunded because no choice " +
                    $"was made before the deadline. {final.ToString("0.00", CultureInfo.InvariantCulture)} " +
                    $"was credited to your wallet after a deduction of " +
                    $"{deduction.ToString("0.00", CultureInfo.InvariantCulture)} " +
                    $"(ticket {ticket.RequestNumber})."
            },
            $"/refund-replacements/{ticket.PublicId}",
            now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConflictException(
                $"The request was changed by another operation. Refresh and try again. ({exception.GetType().Name})");
        }
        catch (DbUpdateException exception)
        {
            // A concurrent winner committed first (unique EventKey, wallet, or
            // refund idempotency key): re-read, and if the ticket is resolved
            // the other instance won — adopt its outcome instead of failing.
            dbContext.ChangeTracker.Clear();
            var current = await dbContext.RefundReplacementRequests
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken);
            if (current is not null
                && (current.CustomerChoice.HasValue
                    || current.Status != RefundReplacementStatus.AwaitingCustomerChoice))
            {
                logger.LogInformation(
                    exception,
                    "Delivery-fail timeout lost a commit race; the concurrent resolution stands.");
                return null;
            }

            throw;
        }

        return new DeliveryFailTimeoutResult(
            ticket.PublicId,
            ticket.RequestNumber,
            deliveryNumber,
            gross,
            deductionPercentage.Value,
            deduction,
            final);
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
