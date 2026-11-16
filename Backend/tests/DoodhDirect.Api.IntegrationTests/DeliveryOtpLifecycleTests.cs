using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Domain.Deliveries;
using Microsoft.EntityFrameworkCore;
using static DoodhDirect.Api.IntegrationTests.DeliveryServiceTests;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// One active OTP per delivery, issued only when the delivery enters its
/// assigned lifecycle. Repeated assignments, API reads, retries and worker
/// runs must never generate another OTP; verification always validates
/// against the delivery's own active OTP; terminal states invalidate it.
/// </summary>
public sealed class DeliveryOtpLifecycleTests
{
    [Fact]
    public async Task UnassignedDelivery_NeverReceivesOtpOrNotification()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        // Repeated materialization/fetch reads never issue an OTP.
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, harness.Today, CancellationToken.None);
        await harness.Service.FetchSubscriptionDeliveriesAsync(
            harness.ManagerActor, harness.Today, CancellationToken.None);
        await harness.Service.IssuePendingOtpsAsync(CancellationToken.None);

        Assert.Empty(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
            .ToListAsync());
        Assert.Empty(harness.OtpDelivery.Messages);

        // The customer app exposes no OTP for an unassigned delivery.
        var customerView = await harness.Service.GetForCustomerAsync(
            harness.Customer.Id, deliveryId, CancellationToken.None);
        Assert.Null(customerView.ActiveOtp);
    }

    [Fact]
    public async Task ReadsNeverGenerateOtp_BeforeOrAfterAssignment()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        await harness.Service.GetForCustomerAsync(
            harness.Customer.Id, deliveryId, CancellationToken.None);
        await harness.Service.GetForOperationsAsync(
            harness.ManagerActor, deliveryId, requireAssignment: false, CancellationToken.None);
        await harness.Service.GetForBranchAsync(
            harness.ManagerActor, harness.Branch.Id, harness.Today,
            DeliveryStatus.ReadyForAssignment, null, null, CancellationToken.None);
        await harness.Service.GetTodayForStaffAsync(
            harness.StaffActor(harness.Staff), harness.Today, null, CancellationToken.None);
        Assert.Empty(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());

        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        await harness.AssignAsync(deliveryId, harness.Staff);

        await harness.Service.GetForCustomerAsync(
            harness.Customer.Id, deliveryId, CancellationToken.None);
        await harness.Service.GetForOperationsAsync(
            harness.ManagerActor, deliveryId, requireAssignment: false, CancellationToken.None);
        await harness.Service.GetForBranchAsync(
            harness.ManagerActor, harness.Branch.Id, harness.Today,
            DeliveryStatus.Assigned, null, null, CancellationToken.None);
        await harness.Service.GetTodayForStaffAsync(
            harness.StaffActor(harness.Staff), harness.Today, null, CancellationToken.None);

        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
            .ToListAsync());
    }

    [Fact]
    public async Task InitialAssignment_GeneratesExactlyOneOtpThatVerifies()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);

        var assigned = await harness.AssignAsync(deliveryId, harness.Staff);
        Assert.Equal(DeliveryStatus.Assigned, assigned.Status);

        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var otp = Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync());
        Assert.NotNull(otp.SentAt);
        Assert.NotNull(otp.ProtectedCode);
        var code = harness.OtpProtector.Unprotect(otp.ProtectedCode);

        // One deterministic OTP-issued event and one in-app send of the same code.
        var otpEvent = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
            .ToListAsync());
        Assert.Equal($"delivery:{deliveryId:N}:otp-issued", otpEvent.EventKey);
        var message = Assert.Single(harness.OtpDelivery.Messages);
        Assert.Equal(code, message.Code);
        Assert.Equal("9999999999", message.Destination);

        // The issued code verifies the delivery through the full lifecycle.
        var actor = harness.StaffActor(harness.Staff);
        await harness.Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(actor, deliveryId, CancellationToken.None);
        await harness.Service.ArriveAsync(actor, deliveryId, CancellationToken.None);
        var verified = await harness.Service.VerifyOtpAsync(
            actor, deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
    }

    [Fact]
    public async Task RepeatedAssignment_BySameEmployee_NeverRegenerates()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        await harness.AssignAsync(deliveryId, harness.Staff);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var repeat = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.AssignAsync(deliveryId, harness.Staff));
        Assert.Equal("The delivery is already assigned to this employee.", repeat.Message);

        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
            .ToListAsync());
        Assert.Single(harness.OtpDelivery.Messages);
        Assert.Equal(code, await harness.GetOtpCodeAsync(deliveryId));
    }

    [Fact]
    public async Task Reassignment_ToDifferentEmployee_ReusesSameOtp()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        await harness.AssignAsync(deliveryId, harness.Staff);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        // Production wall-clock always advances between requests; move the
        // frozen test clock so the reassignment audit/notification timestamps
        // differ (the OTP assertions below are the point of this test).
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var reassigned = await harness.Service.AssignAsync(
            harness.ManagerActor,
            deliveryId,
            new AssignDeliveryRequest(harness.SecondStaff.PublicId, "Shift change"),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Assigned, reassigned.Status);
        Assert.Equal(harness.SecondStaff.PublicId, reassigned.AssignedEmployeeId);

        // No rotation: the same single OTP row, code, event and single send.
        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
            .ToListAsync());
        Assert.Single(harness.OtpDelivery.Messages);
        Assert.Equal(code, await harness.GetOtpCodeAsync(deliveryId));

        // The reused code still verifies for the newly assigned employee.
        var actor = harness.StaffActor(harness.SecondStaff);
        await harness.Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(actor, deliveryId, CancellationToken.None);
        await harness.Service.ArriveAsync(actor, deliveryId, CancellationToken.None);
        var verified = await harness.Service.VerifyOtpAsync(
            actor, deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
    }

    [Fact]
    public async Task BulkAssignment_IssuesExactlyOneOtpPerDelivery()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        await harness.MaterializeAsync();
        var orderDeliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.OrderId == harness.Order.Id)
            .Select(x => x.PublicId)
            .SingleAsync();
        var subscriptionDeliveryId = await harness.MaterializeSubscriptionAsync();

        var orderBatchId = await harness.RecordMilkBatchAsync(4m);
        await harness.SaveAllocationAsync(orderDeliveryId, orderBatchId, 2m);
        var subscriptionBatchId = await harness.RecordMilkBatchAsync(4m);
        await harness.SaveAllocationAsync(subscriptionDeliveryId, subscriptionBatchId, 1m);

        var result = await harness.Service.BulkAssignAsync(
            harness.ManagerActor,
            new BulkAssignDeliveriesRequest(
                [orderDeliveryId, subscriptionDeliveryId],
                harness.Staff.PublicId,
                null),
            CancellationToken.None);
        Assert.Equal(2, result.Deliveries.Count);

        Assert.Equal(2, await harness.Db.DeliveryOtps.AsNoTracking().CountAsync());
        var events = await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.DeliveryOtpIssued)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(
            2,
            events.Select(x => x.EventKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, harness.OtpDelivery.Messages.Count);

        // Independent OTPs per delivery.
        var orderCode = await harness.GetOtpCodeAsync(orderDeliveryId);
        var subscriptionCode = await harness.GetOtpCodeAsync(subscriptionDeliveryId);
        Assert.NotEqual(orderCode, subscriptionCode);
    }

    [Fact]
    public async Task TransportRetry_ResendsSameOtpWithoutRotating()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        harness.OtpDelivery.FailNextSend = true;
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        await harness.AssignAsync(deliveryId, harness.Staff);

        // Assignment created the OTP and its event, but the send failed.
        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var pending = Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync());
        Assert.Null(pending.SentAt);
        Assert.Empty(harness.OtpDelivery.Messages);
        var pendingCode = harness.OtpProtector.Unprotect(pending.ProtectedCode!);

        // Retries resend the same OTP; the event is never duplicated.
        await harness.Service.IssuePendingOtpsAsync(CancellationToken.None);
        await harness.Service.IssuePendingOtpsAsync(CancellationToken.None);

        var otp = Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync());
        Assert.NotNull(otp.SentAt);
        Assert.Equal(pending.ProtectedCode, otp.ProtectedCode);
        var message = Assert.Single(harness.OtpDelivery.Messages);
        Assert.Equal(pendingCode, message.Code);
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"delivery:{deliveryId:N}:otp-issued")
            .ToListAsync());
    }

    [Fact]
    public async Task Verification_UsesDeliveryOwnOtpOnly()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        await harness.MaterializeAsync();
        var firstId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.OrderId == harness.Order.Id)
            .Select(x => x.PublicId)
            .SingleAsync();
        var secondId = await harness.MaterializeSubscriptionAsync();

        var firstBatchId = await harness.RecordMilkBatchAsync(4m);
        await harness.SaveAllocationAsync(firstId, firstBatchId, 2m);
        var secondBatchId = await harness.RecordMilkBatchAsync(4m);
        await harness.SaveAllocationAsync(secondId, secondBatchId, 1m);
        await harness.AssignAsync(firstId, harness.Staff);
        await harness.AssignAsync(secondId, harness.Staff);

        var firstCode = await harness.GetOtpCodeAsync(firstId);
        var secondCode = await harness.GetOtpCodeAsync(secondId);

        var actor = harness.StaffActor(harness.Staff);
        foreach (var deliveryId in new[] { firstId, secondId })
        {
            await harness.Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
            await harness.Service.StartAsync(actor, deliveryId, CancellationToken.None);
            await harness.Service.ArriveAsync(actor, deliveryId, CancellationToken.None);
        }

        // Cross-delivery codes are rejected and do not consume the OTP.
        var cross = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.VerifyOtpAsync(
                actor, firstId, new VerifyDeliveryOtpRequest(secondCode), CancellationToken.None));
        Assert.Equal("code", cross.Field);

        var verified = await harness.Service.VerifyOtpAsync(
            actor, firstId, new VerifyDeliveryOtpRequest(firstCode), CancellationToken.None);
        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
    }

    [Fact]
    public async Task CompletedDelivery_OtpUnusableAndNeverReissued()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        await harness.AssignAsync(deliveryId, harness.Staff);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var actor = harness.StaffActor(harness.Staff);
        await harness.Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(actor, deliveryId, CancellationToken.None);
        await harness.Service.ArriveAsync(actor, deliveryId, CancellationToken.None);
        await harness.Service.VerifyOtpAsync(
            actor, deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None);

        // The consumed OTP can never verify again, and worker reruns create nothing.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.VerifyOtpAsync(
                actor, deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None));
        await harness.Service.IssuePendingOtpsAsync(CancellationToken.None);

        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var otp = Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync());
        Assert.NotNull(otp.ConsumedAt);
        Assert.Null(otp.ProtectedCode);
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"delivery:{deliveryId:N}:otp-issued")
            .ToListAsync());

        var customerView = await harness.Service.GetForCustomerAsync(
            harness.Customer.Id, deliveryId, CancellationToken.None);
        Assert.Null(customerView.ActiveOtp);
    }

    [Fact]
    public async Task FailedDelivery_OtpInvalidatedAndNeverReissued()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        await harness.AssignAsync(deliveryId, harness.Staff);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var actor = harness.StaffActor(harness.Staff);
        await harness.Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(actor, deliveryId, CancellationToken.None);
        await harness.Service.ArriveAsync(actor, deliveryId, CancellationToken.None);
        await harness.Service.FailAsync(
            actor,
            deliveryId,
            new FailDeliveryRequest(
                DeliveryFailureReasons.CustomerNotAvailable, "No response", 12.972m, 77.595m),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        var otp = Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync());
        Assert.NotNull(otp.ConsumedAt);
        Assert.Null(otp.ProtectedCode);

        // The invalidated code cannot verify, and worker reruns create nothing new.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.VerifyOtpAsync(
                actor, deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None));
        await harness.Service.IssuePendingOtpsAsync(CancellationToken.None);
        Assert.Single(await harness.Db.DeliveryOtps.AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id)
            .ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"delivery:{deliveryId:N}:otp-issued")
            .ToListAsync());
    }
}
