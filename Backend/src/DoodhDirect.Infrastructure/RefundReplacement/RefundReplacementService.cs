using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Infrastructure.RefundReplacement;

/// <summary>
/// Customer refund/replacement request workflow.
///
/// The backend is the single source of truth for eligibility and for the
/// post-delivery window: the deadline is computed as
/// <c>Delivery.CompletedAt + configured window hours</c> using the
/// server-authoritative completion timestamp and the India-local clock. The
/// client never supplies a timestamp, an order id or a branch — the request is
/// bound to the delivery the authenticated customer actually owns.
///
/// A request opened from a customer-rejected doorstep milk test follows the
/// special flow: it is exempt from the window and from the proof image
/// requirement, and it auto-links the order, delivery and milk test.
///
/// No settlement rule is invented here: accepting a request records the
/// decision only. Refund execution and replacement-delivery creation remain
/// separate, explicitly-authorized operations.
/// </summary>
public sealed class RefundReplacementService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider,
    IRefundReplacementConfigurationService configurationService,
    INumberSeriesService numberSeriesService,
    IMediaStorage mediaStorage,
    INotificationEventWriter notificationEventWriter,
    IOptions<PaymentOptions>? paymentOptions = null) : IRefundReplacementService
{
    /// <summary>Numbering series used for the human-readable request number.</summary>
    public const string SeriesCode = "REFUNDREPLACEMENT";

    private const string EntityType = "RefundReplacementRequest";
    private const int MaximumReasonLength = 1000;
    private const int MaximumRemarksLength = 1000;
    private const int MaximumFileNameLength = 255;
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;
    private const long MaximumImageBytes = 10L * 1024L * 1024L;

    public async Task<RefundReplacementEligibilityResult> GetEligibilityAsync(
        RefundReplacementActor actor,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        // Ownership is enforced in the predicate: another customer's delivery is
        // indistinguishable from a missing one.
        var delivery = await dbContext.Deliveries
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.PublicId == deliveryId && x.CustomerId == actor.UserId,
                cancellationToken) ?? throw new NotFoundException("The delivery was not found.");

        var configuration = await configurationService.GetAsync(cancellationToken);
        var windowHours = configuration.WindowHours;

        var hasActiveRequest = await dbContext.RefundReplacementRequests
            .AnyAsync(
                x => x.DeliveryId == delivery.Id
                    && (x.Status == RefundReplacementStatus.Pending
                        || x.Status == RefundReplacementStatus.Approved),
                cancellationToken);

        // Resolve the relationship from persisted data. A failed delivery is eligible
        // only when this customer's rejected test belongs to this exact delivery.
        var hasRejectedMilkTest = await dbContext.MilkTests
            .AnyAsync(
                x => x.DeliveryId == delivery.Id
                    && x.CustomerId == actor.UserId
                    && x.CustomerDecision == MilkTestCustomerDecision.Rejected,
                cancellationToken);
        var isMilkTestRejectedFlow = delivery.Status == DeliveryStatus.Failed && hasRejectedMilkTest;
        DateTime? deadline = delivery.CompletedAt?.AddHours(windowHours);

        string? ineligibleReason = null;
        if (delivery.SourceType != DeliverySourceType.OneTimeOrder || delivery.OrderId is null)
        {
            ineligibleReason =
                "Refund or replacement requests are only available for one-time order deliveries.";
        }
        else if (isMilkTestRejectedFlow is false &&
                 (delivery.Status != DeliveryStatus.Delivered || delivery.CompletedAt is null))
        {
            ineligibleReason =
                "Refund or replacement requests are only available after the delivery is completed.";
        }
        else if (hasActiveRequest)
        {
            ineligibleReason =
                "An active refund or replacement request already exists for this delivery.";
        }
        else if (!isMilkTestRejectedFlow && (deadline is null || timeProvider.Now >= deadline.Value))
        {
            ineligibleReason =
                $"The refund or replacement request window of {windowHours} hours has closed.";
        }

        return new RefundReplacementEligibilityResult(
            IsEligible: ineligibleReason is null,
            IneligibleReason: ineligibleReason,
            WindowHours: windowHours,
            Deadline: deadline,
            HasActiveRequest: hasActiveRequest,
            ProofImageRequired: !isMilkTestRejectedFlow,
            IsMilkTestRejectedFlow: isMilkTestRejectedFlow);
    }

    public async Task<RefundReplacementRequestResult> SubmitAsync(
        RefundReplacementActor actor,
        SubmitRefundReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Reason, "reason", MaximumReasonLength, required: true);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var delivery = await dbContext.Deliveries
                .SingleOrDefaultAsync(
                    x => x.PublicId == request.DeliveryId && x.CustomerId == actor.UserId,
                    cancellationToken) ?? throw new NotFoundException("The delivery was not found.");

            if (delivery.SourceType != DeliverySourceType.OneTimeOrder || delivery.OrderId is null)
            {
                throw new BusinessRuleException(
                    "Refund or replacement requests are only available for one-time order deliveries.");
            }

            // Resolve the milk-test-rejection special flow. An explicit reference
            // must be a rejected test on this delivery; otherwise a rejected test
            // is linked automatically so Order/Delivery/MilkTest stay linked and the
            // proof image requirement is waived.
            MilkTest? rejectedMilkTest;
            if (request.MilkTestId is not null)
            {
                rejectedMilkTest = await dbContext.MilkTests
                    .SingleOrDefaultAsync(
                        x => x.PublicId == request.MilkTestId.Value
                            && x.DeliveryId == delivery.Id
                            && x.CustomerId == actor.UserId,
                        cancellationToken) ?? throw new NotFoundException("The doorstep test was not found.");

                if (rejectedMilkTest.CustomerDecision != MilkTestCustomerDecision.Rejected)
                {
                    throw new BusinessRuleException(
                        "The refund/replacement special flow requires a customer-rejected doorstep milk test.");
                }
            }
            else
            {
                rejectedMilkTest = await dbContext.MilkTests
                    .Where(x => x.DeliveryId == delivery.Id
                        && x.CustomerId == actor.UserId
                        && x.CustomerDecision == MilkTestCustomerDecision.Rejected)
                    .OrderByDescending(x => x.RejectedAt)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var source = rejectedMilkTest is null
                ? RefundReplacementSource.CustomerPostDelivery
                : RefundReplacementSource.MilkTestRejected;
            var isMilkTestRejectedFlow = source == RefundReplacementSource.MilkTestRejected;

            if (isMilkTestRejectedFlow)
            {
                if (delivery.Status != DeliveryStatus.Failed)
                {
                    throw new BusinessRuleException(
                        "The milk-test rejection flow requires a failed delivery.");
                }
            }
            else if (delivery.Status != DeliveryStatus.Delivered || delivery.CompletedAt is null)
            {
                throw new BusinessRuleException(
                    "Refund or replacement requests are only available after the delivery is completed.");
            }

            // One active request per delivery. The unique filtered index backs this
            // rule; the check gives a clean 409 instead of a constraint failure.
            if (await dbContext.RefundReplacementRequests.AnyAsync(
                    x => x.DeliveryId == delivery.Id
                        && (x.Status == RefundReplacementStatus.Pending
                            || x.Status == RefundReplacementStatus.Approved),
                    cancellationToken))
            {
                throw new ConflictException(
                    "An active refund or replacement request already exists for this delivery.");
            }

            var configuration = await configurationService.GetAsync(cancellationToken);
            var now = timeProvider.Now;

            DateTime? deadline = null;
            if (source == RefundReplacementSource.CustomerPostDelivery)
            {
                // Authoritative completion timestamp + configured window. The window is
                // exclusive: at the deadline the request is refused, which also keeps the
                // stored deadline strictly greater than the submission timestamp.
                var computed = delivery.CompletedAt!.Value.AddHours(configuration.WindowHours);
                if (now >= computed)
                {
                    throw new BusinessRuleException(
                        $"The refund or replacement request window of {configuration.WindowHours} hours has closed.");
                }

                deadline = computed;
            }

            var order = await dbContext.Orders
                .SingleAsync(x => x.Id == delivery.OrderId!.Value, cancellationToken);
            var customer = await dbContext.Users
                .SingleAsync(x => x.Id == delivery.CustomerId, cancellationToken);
            var branch = await dbContext.Branches
                .SingleAsync(x => x.Id == delivery.BranchId, cancellationToken);

            var entity = new RefundReplacementRequest(
                order.Id,
                delivery.Id,
                delivery.CustomerId,
                delivery.BranchId,
                rejectedMilkTest?.Id,
                request.Type,
                source,
                request.Reason,
                request.Remarks,
                now,
                deadline);

            // Allocated inside the business transaction so a rolled-back submission
            // also rolls back the counter.
            var requestNumber = await numberSeriesService.GetNextNumberAsync(
                SeriesCode,
                actor.UserId,
                cancellationToken);
            Mutate(() => entity.AssignRequestNumber(requestNumber));

            dbContext.RefundReplacementRequests.Add(entity);

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.SUBMIT",
                entity.PublicId,
                null,
                new
                {
                    entity.RequestNumber,
                    entity.OrderId,
                    entity.DeliveryId,
                    Type = entity.Type.ToString(),
                    Source = entity.Source.ToString(),
                    Status = entity.Status.ToString(),
                    entity.Deadline
                },
                request.Reason,
                now);

            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementSubmitted,
                $"refund-replacement:{entity.PublicId:N}:submitted",
                "Your refund/replacement request has been submitted for review.",
                now);

            await dbContext.SaveChangesAsync(cancellationToken);
            result = ToResult(entity, order, delivery, customer, branch, rejectedMilkTest, null, null);
        }, cancellationToken);

        return result!;
    }

    public async Task<IReadOnlyList<RefundReplacementRequestResult>> ListForCustomerAsync(
        RefundReplacementActor actor,
        CancellationToken cancellationToken)
    {
        var requests = await BaseQuery(asNoTracking: true)
            .Where(x => x.CustomerId == actor.UserId)
            .OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        // D1-D slice 6.1: same persisted-replacement enrichment as the staff
        // reads, in one batched lookup.
        var replacements = await LoadPersistedReplacementsAsync(
            requests.Select(x => x.Id).ToArray(), cancellationToken);
        return requests
            .Select(x => ToResult(
                x,
                x.Order,
                x.Delivery,
                x.Customer,
                x.Branch,
                x.MilkTest,
                x.DecidedByUser,
                x.CompletedByUser,
                replacements.TryGetValue(x.Id, out var replacement) ? replacement : null))
            .ToArray();
    }

    public async Task<RefundReplacementRequestResult?> GetForCustomerAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(
                x => x.PublicId == requestId && x.CustomerId == actor.UserId,
                cancellationToken);

        if (request is null)
        {
            return null;
        }

        // D1-D slice 6.1: the persisted replacement (if any) is resolved
        // through the stored occurrence link, never from client input.
        var replacements = await LoadPersistedReplacementsAsync(
            [request.Id], cancellationToken);
        replacements.TryGetValue(request.Id, out var replacement);
        return ToResult(
            request,
            request.Order,
            request.Delivery,
            request.Customer,
            request.Branch,
            request.MilkTest,
            request.DecidedByUser,
            request.CompletedByUser,
            replacement);
    }

    public async Task<RefundReplacementPageResult> ListForStaffAsync(
        RefundReplacementActor actor,
        RefundReplacementListQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? DefaultPageSize : Math.Min(query.PageSize, MaximumPageSize);

        IQueryable<RefundReplacementRequest> requests =
            dbContext.RefundReplacementRequests.AsNoTracking();

        if (actor.HasGlobalAccess)
        {
            if (query.BranchId.HasValue)
            {
                requests = requests.Where(x => x.BranchId == query.BranchId.Value);
            }
        }
        else
        {
            var branchIds = actor.BranchIds.ToArray();
            if (query.BranchId.HasValue)
            {
                if (!branchIds.Contains(query.BranchId.Value))
                {
                    return new RefundReplacementPageResult([], page, pageSize, 0);
                }

                requests = requests.Where(x => x.BranchId == query.BranchId.Value);
            }
            else
            {
                requests = requests.Where(x => branchIds.Contains(x.BranchId));
            }
        }

        if (query.Status.HasValue)
        {
            requests = requests.Where(x => x.Status == query.Status.Value);
        }

        var totalCount = await requests.CountAsync(cancellationToken);

        var items = await requests
            .Include(x => x.Order)
            .Include(x => x.SubscriptionDelivery)
            .Include(x => x.Delivery)
            .Include(x => x.Customer)
            .Include(x => x.Branch)
            .Include(x => x.MilkTest)
            .Include(x => x.Images)
            .OrderByDescending(x => x.SubmittedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // D1-D slice 6.1: approved re-delivery rows carry their persisted
        // replacement date on the list. One batched read-only lookup; tickets
        // without a replacement keep a null date, exactly as before.
        var replacements = await LoadPersistedReplacementsAsync(
            items.Select(x => x.Id).ToArray(), cancellationToken);
        return new RefundReplacementPageResult(
            items.Select(x => ToListItem(
                x,
                replacements.TryGetValue(x.Id, out var replacement)
                    ? replacement.ScheduledDate
                    : null)).ToArray(),
            page,
            pageSize,
            totalCount);
    }

    public async Task<RefundReplacementRequestResult> GetForStaffAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var request = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The refund/replacement request was not found.");

        EnsureStaffAccess(actor, request);

        var replacements = await LoadPersistedReplacementsAsync(
            [request.Id], cancellationToken);
        replacements.TryGetValue(request.Id, out var replacement);
        return ToResult(
            request,
            request.Order,
            request.Delivery,
            request.Customer,
            request.Branch,
            request.MilkTest,
            request.DecidedByUser,
            request.CompletedByUser,
            replacement);
    }

    /// <summary>
    /// D1-B: records the owning customer's explicit remedy choice. Same-choice
    /// retries return the current state with no side effects; a different choice
    /// after resolution, a non-awaiting ticket, or an expired window is
    /// rejected. The row-version token keeps this transition single-winner
    /// against concurrent resolutions (including the future timeout processor).
    /// </summary>
    public async Task<RefundReplacementRequestResult> ChooseAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ChooseRefundReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            // Retry safety: the SQL Server execution strategy may re-run this
            // operation after a transient failure. Discard any half-mutated
            // tracked state so every attempt reads authoritative state.
            dbContext.ChangeTracker.Clear();
            // Ownership is enforced in the predicate: another customer's ticket
            // is indistinguishable from a missing one.
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(
                    x => x.PublicId == requestId && x.CustomerId == actor.UserId,
                    cancellationToken) ?? throw new NotFoundException("The refund/replacement request was not found.");

            if (entity.CustomerChoice == request.Choice)
            {
                result = ToResult(
                    entity,
                    entity.Order,
                    entity.Delivery,
                    entity.Customer,
                    entity.Branch,
                    entity.MilkTest,
                    entity.DecidedByUser,
                    entity.CompletedByUser);
                return;
            }

            if (entity.Status != RefundReplacementStatus.AwaitingCustomerChoice)
            {
                throw new BusinessRuleException("The ticket is not awaiting customer choice.");
            }

            if (entity.CustomerChoice.HasValue)
            {
                throw new ConflictException(
                    $"A different choice ({entity.CustomerChoice}) has already been recorded and cannot be changed.");
            }

            var now = timeProvider.Now;
            if (entity.Deadline is null || now >= entity.Deadline.Value)
            {
                throw new BusinessRuleException("The customer choice window has expired.");
            }

            // D1-D slice 4: a failed replacement is refund-only. The failed
            // occurrence is identified through the ticket's persisted source
            // reference, so this covers every failure path — customer fault
            // and milk-test rejection converge on the same ticket shape.
            if (request.Choice == CustomerChoice.ReDelivery
                && entity.SubscriptionDeliveryId.HasValue
                && await IsReplacementOccurrenceAsync(
                    entity.SubscriptionDeliveryId.Value, cancellationToken))
            {
                throw new BusinessRuleException(
                    "Only a refund is available for a failed re-delivery replacement; it cannot be replaced again.");
            }

            var previousType = entity.Type;
            Mutate(() => entity.RecordCustomerChoice(request.Choice, now));

            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.CUSTOMER_CHOICE",
                entity.PublicId,
                new
                {
                    entity.RequestNumber,
                    Status = RefundReplacementStatus.AwaitingCustomerChoice.ToString(),
                    CustomerChoice = (string?)null,
                    Type = previousType.ToString()
                },
                new
                {
                    entity.RequestNumber,
                    Status = entity.Status.ToString(),
                    CustomerChoice = entity.CustomerChoice.ToString(),
                    entity.CustomerChoiceAt,
                    Type = entity.Type.ToString()
                },
                $"Customer chose {request.Choice}.",
                now);

            await AddStaffChoiceEventsAsync(entity, request.Choice, now, cancellationToken);

            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                entity.DecidedByUser,
                entity.CompletedByUser);
        }, cancellationToken);

        return result!;
    }

    public Task<RefundReplacementRequestResult> ApproveAsync(
        RefundReplacementActor actor,
        Guid requestId,
        ApproveRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, request, approve: true, cancellationToken);

    /// <summary>
    /// D1-D: advisory proposal of the next eligible replacement date. Approval
    /// re-validates (or validates a staff-edited date) inside the authoritative
    /// serializable transaction, so a stale proposal can never create a
    /// conflicting or ineligible occurrence.
    /// </summary>
    public async Task<ReplacementDateProposalResult> ProposeReplacementDateAsync(
        RefundReplacementActor actor,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var entity = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The refund/replacement request was not found.");

        EnsureStaffAccess(actor, entity);
        EnsureRedeliveryApprovable(entity, entity.Status);

        var subscription = await LoadSubscriptionForReplacementAsync(
            entity.SubscriptionDeliveryId!.Value, asNoTracking: true, cancellationToken);
        var proposed = ProposeReplacementDate(
            subscription, timeProvider.Now, await GetReplacementCutoffAsync(cancellationToken));
        return new ReplacementDateProposalResult(entity.PublicId, proposed);
    }

    public Task<RefundReplacementRequestResult> RejectAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        CancellationToken cancellationToken) =>
        DecideAsync(actor, requestId, request, approve: false, cancellationToken);

    public async Task<RefundReplacementRequestResult> CompleteAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The refund/replacement request was not found.");

            EnsureStaffAccess(actor, entity);

            // D1-D slice 3 (Rule 8): a Re-Delivery ticket is completed only by
            // its replacement delivery (same-transaction hook in the delivery
            // completion path). Manual completion is rejected until the
            // linked replacement has actually been delivered; when it already
            // has (e.g. rows delivered before that hook existed), manual
            // completion remains available as the recovery path.
            if (entity.Source == RefundReplacementSource.DeliveryFailed
                && entity.SubscriptionDeliveryId is not null
                && entity.CustomerChoice == CustomerChoice.ReDelivery)
            {
                var replacementDelivered = await dbContext.SubscriptionDeliveries.AsNoTracking()
                    .AnyAsync(
                        x => x.IsReplacement
                            && x.RefundReplacementRequestId == entity.Id
                            && x.Status == SubscriptionDeliveryStatus.Delivered,
                        cancellationToken);
                if (!replacementDelivered)
                {
                    throw new BusinessRuleException(
                        "The re-delivery ticket cannot be completed before its replacement delivery is delivered.");
                }
            }

            var now = timeProvider.Now;
            var previousStatus = entity.Status;
            // Lifecycle first: a non-Approved ticket fails here with the
            // domain status error, never with the settlement error below.
            Mutate(() => entity.Complete(actor.UserId, now, request.Remarks));

            // D1-D order-refund safety: an order-backed Refund ticket must
            // never reach Completed on the strength of the staff call alone.
            // Completion requires a pre-existing, verifiable settlement — a
            // Succeeded refund row on one of the order's payments, created by
            // the authoritative Payments refund flow. This check reuses that
            // record and creates nothing: no amount is invented, no gateway
            // is called, no wallet is credited here. Without it, completion
            // throws before SaveChanges, so the transaction rolls back and
            // the ticket stays Approved for settle-then-retry — never
            // half-completed.
            if (entity.OrderId.HasValue && entity.Type == RefundReplacementType.Refund)
            {
                var settled = await dbContext.Payments.AsNoTracking()
                    .Where(x => x.OrderId == entity.OrderId.Value
                        && x.CustomerId == entity.CustomerId
                        && x.Refunds.Any(r => r.Status == RefundStatus.Succeeded))
                    .AnyAsync(cancellationToken);
                if (!settled)
                {
                    var orderRef = entity.Order?.OrderNumber ?? entity.OrderId.Value.ToString();
                    throw new ConflictException(
                        $"The refund cannot be completed: no verified refund settlement exists for order {orderRef}. " +
                        "Settle the refund through Payments first, then retry completion.");
                }
            }

            // D1-D slice 5.2: a refund-only remedy for a failed replacement
            // carries its own money. Completing it executes the frozen D1-C
            // refund (same formula, same delivery-fail: idempotency key) and
            // resolves the original Approved ReDelivery ticket to the explicit
            // terminal Failed outcome — all inside this serializable
            // transaction, so the pair commits atomically. Any other ticket
            // shape (first failures, ReDelivery choices, order-backed
            // replacements) completes exactly as before: no money moves,
            // nothing resolves. Order-backed Refunds are gated above and never
            // reach this point without a verified Payments settlement.
            // An infeasible refund (no snapshot, no compatible payment, fully
            // refunded payment) throws before SaveChanges, so the completion
            // itself rolls back and both tickets stay Approved for repair and
            // retry — never half-completed.
            var staffRefund = await ExecuteSecondFailureRefundAsync(
                entity, actor.UserId, now, cancellationToken);
            if (staffRefund is not null)
            {
                // Best-effort like the timeout path: a missing link or an
                // already-resolved original never fails the refund itself.
                // This service has no logger; the COMPLETE audit below plus
                // the resolution audit/event record whatever happened.
                await FailedRemedyResolution.TryResolveOriginalAsync(
                    dbContext,
                    notificationEventWriter,
                    entity,
                    staffRefund.WalletCredit,
                    staffRefund.DeliveryNumber,
                    actor.UserId,
                    now,
                    cancellationToken);
            }

            var completer = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == actor.UserId, cancellationToken);
            var decider = entity.DecidedByUserId is null
                ? null
                : await dbContext.Users
                    .AsNoTracking()
                    .SingleAsync(x => x.Id == entity.DecidedByUserId.Value, cancellationToken);

            // Audit remediation A: an explicitly Refund-chosen first failure
            // pays here, at the authoritative completion point — never on
            // choice or approval. Order-backed Refunds never pay here: the
            // guard above requires a pre-existing Payments settlement and no
            // amount is invented (reported gap remains a business decision).
            var firstFailureRefund = await ExecuteFirstFailureRefundAsync(
                entity, actor.UserId, now, cancellationToken);

            object completeNewValue = (staffRefund, firstFailureRefund) switch
            {
                (not null, _) => new
                {
                    Status = entity.Status.ToString(),
                    entity.CompletedAt,
                    Resolution = "SecondFailureStaffRefund",
                    staffRefund.DeliveryNumber,
                    staffRefund.GrossRefund,
                    staffRefund.DeductionPercentage,
                    staffRefund.DeductionAmount,
                    staffRefund.WalletCredit,
                    staffRefund.SubscriptionPayable,
                    staffRefund.TotalEntitlement
                },
                (_, not null) => new
                {
                    Status = entity.Status.ToString(),
                    entity.CompletedAt,
                    Resolution = "FirstFailureStaffRefund",
                    firstFailureRefund.DeliveryNumber,
                    firstFailureRefund.GrossRefund,
                    firstFailureRefund.DeductionPercentage,
                    firstFailureRefund.DeductionAmount,
                    firstFailureRefund.WalletCredit,
                    firstFailureRefund.SubscriptionPayable,
                    firstFailureRefund.TotalEntitlement
                },
                _ => new { Status = entity.Status.ToString(), entity.CompletedAt }
            };
            AddAudit(
                actor.UserId,
                "REFUND_REPLACEMENT.COMPLETE",
                entity.PublicId,
                new { Status = previousStatus.ToString() },
                completeNewValue,
                request.Remarks,
                now);

            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementCompleted,
                $"refund-replacement:{entity.PublicId:N}:completed",
                "Your refund/replacement request has been completed.",
                now);

            await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            // D1-D slice 6.1: a completed re-delivery ticket reports the same
            // persisted replacement as the detail reads (read-only lookup;
            // refund and other shapes still resolve to null).
            var completedReplacements = await LoadPersistedReplacementsAsync(
                [entity.Id], cancellationToken);
            completedReplacements.TryGetValue(entity.Id, out var completedReplacement);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                decider,
                completer,
                completedReplacement);
        }, cancellationToken);

        return result!;
    }

    /// <summary>
    /// D1-D slice 5.2: executes the frozen delivery-fail refund for the
    /// narrow second-failure shape — an explicitly Refund-chosen, approved
    /// refund-only remedy linked to a failed replacement occurrence — being
    /// completed by staff. Returns null for every other ticket shape, leaving
    /// existing completion behavior (including first-failure and one-time
    /// refunds, which remain out-of-system) untouched.
    ///
    /// Uses the shared <see cref="DeliveryFailRefund"/> implementation, so
    /// the formula, snapshots, payment cap, wallet behavior, and the
    /// <c>delivery-fail:</c> idempotency key are identical to the automatic
    /// timeout path: whichever path runs first wins, the other reuses its
    /// records instead of duplicating them.
    /// </summary>
    private async Task<SecondFailureStaffRefund?> ExecuteSecondFailureRefundAsync(
        RefundReplacementRequest entity,
        long staffUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var occurrence = entity.SubscriptionDelivery;
        if (entity.Type != RefundReplacementType.Refund
            || entity.CustomerChoice != CustomerChoice.Refund
            || occurrence is null
            || !occurrence.IsReplacement
            || occurrence.RefundReplacementRequestId is null)
        {
            return null;
        }

        var subscription = await dbContext.Subscriptions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == occurrence.SubscriptionId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: the subscription for ticket {entity.RequestNumber} is missing. " +
                "Resolve it with staff support first.");
        var deductionPercentage = entity.DeliveryFailDeductionPercentageSnapshot
            ?? throw new ConflictException(
                $"The refund cannot be completed: ticket {entity.RequestNumber} has no frozen deduction. " +
                "Resolve it with staff support first.");
        var payment = await DeliveryFailRefund.FindPaymentAsync(
                dbContext, subscription.Id, entity.CustomerId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: no successful payment covers ticket {entity.RequestNumber}. " +
                "Resolve it with staff support first.");
        var amounts = DeliveryFailRefund.Calculate(
            subscription.PayableAmount,
            subscription.TotalEntitlement,
            deductionPercentage,
            payment.Amount,
            payment.RefundedAmount);
        if (amounts.Remaining <= 0)
        {
            throw new ConflictException(
                $"The refund cannot be completed: the payment covering ticket {entity.RequestNumber} " +
                "is already fully refunded. Resolve it with staff support first.");
        }

        var deliveryNumber = entity.Delivery.DeliveryNumber ?? entity.Delivery.ReferenceNumber;
        var final = amounts.Final;
        if (final > 0)
        {
            var idempotencyKey = DeliveryFailRefund.IdempotencyKeyFor(entity.PublicId);
            await DeliveryFailRefund.CreditWalletAsync(
                dbContext,
                entity.CustomerId,
                subscription.Id,
                payment.Id,
                paymentOptions?.Value.Currency ?? payment.Currency,
                final,
                idempotencyKey,
                $"Delivery-fail staff refund {entity.RequestNumber} for delivery {deliveryNumber}",
                now,
                cancellationToken);
            DeliveryFailRefund.RecordRefund(
                payment,
                final,
                idempotencyKey,
                "Delivery-fail staff-completed refund to wallet.",
                staffUserId,
                now);
        }

        return new SecondFailureStaffRefund(
            deliveryNumber,
            amounts.Gross,
            deductionPercentage,
            amounts.Deduction,
            final,
            subscription.PayableAmount,
            subscription.TotalEntitlement);
    }

    /// <summary>
    /// Audit remediation A: executes the frozen delivery-fail refund for an
    /// explicitly Refund-chosen first failure being completed by staff.
    /// Returns null for every other ticket shape, leaving existing behavior
    /// untouched — notably order-backed one-time deliveries (no subscription
    /// occurrence to price a share from; reported gap, never invented math)
    /// and ReDelivery choices (owned by the delivery-completion hook).
    ///
    /// Uses the shared <see cref="DeliveryFailRefund"/> implementation with
    /// the same <c>delivery-fail:</c> per-ticket idempotency key as the D1-C
    /// timeout path: whichever path runs first wins, the other observes the
    /// committed records instead of duplicating them. Infeasible refunds
    /// (no snapshot, no covering payment, exhausted payment) throw before
    /// SaveChanges, so the ticket stays Approved for repair and retry — the
    /// remedy is never marked successful without a committed refund.
    /// </summary>
    private async Task<FirstFailureStaffRefund?> ExecuteFirstFailureRefundAsync(
        RefundReplacementRequest entity,
        long staffUserId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var occurrence = entity.SubscriptionDelivery;
        if (entity.SubscriptionDeliveryId is null
            || occurrence is null
            || occurrence.IsReplacement
            || entity.Source is not (RefundReplacementSource.DeliveryFailed or RefundReplacementSource.MilkTestRejected)
            || entity.Type != RefundReplacementType.Refund
            || entity.CustomerChoice != CustomerChoice.Refund)
        {
            return null;
        }

        var subscription = await dbContext.Subscriptions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == occurrence.SubscriptionId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: the subscription for ticket {entity.RequestNumber} is missing. " +
                "Resolve it with staff support first.");
        var deductionPercentage = entity.DeliveryFailDeductionPercentageSnapshot
            ?? throw new ConflictException(
                $"The refund cannot be completed: ticket {entity.RequestNumber} has no frozen deduction. " +
                "Resolve it with staff support first.");
        // Shared lookup (latest Success/PartiallyRefunded payment), identical
        // to the timeout path; a fully refunded payment reads as no covering
        // payment, and the domain cap below stays exact.
        var payment = await DeliveryFailRefund.FindPaymentAsync(
                dbContext, subscription.Id, entity.CustomerId, cancellationToken)
            ?? throw new ConflictException(
                $"The refund cannot be completed: no successful payment covers ticket {entity.RequestNumber}. " +
                "Resolve it with staff support first.");
        var amounts = DeliveryFailRefund.Calculate(
            subscription.PayableAmount,
            subscription.TotalEntitlement,
            deductionPercentage,
            payment.Amount,
            payment.RefundedAmount);
        if (amounts.Remaining <= 0)
        {
            throw new ConflictException(
                $"The refund cannot be completed: the payment covering ticket {entity.RequestNumber} " +
                "is already fully refunded. Resolve it with staff support first.");
        }

        var deliveryNumber = entity.Delivery.DeliveryNumber ?? entity.Delivery.ReferenceNumber;
        var final = amounts.Final;
        if (final > 0)
        {
            var idempotencyKey = DeliveryFailRefund.IdempotencyKeyFor(entity.PublicId);
            await DeliveryFailRefund.CreditWalletAsync(
                dbContext,
                entity.CustomerId,
                subscription.Id,
                payment.Id,
                paymentOptions?.Value.Currency ?? payment.Currency,
                final,
                idempotencyKey,
                $"Delivery-fail staff refund {entity.RequestNumber} for delivery {deliveryNumber}",
                now,
                cancellationToken);
            DeliveryFailRefund.RecordRefund(
                payment,
                final,
                idempotencyKey,
                "Delivery-fail staff-completed refund to wallet.",
                staffUserId,
                now);
        }

        return new FirstFailureStaffRefund(
            deliveryNumber,
            amounts.Gross,
            deductionPercentage,
            amounts.Deduction,
            final,
            subscription.PayableAmount,
            subscription.TotalEntitlement);
    }

    private sealed record FirstFailureStaffRefund(
        string DeliveryNumber,
        decimal GrossRefund,
        decimal DeductionPercentage,
        decimal DeductionAmount,
        decimal WalletCredit,
        decimal SubscriptionPayable,
        int TotalEntitlement);

    private sealed record SecondFailureStaffRefund(
        string DeliveryNumber,
        decimal GrossRefund,
        decimal DeductionPercentage,
        decimal DeductionAmount,
        decimal WalletCredit,
        decimal SubscriptionPayable,
        int TotalEntitlement);

    public async Task<RefundReplacementImageResult> UploadImageAsync(
        RefundReplacementActor actor,
        Guid requestId,
        Stream content,
        string fileName,
        string contentType,
        long fileSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedContentType = ValidateImage(contentType, fileSize);
        var safeFileName = ValidateFileName(fileName);

        RefundReplacementImageResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            // Only the owning customer attaches the proof image for a normal request.
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(
                    x => x.PublicId == requestId && x.CustomerId == actor.UserId,
                    cancellationToken) ?? throw new NotFoundException("The refund/replacement request was not found.");

            if (!entity.ProofImageRequired)
            {
                throw new BusinessRuleException(
                    "A proof image is not required for a request opened from a rejected doorstep milk test.");
            }

            if (entity.Status != RefundReplacementStatus.Pending)
            {
                throw new ConflictException("A proof image can only be added while the request is pending.");
            }

            var now = timeProvider.Now;
            var extension = ImageExtension(normalizedContentType);
            var storageKey =
                $"{now:yyyy/MM}/{entity.BranchId}/{entity.PublicId:N}/{Guid.NewGuid():N}{extension}";

            var stored = await mediaStorage.SaveAsync(
                storageKey,
                content,
                normalizedContentType,
                cancellationToken);

            try
            {
                if (stored.FileSize != fileSize)
                {
                    throw new InvalidOperationException(
                        "The stored media size does not match the uploaded image size.");
                }

                var image = new RefundReplacementImage(
                    entity.Id,
                    stored.StorageKey,
                    safeFileName,
                    normalizedContentType,
                    stored.FileSize,
                    actor.UserId,
                    now);

                Mutate(() => entity.AddImage(image));

                AddAudit(
                    actor.UserId,
                    "REFUND_REPLACEMENT.IMAGE_UPLOAD",
                    entity.PublicId,
                    null,
                    new { ImageId = image.PublicId, image.FileName, image.ContentType, image.FileSize },
                    null,
                    now);

                await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
                result = ToImageResult(image);
            }
            catch
            {
                // The database write failed — remove the orphaned blob.
                await mediaStorage.DeleteIfExistsAsync(stored.StorageKey, CancellationToken.None);
                throw;
            }
        }, cancellationToken);

        return result!;
    }

    public async Task<StoredMediaContent> OpenImageAsync(
        RefundReplacementActor actor,
        Guid requestId,
        Guid imageId,
        CancellationToken cancellationToken)
    {
        var request = await BaseQuery(asNoTracking: true)
            .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
            ?? throw new NotFoundException("The refund/replacement request was not found.");

        // The owning customer or branch-scoped staff may read the private file.
        if (request.CustomerId != actor.UserId)
        {
            EnsureStaffAccess(actor, request);
        }

        var image = request.Images.FirstOrDefault(x => x.PublicId == imageId)
            ?? throw new NotFoundException("The proof image was not found.");

        var stored = await mediaStorage.OpenReadAsync(image.StorageKey, cancellationToken);
        return new StoredMediaContent(stored.Content, image.ContentType, image.FileSize);
    }

    private async Task<RefundReplacementRequestResult> DecideAsync(
        RefundReplacementActor actor,
        Guid requestId,
        DecideRefundReplacementRequest request,
        bool approve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateText(request.Remarks, "remarks", MaximumRemarksLength, required: false);

        RefundReplacementRequestResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var entity = await BaseQuery()
                .SingleOrDefaultAsync(x => x.PublicId == requestId, cancellationToken)
                ?? throw new NotFoundException("The refund/replacement request was not found.");

            EnsureStaffAccess(actor, entity);

            // Audit remediation B: a failed replacement cannot be replaced
            // again, so a ticket linked to a replacement occurrence is
            // refund-only. Rejecting it would strand the original approved
            // ReDelivery ticket (no re-replace, no timeout, no sweeper), so
            // rejection is refused here even for direct API callers; the
            // staff UI hides the action via IsRefundOnly.
            if (!approve && IsRefundOnlyRemedy(entity))
            {
                throw new BusinessRuleException(
                    "Only the refund remedy is available for a failed re-delivery replacement; " +
                    "it cannot be rejected. Approve and complete the refund instead.");
            }

            // Audit remediation B: approving a refund-only remedy without the
            // recorded Refund choice would create an Approved ticket the
            // timeout worker skips and completion cannot pay (a choice is
            // unrecordable after approval) — permanently stranded. Force the
            // choice-first order; customer abandonment still resolves through
            // the automatic timeout refund.
            if (approve && IsRefundOnlyRemedy(entity) && entity.CustomerChoice != CustomerChoice.Refund)
            {
                throw new ConflictException(
                    "A refund-only remedy can only be approved after the customer has chosen Refund.");
            }

            var now = timeProvider.Now;
            var previousStatus = entity.Status;
            SubscriptionDelivery? createdReplacement = null;
            Mutate(() =>
            {
                if (approve)
                {
                    entity.Approve(actor.UserId, now, request.Remarks);
                }
                else
                {
                    entity.Reject(actor.UserId, now, request.Remarks);
                }
            });

            if (approve && request is ApproveRefundReplacementRequest approveRequest)
            {
                createdReplacement = await CreateReplacementForApprovalAsync(
                    entity,
                    previousStatus,
                    approveRequest.ReplacementDate,
                    approveRequest.Remarks,
                    now,
                    cancellationToken);
            }

            var decider = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(x => x.Id == actor.UserId, cancellationToken);

            object newValue = createdReplacement is null
                ? new { Status = entity.Status.ToString(), entity.DecidedAt }
                : new
                {
                    Status = entity.Status.ToString(),
                    entity.DecidedAt,
                    ReplacementDate = createdReplacement.ScheduledDate,
                    ReplacementDeliveryId = createdReplacement.PublicId,
                };
            AddAudit(
                actor.UserId,
                approve ? "REFUND_REPLACEMENT.APPROVE" : "REFUND_REPLACEMENT.REJECT",
                entity.PublicId,
                new { Status = previousStatus.ToString() },
                newValue,
                request.Remarks,
                now);

            AddCustomerEvent(
                entity,
                NotificationEventTypes.RefundReplacementDecided,
                $"refund-replacement:{entity.PublicId:N}:decided:{entity.Status}",
                approve && createdReplacement is not null
                    ? $"Your replacement delivery has been approved and scheduled for {createdReplacement.ScheduledDate:yyyy-MM-dd}."
                    : approve
                        ? "Your refund/replacement request has been approved."
                        : "Your refund/replacement request has been rejected.",
                now);

            try
            {
                await SaveChangesWithConcurrencyGuardAsync(cancellationToken);
            }
            catch (DbUpdateException) when (createdReplacement is not null)
            {
                // Same-date unique-race backstop: a concurrent approval may have
                // committed an identical replacement before this transaction
                // saved. Re-read authoritatively; a now-present replacement
                // means this retry must lose as a conflict, never duplicate.
                var entityId = entity.Id;
                dbContext.ChangeTracker.Clear();
                var replacementExists = await dbContext.SubscriptionDeliveries
                    .AsNoTracking()
                    .AnyAsync(
                        x => x.IsReplacement && x.RefundReplacementRequestId == entityId,
                        cancellationToken);
                if (replacementExists)
                {
                    throw new ConflictException(
                        "The request was already approved by another operation. Refresh and try again.");
                }

                throw;
            }

            // D1-D slice 6.1: the just-created occurrence is reported through
            // the same summary shape as the persisted reads. No operational
            // delivery exists yet at approval time, so no number is exposed.
            ReplacementSummary? approvedReplacement = createdReplacement is null
                ? null
                : new ReplacementSummary(
                    createdReplacement.PublicId,
                    createdReplacement.ScheduledDate,
                    null);
            result = ToResult(
                entity,
                entity.Order,
                entity.Delivery,
                entity.Customer,
                entity.Branch,
                entity.MilkTest,
                decider,
                entity.CompletedByUser,
                approvedReplacement);
        }, cancellationToken);

        return result!;
    }

    /// <summary>
    /// D1-D: creates the approved re-delivery occurrence inside the approval
    /// transaction. Returns null for every non-re-delivery approval, leaving
    /// the existing one-time refund/replacement behavior unchanged.
    /// The replaced occurrence is always the ticket's own persisted source —
    /// client input supplies at most an optional date, never an identity.
    /// </summary>
    private async Task<SubscriptionDelivery?> CreateReplacementForApprovalAsync(
        RefundReplacementRequest entity,
        RefundReplacementStatus statusBeforeDecision,
        DateOnly? staffDate,
        string? remarks,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (entity.Source != RefundReplacementSource.DeliveryFailed
            || entity.SubscriptionDeliveryId is null
            || entity.CustomerChoice != CustomerChoice.ReDelivery)
        {
            return null;
        }

        EnsureRedeliveryApprovable(entity, statusBeforeDecision);

        var subscription = await LoadSubscriptionForReplacementAsync(
            entity.SubscriptionDeliveryId.Value, asNoTracking: false, cancellationToken);
        var source = subscription.Deliveries.Single(x => x.Id == entity.SubscriptionDeliveryId.Value);
        if (source.Status != SubscriptionDeliveryStatus.Failed)
        {
            throw new ConflictException("Only a failed delivery can be replaced.");
        }
        // D1-D slice 4 (defense in depth): even a directly recorded
        // ReDelivery choice on a replacement failure can never mint another
        // replacement — the domain creator is the single choke point and it
        // rejects replacement sources as well.
        if (source.IsReplacement)
        {
            throw new ConflictException(
                "Only a refund is available for a failed re-delivery replacement; it cannot be replaced again.");
        }

        var cutoff = await GetReplacementCutoffAsync(cancellationToken);
        var date = staffDate.HasValue
            ? ValidateStaffReplacementDate(subscription, staffDate.Value, now, cutoff)
            : ProposeReplacementDate(subscription, now, cutoff);

        SubscriptionDelivery? created = null;
        Mutate(() => created = subscription.AddReplacementDelivery(
            source, date, entity.Id, ResolveReplacementReason(remarks, entity)));
        return created;
    }

    /// <summary>
    /// D1-D: re-delivery approvals require a subscription delivery-fail ticket
    /// still awaiting choice with a recorded ReDelivery choice. The status is
    /// evaluated as of before the approval transition (approval itself moves
    /// the ticket to Approved inside the same transaction).
    /// </summary>
    private static void EnsureRedeliveryApprovable(
        RefundReplacementRequest entity,
        RefundReplacementStatus statusBeforeDecision)
    {
        if (entity.Source != RefundReplacementSource.DeliveryFailed
            || entity.SubscriptionDeliveryId is null
            || entity.CustomerChoice != CustomerChoice.ReDelivery)
        {
            throw new ConflictException(
                "Only a subscription delivery-fail ticket with a recorded re-delivery choice can create a replacement.");
        }

        if (statusBeforeDecision != RefundReplacementStatus.AwaitingCustomerChoice)
        {
            throw new ConflictException(
                "A re-delivery can only be approved while the ticket awaits customer choice.");
        }
    }

    /// <summary>
    /// Audit remediation B: a ticket is refund-only when its failed
    /// occurrence is itself a re-delivery replacement. Read-only, derived
    /// strictly from the persisted ticket-to-occurrence relationship.
    /// </summary>
    private static bool IsRefundOnlyRemedy(RefundReplacementRequest entity) =>
        entity.SubscriptionDelivery?.IsReplacement == true;

    /// <summary>
    /// D1-D slice 4: identifies a failed replacement through the persisted
    /// occurrence flag. Read-only and transaction-safe: the occurrence row is
    /// never modified by choice recording.
    /// </summary>
    private async Task<bool> IsReplacementOccurrenceAsync(
        long subscriptionDeliveryId,
        CancellationToken cancellationToken) =>
        await dbContext.SubscriptionDeliveries.AsNoTracking()
            .AnyAsync(x => x.Id == subscriptionDeliveryId && x.IsReplacement, cancellationToken);

    private async Task<Subscription> LoadSubscriptionForReplacementAsync(
        long sourceDeliveryId,
        bool asNoTracking,
        CancellationToken cancellationToken)
    {
        IQueryable<Subscription> query = dbContext.Subscriptions
            .Include(x => x.Deliveries)
            .Include(x => x.Schedules)
            .Include(x => x.Branch);
        if (asNoTracking)
        {
            query = query.AsNoTracking();
        }

        return await query.SingleOrDefaultAsync(
                x => x.Deliveries.Any(d => d.Id == sourceDeliveryId),
                cancellationToken)
            ?? throw new ConflictException("The failed delivery no longer belongs to a subscription.");
    }

    /// <summary>
    /// D1-D: deterministic proposal — the first date after the approval day
    /// satisfying weekday schedule (when schedules exist), cutoff, active
    /// branch, and no date conflict. May fall after <c>EndDate</c> (the term
    /// itself is never extended). Bounded to one year, failing closed.
    /// D1-D slice 2.1: there is no vacation-range entity — persisted
    /// vacation/skip state is a <c>Skipped</c> occurrence row — so the date
    /// conflict exclusion is the enforceable vacation/skip rule here: a date
    /// already occupied (including by a vacation/skipped occurrence) is never
    /// proposed. The replacement itself is always created <c>Scheduled</c>,
    /// and ordinary skip/vacation of a replacement is rejected elsewhere,
    /// so it can never be silently skipped.
    /// </summary>
    private static DateOnly ProposeReplacementDate(
        Subscription subscription,
        DateTime now,
        TimeSpan cutoff)
    {
        const int horizonDays = 365;
        var weekdays = Weekdays(subscription);
        var start = DateOnly.FromDateTime(now).AddDays(1);
        for (var offset = 0; offset < horizonDays; offset++)
        {
            var candidate = start.AddDays(offset);
            if (IsEligibleReplacementDate(subscription, candidate, now, cutoff, weekdays))
            {
                return candidate;
            }
        }

        throw new ConflictException("No eligible replacement date exists within a year of approval.");
    }

    private static DateOnly ValidateStaffReplacementDate(
        Subscription subscription,
        DateOnly staffDate,
        DateTime now,
        TimeSpan cutoff)
    {
        // D1-D slice 2.1: evaluated inside the authoritative serializable
        // approval transaction, so a vacation/skip committed after the
        // advisory proposal is still caught here — never silently created.
        var occupant = subscription.Deliveries.FirstOrDefault(x => x.ScheduledDate == staffDate);
        if (occupant is not null)
        {
            if (occupant.Status == SubscriptionDeliveryStatus.Skipped)
            {
                throw new ValidationAppException(
                    "The replacement date falls on a vacation or skipped delivery. Request a new proposal and approve an alternative eligible date.",
                    "replacementDate");
            }

            throw new ValidationAppException(
                "The replacement date conflicts with an existing delivery.", "replacementDate");
        }

        var weekdays = Weekdays(subscription);
        if (weekdays.Count > 0 && !weekdays.Contains(staffDate.DayOfWeek))
        {
            throw new ValidationAppException(
                "The replacement date must fall on a scheduled delivery weekday.", "replacementDate");
        }

        if (!subscription.Branch.IsActive)
        {
            throw new ValidationAppException(
                "The branch is not active for the replacement date.", "replacementDate");
        }

        if (now > DeliveryStart(staffDate) - cutoff)
        {
            throw new ValidationAppException(
                "The replacement cutoff has passed for this date.", "replacementDate");
        }

        return staffDate;
    }

    private static bool IsEligibleReplacementDate(
        Subscription subscription,
        DateOnly candidate,
        DateTime now,
        TimeSpan cutoff,
        HashSet<DayOfWeek> weekdays) =>
        !subscription.Deliveries.Any(x => x.ScheduledDate == candidate)
        && (weekdays.Count == 0 || weekdays.Contains(candidate.DayOfWeek))
        && subscription.Branch.IsActive
        && now <= DeliveryStart(candidate) - cutoff;

    private static HashSet<DayOfWeek> Weekdays(Subscription subscription) =>
        subscription.Schedules.Select(x => x.DayOfWeek).ToHashSet();

    private static DateTime DeliveryStart(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);

    /// <summary>
    /// D1-D: reuses the existing skip/pause cutoff configuration
    /// (<c>Subscription.SkipPauseCutoffHours</c>, default 24h). No new
    /// per-day capacity model is introduced.
    /// </summary>
    private async Task<TimeSpan> GetReplacementCutoffAsync(CancellationToken cancellationToken)
    {
        var configuration = await dbContext.SystemConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == "Subscription.SkipPauseCutoffHours", cancellationToken);
        if (configuration is null
            || !double.TryParse(configuration.Value, out var hours)
            || hours < 0)
        {
            return TimeSpan.FromHours(24);
        }

        return TimeSpan.FromHours(hours);
    }

    private static string? ResolveReplacementReason(string? remarks, RefundReplacementRequest entity)
    {
        var trimmed = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        if (trimmed is not null
            && trimmed.Length > SubscriptionDelivery.MaximumReplacementReasonLength)
        {
            throw new ValidationAppException(
                $"Remarks cannot exceed {SubscriptionDelivery.MaximumReplacementReasonLength} characters when approving a re-delivery.",
                "remarks");
        }

        return trimmed ?? entity.FailureReasonSnapshot;
    }

    private IQueryable<RefundReplacementRequest> BaseQuery(bool asNoTracking = false)
    {
        IQueryable<RefundReplacementRequest> query = dbContext.RefundReplacementRequests
            .Include(x => x.Order)
            .Include(x => x.SubscriptionDelivery)
            .Include(x => x.Delivery)
            .Include(x => x.Customer)
            .Include(x => x.Branch)
            .Include(x => x.MilkTest)
            .Include(x => x.DecidedByUser)
            .Include(x => x.CompletedByUser)
            .Include(x => x.Images);

        return asNoTracking ? query.AsNoTracking() : query;
    }

    private static void EnsureStaffAccess(RefundReplacementActor actor, RefundReplacementRequest request)
    {
        if (!actor.HasGlobalAccess && !actor.BranchIds.Contains(request.BranchId))
        {
            // Out-of-scope requests are reported as missing, never as forbidden.
            throw new NotFoundException("The refund/replacement request was not found.");
        }
    }

    private void AddCustomerEvent(
        RefundReplacementRequest request,
        string eventType,
        string eventKey,
        string message,
        DateTime occurredAt)
    {
        var variables = new Dictionary<string, string>
        {
            ["requestId"] = request.PublicId.ToString(),
            ["requestNumber"] = request.RequestNumber,
            ["type"] = request.Type.ToString(),
            ["status"] = request.Status.ToString(),
            ["message"] = message
        };

        notificationEventWriter.Add(new NotificationEventRequest(
            request.CustomerId,
            eventType,
            eventKey,
            variables,
            $"/refund-replacements/{request.PublicId}",
            occurredAt));
    }

    private void AddAudit(
        long userId,
        string action,
        Guid requestId,
        object? oldValue,
        object? newValue,
        string? reason,
        DateTime createdAt) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            EntityType,
            requestId.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            newValue is null ? null : JsonSerializer.Serialize(newValue),
            null,
            null,
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            createdAt));

    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
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

    private static string ValidateImage(string contentType, long fileSize) =>
        RefundReplacementProofImages.ValidateContent(contentType, fileSize);

    private static string ValidateFileName(string fileName) =>
        RefundReplacementProofImages.ValidateFileName(fileName);

    private static string ImageExtension(string contentType) =>
        RefundReplacementProofImages.Extension(contentType);

    private static void ValidateText(string? value, string field, int maximumLength, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException($"{field} is required.", field);
        }

        if (value?.Trim().Length > maximumLength)
        {
            throw new ValidationAppException(
                $"{field} cannot exceed {maximumLength} characters.",
                field);
        }
    }

    /// <summary>
    /// D1-B: fans the recorded choice out to branch Admin / Dairy Manager /
    /// Support staff (global assignments included). One deterministic event per
    /// recipient keeps retries free of duplicate notifications.
    /// </summary>
    private async Task AddStaffChoiceEventsAsync(
        RefundReplacementRequest request,
        CustomerChoice choice,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        var staffRoleCodes = new[]
        {
            AuthorizationCodes.Owner,
            AuthorizationCodes.SystemAdmin,
            AuthorizationCodes.DairyManager,
            AuthorizationCodes.CustomerSupport
        };
        var recipientIds = await dbContext.UserRoles
            .AsNoTracking()
            .Where(x => (x.BranchId == request.BranchId || x.BranchId == null)
                && staffRoleCodes.Contains(x.Role.Code)
                && x.Role.IsActive
                && x.User.IsActive)
            .Select(x => x.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var deliveryNumber = request.Delivery.DeliveryNumber ?? request.Delivery.ReferenceNumber;
        foreach (var userId in recipientIds)
        {
            notificationEventWriter.Add(new NotificationEventRequest(
                userId,
                NotificationEventTypes.RefundReplacementChoiceRecorded,
                $"refund-replacement:{request.PublicId:N}:choice:{choice}:staff:{userId}",
                new Dictionary<string, string>
                {
                    ["requestId"] = request.PublicId.ToString(),
                    ["requestNumber"] = request.RequestNumber,
                    ["deliveryNumber"] = deliveryNumber,
                    ["choice"] = choice.ToString(),
                    ["customerName"] = request.Customer.DisplayName ?? request.Delivery.CustomerNameSnapshot,
                    ["message"] = $"Customer chose {choice} for delivery {deliveryNumber} ({request.RequestNumber})."
                },
                $"/staff/refund-replacements/{request.PublicId}",
                occurredAt));
        }
    }

    private async Task SaveChangesWithConcurrencyGuardAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConflictException(
                $"The request was changed by another operation. Refresh and try again. ({exception.GetType().Name})");
        }
    }

    /// <summary>
    /// D1-D slice 6.1: the persisted replacement routed from an approved
    /// re-delivery ticket, resolved through the stored
    /// <c>SubscriptionDelivery.RefundReplacementRequestId</c> link — never
    /// from client input and never inferred from the original failed
    /// occurrence. <c>DeliveryNumber</c> is the operational delivery's public
    /// DELIVERY-series reference when the replacement has materialized, else
    /// null (never invented).
    /// </summary>
    private sealed record ReplacementSummary(
        Guid OccurrencePublicId,
        DateOnly ScheduledDate,
        string? DeliveryNumber);

    /// <summary>
    /// D1-D slice 6.1: batch-resolves persisted replacements for the given
    /// tickets in one read-only round trip (occurrence rows plus their
    /// operational delivery numbers via the unique
    /// <c>Delivery.SubscriptionDeliveryId</c> link). Tickets without an
    /// approved re-delivery simply have no entry.
    /// </summary>
    private async Task<Dictionary<long, ReplacementSummary>> LoadPersistedReplacementsAsync(
        IReadOnlyCollection<long> ticketIds,
        CancellationToken cancellationToken)
    {
        if (ticketIds.Count == 0)
        {
            return new Dictionary<long, ReplacementSummary>();
        }

        var rows = await dbContext.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement
                && x.RefundReplacementRequestId != null
                && ticketIds.Contains(x.RefundReplacementRequestId.Value))
            .Select(x => new
            {
                TicketId = x.RefundReplacementRequestId!.Value,
                x.PublicId,
                x.ScheduledDate,
                DeliveryNumber = dbContext.Deliveries
                    .Where(d => d.SubscriptionDeliveryId == x.Id)
                    .Select(d => d.DeliveryNumber)
                    .SingleOrDefault()
            })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(
            x => x.TicketId,
            x => new ReplacementSummary(x.PublicId, x.ScheduledDate, x.DeliveryNumber));
    }

    private static RefundReplacementRequestResult ToResult(
        RefundReplacementRequest request,
        Order? order,
        Delivery delivery,
        User customer,
        Branch branch,
        MilkTest? milkTest,
        User? decidedBy,
        User? completedBy,
        ReplacementSummary? replacement = null) => new(
        request.PublicId,
        request.RequestNumber,
        request.OrderId,
        order?.PublicId,
        order?.OrderNumber,
        request.SubscriptionDelivery?.PublicId,
        request.FailedAt,
        request.DeliveryFailWindowHoursSnapshot,
        request.DeliveryFailDeductionPercentageSnapshot,
        request.FailureReasonSnapshot,
        delivery.PublicId,
        delivery.DeliveryNumber,
        request.CustomerId,
        string.IsNullOrWhiteSpace(customer.DisplayName)
            ? delivery.CustomerNameSnapshot
            : customer.DisplayName!,
        string.IsNullOrWhiteSpace(customer.Mobile)
            ? delivery.CustomerMobileSnapshot
            : customer.Mobile!,
        request.BranchId,
        branch.Code,
        branch.Name,
        request.MilkTestId,
        milkTest?.PublicId,
        request.Type,
        request.Source,
        request.Status,
        request.CustomerChoice,
        request.CustomerChoiceAt,
        request.Reason,
        request.Remarks,
        request.SubmittedAt,
        request.Deadline,
        request.DecidedByUserId,
        decidedBy?.DisplayName,
        request.DecidedAt,
        request.DecisionRemarks,
        request.CompletedByUserId,
        completedBy?.DisplayName,
        request.CompletedAt,
        request.CompletionRemarks,
        request.ProofImageRequired,
        request.Images
            .OrderBy(x => x.UploadedAt)
            .Select(ToImageResult)
            .ToArray(),
        replacement?.OccurrencePublicId,
        replacement?.ScheduledDate,
        replacement?.DeliveryNumber,
        request.SubscriptionDelivery?.IsReplacement == true);

    private static RefundReplacementListItem ToListItem(
        RefundReplacementRequest request,
        DateOnly? replacementDate = null) => new(
        request.PublicId,
        request.RequestNumber,
        request.OrderId,
        request.Order?.PublicId,
        request.Order?.OrderNumber,
        request.SubscriptionDelivery?.PublicId,
        request.FailedAt,
        request.DeliveryFailWindowHoursSnapshot,
        request.DeliveryFailDeductionPercentageSnapshot,
        request.FailureReasonSnapshot,
        request.Delivery.PublicId,
        request.Delivery.DeliveryNumber,
        request.CustomerId,
        string.IsNullOrWhiteSpace(request.Customer.DisplayName)
            ? request.Delivery.CustomerNameSnapshot
            : request.Customer.DisplayName!,
        string.IsNullOrWhiteSpace(request.Customer.Mobile)
            ? request.Delivery.CustomerMobileSnapshot
            : request.Customer.Mobile!,
        request.BranchId,
        request.Branch.Code,
        request.Branch.Name,
        request.MilkTestId,
        request.MilkTest?.PublicId,
        request.Type,
        request.Source,
        request.Status,
        request.CustomerChoice,
        request.CustomerChoiceAt,
        request.Reason,
        request.Remarks,
        request.SubmittedAt,
        request.Deadline,
        request.ProofImageRequired,
        request.Images.Count,
        replacementDate,
        request.SubscriptionDelivery?.IsReplacement == true);

    private static RefundReplacementImageResult ToImageResult(RefundReplacementImage image) => new(
        image.PublicId,
        image.FileName,
        image.ContentType,
        image.FileSize,
        image.UploadedAt);
}
