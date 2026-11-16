using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Expired re-delivery to refund workflow: once the one-hour same-day window
/// (CustomerChoiceAt + 1 hour) passes, re-delivery approval stays closed and
/// the ticket can move to Refund — by the customer (Choose Refund) or by
/// confirmed staff action (Convert to refund) — reusing the existing refund
/// approval/settlement path. Expiry notices and conversions are idempotent;
/// expiry and conversion move no money.
/// </summary>
public sealed class RefundReplacementRedeliveryExpiryTests
{
    [Fact]
    public async Task Notify_BeforeExpiry_IsRejectedWithoutSideEffects()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        await Assert.ThrowsAsync<ConflictException>(() => service.NotifyRedeliveryExpiredAsync(
            StaffActor(harness), requestId, CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired"));
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
    }

    [Fact]
    public async Task Notify_AfterExpiry_NotifiesOnceAndChangesNothing()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        var first = await service.NotifyRedeliveryExpiredAsync(
            StaffActor(harness), requestId, CancellationToken.None);
        var second = await service.NotifyRedeliveryExpiredAsync(
            CustomerActor(harness), requestId, CancellationToken.None);

        Assert.Equal(CustomerChoice.ReDelivery, first.CustomerChoice);
        Assert.Equal(CustomerChoice.ReDelivery, second.CustomerChoice);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CustomerChange_BeforeExpiry_IsRejected()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        await Assert.ThrowsAsync<ConflictException>(() => service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None));
    }

    [Fact]
    public async Task CustomerChange_AfterExpiry_ConvertsToRefundWithoutMoneyMovement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        var choiceAt = (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).CustomerChoiceAt;
        Assert.NotNull(choiceAt);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        var converted = await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        Assert.Equal(CustomerChoice.Refund, converted.CustomerChoice);
        Assert.Equal(RefundReplacementType.Refund, converted.Type);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, converted.Status);
        harness.Db.ChangeTracker.Clear();
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        // The original request instant is preserved, so the deadline stays stable.
        Assert.Equal(choiceAt, ticket.CustomerChoiceAt);
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-converted"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_CONVERTED"));
        // Staff fan-out uses the deterministic choice-recorded event key.
        // The shared delivery harness carries no staff roles, so no
        // branch recipients exist here; the conversion notification is the
        // authoritative customer signal in this fixture.
        Assert.True(await harness.Db.NotificationEvents.AsNoTracking()
            .AnyAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-converted"));
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CustomerChange_RetryAfterConversion_IsIdempotent()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        // Same-choice retry returns state without new side effects.
        var retry = await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        Assert.Equal(CustomerChoice.Refund, retry.CustomerChoice);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-converted"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_CONVERTED"));
    }

    [Fact]
    public async Task StaffConvert_RequiresCustomerConfirmation()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        await Assert.ThrowsAsync<ValidationAppException>(() => service.ConvertExpiredRedeliveryToRefundAsync(
            StaffActor(harness), requestId,
            new ConvertExpiredRedeliveryRequest(false, "no consent"), CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(CustomerChoice.ReDelivery,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).CustomerChoice);
    }

    [Fact]
    public async Task StaffConvert_BeforeExpiry_IsRejected()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        await Assert.ThrowsAsync<ConflictException>(() => service.ConvertExpiredRedeliveryToRefundAsync(
            StaffActor(harness), requestId,
            new ConvertExpiredRedeliveryRequest(true, null), CancellationToken.None));
    }

    [Fact]
    public async Task StaffConvert_OutOfScopeBranch_IsNotFound()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        var outsider = new RefundReplacementActor(harness.Manager.Id, new HashSet<long> { 999999 }, false);
        await Assert.ThrowsAsync<NotFoundException>(() => service.ConvertExpiredRedeliveryToRefundAsync(
            outsider, requestId,
            new ConvertExpiredRedeliveryRequest(true, null), CancellationToken.None));
    }

    [Fact]
    public async Task StaffConvert_AfterExpiry_ConvertsThenRefundsThroughExistingWorkflow()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));
        await SeedSubscriptionPaymentAsync(harness);

        var converted = await service.ConvertExpiredRedeliveryToRefundAsync(
            StaffActor(harness), requestId,
            new ConvertExpiredRedeliveryRequest(true, "Customer agreed on call"), CancellationToken.None);
        Assert.Equal(CustomerChoice.Refund, converted.CustomerChoice);
        Assert.Equal(RefundReplacementType.Refund, converted.Type);

        // The existing refund approval/settlement path applies unchanged.
        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        var completed = await service.CompleteAsync(
            StaffActor(harness), requestId, new DecideRefundReplacementRequest(null), CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.True(payment.RefundedAmount > 0);
        Assert.True(payment.RefundedAmount <= payment.Amount);
        var conversionAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_CONVERTED").ToListAsync());
        Assert.Contains("Customer agreed on call", conversionAudit.NewValueJson ?? string.Empty);

        // Retrying the conversion after success is a no-op: no second
        // conversion event, no second refund, no second wallet credit.
        var retry = await service.ConvertExpiredRedeliveryToRefundAsync(
            StaffActor(harness), requestId,
            new ConvertExpiredRedeliveryRequest(true, "retry"), CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, retry.Status);
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-converted"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_CONVERTED"));
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task StaffConvert_PaymentCapRespected_NoOverRefund()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        // Partly refund the covering payment first, then convert and settle.
        // Amount 800 keeps the SQLite TEXT CHECK lexicographically valid
        // (790 vs 800) while preserving the numeric cap: 790 prior leaves
        // 10 remaining, so the 80 gross (waived deduction) caps to 10.
        // Seed clears tracking, so reload the payment tracked before mutating.
        await SeedSubscriptionPaymentAsync(harness);
        var payment = await harness.Db.Payments.SingleAsync();
        var prior = payment.StartRefund(790m, "setup prior refund", "setup-prior-cap", harness.Customer.Id);
        prior.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(790m);
        var wallet = new Wallet(harness.Customer.Id, "INR");
        harness.Db.Wallets.Add(wallet);
        await harness.Db.SaveChangesAsync();
        wallet.Credit(
            WalletTransactionType.RefundCredit, 790m, "setup-prior-wallet",
            "setup", harness.TimeProvider.Now, payment.Id);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        await service.ConvertExpiredRedeliveryToRefundAsync(
            StaffActor(harness), requestId,
            new ConvertExpiredRedeliveryRequest(true, null), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "No service rendered", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), requestId, new DecideRefundReplacementRequest(null), CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var settled = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.True(settled.RefundedAmount <= settled.Amount);
        var walletBalance = await harness.Db.Wallets.AsNoTracking().Select(x => x.Balance).SingleAsync();
        Assert.Equal(settled.RefundedAmount, walletBalance);
    }

    [Fact]
    public async Task ExpiredRedeliveryApproval_RejectedWithoutReplacementOrRefund()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));
        Assert.Contains("one-hour", exception.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .CountAsync(x => x.IsReplacement));
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ConvertedTicket_CannotReturnToReDelivery()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));
        await service.ConvertExpiredRedeliveryToRefundAsync(
            StaffActor(harness), requestId,
            new ConvertExpiredRedeliveryRequest(true, null), CancellationToken.None);

        // Neither the customer nor staff can resurrect re-delivery: the
        // recorded choice is immutable, and the converted ticket only
        // accepts the refund approval path (a re-delivery date is refused
        // without ever minting a replacement).
        await Assert.ThrowsAsync<ConflictException>(() => service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 16), SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(0, await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .CountAsync(x => x.IsReplacement));
    }

    private static async Task FailAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);

    private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        await harness.AssignAsync(deliveryId, harness.Staff);
        return deliveryId;
    }

    private static async Task<Guid> TicketPublicIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static Task ChooseReDeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId) =>
        service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);

    private static async Task<Payment> SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        decimal? amountOverride = null)
    {
        // SQLite stores decimals as TEXT, so its CHECK compares
        // lexicographically: 72 vs 160 would falsely fail while 72 vs 800
        // passes and preserves the numeric invariant (72 <= 800). The refund
        // math still uses the frozen subscription payable (160/2=80 gross),
        // so the payment amount only supplies the refundable-balance cap.
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            amountOverride ?? 800m,
            "INR",
            $"expiry-sub-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        return payment;
    }

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
            harness.Db,
            harness.TimeProvider,
            harness.ConfigurationService,
            new NumberSeriesService(harness.Db, harness.TimeProvider),
            harness.ProofStorage,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
}
