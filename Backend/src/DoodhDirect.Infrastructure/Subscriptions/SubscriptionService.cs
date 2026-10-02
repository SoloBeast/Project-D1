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
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
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

    public async Task<SubscriptionResult> CancelAsync(long customerId, Guid subscriptionId, CancellationToken cancellationToken)
    {
        var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
        var now = timeProvider.Now;
        subscription.Cancel(now);
        await InvalidateMaterializedOtpsAsync(subscription.Deliveries.Select(x => x.Id), now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return subscription.ToResult();
    }

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
