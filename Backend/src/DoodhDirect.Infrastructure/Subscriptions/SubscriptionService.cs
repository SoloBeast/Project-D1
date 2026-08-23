using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
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
        var subscription = new Subscription(
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
            subscription.AddSchedule(day, request.Slot);
        }
        foreach (var date in dates)
        {
            subscription.AddDelivery(date, request.Slot);
        }

        dbContext.Subscriptions.Add(subscription);
        var createdEventKey = $"subscription:{subscription.PublicId:N}:created";
        var paymentPendingEventKey = $"subscription:{subscription.PublicId:N}:payment-pending";
        notificationEventWriter.Add(new NotificationEventRequest(
            customerId,
            NotificationEventTypes.SubscriptionCreated,
            createdEventKey,
            new Dictionary<string, string>
            {
                ["message"] = $"Your {subscription.ProductNameSnapshot} subscription has been created.",
                ["subscriptionId"] = subscription.PublicId.ToString()
            },
            $"/subscriptions/{subscription.PublicId}"));
        notificationEventWriter.Add(new NotificationEventRequest(
            customerId,
            NotificationEventTypes.SubscriptionPaymentPending,
            paymentPendingEventKey,
            new Dictionary<string, string>
            {
                ["amount"] = subscription.PayableAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
                ["currency"] = "INR",
                ["message"] = "Payment is pending for your subscription.",
                ["subscriptionId"] = subscription.PublicId.ToString()
            },
            $"/subscriptions/{subscription.PublicId}"));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            foreach (var schedule in subscription.Schedules)
            {
                dbContext.Entry(schedule).State = EntityState.Detached;
            }
            foreach (var delivery in subscription.Deliveries)
            {
                dbContext.Entry(delivery).State = EntityState.Detached;
            }
            dbContext.Entry(subscription).State = EntityState.Detached;
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

        await LoadNavigationAsync(subscription, cancellationToken);
        return await CompleteCreationAsync(
            customerId, subscription, request, normalizedKey, cancellationToken);
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

    public async Task<SubscriptionDeliveryResult> SkipAsync(
        long customerId,
        Guid subscriptionId,
        SkipSubscriptionDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var subscription = await FindOwnedAsync(customerId, subscriptionId, cancellationToken);
        var delivery = subscription.Deliveries.SingleOrDefault(x => x.PublicId == request.DeliveryId)
            ?? throw new NotFoundException("The subscription delivery was not found.");
        var now = timeProvider.Now;
        subscription.Skip(delivery, now, await GetCutoffAsync(cancellationToken));
        notificationEventWriter.Add(new NotificationEventRequest(
            customerId,
            NotificationEventTypes.SubscriptionSkipped,
            $"subscription-delivery:{delivery.PublicId:N}:skipped",
            new Dictionary<string, string>
            {
                ["date"] = delivery.ScheduledDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                ["message"] = $"Your delivery for {delivery.ScheduledDate:dd MMM yyyy} has been skipped.",
                ["subscriptionId"] = subscription.PublicId.ToString()
            },
            $"/subscriptions/{subscription.PublicId}",
            delivery.StatusChangedAt));
        await InvalidateMaterializedOtpsAsync([delivery.Id], now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await LoadDeliveryNavigationAsync(delivery, cancellationToken);
        return delivery.ToResult();
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
