using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class SubscriptionServiceTests
{
    [Fact]
    public async Task Create_GeneratesFiniteOccurrencesFromWeekdaysAndUsesAuthoritativePrice()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var request = harness.Request(
            quantity: 1.5m,
            startDate: new DateOnly(2026, 8, 17),
            days: [DayOfWeek.Monday, DayOfWeek.Wednesday],
            entitlement: 5);

        var result = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-1", CancellationToken.None);

        Assert.Equal(600m, result.Subscription.PayableAmount);
        Assert.Equal(80m, result.Subscription.UnitPrice);
        Assert.Equal(5, result.Subscription.TotalEntitlement);
        Assert.Equal(5, result.Subscription.RemainingEntitlement);
        Assert.Equal(new DateOnly(2026, 8, 31), result.Subscription.EndDate);
        Assert.Equal(PaymentMethod.Razorpay, result.Payment.Method);
        Assert.Equal(result.Subscription.PublicId, result.Payment.SubscriptionId);

        var deliveries = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .ToListAsync();
        Assert.Equal(
            [
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 19),
                new DateOnly(2026, 8, 24),
                new DateOnly(2026, 8, 26),
                new DateOnly(2026, 8, 31)
            ],
            deliveries.Select(x => x.ScheduledDate));
        Assert.All(deliveries, x => Assert.Equal(SubscriptionDeliveryStatus.Scheduled, x.Status));
        Assert.Single(harness.PaymentService.Calls);
        Assert.Equal("subscription-1", harness.PaymentService.Calls[0].IdempotencyKey);
    }

    [Fact]
    public async Task Create_ReplayReturnsSameSubscriptionAndRejectsChangedRequest()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var request = harness.Request();

        var first = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-1", CancellationToken.None);
        var replay = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-1", CancellationToken.None);

        Assert.Equal(first.Subscription.PublicId, replay.Subscription.PublicId);
        Assert.Single(await harness.Db.Subscriptions.AsNoTracking().ToListAsync());
        Assert.Equal(2, harness.PaymentService.Calls.Count);

        var events = await harness.Db.NotificationEvents
            .AsNoTracking()
            .OrderBy(x => x.EventType)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, notificationEvent =>
        {
            Assert.Equal(harness.Customer.Id, notificationEvent.UserId);
            Assert.Equal(harness.TimeProvider.Now, notificationEvent.OccurredAt);
            Assert.Equal(
                $"/subscriptions/{first.Subscription.PublicId}",
                Payload(notificationEvent).GetProperty("DeepLink").GetString());
            Assert.Equal(
                first.Subscription.PublicId.ToString(),
                Variables(notificationEvent).GetProperty("subscriptionId").GetString());
        });
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionCreated &&
            notificationEvent.EventKey ==
                $"subscription:{first.Subscription.PublicId:N}:created" &&
            !notificationEvent.IsCritical);
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionPaymentPending &&
            notificationEvent.EventKey ==
                $"subscription:{first.Subscription.PublicId:N}:payment-pending" &&
            notificationEvent.IsCritical &&
            Variables(notificationEvent).GetProperty("amount").GetString() == "320.00" &&
            Variables(notificationEvent).GetProperty("currency").GetString() == "INR");

        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CreateAsync(
            harness.Customer.Id,
            request with { TotalEntitlement = request.TotalEntitlement + 1 },
            "subscription-1",
            CancellationToken.None));
        Assert.Equal(2, harness.PaymentService.Calls.Count);
    }

    [Fact]
    public async Task ReadsAndActions_AreCustomerScoped()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-1", CancellationToken.None);

        Assert.Empty(await harness.Service.GetForCustomerAsync(
            harness.OtherCustomer.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetAsync(
            harness.OtherCustomer.Id, created.Subscription.PublicId, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.CancelAsync(
            harness.OtherCustomer.Id, created.Subscription.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task PauseAndSkip_EnforceConfiguredCutoff()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 8, 16, 13, 0, 0, DateTimeKind.Utc),
            cutoffHours: 12);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(startDate: new DateOnly(2026, 8, 17), days: [DayOfWeek.Monday]),
            "subscription-1",
            CancellationToken.None);
        await harness.ActivateAsync();
        var delivery = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .FirstAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.PauseAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.SkipAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new SkipSubscriptionDeliveryRequest(delivery.PublicId),
            CancellationToken.None));

        Assert.Equal(
            SubscriptionDeliveryStatus.Scheduled,
            (await harness.Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.PublicId == delivery.PublicId)).Status);
    }

    [Fact]
    public async Task PauseResumeSkipAndCancel_PersistExpectedCustomerState()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
            cutoffHours: 12);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 8, 17),
                days: [DayOfWeek.Monday, DayOfWeek.Wednesday],
                entitlement: 2),
            "subscription-1",
            CancellationToken.None);
        await harness.ActivateAsync();
        var firstDelivery = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .FirstAsync();

        var paused = await harness.Service.PauseAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        var resumed = await harness.Service.ResumeAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        var skipped = await harness.Service.SkipAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new SkipSubscriptionDeliveryRequest(firstDelivery.PublicId),
            CancellationToken.None);
        var cancelled = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        var replay = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Paused, paused.Status);
        Assert.Equal(SubscriptionStatus.Active, resumed.Status);
        Assert.Equal(SubscriptionDeliveryStatus.Skipped, skipped.Status);
        Assert.Equal(SubscriptionStatus.Cancelled, cancelled.Status);
        Assert.Equal(SubscriptionStatus.Cancelled, replay.Status);
        var statuses = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .Select(x => x.Status)
            .ToListAsync();
        Assert.Equal([SubscriptionDeliveryStatus.Skipped, SubscriptionDeliveryStatus.Cancelled], statuses);

        var events = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x =>
                x.EventType == NotificationEventTypes.SubscriptionPaused ||
                x.EventType == NotificationEventTypes.SubscriptionResumed ||
                x.EventType == NotificationEventTypes.SubscriptionSkipped)
            .OrderBy(x => x.EventType)
            .ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionPaused &&
            notificationEvent.EventKey ==
                $"subscription:{created.Subscription.PublicId:N}:paused:{harness.TimeProvider.Now.Ticks}" &&
            notificationEvent.OccurredAt == harness.TimeProvider.Now);
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionResumed &&
            notificationEvent.EventKey ==
                $"subscription:{created.Subscription.PublicId:N}:resumed:{harness.TimeProvider.Now.Ticks}" &&
            notificationEvent.OccurredAt == harness.TimeProvider.Now);
        var skippedEvent = Assert.Single(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.SubscriptionSkipped);
        Assert.Equal(
            $"subscription-delivery:{firstDelivery.PublicId:N}:skipped",
            skippedEvent.EventKey);
        Assert.Equal(harness.TimeProvider.Now, skippedEvent.OccurredAt);
        Assert.Equal(
            "2026-08-17",
            Variables(skippedEvent).GetProperty("date").GetString());
        Assert.Equal(
            $"/subscriptions/{created.Subscription.PublicId}",
            Payload(skippedEvent).GetProperty("DeepLink").GetString());
    }

    private static JsonElement Payload(DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson);

    private static JsonElement Variables(DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        Payload(notificationEvent).GetProperty("Variables");

    // ---------------------------------------------------------------------
    // Vacation (customer-wide inclusive date-range skip) tests.
    // The range command resolves to existing per-occurrence Skipped state;
    // no vacation entity is persisted.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Vacation_RejectsInvalidRanges()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc));

        // toDate before fromDate.
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 15)),
            CancellationToken.None));
        // fromDate in the past.
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 20)),
            CancellationToken.None));
        // Range longer than 366 days.
        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 10, 15), new DateOnly(2027, 10, 20)),
            CancellationToken.None));
    }

    [Fact]
    public async Task Vacation_SkipsEligibleOccurrencesAcrossSubscriptionsAndReportsIneligible()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc),
            cutoffHours: 24);
        // Subscription A: Monday/Wednesday occurrences. 15 Oct 2026 is a
        // Thursday, so the first occurrence is Monday 19 Oct.
        var first = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 10, 15),
                days: [DayOfWeek.Monday, DayOfWeek.Wednesday],
                entitlement: 4),
            "subscription-a",
            CancellationToken.None);
        await harness.ActivateAsync();
        var otherProduct = await harness.AddSecondProductAsync();
        var secondSubscription = await harness.CreateSecondSubscriptionAsync(
            otherProduct, "subscription-b", new DateOnly(2026, 10, 16), [DayOfWeek.Monday]);

        var result = await harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 22)),
            CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 10, 15), result.FromDate);
        Assert.Equal(new DateOnly(2026, 10, 22), result.ToDate);
        // Subscription A: Mon 19 + Wed 21 Oct; Subscription B: Mon 19 Oct.
        Assert.Equal(3, result.SkippedCount);
        Assert.All(result.SkippedDates, item =>
            Assert.Contains(item.SubscriptionId, new[] { first.Subscription.PublicId, secondSubscription.Subscription.PublicId }));
        Assert.Empty(result.Ineligible);

        var statuses = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .Where(x => x.ScheduledDate >= new DateOnly(2026, 10, 15) &&
                x.ScheduledDate <= new DateOnly(2026, 10, 22))
            .ToListAsync();
        Assert.Equal(3, statuses.Count(x => x.Status == SubscriptionDeliveryStatus.Skipped));

        // Entitlement untouched, end dates unchanged, subscription still active.
        var reloadedFirst = await harness.Db.Subscriptions
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == first.Subscription.PublicId);
        Assert.Equal(0, reloadedFirst.UsedEntitlement);
        Assert.Equal(4, reloadedFirst.TotalEntitlement);
        Assert.Equal(SubscriptionStatus.Active, reloadedFirst.Status);

        // One summary vacation notification, none per occurrence.
        var vacationEvents = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.SubscriptionVacationSet)
            .ToListAsync();
        var vacationEvent = Assert.Single(vacationEvents);
        Assert.Equal(
            $"subscription-vacation:{harness.Customer.Id}:20261015-20261022",
            vacationEvent.EventKey);
        Assert.Equal(0, vacationEvent.IsCritical ? 1 : 0);
        Assert.Equal(
            "3",
            Variables(vacationEvent).GetProperty("skippedCount").GetString());
        Assert.Empty((await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.SubscriptionSkipped)
            .ToListAsync()));

        // One summary audit + one SUBSCRIPTION.SKIP audit per skipped occurrence.
        var vacationAudits = await harness.Db.AuditLogs
            .AsNoTracking()
            .Where(x => x.Action == "SUBSCRIPTION.VACATION")
            .ToListAsync();
        Assert.Single(vacationAudits);
        Assert.Equal(
            3,
            (await harness.Db.AuditLogs
                .AsNoTracking()
                .Where(x => x.Action == "SUBSCRIPTION.SKIP")
                .ToListAsync()).Count);
    }

    [Fact]
    public async Task Vacation_ReplayDoesNotDuplicateNotificationOrSkipAudits()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc),
            cutoffHours: 24);
        await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 10, 15),
                days: [DayOfWeek.Monday],
                entitlement: 2),
            "subscription-a",
            CancellationToken.None);
        await harness.ActivateAsync();
        var request = new CreateVacationRequest(
            new DateOnly(2026, 10, 15),
            new DateOnly(2026, 10, 26));

        var first = await harness.Service.CreateVacationAsync(
            harness.Customer.Id, request, CancellationToken.None);
        var replay = await harness.Service.CreateVacationAsync(
            harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal(2, first.SkippedCount);
        // Second run: occurrences are already Skipped → reported, not re-skipped.
        Assert.Equal(0, replay.SkippedCount);
        Assert.Equal(
            replay.Ineligible.Count,
            replay.Ineligible.Count(x => x.Reason == "alreadySkipped"));
        Assert.Single(await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.SubscriptionVacationSet)
            .ToListAsync());
        Assert.Equal(
            2,
            (await harness.Db.AuditLogs
                .AsNoTracking()
                .Where(x => x.Action == "SUBSCRIPTION.SKIP")
                .ToListAsync()).Count);
        // Summary audit is written per command run (each run is a distinct action).
        Assert.Equal(
            2,
            (await harness.Db.AuditLogs
                .AsNoTracking()
                .Where(x => x.Action == "SUBSCRIPTION.VACATION")
                .ToListAsync()).Count);
    }

    [Fact]
    public async Task Vacation_ReportsCutoffPassedAndLeavesOccurrenceScheduled()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 10, 14, 20, 0, 0, DateTimeKind.Utc),
            cutoffHours: 24);
        await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 10, 15),
                days: [DayOfWeek.Thursday],
                entitlement: 1),
            "subscription-a",
            CancellationToken.None);
        await harness.ActivateAsync();

        // India-local now is 15 Oct 01:30; the 15 Oct occurrence starts at
        // midnight minus the 24h cutoff → cutoff has passed.
        var result = await harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15)),
            CancellationToken.None);

        Assert.Equal(0, result.SkippedCount);
        var ineligible = Assert.Single(result.Ineligible);
        Assert.Equal("cutoffPassed", ineligible.Reason);
        Assert.Equal(
            SubscriptionDeliveryStatus.Scheduled,
            (await harness.Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync()).Status);
    }

    [Fact]
    public async Task Vacation_IsCustomerScopedAndIgnoresIneligibleSubscriptionStates()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc),
            cutoffHours: 24);
        var mine = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 10, 15),
                days: [DayOfWeek.Monday],
                entitlement: 2),
            "subscription-a",
            CancellationToken.None);
        var foreignAddress = new CustomerAddress(
            harness.OtherCustomer.Id,
            "Home",
            "2 Other Road",
            "Central",
            "Bengaluru",
            "Karnataka",
            "560001",
            "Other Customer",
            "9888888888",
            12.9716m,
            77.5946m);
        harness.Db.CustomerAddresses.Add(foreignAddress);
        await harness.Db.SaveChangesAsync();
        var foreign = await harness.Service.CreateAsync(
            harness.OtherCustomer.Id,
            new CreateSubscriptionRequest(
                harness.Product.PublicId,
                foreignAddress.PublicId,
                1m,
                new DateOnly(2026, 10, 15),
                [DayOfWeek.Monday],
                2,
                PaymentMethod.Razorpay),
            "subscription-foreign",
            CancellationToken.None);
        // A cancelled subscription of the same customer must be ignored.
        var cancelled = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 10, 15),
                days: [DayOfWeek.Wednesday],
                entitlement: 2),
            "subscription-cancelled",
            CancellationToken.None);
        await harness.Service.CancelAsync(
            harness.Customer.Id, cancelled.Subscription.PublicId, CancellationToken.None);
        await harness.Db.Subscriptions
            .Where(x => x.PublicId == mine.Subscription.PublicId || x.PublicId == foreign.Subscription.PublicId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Status, SubscriptionStatus.Active));
        harness.Db.ChangeTracker.Clear();

        var result = await harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 31)),
            CancellationToken.None);

        // Only the customer's active subscription occurrences are skipped.
        Assert.Equal(2, result.SkippedCount);
        Assert.All(result.SkippedDates, item =>
            Assert.Equal(mine.Subscription.PublicId, item.SubscriptionId));
        var foreignSubscriptionId = await harness.Db.Subscriptions
            .AsNoTracking()
            .Where(s => s.PublicId == foreign.Subscription.PublicId)
            .Select(s => s.Id)
            .SingleAsync();
        var foreignStatuses = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .Where(x => x.SubscriptionId == foreignSubscriptionId)
            .ToListAsync();
        Assert.All(foreignStatuses, x =>
            Assert.Equal(SubscriptionDeliveryStatus.Scheduled, x.Status));
    }

    [Fact]
    public async Task Skip_SingleOccurrenceWithMaterializedDeliveryIsRejected()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc),
            cutoffHours: 24);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 10, 15),
                days: [DayOfWeek.Thursday],
                entitlement: 1),
            "subscription-a",
            CancellationToken.None);
        await harness.ActivateAsync();
        var delivery = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .SingleAsync();
        var branchId = await harness.Db.Subscriptions
            .AsNoTracking()
            .Where(s => s.PublicId == created.Subscription.PublicId)
            .Select(s => s.BranchId)
            .SingleAsync();
        harness.Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
            delivery.Id,
            harness.Customer.Id,
            branchId,
            delivery.ScheduledDate,
            "SUB-TEST",
            "Customer",
            "9999999999",
            "1 Main Road, Central, Bengaluru, Karnataka 560001",
            null,
            12.9716m,
            77.5946m));
        await harness.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.SkipAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new SkipSubscriptionDeliveryRequest(delivery.PublicId),
            CancellationToken.None));

        Assert.Contains(
            "already being prepared",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            SubscriptionDeliveryStatus.Scheduled,
            (await harness.Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync()).Status);
    }

    [Fact]
    public async Task Vacation_ReportsMaterializedDeliveryAsIneligible()
    {
        await using var harness = await SubscriptionHarness.CreateAsync(
            utcNow: new DateTime(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc),
            cutoffHours: 24);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(
                startDate: new DateOnly(2026, 10, 15),
                days: [DayOfWeek.Thursday],
                entitlement: 2),
            "subscription-a",
            CancellationToken.None);
        await harness.ActivateAsync();
        var deliveries = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .ToListAsync();
        var branchId = await harness.Db.Subscriptions
            .AsNoTracking()
            .Where(s => s.PublicId == created.Subscription.PublicId)
            .Select(s => s.BranchId)
            .SingleAsync();
        harness.Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
            deliveries[0].Id,
            harness.Customer.Id,
            branchId,
            deliveries[0].ScheduledDate,
            "SUB-TEST",
            "Customer",
            "9999999999",
            "1 Main Road, Central, Bengaluru, Karnataka 560001",
            null,
            12.9716m,
            77.5946m));
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 31)),
            CancellationToken.None);

        // 15 Oct occurrence has a materialized Delivery → reported, kept Scheduled.
        var prepared = Assert.Single(result.Ineligible, x => x.Reason == "deliveryPrepared");
        Assert.Equal(deliveries[0].ScheduledDate, prepared.Date);
        Assert.Equal(
            SubscriptionDeliveryStatus.Scheduled,
            (await harness.Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.Id == deliveries[0].Id)).Status);
        // The later occurrence (Mon 26 Oct) skipped normally.
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(
            SubscriptionDeliveryStatus.Skipped,
            (await harness.Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.Id == deliveries[1].Id)).Status);
    }

    [Fact]
    public async Task Update_RejectsChangesToGeneratedCommercialAndScheduleFields()
    {
        await using var harness = await SubscriptionHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-1", CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.UpdateAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new UpdateSubscriptionRequest(2m, null, null),
            CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.UpdateAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new UpdateSubscriptionRequest(null, null, [DayOfWeek.Friday]),
            CancellationToken.None));
    }

    private sealed class SubscriptionHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private SubscriptionHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            Product product,
            CustomerAddress address,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            CapturingSubscriptionPaymentService paymentService,
            SubscriptionService service)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Product = product;
            Address = address;
            Clock = clock;
            TimeProvider = timeProvider;
            PaymentService = paymentService;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public Product Product { get; }
        public CustomerAddress Address { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public CapturingSubscriptionPaymentService PaymentService { get; }
        public SubscriptionService Service { get; }

        public CreateSubscriptionRequest Request(
            decimal quantity = 1m,
            DateOnly? startDate = null,
            IReadOnlyCollection<DayOfWeek>? days = null,
            int entitlement = 4) =>
            new(
                Product.PublicId,
                Address.PublicId,
                quantity,
                startDate ?? new DateOnly(2026, 8, 17),
                days ?? [DayOfWeek.Monday, DayOfWeek.Wednesday],
                entitlement,
                PaymentMethod.Razorpay);

        public async Task ActivateAsync()
        {
            var subscription = await Db.Subscriptions.SingleAsync();
            subscription.Activate(TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>Activates every seeded subscription (multi-subscription tests).</summary>
        public async Task ActivateAllAsync()
        {
            var subscriptions = await Db.Subscriptions.ToListAsync();
            foreach (var subscription in subscriptions)
            {
                subscription.Activate(TimeProvider.Now);
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>Adds a second product so a second subscription can reference it.</summary>
        public async Task<Product> AddSecondProductAsync()
        {
            var product = new Product(Product.CategoryId, "GHEE-001", "Ghee", null, "litre", 700m);
            product.Activate();
            Db.Products.Add(product);
            await Db.SaveChangesAsync();
            Db.ProductBranches.Add(new ProductBranch(product.Id, Db.Entry(Product).Property(p => p.Id).CurrentValue, true, 100m));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return product;
        }

        /// <summary>
        /// Creates a second subscription for the same customer on a different
        /// product/weekday set, mirroring the harness's standard creation path.
        /// </summary>
        public async Task<CreatedSubscriptionResult> CreateSecondSubscriptionAsync(
            Product product,
            string idempotencyKey,
            DateOnly startDate,
            IReadOnlyCollection<DayOfWeek>? days = null)
        {
            var created = await Service.CreateAsync(
                Customer.Id,
                new CreateSubscriptionRequest(
                    product.PublicId,
                    Address.PublicId,
                    1m,
                    startDate,
                    days ?? [DayOfWeek.Wednesday],
                    2,
                    PaymentMethod.Razorpay),
                idempotencyKey,
                CancellationToken.None);
            await ActivateAllAsync();
            return created;
        }

        public static async Task<SubscriptionHarness> CreateAsync(
            DateTime? utcNow = null,
            int? cutoffHours = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            db.Users.AddRange(customer, otherCustomer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var address = new CustomerAddress(
                customer.Id,
                "Home",
                "1 Main Road",
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                "Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            db.AddRange(product, branch, address);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            if (cutoffHours.HasValue)
            {
                db.SystemConfigurations.Add(new SystemConfiguration(
                    "Subscription.SkipPauseCutoffHours",
                    cutoffHours.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "integer"));
            }
            await db.SaveChangesAsync();

            var clock = new TestClock(utcNow ?? new DateTime(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var paymentService = new CapturingSubscriptionPaymentService(db, clock);
            var allocation = new FixedBranchAllocationService(branch);
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var service = new SubscriptionService(
                db,
                allocation,
                paymentService,
                timeProvider,
                notificationEventWriter);
            return new SubscriptionHarness(
                connection, db, customer, otherCustomer, product, address, clock, timeProvider, paymentService, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedBranchAllocationService(Branch branch) : IBranchAllocationService
    {
        public Task<BranchAllocationResult> AllocateAsync(
            decimal latitude,
            decimal longitude,
            IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BranchAllocationResult(
                branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
    }

    private sealed class CapturingSubscriptionPaymentService(
        DoodhDirectDbContext db,
        TestClock clock) : IPaymentService
    {
        public List<(long CustomerId, long SubscriptionId, PaymentMethod Method, string IdempotencyKey)> Calls { get; } = [];

        public async Task<PaymentResult> CreateForSubscriptionAsync(
            long customerId,
            long subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls.Add((customerId, subscriptionId, method, idempotencyKey));
            var subscription = await db.Subscriptions
                .AsNoTracking()
                .SingleAsync(x => x.Id == subscriptionId, cancellationToken);
            return new PaymentResult(
                Guid.NewGuid(),
                null,
                null,
                method,
                method switch
                {
                    PaymentMethod.Wallet => "Wallet",
                    PaymentMethod.Development => "Mock",
                    _ => "Razorpay"
                },
                PaymentStatus.Pending,
                subscription.PayableAmount,
                0m,
                "INR",
                $"order_{subscription.PublicId:N}",
                null,
                "rzp_test",
                null,
                null,
                clock.UtcNow.AddMinutes(15),
                null,
                clock.UtcNow,
                subscription.PublicId);
        }

        public Task<PaymentResult> CreateWalletTopUpAsync(
            long customerId,
            decimal amount,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> RetrySubscriptionAsync(
            long customerId,
            Guid subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CompleteDevelopmentAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CancelAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CreateAsync(
            long customerId,
            CreatePaymentRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> VerifyAsync(
            long customerId,
            VerifyPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> GetAsync(
            long userId,
            Guid paymentId,
            bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentReconciliationResult> ReconcileAsync(
            long requestedByUserId,
            Guid paymentId,
            bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RefundResult> RefundAsync(
            long requestedByUserId,
            Guid paymentId,
            RefundPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ProcessWebhookAsync(
            byte[] payload,
            string signature,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
