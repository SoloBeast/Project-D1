using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Audit remediation regression tests.
///
/// Part A: an explicitly Refund-chosen first failure pays at the authoritative
/// staff completion point (never on choice or approval), reusing the shared
/// frozen D1-C refund under the same per-ticket idempotency key as the
/// timeout path. Part B: refund-only second-failure tickets cannot be
/// rejected or approved-without-choice, so the original ticket is never
/// stranded by destroying its only remedy.
/// </summary>
public sealed class RefundReplacementExplicitRefundTests
{
    [Theory]
    [InlineData(PaymentMethod.Wallet)]
    [InlineData(PaymentMethod.Development)]
    public async Task ExplicitRefund_Approve_Complete_CreditsFrozenShare(PaymentMethod method)
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await SeedSubscriptionPaymentAsync(harness, method);
        var service = CreateRefundService(harness);

        // Choice alone moves no money.
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());

        // Approval alone moves no money and creates no replacement.
        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.False(approved.IsRefundOnly);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());

        // Completion executes the frozen refund: payable 160 / 2 = 80 gross,
        // 10% snapshot = 8 deduction, final 72 — under the timeout namespace.
        var completed = await service.CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);

        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(72m, walletTxn.Amount);
        Assert.Equal(WalletTransactionType.RefundCredit, walletTxn.Type);
        Assert.Equal($"delivery-fail:{requestId:N}:refund", walletTxn.IdempotencyKey);
        Assert.Equal(72m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(72m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Equal($"delivery-fail:{requestId:N}:refund", refund.IdempotencyKey);

        // The failed occurrence is preserved; frozen financials untouched.
        var occurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionDeliveryStatus.Failed, occurrence.Status);
        Assert.False(occurrence.IsReplacement);
        Assert.Equal(
            harness.Subscription.PayableAmount,
            (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).PayableAmount);

        var completeAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.COMPLETE").ToListAsync());
        Assert.Contains("FirstFailureStaffRefund", completeAudit.NewValueJson ?? string.Empty);
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementCompleted).ToListAsync());
        Assert.Empty(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == FailedRemedyResolution.ResolvedAction).ToListAsync());
    }

    [Fact]
    public async Task TimeoutThenExplicitCompletion_CreditsExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);

        // Explicit completion first: the later timeout run is a no-op.
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        harness.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Null(await TimeoutProcessor(harness).ProcessTicketAsync(requestId, CancellationToken.None));
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ExplicitCompletionAfterTimeout_IsRejectedWithoutDuplication()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);

        // Timeout first (no recorded choice): pays and completes.
        harness.Clock.Advance(TimeSpan.FromHours(25));
        await TimeoutProcessor(harness).ProcessTicketAsync(requestId, CancellationToken.None);

        // A later staff completion is rejected; nothing duplicates.
        await Assert.ThrowsAsync<ConflictException>(() => CreateRefundService(harness).CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("late"),
            CancellationToken.None));
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task RefundCapRespected_AgainstPartiallyRefundedPayment()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);

        // Payment already refunded 100 of 160 elsewhere: only 60 remains.
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            harness.Subscription.PayableAmount,
            "INR",
            $"cap-setup-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        var prior = payment.StartRefund(100m, "setup", "cap-setup-key", harness.Customer.Id);
        prior.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(100m);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        var completed = await service.CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("capped"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(60m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(160m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, (await harness.Db.Payments.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task InfeasibleRefund_RollsBackStaysApprovedAndRetryable()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        // No covering payment: completion fails before any write; Approved is
        // retained for repair and retry — never a false success.
        await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("attempt"),
            CancellationToken.None));
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());

        // Recovery: seeding the payment lets the same completion succeed.
        // The failed scope's tracked (rolled-back) mutation is discarded —
        // production gives every request a fresh scope, so Clear mirrors a
        // staff retry arriving as a new request.
        harness.Db.ChangeTracker.Clear();
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);
        var completed = await service.CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("recovered"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(72m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task RejectRefundOnly_BlockedAtService_OriginalStaysApprovable()
    {
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        var second = await service.GetForStaffAsync(StaffActor(harness), secondId, CancellationToken.None);
        Assert.True(second.IsRefundOnly);
        var first = await service.GetForStaffAsync(StaffActor(harness), firstId, CancellationToken.None);
        Assert.False(first.IsRefundOnly);
        var page = await service.ListForStaffAsync(
            StaffActor(harness),
            new RefundReplacementListQuery(null, null, 1, 20),
            CancellationToken.None);
        Assert.True(page.Items.Single(x => x.RequestId == secondId).IsRefundOnly);
        Assert.False(page.Items.Single(x => x.RequestId == firstId).IsRefundOnly);

        // Rejection is refused even though the ticket is otherwise decidable.
        var rejection = await Assert.ThrowsAsync<BusinessRuleException>(() => service.RejectAsync(
            StaffActor(harness), secondId,
            new DecideRefundReplacementRequest("won't pay"),
            CancellationToken.None));
        Assert.Contains("refund", rejection.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == firstId)).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ApproveRefundOnly_WithoutChoice_BlockedTimeoutStillAvailable()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness);
        var service = CreateRefundService(harness);

        // Approving a refund-only remedy with no recorded choice would strand
        // it (Approved is invisible to the timeout worker and uncompletable).
        await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null), CancellationToken.None));
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);

        // Customer abandonment still resolves through the automatic timeout
        // refund and closes the original as Failed.
        await SeedSubscriptionPaymentAsync(harness, PaymentMethod.Wallet);
        harness.Clock.Advance(TimeSpan.FromHours(25));
        var outcome = await TimeoutProcessor(harness).ProcessTicketAsync(secondId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(72m, outcome!.WalletCredit);
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync();
        Assert.Equal(RefundReplacementStatus.Failed, tickets.Single(x => x.PublicId != secondId).Status);
        Assert.Equal(RefundReplacementStatus.Completed, tickets.Single(x => x.PublicId == secondId).Status);
    }

    [Fact]
    public async Task RejectOrdinary_PreservedForNonRefundOnlyTickets()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);

        var rejected = await service.RejectAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("not eligible"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Rejected, rejected.Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task OneTimeCompletion_BlockedWithoutSettlement_StaysApproved()
    {
        // D1-D order-refund safety: an order-backed Refund ticket cannot
        // reach Completed on the strength of the staff call alone. Without
        // a verified Payments settlement for the order, completion is
        // refused before any state transition: the ticket stays Approved
        // (recoverable), no money moves, and no COMPLETE audit is written.
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);

        var approved = await service.ApproveAsync(
            StaffActor(harness), ticketId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.False(approved.IsRefundOnly);

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("settled out of system"),
            CancellationToken.None));
        Assert.Contains("no verified refund settlement", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Empty(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.COMPLETE").ToListAsync());
    }

    [Fact]
    public async Task OneTimeCompletion_SucceedsAfterVerifiedSettlement_CreatesNothing()
    {
        // The guard reuses the authoritative Payments settlement record and
        // creates nothing itself: after the external settlement exists, the
        // retry completes the ticket without adding wallet credits or refund
        // rows. A Failed (non-Succeeded) refund row does not satisfy it.
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("too early"),
            CancellationToken.None));
        harness.Db.ChangeTracker.Clear();

        var ticketOrderId = (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).OrderId;
        var order = await harness.Db.Orders.AsNoTracking().SingleAsync(x => x.Id == ticketOrderId);
        var payment = new Payment(
            order.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            order.PayableAmount,
            "INR",
            $"one-time-settlement-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        var settlement = payment.StartRefund(
            order.PayableAmount, "Order refund via Payments", "one-time-settlement-refund", harness.Manager.Id);
        settlement.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(order.PayableAmount);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var completed = await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("settlement verified"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        // Completion reused the settlement: exactly the seeded rows exist.
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.COMPLETE").ToListAsync());
    }

    [Fact]
    public async Task ConcurrentExplicitRefundCompleteOnSqlServer_CreditsExactlyOnce()
    {
        await using var harness = await SqlExplicitRefundHarness.CreateAsync();

        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();
        var firstService = harness.CreateRefundService(firstDb);
        var secondService = harness.CreateRefundService(secondDb);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.CompleteAsync(
                    harness.StaffActor, harness.TicketPublicId,
                    new DecideRefundReplacementRequest("first"),
                    CancellationToken.None),
                secondService.CompleteAsync(
                    harness.StaffActor, harness.TicketPublicId,
                    new DecideRefundReplacementRequest("second"),
                    CancellationToken.None)
            }.Select(async attempt =>
            {
                try
                {
                    return (Result: await attempt, Error: (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (Result: (RefundReplacementRequestResult?)null, Error: exception);
                }
            }));

        Assert.Single(outcomes, x => x.Result is not null);
        var failure = Assert.Single(outcomes, x => x.Result is null);
        Assert.IsType<ConflictException>(failure.Error);

        await using var verifyDb = harness.CreateContext();
        Assert.Equal(1, await verifyDb.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(72m, (await verifyDb.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(1, await verifyDb.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundReplacementStatus.Completed,
            (await verifyDb.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

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
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, false);

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }));

    private static DeliveryFailTimeoutProcessor TimeoutProcessor(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new DeliveryFailTimeoutOptions()),
        Options.Create(new PaymentOptions { Currency = "INR" }),
        NullLogger<DeliveryFailTimeoutProcessor>.Instance);

    private static async Task SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness, PaymentMethod method)
    {
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            method,
            harness.Subscription.PayableAmount,
            "INR",
            $"explicit-refund-pay-{method}-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed("seed-ref", method == PaymentMethod.Wallet ? "WALLET" : "Mock", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static async Task<Guid> FailAndApproveRedeliveryAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        return requestId;
    }

    private static async Task<Guid> FailReplacementAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var firstId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 18), CancellationToken.None);
        var replacementDeliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId)
            .SingleAsync();

        await harness.AssignAsync(replacementDeliveryId, harness.Staff);
        await harness.FailSubscriptionAsync(replacementDeliveryId, harness.Staff);

        harness.Db.ChangeTracker.Clear();
        return await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Where(x => x.PublicId != firstId)
            .Select(x => x.PublicId)
            .SingleAsync();
    }

    /// <summary>
    /// Seeds a directly-constructed order-backed one-time ticket (delivered
    /// order delivery → customer-submitted Refund request, Pending).
    /// </summary>
    private static async Task<Guid> SeedOneTimeTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var order = new Order(
            harness.Customer.Id,
            null,
            harness.Branch.Id,
            $"one-time-order-{Guid.NewGuid():N}",
            "ORD-EXPLICIT-1",
            200m,
            0m,
            harness.Branch.Code,
            harness.Branch.Name,
            "Home",
            "1 Main Road",
            null,
            "Central",
            "Bengaluru",
            "Karnataka",
            "560001",
            null,
            null,
            "Customer",
            "9999999999",
            12.9716m,
            77.5946m);
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var delivery = Delivery.ForOrder(
            order.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            DateOnly.FromDateTime(now),
            "REF-ONETIME-1",
            "Customer",
            "9999999999",
            "Home, 1 Main Road, Central, Bengaluru",
            null,
            12.9716m,
            77.5946m);
        delivery.AssignDeliveryNumber("DLV-ONETIME-1");
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync();

        delivery.Assign(harness.Staff.Id, harness.Staff.Id, now, null);
        delivery.PickUp(harness.Staff.Id, now, null);
        delivery.Start(harness.Staff.Id, now);
        delivery.Arrive(harness.Staff.Id, now);
        delivery.RecordOtpVerified(harness.Staff.Id, now);
        delivery.Complete(harness.Staff.Id, now, null);
        await db.SaveChangesAsync();

        var ticket = new RefundReplacementRequest(
            order.Id,
            delivery.Id,
            harness.Customer.Id,
            harness.Branch.Id,
            null,
            RefundReplacementType.Refund,
            RefundReplacementSource.CustomerPostDelivery,
            "Item was not satisfactory",
            null,
            now,
            now.AddHours(48));
        ticket.AssignRequestNumber("RR-ONETIME-1");
        db.RefundReplacementRequests.Add(ticket);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return ticket.PublicId;
    }

    /// <summary>
    /// Throwaway SQL Server database so concurrent explicit-refund
    /// completions exercise real serializable locking and unique-key
    /// contention instead of SQLite.
    /// </summary>
    private sealed class SqlExplicitRefundHarness : IAsyncDisposable
    {
        private const string SqlServerInstance = @".\SQLEXPRESS";
        private readonly string connectionString;

        private SqlExplicitRefundHarness(
            string connectionString,
            Guid ticketPublicId,
            long staffId,
            long branchId)
        {
            this.connectionString = connectionString;
            TicketPublicId = ticketPublicId;
            StaffActor = new RefundReplacementActor(staffId, new HashSet<long> { branchId }, false);
        }

        public Guid TicketPublicId { get; }
        public RefundReplacementActor StaffActor { get; }

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

        public RefundReplacementService CreateRefundService(DoodhDirectDbContext db)
        {
            var time = new TestIndiaTimeProvider(
                new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified)));
            return new RefundReplacementService(
                db,
                time,
                new RefundReplacementConfigurationService(db, time),
                new NumberSeriesService(db, time),
                new ThrowawayMediaStorage(),
                new TestNotificationEventWriter(db, time),
                Options.Create(new PaymentOptions { Currency = "INR" }));
        }

        public static async Task<SqlExplicitRefundHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_ExplicitRefund_{Guid.NewGuid():N}";
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

            var now = new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified);
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
            product.Activate();
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            db.AddRange(product, address);
            await db.SaveChangesAsync();

            // Two-entitlement subscription at 80/unit: frozen share math is
            // 160 / 2 = 80 gross, 10% deduction = 8, final 72.
            var subscription = new Subscription(
                customer.Id, product.Id, address.Id, branch.Id, "explicit-sql-1",
                DateOnly.FromDateTime(now), DateOnly.FromDateTime(now).AddDays(7),
                1m, 80m, 2, product.Sku, product.Name, product.UnitOfMeasure,
                branch.Code, branch.Name, "1 Main Road, Bengaluru");
            subscription.AddDelivery(DateOnly.FromDateTime(now));
            subscription.Activate(now);
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
            var occurrence = subscription.Deliveries.Single();

            var payment = Payment.CreateForSubscription(
                subscription.Id, customer.Id, PaymentMethod.Wallet,
                subscription.PayableAmount, "INR", "explicit-sql-pay-1", now.AddMinutes(15));
            payment.Succeed(null, "WALLET", now);
            db.Payments.Add(payment);
            await db.SaveChangesAsync();

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

            var harness = new SqlExplicitRefundHarness(
                builder.ConnectionString, ticket.PublicId, staff.Id, branch.Id);

            // Choice + approval settle before the race: both contenders race
            // only the completion (the money-moving point).
            var setupService = harness.CreateRefundService(db);
            await setupService.ChooseAsync(
                new RefundReplacementActor(customer.Id, new HashSet<long>(), false),
                ticket.PublicId,
                new ChooseRefundReplacementRequest(CustomerChoice.Refund),
                CancellationToken.None);
            await setupService.ApproveAsync(
                harness.StaffActor,
                ticket.PublicId,
                new ApproveRefundReplacementRequest(null),
                CancellationToken.None);
            await db.DisposeAsync();
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = CreateContext();
            try
            {
                await db.Database.EnsureDeletedAsync();
            }
            catch
            {
                // Best-effort cleanup; the database name is unique per run.
            }
        }

        private sealed class ThrowawayMediaStorage : IMediaStorage
        {
            public Task<StoredMediaResult> SaveAsync(
                string storageKey, Stream content, string contentType, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Explicit-refund completion records no media.");

            public Task<StoredMediaContent> OpenReadAsync(
                string storageKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Explicit-refund completion records no media.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
