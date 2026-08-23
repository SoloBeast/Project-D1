using System.Text.Json;
using Microsoft.EntityFrameworkCore.Infrastructure;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Microsoft.AspNetCore.DataProtection;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class DeliveryServiceTests
{
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
    }

    [Fact]
    public async Task OtpVerification_RemainsValidPastLegacyExpiryAndRejectsConsumedOrAttemptLimitedCodes()
    {
        await using var activeHarness = await DeliveryHarness.CreateAsync();
        var activeDeliveryId = await activeHarness.MaterializeOrderAsync();
        await activeHarness.AdvanceToArrivedAsync(activeDeliveryId, activeHarness.Staff);
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

        var retried = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, retried.Status);
    }

    [Fact]
    public async Task ConcurrentCorrectOtpVerification_CompletesExactlyOnce()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
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

    private static JsonElement Payload(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson);

    private static JsonElement Variables(
        DoodhDirect.Domain.Notifications.NotificationEvent notificationEvent) =>
        Payload(notificationEvent).GetProperty("Variables");

    private sealed class DeliveryHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly string connectionString;
        private readonly int subscriptionGenerationWindowDays;

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

    private sealed class CapturingOtpDeliveryService : IOtpDeliveryService
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

    private sealed class CapturingRealtimePublisher : IDeliveryRealtimePublisher
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
