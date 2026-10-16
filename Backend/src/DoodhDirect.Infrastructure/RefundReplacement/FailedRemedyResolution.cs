using System.Globalization;
using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

internal enum OriginalTicketResolutionOutcome
{
    Resolved,
    NotApplicable,
    AlreadyResolved,
    OriginalMissing
}

/// <summary>
/// D1-D slice 5.2: shared resolution of the original approved ReDelivery
/// ticket whose replacement failed and was made whole through the linked
/// refund-only remedy. Used by every successful refund path (automatic
/// timeout, staff-driven completion) so the outcome, audit, and notification
/// are identical wherever the refund succeeds.
///
/// The original remedy (re-delivery) can never be fulfilled, so leaving the
/// ticket <c>Approved</c> would strand it forever with no owner. The
/// resolution is the explicit terminal <c>Failed</c> status — never <c>Completed</c>,
/// which keeps meaning successful fulfillment (Rule 8) — with remarks, audit
/// (<c>Resolution = "ReplacementFailedRefunded"</c>) and a customer
/// notification that all state the replacement failed and name the refunding
/// ticket. It moves no money: the single-share wallet credit and <c>Refund</c>
/// row written for the second ticket (under its own <c>delivery-fail:</c>
/// idempotency key) are the only financial effects.
///
/// Safety: every caller invokes this inside the second ticket's serializable
/// transaction, after the second ticket's refund has succeeded and been
/// recorded — so both tickets resolve atomically ("nothing happened" or
/// "everything happened"). A recorded choice, a pending approval, a pending
/// refund, or a failed refund never reaches this helper. A missing link or a
/// non-Approved original resolves nothing (the refund still stands);
/// an already-resolved original is skipped idempotently. Concurrent duplicate
/// runs contend on both tickets' <c>RowVersion</c> tokens, so exactly one run
/// can resolve the pair.
/// </summary>
internal static class FailedRemedyResolution
{
    public const string ResolvedAction = "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED";

    private const string EntityType = "RefundReplacementRequest";

    public static async Task<OriginalTicketResolutionOutcome> TryResolveOriginalAsync(
        DoodhDirectDbContext dbContext,
        INotificationEventWriter notificationEventWriter,
        RefundReplacementRequest secondTicket,
        decimal walletCredit,
        string deliveryNumber,
        long resolvedByUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(notificationEventWriter);
        ArgumentNullException.ThrowIfNull(secondTicket);

        var occurrence = secondTicket.SubscriptionDelivery;
        if (occurrence is null || !occurrence.IsReplacement || occurrence.RefundReplacementRequestId is null)
        {
            return OriginalTicketResolutionOutcome.NotApplicable;
        }

        var originalId = occurrence.RefundReplacementRequestId.Value;
        var original = await dbContext.RefundReplacementRequests
            .SingleOrDefaultAsync(x => x.Id == originalId, cancellationToken);
        if (original is null)
        {
            return OriginalTicketResolutionOutcome.OriginalMissing;
        }

        if (original.Status != RefundReplacementStatus.Approved
            || original.CustomerChoice != CustomerChoice.ReDelivery)
        {
            return OriginalTicketResolutionOutcome.AlreadyResolved;
        }

        var remarks =
            $"Replacement delivery {deliveryNumber} failed; the outstanding entitlement was refunded " +
            $"{walletCredit.ToString("0.00", CultureInfo.InvariantCulture)} to wallet under ticket {secondTicket.RequestNumber}. " +
            "Original re-delivery remedy closed as non-delivered (no delivery occurred).";
        try
        {
            original.MarkRemedyFailed(resolvedByUserId, now, remarks);
        }
        catch (InvalidOperationException exception)
        {
            throw new ConflictException(exception.Message);
        }

        dbContext.AddAuditLog(new AuditLog(
            resolvedByUserId,
            ResolvedAction,
            EntityType,
            original.PublicId.ToString(),
            JsonSerializer.Serialize(new
            {
                original.RequestNumber,
                Status = RefundReplacementStatus.Approved.ToString()
            }),
            JsonSerializer.Serialize(new
            {
                original.RequestNumber,
                Status = original.Status.ToString(),
                Resolution = "ReplacementFailedRefunded",
                RefundTicket = secondTicket.RequestNumber,
                DeliveryNumber = deliveryNumber,
                WalletCredit = walletCredit
            }),
            null,
            null,
            remarks,
            now));

        notificationEventWriter.Add(new NotificationEventRequest(
            original.CustomerId,
            NotificationEventTypes.RedeliveryFailedResolved,
            $"refund-replacement:{original.PublicId:N}:redelivery-failed-resolved",
            new Dictionary<string, string>
            {
                ["requestId"] = original.PublicId.ToString(),
                ["requestNumber"] = original.RequestNumber,
                ["refundRequestNumber"] = secondTicket.RequestNumber,
                ["deliveryNumber"] = deliveryNumber,
                ["walletCredit"] = walletCredit.ToString("0.00", CultureInfo.InvariantCulture),
                ["message"] =
                    $"Your replacement delivery {deliveryNumber} could not be completed, so " +
                    $"{walletCredit.ToString("0.00", CultureInfo.InvariantCulture)} was credited to your wallet " +
                    $"under ticket {secondTicket.RequestNumber} and the original re-delivery request " +
                    $"{original.RequestNumber} was closed without delivery."
            },
            $"/refund-replacements/{original.PublicId}",
            now));

        return OriginalTicketResolutionOutcome.Resolved;
    }
}
