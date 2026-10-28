using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 3 — re-delivery completion lifecycle (Rule 8).
///
/// The ticket is completed only when its linked replacement delivery is
/// completed, through the single shared delivery mutation used by both normal
/// completion and OTP-verified completion, inside the same serializable
/// transaction. Manual completion of a Re-Delivery ticket is rejected until
/// the replacement is actually delivered. One-time completion and the D1-C
/// automatic refund path are covered by the existing suites.
/// </summary>
public sealed class RefundReplacementRedeliveryCompletionTests
{
    [Fact]
    public async Task ManualComplete_BeforeReplacementDelivery_IsRejected()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CompleteAsync(
            StaffActor(harness),
            requestId,
            new DecideRefundReplacementRequest(null),
            CancellationToken.None));
        Assert.Contains(
            "before its replacement delivery is delivered", exception.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.COMPLETE"));
    }

    [Fact]
    public async Task DeliveryCompletion_CompletesLinkedRedeliveryTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await AdvanceToArrivedAsync(harness, deliveryId);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);

        // Normal (non-OTP-endpoint) completion: OTP already verified through
        // the domain operation, then completed via CompleteAsync. Both completion
        // entry points share the ticket hook under test.
        var delivery = await harness.Db.Deliveries
            .Include(x => x.Otps).SingleAsync(x => x.PublicId == deliveryId);
        delivery.RecordOtpVerified(harness.Staff.Id, harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var completed = await harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest("Delivered"),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Delivered, completed.Status);
        await AssertTicketCompletedAsync(harness, requestId, deliveryId);
    }

    [Fact]
    public async Task OtpVerification_CompletesLinkedRedeliveryTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await AdvanceToArrivedAsync(harness, deliveryId);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        Assert.NotNull(verified.OtpVerifiedAt);
        await AssertTicketCompletedAsync(harness, requestId, deliveryId);
    }

    [Fact]
    public async Task UnrelatedTickets_AreUntouchedByReplacementCompletion()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await AddDeliveryAsync(harness, new DateOnly(2026, 8, 19));
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 19), CancellationToken.None);
        var operational = await harness.Db.Deliveries.AsNoTracking()
            .Include(x => x.SubscriptionDelivery)
            .Where(x => x.SubscriptionDelivery != null)
            .OrderBy(x => x.SubscriptionDelivery!.ScheduledDate)
            .ToListAsync();
        Assert.Equal(2, operational.Count);
        foreach (var delivery in operational)
        {
            await harness.AssignAsync(delivery.PublicId, harness.Staff);
            await harness.FailSubscriptionAsync(delivery.PublicId, harness.Staff);
        }

        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, tickets.Count);
        var service = CreateRefundService(harness);
        var firstId = tickets[0].PublicId;
        var secondId = tickets[1].PublicId;
        await ChooseAsync(service, harness, firstId, CustomerChoice.ReDelivery);
        await ChooseAsync(service, harness, secondId, CustomerChoice.Refund);
        await service.ApproveAsync(
            StaffActor(harness), firstId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await AdvanceToArrivedAsync(harness, deliveryId);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(
            RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == firstId)).Status);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{firstId:N}:redelivery-delivered"));
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.COMPLETE"
                && x.EntityId == secondId.ToString()));
    }

    [Fact]
    public async Task RepeatCompletion_DoesNotDuplicateTransitionOrSideEffects()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await AdvanceToArrivedAsync(harness, deliveryId);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        // The delivery is delivered: OTP, direct completion, and manual ticket
        // completion must all fail without touching the ticket again.
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new DeliveryNotesRequest(null),
            CancellationToken.None));
        harness.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness),
            requestId,
            new DecideRefundReplacementRequest(null),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(
            RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.COMPLETE"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-delivered"));
    }

    [Fact]
    public async Task ConcurrentCompletion_CompletesTicketExactlyOnce()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await AdvanceToArrivedAsync(harness, deliveryId);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        var code = await harness.GetOtpCodeAsync(deliveryId);

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateService(firstDb);
        var secondService = harness.CreateService(secondDb);
        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.VerifyOtpAsync(
                    harness.StaffActor(harness.Staff), deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None),
                secondService.VerifyOtpAsync(
                    harness.StaffActor(harness.Staff), deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None)
            }.Select(async attempt =>
            {
                try
                {
                    return (Result: await attempt, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (DeliveryResult?)null, Error: exception);
                }
            }));

        Assert.Single(outcomes, x => x.Result is not null);
        Assert.Single(outcomes, x => x.Error is not null);

        await using var verifyDb = harness.CreateContext();
        Assert.Equal(
            RefundReplacementStatus.Completed,
            (await verifyDb.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, await verifyDb.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.COMPLETE"));
        Assert.Equal(1, await verifyDb.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-delivered"));
    }

    /// <summary>
    /// Shared post-condition: the correct ticket reached Completed exactly
    /// once via the delivery path, with history, entitlement, term, and
    /// financials preserved.
    /// </summary>
    private static async Task AssertTicketCompletedAsync(
        DeliveryServiceTests.DeliveryHarness harness, Guid requestId, Guid deliveryId)
    {
        harness.Db.ChangeTracker.Clear();
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementStatus.Completed, ticket.Status);
        Assert.NotNull(ticket.CompletedAt);
        Assert.NotNull(ticket.CompletedByUserId);

        var delivery = await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(delivery.CompletedAt, ticket.CompletedAt);

        var rows = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, rows[0].Status);
        Assert.False(rows[0].IsReplacement);
        Assert.Equal(SubscriptionDeliveryStatus.Delivered, rows[1].Status);
        Assert.True(rows[1].IsReplacement);
        Assert.Equal(rows[0].Id, rows[1].ReplacesSubscriptionDeliveryId);

        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(1, subscription.UsedEntitlement);
        Assert.Equal(new DateOnly(2026, 8, 23), subscription.EndDate);
        Assert.Equal(harness.Subscription.PayableAmount, subscription.PayableAmount);
        Assert.Equal(harness.Subscription.ChargesTotal, subscription.ChargesTotal);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());

        var audit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.COMPLETE").ToListAsync());
        Assert.Equal(requestId.ToString(), audit.EntityId);
        var completedEvent = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-delivered")
            .ToListAsync());
        Assert.Equal(NotificationEventTypes.RefundReplacementCompleted, completedEvent.EventType);
        Assert.Equal(ticket.CustomerId, completedEvent.UserId);
    }

    private static async Task<Guid> MaterializeReplacementAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 18), CancellationToken.None);
        return await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId)
            .SingleAsync();
    }

    private static Task AdvanceToArrivedAsync(
        DeliveryServiceTests.DeliveryHarness harness, Guid deliveryId) =>
        harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);

    private static async Task AddDeliveryAsync(
        DeliveryServiceTests.DeliveryHarness harness, DateOnly date)
    {
        var subscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries).SingleAsync();
        subscription.AddDelivery(date);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static async Task FailAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);

    private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        return deliveryId;
    }

    private static async Task<Guid> TicketPublicIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static Task ChooseReDeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId) =>
        ChooseAsync(service, harness, requestId, CustomerChoice.ReDelivery);

    private static async Task ChooseAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId,
        CustomerChoice choice) =>
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(choice),
            CancellationToken.None);

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
            harness.Db,
            harness.TimeProvider,
            harness.ConfigurationService,
            new NumberSeriesService(harness.Db, harness.TimeProvider),
            harness.ProofStorage,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
}
