using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 5: subscription cancellation with a pending approved
/// re-delivery replacement. Cancelling voids the unfulfilled replacement
/// (terminal operational Cancelled state, never a failure), refunds the
/// frozen one-share entitlement to the wallet under a cancel-redelivery
/// idempotency namespace, and completes the ticket with an explicit
/// cancellation outcome. In-flight replacements block the cancellation;
/// delivered replacements are preserved without a second refund.
/// </summary>
public sealed class RefundReplacementCancelPendingRedeliveryTests
{
    [Fact]
    public async Task Cancel_WithApprovedRedeliveryBeforeMaterialization_VoidsReplacementAndRefundsFrozenShare()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);
        var subscriptionId = harness.Subscription.PublicId;
        var payable = harness.Subscription.PayableAmount;
        var charges = harness.Subscription.ChargesTotal;

        var cancelled = await CancelService(harness).CancelAsync(
            harness.Customer.Id, subscriptionId, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Cancelled, cancelled.Status);

        var subscription = await harness.Db.Subscriptions.AsNoTracking()
            .Include(x => x.Deliveries).SingleAsync();
        var original = Assert.Single(subscription.Deliveries, x => !x.IsReplacement);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, original.Status);
        var replacement = Assert.Single(subscription.Deliveries, x => x.IsReplacement);
        Assert.Equal(SubscriptionDeliveryStatus.Cancelled, replacement.Status);

        // No operational delivery was ever materialized for the replacement.
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDeliveryId == replacement.Id).ToListAsync());

        // Frozen math: payable 160 / 2 = 80 gross, 10% snapshot = 8, final 72.
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        // D1-D slice 7.1: voided and refunded without delivery — Cancelled,
        // never Completed (which means successful re-delivery).
        Assert.Equal(RefundReplacementStatus.Cancelled, ticket.Status);
        Assert.Equal(CustomerChoice.ReDelivery, ticket.CustomerChoice);
        Assert.Equal(RefundReplacementType.Replacement, ticket.Type);
        Assert.Equal(harness.Customer.Id, ticket.CompletedByUserId);
        Assert.NotNull(ticket.CompletedAt);
        Assert.Contains("cancelled", ticket.CompletionRemarks ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(72m, walletTxn.Amount);
        Assert.Equal(WalletTransactionType.RefundCredit, walletTxn.Type);
        Assert.Equal($"cancel-redelivery:{requestId:N}:refund", walletTxn.IdempotencyKey);
        Assert.Equal(72m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        // Distinct namespace: the D1-C timeout key was never used.
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking()
            .Where(x => x.IdempotencyKey.StartsWith("delivery-fail:")).ToListAsync());

        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(72m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(72m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Equal($"cancel-redelivery:{requestId:N}:refund", refund.IdempotencyKey);

        // Frozen financials untouched.
        Assert.Equal(payable, subscription.PayableAmount);
        Assert.Equal(charges, subscription.ChargesTotal);

        var cancelAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.CANCEL_REFUND").ToListAsync());
        Assert.Equal(requestId.ToString(), cancelAudit.EntityId);
        Assert.Contains("SubscriptionCancelled", cancelAudit.NewValueJson ?? string.Empty);
        var cancelEvent = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"refund-replacement:{requestId:N}:cancel-refund").ToListAsync());
        Assert.Equal(NotificationEventTypes.RefundReplacementCancelRefunded, cancelEvent.EventType);
    }

    [Fact]
    public async Task Cancel_WithPrePickupOperationalDelivery_VoidsItInvalidatesOtpAndExcludesFromProcessing()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await harness.AssignAsync(deliveryId, harness.Staff);

        await CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);

        var operational = await harness.Db.Deliveries
            .Include(x => x.Otps).SingleAsync(x => x.PublicId == deliveryId);
        Assert.Equal(DeliveryStatus.Cancelled, operational.Status);
        // History preserved: assignment and OTP rows survive with dead OTPs.
        Assert.NotNull(operational.AssignedEmployeeId);
        var otp = Assert.Single(operational.Otps);
        Assert.NotNull(otp.ConsumedAt);
        Assert.Null(otp.ProtectedCode);

        var voidAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "DELIVERY.CANCEL").ToListAsync());
        Assert.Equal(deliveryId.ToString(), voidAudit.EntityId);

        // The voided delivery accepts no further operational processing.
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.PickUpAsync(
            harness.StaffActor(harness.Staff), deliveryId, new DeliveryNotesRequest(null), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.FailAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new FailDeliveryRequest(DeliveryFailureReasons.CustomerNotAvailable, null, null, null),
            CancellationToken.None));
        var batchId = await harness.RecordMilkBatchAsync();
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.SaveAllocationAsync(
            deliveryId, batchId, 1m));
        // Re-materialization creates nothing for the cancelled occurrence.
        var replacementId = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement).Select(x => x.Id).SingleAsync();
        await harness.Service.FetchSubscriptionDeliveriesAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 18), CancellationToken.None);
        Assert.Equal(1, await harness.Db.Deliveries.AsNoTracking()
            .CountAsync(x => x.SubscriptionDeliveryId == replacementId));

        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(RefundReplacementStatus.Cancelled, ticket.Status);
        Assert.Equal(72m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Cancel_WithPickedUpReplacement_RejectsWithNoPartialChanges()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await harness.AssignAsync(deliveryId, harness.Staff);
        await harness.Service.PickUpAsync(
            harness.StaffActor(harness.Staff), deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);

        var error = await Assert.ThrowsAsync<ConflictException>(() => CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None));
        Assert.Contains("must be operationally resolved first", error.Message, StringComparison.Ordinal);

        // No partial database or financial changes: everything is untouched.
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Active, (await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync()).Status);
        var replacement = Assert.Single(await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement).ToListAsync());
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.Equal(DeliveryStatus.PickedUp, (await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == deliveryId)).Status);
        Assert.Equal(RefundReplacementStatus.Approved, (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId)).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.CANCEL_REFUND" || x.Action == "DELIVERY.CANCEL"));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RefundReplacementCancelRefunded));
    }

    [Fact]
    public async Task Cancel_WithOutForDeliveryReplacement_Rejects()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        var deliveryId = await MaterializeReplacementAsync(harness);
        var actor = harness.StaffActor(harness.Staff);
        await harness.AssignAsync(deliveryId, harness.Staff);
        await harness.Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(actor, deliveryId, CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Active, (await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Cancel_WithArrivedReplacement_Rejects()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, await TicketPublicIdAsync(harness));
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);

        await Assert.ThrowsAsync<ConflictException>(() => CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Active, (await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Cancel_AfterReplacementDelivered_PreservesCompletionWithoutDuplicateRefund()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var choiceService = CreateChoiceService(harness);
        await ChooseReDeliveryAsync(choiceService, harness, requestId);
        await choiceService.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        var deliveryId = await MaterializeReplacementAsync(harness);
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        var delivery = await harness.Db.Deliveries.Include(x => x.Otps).SingleAsync(x => x.PublicId == deliveryId);
        delivery.RecordOtpVerified(harness.Staff.Id, harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        await harness.Service.CompleteAsync(
            harness.StaffActor(harness.Staff), deliveryId, new DeliveryNotesRequest("Delivered"), CancellationToken.None);

        var completedAt = (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId)).CompletedAt;
        Assert.NotNull(completedAt);

        await CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);

        // The completed ticket and its history are preserved; no cancellation
        // refund is issued for the already-fulfilled entitlement.
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(RefundReplacementStatus.Completed, ticket.Status);
        Assert.Equal(completedAt, ticket.CompletedAt);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.CANCEL_REFUND"));
        Assert.Equal(SubscriptionDeliveryStatus.Delivered, (await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.IsReplacement)).Status);
    }

    [Fact]
    public async Task Cancel_UsesTicketDeductionSnapshotNotCurrentConfig()
    {
        await using var harness = await CreateHarnessAsync();
        // Ticket snapshots 25%; the live configuration is moved back to 10%
        // before cancellation — the refund must still use the snapshot.
        await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, null, 25m),
            harness.Manager.Id, null, null, CancellationToken.None);
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        Assert.Equal(25m, (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync()).DeliveryFailDeductionPercentageSnapshot);
        await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, null, 10m),
            harness.Manager.Id, null, null, CancellationToken.None);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        await CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);

        // 80 gross - 20 (25%) = 60, not the 72 the live 10% would produce.
        Assert.Equal(60m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(60m, refund.Amount);
    }

    [Fact]
    public async Task Cancel_UsesFrozenOneShareMath()
    {
        await using var harness = await CreateHarnessAsync();
        // Custom stack: 1 x 100 over 3 entitlements = payable 300, 15% snapshot.
        // Gross 100.00, deduction 15.00, final 85.00 (AwayFromZero throughout).
        var seed = await SeedCustomTicketAsync(harness, 1m, 100m, 3, 15m);
        var choiceService = CreateChoiceService(harness);
        await ChooseReDeliveryAsync(choiceService, harness, seed.RequestPublicId);
        await choiceService.ApproveAsync(
            StaffActor(harness), seed.RequestPublicId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        await CancelService(harness).CancelAsync(
            harness.Customer.Id, seed.SubscriptionPublicId, CancellationToken.None);

        Assert.Equal(85m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking()
            .Where(x => x.IdempotencyKey == $"cancel-redelivery:{seed.RequestPublicId:N}:refund").ToListAsync());
        Assert.Equal(85m, refund.Amount);
        var subscription = await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.PublicId == seed.SubscriptionPublicId);
        Assert.Equal(seed.Payable, subscription.PayableAmount);
    }

    [Fact]
    public async Task Cancel_CreditsWalletForRazorpayPaymentWithoutGatewayCall()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Razorpay);

        await CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);

        // Wallet-only credit regardless of the original payment method; the
        // refund is recorded locally as succeeded with no gateway refund id.
        Assert.Equal(72m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(72m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Null(refund.GatewayRefundId);
    }

    [Fact]
    public async Task Cancel_IsIdempotentAcrossRetries()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        var service = CancelService(harness);
        var first = await service.CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);
        Assert.Equal(SubscriptionStatus.Cancelled, first.Status);
        var second = await service.CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);
        Assert.Equal(SubscriptionStatus.Cancelled, second.Status);

        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:cancel-refund"));
        // The retry resolves nothing twice: one refund, one terminal
        // cancellation outcome (never Completed, which means delivered).
        Assert.Equal(RefundReplacementStatus.Cancelled, (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId)).Status);
    }

    [Fact]
    public async Task Cancel_CapsRefundAtPaymentRemaining()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        var payment = await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        // A prior 100.00 refund leaves 60.00 refundable — below the 72.00 share.
        var tracked = await harness.Db.Payments.Include(x => x.Refunds).SingleAsync(x => x.Id == payment.Id);
        var prior = tracked.StartRefund(100m, "Prior staff refund.", "prior-staff-refund", harness.Customer.Id);
        prior.Succeed(harness.TimeProvider.Now);
        tracked.CompleteRefund(100m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        await CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(60m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(160m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(RefundReplacementStatus.Cancelled, (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId)).Status);
    }

    [Fact]
    public async Task Cancel_WithFullyRefundedPayment_RollsBackEverything()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);
        var payment = await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        var tracked = await harness.Db.Payments.Include(x => x.Refunds).SingleAsync(x => x.Id == payment.Id);
        var prior = tracked.StartRefund(160m, "Prior full refund.", "prior-full-refund", harness.Customer.Id);
        prior.Succeed(harness.TimeProvider.Now);
        tracked.CompleteRefund(160m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<ConflictException>(() => CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None));
        Assert.Contains("fully refunded", error.Message, StringComparison.Ordinal);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Active, (await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync()).Status);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, (await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.IsReplacement)).Status);
        Assert.Equal(RefundReplacementStatus.Approved, (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId)).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Cancel_WithoutSuccessfulPayment_RollsBackEverything()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ApproveRedeliveryAsync(CreateChoiceService(harness), harness, requestId);

        var error = await Assert.ThrowsAsync<ConflictException>(() => CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None));
        Assert.Contains("no successful payment", error.Message, StringComparison.Ordinal);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Active, (await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync()).Status);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, (await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.IsReplacement)).Status);
        Assert.Equal(RefundReplacementStatus.Approved, (await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == requestId)).Status);
    }

    [Fact]
    public async Task Cancel_WithTwoPendingReplacements_ResolvesEachSeparately()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var firstId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var choiceService = CreateChoiceService(harness);
        await ChooseReDeliveryAsync(choiceService, harness, firstId);

        // A second original failure on a later occurrence: its own ticket.
        var subscription = await harness.Db.Subscriptions.Include(x => x.Deliveries).SingleAsync();
        subscription.AddDelivery(new DateOnly(2026, 8, 19));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 19), CancellationToken.None);
        var secondDeliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null
                && x.SubscriptionDelivery.ScheduledDate == new DateOnly(2026, 8, 19)
                && !x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId).SingleAsync();
        await harness.AssignAsync(secondDeliveryId, harness.Staff);
        await harness.FailSubscriptionAsync(secondDeliveryId, harness.Staff);
        var secondId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Where(x => x.PublicId != firstId).Select(x => x.PublicId).SingleAsync();
        await ChooseReDeliveryAsync(choiceService, harness, secondId);

        await choiceService.ApproveAsync(
            StaffActor(harness), firstId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        await choiceService.ApproveAsync(
            StaffActor(harness),
            secondId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 20)),
            CancellationToken.None);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        await CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);

        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync();
        Assert.Equal(2, tickets.Count);
        Assert.All(tickets, x => Assert.Equal(RefundReplacementStatus.Cancelled, x.Status));
        Assert.Equal(2, await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .CountAsync(x => x.IsReplacement && x.Status == SubscriptionDeliveryStatus.Cancelled));
        // One share per ticket; the payment cap holds the total (144 <= 160).
        Assert.Equal(144m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(2, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(144m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(2, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RefundReplacementCancelRefunded));
    }

    [Fact]
    public async Task Cancel_BeforeCustomerChoice_PreservesTimeoutRefund()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        // Cancellation with no approved replacement touches neither the
        // ticket nor the wallet.
        await CancelService(harness).CancelAsync(
            harness.Customer.Id, harness.Subscription.PublicId, CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, (await harness.Db.RefundReplacementRequests
            .AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());

        // The existing D1-C timeout path still fires after cancel.
        harness.Clock.Advance(TimeSpan.FromHours(25));
        var resolved = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(1, resolved);
        Assert.Equal(RefundReplacementStatus.Completed, (await harness.Db.RefundReplacementRequests
            .AsNoTracking().SingleAsync()).Status);
        Assert.Equal(72m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal($"delivery-fail:{requestId:N}:refund", (await harness.Db.WalletTransactions.AsNoTracking()
            .SingleAsync()).IdempotencyKey);
    }

    [Fact]
    public async Task Cancel_ConcurrentCancelsOnSqlServer_CreditExactlyOnce()
    {
        await using var harness = await SqlCancelHarness.CreateAsync();

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateCancelService(firstDb);
        var secondService = harness.CreateCancelService(secondDb);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.CancelAsync(harness.CustomerId, harness.SubscriptionPublicId, CancellationToken.None),
                secondService.CancelAsync(harness.CustomerId, harness.SubscriptionPublicId, CancellationToken.None)
            }.Select(async attempt =>
            {
                try
                {
                    return (Result: await attempt, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (SubscriptionResult?)null, Error: exception);
                }
            }));

        // Both outcomes are benign (work + idempotent no-op, or a conflict the
        // caller retries into a no-op), but the money moves exactly once.
        Assert.All(outcomes, x => Assert.True(
            x.Result is not null || x.Error is ConflictException,
            $"Unexpected error: {x.Error}"));
        Assert.Contains(outcomes, x => x.Result is not null);

        await using var verifyDb = harness.CreateContext();
        Assert.Equal(1, await verifyDb.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(72m, (await verifyDb.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(1, await verifyDb.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(RefundReplacementStatus.Cancelled, (await verifyDb.RefundReplacementRequests.AsNoTracking()
            .SingleAsync()).Status);
        Assert.Equal(SubscriptionStatus.Cancelled, (await verifyDb.Subscriptions.AsNoTracking()
            .SingleAsync()).Status);
    }

    /// <summary>
    /// Mirrors the D1-C timeout tests: SQLite stores decimals as text, which
    /// makes the payment CHECK constraints unreliable there (production SQL
    /// Server uses real numerics). Constraints stay enforced by the domain
    /// guards and by the SQL Server concurrency test below.
    /// </summary>
    private static async Task<DeliveryServiceTests.DeliveryHarness> CreateHarnessAsync()
    {
        var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        return harness;
    }

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

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static async Task ChooseReDeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId) =>
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);

    private static async Task ApproveRedeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId)
    {
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
    }

    private static RefundReplacementService CreateChoiceService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    private static SubscriptionService CancelService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        new FixedBranchAllocationService(harness.Branch),
        new NotSupportedPaymentService(),
        harness.TimeProvider,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

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

    private static async Task<Payment> SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        PaymentMethod method)
    {
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            method,
            harness.Subscription.PayableAmount,
            "INR",
            $"cancel-sub-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(
            method == PaymentMethod.Wallet ? null : "pay_test_cancel123",
            method == PaymentMethod.Wallet ? "WALLET" : "captured",
            harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        return payment;
    }

    private sealed record CustomTicketSeed(Guid RequestPublicId, Guid SubscriptionPublicId, decimal Payable);

    private static async Task<CustomTicketSeed> SeedCustomTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        decimal quantity,
        decimal unitPrice,
        int totalEntitlement,
        decimal deductionPercentage)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var branch = harness.Branch;
        var customer = harness.Customer;
        var staff = harness.Staff;

        var category = await db.ProductCategories.SingleAsync();
        var product = new Product(
            category.Id, $"CXL-{suffix}", "Cancel Milk", null, "litre", unitPrice);
        var address = new CustomerAddress(
            customer.Id, $"Cxl {suffix}", "7 Cancel Road", "Central", "Bengaluru", "Karnataka",
            "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
        db.AddRange(product, address);
        await db.SaveChangesAsync();

        var today = harness.TimeProvider.Today;
        var subscription = new Subscription(
            customer.Id, product.Id, address.Id, branch.Id, $"cancel-sub-{suffix}",
            today, today.AddDays(7), quantity, unitPrice, totalEntitlement,
            product.Sku, product.Name, product.UnitOfMeasure,
            branch.Code, branch.Name, "7 Cancel Road, Bengaluru");
        subscription.AddDelivery(today);
        subscription.Activate(now);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var occurrence = subscription.Deliveries.Single();

        var delivery = Delivery.ForSubscriptionOccurrence(
            occurrence.Id, customer.Id, branch.Id, today,
            $"SUB-CXL-{suffix}-{today:yyyyMMdd}", "Customer", "9999999999",
            "7 Cancel Road, Bengaluru", null, 12.9716m, 77.5946m);
        delivery.AssignDeliveryNumber($"DLV/CXL/{suffix}");
        delivery.Assign(staff.Id, staff.Id, now, null);
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync();

        subscription.MarkFailed(occurrence, now);
        delivery.Fail(
            staff.Id, now, DeliveryFailureReasons.CustomerNotAvailable, "No response", null, null);
        var ticket = new RefundReplacementRequest(
            null, delivery.Id, customer.Id, branch.Id, null,
            RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
            DeliveryFailureReasons.CustomerNotAvailable, null, now, now.AddHours(24),
            occurrence.Id, now, 24, deductionPercentage,
            DeliveryFailureReasons.CustomerNotAvailable,
            RefundReplacementStatus.AwaitingCustomerChoice);
        ticket.AssignRequestNumber($"RR/CXL/{suffix}");
        db.RefundReplacementRequests.Add(ticket);

        var payment = Payment.CreateForSubscription(
            subscription.Id, customer.Id, PaymentMethod.Wallet,
            subscription.PayableAmount, "INR", $"cancel-pay-{suffix}", now.AddMinutes(15));
        payment.Succeed(null, "WALLET", now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new CustomTicketSeed(ticket.PublicId, subscription.PublicId, subscription.PayableAmount);
    }

    private static DeliveryFailTimeoutProcessor TimeoutProcessor(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new DeliveryFailTimeoutOptions()),
        Options.Create(new PaymentOptions { Currency = "INR" }),
        NullLogger<DeliveryFailTimeoutProcessor>.Instance);

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

    private sealed class NotSupportedPaymentService : IPaymentService
    {
        public Task<PaymentResult> CreateAsync(long c, CreatePaymentRequest r, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never create payments.");
        public Task<PaymentResult> CreateForSubscriptionAsync(long c, long s, PaymentMethod m, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never create payments.");
        public Task<PaymentResult> RetrySubscriptionAsync(long c, Guid s, PaymentMethod m, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never retry payments.");
        public Task<PaymentResult> CreateWalletTopUpAsync(long c, decimal a, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never top up.");
        public Task<PaymentResult> VerifyAsync(long c, VerifyPaymentRequest r, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never verify payments.");
        public Task<PaymentResult> CompleteDevelopmentAsync(long c, Guid p, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never complete payments.");
        public Task<PaymentResult> CancelAsync(long c, Guid p, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never cancel payments.");
        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never query capabilities.");
        public Task<PaymentResult> GetAsync(long u, Guid p, bool b, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never read payments.");
        public Task<RefundResult> RefundAsync(long r, Guid p, RefundPaymentRequest q, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never gateway-refund.");
        public Task<PaymentReconciliationResult> ReconcileAsync(long r, Guid p, bool b, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never reconcile.");
        public Task ProcessWebhookAsync(byte[] p, string s, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never process webhooks.");
    }

    /// <summary>
    /// Throwaway SQL Server database so concurrent cancels exercise real
    /// serializable locking and unique-key contention instead of SQLite.
    /// </summary>
    private sealed class SqlCancelHarness : IAsyncDisposable
    {
        private const string SqlServerInstance = @".\SQLEXPRESS";
        private readonly string connectionString;

        private SqlCancelHarness(string connectionString, Guid subscriptionPublicId, long customerId)
        {
            this.connectionString = connectionString;
            SubscriptionPublicId = subscriptionPublicId;
            CustomerId = customerId;
        }

        public Guid SubscriptionPublicId { get; }
        public long CustomerId { get; }

        public DoodhDirectDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlServer(
                    connectionString,
                    sql =>
                    {
                        sql.CommandTimeout(60);
                        sql.EnableRetryOnFailure(maxRetryCount: 5);
                    })
                .Options;
            return new DoodhDirectDbContext(options);
        }

        public SubscriptionService CreateCancelService(DoodhDirectDbContext db)
        {
            var clock = new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified));
            var time = new TestIndiaTimeProvider(clock);
            return new SubscriptionService(
                db,
                new StaticBranchAllocationService(db),
                new NotSupportedPaymentService(),
                time,
                new TestNotificationEventWriter(db, time));
        }

        public static async Task<SqlCancelHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_Cancel_{Guid.NewGuid():N}";
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = SqlServerInstance,
                InitialCatalog = databaseName,
                IntegratedSecurity = true,
                Encrypt = true,
                TrustServerCertificate = true,
                MultipleActiveResultSets = true
            };
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlServer(builder.ConnectionString, sql => sql.CommandTimeout(60))
                .Options;
            var db = new DoodhDirectDbContext(options);
            try
            {
                await db.Database.EnsureCreatedAsync();
            }
            catch (SqlException ex)
            {
                await db.DisposeAsync();
                throw new InvalidOperationException(
                    $"This test requires the local SQL Server Express instance ('{SqlServerInstance}').", ex);
            }

            var clock = new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified));
            var time = new TestIndiaTimeProvider(clock);
            var now = time.Now;
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            customer.SetContact("9999999999", null);
            var staff = new User(UserType.Employee);
            staff.SetProfile("Support Agent");
            staff.SetContact("9000000011", null);
            db.AddRange(branch, customer, staff);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            db.AddRange(product, address);
            await db.SaveChangesAsync();

            var subscription = new Subscription(
                customer.Id, product.Id, address.Id, branch.Id, "cancel-sql-1",
                DateOnly.FromDateTime(now), DateOnly.FromDateTime(now).AddDays(7),
                1m, 80m, 2, product.Sku, product.Name, product.UnitOfMeasure,
                branch.Code, branch.Name, "1 Main Road, Bengaluru");
            subscription.AddDelivery(DateOnly.FromDateTime(now));
            subscription.Activate(now);
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
            var occurrence = subscription.Deliveries.Single();

            var delivery = Delivery.ForSubscriptionOccurrence(
                occurrence.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now),
                $"SUB-{subscription.PublicId:N}-{DateOnly.FromDateTime(now):yyyyMMdd}",
                "Customer", "9999999999", "1 Main Road, Bengaluru", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber("DLV/MAIN/000001");
            delivery.Assign(staff.Id, staff.Id, now, null);
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();

            subscription.MarkFailed(occurrence, now);
            delivery.Fail(
                staff.Id, now, DeliveryFailureReasons.CustomerNotAvailable, "No response", null, null);
            var ticket = new RefundReplacementRequest(
                null, delivery.Id, customer.Id, branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                DeliveryFailureReasons.CustomerNotAvailable, null, now, now.AddHours(24),
                occurrence.Id, now, 24, 10m, DeliveryFailureReasons.CustomerNotAvailable,
                RefundReplacementStatus.AwaitingCustomerChoice);
            ticket.AssignRequestNumber("RR/TEST/000001");
            db.RefundReplacementRequests.Add(ticket);
            await db.SaveChangesAsync();

            ticket.RecordCustomerChoice(CustomerChoice.ReDelivery, now);
            await db.SaveChangesAsync();

            var choiceService = new RefundReplacementService(
                db,
                time,
                new Infrastructure.RefundReplacement.RefundReplacementConfigurationService(db, time),
                new NumberSeriesService(db, time),
                new ThrowawayMediaStorage(),
                new TestNotificationEventWriter(db, time));
            var staffActor = new RefundReplacementActor(staff.Id, new HashSet<long> { branch.Id }, true);
            await choiceService.ApproveAsync(
                staffActor, ticket.PublicId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

            var payment = Payment.CreateForSubscription(
                subscription.Id, customer.Id, PaymentMethod.Wallet,
                subscription.PayableAmount, "INR", "cancel-sql-pay-1", now.AddMinutes(15));
            payment.Succeed(null, "WALLET", now);
            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            var result = new SqlCancelHarness(
                builder.ConnectionString, subscription.PublicId, customer.Id);
            await db.DisposeAsync();
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CreateContext();
            await db.Database.EnsureDeletedAsync();
        }

        private sealed class StaticBranchAllocationService(DoodhDirectDbContext db) : IBranchAllocationService
        {
            public async Task<BranchAllocationResult> AllocateAsync(
                decimal latitude,
                decimal longitude,
                IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
                CancellationToken cancellationToken)
            {
                var branch = await db.Branches.AsNoTracking().SingleAsync(cancellationToken);
                return new BranchAllocationResult(
                    branch.Id, branch.PublicId, branch.Code, branch.Name, 0m);
            }
        }

        private sealed class ThrowawayMediaStorage : IMediaStorage
        {
            public Task<StoredMediaResult> SaveAsync(
                string storageKey, Stream content, string contentType, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Approval records no media.");

            public Task<StoredMediaContent> OpenReadAsync(
                string storageKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Approval records no media.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
