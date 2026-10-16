using System.Globalization;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Subscriptions;

public sealed class SubscriptionService(
    DoodhDirectDbContext dbContext,
    IBranchAllocationService branchAllocationService,
    IPaymentService paymentService,
    IIndiaTimeProvider timeProvider,
    INotificationEventWriter notificationEventWriter) : ISubscriptionService
{
    private const string CutoffConfigurationKey = "Subscription.SkipPauseCutoffHours";
    private const int DefaultCutoffHours = 24;

    public async Task<CreatedSubscriptionResult> CreateAsync(
        long customerId,
        CreateSubscriptionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        ValidateRequest(request);

        var normalizedKey = idempotencyKey.Trim();
        var existing = await Query()
            .SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (existing is not null)
        {
            return await CompleteCreationAsync(
                customerId, existing, request, normalizedKey, cancellationToken);
        }

        Subscription? subscription = null;
        var createdEventKey = string.Empty;
        var paymentPendingEventKey = string.Empty;
        try
        {
            // Authoritative creation runs inside the existing SERIALIZABLE
            // transaction: product/branch reads, charge-master resolution,
            // monetary construction, charge snapshots, MarkUsed and the single
            // SaveChanges commit atomically. Payment creation stays outside
            // (external gateway I/O must never hold a database transaction).
            subscription = await ExecuteSerializableAsync(async () =>
            {
                var address = await dbContext.CustomerAddresses
                    .SingleOrDefaultAsync(x => x.PublicId == request.AddressId && x.UserId == customerId && x.IsActive, cancellationToken)
                    ?? throw new NotFoundException("The selected address was not found or is inactive.");
                var product = await dbContext.Products
                    .Include(x => x.ProductBranches)
                    .SingleOrDefaultAsync(x => x.PublicId == request.ProductId && x.IsActive, cancellationToken)
                    ?? throw new NotFoundException("The selected product was not found or is inactive.");

                var allocation = await branchAllocationService.AllocateAsync(
                    address.Latitude,
                    address.Longitude,
                    [(product.Id, request.Quantity)],
                    cancellationToken);
                var branch = await dbContext.Branches
                    .SingleAsync(x => x.Id == allocation.BranchId, cancellationToken);

                var dates = GenerateDates(request.StartDate, request.DeliveryDays, request.TotalEntitlement);
                var created = new Subscription(
                    customerId,
                    product.Id,
                    address.Id,
                    branch.Id,
                    normalizedKey,
                    request.StartDate,
                    dates[^1],
                    request.Quantity,
                    product.Price,
                    request.TotalEntitlement,
                    product.Sku,
                    product.Name,
                    product.UnitOfMeasure,
                    branch.Code,
                    branch.Name,
                    FormatAddress(address));

                foreach (var day in request.DeliveryDays.Distinct())
                {
                    created.AddSchedule(day, request.Slot);
                }
                foreach (var date in dates)
                {
                    created.AddDelivery(date, request.Slot);
                }

                // Stage 3: charges apply ONCE to the complete prepaid product
                // value. PayableAmount still equals the constructor-established
                // product value here (AddCharges has not run), so it is the
                // frozen base — never a second live Product.Price read.
                var (charges, chargeMasters) = await ResolveSubscriptionChargesAsync(
                    product.Id,
                    created.PayableAmount,
                    cancellationToken);
                created.AddCharges(charges);
                foreach (var master in chargeMasters)
                {
                    if (!master.IsUsed)
                    {
                        master.MarkUsed();
                    }
                }

                dbContext.Subscriptions.Add(created);
                createdEventKey = $"subscription:{created.PublicId:N}:created";
                paymentPendingEventKey = $"subscription:{created.PublicId:N}:payment-pending";
                notificationEventWriter.Add(new NotificationEventRequest(
                    customerId,
                    NotificationEventTypes.SubscriptionCreated,
                    createdEventKey,
                    new Dictionary<string, string>
                    {
                        ["message"] = $"Your {created.ProductNameSnapshot} subscription has been created.",
                        ["subscriptionId"] = created.PublicId.ToString()
                    },
                    $"/subscriptions/{created.PublicId}"));
                notificationEventWriter.Add(new NotificationEventRequest(
                    customerId,
                    NotificationEventTypes.SubscriptionPaymentPending,
                    paymentPendingEventKey,
                    new Dictionary<string, string>
                    {
                        ["amount"] = created.PayableAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                        ["currency"] = "INR",
                        ["message"] = "Payment is pending for your subscription.",
                        ["subscriptionId"] = created.PublicId.ToString()
                    },
                    $"/subscriptions/{created.PublicId}"));
                // Captured for the DbUpdateException fallback below: if the
                // save fails, the failed graph must be detached before the
                // winning-row reload (mirrors the previous structure).
                subscription = created;
                await dbContext.SaveChangesAsync(cancellationToken);
                return created;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (subscription is not null)
            {
                foreach (var schedule in subscription.Schedules)
                {
                    dbContext.Entry(schedule).State = EntityState.Detached;
                }
                foreach (var delivery in subscription.Deliveries)
                {
                    dbContext.Entry(delivery).State = EntityState.Detached;
                }
                foreach (var charge in subscription.Charges)
                {
                    dbContext.Entry(charge).State = EntityState.Detached;
                }
                dbContext.Entry(subscription).State = EntityState.Detached;
            }
            foreach (var eventKey in new[] { createdEventKey, paymentPendingEventKey })
            {
                var notificationEvent = dbContext.NotificationEvents.Local
                    .SingleOrDefault(item => item.EventKey == eventKey);
                if (notificationEvent is not null)
                {
                    dbContext.Entry(notificationEvent).State = EntityState.Detached;
                }
            }
            var duplicate = await Query()
                .SingleOrDefaultAsync(x => x.CustomerId == customerId && x.IdempotencyKey == normalizedKey, cancellationToken);
            if (duplicate is not null)
            {
                return await CompleteCreationAsync(
                    customerId, duplicate, request, normalizedKey, cancellationToken);
            }
            throw;
        }

        await LoadNavigationAsync(subscription!, cancellationToken);
        return await CompleteCreationAsync(
            customerId, subscription!, request, normalizedKey, cancellationToken);
    }

    public async Task<IReadOnlyList<SubscriptionResult>> GetForCustomerAsync(long customerId, CancellationToken cancellationToken) =>
        (await Query()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken))
        .Select(x => x.ToResult())
        .ToArray();

    public async Task<SubscriptionResult> GetAsync(long customerId, Guid subscriptionId, CancellationToken cancellationToken) =>
        (await FindOwnedAsync(customerId, subscriptionId, cancellationToken)).ToResult();

    public async Task<CreatedSubscriptionResult> RetryPaymentAsync(
        long customerId,
        Guid subscriptionId,
        RetrySubscriptionPaymentRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var payment = await paymentService.RetrySubscriptionAsync(
            customerId,
            subscriptionId,
            request.PaymentMethod,
            idempotencyKey,
            cancellationToken);
        var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
        return new CreatedSubscriptionResult(subscription.ToResult(), payment);
    }

    public async Task<SubscriptionResult> UpdateAsync(
        long customerId,
        Guid subscriptionId,
        UpdateSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
        if (subscription.Status is not (SubscriptionStatus.PaymentPending or SubscriptionStatus.Paused))
        {
            throw new BusinessRuleException("Only payment-pending or paused subscriptions can be updated.");
        }
        if (request.Quantity.HasValue && request.Quantity.Value != subscription.Quantity)
        {
            throw new BusinessRuleException("Quantity cannot be changed after subscription creation.");
        }
        if (request.AddressId.HasValue && request.AddressId.Value != subscription.CustomerAddress.PublicId)
        {
            throw new BusinessRuleException("Address cannot be changed after subscription creation.");
        }
        if (request.DeliveryDays is not null || request.Slot.HasValue)
        {
            if (request.DeliveryDays is not null)
            {
                ValidateDays(request.DeliveryDays);
            }
            throw new BusinessRuleException("Delivery schedule changes are not supported after occurrences are generated.");
        }

        return subscription.ToResult();
    }

    public async Task<SubscriptionResult> PauseAsync(long customerId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
        await EnsureCutoffAsync(subscription, cancellationToken);
        subscription.Pause(timeProvider.Now);
        notificationEventWriter.Add(new NotificationEventRequest(
            customerId,
            NotificationEventTypes.SubscriptionPaused,
            $"subscription:{subscription.PublicId:N}:paused:{subscription.PausedAt!.Value.Ticks}",
            new Dictionary<string, string>
            {
                ["message"] = $"Your {subscription.ProductNameSnapshot} subscription has been paused.",
                ["subscriptionId"] = subscription.PublicId.ToString()
            },
            $"/subscriptions/{subscription.PublicId}",
            subscription.PausedAt));
        await dbContext.SaveChangesAsync(cancellationToken);
        return subscription.ToResult();
    }

    public async Task<SubscriptionResult> ResumeAsync(long customerId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
        var pausedAt = subscription.PausedAt
            ?? throw new BusinessRuleException("Only a paused subscription can be resumed.");
        subscription.Resume();
        notificationEventWriter.Add(new NotificationEventRequest(
            customerId,
            NotificationEventTypes.SubscriptionResumed,
            $"subscription:{subscription.PublicId:N}:resumed:{pausedAt.Ticks}",
            new Dictionary<string, string>
            {
                ["message"] = $"Your {subscription.ProductNameSnapshot} subscription has resumed.",
                ["subscriptionId"] = subscription.PublicId.ToString()
            },
            $"/subscriptions/{subscription.PublicId}"));
        await dbContext.SaveChangesAsync(cancellationToken);
        return subscription.ToResult();
    }

    /// <summary>
    /// D1-D slice 5: cancellation with a pending re-delivery replacement.
    ///
    /// The whole cancellation — subscription + occurrence flips, operational
    /// voids, wallet/refund ledger rows, ticket resolutions, audits and outbox
    /// events — commits atomically inside one serializable transaction, so a
    /// crash or race can only leave "nothing happened" (safe to retry) or
    /// "everything happened" (idempotent skip on retry).
    ///
    /// State-by-state behavior for approved ReDelivery tickets linked to this
    /// subscription:
    /// <list type="bullet">
    /// <item>Replacement <c>Scheduled</c> (or already <c>Cancelled</c> by a
    /// prior cancel), operational delivery absent or pre-pickup
    /// (<c>ReadyForAssignment</c>/<c>Assigned</c>): the operational row is
    /// voided to the terminal <c>Cancelled</c> state (never via
    /// <c>Delivery.Fail</c> — no failure ticket), OTPs die with it, the frozen
    /// one-share entitlement (ticket deduction snapshot) is credited to the
    /// wallet under a <c>cancel-redelivery:</c> idempotency namespace, and the
    /// ticket is completed with an explicit cancellation outcome.</item>
    /// <item>Operational delivery <c>PickedUp</c>/<c>OutForDelivery</c>/
    /// <c>Arrived</c>: the cancellation is rejected with a <c>Conflict</c>
    /// before any state or financial mutation; the in-flight delivery must be
    /// operationally resolved first. Nothing is half-cancelled.</item>
    /// <item>Replacement <c>Delivered</c>: owned by the Slice-3 completion
    /// path (ticket already <c>Completed</c>) — untouched, no second refund.</item>
    /// <item>Replacement <c>Failed</c>: owned by the Slice-4 refund-only remedy
    /// path — untouched here so the same share can never refund twice.</item>
    /// <item>Ticket still awaiting customer choice: not an approved
    /// replacement — untouched; the D1-C timeout worker still refunds it at
    /// its persisted deadline.</item>
    /// </list>
    /// Ordinary future occurrences keep the existing policy: no general
    /// cancellation refund exists, and only safely-voided approved replacements
    /// are refunded (each resolved separately under the payment cap).
    /// </summary>
    public async Task<SubscriptionResult> CancelAsync(long customerId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteSerializableAsync(async () =>
            {
            // Fresh read inside the serializable transaction: candidate
            // detection, the in-transit blocker, voids, refunds and ticket
            // resolutions all contend on committed state only.
            var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
            var now = timeProvider.Now;

            // Unchanged domain transition (same throw behavior for
            // Completed/PaymentFailed): flips every Scheduled occurrence —
            // including pending replacements — to Cancelled.
            subscription.Cancel(now);

            var candidates = await FindPendingRedeliveryCandidatesAsync(subscription.Id, cancellationToken);

            // In-transit blocker before any refund or void work: an in-flight
            // replacement stays deliverable, so the subscription must not be
            // half-cancelled around it. Throwing before SaveChanges rolls back
            // the in-memory cancel above as well.
            foreach (var candidate in candidates)
            {
                var operational = candidate.Operational;
                if (operational?.Status is DeliveryStatus.PickedUp or DeliveryStatus.OutForDelivery or DeliveryStatus.Arrived)
                {
                    var number = operational.DeliveryNumber ?? operational.ReferenceNumber;
                    throw new ConflictException(
                        $"The subscription cannot be cancelled while replacement delivery {number} is {operational.Status}. " +
                        "The active replacement delivery must be operationally resolved first.");
                }
            }

            foreach (var candidate in candidates)
            {
                await ResolvePendingReplacementAsync(customerId, subscription, candidate, now, cancellationToken);
            }

            await InvalidateMaterializedOtpsAsync(subscription.Deliveries.Select(x => x.Id), now, cancellationToken);

            SubscriptionResult? result;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                result = subscription.ToResult();
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new ConflictException(
                    $"The subscription was changed by another operation. Refresh and try again. ({exception.GetType().Name})");
            }
            catch (DbUpdateException)
            {
                // A concurrent winner committed first (ticket row-version, or
                // the wallet/refund/event idempotency keys, all namespaced to
                // this path): re-read, and if no candidate is still pending
                // the other instance won — adopt its outcome instead of failing.
                // A retry is safe either way (completed tickets are skipped).
                // A deadlock victim must NOT adopt: SQL Server aborts the
                // whole transaction, so the adoption reads fail and the
                // original error is rethrown for the execution strategy to
                // retry cleanly (the retry then observes the resolved state).
                dbContext.ChangeTracker.Clear();
                IReadOnlyList<PendingRedeliveryCandidate>? stillPending = null;
                var adoptionReadable = false;
                try
                {
                    stillPending = await FindPendingRedeliveryCandidatesAsync(subscription.Id, cancellationToken);
                    adoptionReadable = true;
                }
                catch (Exception)
                {
                    adoptionReadable = false;
                }
                if (!adoptionReadable)
                {
                    // Rethrows the original DbUpdateException: a deadlock
                    // victim is transient, so the execution strategy retries
                    // it in a fresh transaction.
                    throw;
                }
                if (stillPending!.Count == 0)
                {
                    var current = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
                    return current.ToResult();
                }

                throw;
            }

            return result;
            }, cancellationToken);
        }
        catch (InvalidOperationException exception) when (IsCompletedTransactionError(exception))
        {
            // Commit-phase server abort (a serializable deadlock decided at
            // commit surfaces as a bare completed-transaction error, hiding
            // the transient cause from the execution strategy): this attempt
            // committed nothing. Re-read authoritatively outside the dead
            // transaction — if another attempt won, adopt its outcome so a
            // concurrent double-cancel stays idempotent; otherwise surface a
            // retryable conflict. Never masks domain errors: their messages
            // never match the completed-transaction guard below.
            dbContext.ChangeTracker.Clear();
            var current = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
            var stillPending = await FindPendingRedeliveryCandidatesAsync(current.Id, cancellationToken);
            if (stillPending.Count == 0)
            {
                return current.ToResult();
            }

            throw new ConflictException(
                "The subscription was changed by another operation. Refresh and try again.");
        }
    }

    private static bool IsCompletedTransactionError(InvalidOperationException exception) =>
        exception.Message.Contains("has completed", StringComparison.OrdinalIgnoreCase)
        && exception.Message.Contains("no longer usable", StringComparison.OrdinalIgnoreCase);

    public Task<SubscriptionDeliveryResult> SkipAsync(
        long customerId,
        Guid subscriptionId,
        SkipSubscriptionDeliveryRequest request,
        CancellationToken cancellationToken) =>
        ExecuteSerializableAsync(async () =>
        {
            var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
            var delivery = subscription.Deliveries.SingleOrDefault(x => x.PublicId == request.DeliveryId)
                ?? throw new NotFoundException("The subscription delivery was not found.");
            // D1-D slice 2.1 (Rule 7): a re-delivery replacement must never be
            // silently consumed by an ordinary skip. The domain guard below is
            // the backstop; this explicit check returns the clear business
            // error before any materialization or cutoff evaluation.
            if (delivery.IsReplacement)
            {
                throw new BusinessRuleException(
                    "A re-delivery replacement cannot be skipped as vacation; it requires an explicit operational decision.");
            }
            var now = timeProvider.Now;
            await EnsureNoMaterializedDeliveryAsync(delivery, cancellationToken);
            subscription.Skip(delivery, now, await GetCutoffAsync(cancellationToken));
            AddSkipAudit(customerId, subscription, delivery, now);
            notificationEventWriter.Add(new NotificationEventRequest(
                customerId,
                NotificationEventTypes.SubscriptionSkipped,
                $"subscription-delivery:{delivery.PublicId:N}:skipped",
                new Dictionary<string, string>
                {
                    ["date"] = delivery.ScheduledDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["message"] = $"Your delivery for {delivery.ScheduledDate:dd MMM yyyy} has been skipped.",
                    ["subscriptionId"] = subscription.PublicId.ToString()
                },
                $"/subscriptions/{subscription.PublicId}",
                delivery.StatusChangedAt));
            await InvalidateMaterializedOtpsAsync([delivery.Id], now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await LoadDeliveryNavigationAsync(delivery, cancellationToken);
            return delivery.ToResult();
        }, cancellationToken);

    public Task<VacationResult> CreateVacationAsync(
        long customerId,
        CreateVacationRequest request,
        CancellationToken cancellationToken)
    {
        ValidateVacationRange(request);
        return ExecuteSerializableAsync(async () =>
        {
            var now = timeProvider.Now;
            var cutoff = await GetCutoffAsync(cancellationToken);

            // Fresh read inside the serializable transaction: eligibility is
            // evaluated against committed state only.
            var subscriptions = await dbContext.Subscriptions
                .Include(x => x.Product)
                .Include(x => x.Deliveries)
                .Where(x => x.CustomerId == customerId &&
                    (x.Status == SubscriptionStatus.Active || x.Status == SubscriptionStatus.Paused))
                .OrderBy(x => x.Id)
                .ToListAsync(cancellationToken);

            var occurrences = subscriptions
                .SelectMany(x => x.Deliveries)
                .Where(x => x.ScheduledDate >= request.FromDate && x.ScheduledDate <= request.ToDate)
                .ToArray();

            var occurrenceIds = occurrences.Select(x => x.Id).ToArray();
            var materializedIds = occurrenceIds.Length == 0
                ? new HashSet<long>()
                : (await dbContext.Deliveries
                    .AsNoTracking()
                    .Where(x => x.SubscriptionDeliveryId != null && occurrenceIds.Contains(x.SubscriptionDeliveryId.Value))
                    .Select(x => x.SubscriptionDeliveryId!.Value)
                    .ToListAsync(cancellationToken)).ToHashSet();

            var skipped = new List<VacationSkippedItem>();
            var ineligible = new List<VacationIneligibleItem>();
            foreach (var occurrence in occurrences.OrderBy(x => x.ScheduledDate).ThenBy(x => x.Slot).ThenBy(x => x.Id))
            {
                var subscription = occurrence.Subscription;
                if (occurrence.Status != SubscriptionDeliveryStatus.Scheduled)
                {
                    ineligible.Add(new VacationIneligibleItem(
                        occurrence.ScheduledDate,
                        subscription.PublicId,
                        subscription.ProductNameSnapshot,
                        IneligibleReason(occurrence.Status)));
                    continue;
                }
                // D1-D slice 2.1 (Rule 7): a scheduled re-delivery replacement
                // is never auto-skipped by vacation. It is reported as
                // ineligible so staff can make the explicit operational
                // decision; the remedy stays Scheduled and fulfillable.
                if (occurrence.IsReplacement)
                {
                    ineligible.Add(new VacationIneligibleItem(
                        occurrence.ScheduledDate,
                        subscription.PublicId,
                        subscription.ProductNameSnapshot,
                        "replacementDelivery"));
                    continue;
                }
                if (materializedIds.Contains(occurrence.Id))
                {
                    ineligible.Add(new VacationIneligibleItem(
                        occurrence.ScheduledDate,
                        subscription.PublicId,
                        subscription.ProductNameSnapshot,
                        "deliveryPrepared"));
                    continue;
                }
                var deliveryStarts = DateTime.SpecifyKind(
                    occurrence.ScheduledDate.ToDateTime(TimeOnly.MinValue),
                    DateTimeKind.Unspecified);
                if (now > deliveryStarts - cutoff)
                {
                    ineligible.Add(new VacationIneligibleItem(
                        occurrence.ScheduledDate,
                        subscription.PublicId,
                        subscription.ProductNameSnapshot,
                        "cutoffPassed"));
                    continue;
                }

                subscription.Skip(occurrence, now, cutoff);
                AddSkipAudit(customerId, subscription, occurrence, now);
                skipped.Add(new VacationSkippedItem(
                    occurrence.ScheduledDate,
                    subscription.PublicId,
                    occurrence.PublicId,
                    subscription.ProductNameSnapshot,
                    occurrence.Slot));
            }

            dbContext.AddAuditLog(new AuditLog(
                customerId,
                "SUBSCRIPTION.VACATION",
                "Subscription",
                $"{request.FromDate:yyyy-MM-dd}/{request.ToDate:yyyy-MM-dd}",
                null,
                JsonSerializer.Serialize(new { skippedCount = skipped.Count, ineligibleCount = ineligible.Count }),
                null,
                null,
                null,
                now));

            await AddVacationNotificationAsync(customerId, request, skipped.Count, now, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new VacationResult(request.FromDate, request.ToDate, skipped.Count, skipped, ineligible);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SubscriptionDeliveryResult>> GetCalendarAsync(long customerId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
        return subscription.Deliveries
            .OrderBy(x => x.ScheduledDate)
            .Select(x => x.ToResult())
            .ToArray();
    }

    public async Task MarkDeliveryFailedAsync(long deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await dbContext.SubscriptionDeliveries
            .Include(x => x.Subscription)
            .SingleAsync(x => x.Id == deliveryId, cancellationToken);
        var now = timeProvider.Now;
        delivery.Subscription.MarkFailed(delivery, now);
        await InvalidateMaterializedOtpsAsync([delivery.Id], now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkDeliveryDeliveredAsync(long deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await dbContext.SubscriptionDeliveries
            .Include(x => x.Subscription)
            .SingleAsync(x => x.Id == deliveryId, cancellationToken);
        var now = timeProvider.Now;
        delivery.Subscription.MarkDelivered(delivery, now);
        await InvalidateMaterializedOtpsAsync([delivery.Id], now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the once-at-creation subscription charges for the single
    /// subscription product: active globals plus active item-level charges
    /// mapped to that product. The subscription has exactly one product, so
    /// every applicable charge shares the same frozen <paramref name="productValue"/>
    /// base (the complete prepaid product value). Grouping is by Charge.Id; the
    /// Stage 1 invariant
    /// (ApplicableOnAll = true ⇒ zero mappings) keeps the sets disjoint, and as
    /// pure defense a charge present in both resolves once as global — its current
    /// master flag — without mutating master data. Each amount is rounded
    /// independently (subscription convention); result is ordered by
    /// ChargeType, ChargeCode.
    /// </summary>
    private async Task<(IReadOnlyCollection<SubscriptionCharge> Charges, IReadOnlyList<Charge> Masters)> ResolveSubscriptionChargesAsync(
        long productId,
        decimal productValue,
        CancellationToken cancellationToken)
    {
        // GLOBAL set: active charges whose master flag is ApplicableOnAll.
        var globalMasters = await dbContext.Charges
            .Where(charge => charge.IsActive && charge.ApplicableOnAll)
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToListAsync(cancellationToken);
        var globalIds = globalMasters.Select(charge => charge.Id).ToHashSet();

        // PRODUCT-MAPPED set: the single product's mappings to active
        // item-level charges. (ProductId, ChargeId) is unique, so each charge
        // appears at most once; DistinctBy is pure defense.
        var mappedMasters = (await dbContext.ProductCharges
            .Where(link => link.ProductId == productId)
            .Where(link => link.Charge.IsActive && !link.Charge.ApplicableOnAll)
            .Select(link => link.Charge)
            .ToListAsync(cancellationToken))
            .DistinctBy(charge => charge.Id)
            .Where(charge => !globalIds.Contains(charge.Id))
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToList();

        var masters = globalMasters
            .Concat(mappedMasters)
            .OrderBy(charge => charge.ChargeType)
            .ThenBy(charge => charge.ChargeCode)
            .ToList();
        var rows = masters
            .Select(charge => new SubscriptionCharge(
                charge.ChargeType,
                charge.ChargeCode,
                charge.Description,
                charge.Percentage,
                productValue,
                decimal.Round(productValue * charge.Percentage / 100m, 2, MidpointRounding.AwayFromZero)))
            .ToArray();
        return (rows, masters);
    }

    private async Task<CreatedSubscriptionResult> CompleteCreationAsync(
        long customerId,
        Subscription subscription,
        CreateSubscriptionRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureMatchingCreationRequest(subscription, request);
        var payment = await paymentService.CreateForSubscriptionAsync(
            customerId,
            subscription.Id,
            request.PaymentMethod,
            idempotencyKey,
            cancellationToken);
        return new CreatedSubscriptionResult(subscription.ToResult(), payment);
    }

    private static void EnsureMatchingCreationRequest(
        Subscription subscription,
        CreateSubscriptionRequest request)
    {
        var requestedDays = request.DeliveryDays.Distinct().OrderBy(x => x).ToArray();
        var persistedDays = subscription.Schedules.Select(x => x.DayOfWeek).OrderBy(x => x).ToArray();
        var persistedSlot = subscription.Schedules
            .Select(x => x.Slot)
            .Distinct()
            .SingleOrDefault();
        if (subscription.Product.PublicId != request.ProductId ||
            subscription.CustomerAddress.PublicId != request.AddressId ||
            subscription.Quantity != request.Quantity ||
            subscription.StartDate != request.StartDate ||
            subscription.TotalEntitlement != request.TotalEntitlement ||
            persistedSlot != request.Slot ||
            !persistedDays.SequenceEqual(requestedDays))
        {
            throw new ConflictException(
                "The idempotency key is already associated with a different subscription request.");
        }
    }

    /// <summary>
    /// D1-D slice 5: an approved ReDelivery ticket linked to one of this
    /// subscription's replacements that is still unfulfilled. Candidates are
    /// identified strictly through persisted linkage (ticket → replacement
    /// occurrence → operational delivery), never from client input.
    /// </summary>
    private sealed record PendingRedeliveryCandidate(
        RefundReplacementRequest Ticket,
        SubscriptionDelivery Replacement,
        Delivery? Operational);

    private async Task<IReadOnlyList<PendingRedeliveryCandidate>> FindPendingRedeliveryCandidatesAsync(
        long subscriptionId,
        CancellationToken cancellationToken)
    {
        var tickets = await dbContext.RefundReplacementRequests
            .Include(x => x.SubscriptionDelivery)
            .Include(x => x.Delivery)
            .Where(x => x.Status == RefundReplacementStatus.Approved
                && x.Source == RefundReplacementSource.DeliveryFailed
                && x.CustomerChoice == CustomerChoice.ReDelivery
                && x.SubscriptionDeliveryId != null)
            .ToListAsync(cancellationToken);
        tickets = tickets
            .Where(x => x.SubscriptionDelivery is not null && x.SubscriptionDelivery.SubscriptionId == subscriptionId)
            .ToList();
        if (tickets.Count == 0)
        {
            return [];
        }

        var ticketIds = tickets.Select(x => x.Id).ToHashSet();
        var replacements = await dbContext.SubscriptionDeliveries
            .Where(x => x.IsReplacement
                && x.SubscriptionId == subscriptionId
                && x.RefundReplacementRequestId != null
                && ticketIds.Contains(x.RefundReplacementRequestId.Value))
            .ToListAsync(cancellationToken);
        var byTicket = replacements.ToDictionary(x => x.RefundReplacementRequestId!.Value);

        var candidates = new List<PendingRedeliveryCandidate>();
        foreach (var ticket in tickets)
        {
            // No replacement row: a data anomaly (approval always creates
            // one). Left for explicit staff resolution, never auto-refunded.
            if (!byTicket.TryGetValue(ticket.Id, out var replacement))
            {
                continue;
            }
            // Delivered replacements are owned by the Slice-3 completion path
            // (ticket already Completed); Failed replacements are owned by the
            // Slice-4 refund-only remedy path. Resolving either here could
            // refund the same entitlement twice.
            if (replacement.Status is SubscriptionDeliveryStatus.Delivered or SubscriptionDeliveryStatus.Failed)
            {
                continue;
            }
            var operational = await dbContext.Deliveries
                .SingleOrDefaultAsync(x => x.SubscriptionDeliveryId == replacement.Id, cancellationToken);
            candidates.Add(new PendingRedeliveryCandidate(ticket, replacement, operational));
        }

        return candidates;
    }

    /// <summary>
    /// D1-D slice 5: voids one safely-cancellable pending replacement and
    /// resolves its ticket with the frozen entitlement refund. Payment
    /// feasibility is validated before the replacement is voided; any failure
    /// throws before SaveChanges, rolling back the whole cancellation.
    /// </summary>
    private async Task ResolvePendingReplacementAsync(
        long customerId,
        Subscription subscription,
        PendingRedeliveryCandidate candidate,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var ticket = candidate.Ticket;
        var operational = candidate.Operational;

        // The ticket's frozen deduction snapshot is authoritative; current
        // configuration is never consulted (same rule as D1-C).
        var deductionPercentage = ticket.DeliveryFailDeductionPercentageSnapshot
            ?? throw new ConflictException(
                $"The subscription cannot be cancelled: replacement ticket {ticket.RequestNumber} has no frozen deduction. " +
                "Resolve it with staff support first.");

        // Success/PartiallyRefunded mirror the D1-C lookup; Refunded is also
        // matched so an exhausted payment reports its exact state below
        // instead of a misleading "no payment" error. Any other status (or
        // no row) cannot back a safe refund.
        var payment = await dbContext.Payments
            .Include(x => x.Refunds)
            .Where(x => x.SubscriptionId == subscription.Id
                && x.CustomerId == ticket.CustomerId
                && (x.Status == PaymentStatus.Success
                    || x.Status == PaymentStatus.PartiallyRefunded
                    || x.Status == PaymentStatus.Refunded))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ConflictException(
                $"The subscription cannot be cancelled: no successful payment covers replacement ticket {ticket.RequestNumber}. " +
                "Resolve it with staff support first.");

        // Frozen refund math, identical to D1-C: one charge-inclusive share of
        // the frozen PayableAmount (never repriced), less the snapshotted
        // deduction, capped by the payment's remaining refundable balance so
        // cumulative credits never exceed the frozen payable. PayableAmount,
        // ChargesTotal, prices and charge snapshots are only read, never
        // recalculated.
        var gross = decimal.Round(
            subscription.PayableAmount / subscription.TotalEntitlement,
            2,
            MidpointRounding.AwayFromZero);
        var deduction = decimal.Round(
            gross * deductionPercentage / 100m,
            2,
            MidpointRounding.AwayFromZero);
        var final = gross - deduction;
        var remaining = payment.Amount - payment.RefundedAmount;
        if (remaining <= 0)
        {
            throw new ConflictException(
                $"The subscription cannot be cancelled: the payment covering replacement ticket {ticket.RequestNumber} " +
                "is already fully refunded. Resolve it with staff support first.");
        }
        if (final > remaining)
        {
            final = remaining;
        }

        // Distinct deterministic namespace: a prior D1-C timeout credit (or a
        // retry of this path) can never duplicate, and the payment cap stays
        // exact across namespaces.
        var idempotencyKey = $"cancel-redelivery:{ticket.PublicId:N}:refund";
        var deliveryNumber = ticket.Delivery.DeliveryNumber ?? ticket.Delivery.ReferenceNumber;
        var voidReason =
            $"Subscription cancelled; pending re-delivery replacement voided (ticket {ticket.RequestNumber}).";

        // Void the pre-pickup operational delivery through the terminal
        // cancellation state — never via Delivery.Fail, which would mint
        // another failure ticket and trigger the wrong refund lifecycle. The
        // occurrence itself was already flipped to Cancelled by the domain
        // subscription cancel above; both histories are preserved.
        if (operational is not null && operational.Status != DeliveryStatus.Cancelled)
        {
            var previousOperationalStatus = operational.Status;
            Mutate(() => operational.Cancel(now, voidReason));
            dbContext.AddAuditLog(new AuditLog(
                customerId,
                "DELIVERY.CANCEL",
                "Delivery",
                operational.PublicId.ToString(),
                JsonSerializer.Serialize(new { Status = previousOperationalStatus.ToString() }),
                JsonSerializer.Serialize(new
                {
                    Status = operational.Status.ToString(),
                    Ticket = ticket.RequestNumber
                }),
                null,
                null,
                voidReason,
                now));
        }

        if (final > 0)
        {
            await CreditCancelWalletAsync(
                ticket.CustomerId,
                subscription.Id,
                payment,
                final,
                idempotencyKey,
                $"Subscription-cancellation re-delivery refund {ticket.RequestNumber} for delivery {deliveryNumber}",
                now,
                cancellationToken);
            RecordCancelRefund(payment, final, idempotencyKey, ticket.CustomerId, now);
        }

        // D1-D slice 7.1: explicit cancellation/refund outcome — never a
        // delivery completion. The replacement was voided, not delivered, so
        // the ticket resolves to the distinct terminal Cancelled status
        // (Completed keeps meaning successful re-delivery; Failed keeps
        // meaning the replacement-failure refund-only path). The resolver is
        // the cancelling customer (the real initiating actor), never an
        // employee.
        var remarks = final > 0
            ? $"Subscription cancelled; pending replacement voided and " +
              $"{final.ToString("0.00", CultureInfo.InvariantCulture)} refunded to wallet " +
              $"(gross {gross.ToString("0.00", CultureInfo.InvariantCulture)}, " +
              $"deduction {deduction.ToString("0.00", CultureInfo.InvariantCulture)})."
            : "Subscription cancelled; pending replacement voided with no wallet credit (fully deducted).";
        Mutate(() => ticket.MarkSubscriptionCancelled(customerId, now, remarks));

        dbContext.AddAuditLog(new AuditLog(
            customerId,
            "REFUND_REPLACEMENT.CANCEL_REFUND",
            "RefundReplacementRequest",
            ticket.PublicId.ToString(),
            JsonSerializer.Serialize(new
            {
                ticket.RequestNumber,
                Status = RefundReplacementStatus.Approved.ToString()
            }),
            JsonSerializer.Serialize(new
            {
                ticket.RequestNumber,
                Status = ticket.Status.ToString(),
                Resolution = "SubscriptionCancelled",
                DeliveryNumber = deliveryNumber,
                GrossRefund = gross,
                DeductionPercentage = deductionPercentage,
                DeductionAmount = deduction,
                WalletCredit = final,
                SubscriptionPayable = subscription.PayableAmount,
                subscription.TotalEntitlement
            }),
            null,
            null,
            remarks,
            now));

        // Buffered into the same transaction (outbox): the customer is
        // notified only after the financial operation is durably committed,
        // and the unique EventKey keeps redelivery exactly-once.
        notificationEventWriter.Add(new NotificationEventRequest(
            ticket.CustomerId,
            NotificationEventTypes.RefundReplacementCancelRefunded,
            $"refund-replacement:{ticket.PublicId:N}:cancel-refund",
            new Dictionary<string, string>
            {
                ["requestId"] = ticket.PublicId.ToString(),
                ["requestNumber"] = ticket.RequestNumber,
                ["deliveryNumber"] = deliveryNumber,
                ["grossRefund"] = gross.ToString("0.00", CultureInfo.InvariantCulture),
                ["deductionPercentage"] = deductionPercentage.ToString("0.##", CultureInfo.InvariantCulture),
                ["deductionAmount"] = deduction.ToString("0.00", CultureInfo.InvariantCulture),
                ["walletCredit"] = final.ToString("0.00", CultureInfo.InvariantCulture),
                ["message"] =
                    $"Your subscription was cancelled while replacement delivery {deliveryNumber} was still pending. " +
                    $"{final.ToString("0.00", CultureInfo.InvariantCulture)} was credited to your wallet after a deduction of " +
                    $"{deduction.ToString("0.00", CultureInfo.InvariantCulture)} (ticket {ticket.RequestNumber})."
            },
            $"/refund-replacements/{ticket.PublicId}",
            now));
    }

    private async Task CreditCancelWalletAsync(
        long customerId,
        long subscriptionId,
        Payment payment,
        decimal amount,
        string idempotencyKey,
        string description,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Same ledger mechanics as the D1-C timeout path: the wallet is
        // created on demand in the payment's currency (wallet credit applies
        // regardless of the original payment method; no gateway is called),
        // and a committed credit with this key is reused, never duplicated.
        // The tracker is checked first so multiple tickets resolved in one
        // cancellation share a single created wallet instead of inserting
        // duplicates (the database row does not exist until commit).
        var wallet = dbContext.Wallets.Local.SingleOrDefault(x => x.CustomerId == customerId)
            ?? await dbContext.Wallets
                .SingleOrDefaultAsync(x => x.CustomerId == customerId, cancellationToken);
        if (wallet is null)
        {
            wallet = new Wallet(customerId, payment.Currency);
            dbContext.Wallets.Add(wallet);
        }
        else
        {
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
            payment.Id,
            subscriptionId: subscriptionId));
    }

    private static void RecordCancelRefund(
        Payment payment,
        decimal amount,
        string idempotencyKey,
        long customerId,
        DateTime now)
    {
        // Same local refund recording as D1-C: no gateway call, the money is
        // returned as wallet credit, and the payment cap
        // (Amount - RefundedAmount) is enforced by the domain. A pre-existing
        // refund row with this key is authoritative and never duplicated.
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
            "Subscription-cancellation re-delivery refund to wallet.",
            idempotencyKey,
            customerId));
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

    private async Task InvalidateMaterializedOtpsAsync(
        IEnumerable<long> subscriptionDeliveryIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var ids = subscriptionDeliveryIds.ToArray();
        if (ids.Length == 0) return;

        var deliveries = await dbContext.Deliveries
            .Include(x => x.Otps)
            .Where(x => x.SubscriptionDeliveryId.HasValue && ids.Contains(x.SubscriptionDeliveryId.Value))
            .ToListAsync(cancellationToken);
        foreach (var materializedDelivery in deliveries)
        {
            foreach (var otp in materializedDelivery.Otps)
            {
                otp.Invalidate(now);
            }
        }
    }

    private IQueryable<Subscription> Query() => dbContext.Subscriptions
        .Include(x => x.Product)
        .Include(x => x.CustomerAddress)
        .Include(x => x.Branch)
        .Include(x => x.Schedules)
        .Include(x => x.Deliveries).ThenInclude(x => x.Branch);

    private async Task<Subscription> FindOwnedAsync(long customerId, Guid subscriptionId, CancellationToken cancellationToken) =>
        await Query().SingleOrDefaultAsync(x => x.CustomerId == customerId && x.PublicId == subscriptionId, cancellationToken)
        ?? throw new NotFoundException("The subscription was not found.");

    private async Task LoadNavigationAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        await dbContext.Entry(subscription).Reference(x => x.Product).LoadAsync(cancellationToken);
        await dbContext.Entry(subscription).Reference(x => x.CustomerAddress).LoadAsync(cancellationToken);
        await dbContext.Entry(subscription).Reference(x => x.Branch).LoadAsync(cancellationToken);
    }

    private async Task LoadDeliveryNavigationAsync(SubscriptionDelivery delivery, CancellationToken cancellationToken) =>
        await dbContext.Entry(delivery).Reference(x => x.Branch).LoadAsync(cancellationToken);

    private async Task EnsureCutoffAsync(Subscription subscription, CancellationToken cancellationToken)
    {
        var next = subscription.Deliveries
            .Where(x => x.Status == SubscriptionDeliveryStatus.Scheduled)
            .OrderBy(x => x.ScheduledDate)
            .FirstOrDefault();
        if (next is null) return;

        var deliveryStart = DateTime.SpecifyKind(
            next.ScheduledDate.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);
        if (timeProvider.Now > deliveryStart - await GetCutoffAsync(cancellationToken))
        {
            throw new BusinessRuleException("The pause cutoff has passed for the next delivery.");
        }
    }

    private async Task<TimeSpan> GetCutoffAsync(CancellationToken cancellationToken)
    {
        var configuration = await dbContext.SystemConfigurations
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == CutoffConfigurationKey, cancellationToken);
        if (configuration is null ||
            !double.TryParse(configuration.Value, out var hours) ||
            hours < 0)
        {
            return TimeSpan.FromHours(DefaultCutoffHours);
        }

        return TimeSpan.FromHours(hours);
    }

    private void ValidateVacationRange(CreateVacationRequest request)
    {
        if (request.ToDate < request.FromDate)
        {
            throw new ValidationAppException("The vacation end date cannot precede its start date.", "toDate");
        }

        if (request.FromDate < timeProvider.Today)
        {
            throw new ValidationAppException("The vacation start date cannot be in the past.", "fromDate");
        }

        // Same inclusive-term cap as subscription creation (1-366 entitlement days).
        if (request.ToDate > request.FromDate.AddDays(365))
        {
            throw new ValidationAppException("The vacation range cannot exceed 366 days.", "toDate");
        }
    }

    private static string IneligibleReason(SubscriptionDeliveryStatus status) => status switch
    {
        SubscriptionDeliveryStatus.Skipped => "alreadySkipped",
        SubscriptionDeliveryStatus.Delivered => "alreadyDelivered",
        SubscriptionDeliveryStatus.Failed => "failed",
        SubscriptionDeliveryStatus.Cancelled => "cancelled",
        _ => "notScheduled"
    };

    /// <summary>
    /// One summary notification per vacation request. The unique event key
    /// (customer + inclusive range) makes retries idempotent: NotificationEvent
    /// has a unique index on EventKey, so a repeated request cannot enqueue a
    /// duplicate summary.
    /// </summary>
    private async Task AddVacationNotificationAsync(
        long customerId,
        CreateVacationRequest request,
        int skippedCount,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.NotificationEvents
            .AsNoTracking()
            .AnyAsync(x => x.UserId == customerId &&
                x.EventType == NotificationEventTypes.SubscriptionVacationSet &&
                x.EventKey == VacationEventKey(customerId, request), cancellationToken);
        if (existing)
        {
            return;
        }

        var message = skippedCount == 0
            ? $"Vacation set from {request.FromDate:dd MMM yyyy} to {request.ToDate:dd MMM yyyy}. " +
              "No deliveries were scheduled in this period."
            : $"Vacation set from {request.FromDate:dd MMM yyyy} to {request.ToDate:dd MMM yyyy}. " +
              $"{skippedCount} " + (skippedCount == 1 ? "delivery was" : "deliveries were") + " skipped.";
        notificationEventWriter.Add(new NotificationEventRequest(
            customerId,
            NotificationEventTypes.SubscriptionVacationSet,
            VacationEventKey(customerId, request),
            new Dictionary<string, string>
            {
                ["message"] = message,
                ["fromDate"] = request.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["toDate"] = request.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["skippedCount"] = skippedCount.ToString(CultureInfo.InvariantCulture)
            },
            "/subscriptions"));
    }

    private static string VacationEventKey(long customerId, CreateVacationRequest request) =>
        $"subscription-vacation:{customerId}:{request.FromDate:yyyyMMdd}-{request.ToDate:yyyyMMdd}";

    private void AddSkipAudit(long customerId, Subscription subscription, SubscriptionDelivery delivery, DateTime now) =>
        dbContext.AddAuditLog(new AuditLog(
            customerId,
            "SUBSCRIPTION.SKIP",
            "SubscriptionDelivery",
            delivery.PublicId.ToString(),
            null,
            JsonSerializer.Serialize(new
            {
                subscriptionId = subscription.PublicId,
                scheduledDate = delivery.ScheduledDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }),
            null,
            null,
            null,
            now));

    private async Task EnsureNoMaterializedDeliveryAsync(
        SubscriptionDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (await dbContext.Deliveries.AsNoTracking()
                .AnyAsync(x => x.SubscriptionDeliveryId == delivery.Id, cancellationToken))
        {
            throw new BusinessRuleException(
                "Delivery is already being prepared for this date and can no longer be skipped.");
        }
    }

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
                System.Data.IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
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

    private static DateOnly[] GenerateDates(DateOnly startDate, IReadOnlyCollection<DayOfWeek> days, int entitlement)
    {
        var selected = days.ToHashSet();
        var dates = new List<DateOnly>(entitlement);
        for (var date = startDate; dates.Count < entitlement && date <= startDate.AddDays(730); date = date.AddDays(1))
        {
            if (selected.Contains(date.DayOfWeek)) dates.Add(date);
        }
        if (dates.Count != entitlement)
        {
            throw new ValidationAppException("The requested schedule cannot produce the entitlement within the allowed term.");
        }
        return dates.ToArray();
    }

    private static string FormatAddress(CustomerAddress address) =>
        string.Join(", ", new[]
        {
            address.Label, address.AddressLine1, address.AddressLine2, address.Locality,
            address.City, address.State, address.PinCode, address.Landmark
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private void ValidateRequest(CreateSubscriptionRequest request)
    {
        if (request.ProductId == Guid.Empty) throw new ValidationAppException("A product is required.", "ProductId");
        if (request.AddressId == Guid.Empty) throw new ValidationAppException("An active address is required.", "AddressId");
        if (request.StartDate < timeProvider.Today) throw new ValidationAppException("Start date cannot be in the past.", "StartDate");
        if (request.TotalEntitlement is < 1 or > 366) throw new ValidationAppException("Entitlement must be between 1 and 366.", "TotalEntitlement");
        if (request.Quantity <= 0 || decimal.Round(request.Quantity, 3) != request.Quantity) throw new ValidationAppException("Quantity must be positive and use at most three decimal places.", "Quantity");
        if (!Enum.IsDefined(request.Slot)) throw new ValidationAppException("A valid delivery slot is required.", "Slot");
        ValidateDays(request.DeliveryDays);
    }

    private static void ValidateDays(IReadOnlyCollection<DayOfWeek>? days)
    {
        if (days is null || days.Count == 0 || days.Count != days.Distinct().Count())
        {
            throw new ValidationAppException("Select at least one unique delivery day.", "DeliveryDays");
        }
    }

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100)
        {
            throw new ValidationAppException("A valid Idempotency-Key header is required.", "Idempotency-Key");
        }
    }
}
