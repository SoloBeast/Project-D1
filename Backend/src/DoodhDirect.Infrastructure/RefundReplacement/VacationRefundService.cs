using System.Globalization;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// End-of-subscription automatic vacation-refund requests. One consolidated
/// Refund request per naturally completed prepaid subscription, containing
/// the eligible vacation-skipped occurrences as durable entries.
///
/// Business rules (owner-confirmed):
/// <list type="bullet">
/// <item>0% deduction, always — never the customer-fault percentage.</item>
/// <item>Frozen subscription pricing and charge snapshots; never repriced.</item>
/// <item>Created only after the final day's outcomes are finalized; unresolved
/// final-day work defers processing (no arbitrary grace period).</item>
/// <item>Creation never approves, settles, or credits: support reviews and the
/// existing authorized completion workflow settles to wallet.</item>
/// <item>Occurrences settled through any other refund workflow are excluded
/// entirely — including the retained deduction of a customer-fault partial.
/// Unresolved overlapping requests defer the subscription; ambiguous
/// settlement is excluded and reported, never guessed.</item>
/// </list>
/// </summary>
public sealed class VacationRefundService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    INumberSeriesService numberSeriesService,
    INotificationEventWriter notificationEventWriter) : IVacationRefundService
{
    public const string SeriesCode = "VACATIONREFUND";

    // Exclusion reason codes surfaced to support and asserted by tests.
    public const string ReasonManualSkip = "manualSkip";
    public const string ReasonAmbiguousCause = "ambiguousCause";
    public const string ReasonSettledElsewhere = "settledElsewhere";
    public const string ReasonUnresolvedTicket = "unresolvedTicket";
    public const string ReasonAmbiguousSettlement = "ambiguousSettlement";
    public const string ReasonAlreadyIncluded = "alreadyIncluded";
    public const string ReasonMissedDeliveryUnresolved = "missedDeliveryUnresolved";

    private const int MaximumRemarksLength = 1000;

    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var today = timeProvider.Today;
        var dueIds = await dbContext.Subscriptions
            .AsNoTracking()
            .Where(x => (x.Status == SubscriptionStatus.Active || x.Status == SubscriptionStatus.Paused)
                && x.EndDate < today
                && x.VacationRefundProcessedAt == null)
            .OrderBy(x => x.Id)
            .Select(x => x.PublicId)
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var publicId in dueIds)
        {
            var outcome = await ProcessSubscriptionAsync(publicId, cancellationToken);
            if (outcome.RequestCreated)
            {
                created++;
            }
        }

        return created;
    }

    public Task<VacationRefundProcessResult> ProcessSubscriptionAsync(
        Guid subscriptionPublicId,
        CancellationToken cancellationToken) =>
        ExecuteSerializableAsync(
            () => ProcessSubscriptionCoreAsync(subscriptionPublicId, cancellationToken),
            cancellationToken);

    public async Task<VacationRefundRequestResult> GetForStaffAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var entity = await DetailQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The vacation-refund request was not found.");
        EnsureStaffAccess(actor, entity);
        return ToResult(entity);
    }

    public async Task<VacationRefundRequestResult> GetForCustomerAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var entity = await DetailQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The vacation-refund request was not found.");
        if (entity.CustomerId != actor.UserId)
        {
            throw new NotFoundException("The vacation-refund request was not found.");
        }

        return ToResult(entity);
    }

    public async Task<VacationRefundSubscriptionStatus> GetStatusForCustomerAsync(
        RefundReplacementActor actor,
        Guid subscriptionPublicId,
        CancellationToken cancellationToken)
    {
        var subscription = await dbContext.Subscriptions
            .AsNoTracking()
            .Include(x => x.Deliveries)
            .SingleOrDefaultAsync(
                x => x.PublicId == subscriptionPublicId && x.CustomerId == actor.UserId,
                cancellationToken)
            ?? throw new NotFoundException("The subscription was not found.");

        var today = timeProvider.Today;
        var termEnded = subscription.EndDate < today;
        var processed = subscription.VacationRefundProcessedAt is not null;

        var request = await DetailQuery(asNoTracking: true)
            .Where(x => x.SubscriptionId == subscription.Id)
            .SingleOrDefaultAsync(cancellationToken);

        var inTerm = subscription.Deliveries
            .Where(x => x.ScheduledDate >= subscription.StartDate
                && x.ScheduledDate <= subscription.EndDate)
            .ToList();
        var vacationSkipped = inTerm
            .Count(x => x.Status == SubscriptionDeliveryStatus.Skipped
                && !x.IsReplacement
                && x.SkipCause == SubscriptionSkipCause.Vacation);
        var unresolved = inTerm
            .Count(x => x.Status == SubscriptionDeliveryStatus.Scheduled);

        var candidateIds = inTerm
            .Where(x => x.Status == SubscriptionDeliveryStatus.Skipped && !x.IsReplacement)
            .Select(x => x.Id)
            .ToList();
        var hasBlockingTicket = candidateIds.Count > 0 &&
            await dbContext.RefundReplacementRequests
                .AsNoTracking()
                .AnyAsync(x => x.SubscriptionDeliveryId != null
                    && candidateIds.Contains(x.SubscriptionDeliveryId.Value)
                    && (x.Status == RefundReplacementStatus.Pending
                        || x.Status == RefundReplacementStatus.Approved
                        || x.Status == RefundReplacementStatus.AwaitingCustomerChoice),
                    cancellationToken);

        var payment = await DeliveryFailRefund.FindPaymentAsync(
            dbContext, subscription.Id, subscription.CustomerId, cancellationToken);
        var hasBackingPayment = payment is not null;
        var remaining = payment is null ? 0m : payment.Amount - payment.RefundedAmount;

        string? blockedReason = null;
        if (termEnded && !processed && request is null)
        {
            if (subscription.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Paused))
            {
                blockedReason = "subscriptionNotActive";
            }
            else if (hasBlockingTicket)
            {
                blockedReason = "unresolvedTicketOverlap";
            }
            else if (!hasBackingPayment)
            {
                blockedReason = "noSuccessfulPayment";
            }
            else if (remaining <= 0)
            {
                blockedReason = "paymentFullyRefunded";
            }
        }

        return new VacationRefundSubscriptionStatus(
            subscription.PublicId,
            termEnded,
            processed,
            request is null ? null : ToResult(request),
            vacationSkipped,
            unresolved,
            hasBlockingTicket,
            hasBackingPayment,
            blockedReason);
    }

    public async Task<IReadOnlyList<VacationRefundRequestResult>> ListForStaffAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken)
    {
        var query = DetailQuery(asNoTracking: true);
        if (!actor.HasGlobalAccess)
        {
            var branches = actor.BranchIds;
            query = query.Where(x => branches.Contains(x.BranchId));
        }

        return (await query
                .OrderByDescending(x => x.Id)
                .ToListAsync(cancellationToken))
            .Select(ToResult)
            .ToList();
    }

    public async Task<IReadOnlyList<VacationRefundRequestResult>> ListForCustomerAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken) =>
        (await DetailQuery(asNoTracking: true)
                .Where(x => x.CustomerId == actor.UserId)
                .OrderByDescending(x => x.Id)
                .ToListAsync(cancellationToken))
            .Select(ToResult)
            .ToList();

    public Task<VacationRefundRequestResult> ApproveAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideVacationRefundRequest request,
        CancellationToken cancellationToken) =>
        ExecuteSerializableAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRemarks(request.Remarks);
            var entity = await DetailQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The vacation-refund request was not found.");
            EnsureStaffAccess(actor, entity);

            // Employee-origin policy: an employee-originated vacation-refund
            // decision requires an Owner or System Administrator actor
            // (never the requester); customer requests keep existing rules.
            await EmployeeOriginGuard.RequireDecisionAuthorityAsync(
                dbContext, entity.CustomerId, actor.UserId, "approved or rejected", cancellationToken);

            var now = timeProvider.Now;
            Mutate(() => entity.Approve(actor.UserId, now, request.Remarks));
            AddAudit(
                actor.UserId,
                "VACATION_REFUND.APPROVE",
                entity,
                new { entity.RequestNumber, Status = VacationRefundStatus.Pending.ToString() },
                new { entity.RequestNumber, Status = entity.Status.ToString() },
                request.Remarks,
                now);
            notificationEventWriter.Add(new NotificationEventRequest(
                entity.CustomerId,
                NotificationEventTypes.VacationRefundApproved,
                $"vacation-refund:{entity.PublicId:N}:approved",
                new Dictionary<string, string>
                {
                    ["requestId"] = entity.PublicId.ToString(),
                    ["requestNumber"] = entity.RequestNumber,
                    ["occurrenceCount"] = entity.OccurrenceCount.ToString(CultureInfo.InvariantCulture),
                    ["amount"] = entity.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture),
                    ["message"] = $"Your vacation-refund request {entity.RequestNumber} was approved and is pending completion."
                },
                $"/vacation-refunds/{entity.PublicId}",
                now));

            await dbContext.SaveChangesAsync(cancellationToken);
            return ToResult(entity);
        }, cancellationToken);

    public Task<VacationRefundRequestResult> RejectAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideVacationRefundRequest request,
        CancellationToken cancellationToken) =>
        ExecuteSerializableAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRemarks(request.Remarks);
            var entity = await DetailQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The vacation-refund request was not found.");
            EnsureStaffAccess(actor, entity);

            // Employee-origin policy: see ApproveAsync — the same rule
            // governs rejection.
            await EmployeeOriginGuard.RequireDecisionAuthorityAsync(
                dbContext, entity.CustomerId, actor.UserId, "approved or rejected", cancellationToken);

            var now = timeProvider.Now;
            Mutate(() => entity.Reject(actor.UserId, now, request.Remarks));
            AddAudit(
                actor.UserId,
                "VACATION_REFUND.REJECT",
                entity,
                new { entity.RequestNumber, Status = VacationRefundStatus.Pending.ToString() },
                new { entity.RequestNumber, Status = entity.Status.ToString() },
                request.Remarks,
                now);

            await dbContext.SaveChangesAsync(cancellationToken);
            return ToResult(entity);
        }, cancellationToken);

    public Task<VacationRefundRequestResult> CompleteAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideVacationRefundRequest request,
        CancellationToken cancellationToken) =>
        ExecuteSerializableAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRemarks(request.Remarks);
            var entity = await DetailQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The vacation-refund request was not found.");
            EnsureStaffAccess(actor, entity);

            // Employee-origin policy: completing credits the wallet, so an
            // employee-originated request requires a verifiable administrator
            // approval before settlement may continue.
            await EmployeeOriginGuard.RequireVerifiableAdminApprovalAsync(
                dbContext, entity.CustomerId, entity.DecidedByUserId, entity.RequestNumber, "completed", cancellationToken);

            var now = timeProvider.Now;
            // Lifecycle first: a non-Approved request fails here with the
            // domain status error, never with the settlement error below.
            Mutate(() => entity.Complete(actor.UserId, now, request.Remarks));

            var payment = await dbContext.Payments
                .Include(x => x.Refunds)
                .SingleOrDefaultAsync(x => x.Id == entity.PaymentId, cancellationToken)
                ?? throw new ConflictException(
                    $"Vacation-refund request {entity.RequestNumber} cannot be completed: " +
                    "its backing payment is no longer available. Resolve it with staff support first.");
            if (payment.Status is not (PaymentStatus.Success or PaymentStatus.PartiallyRefunded))
            {
                throw new ConflictException(
                    $"Vacation-refund request {entity.RequestNumber} cannot be completed: " +
                    $"its backing payment is '{payment.Status}'. Resolve it with staff support first.");
            }

            // The frozen basis is never repriced: the stored total must fit
            // the payment's remaining refundable balance exactly, otherwise
            // completion rolls back and the request stays Approved for repair.
            var remaining = payment.Amount - payment.RefundedAmount;
            if (remaining < entity.TotalAmount)
            {
                throw new ConflictException(
                    $"Vacation-refund request {entity.RequestNumber} cannot be completed: " +
                    $"the frozen total {entity.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture)} " +
                    $"exceeds the payment's remaining refundable balance " +
                    $"{remaining.ToString("0.00", CultureInfo.InvariantCulture)}. " +
                    "Resolve it with staff support first.");
            }

            var entries = entity.Occurrences
                .OrderBy(x => x.ScheduledDate)
                .ThenBy(x => x.SubscriptionDeliveryId)
                .ToList();
            // One wallet per customer: load-or-create once, then credit each
            // occurrence allocation under its own deterministic key. A key
            // already credited (a retried completion after a partial commit)
            // is reused, never duplicated.
            var wallet = await dbContext.Wallets
                .SingleOrDefaultAsync(x => x.CustomerId == entity.CustomerId, cancellationToken);
            if (wallet is null)
            {
                wallet = new Wallet(entity.CustomerId, entity.Currency);
                dbContext.Wallets.Add(wallet);
            }
            foreach (var entry in entries)
            {
                var creditKey = OccurrenceCreditKey(entity.PublicId, entry.SubscriptionDelivery.PublicId);
                if (wallet.Id != 0 && await dbContext.WalletTransactions.AnyAsync(
                    x => x.WalletId == wallet.Id && x.IdempotencyKey == creditKey,
                    cancellationToken))
                {
                    continue;
                }

                Mutate(() => wallet.Credit(
                    WalletTransactionType.RefundCredit,
                    entry.AllocationAmount,
                    creditKey,
                    $"Vacation refund {entity.RequestNumber} for {entry.ScheduledDate:dd MMM yyyy}",
                    now,
                    payment.Id,
                    subscriptionId: entity.SubscriptionId));
            }

            DeliveryFailRefund.RecordRefund(
                payment,
                entity.TotalAmount,
                RequestRefundKey(entity.PublicId),
                $"Vacation refund {entity.RequestNumber} ({entity.OccurrenceCount} occurrences, 0% deduction)",
                actor.UserId,
                now);

            AddAudit(
                actor.UserId,
                "VACATION_REFUND.COMPLETE",
                entity,
                new { entity.RequestNumber, Status = VacationRefundStatus.Approved.ToString() },
                new
                {
                    entity.RequestNumber,
                    Status = entity.Status.ToString(),
                    entity.OccurrenceCount,
                    entity.TotalAmount,
                    entity.DeductionPercentage,
                    PaymentId = payment.PublicId
                },
                request.Remarks,
                now);
            notificationEventWriter.Add(new NotificationEventRequest(
                entity.CustomerId,
                NotificationEventTypes.VacationRefundCompleted,
                $"vacation-refund:{entity.PublicId:N}:completed",
                new Dictionary<string, string>
                {
                    ["requestId"] = entity.PublicId.ToString(),
                    ["requestNumber"] = entity.RequestNumber,
                    ["occurrenceCount"] = entity.OccurrenceCount.ToString(CultureInfo.InvariantCulture),
                    ["amount"] = entity.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture),
                    ["message"] = $"Your vacation refund {entity.TotalAmount.ToString("0.00", CultureInfo.InvariantCulture)} " +
                        $"for request {entity.RequestNumber} was credited to your wallet."
                },
                $"/vacation-refunds/{entity.PublicId}",
                now));

            await dbContext.SaveChangesAsync(cancellationToken);
            return ToResult(entity);
        }, cancellationToken);

    internal static string RequestRefundKey(Guid requestPublicId) =>
        $"vacation-refund:{requestPublicId:N}:refund";

    internal static string OccurrenceCreditKey(Guid requestPublicId, Guid occurrencePublicId) =>
        $"vacation-refund:{requestPublicId:N}:occurrence-{occurrencePublicId:N}";

    private async Task<VacationRefundProcessResult> ProcessSubscriptionCoreAsync(
        Guid subscriptionPublicId,
        CancellationToken cancellationToken)
    {
        var subscription = await dbContext.Subscriptions
            .Include(x => x.Deliveries)
            .SingleOrDefaultAsync(x => x.PublicId == subscriptionPublicId, cancellationToken)
            ?? throw new NotFoundException("The subscription was not found.");

        var now = timeProvider.Now;
        var today = DateOnly.FromDateTime(now);

        if (subscription.VacationRefundProcessedAt is not null)
        {
            var existing = await dbContext.VacationRefundRequests
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.SubscriptionId == subscription.Id, cancellationToken);
            return new VacationRefundProcessResult(
                subscription.PublicId, false, existing?.PublicId, existing?.OccurrenceCount ?? 0, [], null, true);
        }

        // Only naturally completable subscriptions are processed. Cancelled
        // subscriptions follow their own settlement path, so they are
        // recorded as processed without a vacation request.
        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            subscription.MarkVacationRefundProcessed(now);
            AddAudit(null, "VACATION_REFUND.SKIP_CANCELLED", subscription, now,
                "Subscription cancelled; no vacation-refund request.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return new VacationRefundProcessResult(subscription.PublicId, false, null, 0, [], null, false);
        }

        if (subscription.Status is not (SubscriptionStatus.Active or SubscriptionStatus.Paused))
        {
            return new VacationRefundProcessResult(
                subscription.PublicId, false, null, 0, [], "subscriptionNotActive", false);
        }

        if (subscription.EndDate >= today)
        {
            return new VacationRefundProcessResult(
                subscription.PublicId, false, null, 0, [], "termNotEnded", false);
        }

        // End-of-term finalization signal: an in-term occurrence that is still
        // Scheduled defers processing only while it could still become a vacation
        // candidate (today or a future date, where the vacation cutoff has not
        // passed). A past-due Scheduled occurrence — e.g. a missed final delivery
        // outside the vacation — can never be vacation-skipped, so it must not
        // strand the eligible vacation occurrences. It is excluded with an explicit
        // reason and handled separately through the existing missed-delivery
        // (delivery-fail ticket) workflow. Replacements dated beyond the term
        // belong to remedy workflows, not to this term.
        var unresolved = subscription.Deliveries
            .Where(x => x.ScheduledDate <= subscription.EndDate
                && x.Status == SubscriptionDeliveryStatus.Scheduled)
            .OrderBy(x => x.ScheduledDate)
            .ToList();
        if (unresolved.Any(x => x.ScheduledDate >= today))
        {
            return new VacationRefundProcessResult(
                subscription.PublicId, false, null, 0, [], "finalDayUnresolved", false);
        }
        var missedUnresolved = unresolved
            .Where(x => x.ScheduledDate < today)
            .ToList();

        var candidates = subscription.Deliveries
            .Where(x => x.ScheduledDate >= subscription.StartDate
                && x.ScheduledDate <= subscription.EndDate
                && x.Status == SubscriptionDeliveryStatus.Skipped
                && !x.IsReplacement)
            .OrderBy(x => x.ScheduledDate)
            .ThenBy(x => x.Id)
            .ToList();

        var candidateIds = candidates.Select(x => x.Id).ToList();
        var tickets = candidateIds.Count == 0
            ? []
            : await dbContext.RefundReplacementRequests
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId != null && candidateIds.Contains(x.SubscriptionDeliveryId.Value))
                .ToListAsync(cancellationToken);

        // An unresolved per-delivery ticket on ANY candidate defers the whole
        // subscription: creating a partial request now could overlap the
        // ticket's later outcome, and marking processed could strand the
        // occurrence if the ticket is rejected. Retried on a later cycle.
        var blockingTicket = tickets.Any(x =>
            x.Status is RefundReplacementStatus.Pending
                or RefundReplacementStatus.Approved
                or RefundReplacementStatus.AwaitingCustomerChoice);
        if (blockingTicket)
        {
            return new VacationRefundProcessResult(
                subscription.PublicId, false, null, 0, [], "unresolvedTicketOverlap", false);
        }

        var alreadyIncluded = candidateIds.Count == 0
            ? new HashSet<long>()
            : (await dbContext.VacationRefundOccurrences
                .AsNoTracking()
                .Where(x => candidateIds.Contains(x.SubscriptionDeliveryId))
                .Select(x => x.SubscriptionDeliveryId)
                .ToListAsync(cancellationToken)).ToHashSet();

        var eligible = new List<SubscriptionDelivery>();
        var excluded = new List<VacationRefundExcludedOccurrence>();
        foreach (var missed in missedUnresolved)
        {
            excluded.Add(new VacationRefundExcludedOccurrence(
                missed.PublicId, missed.ScheduledDate, ReasonMissedDeliveryUnresolved));
        }
        foreach (var occurrence in candidates)
        {
            var exclusion = await ClassifyAsync(occurrence, tickets, alreadyIncluded, cancellationToken);
            if (exclusion is null)
            {
                eligible.Add(occurrence);
            }
            else
            {
                excluded.Add(new VacationRefundExcludedOccurrence(
                    occurrence.PublicId, occurrence.ScheduledDate, exclusion));
            }
        }

        var payment = await DeliveryFailRefund.FindPaymentAsync(
            dbContext, subscription.Id, subscription.CustomerId, cancellationToken);

        if (eligible.Count == 0 || payment is null)
        {
            subscription.MarkVacationRefundProcessed(now);
            AddAudit(null, "VACATION_REFUND.PROCESSED_EMPTY", subscription, now,
                JsonSerializer.Serialize(new
                {
                    eligibleCount = eligible.Count,
                    excludedCount = excluded.Count,
                    hasPayment = payment is not null,
                    excluded
                }));
            await dbContext.SaveChangesAsync(cancellationToken);
            return new VacationRefundProcessResult(
                subscription.PublicId, false, null, 0, excluded, payment is null ? "noSuccessfulPayment" : null, false);
        }

        // Frozen 0%-deduction math on the charge-inclusive payable snapshot
        // (never repriced): per-occurrence share rounded to paise, capped by
        // the payment's remaining refundable balance with any shortfall
        // absorbed from the last occurrence backwards, so the persisted
        // allocations always sum exactly to a total that never over-refunds.
        var gross = decimal.Round(
            subscription.PayableAmount / subscription.TotalEntitlement,
            2,
            MidpointRounding.AwayFromZero);
        var remaining = payment.Amount - payment.RefundedAmount;
        if (remaining <= 0)
        {
            subscription.MarkVacationRefundProcessed(now);
            AddAudit(null, "VACATION_REFUND.PROCESSED_FULLY_REFUNDED", subscription, now,
                "Backing payment is fully refunded; the customer is already whole.");
            await dbContext.SaveChangesAsync(cancellationToken);
            return new VacationRefundProcessResult(
                subscription.PublicId, false, null, 0, excluded, "paymentFullyRefunded", false);
        }

        var allocations = eligible.Select(_ => gross).ToList();
        var total = allocations.Sum();
        if (total > remaining)
        {
            var shortfall = total - remaining;
            for (var i = allocations.Count - 1; i >= 0 && shortfall > 0; i--)
            {
                var reduction = Math.Min(allocations[i], shortfall);
                allocations[i] -= reduction;
                shortfall -= reduction;
            }

            total = allocations.Sum();
        }

        var basis = JsonSerializer.Serialize(new
        {
            subscriptionPayable = subscription.PayableAmount,
            totalEntitlement = subscription.TotalEntitlement,
            grossPerOccurrence = gross,
            deductionPercentage = VacationRefundRequest.VacationDeductionPercentage,
            paymentRemaining = remaining,
            included = eligible
                .Select((x, i) => new
                {
                    occurrencePublicId = x.PublicId,
                    scheduledDate = x.ScheduledDate,
                    quantity = x.Quantity,
                    allocation = allocations[i]
                })
                .ToArray(),
            excluded = excluded
                .Select(x => new { x.OccurrencePublicId, x.ScheduledDate, reason = x.Reason })
                .ToArray()
        });

        var request = new VacationRefundRequest(
            subscription.Id,
            subscription.CustomerId,
            subscription.BranchId,
            gross,
            total,
            eligible.Count,
            subscription.PayableAmount,
            subscription.TotalEntitlement,
            payment.Currency,
            payment.Id,
            basis,
            now);
        var requestNumber = await numberSeriesService.GetNextNumberAsync(
            NumberSeriesSeedService.VacationRefundCode, null, cancellationToken);
        Mutate(() => request.AssignRequestNumber(requestNumber));
        dbContext.VacationRefundRequests.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);

        for (var i = 0; i < eligible.Count; i++)
        {
            dbContext.VacationRefundOccurrences.Add(new VacationRefundOccurrence(
                request.Id,
                eligible[i].Id,
                eligible[i].ScheduledDate,
                eligible[i].Quantity,
                allocations[i]));
        }

        subscription.MarkVacationRefundProcessed(now);
        AddAudit(null, "VACATION_REFUND.CREATE", subscription, now,
            JsonSerializer.Serialize(new
            {
                request.RequestNumber,
                occurrenceCount = eligible.Count,
                total,
                deductionPercentage = VacationRefundRequest.VacationDeductionPercentage,
                excludedCount = excluded.Count,
                excluded = excluded
                    .Select(x => new { x.OccurrencePublicId, x.ScheduledDate, reason = x.Reason })
                    .ToArray()
            }));
        notificationEventWriter.Add(new NotificationEventRequest(
            subscription.CustomerId,
            NotificationEventTypes.VacationRefundRequested,
            $"vacation-refund:{request.PublicId:N}:requested",
            new Dictionary<string, string>
            {
                ["requestId"] = request.PublicId.ToString(),
                ["requestNumber"] = request.RequestNumber,
                ["subscriptionId"] = subscription.PublicId.ToString(),
                ["occurrenceCount"] = eligible.Count.ToString(CultureInfo.InvariantCulture),
                ["amount"] = total.ToString("0.00", CultureInfo.InvariantCulture),
                ["message"] = $"A vacation-refund request {request.RequestNumber} was created for " +
                    $"{eligible.Count} skipped deliveries. Support will review it before any refund is issued."
            },
            $"/vacation-refunds/{request.PublicId}",
            now));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent run won the race for this subscription (unique
            // conflict or a write race the loser cannot win): adopt the
            // winner instead of duplicating the logical request. Only when
            // no request exists is the failure real, and it is rethrown.
            dbContext.ChangeTracker.Clear();
            var winner = await dbContext.VacationRefundRequests
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.SubscriptionId == subscription.Id, cancellationToken);
            if (winner is not null)
            {
                return new VacationRefundProcessResult(
                    subscription.PublicId, false, winner.PublicId, winner.OccurrenceCount, excluded, null, false);
            }

            throw;
        }

        return new VacationRefundProcessResult(
            subscription.PublicId, true, request.PublicId, eligible.Count, excluded, null, false);
    }

    /// <summary>
    /// Classifies one vacation-skipped candidate against authoritative refund
    /// records. Returns null when the occurrence is eligible, otherwise the
    /// exclusion reason code. Never relies on displayed ticket status or
    /// customer-facing responses for settlement: settled means a succeeded
    /// refund row or a committed wallet credit under the ticket's
    /// deterministic key.
    /// </summary>
    private async Task<string?> ClassifyAsync(
        SubscriptionDelivery occurrence,
        IReadOnlyList<RefundReplacementRequest> tickets,
        HashSet<long> alreadyIncluded,
        CancellationToken cancellationToken)
    {
        if (occurrence.SkipCause != SubscriptionSkipCause.Vacation)
        {
            return occurrence.SkipCause == SubscriptionSkipCause.Manual
                ? ReasonManualSkip
                : ReasonAmbiguousCause;
        }

        if (alreadyIncluded.Contains(occurrence.Id))
        {
            return ReasonAlreadyIncluded;
        }

        var own = tickets.Where(x => x.SubscriptionDeliveryId == occurrence.Id).ToList();
        if (own.Count == 0)
        {
            return null;
        }

        foreach (var ticket in own.Where(x =>
            x.Status is RefundReplacementStatus.Completed or RefundReplacementStatus.Cancelled))
        {
            var keys = new[]
            {
                DeliveryFailRefund.IdempotencyKeyFor(ticket.PublicId),
                $"cancel-redelivery:{ticket.PublicId:N}:refund"
            };
            if (await HasSettlementEvidenceAsync(keys, cancellationToken))
            {
                return ReasonSettledElsewhere;
            }

            return ReasonAmbiguousSettlement;
        }

        foreach (var failed in own.Where(x => x.Status == RefundReplacementStatus.Failed))
        {
            // A Failed original was made whole through the linked refund-only
            // remedy on its replacement occurrence: exclude it as settled.
            var replacementIds = await dbContext.SubscriptionDeliveries
                .AsNoTracking()
                .Where(x => x.IsReplacement && x.ReplacesSubscriptionDeliveryId == occurrence.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            var remedyCompleted = replacementIds.Count > 0 &&
                await dbContext.RefundReplacementRequests
                    .AsNoTracking()
                    .Where(x => x.SubscriptionDeliveryId != null
                        && replacementIds.Contains(x.SubscriptionDeliveryId.Value)
                        && x.Status == RefundReplacementStatus.Completed)
                    .AnyAsync(cancellationToken);
            if (remedyCompleted)
            {
                return ReasonSettledElsewhere;
            }

            return ReasonAmbiguousSettlement;
        }

        // Rejected / Superseded tickets moved no money; the occurrence stays
        // eligible. (Pending / Approved / AwaitingCustomerChoice never reach
        // here: they defer the whole subscription above.)
        return null;
    }

    private async Task<bool> HasSettlementEvidenceAsync(
        string[] idempotencyKeys,
        CancellationToken cancellationToken)
    {
        if (await dbContext.Refunds
            .AsNoTracking()
            .AnyAsync(x => idempotencyKeys.Contains(x.IdempotencyKey)
                && x.Status == RefundStatus.Succeeded, cancellationToken))
        {
            return true;
        }

        return await dbContext.WalletTransactions
            .AsNoTracking()
            .AnyAsync(x => idempotencyKeys.Contains(x.IdempotencyKey), cancellationToken);
    }

    private IQueryable<VacationRefundRequest> DetailQuery(bool asNoTracking = false)
    {
        var query = dbContext.VacationRefundRequests
            .Include(x => x.Occurrences)
            .ThenInclude(x => x.SubscriptionDelivery)
            .Include(x => x.Subscription)
            .ThenInclude(x => x.Customer)
            .Include(x => x.Subscription)
            .ThenInclude(x => x.Branch)
            .AsQueryable();
        return asNoTracking ? query.AsNoTracking() : query;
    }

    private static VacationRefundRequestResult ToResult(VacationRefundRequest entity)
    {
        var customer = entity.Subscription.Customer;
        var branch = entity.Subscription.Branch;
        return new VacationRefundRequestResult(
            entity.PublicId,
            entity.RequestNumber,
            entity.Subscription.PublicId,
            entity.CustomerId,
            customer?.DisplayName ?? string.Empty,
            customer?.Mobile ?? string.Empty,
            entity.BranchId,
            branch?.Code ?? string.Empty,
            branch?.Name ?? string.Empty,
            entity.Reason,
            entity.Status,
            entity.OccurrenceCount,
            entity.GrossPerOccurrence,
            entity.TotalAmount,
            entity.DeductionPercentage,
            entity.PayableAmountSnapshot,
            entity.TotalEntitlementSnapshot,
            entity.Currency,
            entity.SubmittedAt,
            entity.DecidedByUserId,
            null,
            entity.DecidedAt,
            entity.DecisionRemarks,
            entity.CompletedByUserId,
            null,
            entity.CompletedAt,
            entity.CompletionRemarks,
            entity.Occurrences
                .OrderBy(x => x.ScheduledDate)
                .ThenBy(x => x.SubscriptionDeliveryId)
                .Select(x => new VacationRefundOccurrenceResult(
                    x.SubscriptionDelivery.PublicId, x.ScheduledDate, x.Quantity, x.AllocationAmount))
                .ToList(),
            customer is not null && EmployeeOriginGuard.IsEmployeeOriginated(customer.UserType));
    }

    private static void EnsureStaffAccess(RefundReplacementActor actor, VacationRefundRequest request)
    {
        if (!actor.HasGlobalAccess && !actor.BranchIds.Contains(request.BranchId))
        {
            // Out-of-scope requests are reported as missing, never as forbidden.
            throw new NotFoundException("The vacation-refund request was not found.");
        }
    }

    private void AddAudit(
        long? userId,
        string action,
        VacationRefundRequest request,
        object? oldValue,
        object? newValue,
        string? reason,
        DateTime now) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            "VacationRefundRequest",
            request.PublicId.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            newValue is null ? null : JsonSerializer.Serialize(newValue),
            null,
            null,
            reason,
            now));

    private void AddAudit(
        long? userId,
        string action,
        Subscription subscription,
        DateTime now,
        string? reason) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            "Subscription",
            subscription.PublicId.ToString(),
            null,
            null,
            null,
            null,
            reason,
            now));

    private static void ValidateRemarks(string? remarks)
    {
        if (remarks is not null && remarks.Length > MaximumRemarksLength)
        {
            throw new ValidationAppException(
                $"Remarks cannot exceed {MaximumRemarksLength} characters.", "remarks");
        }
    }

    private async Task<T> ExecuteSerializableAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            return await operation();
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable,
                cancellationToken);
            var result = await operation();
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
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
