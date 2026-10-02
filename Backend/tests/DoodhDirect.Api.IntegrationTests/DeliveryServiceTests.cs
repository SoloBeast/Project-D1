using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Dairy;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Dairy;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Microsoft.AspNetCore.DataProtection;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class DeliveryServiceTests
{
    [Fact]
    public async Task MaterializeEligible_NeverCreatesDeliveryForCommittedSkippedOccurrence()
    {
        await using var harness = await DeliveryHarness.CreateAsync();

        // A second occurrence inside the subscription term, still skip-eligible
        // under the 24h cutoff (day-after-tomorrow's delivery: its midnight
        // start minus 24h is still in the future at the harness's 09:30 now).
        var targetDate = harness.Today.AddDays(2);
        var trackedSubscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries)
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        trackedSubscription.AddDelivery(targetDate);
        await harness.Db.SaveChangesAsync();
        var tomorrowOccurrence = await harness.Db.SubscriptionDeliveries
            .SingleAsync(x => x.ScheduledDate == targetDate);

        // Customer skips the future occurrence (race simulation: the skip
        // commits before the manager's materialization runs).
        trackedSubscription.Skip(
            tomorrowOccurrence,
            harness.TimeProvider.Now,
            TimeSpan.FromHours(24));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var result = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today.AddDays(2),
            CancellationToken.None);

        // The one-time-order delivery and the still-Scheduled today occurrence
        // are created; the committed-Skipped occurrence is dropped by the
        // in-transaction re-check.
        Assert.Equal(1, result.OrdersCreated);
        Assert.Equal(1, result.SubscriptionOccurrencesCreated);
        Assert.Empty(await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.SubscriptionDeliveryId == tomorrowOccurrence.Id)
            .ToListAsync());
        Assert.Single(await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.SubscriptionDeliveryId == harness.SubscriptionDelivery.Id)
            .ToListAsync());
        // OTP exists only for real materialized work (order + today's task).
        Assert.Equal(
            2,
            await harness.Db.DeliveryOtps.AsNoTracking().CountAsync());
        Assert.Equal(
            2,
            await harness.Db.NotificationEvents.AsNoTracking()
                .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued));
    }

    [Fact]
    public async Task FetchSubscriptionDeliveries_NeverCreatesDeliveryForCommittedSkippedOccurrence()
    {
        await using var harness = await DeliveryHarness.CreateAsync();

        var targetDate = harness.Today.AddDays(2);
        var trackedSubscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries)
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        trackedSubscription.AddDelivery(targetDate);
        await harness.Db.SaveChangesAsync();
        var tomorrowOccurrenceId = await harness.Db.SubscriptionDeliveries
            .Where(x => x.ScheduledDate == targetDate)
            .Select(x => x.Id)
            .SingleAsync();
        var tomorrowOccurrence = await harness.Db.SubscriptionDeliveries
            .SingleAsync(x => x.Id == tomorrowOccurrenceId);

        trackedSubscription.Skip(
            tomorrowOccurrence,
            harness.TimeProvider.Now,
            TimeSpan.FromHours(24));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var result = await harness.Service.FetchSubscriptionDeliveriesAsync(
            harness.ManagerActor,
            harness.Today.AddDays(2),
            CancellationToken.None);

        // Only the still-Scheduled today occurrence materializes.
        Assert.Equal(0, result.OrdersCreated);
        Assert.Equal(1, result.SubscriptionOccurrencesCreated);
        Assert.Empty(await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.SubscriptionDeliveryId == tomorrowOccurrenceId)
            .ToListAsync());
        Assert.Single(await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.SubscriptionDeliveryId == harness.SubscriptionDelivery.Id)
            .ToListAsync());
    }

    [Fact]
    public async Task MaterializeEligible_WithNoSubscriptionOccurrencesOnlyCreatesOrderDelivery()
    {
        await using var harness = await DeliveryHarness.CreateAsync();

        // Sanity counterpart: with the occurrence still Scheduled the
        // materialization creates the subscription delivery and its OTP.
        var result = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today,
            CancellationToken.None);

        Assert.Equal(new DeliveryMaterializationResult(1, 1), result);
        Assert.Equal(
            1,
            await harness.Db.Deliveries.AsNoTracking()
                .CountAsync(x => x.SubscriptionDeliveryId == harness.SubscriptionDelivery.Id));
    }

    [Fact]
    public async Task MaterializeEligible_CreatesOrderAndSubscriptionDeliveriesAndIsIdempotent()
    {
        await using var harness = await DeliveryHarness.CreateAsync();

        var first = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today,
            CancellationToken.None);
        var second = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today,
            CancellationToken.None);

        Assert.Equal(new DeliveryMaterializationResult(1, 1), first);
        Assert.Equal(new DeliveryMaterializationResult(0, 0), second);
        var deliveries = await harness.Db.Deliveries
            .AsNoTracking()
            .OrderBy(x => x.SourceType)
            .ToListAsync();
        Assert.Equal(2, deliveries.Count);
        var orderDelivery = Assert.Single(deliveries, x =>
            x.SourceType == DeliverySourceType.OneTimeOrder &&
            x.OrderId == harness.Order.Id &&
            x.ScheduledDate == harness.Today);
        var subscriptionDelivery = Assert.Single(deliveries, x =>
            x.SourceType == DeliverySourceType.SubscriptionOccurrence &&
            x.SubscriptionDeliveryId == harness.SubscriptionDelivery.Id &&
            x.ScheduledDate == harness.Today);

        var otps = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .Where(x => x.DeliveryId == orderDelivery.Id || x.DeliveryId == subscriptionDelivery.Id)
            .ToListAsync();
        Assert.Equal(2, otps.Count);
        Assert.All(otps, otp =>
        {
            Assert.NotEmpty(otp.CodeHash);
            Assert.NotNull(otp.ProtectedCode);
            Assert.NotNull(otp.SentAt);
        });
        Assert.Equal(
            1,
            await harness.Db.DeliveryOtps.AsNoTracking()
                .CountAsync(x => x.DeliveryId == orderDelivery.Id));
        Assert.Equal(
            2,
            await harness.Db.NotificationEvents.AsNoTracking()
                .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued));
        Assert.Equal(2, harness.OtpDelivery.Messages.Count);
        foreach (var delivery in deliveries)
        {
            var otp = Assert.Single(otps, x => x.DeliveryId == delivery.Id);
            var code = harness.OtpProtector.Unprotect(Assert.IsType<string>(otp.ProtectedCode));
            Assert.Contains(harness.OtpDelivery.Messages, message => message.Code == code);
            Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
                .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued &&
                    x.EventKey == $"delivery:{delivery.PublicId:N}:otp-issued")
                .ToListAsync());
        }

        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "DELIVERY.MATERIALIZE")
            .ToListAsync());
    }

    [Fact]
    public async Task MaterializeEligible_RetryAfterOtpTransportFailureReusesPendingOtp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        harness.OtpDelivery.FailNextSend = true;

        await harness.MaterializeOrderAsync();

        var delivery = await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.OrderId == harness.Order.Id)
            .SingleAsync();
        var deliveryId = delivery.Id;
        var firstOtp = Assert.Single(await harness.Db.DeliveryOtps
            .AsNoTracking()
            .Where(x => x.DeliveryId == deliveryId)
            .ToListAsync());
        Assert.Null(firstOtp.SentAt);
        var firstCode = harness.OtpProtector.Unprotect(Assert.IsType<string>(firstOtp.ProtectedCode));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued &&
                x.EventKey == $"delivery:{delivery.PublicId:N}:otp-issued"));
        Assert.Equal(2, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued));

        await harness.Service.IssuePendingOtpsAsync(
            CancellationToken.None);

        var otps = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .Where(x => x.DeliveryId == deliveryId)
            .ToListAsync();
        Assert.Single(otps);
        Assert.NotNull(otps[0].SentAt);
        Assert.Equal(firstCode, Assert.Single(
            harness.OtpDelivery.Messages,
            message => message.Code == firstCode).Code);
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued &&
                x.EventKey == $"delivery:{delivery.PublicId:N}:otp-issued"));
        Assert.Equal(2, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued));
    }

    [Fact]
    public async Task ConcurrentOtpIssuance_ReusesOneOtpAndOneDeterministicEvent()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        harness.OtpDelivery.FailNextSend = true;
        var deliveryId = await harness.MaterializeOrderAsync();
        var delivery = await harness.Db.Deliveries
            .AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var initialOtp = Assert.Single(await harness.Db.DeliveryOtps
            .AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync());
        var initialEvent = Assert.Single(await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventKey == $"delivery:{deliveryId:N}:otp-issued")
            .ToListAsync());
        Assert.Null(initialOtp.SentAt);
        var initialCode = harness.OtpProtector.Unprotect(
            Assert.IsType<string>(initialOtp.ProtectedCode));
        harness.OtpDelivery.Messages.Clear();
        harness.OtpDelivery.Attempts.Clear();
        harness.OtpDelivery.BlockNextSend = true;

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateService(firstDb);
        var secondService = harness.CreateService(secondDb);

        var firstIssuance = firstService.IssuePendingOtpsAsync(CancellationToken.None);
        await harness.OtpDelivery.SendStarted.Task;
        var secondIssuance = secondService.IssuePendingOtpsAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(
            1,
            harness.OtpDelivery.Attempts.Count(x =>
                x.Destination == delivery.CustomerMobileSnapshot &&
                x.Code == initialCode));

        harness.OtpDelivery.ReleaseBlockedSend();
        var outcomes = await Task.WhenAll(
            ObserveAsync(() => firstIssuance),
            ObserveAsync(() => secondIssuance));

        Assert.All(outcomes, exception => Assert.Null(exception));
        harness.Db.ChangeTracker.Clear();
        var otps = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync();
        var events = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventKey == $"delivery:{deliveryId:N}:otp-issued")
            .ToListAsync();

        Assert.Single(otps);
        Assert.Single(events);
        Assert.Equal(initialOtp.ProtectedCode, otps[0].ProtectedCode);
        Assert.Equal(initialEvent.Id, events[0].Id);
        Assert.NotNull(otps[0].SentAt);
        Assert.Contains(
            harness.OtpDelivery.Messages,
            x => x.Destination == delivery.CustomerMobileSnapshot &&
                x.Code == initialCode);
    }

    private static async Task<Exception?> ObserveAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [Theory]
    [InlineData("2026-08-20T00:00:00", 2026, 8, 20)]
    [InlineData("2026-08-20T00:01:00", 2026, 8, 20)]
    [InlineData("2026-08-20T03:32:00", 2026, 8, 20)]
    [InlineData("2026-08-20T23:59:00", 2026, 8, 20)]
    [InlineData("2026-08-21T00:00:00", 2026, 8, 21)]
    public async Task MaterializeEligible_UsesIndiaLocalBusinessDateAtMidnightBoundaries(
        string indiaLocalTimestamp,
        int year,
        int month,
        int day)
    {
        var indiaLocalNow = DateTime.SpecifyKind(
            DateTime.Parse(indiaLocalTimestamp, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeKind.Unspecified);
        var expectedDate = new DateOnly(year, month, day);
        await using var harness = await DeliveryHarness.CreateAsync(indiaLocalNow);

        var result = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today,
            CancellationToken.None);

        Assert.Equal(expectedDate, harness.Today);
        Assert.Equal(new DeliveryMaterializationResult(1, 1), result);
        Assert.All(
            await harness.Db.Deliveries.AsNoTracking().ToListAsync(),
            delivery => Assert.Equal(expectedDate, delivery.ScheduledDate));
    }

    [Fact]
    public async Task MaterializeEligible_RestrictsBranchActorAndAllowsGlobalActor()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var otherBranchActor = new DeliveryActor(harness.Manager.Id, [harness.OtherBranch.Id]);

        var restricted = await harness.Service.MaterializeEligibleAsync(
            otherBranchActor,
            harness.Today,
            CancellationToken.None);
        var global = await harness.Service.MaterializeEligibleAsync(
            new DeliveryActor(harness.Manager.Id, [], HasGlobalAccess: true),
            harness.Today,
            CancellationToken.None);

        Assert.Equal(new DeliveryMaterializationResult(0, 0), restricted);
        Assert.Equal(new DeliveryMaterializationResult(1, 1), global);
    }

    [Fact]
    public async Task MaterializeEligible_RejectsActorWithoutBranchOrGlobalScope()
    {
        await using var harness = await DeliveryHarness.CreateAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.MaterializeEligibleAsync(
                new DeliveryActor(harness.Manager.Id, []),
                harness.Today,
                CancellationToken.None));

        Assert.Contains("branch assignment", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BranchReads_SourceAndSlotFiltersMapOperationalResults()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        await harness.MaterializeAsync();

        var all = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            null,
            DeliveryStatus.ReadyForAssignment,
            null,
            null,
            CancellationToken.None);
        var oneTime = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            null,
            DeliveryStatus.ReadyForAssignment,
            DeliverySourceType.OneTimeOrder,
            null,
            CancellationToken.None);
        var subscription = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            null,
            DeliveryStatus.ReadyForAssignment,
            DeliverySourceType.SubscriptionOccurrence,
            SubscriptionDeliverySlot.Morning,
            CancellationToken.None);
        var evening = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            null,
            DeliveryStatus.ReadyForAssignment,
            null,
            SubscriptionDeliverySlot.Evening,
            CancellationToken.None);

        Assert.Equal(2, all.Count);
        var orderDelivery = Assert.Single(oneTime);
        Assert.Equal(DeliverySourceType.OneTimeOrder, orderDelivery.SourceType);
        Assert.NotNull(orderDelivery.OrderSummary);
        Assert.Equal(harness.Order.OrderNumber, orderDelivery.OrderSummary.OrderNumber);
        Assert.Equal(2m, orderDelivery.OrderSummary.TotalQuantity);
        Assert.Equal(80m, orderDelivery.OrderSummary.TotalAmount);
        Assert.Equal(["Fresh Milk x 2 litre"], orderDelivery.OrderSummary.Items);

        var subscriptionDelivery = Assert.Single(subscription);
        Assert.Equal(DeliverySourceType.SubscriptionOccurrence, subscriptionDelivery.SourceType);
        Assert.Equal(SubscriptionDeliverySlot.Morning, subscriptionDelivery.SubscriptionSlot);
        Assert.Equal(1m, subscriptionDelivery.Quantity);
        Assert.Empty(evening);
    }

    [Fact]
    public async Task FetchSubscriptionDeliveries_SlotFilterOnlyMaterializesMatchingOccurrences()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var subscription = await harness.Db.Subscriptions
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        subscription.AddDelivery(harness.Today.AddDays(1), SubscriptionDeliverySlot.Evening);
        await harness.Db.SaveChangesAsync();

        var fetched = await harness.Service.FetchSubscriptionDeliveriesAsync(
            harness.ManagerActor,
            harness.Today.AddDays(1),
            SubscriptionDeliverySlot.Evening,
            CancellationToken.None);

        Assert.Equal(new DeliveryMaterializationResult(0, 1), fetched);
        var deliveries = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            null,
            null,
            DeliverySourceType.SubscriptionOccurrence,
            null,
            CancellationToken.None);
        var delivery = Assert.Single(deliveries);
        Assert.Equal(harness.Today.AddDays(1), delivery.ScheduledDate);
        Assert.Equal(SubscriptionDeliverySlot.Evening, delivery.SubscriptionSlot);
        Assert.Equal(1m, delivery.Quantity);
    }

    [Fact]
    public async Task SubscriptionGenerationWindow_IncludesLastAllowedDate()
    {
        await using var harness = await DeliveryHarness.CreateAsync(
            subscriptionGenerationWindowDays: 3);
        var subscription = await harness.Db.Subscriptions
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        subscription.AddDelivery(
            harness.Today.AddDays(2),
            SubscriptionDeliverySlot.Evening);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.FetchSubscriptionDeliveriesAsync(
            harness.ManagerActor,
            harness.Today.AddDays(2),
            CancellationToken.None);

        Assert.Equal(new DeliveryMaterializationResult(0, 2), result);
        Assert.Equal(
            [harness.Today, harness.Today.AddDays(2)],
            await harness.Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SourceType == DeliverySourceType.SubscriptionOccurrence)
                .OrderBy(x => x.ScheduledDate)
                .Select(x => x.ScheduledDate)
                .ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscriptionGenerationWindow_RejectsDateBeyondConfiguredWindowWithoutMutation(
        bool subscriptionOnly)
    {
        await using var harness = await DeliveryHarness.CreateAsync(
            subscriptionGenerationWindowDays: 3);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            subscriptionOnly
                ? harness.Service.FetchSubscriptionDeliveriesAsync(
                    harness.ManagerActor,
                    harness.Today.AddDays(3),
                    CancellationToken.None)
                : harness.Service.MaterializeEligibleAsync(
                    harness.ManagerActor,
                    harness.Today.AddDays(3),
                    CancellationToken.None));

        Assert.Equal("throughDate", exception.Field);
        Assert.Contains("next 3 days", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task BulkAssign_AssignsAllSelectedDeliveriesAtomically()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        await harness.MaterializeAsync();
        var deliveryIds = await harness.Db.Deliveries
            .AsNoTracking()
            .Select(x => x.PublicId)
            .ToArrayAsync();

        var result = await harness.Service.BulkAssignAsync(
            harness.ManagerActor,
            new BulkAssignDeliveriesRequest(deliveryIds, harness.Staff.PublicId, "Morning route"),
            CancellationToken.None);

        Assert.Equal(2, result.Deliveries.Count);
        Assert.All(result.Deliveries, delivery =>
        {
            Assert.Equal(DeliveryStatus.Assigned, delivery.Status);
            Assert.Equal(harness.Staff.PublicId, delivery.AssignedEmployeeId);
        });
        Assert.Equal(OrderStatus.Assigned,
            (await harness.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == harness.Order.Id)).Status);
        Assert.Equal(2, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.ASSIGN"));
        Assert.Equal(2, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryAssigned));
        Assert.Equal(2, harness.Realtime.Deliveries.Count);
    }

    [Fact]
    public async Task BulkAssign_RejectsNonReadySelectionWithoutPartialMutation()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        await harness.MaterializeAsync();
        var deliveryIds = await harness.Db.Deliveries
            .AsNoTracking()
            .OrderBy(x => x.SourceType)
            .Select(x => x.PublicId)
            .ToArrayAsync();
        await harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryIds[0],
            new AssignDeliveryRequest(harness.Staff.PublicId, "Already assigned"),
            CancellationToken.None);
        harness.Realtime.Deliveries.Clear();
        var auditCount = await harness.Db.AuditLogs.AsNoTracking().CountAsync();
        var eventCount = await harness.Db.NotificationEvents.AsNoTracking().CountAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.BulkAssignAsync(
            harness.ManagerActor,
            new BulkAssignDeliveriesRequest(deliveryIds, harness.SecondStaff.PublicId, null),
            CancellationToken.None));

        var second = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryIds[1]);
        Assert.Equal(DeliveryStatus.ReadyForAssignment, second.Status);
        Assert.Null(second.AssignedEmployeeId);
        Assert.Equal(auditCount, await harness.Db.AuditLogs.AsNoTracking().CountAsync());
        Assert.Equal(eventCount, await harness.Db.NotificationEvents.AsNoTracking().CountAsync());
        Assert.Empty(harness.Realtime.Deliveries);
    }

    [Fact]
    public async Task BranchReads_UsesIndiaLocalDeliveryDateAt0332IstRegression()
    {
        await using var harness = await DeliveryHarness.CreateAsync(
            new DateTime(2026, 8, 20, 3, 32, 0, DateTimeKind.Unspecified));
        var indiaLocalDate = new DateOnly(2026, 8, 20);

        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            indiaLocalDate,
            CancellationToken.None);

        var deliveries = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            indiaLocalDate,
            DeliveryStatus.ReadyForAssignment,
            CancellationToken.None);

        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, delivery => Assert.Equal(indiaLocalDate, delivery.ScheduledDate));
    }

    [Fact]
    public async Task BranchReads_HideResourcesOutsideActorScope()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        await harness.MaterializeAsync();
        var otherBranchActor = new DeliveryActor(harness.Manager.Id, [harness.OtherBranch.Id]);

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetForBranchAsync(
            otherBranchActor,
            harness.Branch.Id,
            harness.Today,
            null,
            CancellationToken.None));

        var visible = await harness.Service.GetForBranchAsync(
            harness.ManagerActor,
            harness.Branch.Id,
            harness.Today,
            DeliveryStatus.ReadyForAssignment,
            CancellationToken.None);
        Assert.Equal(2, visible.Count);
    }

    [Fact]
    public async Task Assign_RequiresActiveDeliveryStaffInDeliveryBranch()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        var wrongBranch = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.OtherBranchStaff.PublicId, null),
            CancellationToken.None));
        var inactive = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.InactiveStaff.PublicId, null),
            CancellationToken.None));

        Assert.Equal("employeeId", wrongBranch.Field);
        Assert.Equal("employeeId", inactive.Field);
        var delivery = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.ReadyForAssignment, delivery.Status);
        Assert.Null(delivery.AssignedEmployeeId);
    }

    [Fact]
    public async Task AssignAndReassign_BeforePickupPersistHistoryAndSynchronizeOrder()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        var assigned = await harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.Staff.PublicId, "Initial route"),
            CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var reassigned = await harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.SecondStaff.PublicId, "Coverage change"),
            CancellationToken.None);

        Assert.Equal(harness.Staff.PublicId, assigned.AssignedEmployeeId);
        Assert.Equal(harness.SecondStaff.PublicId, reassigned.AssignedEmployeeId);
        Assert.Equal(2, reassigned.Assignments.Count);
        Assert.Equal(["Initial route", "Coverage change"], reassigned.Assignments.Select(x => x.Reason));
        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries
            .AsNoTracking()
            .Include(x => x.Assignments)
            .SingleAsync(x => x.PublicId == deliveryId);
        var order = await harness.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == harness.Order.Id);
        Assert.Equal(DeliveryStatus.Assigned, delivery.Status);
        Assert.Equal(harness.SecondStaff.Id, delivery.AssignedEmployeeId);
        Assert.Equal(2, delivery.Assignments.Count);
        Assert.Equal(OrderStatus.Assigned, order.Status);
        Assert.Equal(2, harness.Realtime.Deliveries.Count);
        Assert.Equal(2, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.ASSIGN" || x.Action == "DELIVERY.REASSIGN"));
        var assignedEvents = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryAssigned)
            .OrderBy(x => x.OccurredAt)
            .ToListAsync();
        Assert.Equal(2, assignedEvents.Count);
        Assert.Equal(
            [
                $"delivery:{deliveryId:N}:assigned:{assigned.AssignedAt!.Value.Ticks}",
                $"delivery:{deliveryId:N}:assigned:{reassigned.AssignedAt!.Value.Ticks}"
            ],
            assignedEvents.Select(x => x.EventKey));
        Assert.All(assignedEvents, notificationEvent =>
        {
            Assert.Equal(harness.Customer.Id, notificationEvent.UserId);
            Assert.True(notificationEvent.IsCritical);
            Assert.Equal(
                $"/deliveries/{deliveryId}",
                Payload(notificationEvent).GetProperty("DeepLink").GetString());
            Assert.Equal(
                deliveryId.ToString(),
                Variables(notificationEvent).GetProperty("deliveryId").GetString());
        });
        Assert.Equal(
            harness.Staff.PublicId.ToString(),
            Variables(assignedEvents[0]).GetProperty("employeeId").GetString());
        Assert.Equal(
            harness.SecondStaff.PublicId.ToString(),
            Variables(assignedEvents[1]).GetProperty("employeeId").GetString());
    }

    [Fact]
    public async Task StaffTransitions_HideDeliveryFromEmployeeWhoIsNotAssigned()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.PickUpAsync(
            harness.StaffActor(harness.SecondStaff),
            deliveryId,
            new DeliveryNotesRequest(null),
            CancellationToken.None));

        var delivery = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Assigned, delivery.Status);
    }

    [Fact]
    public async Task OrderWorkflow_RequiresOtpAndSynchronizesDeliveredStatus()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var orderWorkflowBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, orderWorkflowBatchId, 2m);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest(null),
            CancellationToken.None));

        var targetDeliveryId = await harness.Db.Deliveries
            .AsNoTracking()
            .Where(x => x.PublicId == deliveryId)
            .Select(x => x.Id)
            .SingleAsync();
        var issuedOtp = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .SingleAsync(x => x.DeliveryId == targetDeliveryId);
        var issuedCode = harness.OtpProtector.Unprotect(Assert.IsType<string>(issuedOtp.ProtectedCode));
        var message = Assert.Single(harness.OtpDelivery.Messages, x => x.Code == issuedCode);
        Assert.Equal("9999999999", message.Destination);
        var invalid = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest("000000" == message.Code ? "111111" : "000000"),
            CancellationToken.None));
        Assert.Equal("code", invalid.Field);

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(message.Code),
            CancellationToken.None);

        Assert.NotNull(verified.OtpVerifiedAt);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        Assert.False(verified.IsTrackingActive);
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(message.Code),
            CancellationToken.None));
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.Delivered,
            (await harness.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == harness.Order.Id)).Status);
        var otp = await harness.Db.DeliveryOtps
            .AsNoTracking()
            .SingleAsync(x => x.DeliveryId == targetDeliveryId);
        Assert.Equal(1, otp.AttemptCount);
        Assert.NotNull(otp.ConsumedAt);

        Assert.Equal(1, harness.Realtime.Deliveries.Count(x => x.Status == DeliveryStatus.Delivered));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.COMPLETE"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryCompleted));

        var eventKeyPrefix = $"delivery:{deliveryId:N}:";
        var events = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventKey.StartsWith(eventKeyPrefix))
            .OrderBy(x => x.EventType)
            .ToListAsync();
        Assert.Equal(5, events.Count);
        Assert.All(events, notificationEvent =>
        {
            Assert.Equal(harness.Customer.Id, notificationEvent.UserId);
            Assert.True(notificationEvent.IsCritical);
            Assert.Equal(harness.TimeProvider.Now, notificationEvent.OccurredAt);
            Assert.Equal(
                $"/deliveries/{deliveryId}",
                Payload(notificationEvent).GetProperty("DeepLink").GetString());
            Assert.Equal(
                harness.Order.OrderNumber,
                Variables(notificationEvent).GetProperty("referenceNumber").GetString());
        });
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.DeliveryOtpIssued &&
            notificationEvent.EventKey == $"delivery:{deliveryId:N}:otp-issued");
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.DeliveryAssigned &&
            notificationEvent.EventKey ==
                $"delivery:{deliveryId:N}:assigned:{harness.TimeProvider.Now.Ticks}");
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.DeliveryStarted &&
            notificationEvent.EventKey ==
                $"delivery:{deliveryId:N}:started:{harness.TimeProvider.Now.Ticks}");
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.DeliveryNearCustomer &&
            notificationEvent.EventKey ==
                $"delivery:{deliveryId:N}:near-customer:{harness.TimeProvider.Now.Ticks}");
        Assert.Contains(events, notificationEvent =>
            notificationEvent.EventType == NotificationEventTypes.DeliveryCompleted &&
            notificationEvent.EventKey ==
                $"delivery:{deliveryId:N}:completed:{harness.TimeProvider.Now.Ticks}");
    }

    [Fact]
    public async Task SubscriptionOtpVerification_CompletesOccurrenceAndConsumesEntitlementExactlyOnce()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var subscriptionAllocationBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, subscriptionAllocationBatchId, 1m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        Assert.NotNull(verified.OtpVerifiedAt);
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        var occurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.Id == harness.SubscriptionDelivery.Id);
        var subscription = await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        Assert.Equal(SubscriptionDeliveryStatus.Delivered, occurrence.Status);
        Assert.Equal(1, subscription.UsedEntitlement);
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.COMPLETE"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryCompleted));
        Assert.Equal(1, harness.Realtime.Deliveries.Count(x =>
            x.DeliveryId == deliveryId && x.Status == DeliveryStatus.Delivered));
        var usage = Assert.Single(await harness.Db.MilkUsages.AsNoTracking().ToListAsync());
        Assert.Equal(MilkUsageSource.Subscription, usage.Source);
        Assert.Equal("Automatic Subscription Consumption", usage.Purpose);
        Assert.Equal(harness.SubscriptionDelivery.Id, usage.SubscriptionDeliveryId);
        Assert.Equal(1m, usage.QuantityUsed);
        Assert.Equal(harness.Branch.Id, usage.BranchId);
        Assert.Equal(subscriptionAllocationBatchId, usage.BatchId);
        Assert.NotNull(usage.DeliveryBatchAllocationId);
    }

    [Fact]
    public async Task OtpVerification_RemainsValidPastLegacyExpiryAndRejectsConsumedOrAttemptLimitedCodes()
    {
        await using var activeHarness = await DeliveryHarness.CreateAsync();
        var activeDeliveryId = await activeHarness.MaterializeOrderAsync();
        await activeHarness.AdvanceToArrivedAsync(activeDeliveryId, activeHarness.Staff);
        var activeAllocationBatchId = await activeHarness.RecordMilkBatchAsync();
        await activeHarness.SaveAllocationAsync(activeDeliveryId, activeAllocationBatchId, 2m);
        var activeCode = await activeHarness.GetOtpCodeAsync(activeDeliveryId);
        activeHarness.Clock.Advance(TimeSpan.FromMinutes(11));

        var verified = await activeHarness.Service.VerifyOtpAsync(
            activeHarness.StaffActor(activeHarness.Staff),
            activeDeliveryId,
            new VerifyDeliveryOtpRequest(activeCode),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        var activeDeliveryKey = await activeHarness.Db.Deliveries.AsNoTracking()
            .Where(x => x.PublicId == activeDeliveryId)
            .Select(x => x.Id)
            .SingleAsync();
        var activeOtp = await activeHarness.Db.DeliveryOtps.AsNoTracking()
            .SingleAsync(x => x.DeliveryId == activeDeliveryKey);
        Assert.NotNull(activeOtp.ConsumedAt);
        Assert.Null(activeOtp.ProtectedCode);

        await using var consumedHarness = await DeliveryHarness.CreateAsync();
        var consumedDeliveryId = await consumedHarness.MaterializeOrderAsync();
        await consumedHarness.AdvanceToArrivedAsync(consumedDeliveryId, consumedHarness.Staff);
        var consumedDeliveryKey = await consumedHarness.Db.Deliveries.AsNoTracking()
            .Where(x => x.PublicId == consumedDeliveryId)
            .Select(x => x.Id)
            .SingleAsync();
        var consumedOtp = await consumedHarness.Db.DeliveryOtps
            .SingleAsync(x => x.DeliveryId == consumedDeliveryKey);
        consumedOtp.Consume(consumedHarness.TimeProvider.Now);
        await consumedHarness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => consumedHarness.Service.VerifyOtpAsync(
            consumedHarness.StaffActor(consumedHarness.Staff),
            consumedDeliveryId,
            new VerifyDeliveryOtpRequest("482913"),
            CancellationToken.None));
        Assert.Equal(DeliveryStatus.Arrived, (await consumedHarness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == consumedDeliveryId)).Status);

        await using var limitedHarness = await DeliveryHarness.CreateAsync();
        var limitedDeliveryId = await limitedHarness.MaterializeOrderAsync();
        await limitedHarness.AdvanceToArrivedAsync(limitedDeliveryId, limitedHarness.Staff);
        var correctCode = await limitedHarness.GetOtpCodeAsync(limitedDeliveryId);
        var wrongCode = correctCode == "000000" ? "111111" : "000000";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<ValidationAppException>(() => limitedHarness.Service.VerifyOtpAsync(
                limitedHarness.StaffActor(limitedHarness.Staff),
                limitedDeliveryId,
                new VerifyDeliveryOtpRequest(wrongCode),
                CancellationToken.None));
        }

        var limited = await Assert.ThrowsAsync<BusinessRuleException>(() => limitedHarness.Service.VerifyOtpAsync(
            limitedHarness.StaffActor(limitedHarness.Staff),
            limitedDeliveryId,
            new VerifyDeliveryOtpRequest(correctCode),
            CancellationToken.None));
        Assert.Contains("attempt limit", limited.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DeliveryStatus.Arrived, (await limitedHarness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == limitedDeliveryId)).Status);
        Assert.Equal(0, await limitedHarness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.COMPLETE"));
    }

    [Fact]
    public async Task OtpVerification_DownstreamFailureRollsBackAllCompletionMutations()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var rollbackAllocationBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, rollbackAllocationBatchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        await using var failingDb = harness.CreateContext();
        var failingService = harness.CreateService(
            failingDb,
            notificationEventWriter: new ThrowingNotificationEventWriter(NotificationEventTypes.DeliveryCompleted));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failingService.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var otp = await harness.Db.DeliveryOtps.AsNoTracking()
            .SingleAsync(x => x.DeliveryId == delivery.Id);
        Assert.Equal(DeliveryStatus.Arrived, delivery.Status);
        Assert.Null(delivery.OtpVerifiedAt);
        Assert.Null(otp.ConsumedAt);
        Assert.NotNull(otp.ProtectedCode);
        Assert.Equal(OrderStatus.OutForDelivery, (await harness.Db.Orders.AsNoTracking()
            .SingleAsync(x => x.Id == harness.Order.Id)).Status);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.OTP_SUCCESS" || x.Action == "DELIVERY.COMPLETE"));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryCompleted));
        Assert.Equal(0, harness.Realtime.Deliveries.Count(x => x.Status == DeliveryStatus.Delivered));
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        var retried = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, retried.Status);
        Assert.Equal(1, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task OtpVerification_BlockedByUnresolvedMilkTest_DoesNotConsumeOtpOrComplete()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        harness.Db.MilkTests.Add(new MilkTest(
            delivery.Id, harness.Customer.Id, harness.Branch.Id, harness.Customer.Id, harness.TimeProvider.Now));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None));
        Assert.Equal(
            "Customer test confirmation required before delivery can be completed.",
            exception.Message);

        // The serializable transaction rolls back all completion mutations: the OTP is
        // NOT consumed, the delivery is NOT marked verified/delivered, and no
        // DELIVERY.OTP_SUCCESS/DELIVERY.COMPLETE audit or notification is written.
        harness.Db.ChangeTracker.Clear();
        var unchanged = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var otp = await harness.Db.DeliveryOtps.AsNoTracking()
            .SingleAsync(x => x.DeliveryId == unchanged.Id);
        Assert.Equal(DeliveryStatus.Arrived, unchanged.Status);
        Assert.Null(unchanged.OtpVerifiedAt);
        Assert.Null(otp.ConsumedAt);
        Assert.NotNull(otp.ProtectedCode);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.OTP_SUCCESS" || x.Action == "DELIVERY.COMPLETE"));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryCompleted));
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteAsync_BlockedByUnresolvedMilkTest()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);

        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        harness.Db.MilkTests.Add(new MilkTest(
            delivery.Id, harness.Customer.Id, harness.Branch.Id, harness.Customer.Id, harness.TimeProvider.Now));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest("Delivered manually"),
            CancellationToken.None));
        Assert.Equal(
            "Customer test confirmation required before delivery can be completed.",
            exception.Message);

        harness.Db.ChangeTracker.Clear();
        var unchanged = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Arrived, unchanged.Status);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.COMPLETE"));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryCompleted));
    }

    [Fact]
    public async Task OtpVerification_AllowedWhenMilkTestCancelledByCustomer()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var milkTest = new MilkTest(
            delivery.Id, harness.Customer.Id, harness.Branch.Id, harness.Customer.Id, harness.TimeProvider.Now);
        milkTest.Cancel(harness.TimeProvider.Now, "No longer needed");
        harness.Db.MilkTests.Add(milkTest);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);

        var completed = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Delivered, completed.Status);
        Assert.NotNull(completed.OtpVerifiedAt);
        Assert.Equal(1, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ConcurrentCorrectOtpVerification_CompletesExactlyOnce()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var concurrentAllocationBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, concurrentAllocationBatchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateService(firstDb);
        var secondService = harness.CreateService(secondDb);

        var requests = new[]
        {
            firstService.VerifyOtpAsync(
                harness.StaffActor(harness.Staff),
                deliveryId,
                new VerifyDeliveryOtpRequest(code),
                CancellationToken.None),
            secondService.VerifyOtpAsync(
                harness.StaffActor(harness.Staff),
                deliveryId,
                new VerifyDeliveryOtpRequest(code),
                CancellationToken.None)
        };
        var outcomes = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                return (Result: await request, Error: (Exception?)null);
            }
            catch (Exception exception)
            {
                return (Result: (DeliveryResult?)null, Error: exception);
            }
        }));

        Assert.Single(outcomes, x => x.Result?.Status == DeliveryStatus.Delivered);
        Assert.Single(outcomes, x => x.Error is BusinessRuleException);
        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var otp = await harness.Db.DeliveryOtps.AsNoTracking()
            .SingleAsync(x => x.DeliveryId == delivery.Id);
        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
        Assert.NotNull(delivery.OtpVerifiedAt);
        Assert.NotNull(otp.ConsumedAt);
        Assert.Equal(OrderStatus.Delivered, (await harness.Db.Orders.AsNoTracking()
            .SingleAsync(x => x.Id == harness.Order.Id)).Status);
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.OTP_SUCCESS"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.COMPLETE"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryCompleted));
        Assert.Equal(1, harness.Realtime.Deliveries.Count(x =>
            x.DeliveryId == deliveryId && x.Status == DeliveryStatus.Delivered));
        Assert.Equal(1, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task LocationTracking_IsVisibleToCustomerOnlyWhileActiveAndRejectsStaleTimestamp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        var beforeStart = await harness.Service.GetForCustomerAsync(harness.Customer.Id, deliveryId, CancellationToken.None);
        Assert.False(beforeStart.IsTrackingActive);
        Assert.Null(beforeStart.LatestLocation);

        await harness.Service.PickUpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest(null),
            CancellationToken.None);
        await harness.Service.StartAsync(harness.StaffActor(harness.Staff), deliveryId, CancellationToken.None);
        var location = await harness.Service.RecordLocationAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryLocationRequest(12.972m, 77.595m, 8m, harness.TimeProvider.Now),
            CancellationToken.None);
        var active = await harness.Service.GetForCustomerAsync(harness.Customer.Id, deliveryId, CancellationToken.None);

        Assert.True(active.IsTrackingActive);
        Assert.Equal(location, active.LatestLocation);
        Assert.Single(harness.Realtime.Locations);
        var stale = await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.RecordLocationAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryLocationRequest(12.972m, 77.595m, null, harness.TimeProvider.Now.AddMinutes(-16)),
            CancellationToken.None));
        Assert.Equal("recordedAt", stale.Field);
    }

    [Fact]
    public async Task SubscriptionFailure_SynchronizesOccurrenceWithoutConsumingEntitlement()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var failed = await harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(
                DeliveryFailureReasons.CustomerNotAvailable,
                "No response",
                12.972m,
                77.595m),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(DeliveryFailureReasons.CustomerNotAvailable, failed.FailureReason);
        harness.Db.ChangeTracker.Clear();
        var occurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.Id == harness.SubscriptionDelivery.Id);
        var subscription = await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, occurrence.Status);
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        var failedEvent = Assert.Single(
            await harness.Db.NotificationEvents
                .AsNoTracking()
                .Where(x => x.EventType == NotificationEventTypes.DeliveryFailed)
                .ToListAsync());
        Assert.Equal(harness.Customer.Id, failedEvent.UserId);
        Assert.True(failedEvent.IsCritical);
        Assert.Equal(harness.TimeProvider.Now, failedEvent.OccurredAt);
        Assert.Equal(
            $"delivery:{deliveryId:N}:failed:{harness.TimeProvider.Now.Ticks}",
            failedEvent.EventKey);
        Assert.Equal(
            DeliveryFailureReasons.CustomerNotAvailable,
            Variables(failedEvent).GetProperty("reason").GetString());
        Assert.Equal(
            $"/deliveries/{deliveryId}",
            Payload(failedEvent).GetProperty("DeepLink").GetString());
    }

    [Fact]
    public async Task AutomaticConsumption_OneTimeOrder_CreatedOnlyAfterDeliveredWithSnapshot()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        var code = await harness.GetOtpCodeAsync(deliveryId);
        var delivered = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, delivered.Status);

        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var usage = Assert.Single(await harness.Db.MilkUsages.AsNoTracking().ToListAsync());
        Assert.Equal(MilkUsageSource.Order, usage.Source);
        Assert.Equal("Automatic Order Consumption", usage.Purpose);
        Assert.Equal(harness.Branch.Id, usage.BranchId);
        Assert.Equal(2m, usage.QuantityUsed);
        Assert.Equal("L", usage.Unit);
        Assert.Equal("Fresh Milk", usage.ProductName);
        Assert.Equal(harness.Order.Id, usage.OrderId);
        Assert.Equal(harness.Order.Items.Single().Id, usage.OrderItemId);
        Assert.Equal("ORD-DEL-001", usage.OrderNumber);
        Assert.Equal(delivery.DeliveryNumber, usage.DeliveryNumber);
        Assert.Equal(delivery.Id, usage.DeliveryId);
        Assert.Null(usage.SubscriptionDeliveryId);
        Assert.Equal(batchId, usage.BatchId);
        Assert.NotNull(usage.DeliveryBatchAllocationId);
        Assert.Equal(harness.TimeProvider.Now, usage.UsedAt);
        Assert.Equal(harness.Staff.Id, usage.RecordedByUserId);
    }

    [Fact]
    public async Task AutomaticConsumption_OneTimeOrder_RetryDoesNotDuplicate()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var retryBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, retryBatchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        Assert.Equal(1, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None));

        var usage = Assert.Single(await harness.Db.MilkUsages.AsNoTracking().ToListAsync());
        Assert.Equal(2m, usage.QuantityUsed);
    }

    [Fact]
    public async Task AutomaticConsumption_FailedOrderDelivery_CreatesNone()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var failed = await harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(
                DeliveryFailureReasons.CustomerNotAvailable,
                "No response",
                12.972m,
                77.595m),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AutomaticConsumption_Subscription_ActivationNone_SingleDeliveredOccurrenceOne()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var subscriptionBatchId = await harness.RecordMilkBatchAsync(1m);
        await harness.SaveAllocationAsync(deliveryId, subscriptionBatchId, 1m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);

        var usage = Assert.Single(await harness.Db.MilkUsages.AsNoTracking().ToListAsync());
        Assert.Equal(MilkUsageSource.Subscription, usage.Source);
        Assert.Equal("Automatic Subscription Consumption", usage.Purpose);
        Assert.Equal(harness.Branch.Id, usage.BranchId);
        Assert.Equal(1m, usage.QuantityUsed);
        Assert.Equal("L", usage.Unit);
        Assert.Equal("Fresh Milk", usage.ProductName);
        Assert.Equal(harness.SubscriptionDelivery.Id, usage.SubscriptionDeliveryId);
        Assert.Equal(harness.TimeProvider.Now, usage.UsedAt);
        Assert.Null(usage.OrderId);
        Assert.Null(usage.OrderItemId);
        Assert.Null(usage.OrderNumber);
        Assert.Equal(subscriptionBatchId, usage.BatchId);
        Assert.NotNull(usage.DeliveryBatchAllocationId);
    }

    [Fact]
    public async Task AutomaticConsumption_Subscription_TwoDeliveredOccurrences_CreateTwoRecords()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var subscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries)
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        subscription.AddDelivery(harness.Today.AddDays(1));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        _ = await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor,
            harness.Today.AddDays(1),
            CancellationToken.None);

        var deliveries = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDeliveryId != null)
            .ToListAsync();
        Assert.Equal(2, deliveries.Count);
        foreach (var delivery in deliveries)
        {
            await harness.AdvanceToArrivedAsync(delivery.PublicId, harness.Staff);
            var occurrenceBatchId = await harness.RecordMilkBatchAsync(1m);
            await harness.SaveAllocationAsync(delivery.PublicId, occurrenceBatchId, 1m);
            var code = await harness.GetOtpCodeAsync(delivery.PublicId);
            await harness.Service.VerifyOtpAsync(
                harness.StaffActor(harness.Staff),
                delivery.PublicId,
                new VerifyDeliveryOtpRequest(code),
                CancellationToken.None);
        }

        var usages = await harness.Db.MilkUsages.AsNoTracking()
            .Where(x => x.Source == MilkUsageSource.Subscription)
            .ToListAsync();
        Assert.Equal(2, usages.Count);
        Assert.Equal(2, usages.Select(x => x.SubscriptionDeliveryId).Distinct().Count());
        Assert.All(usages, usage =>
        {
            Assert.Equal(1m, usage.QuantityUsed);
            Assert.Equal(harness.Branch.Id, usage.BranchId);
        });
    }

    [Fact]
    public async Task AutomaticConsumption_SameProductTwoBranches_KeepsSeparateBranchTotals()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var product = await harness.Db.Products.AsNoTracking().SingleAsync(x => x.Sku == "MILK-001");
        var address = await harness.Db.CustomerAddresses.AsNoTracking()
            .SingleAsync(x => x.UserId == harness.Customer.Id);

        var northOrder = new Order(
            harness.Customer.Id,
            address.Id,
            harness.OtherBranch.Id,
            "order-delivery-2",
            "ORD-DEL-002",
            80m,
            0m,
            harness.OtherBranch.Code,
            harness.OtherBranch.Name,
            address.Label,
            address.AddressLine1,
            address.AddressLine2,
            address.Locality,
            address.City,
            address.State,
            address.PinCode,
            address.Landmark,
            address.DeliveryInstructions,
            address.ContactName,
            address.ContactMobile,
            address.Latitude,
            address.Longitude);
        northOrder.ConfirmPayment();
        northOrder.AddItem(new OrderItem(product.Id, 3m, 40m, product.Sku, product.Name, product.UnitOfMeasure));
        harness.Db.Orders.Add(northOrder);
        await harness.Db.SaveChangesAsync();

        var bothBranches = new DeliveryActor(harness.Manager.Id, [harness.Branch.Id, harness.OtherBranch.Id]);
        _ = await harness.Service.MaterializeEligibleAsync(bothBranches, harness.Today, CancellationToken.None);

        var mainDelivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.OrderId == harness.Order.Id);
        var northDelivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.OrderId == northOrder.Id);

        await harness.AdvanceToArrivedAsync(mainDelivery.PublicId, harness.Staff);
        var mainBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(mainDelivery.PublicId, mainBatchId, 2m);
        var mainCode = await harness.GetOtpCodeAsync(mainDelivery.PublicId);
        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            mainDelivery.PublicId,
            new VerifyDeliveryOtpRequest(mainCode),
            CancellationToken.None);

        var northActor = new DeliveryActor(harness.OtherBranchStaff.Id, [harness.OtherBranch.Id]);
        await harness.Service.AssignAsync(
            northActor,
            northDelivery.PublicId,
            new AssignDeliveryRequest(harness.OtherBranchStaff.PublicId, null),
            CancellationToken.None);
        await harness.Service.PickUpAsync(
            northActor, northDelivery.PublicId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(northActor, northDelivery.PublicId, CancellationToken.None);
        await harness.Service.ArriveAsync(northActor, northDelivery.PublicId, CancellationToken.None);
        var northBatchId = await harness.RecordMilkBatchAsync(5m, harness.OtherBranch.Id);
        await harness.SaveAllocationAsync(northDelivery.PublicId, northBatchId, 3m, northActor);
        var northCode = await harness.GetOtpCodeAsync(northDelivery.PublicId);
        await harness.Service.VerifyOtpAsync(
            northActor,
            northDelivery.PublicId,
            new VerifyDeliveryOtpRequest(northCode),
            CancellationToken.None);

        var usages = await harness.Db.MilkUsages.AsNoTracking()
            .Where(x => x.Source == MilkUsageSource.Order)
            .ToListAsync();
        Assert.Equal(2, usages.Count);
        var mainUsage = usages.Single(x => x.BranchId == harness.Branch.Id);
        var northUsage = usages.Single(x => x.BranchId == harness.OtherBranch.Id);
        Assert.Equal(2m, mainUsage.QuantityUsed);
        Assert.Equal(harness.Order.Id, mainUsage.OrderId);
        Assert.Equal(3m, northUsage.QuantityUsed);
        Assert.Equal(northOrder.Id, northUsage.OrderId);
    }

    [Fact]
    public async Task Availability_ManualBatchUsage_ReducesAvailableOnce()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var production = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);

        var before = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, before.QuantityProduced);
        Assert.Equal(0m, before.QuantityUsed);
        Assert.Equal(10m, before.AvailableQuantity);

        await dairy.RecordUsageAsync(
            actor,
            production.Batch.PublicId,
            new RecordMilkUsageRequest(harness.TimeProvider.Now, 2m, "Pasteurization", null),
            CancellationToken.None);

        var after = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, after.QuantityProduced);
        Assert.Equal(2m, after.QuantityUsed);
        Assert.Equal(8m, after.AvailableQuantity);
    }

    [Fact]
    public async Task Availability_OrderDelivery_NoReductionBefore_ReducesExactlyOrderQuantityAfter()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var production = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var batchId = await harness.BatchIdAsync(production.Batch.PublicId);

        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);

        var inTransit = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, inTransit.QuantityProduced);
        Assert.Equal(0m, inTransit.QuantityUsed);
        Assert.Equal(10m, inTransit.AvailableQuantity);

        var code = await harness.GetOtpCodeAsync(deliveryId);
        var delivered = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, delivered.Status);

        var after = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, after.QuantityProduced);
        Assert.Equal(2m, after.QuantityUsed);
        Assert.Equal(8m, after.AvailableQuantity);
    }

    [Fact]
    public async Task Availability_ManualAndOrderConsumption_CountedTogether()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var production = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);

        await dairy.RecordUsageAsync(
            actor,
            production.Batch.PublicId,
            new RecordMilkUsageRequest(harness.TimeProvider.Now, 2m, "Pasteurization", null),
            CancellationToken.None);
        var batchId = await harness.BatchIdAsync(production.Batch.PublicId);

        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        var delivered = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, delivered.Status);

        var availability = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, availability.QuantityProduced);
        Assert.Equal(4m, availability.QuantityUsed);
        Assert.Equal(6m, availability.AvailableQuantity);
    }

    [Fact]
    public async Task Availability_Subscription_ActivationNoReduction_DeliveredOccurrenceReducesByQuantity()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var production = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var batchId = await harness.BatchIdAsync(production.Batch.PublicId);

        var deliveryId = await harness.MaterializeSubscriptionAsync();
        var activated = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, activated.QuantityProduced);
        Assert.Equal(0m, activated.QuantityUsed);
        Assert.Equal(10m, activated.AvailableQuantity);

        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);

        var after = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, after.QuantityProduced);
        Assert.Equal(1m, after.QuantityUsed);
        Assert.Equal(9m, after.AvailableQuantity);
    }

    [Fact]
    public async Task Availability_FailedAndSkippedSubscriptionOccurrences_CreateNoConsumption()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);

        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        var failed = await harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(
                DeliveryFailureReasons.CustomerNotAvailable,
                "No response",
                12.972m,
                77.595m),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        var subscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries)
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        subscription.AddDelivery(harness.Today.AddDays(2));
        subscription.Skip(subscription.Deliveries.Single(x => x.ScheduledDate == harness.Today.AddDays(2)), harness.TimeProvider.Now, TimeSpan.FromHours(24));
        await harness.Db.SaveChangesAsync();
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());

        var availability = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, availability.QuantityProduced);
        Assert.Equal(0m, availability.QuantityUsed);
        Assert.Equal(10m, availability.AvailableQuantity);
    }

    [Fact]
    public async Task Availability_TwoBranches_KeepsSeparateAvailabilityTotals()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var mainProduction = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var mainBatchId = await harness.BatchIdAsync(mainProduction.Batch.PublicId);
        var northProduction = await dairy.RecordProductionAsync(
            actor,
            harness.OtherBranch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                5m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var northBatchId = await harness.BatchIdAsync(northProduction.Batch.PublicId);

        var product = await harness.Db.Products.AsNoTracking().SingleAsync(x => x.Sku == "MILK-001");
        var address = await harness.Db.CustomerAddresses.AsNoTracking()
            .SingleAsync(x => x.UserId == harness.Customer.Id);
        var northOrder = new Order(
            harness.Customer.Id,
            address.Id,
            harness.OtherBranch.Id,
            "order-delivery-2",
            "ORD-DEL-002",
            80m,
            0m,
            harness.OtherBranch.Code,
            harness.OtherBranch.Name,
            address.Label,
            address.AddressLine1,
            address.AddressLine2,
            address.Locality,
            address.City,
            address.State,
            address.PinCode,
            address.Landmark,
            address.DeliveryInstructions,
            address.ContactName,
            address.ContactMobile,
            address.Latitude,
            address.Longitude);
        northOrder.ConfirmPayment();
        northOrder.AddItem(new OrderItem(product.Id, 3m, 40m, product.Sku, product.Name, product.UnitOfMeasure));
        harness.Db.Orders.Add(northOrder);
        await harness.Db.SaveChangesAsync();

        var bothBranches = new DeliveryActor(harness.Manager.Id, [harness.Branch.Id, harness.OtherBranch.Id]);
        _ = await harness.Service.MaterializeEligibleAsync(bothBranches, harness.Today, CancellationToken.None);

        var mainDelivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.OrderId == harness.Order.Id);
        var northDelivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.OrderId == northOrder.Id);

        await harness.AdvanceToArrivedAsync(mainDelivery.PublicId, harness.Staff);
        await harness.SaveAllocationAsync(mainDelivery.PublicId, mainBatchId, 2m);
        var mainCode = await harness.GetOtpCodeAsync(mainDelivery.PublicId);
        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            mainDelivery.PublicId,
            new VerifyDeliveryOtpRequest(mainCode),
            CancellationToken.None);

        var northActor = new DeliveryActor(harness.OtherBranchStaff.Id, [harness.OtherBranch.Id]);
        await harness.Service.AssignAsync(
            northActor,
            northDelivery.PublicId,
            new AssignDeliveryRequest(harness.OtherBranchStaff.PublicId, null),
            CancellationToken.None);
        await harness.Service.PickUpAsync(
            northActor, northDelivery.PublicId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(northActor, northDelivery.PublicId, CancellationToken.None);
        await harness.Service.ArriveAsync(northActor, northDelivery.PublicId, CancellationToken.None);
        await harness.SaveAllocationAsync(northDelivery.PublicId, northBatchId, 3m, northActor);
        var northCode = await harness.GetOtpCodeAsync(northDelivery.PublicId);
        await harness.Service.VerifyOtpAsync(
            northActor,
            northDelivery.PublicId,
            new VerifyDeliveryOtpRequest(northCode),
            CancellationToken.None);

        var mainAvailability = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, mainAvailability.QuantityProduced);
        Assert.Equal(2m, mainAvailability.QuantityUsed);
        Assert.Equal(8m, mainAvailability.AvailableQuantity);

        var northAvailability = await dairy.GetAvailabilityAsync(actor, harness.OtherBranch.Id, CancellationToken.None);
        Assert.Equal(5m, northAvailability.QuantityProduced);
        Assert.Equal(3m, northAvailability.QuantityUsed);
        Assert.Equal(2m, northAvailability.AvailableQuantity);
    }

    [Fact]
    public async Task Availability_NoDoubleCounting_CountsEachMilkUsageExactlyOnce()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var first = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var firstBatchId = await harness.BatchIdAsync(first.Batch.PublicId);
        await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Evening",
                12,
                5m,
                "L",
                "Fresh production"),
            CancellationToken.None);

        await dairy.RecordUsageAsync(
            actor,
            first.Batch.PublicId,
            new RecordMilkUsageRequest(harness.TimeProvider.Now, 2m, "Pasteurization", null),
            CancellationToken.None);

        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        await harness.SaveAllocationAsync(deliveryId, firstBatchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        var availability = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        // Total produced = 10 + 5 = 15. Total consumed = 2 (manual, batch-linked) + 2 (automatic order) = 4.
        // Available must be 11, NOT 15 - (2 + 4) = 9 (which would double count the manual usage).
        Assert.Equal(15m, availability.QuantityProduced);
        Assert.Equal(4m, availability.QuantityUsed);
        Assert.Equal(11m, availability.AvailableQuantity);
        Assert.Equal(4m, await harness.Db.MilkUsages.AsNoTracking().SumAsync(x => x.QuantityUsed));
    }

    [Fact]
    public async Task Availability_ArchivedBranch_HistoricalConsumptionStaysAttributed()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var production = await dairy.RecordProductionAsync(
            actor,
            harness.OtherBranch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                5m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var northBatchId = await harness.BatchIdAsync(production.Batch.PublicId);

        var product = await harness.Db.Products.AsNoTracking().SingleAsync(x => x.Sku == "MILK-001");
        var address = await harness.Db.CustomerAddresses.AsNoTracking()
            .SingleAsync(x => x.UserId == harness.Customer.Id);
        var northOrder = new Order(
            harness.Customer.Id,
            address.Id,
            harness.OtherBranch.Id,
            "order-delivery-3",
            "ORD-DEL-003",
            80m,
            0m,
            harness.OtherBranch.Code,
            harness.OtherBranch.Name,
            address.Label,
            address.AddressLine1,
            address.AddressLine2,
            address.Locality,
            address.City,
            address.State,
            address.PinCode,
            address.Landmark,
            address.DeliveryInstructions,
            address.ContactName,
            address.ContactMobile,
            address.Latitude,
            address.Longitude);
        northOrder.ConfirmPayment();
        northOrder.AddItem(new OrderItem(product.Id, 3m, 40m, product.Sku, product.Name, product.UnitOfMeasure));
        harness.Db.Orders.Add(northOrder);
        await harness.Db.SaveChangesAsync();

        var bothBranches = new DeliveryActor(harness.Manager.Id, [harness.Branch.Id, harness.OtherBranch.Id]);
        _ = await harness.Service.MaterializeEligibleAsync(bothBranches, harness.Today, CancellationToken.None);

        var northDelivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.OrderId == northOrder.Id);
        var northActor = new DeliveryActor(harness.OtherBranchStaff.Id, [harness.OtherBranch.Id]);
        await harness.Service.AssignAsync(
            northActor,
            northDelivery.PublicId,
            new AssignDeliveryRequest(harness.OtherBranchStaff.PublicId, null),
            CancellationToken.None);
        await harness.Service.PickUpAsync(
            northActor, northDelivery.PublicId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(northActor, northDelivery.PublicId, CancellationToken.None);
        await harness.Service.ArriveAsync(northActor, northDelivery.PublicId, CancellationToken.None);
        await harness.SaveAllocationAsync(northDelivery.PublicId, northBatchId, 3m, northActor);
        var northCode = await harness.GetOtpCodeAsync(northDelivery.PublicId);
        await harness.Service.VerifyOtpAsync(
            northActor,
            northDelivery.PublicId,
            new VerifyDeliveryOtpRequest(northCode),
            CancellationToken.None);

        var archivedBranch = await harness.Db.Branches.SingleAsync(x => x.Id == harness.OtherBranch.Id);
        archivedBranch.Archive(harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();

        var usage = Assert.Single(await harness.Db.MilkUsages.AsNoTracking().ToListAsync());
        Assert.Equal(harness.OtherBranch.Id, usage.BranchId);
        Assert.Equal(3m, usage.QuantityUsed);

        var produced = await harness.Db.MilkBatches.AsNoTracking()
            .Where(x => x.BranchId == harness.OtherBranch.Id)
            .SumAsync(x => x.QuantityProduced);
        var used = await harness.Db.MilkUsages.AsNoTracking()
            .Where(x => x.BranchId == harness.OtherBranch.Id)
            .SumAsync(x => x.QuantityUsed);
        Assert.Equal(5m, produced);
        Assert.Equal(3m, used);
        Assert.Equal(2m, produced - used);
    }

    [Fact]
    public async Task Availability_RetriedOrderDelivery_ConsumptionCreatedExactlyOnce()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var production = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                10m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var batchId = await harness.BatchIdAsync(production.Batch.PublicId);

        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None));

        Assert.Equal(1, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
        var availability = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(10m, availability.QuantityProduced);
        Assert.Equal(2m, availability.QuantityUsed);
        Assert.Equal(8m, availability.AvailableQuantity);
    }

    [Fact]
    public async Task Availability_ConsumptionExceedsProduction_ReportsNegativeWithoutClamping()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var dairy = new DairyService(harness.CreateContext(), harness.TimeProvider);
        var actor = new DairyActor(harness.Manager.Id, [], HasGlobalAccess: true);
        var production = await dairy.RecordProductionAsync(
            actor,
            harness.Branch.Id,
            new RecordMilkProductionRequest(
                harness.TimeProvider.Now.AddHours(-1),
                "Morning",
                12,
                2m,
                "L",
                "Fresh production"),
            CancellationToken.None);
        var batchId = await harness.BatchIdAsync(production.Batch.PublicId);

        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);

        // Allocate the full batch to the delivery BEFORE the manual usage exhausts it. The allocation
        // reserves the batch (SaveBatchAllocationsAsync sees produced quantity minus any MilkUsage, and
        // allocations do not count) but does NOT itself reduce branch availability.
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);

        // Manual usage exhausts the batch (2L used of 2L produced) — RecordUsageAsync allows a usage equal
        // to the batch's available quantity and marks the batch exhausted. Allocations are not counted in
        // the manual-usage availability ledger, so the full 2L remains recordable.
        await dairy.RecordUsageAsync(
            actor,
            production.Batch.PublicId,
            new RecordMilkUsageRequest(harness.TimeProvider.Now, 2m, "Pasteurization", null),
            CancellationToken.None);

        // Delivered order adds automatic consumption (2L) which is NOT gated on batch availability.
        var code = await harness.GetOtpCodeAsync(deliveryId);
        var delivered = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, delivered.Status);

        // Total used = 2L manual + 2L automatic = 4L, exceeding the 2L produced. The negative availability
        // must be reported as-is (NOT clamped to zero) — overselling is not prevented by this service.
        var availability = await dairy.GetAvailabilityAsync(actor, harness.Branch.Id, CancellationToken.None);
        Assert.Equal(2m, availability.QuantityProduced);
        Assert.Equal(4m, availability.QuantityUsed);
        Assert.Equal(-2m, availability.AvailableQuantity);
    }

    [Fact]
    public async Task BatchAllocations_RequiresBranchScope_OutOfBranchActorIsNotFound()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        var northActor = new DeliveryActor(harness.OtherBranchStaff.Id, [harness.OtherBranch.Id]);

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.SaveBatchAllocationsAsync(
            northActor,
            deliveryId,
            new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(batchId, 2m)]),
            CancellationToken.None));

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetBatchAllocationsAsync(
            northActor,
            deliveryId,
            CancellationToken.None));

        // A manager can save allocations before the delivery is assigned at all.
        var saved = await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        Assert.Equal(DeliveryStatus.ReadyForAssignment, saved.Status);
        Assert.Equal(1, await harness.Db.DeliveryBatchAllocations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BatchAllocations_Validation_RejectsInvalidRequests()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync(10m);

        var empty = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest([]),
                CancellationToken.None));
        Assert.Equal("At least one batch allocation is required.", empty.Message);

        var duplicate = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest(
                    [new DeliveryBatchAllocationRequest(batchId, 1m), new DeliveryBatchAllocationRequest(batchId, 1m)]),
                CancellationToken.None));
        Assert.Equal("A batch cannot be allocated more than once.", duplicate.Message);

        var zero = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(batchId, 0m)]),
                CancellationToken.None));
        Assert.Equal("Allocation quantity must be greater than zero.", zero.Message);

        var precision = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(batchId, 1.2345m)]),
                CancellationToken.None));
        Assert.Equal("Allocation quantity cannot exceed three decimal places.", precision.Message);

        var mismatch = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(batchId, 1.5m)]),
                CancellationToken.None));
        Assert.Contains("must equal the delivery requirement (2 L)", mismatch.Message);

        // None of the rejected requests may have persisted a partial allocation.
        Assert.Equal(0, await harness.Db.DeliveryBatchAllocations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BatchAllocations_Validation_RejectsBatchFromAnotherBranch()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var northBatchId = await harness.RecordMilkBatchAsync(2m, harness.OtherBranch.Id);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(northBatchId, 2m)]),
                CancellationToken.None));
        Assert.Equal("One or more selected batches do not exist for this branch.", exception.Message);
        Assert.Equal(0, await harness.Db.DeliveryBatchAllocations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BatchAllocations_OverAllocation_ThrowsBusinessRuleWithRemainingQuantity()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        // The batch only holds 1L, so the full 2L requirement cannot be met by it alone.
        // The request total (2L) matches the delivery requirement so the over-allocation
        // check (not the total-mismatch validation) is what rejects it.
        var batchId = await harness.RecordMilkBatchAsync(1m);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(batchId, 2m)]),
                CancellationToken.None));
        Assert.Contains("has only 1 L available to allocate", exception.Message);
        Assert.Contains("requested 2 L", exception.Message);
        Assert.Equal(0, await harness.Db.DeliveryBatchAllocations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BatchAllocations_Get_ReportsEligibleAvailabilityAndCrossDeliveryReservations()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var orderDeliveryId = await harness.MaterializeOrderAsync();
        var subscriptionDeliveryId = await harness.MaterializeSubscriptionAsync();
        var firstBatchId = await harness.RecordMilkBatchAsync(2m);
        var secondBatchId = await harness.RecordMilkBatchAsync(2m);

        var before = await harness.Service.GetBatchAllocationsAsync(
            harness.ManagerActor, orderDeliveryId, CancellationToken.None);
        Assert.Equal(2m, before.TotalRequiredQuantity);
        Assert.Equal(2, before.EligibleBatches.Count);
        Assert.All(before.EligibleBatches, b => Assert.Equal(2m, b.QuantityAvailable));
        Assert.Empty(before.Allocations);

        await harness.SaveAllocationAsync(orderDeliveryId, firstBatchId, 2m);

        // The other delivery must see the first delivery's pending allocation as a reservation.
        var forSubscription = await harness.Service.GetBatchAllocationsAsync(
            harness.ManagerActor, subscriptionDeliveryId, CancellationToken.None);
        Assert.Equal(1m, forSubscription.TotalRequiredQuantity);
        Assert.Equal(0m, forSubscription.EligibleBatches.Single(x => x.BatchId == firstBatchId).QuantityAvailable);
        Assert.Equal(2m, forSubscription.EligibleBatches.Single(x => x.BatchId == secondBatchId).QuantityAvailable);

        // The owning delivery's own allocation is NOT double-counted in its own eligibility.
        var forOrder = await harness.Service.GetBatchAllocationsAsync(
            harness.ManagerActor, orderDeliveryId, CancellationToken.None);
        Assert.Equal(2m, forOrder.EligibleBatches.Single(x => x.BatchId == firstBatchId).QuantityAvailable);
        var allocation = Assert.Single(forOrder.Allocations);
        Assert.Equal(firstBatchId, allocation.BatchId);
        Assert.Equal(2m, allocation.QuantityAllocated);

        var overReserved = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                subscriptionDeliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(firstBatchId, 1m)]),
                CancellationToken.None));
        Assert.Contains("has only 0 L available to allocate", overReserved.Message);
    }

    [Fact]
    public async Task BatchAllocations_Replace_SwapsToNewBatchAndPersistsExactlyOneSet()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var firstBatchId = await harness.RecordMilkBatchAsync(2m);
        var secondBatchId = await harness.RecordMilkBatchAsync(2m);

        await harness.SaveAllocationAsync(deliveryId, firstBatchId, 2m);
        Assert.Equal(1, await harness.Db.DeliveryBatchAllocations.AsNoTracking().CountAsync());

        await harness.SaveAllocationAsync(deliveryId, secondBatchId, 2m);

        var persisted = Assert.Single(await harness.Db.DeliveryBatchAllocations.AsNoTracking().ToListAsync());
        Assert.Equal(secondBatchId, persisted.BatchId);
        Assert.Equal(2m, persisted.QuantityAllocated);
        Assert.Equal(2, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.BATCH_ALLOCATIONS"));
    }

    [Fact]
    public async Task BatchAllocations_Immutability_AfterDeliveredOrFailed_RejectsChanges()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var orderDeliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(orderDeliveryId, harness.Staff);
        var orderBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(orderDeliveryId, orderBatchId, 2m);
        var code = await harness.GetOtpCodeAsync(orderDeliveryId);
        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            orderDeliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        var delivered = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                orderDeliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(orderBatchId, 2m)]),
                CancellationToken.None));
        Assert.Contains("cannot be changed after a delivery has been completed or failed", delivered.Message);

        var subscriptionDeliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(subscriptionDeliveryId, harness.Staff);
        var failedBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(subscriptionDeliveryId, failedBatchId, 1m);
        await harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            subscriptionDeliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, "Test", null, null),
            CancellationToken.None);

        var failed = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                subscriptionDeliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(failedBatchId, 1m)]),
                CancellationToken.None));
        Assert.Contains("cannot be changed after a delivery has been completed or failed", failed.Message);
    }

    [Fact]
    public async Task BatchAllocations_ConcurrentManagers_CannotBothOverAllocateSharedBatch()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var orderDeliveryId = await harness.MaterializeOrderAsync();
        var subscriptionDeliveryId = await harness.MaterializeSubscriptionAsync();
        var sharedBatchId = await harness.RecordMilkBatchAsync(2m);

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateService(firstDb);
        var secondService = harness.CreateService(secondDb);

        var requests = new[]
        {
            firstService.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                orderDeliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(sharedBatchId, 2m)]),
                CancellationToken.None),
            secondService.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                subscriptionDeliveryId,
                new SaveDeliveryBatchAllocationsRequest([new DeliveryBatchAllocationRequest(sharedBatchId, 1m)]),
                CancellationToken.None)
        };
        var outcomes = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                return (Result: await request, Error: (Exception?)null);
            }
            catch (Exception exception)
            {
                return (Result: (DeliveryResult?)null, Error: exception);
            }
        }));

        // Exactly one manager may commit; the other is rejected either by the
        // reservation business rule or by SQLite's serializable lock.
        Assert.Single(outcomes, x => x.Result is not null);
        Assert.Single(outcomes, x => x.Error is not null);
        var loser = outcomes.Single(x => x.Error is not null);
        if (loser.Error is BusinessRuleException businessRule)
        {
            Assert.Contains("available to allocate", businessRule.Message);
        }

        harness.Db.ChangeTracker.Clear();
        var winner = Assert.Single(await harness.Db.DeliveryBatchAllocations.AsNoTracking().ToListAsync());
        Assert.True(winner.QuantityAllocated is 1m or 2m);
        Assert.True(winner.QuantityAllocated <= 2m);
    }

    [Fact]
    public async Task BranchInspectionRead_ExposesDeliveryPerformerToBranchReader()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);

        var result = await harness.Service.GetForOperationsAsync(
            harness.ManagerActor,
            deliveryId,
            requireAssignment: false,
            CancellationToken.None);

        Assert.Equal(harness.Staff.PublicId, result.AssignedEmployeeId);
        Assert.Equal("Delivery Staff One", result.AssignedEmployeeName);
    }

    [Fact]
    public async Task BranchInspectionRead_OutOfBranchActorCannotSeeDeliveryPerformer()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        var otherBranchActor = new DeliveryActor(harness.Manager.Id, [harness.OtherBranch.Id]);

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.GetForOperationsAsync(
            otherBranchActor,
            deliveryId,
            requireAssignment: false,
            CancellationToken.None));
    }

    private static JsonElement Payload(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson);

    private static JsonElement Variables(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        Payload(notificationEvent).GetProperty("Variables");

    internal sealed class DeliveryHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string connectionString;
        private readonly int subscriptionGenerationWindowDays;
        private int batchSequence;

        private DeliveryHarness(
            SqliteConnection connection,
            string connectionString,
            int subscriptionGenerationWindowDays,
            DoodhDirectDbContext db,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            CapturingOtpDeliveryService otpDelivery,
            DeliveryOtpHandoffProtector otpProtector,
            CapturingRealtimePublisher realtime,
            DeliveryService service,
            User customer,
            User manager,
            User staff,
            User secondStaff,
            User otherBranchStaff,
            User inactiveStaff,
            Branch branch,
            Branch otherBranch,
            Order order,
            Subscription subscription,
            SubscriptionDelivery subscriptionDelivery)
        {
            this.connection = connection;
            this.connectionString = connectionString;
            this.subscriptionGenerationWindowDays = subscriptionGenerationWindowDays;
            Db = db;
            Clock = clock;
            TimeProvider = timeProvider;
            OtpDelivery = otpDelivery;
            OtpProtector = otpProtector;
            Realtime = realtime;
            Service = service;
            Customer = customer;
            Manager = manager;
            Staff = staff;
            SecondStaff = secondStaff;
            OtherBranchStaff = otherBranchStaff;
            InactiveStaff = inactiveStaff;
            Branch = branch;
            OtherBranch = otherBranch;
            Order = order;
            Subscription = subscription;
            SubscriptionDelivery = subscriptionDelivery;
        }

        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public CapturingOtpDeliveryService OtpDelivery { get; }
        public DeliveryOtpHandoffProtector OtpProtector { get; }
        public CapturingRealtimePublisher Realtime { get; }
        public DeliveryService Service { get; }
        public User Customer { get; }
        public User Manager { get; }
        public User Staff { get; }
        public User SecondStaff { get; }
        public User OtherBranchStaff { get; }
        public User InactiveStaff { get; }
        public Branch Branch { get; }
        public Branch OtherBranch { get; }
        public Order Order { get; }
        public Subscription Subscription { get; }
        public SubscriptionDelivery SubscriptionDelivery { get; }
        public DateOnly Today => TimeProvider.Today;
        public DeliveryActor ManagerActor => new(Manager.Id, [Branch.Id]);
        public DeliveryActor StaffActor(User employee) => new(employee.Id, [Branch.Id]);

        public async Task MaterializeAsync() =>
            _ = await Service.MaterializeEligibleAsync(ManagerActor, Today, CancellationToken.None);

        public async Task<Guid> MaterializeOrderAsync()
        {
            await MaterializeAsync();
            return await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.OrderId == Order.Id)
                .Select(x => x.PublicId)
                .SingleAsync();
        }

        public async Task<Guid> MaterializeSubscriptionAsync()
        {
            await MaterializeAsync();
            return await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == SubscriptionDelivery.Id)
                .Select(x => x.PublicId)
                .SingleAsync();
        }

        public async Task<string> GetOtpCodeAsync(Guid deliveryId)
        {
            var delivery = await Db.Deliveries
                .AsNoTracking()
                .SingleAsync(x => x.PublicId == deliveryId);
            var otp = await Db.DeliveryOtps
                .AsNoTracking()
                .SingleAsync(x => x.DeliveryId == delivery.Id);
            return OtpProtector.Unprotect(Assert.IsType<string>(otp.ProtectedCode));
        }

        public Task<DeliveryResult> AssignAsync(Guid deliveryId, User employee) =>
            Service.AssignAsync(
                ManagerActor,
                deliveryId,
                new AssignDeliveryRequest(employee.PublicId, null),
                CancellationToken.None);

        public async Task AdvanceToArrivedAsync(Guid deliveryId, User employee)
        {
            var actor = StaffActor(employee);
            await AssignAsync(deliveryId, employee);
            await Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
            await Service.StartAsync(actor, deliveryId, CancellationToken.None);
            await Service.ArriveAsync(actor, deliveryId, CancellationToken.None);
        }

        public async Task<long> RecordMilkBatchAsync(decimal quantityProduced = 2m, long? branchId = null)
        {
            var branch = branchId ?? Branch.Id;
            var producedAt = TimeProvider.Now.AddHours(-1);
            var production = new MilkProduction(
                branch,
                producedAt,
                12,
                quantityProduced,
                "L",
                Manager.Id,
                "Morning",
                null);
            Db.MilkProductions.Add(production);
            await Db.SaveChangesAsync();

            var batch = new MilkBatch(
                branch,
                production.Id,
                $"TEST-{++batchSequence}",
                producedAt,
                quantityProduced,
                "L");
            Db.MilkBatches.Add(batch);
            await Db.SaveChangesAsync();
            return batch.Id;
        }

        public async Task<long> BatchIdAsync(Guid publicId) =>
            await Db.MilkBatches.AsNoTracking()
                .Where(x => x.PublicId == publicId)
                .Select(x => x.Id)
                .SingleAsync();

        public async Task<DeliveryResult> SaveAllocationAsync(
            Guid deliveryId,
            long batchId,
            decimal quantity,
            DeliveryActor? actor = null) =>
            await Service.SaveBatchAllocationsAsync(
                actor ?? ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest(
                    [new DeliveryBatchAllocationRequest(batchId, quantity)]),
                CancellationToken.None);

        public DoodhDirectDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(10))
                .Options;
            return new DoodhDirectDbContext(options);
        }

        public DeliveryService CreateService(
            DoodhDirectDbContext db,
            CapturingRealtimePublisher? realtime = null,
            INotificationEventWriter? notificationEventWriter = null) => new(
                db,
                TimeProvider,
                new TestPasswordHasher(),
                OtpDelivery,
                realtime ?? Realtime,
                DeliveryOptionsFor(subscriptionGenerationWindowDays),
                notificationEventWriter ?? new TestNotificationEventWriter(db, Clock),
                new NumberSeriesService(db, TimeProvider),
                OtpProtector);

        public static async Task<DeliveryHarness> CreateAsync(
            DateTime? indiaLocalNow = null,
            int subscriptionGenerationWindowDays = 31)
        {
            var clock = new TestClock(
                indiaLocalNow ?? new DateTime(2026, 8, 16, 9, 30, 0, DateTimeKind.Unspecified));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"delivery-tests-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 10
            }.ToString();
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection, sqlite => sqlite.CommandTimeout(10))
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY", "Delivery Number", "DEL/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
            // Branch-scoped delivery series: strict allocation (see
            // NumberSeriesService.FindForAllocationAsync) requires a scope-specific
            // series per branch and refuses to fall back to the unscoped legacy row.
            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY_MAIN", "Delivery MAIN", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY_NORTH", "Delivery NORTH", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NORTH"));

            var customer = User(UserType.Customer, "Customer", "9999999999");
            var manager = User(UserType.Employee, "Delivery Manager", "9000000000");
            var staff = User(UserType.Employee, "Delivery Staff One", "9000000001");
            var secondStaff = User(UserType.Employee, "Delivery Staff Two", "9000000002");
            var otherBranchStaff = User(UserType.Employee, "Other Branch Staff", "9000000003");
            var inactiveStaff = User(UserType.Employee, "Inactive Staff", "9000000004");
            inactiveStaff.Deactivate();
            var role = new Role(AuthorizationCodes.DeliveryStaff, "Delivery Staff");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var otherBranch = new Branch("NORTH", "North Branch", "Bengaluru", "Karnataka", 13.0358m, 77.5970m);
            db.AddRange(customer, manager, staff, secondStaff, otherBranchStaff, inactiveStaff, role, branch, otherBranch);
            await db.SaveChangesAsync();

            staff.AssignRole(role, branch.Id);
            secondStaff.AssignRole(role, branch.Id);
            otherBranchStaff.AssignRole(role, otherBranch.Id);
            inactiveStaff.AssignRole(role, branch.Id);
            var category = new ProductCategory("MILK", "Milk");
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
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
            db.AddRange(product, address);
            await db.SaveChangesAsync();

            var order = new Order(
                customer.Id,
                address.Id,
                branch.Id,
                "order-delivery-1",
                "ORD-DEL-001",
                80m,
                0m,
                branch.Code,
                branch.Name,
                address.Label,
                address.AddressLine1,
                address.AddressLine2,
                address.Locality,
                address.City,
                address.State,
                address.PinCode,
                address.Landmark,
                address.DeliveryInstructions,
                address.ContactName,
                address.ContactMobile,
                address.Latitude,
                address.Longitude);
            order.ConfirmPayment();
            order.AddItem(new OrderItem(
                product.Id,
                2m,
                40m,
                product.Sku,
                product.Name,
                product.UnitOfMeasure));
            var today = timeProvider.Today;
            var subscription = new Subscription(
                customer.Id,
                product.Id,
                address.Id,
                branch.Id,
                "subscription-delivery-1",
                today,
                today.AddDays(7),
                1m,
                80m,
                2,
                product.Sku,
                product.Name,
                product.UnitOfMeasure,
                branch.Code,
                branch.Name,
                "1 Main Road, Central, Bengaluru, Karnataka 560001");
            subscription.AddDelivery(today);
            subscription.Activate(timeProvider.Now);
            db.AddRange(order, subscription);
            await db.SaveChangesAsync();
            var subscriptionDelivery = subscription.Deliveries.Single();
            db.ChangeTracker.Clear();

            var otpDelivery = new CapturingOtpDeliveryService();
            var otpProtector = new DeliveryOtpHandoffProtector(new EphemeralDataProtectionProvider());
            var realtime = new CapturingRealtimePublisher();
            var service = new DeliveryService(
                db,
                timeProvider,
                new TestPasswordHasher(),
                otpDelivery,
                realtime,
                DeliveryOptionsFor(subscriptionGenerationWindowDays),
                new TestNotificationEventWriter(db, clock),
                new NumberSeriesService(db, timeProvider),
                otpProtector);
            return new DeliveryHarness(
                connection,
                connectionString,
                subscriptionGenerationWindowDays,
                db,
                clock,
                timeProvider,
                otpDelivery,
                otpProtector,
                realtime,
                service,
                customer,
                manager,
                staff,
                secondStaff,
                otherBranchStaff,
                inactiveStaff,
                branch,
                otherBranch,
                order,
                subscription,
                subscriptionDelivery);
        }

        private static IOptions<DeliveryOptions> DeliveryOptionsFor(
            int subscriptionGenerationWindowDays) => Options.Create(new DeliveryOptions
            {
                OtpCodeLength = 6,
                OtpExpiryMinutes = 10,
                OtpMaximumAttempts = 3,
                MaximumLocationAgeMinutes = 15,
                MaximumLocationFutureSkewMinutes = 5,
                MaximumLocationsPerDelivery = 10,
                LocationRetentionDays = 30,
                SubscriptionGenerationWindowDays = subscriptionGenerationWindowDays
            });

        private static User User(UserType type, string name, string mobile)
        {
            var user = new User(type);
            user.SetProfile(name);
            user.SetContact(mobile, null);
            return user;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal sealed class CapturingOtpDeliveryService : IOtpDeliveryService
    {
        public List<(string Destination, string Code)> Messages { get; } = [];
        public List<(string Destination, string Code)> Attempts { get; } = [];
        public TaskCompletionSource<bool> SendStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailNextSend { get; set; }
        public bool BlockNextSend { get; set; }

        private readonly TaskCompletionSource<bool> releaseBlockedSend =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(
            string destination,
            string code,
            CancellationToken cancellationToken)
        {
            lock (Attempts)
            {
                Attempts.Add((destination, code));
            }

            SendStarted.TrySetResult(true);

            if (FailNextSend)
            {
                FailNextSend = false;
                throw new InvalidOperationException("Simulated OTP transport failure.");
            }

            if (BlockNextSend)
            {
                BlockNextSend = false;
                await releaseBlockedSend.Task.WaitAsync(cancellationToken);
            }

            Messages.Add((destination, code));
        }

        public void ReleaseBlockedSend() => releaseBlockedSend.TrySetResult(true);
    }

    private sealed class ThrowingNotificationEventWriter(string eventType) : INotificationEventWriter
    {
        public void Add(NotificationEventRequest request)
        {
            if (request.EventType == eventType)
            {
                throw new InvalidOperationException("Simulated downstream persistence failure.");
            }
        }
    }

    internal sealed class CapturingRealtimePublisher : IDeliveryRealtimePublisher
    {
        public List<DeliveryResult> Deliveries { get; } = [];
        public List<(Guid DeliveryId, DeliveryLocationResult Location)> Locations { get; } = [];

        public Task DeliveryChangedAsync(DeliveryResult delivery, CancellationToken cancellationToken)
        {
            Deliveries.Add(delivery);
            return Task.CompletedTask;
        }

        public Task LocationChangedAsync(
            Guid deliveryId,
            DeliveryLocationResult location,
            CancellationToken cancellationToken)
        {
            Locations.Add((deliveryId, location));
            return Task.CompletedTask;
        }
    }
}

internal sealed class TestIndiaTimeProvider(IClock clock) : IIndiaTimeProvider
{
    private static readonly TimeZoneInfo IndiaTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    public DateTime Now => DateTime.SpecifyKind(
        TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, IndiaTimeZone),
        DateTimeKind.Unspecified);

    public DateTime ToUtc(DateTime indiaLocal) =>
        TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(indiaLocal, DateTimeKind.Unspecified),
            IndiaTimeZone);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public DateOnly CurrentDate => Today;

    public DateTime CurrentDateTime => Now;

    public string FormatDateTime(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    public string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public DateTime ParseApplicationDateTime(string value) =>
        DateTime.SpecifyKind(
            DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeKind.Unspecified);
}
