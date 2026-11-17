using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Expired re-delivery notice sweeper. Periodically finds subscription
/// delivery-fail tickets whose recorded Re-Delivery choice passed the
/// one-hour same-day window (<c>CustomerChoiceAt + 1 hour</c>, India-local)
/// while still awaiting staff action, and ensures the single deterministic
/// customer expiry notice through the existing sweep notice path.
///
/// Notice-only: never converts, approves, or moves money. Each ticket is
/// processed on its own; a ticket lost to a concurrent winner (lazy
/// customer/staff notice, conversion, approval, completion) is skipped, never
/// retried in the same cycle. Restarts are safe: tickets whose deadline
/// passed while the API was down are picked up on the next cycle.
/// </summary>
public sealed class RedeliveryExpiryProcessor(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    IRefundReplacementService refundReplacementService,
    IOptions<RedeliveryExpiryOptions> expiryOptions,
    ILogger<RedeliveryExpiryProcessor> logger) : IRedeliveryExpiryProcessor
{
    public async Task<int> ProcessExpiredRedeliveriesAsync(CancellationToken cancellationToken)
    {
        var batchSize = expiryOptions.Value.BatchSize;
        var now = timeProvider.Now;
        var threshold = now.AddHours(-1);
        // Already-noticed tickets stay AwaitingCustomerChoice with their
        // Re-Delivery choice, so without this filter they would keep matching
        // every sweep and — via Take(batchSize) — starve later un-noticed
        // tickets. This only narrows selection; the transactional unique
        // EventKey guard in EnsureRedeliveryExpiryNoticedAsync stays the
        // authoritative exactly-once safeguard.
        var candidates = await UnnoticedCandidates()
            .AsNoTracking()
            .Where(x => x.Status == RefundReplacementStatus.AwaitingCustomerChoice
                && (x.Source == RefundReplacementSource.DeliveryFailed || x.Source == RefundReplacementSource.MilkTestRejected)
                && x.SubscriptionDeliveryId != null
                && x.CustomerChoice == CustomerChoice.ReDelivery
                && x.CustomerChoiceAt != null
                && x.CustomerChoiceAt <= threshold)
            .OrderBy(x => x.CustomerChoiceAt)
            .ThenBy(x => x.Id)
            .Take(batchSize)
            .Select(x => x.PublicId)
            .ToListAsync(cancellationToken);

        var notified = 0;
        foreach (var publicId in candidates)
        {
            try
            {
                if (await ProcessTicketAsync(publicId, cancellationToken))
                {
                    notified++;
                }
            }
            catch (NotFoundException)
            {
                // Ticket deleted between candidate selection and processing.
            }
            catch (ConflictException exception)
            {
                // Lost a race with a concurrent winner (conversion, approval,
                // completion) or not yet expired on re-read; the winner owns
                // the outcome, so there is nothing to retry.
                logger.LogInformation(
                    exception,
                    "Redelivery expiry sweep skipped a ticket already resolved by a concurrent operation.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Redelivery expiry sweep failed for a ticket.");
            }
        }

        return notified;
    }

    /// <summary>
    /// Candidate tickets not yet carrying the deterministic expiry notice.
    /// The notice key embeds the request id in N format (lowercase, no
    /// dashes); SQL renders the same text from the stored id, so the
    /// exclusion happens server-side, before ordering and Take(batchSize).
    /// SQLite keeps Guids as dashed uppercase TEXT, SQL Server as
    /// uniqueidentifier: both spellings normalize to the N form below.
    /// </summary>
    private IQueryable<RefundReplacementRequest> UnnoticedCandidates() =>
        dbContext.Database.IsSqlServer()
            ? dbContext.RefundReplacementRequests.FromSqlRaw(
                "SELECT [r].* FROM [RefundReplacementRequest] AS [r] " +
                "WHERE NOT EXISTS (SELECT 1 FROM [NotificationEvent] AS [e] WHERE [e].[EventKey] = " +
                "'refund-replacement:' + LOWER(REPLACE(CONVERT(nvarchar(36), [r].[PublicId]), '-', '')) + ':redelivery-expired')")
            : dbContext.RefundReplacementRequests.FromSqlRaw(
                "SELECT [r].* FROM [RefundReplacementRequest] AS [r] " +
                "WHERE NOT EXISTS (SELECT 1 FROM [NotificationEvent] AS [e] WHERE [e].[EventKey] = " +
                "'refund-replacement:' || LOWER(REPLACE([r].[PublicId], '-', '')) || ':redelivery-expired')");

    public async Task<bool> ProcessTicketAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await refundReplacementService.NotifyExpiredRedeliveryForSweepAsync(
                requestId, cancellationToken);
        }
        catch (NotFoundException)
        {
            return false;
        }
        catch (ConflictException)
        {
            return false;
        }
    }
}
