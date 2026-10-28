using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
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
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Deliveries;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Past-due undelivered subscription delivery (the 25 Oct 2026 case): an
/// occurrence that was never operationalized — no delivery row, no visit, no
/// service — must be confirmable by branch management through the existing
/// delivery-fail ticket workflow, and the confirmed miss refunds the full
/// eligible per-delivery prepaid amount with no customer-fault deduction.
/// </summary>
public sealed class MissedOccurrenceFailTests
{
    private static readonly DateOnly TermStart = new(2026, 10, 19);
    private static readonly DateOnly TermEnd = new(2026, 10, 25);
    private static readonly DateOnly MissedDate = new(2026, 10, 25);
    private static readonly DateTime TermStartAt = new(2026, 10, 19, 9, 30, 0, DateTimeKind.Unspecified);
    private static readonly DateTime AfterTermAt = new(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified);

    [Fact]
    public async Task MissedList_ShowsOnlyPastDueUnmaterializedOccurrence()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-list");
        await harness.SkipAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        harness.AdvanceTo(AfterTermAt);

        var missed = await harness.Deliveries.GetMissedOccurrencesAsync(
            harness.ManagerActor, harness.Branch.Id, CancellationToken.None);

        var single = Assert.Single(missed);
        Assert.Equal(MissedDate, single.ScheduledDate);
        Assert.Equal(3, single.DaysOverdue);
        Assert.Equal(harness.Branch.Id, single.BranchId);
        Assert.Equal(harness.Branch.Name, single.BranchName);
        Assert.Equal(harness.Customer.PublicId, single.CustomerId);
        Assert.Equal(100m, single.EstimatedRefundAmount);
        var occurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.ScheduledDate == MissedDate);
        Assert.Equal(occurrence.PublicId, single.OccurrenceId);
        Assert.Equal(subscription.PublicId, single.SubscriptionId);
    }

    [Fact]
    public async Task MissedList_BeforeDueDate_IsEmpty()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-not-yet");
        await harness.SkipAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));

        // On the term's first day nothing is past due yet.
        Assert.Empty(await harness.Deliveries.GetMissedOccurrencesAsync(
            harness.ManagerActor, harness.Branch.Id, CancellationToken.None));

        // The 19th is delivered on the day; on the missed day itself the 25th
        // occurrence is due, not past due.
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        harness.AdvanceTo(new DateTime(2026, 10, 25, 9, 30, 0, DateTimeKind.Unspecified));
        Assert.Empty(await harness.Deliveries.GetMissedOccurrencesAsync(
            harness.ManagerActor, harness.Branch.Id, CancellationToken.None));
    }

    [Fact]
    public async Task MissedList_GlobalActorSeesBranchScope_UnrelatedBranchSeesNothing()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-scope");
        await harness.SkipAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        harness.AdvanceTo(AfterTermAt);

        var global = new DeliveryActor(harness.Manager.Id, new List<long> { harness.Branch.Id }, HasGlobalAccess: true);
        Assert.Single(await harness.Deliveries.GetMissedOccurrencesAsync(
            global, harness.Branch.Id, CancellationToken.None));

        // An actor scoped to an unrelated branch sees nothing and cannot act.
        var other = new DeliveryActor(harness.Manager.Id, new List<long> { harness.OtherBranch.Id });
        Assert.Empty(await harness.Deliveries.GetMissedOccurrencesAsync(
            other, harness.OtherBranch.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() => harness.Deliveries.GetMissedOccurrencesAsync(
            other, harness.Branch.Id, CancellationToken.None));
    }

    [Fact]
    public async Task FailMissed_CreatesFailedDeliveryAndZeroDeductionTicket()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-fail");
        await harness.SkipAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        harness.AdvanceTo(AfterTermAt);
        var occurrenceId = await harness.OccurrencePublicIdAsync(MissedDate);

        var failed = await harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, occurrenceId,
            new FailMissedOccurrenceRequest("Vehicle issue", "Van never left the depot; no visit attempted."),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Equal(MissedDate, failed.ScheduledDate);
        Assert.NotNull(failed.FailedAt);
        Assert.Equal("Vehicle issue", failed.FailureReason);
        Assert.Null(failed.AssignedEmployeeId);
        Assert.False(string.IsNullOrWhiteSpace(failed.ReferenceNumber));

        var occurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.ScheduledDate == MissedDate);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, occurrence.Status);

        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(RefundReplacementSource.DeliveryFailed, ticket.Source);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        Assert.Equal(occurrence.Id, ticket.SubscriptionDeliveryId);
        Assert.True(ticket.NoServiceProvided);
        Assert.Equal(0m, ticket.DeliveryFailDeductionPercentageSnapshot);
        Assert.False(ticket.ProofImageRequired);
        Assert.Equal(failed.FailedAt, ticket.FailedAt);
        Assert.Equal(ticket.FailedAt!.Value.AddHours(24), ticket.Deadline);
        Assert.False(string.IsNullOrWhiteSpace(ticket.RequestNumber));

        // Exactly one customer decision request and one operational event.
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRequired));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.DeliveryFailed));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.MISSED_OCCURRENCE_FAIL"));

        // Other occurrences are untouched.
        var others = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.ScheduledDate != MissedDate).OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(6, others.Count);
        Assert.Single(others.Where(x => x.Status == SubscriptionDeliveryStatus.Delivered));
        Assert.Equal(5, others.Count(x => x.Status == SubscriptionDeliveryStatus.Skipped));
    }

    [Fact]
    public async Task FailMissed_FullRefundThroughChooseApproveComplete()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-full");
        await harness.SkipAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.AdvanceTo(AfterTermAt);
        var ticketId = await harness.FailMissedAsync(MissedDate);

        await harness.Refunds.ChooseAsync(
            harness.CustomerActor, ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await harness.Refunds.ApproveAsync(
            harness.StaffActor, ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        var completed = await harness.Refunds.CompleteAsync(
            harness.StaffActor, ticketId,
            new DecideRefundReplacementRequest("Miss confirmed; full no-service refund."),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.True(ticket.NoServiceProvided);
        Assert.Equal(
            0m,
            DeliveryFailRefund.EffectiveDeductionPercentage(
                ticket.Source,
                ticket.SubscriptionDeliveryId,
                ticket.AppliedDeductionPercentage,
                ticket.DeliveryFailDeductionPercentageSnapshot));

        // Full per-delivery share: 700 payable / 7 = 100 with zero deduction.
        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(100m, walletTxn.Amount);
        Assert.Equal($"delivery-fail:{ticketId:N}:refund", walletTxn.IdempotencyKey);
        Assert.Equal(100m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(100m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(100m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);

        // Subscription financials and the other entitlements are unchanged.
        var refreshed = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(700m, refreshed.PayableAmount);
        Assert.Equal(0m, refreshed.ChargesTotal);
        Assert.Equal(1, refreshed.UsedEntitlement);
        Assert.Equal(0, await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .CountAsync(x => x.Status == SubscriptionDeliveryStatus.Scheduled));
    }

    [Fact]
    public async Task FailMissed_IncludesFrozenChargesInFullRefund()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, 100m, "missed-charges",
            [new SubscriptionCharge("GST", "GST-18", "GST 18%", 18m, 700m, 70m)]);
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.AdvanceTo(AfterTermAt);
        var ticketId = await harness.FailMissedAsync(MissedDate);

        await harness.Refunds.ChooseAsync(
            harness.CustomerActor, ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await harness.Refunds.ApproveAsync(
            harness.StaffActor, ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await harness.Refunds.CompleteAsync(
            harness.StaffActor, ticketId,
            new DecideRefundReplacementRequest("Settled with charges."),
            CancellationToken.None);

        // Payable 770 / 7 = 110 gross, zero deduction: frozen charges included.
        var refreshed = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(770m, refreshed.PayableAmount);
        Assert.Equal(70m, refreshed.ChargesTotal);
        Assert.Equal(110m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(110m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
    }

    [Fact]
    public async Task FailMissed_PaymentCapRespected()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-cap");
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        var payment = await harness.SeedPaymentAsync(subscription);

        // 650 already refunded elsewhere: only 50 of the 700 payable remains.
        var prior = payment.StartRefund(650m, "setup prior refund", "missed-cap-prior", harness.Customer.Id);
        prior.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(650m);
        var wallet = new Wallet(harness.Customer.Id, "INR");
        harness.Db.Wallets.Add(wallet);
        await harness.Db.SaveChangesAsync();
        wallet.Credit(
            WalletTransactionType.RefundCredit, 650m, "missed-cap-prior-wallet",
            "setup", harness.TimeProvider.Now, payment.Id,
            subscriptionId: subscription.Id);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(AfterTermAt);
        var ticketId = await harness.FailMissedAsync(MissedDate);
        await harness.Refunds.ChooseAsync(
            harness.CustomerActor, ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await harness.Refunds.ApproveAsync(
            harness.StaffActor, ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await harness.Refunds.CompleteAsync(
            harness.StaffActor, ticketId,
            new DecideRefundReplacementRequest("Capped settlement."),
            CancellationToken.None);

        // The 100 share is capped to the remaining 50; never over-refunded.
        Assert.Equal(700m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        var refreshed = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(700m, refreshed.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, refreshed.Status);
        Assert.Equal(50m, await harness.Db.WalletTransactions.AsNoTracking()
            .Where(x => x.IdempotencyKey == $"delivery-fail:{ticketId:N}:refund")
            .Select(x => x.Amount).SingleAsync());
    }

    [Fact]
    public async Task FailMissed_RetryIsIdempotent_TimeoutRefundsFullAmountOnce()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-retry");
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.AdvanceTo(AfterTermAt);
        var occurrenceId = await harness.OccurrencePublicIdAsync(MissedDate);
        var request = new FailMissedOccurrenceRequest("Other", "Depot missed the run; retrying.");

        var first = await harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, occurrenceId, request, CancellationToken.None);
        var second = await harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, occurrenceId, request, CancellationToken.None);

        Assert.Equal(first.DeliveryId, second.DeliveryId);
        Assert.Equal(1, await harness.Db.Deliveries.AsNoTracking()
            .CountAsync(x => x.PublicId == first.DeliveryId));
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RefundReplacementChoiceRequired));

        var ticketId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        harness.Clock.Advance(TimeSpan.FromHours(25));
        var outcome = await harness.Timeouts.ProcessTicketAsync(ticketId, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(100m, outcome.GrossRefund);
        Assert.Equal(0m, outcome.DeductionAmount);
        Assert.Equal(100m, outcome.WalletCredit);
        Assert.Null(await harness.Timeouts.ProcessTicketAsync(ticketId, CancellationToken.None));
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task FailMissed_RejectsTodayAndFutureOccurrences()
    {
        await using var harness = await MissedHarness.CreateAsync();
        await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-notpast");

        // On the missed day itself the occurrence is due, not past due.
        harness.AdvanceTo(new DateTime(2026, 10, 25, 9, 30, 0, DateTimeKind.Unspecified));
        var occurrenceId = await harness.OccurrencePublicIdAsync(MissedDate);
        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, occurrenceId,
            new FailMissedOccurrenceRequest("Other", "Too early."),
            CancellationToken.None));
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FailMissed_RejectsSkippedDeliveredAndReplacedOccurrences()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-guards");
        await harness.SkipAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        harness.AdvanceTo(AfterTermAt);

        // A vacation-skipped date is never a missed delivery.
        var skippedId = await harness.OccurrencePublicIdAsync(new DateOnly(2026, 10, 20));
        await Assert.ThrowsAsync<ConflictException>(() => harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, skippedId,
            new FailMissedOccurrenceRequest("Other", "Vacation."),
            CancellationToken.None));

        // A delivered occurrence is never a missed delivery.
        var deliveredId = await harness.OccurrencePublicIdAsync(new DateOnly(2026, 10, 19));
        await Assert.ThrowsAsync<ConflictException>(() => harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, deliveredId,
            new FailMissedOccurrenceRequest("Other", "Delivered."),
            CancellationToken.None));

        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FailMissed_RejectsWhenOperationalDeliveryExists()
    {
        await using var harness = await MissedHarness.CreateAsync();
        await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-hasrow");

        // The 25th was materialized on the day, so the delivery flow owns it.
        harness.AdvanceTo(new DateTime(2026, 10, 25, 9, 30, 0, DateTimeKind.Unspecified));
        await harness.Deliveries.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 10, 25), CancellationToken.None);
        harness.AdvanceTo(AfterTermAt);

        var occurrenceId = await harness.OccurrencePublicIdAsync(MissedDate);
        var error = await Assert.ThrowsAsync<ConflictException>(() => harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, occurrenceId,
            new FailMissedOccurrenceRequest("Other", "Has a row."),
            CancellationToken.None));
        Assert.Contains("delivery", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FailMissed_RejectsReplacementOccurrences()
    {
        await using var harness = await MissedHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-replacement");
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        harness.AdvanceTo(AfterTermAt);
        var ticketId = await harness.FailMissedAsync(MissedDate);

        // A re-delivery replacement scheduled afterwards keeps its own remedy.
        var loaded = await harness.Db.Subscriptions
            .Include(x => x.Deliveries)
            .SingleAsync(x => x.PublicId == subscription.PublicId);
        var failedOccurrence = loaded.Deliveries.Single(x => x.ScheduledDate == MissedDate);
        var ticketLongId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Where(x => x.PublicId == ticketId).Select(x => x.Id).SingleAsync();
        var replacement = loaded.AddReplacementDelivery(
            failedOccurrence, new DateOnly(2026, 10, 26), ticketLongId, "Re-run");
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, replacement.PublicId,
            new FailMissedOccurrenceRequest("Other", "Replacement."),
            CancellationToken.None));
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task FailMissed_RejectsUnsupportedReason()
    {
        await using var harness = await MissedHarness.CreateAsync();
        await harness.SeedSubscriptionAsync(TermStart, TermEnd, 100m, "missed-reason");
        harness.AdvanceTo(AfterTermAt);
        var occurrenceId = await harness.OccurrencePublicIdAsync(MissedDate);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Deliveries.FailMissedOccurrenceAsync(
            harness.ManagerActor, harness.Branch.Id, occurrenceId,
            new FailMissedOccurrenceRequest("Not a supported reason", null),
            CancellationToken.None));
        Assert.Empty(await harness.Db.Deliveries.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UnattemptedFailure_RequiresReadyForAssignment()
    {
        var delivery = Delivery.ForSubscriptionOccurrence(
            7, 9, 11, new DateOnly(2026, 10, 25), "SUB-REF",
            "Customer", "9999999999", "Address", null, 12.9m, 77.6m);
        delivery.RecordUnattemptedFailure(
            new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified), "Vehicle issue", null);
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Throws<InvalidOperationException>(() => delivery.RecordUnattemptedFailure(
            new DateTime(2026, 10, 28, 10, 30, 0, DateTimeKind.Unspecified), "Vehicle issue", null));
    }

    private sealed class MissedHarness : IAsyncDisposable
    {
        private readonly SqliteConnection keeper;
        private readonly TestClock clock;

        private MissedHarness(
            SqliteConnection keeper,
            string connectionString,
            DoodhDirectDbContext db,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            User customer,
            User manager,
            Branch branch,
            Branch otherBranch,
            Product product,
            CustomerAddress address,
            NumberSeriesService numberSeriesService,
            RefundReplacementConfigurationService configurationService,
            DeliveryService deliveryService,
            RefundReplacementService refundService,
            DeliveryFailTimeoutProcessor timeoutProcessor,
            SubscriptionService subscriptionService)
        {
            this.keeper = keeper;
            this.clock = clock;
            Db = db;
            TimeProvider = timeProvider;
            Customer = customer;
            Manager = manager;
            Branch = branch;
            OtherBranch = otherBranch;
            Product = product;
            Address = address;
            Deliveries = deliveryService;
            Refunds = refundService;
            Timeouts = timeoutProcessor;
            SubscriptionService = subscriptionService;
        }

        public DoodhDirectDbContext Db { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public TestClock Clock => clock;
        public User Customer { get; }
        public User Manager { get; }
        public Branch Branch { get; }
        public Branch OtherBranch { get; }
        public Product Product { get; }
        public CustomerAddress Address { get; }
        public DeliveryService Deliveries { get; }
        public RefundReplacementService Refunds { get; }
        public DeliveryFailTimeoutProcessor Timeouts { get; }
        private SubscriptionService SubscriptionService { get; }

        public DeliveryActor ManagerActor => new(Manager.Id, [Branch.Id]);
        public RefundReplacementActor CustomerActor => new(Customer.Id, new HashSet<long>(), false);
        public RefundReplacementActor StaffActor => new(Manager.Id, new HashSet<long> { Branch.Id }, true);

        public void AdvanceTo(DateTime indiaLocalNow) => clock.Advance(indiaLocalNow - clock.Now);

        public static async Task<MissedHarness> CreateAsync()
        {
            var clock = new TestClock(TermStartAt);
            var timeProvider = new TestIndiaTimeProvider(clock);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"missed-occurrence-tests-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 10
            }.ToString();
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(10))
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");

            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY_MAIN", "Delivery MAIN", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY_NORTH", "Delivery NORTH", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NORTH"));
            db.NumberSeries.Add(new NumberSeries(
                RefundReplacementService.SeriesCode,
                "Refund/replacement request numbers",
                "RR/{NUMBER:000000}",
                1, 1, NumberSeriesResetPolicy.Never));

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var manager = new User(UserType.Employee);
            manager.SetProfile("Branch Manager");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var otherBranch = new Branch("NORTH", "North Branch", "Bengaluru", "Karnataka", 13.0358m, 77.5970m);
            db.AddRange(customer, manager, branch, otherBranch);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 100m);
            product.Activate();
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            db.AddRange(product, address);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var eventWriter = new TestNotificationEventWriter(db, timeProvider);
            var numberSeriesService = new NumberSeriesService(db, timeProvider);
            var configurationService = new RefundReplacementConfigurationService(db, timeProvider);
            var subscriptionService = new SubscriptionService(
                db,
                new FixedBranchAllocation(branch),
                new ThrowingPaymentService(),
                timeProvider,
                eventWriter,
                numberSeriesService);
            var deliveryService = new DeliveryService(
                db,
                timeProvider,
                new TestPasswordHasher(),
                new NoopOtpDeliveryService(),
                new NullDeliveryRealtimePublisher(),
                Options.Create(new DeliveryOptions()),
                eventWriter,
                numberSeriesService,
                new DeliveryOtpHandoffProtector(new EphemeralDataProtectionProvider()),
                refundReplacementConfigurationService: configurationService,
                refundReplacementMediaStorage: new ThrowingMediaStorage());
            var refundService = new RefundReplacementService(
                db,
                timeProvider,
                configurationService,
                numberSeriesService,
                new ThrowingMediaStorage(),
                eventWriter,
                Options.Create(new PaymentOptions { Currency = "INR" }));
            var timeoutProcessor = new DeliveryFailTimeoutProcessor(
                db,
                timeProvider,
                eventWriter,
                Options.Create(new DeliveryFailTimeoutOptions()),
                Options.Create(new PaymentOptions { Currency = "INR" }),
                NullLogger<DeliveryFailTimeoutProcessor>.Instance);
            return new MissedHarness(
                keeper, connectionString, db, clock, timeProvider, customer, manager,
                branch, otherBranch, product, address, numberSeriesService,
                configurationService, deliveryService, refundService, timeoutProcessor,
                subscriptionService);
        }

        public async Task<Subscription> SeedSubscriptionAsync(
            DateOnly startDate,
            DateOnly endDate,
            decimal unitPrice,
            string key,
            IReadOnlyCollection<SubscriptionCharge>? charges = null)
        {
            var subscription = new Subscription(
                Customer.Id,
                Product.Id,
                Address.Id,
                Branch.Id,
                key,
                startDate,
                endDate,
                1m,
                unitPrice,
                (int)(endDate.DayNumber - startDate.DayNumber + 1),
                Product.Sku,
                Product.Name,
                Product.UnitOfMeasure,
                Branch.Code,
                Branch.Name,
                "1 Main Road, Central, Bengaluru, Karnataka 560001");
            for (var date = startDate; date <= endDate; date = date.AddDays(1))
            {
                subscription.AddDelivery(date);
            }

            if (charges is not null)
            {
                subscription.AddCharges(charges);
            }

            subscription.Activate(TimeProvider.Now);
            Db.Subscriptions.Add(subscription);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return await Db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == subscription.Id);
        }

        public async Task SkipAsync(Guid subscriptionPublicId, DateOnly from, DateOnly to)
        {
            await SubscriptionService.CreateVacationAsync(
                Customer.Id, new CreateVacationRequest(from, to), CancellationToken.None);
            Db.ChangeTracker.Clear();
        }

        public async Task DeliverAsync(Guid subscriptionPublicId, DateOnly date)
        {
            var subscription = await Db.Subscriptions
                .Include(x => x.Deliveries)
                .SingleAsync(x => x.PublicId == subscriptionPublicId);
            subscription.MarkDelivered(
                subscription.Deliveries.Single(x => x.ScheduledDate == date),
                TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public Task<Guid> OccurrencePublicIdAsync(DateOnly date) =>
            Db.SubscriptionDeliveries.AsNoTracking()
                .Where(x => x.ScheduledDate == date)
                .Select(x => x.PublicId)
                .SingleAsync();

        public async Task<Guid> FailMissedAsync(DateOnly date)
        {
            var occurrenceId = await OccurrencePublicIdAsync(date);
            await Deliveries.FailMissedOccurrenceAsync(
                ManagerActor, Branch.Id, occurrenceId,
                new FailMissedOccurrenceRequest("Vehicle issue", "Depot missed the run."),
                CancellationToken.None);
            Db.ChangeTracker.Clear();
            return await Db.RefundReplacementRequests.AsNoTracking()
                .Select(x => x.PublicId).SingleAsync();
        }

        public async Task<Payment> SeedPaymentAsync(Subscription subscription)
        {
            var payment = Payment.CreateForSubscription(
                subscription.Id,
                Customer.Id,
                PaymentMethod.Wallet,
                subscription.PayableAmount,
                "INR",
                $"missed-pay-{subscription.PublicId:N}",
                TimeProvider.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", TimeProvider.Now);
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return await Db.Payments.SingleAsync(x => x.Id == payment.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await keeper.DisposeAsync();
        }

        private sealed class FixedBranchAllocation(Branch branch) : IBranchAllocationService
        {
            public Task<BranchAllocationResult> AllocateAsync(
                decimal latitude,
                decimal longitude,
                IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
                CancellationToken cancellationToken) =>
                Task.FromResult(new BranchAllocationResult(
                    branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
        }

        private sealed class ThrowingPaymentService : IPaymentService
        {
            public Task<PaymentResult> CreateForSubscriptionAsync(long c, long s, PaymentMethod m, string k, CancellationToken t) =>
                throw new NotSupportedException("Missed-occurrence tests seed payments directly.");
            public Task<PaymentResult> CreateWalletTopUpAsync(long c, decimal a, string k, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentResult> RetrySubscriptionAsync(long c, Guid s, PaymentMethod m, string k, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentPlanResult> CreateOrderPlanAsync(long c, Guid o, bool u, string k, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentPlanResult> CreateSubscriptionPlanAsync(long c, long s, bool u, string k, CancellationToken t, bool e = false) =>
                throw new NotSupportedException();
            public Task<PaymentResult> CompleteDevelopmentAsync(long c, Guid p, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentResult> CancelAsync(long c, Guid p, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentResult> CreateAsync(long c, CreatePaymentRequest r, string k, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentResult> VerifyAsync(long c, VerifyPaymentRequest r, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentResult> GetAsync(long u, Guid p, bool b, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<PaymentReconciliationResult> ReconcileAsync(long r, Guid p, bool b, CancellationToken t) =>
                throw new NotSupportedException();
            public Task<RefundResult> RefundAsync(long r, Guid p, RefundPaymentRequest q, CancellationToken t) =>
                throw new NotSupportedException();
            public Task ProcessWebhookAsync(byte[] p, string s, CancellationToken t) =>
                throw new NotSupportedException();
        }

        private sealed class NoopOtpDeliveryService : IOtpDeliveryService
        {
            public Task SendAsync(string destination, string code, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }

    /// <summary>
    /// The no-service path never captures, stores or reads proof media: the
    /// visit was never attempted, so any storage access fails loudly instead
    /// of silently succeeding.
    /// </summary>
    internal sealed class ThrowingMediaStorage : IMediaStorage
    {
        public Task<StoredMediaResult> SaveAsync(
            string storageKey, Stream content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No proof media is captured for an unattempted delivery.");

        public Task<StoredMediaContent> OpenReadAsync(
            string storageKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No proof media is captured for an unattempted delivery.");

        public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No proof media is captured for an unattempted delivery.");
    }
}
