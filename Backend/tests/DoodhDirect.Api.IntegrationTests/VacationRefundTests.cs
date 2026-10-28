using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Setup;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Configuration;
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
/// End-of-subscription automatic vacation-refund requests: one consolidated
/// Refund request per naturally completed prepaid subscription, settled only
/// through the authorized completion workflow.
/// </summary>
public sealed class VacationRefundTests
{
    private static readonly DateTime RecordVacationAt = new(2026, 7, 31, 9, 30, 0, DateTimeKind.Unspecified);
    private static readonly DateOnly TermStart = new(2026, 8, 1);
    private static readonly DateOnly TermEnd = new(2026, 8, 8);

    [Fact]
    public async Task SevenVacationSkips_CreateOneConsolidatedRequestWithSevenOccurrences()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-basic");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));

        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        Assert.Equal(7, outcome.IncludedCount);
        Assert.Empty(outcome.Excluded);
        Assert.Null(outcome.DeferredReason);
        Assert.False(outcome.AlreadyProcessed);

        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(VacationRefundStatus.Pending, request.Status);
        Assert.Equal("Customer vacation", request.Reason);
        Assert.Equal(0m, request.DeductionPercentage);
        Assert.Equal(7, request.OccurrenceCount);
        Assert.Equal(100m, request.GrossPerOccurrence);
        Assert.Equal(700m, request.TotalAmount);
        Assert.Equal(800m, request.PayableAmountSnapshot);
        Assert.Equal(8, request.TotalEntitlementSnapshot);
        Assert.False(string.IsNullOrWhiteSpace(request.RequestNumber));
        Assert.Equal(7, request.Occurrences.Count);
        Assert.Equal(7, request.Occurrences.Select(x => x.SubscriptionDeliveryId).Distinct().Count());
        Assert.All(request.Occurrences, x => Assert.Equal(100m, x.AllocationAmount));
        Assert.Equal(
            new DateOnly(2026, 8, 2),
            request.Occurrences.Min(x => x.ScheduledDate));
        Assert.Equal(
            new DateOnly(2026, 8, 8),
            request.Occurrences.Max(x => x.ScheduledDate));

        // Creation moves no money and approves nothing.
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Contains(
            await harness.Db.NotificationEvents.AsNoTracking().ToListAsync(),
            e => e.EventType == NotificationEventTypes.VacationRefundRequested);

        // The durable vacation cause distinguishes these skips from manual ones.
        var causes = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .Where(x => x.Status == SubscriptionDeliveryStatus.Skipped)
            .Select(x => x.SkipCause)
            .ToListAsync();
        Assert.All(causes, c => Assert.Equal(SubscriptionSkipCause.Vacation, c));
    }

    [Fact]
    public async Task SettledCustomerFaultOccurrence_Excluded_RetainedDeductionNeverRefundedAgain()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-fault");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        var payment = await harness.SeedPaymentAsync(subscription);

        // One of the seven vacation-skipped occurrences was already settled
        // through a customer-fault refund with a 20% deduction (80 credited,
        // 20 retained). Support-settled through the authoritative records.
        var settledDate = new DateOnly(2026, 8, 5);
        await harness.SettleOccurrenceAsync(subscription.PublicId, settledDate, deductionPercentage: 20m);
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        Assert.Equal(6, outcome.IncludedCount);
        var excluded = Assert.Single(outcome.Excluded);
        Assert.Equal(settledDate, excluded.ScheduledDate);
        Assert.Equal(VacationRefundService.ReasonSettledElsewhere, excluded.Reason);

        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(6, request.Occurrences.Count);
        Assert.Equal(600m, request.TotalAmount);
        Assert.DoesNotContain(request.Occurrences, x => x.ScheduledDate == settledDate);

        // The retained 20 deduction is never refunded again: approve and
        // complete, then the wallet holds exactly 80 (fault) + 600 (vacation).
        var staff = harness.StaffActor;
        var requestId = request.PublicId;
        await harness.VacationService.ApproveAsync(
            staff, requestId, new DecideVacationRefundRequest("Verified"), CancellationToken.None);
        var completed = await harness.VacationService.CompleteAsync(
            staff, requestId, new DecideVacationRefundRequest("Settled"), CancellationToken.None);
        Assert.Equal(VacationRefundStatus.Completed, completed.Status);

        Assert.Equal(680m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(680m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        var credits = await harness.Db.WalletTransactions.AsNoTracking().ToListAsync();
        Assert.Equal(7, credits.Count);
        Assert.DoesNotContain(credits, c => c.Amount == 20m);
    }

    [Fact]
    public async Task FullSettledAndFailedRemedyOccurrences_AreExcluded()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 4), unitPrice: 100m, key: "vacation-full");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 4));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);

        // Full settlement (0% deduction ticket, fully credited) on 03 Aug.
        await harness.SettleOccurrenceAsync(
            subscription.PublicId, new DateOnly(2026, 8, 3), deductionPercentage: 0m);
        // Failed original with no linked remedy money on 04 Aug: ambiguous,
        // excluded and reported rather than guessed.
        await harness.FailTicketWithoutRemedyAsync(subscription.PublicId, new DateOnly(2026, 8, 4));
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        Assert.Equal(1, outcome.IncludedCount);
        Assert.Equal(2, outcome.Excluded.Count);
        Assert.Contains(outcome.Excluded, x =>
            x.ScheduledDate == new DateOnly(2026, 8, 3) &&
            x.Reason == VacationRefundService.ReasonSettledElsewhere);
        Assert.Contains(outcome.Excluded, x =>
            x.ScheduledDate == new DateOnly(2026, 8, 4) &&
            x.Reason == VacationRefundService.ReasonAmbiguousSettlement);

        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(new DateOnly(2026, 8, 2), request.Occurrences.Single().ScheduledDate);
    }

    [Fact]
    public async Task AmbiguousSettlement_ExcludedAndReported_RequestStillCreatedForOthers()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-ambiguous");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);

        // Completed ticket with no refund row and no wallet credit: settlement
        // cannot be established confidently.
        await harness.CompleteTicketWithoutMoneyAsync(subscription.PublicId, new DateOnly(2026, 8, 6));
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        Assert.Equal(6, outcome.IncludedCount);
        var excluded = Assert.Single(outcome.Excluded);
        Assert.Equal(new DateOnly(2026, 8, 6), excluded.ScheduledDate);
        Assert.Equal(VacationRefundService.ReasonAmbiguousSettlement, excluded.Reason);

        var audits = await harness.Db.AuditLogs.AsNoTracking().ToListAsync();
        Assert.Contains(audits, a =>
            a.Action == "VACATION_REFUND.CREATE" &&
            a.Reason != null &&
            a.Reason.Contains(VacationRefundService.ReasonAmbiguousSettlement, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnresolvedTicket_DefersWholeSubscription_ThenProceedsAfterRejection()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-unresolved");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        await harness.PendingTicketAsync(subscription.PublicId, new DateOnly(2026, 8, 3));
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var deferred = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.False(deferred.RequestCreated);
        Assert.Equal("unresolvedTicketOverlap", deferred.DeferredReason);
        Assert.Equal(0, await harness.Db.VacationRefundRequests.AsNoTracking().CountAsync());
        Assert.Null((await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).VacationRefundProcessedAt);

        // The overlapping ticket is rejected (no money moved): a later cycle
        // safely includes the occurrence — no duplicate, no overlap.
        var ticket = await harness.Db.RefundReplacementRequests.SingleAsync();
        ticket.Reject(harness.Staff.Id, harness.TimeProvider.Now, "Not eligible");
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.True(outcome.RequestCreated);
        Assert.Equal(7, outcome.IncludedCount);
        Assert.Equal(1, await harness.Db.VacationRefundRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task IneligibleRecords_NeverIncluded()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 6), unitPrice: 100m, key: "vacation-mixed");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 2));
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 5));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 6));
        await harness.ManualSkipAsync(subscription.PublicId, new DateOnly(2026, 8, 3));
        await harness.FailAsync(subscription.PublicId, new DateOnly(2026, 8, 4));
        await harness.AddReplacementAsync(subscription.PublicId, new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 10));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();

        // A manual skip records the Manual cause durably.
        Assert.Equal(
            SubscriptionSkipCause.Manual,
            (await harness.Db.SubscriptionDeliveries.AsNoTracking()
                .SingleAsync(x => x.ScheduledDate == new DateOnly(2026, 8, 3))).SkipCause);

        harness.AdvanceTo(new DateTime(2026, 8, 12, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        Assert.Equal(2, outcome.IncludedCount);
        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(
            [new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 5)],
            request.Occurrences.Select(x => x.ScheduledDate).OrderBy(x => x));
        Assert.Contains(outcome.Excluded, x =>
            x.ScheduledDate == new DateOnly(2026, 8, 3) &&
            x.Reason == VacationRefundService.ReasonManualSkip);
    }

    [Fact]
    public async Task CancelledSubscription_ProcessedWithoutRequest()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-cancelled");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 3));
        await harness.SeedPaymentAsync(subscription);

        var tracked = await harness.Db.Subscriptions
            .Include(x => x.Deliveries)
            .SingleAsync(x => x.PublicId == subscription.PublicId);
        tracked.Cancel(harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.False(outcome.RequestCreated);
        Assert.Equal(0, await harness.Db.VacationRefundRequests.AsNoTracking().CountAsync());
        Assert.NotNull((await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).VacationRefundProcessedAt);
    }

    [Fact]
    public async Task MultipleVacationIntervals_HandledInOneRequest()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-intervals");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 3));
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 4));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 5));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 8));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        Assert.Equal(4, outcome.IncludedCount);
        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(
            [new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 7)],
            request.Occurrences.Select(x => x.ScheduledDate).OrderBy(x => x));
        Assert.Equal(400m, request.TotalAmount);
    }

    [Fact]
    public async Task FrozenPricingUsed_AfterCataloguePriceChange()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-frozen");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);

        // Catalogue repricing after purchase never affects the refund.
        var product = await harness.Db.Products.SingleAsync();
        product.Update(product.CategoryId, product.Sku, product.Name, product.Description, product.UnitOfMeasure, 500m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        var request = await harness.Db.VacationRefundRequests.AsNoTracking().SingleAsync();
        Assert.Equal(800m, request.PayableAmountSnapshot);
        Assert.Equal(100m, request.GrossPerOccurrence);
        Assert.Equal(700m, request.TotalAmount);
    }

    [Fact]
    public async Task DeductionAlwaysZero_DespiteCustomerFaultConfiguration()
    {
        await using var harness = await VacationHarness.CreateAsync();
        // The environment configures a 25% customer-fault deduction.
        harness.Db.SystemConfigurations.Add(new SystemConfiguration(
            RefundReplacementConfigurationKeys.DeliveryFailDeductionPercentage, "25", "decimal"));
        await harness.Db.SaveChangesAsync();

        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-zero");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        var request = await harness.Db.VacationRefundRequests.AsNoTracking().SingleAsync();
        Assert.Equal(0m, request.DeductionPercentage);
        Assert.Equal(700m, request.TotalAmount);
    }

    [Fact]
    public async Task RoundingAndCap_NeverOverRefund()
    {
        await using var harness = await VacationHarness.CreateAsync();
        // 999.99 / 3 = 333.33 per occurrence: exact, remainder-free.
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 4), unitPrice: 333.33m, key: "vacation-round");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 4));
        var payment = await harness.SeedPaymentAsync(subscription);

        // 50 already refunded elsewhere: remaining 949.99 caps the total and
        // the shortfall is absorbed from the last occurrence backwards.
        payment.StartRefund(50m, "elsewhere", "rounding-prior-key", harness.Staff.Id);
        var prior = payment.Refunds.Single(x => x.IdempotencyKey == "rounding-prior-key");
        prior.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(50m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(333.33m, request.GrossPerOccurrence);
        Assert.Equal(949.99m, request.TotalAmount);
        Assert.Equal(
            [333.33m, 333.33m, 283.33m],
            request.Occurrences.OrderBy(x => x.ScheduledDate).Select(x => x.AllocationAmount));
        Assert.Equal(
            request.TotalAmount,
            request.Occurrences.Sum(x => x.AllocationAmount));
    }

    [Fact]
    public async Task RepeatedExecution_DoesNotDuplicate()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-repeat");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));

        var first = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        var second = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(first.RequestCreated);
        Assert.False(second.RequestCreated);
        Assert.True(second.AlreadyProcessed);
        Assert.Equal(first.RequestPublicId, second.RequestPublicId);
        Assert.Equal(1, await harness.Db.VacationRefundRequests.AsNoTracking().CountAsync());
        Assert.Equal(7, await harness.Db.VacationRefundOccurrences.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ConcurrentExecution_CreatesSingleLogicalRequest()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-concurrent");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var subscriptionId = subscription.PublicId;
        harness.Db.ChangeTracker.Clear();

        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                await using var context = harness.CreateContext();
                var service = harness.CreateVacationService(context);
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        return await service.ProcessSubscriptionAsync(subscriptionId, CancellationToken.None);
                    }
                    catch (Exception exception) when (
                        attempt < 5 && IsTransientLock(exception))
                    {
                        await Task.Delay(50 * (attempt + 1));
                    }
                }
            })));

        Assert.Equal(2, outcomes.Length);
        await using var verify = harness.CreateContext();
        Assert.Equal(1, await verify.VacationRefundRequests.AsNoTracking().CountAsync());
        Assert.Equal(7, await verify.VacationRefundOccurrences.AsNoTracking().CountAsync());
        Assert.Single(outcomes, o => o.RequestCreated);
        Assert.All(outcomes, o => Assert.NotNull(o.RequestPublicId));
        Assert.Equal(outcomes[0].RequestPublicId, outcomes[1].RequestPublicId);
    }

    [Fact]
    public async Task PastDueUnresolvedFinalDay_ExcludedWithoutBlockingVacationRefund()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-finalday");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 7));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();

        // 08 Aug is still Scheduled past its date (a missed delivery outside the
        // vacation): it no longer defers the eligible vacation occurrences.
        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.True(outcome.RequestCreated);
        Assert.Equal(6, outcome.IncludedCount);
        var excluded = Assert.Single(outcome.Excluded);
        Assert.Equal(new DateOnly(2026, 8, 8), excluded.ScheduledDate);
        Assert.Equal(VacationRefundService.ReasonMissedDeliveryUnresolved, excluded.Reason);

        // The missed final day finalizes later through its own workflow: no
        // second vacation request is ever created for the subscription.
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 8));
        harness.Db.ChangeTracker.Clear();
        var second = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.False(second.RequestCreated);
        Assert.True(second.AlreadyProcessed);
        Assert.Equal(outcome.RequestPublicId, second.RequestPublicId);
        Assert.Equal(1, await harness.Db.VacationRefundRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task EndOfTerm_MissedFinalDayDoesNotBlockEligibleVacationRefund()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();

        // By 28 Oct the term has ended, but 25 Oct (outside the vacation)
        // was missed and never finalized: it stays Scheduled.
        harness.AdvanceTo(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        Assert.Equal(5, outcome.IncludedCount);
        var excluded = Assert.Single(outcome.Excluded);
        Assert.Equal(new DateOnly(2026, 10, 25), excluded.ScheduledDate);
        Assert.Equal(VacationRefundService.ReasonMissedDeliveryUnresolved, excluded.Reason);

        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(5, request.OccurrenceCount);
        Assert.Equal(500m, request.TotalAmount);
        Assert.DoesNotContain(request.Occurrences, x => x.ScheduledDate == new DateOnly(2026, 10, 25));
        Assert.DoesNotContain(request.Occurrences, x => x.ScheduledDate == new DateOnly(2026, 10, 19));

        // Retried processing issues nothing twice: the same logical request
        // is adopted and no second refund can be created.
        var retry = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.False(retry.RequestCreated);
        Assert.True(retry.AlreadyProcessed);
        Assert.Equal(outcome.RequestPublicId, retry.RequestPublicId);
        Assert.Equal(1, await harness.Db.VacationRefundRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task EndOfTerm_StatusShowsPendingThenRequested()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot-status");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();
        harness.AdvanceTo(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified));

        // Before the worker evaluates the subscription, the customer sees a
        // pending posture with counts — not an empty history with no context.
        // The subscription row still shows Active even though 25 Oct has
        // passed (skips never consume entitlement, so nothing completes it):
        // term completion is determined from the authoritative dates, never
        // from that stale status.
        Assert.Equal(
            SubscriptionStatus.Active,
            (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).Status);
        var pending = await harness.VacationService.GetStatusForCustomerAsync(
            harness.CustomerActor, subscription.PublicId, CancellationToken.None);
        Assert.Equal(subscription.PublicId, pending.SubscriptionPublicId);
        Assert.True(pending.TermEnded);
        Assert.False(pending.Processed);
        Assert.Null(pending.Request);
        Assert.Equal(5, pending.VacationSkippedCount);
        Assert.Equal(1, pending.UnresolvedInTermCount);
        Assert.False(pending.HasBlockingTicket);
        Assert.True(pending.HasBackingPayment);
        Assert.Null(pending.BlockedReason);

        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.True(outcome.RequestCreated);

        // After evaluation the created request is linked with its review
        // state: Pending explicitly means no refund has been issued yet.
        var requested = await harness.VacationService.GetStatusForCustomerAsync(
            harness.CustomerActor, subscription.PublicId, CancellationToken.None);
        Assert.True(requested.Processed);
        Assert.NotNull(requested.Request);
        Assert.Equal(VacationRefundStatus.Pending, requested.Request.Status);
        Assert.Equal(5, requested.Request.OccurrenceCount);
        Assert.Equal(500m, requested.Request.TotalAmount);
        Assert.Null(requested.BlockedReason);
    }

    [Fact]
    public async Task EndOfTerm_StatusShowsBlockedOnUnresolvedTicket()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot-blocked");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        await harness.PendingTicketAsync(subscription.PublicId, new DateOnly(2026, 10, 21));
        harness.Db.ChangeTracker.Clear();
        harness.AdvanceTo(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified));

        var status = await harness.VacationService.GetStatusForCustomerAsync(
            harness.CustomerActor, subscription.PublicId, CancellationToken.None);
        Assert.True(status.TermEnded);
        Assert.False(status.Processed);
        Assert.Null(status.Request);
        Assert.True(status.HasBlockingTicket);
        Assert.Equal("unresolvedTicketOverlap", status.BlockedReason);

        var deferred = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.False(deferred.RequestCreated);
        Assert.Equal("unresolvedTicketOverlap", deferred.DeferredReason);
    }

    [Fact]
    public async Task EndOfTerm_StaffQueue_BranchScopedActorSeesOwnBranchOnly()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot-staff");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();
        harness.AdvanceTo(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified));

        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.True(outcome.RequestCreated);
        var requestId = outcome.RequestPublicId!.Value;

        // A branch-scoped manager of the owning branch (e.g. Dairy Manager)
        // sees the pending request in the staff queue without global access.
        var branchActor = new RefundReplacementActor(
            harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);
        var queue = await harness.VacationService.ListForStaffAsync(
            branchActor, CancellationToken.None);
        Assert.Equal(requestId, Assert.Single(queue).RequestId);
        var detail = await harness.VacationService.GetForStaffAsync(
            branchActor, requestId, CancellationToken.None);
        Assert.Equal(VacationRefundStatus.Pending, detail.Status);

        // A manager scoped to an unrelated branch sees nothing: out-of-scope
        // requests are reported as missing, never as forbidden.
        var otherActor = new RefundReplacementActor(
            harness.Staff.Id, new HashSet<long> { harness.Branch.Id + 1000 }, false);
        Assert.Empty(await harness.VacationService.ListForStaffAsync(
            otherActor, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.VacationService.GetForStaffAsync(
                otherActor, requestId, CancellationToken.None));
    }

    [Fact]
    public async Task EndOfTerm_BranchManagerCanApproveAndCompleteCustomerOriginatedRequest()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot-decide");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();
        harness.AdvanceTo(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified));

        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.True(outcome.RequestCreated);

        // A non-global branch manager decides the customer-originated
        // request through the same authorized workflow: approval moves no
        // money, completion credits the frozen total exactly once.
        var branchActor = new RefundReplacementActor(
            harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);
        var approved = await harness.VacationService.ApproveAsync(
            branchActor, outcome.RequestPublicId!.Value,
            new DecideVacationRefundRequest("Verified against subscription"),
            CancellationToken.None);
        Assert.Equal(VacationRefundStatus.Approved, approved.Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());

        var completed = await harness.VacationService.CompleteAsync(
            branchActor, outcome.RequestPublicId!.Value,
            new DecideVacationRefundRequest("Settled"),
            CancellationToken.None);
        Assert.Equal(VacationRefundStatus.Completed, completed.Status);
        Assert.Equal(500m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task EndOfTerm_StatusHiddenFromUnrelatedCustomer()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot-private");
        harness.Db.ChangeTracker.Clear();

        var stranger = new RefundReplacementActor(999999, new HashSet<long>(), false);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.VacationService.GetStatusForCustomerAsync(
                stranger, subscription.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task EndOfTerm_PartialPaymentCap_CapsVacationTotal()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot-cap");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        var payment = await harness.SeedPaymentAsync(subscription);

        // 250 already refunded elsewhere: only 450 remains for the 500 of
        // eligible vacation occurrences, so the shortfall is absorbed from
        // the last occurrence backwards and the payment is never over-refunded.
        payment.StartRefund(250m, "elsewhere", "eot-prior-key", harness.Staff.Id);
        var prior = payment.Refunds.Single(x => x.IdempotencyKey == "eot-prior-key");
        prior.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(250m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        harness.AdvanceTo(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified));
        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);

        Assert.True(outcome.RequestCreated);
        var request = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(450m, request.TotalAmount);
        Assert.Equal(
            [100m, 100m, 100m, 100m, 50m],
            request.Occurrences.OrderBy(x => x.ScheduledDate).Select(x => x.AllocationAmount));
        Assert.Equal(
            request.TotalAmount,
            request.Occurrences.Sum(x => x.AllocationAmount));
    }

    [Fact]
    public async Task EndOfTerm_MissedFinalDaySettledSeparatelyWithFullRefund()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 25), unitPrice: 100m, key: "vacation-eot-separate");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 24));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 10, 19));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();
        harness.AdvanceTo(new DateTime(2026, 10, 28, 9, 30, 0, DateTimeKind.Unspecified));

        // The missed 25 Oct was never operationalized, so no ordinary fail
        // flow could reach it. The manager confirms the miss through the
        // missed-occurrence path: the ticket freezes a zero deduction because
        // no service was provided — never the customer-fault percentage, and
        // never through the vacation request.
        harness.Db.NumberSeries.Add(new NumberSeries(
            "DELIVERY_MAIN", "Delivery MAIN", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
        harness.Db.NumberSeries.Add(new NumberSeries(
            RefundReplacementService.SeriesCode,
            "Refund/replacement request numbers",
            "RR/{NUMBER:000000}",
            1, 1, NumberSeriesResetPolicy.Never));
        await harness.Db.SaveChangesAsync();
        var occurrenceId = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.ScheduledDate == new DateOnly(2026, 10, 25))
            .Select(x => x.PublicId)
            .SingleAsync();
        var failed = await CreateDeliveryService(harness).FailMissedOccurrenceAsync(
            new DeliveryActor(harness.Staff.Id, new List<long> { harness.Branch.Id }),
            harness.Branch.Id,
            occurrenceId,
            new FailMissedOccurrenceRequest("Vehicle issue", "Depot missed the final run."),
            CancellationToken.None);
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        var ticketId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var missedTicket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.True(missedTicket.NoServiceProvided);
        Assert.Equal(0m, missedTicket.DeliveryFailDeductionPercentageSnapshot);
        Assert.False(missedTicket.ProofImageRequired);

        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        Assert.True(outcome.RequestCreated);
        Assert.Equal(5, outcome.IncludedCount);
        Assert.DoesNotContain(outcome.Excluded, x => x.ScheduledDate == new DateOnly(2026, 10, 25));

        var staff = harness.StaffActor;
        var requestId = outcome.RequestPublicId!.Value;
        await harness.VacationService.ApproveAsync(
            staff, requestId, new DecideVacationRefundRequest("Verified"), CancellationToken.None);
        await harness.VacationService.CompleteAsync(
            staff, requestId, new DecideVacationRefundRequest("Settled"), CancellationToken.None);

        var refunds = CreateRefundService(harness);
        await refunds.ChooseAsync(
            harness.CustomerActor, ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await refunds.ApproveAsync(
            harness.StaffActor, ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await refunds.CompleteAsync(
            harness.StaffActor, ticketId,
            new DecideRefundReplacementRequest("Miss confirmed; full no-service refund."),
            CancellationToken.None);

        // 500 of vacation refund (0% deduction) plus the full 100 for the
        // missed day (no deduction: no service was provided): separate
        // policies, one payment cap, no overlap and no duplicate credit.
        Assert.Equal(600m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(600m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(6, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        var vacationRequest = await harness.Db.VacationRefundRequests
            .Include(x => x.Occurrences)
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(500m, vacationRequest.TotalAmount);
        Assert.Equal(5, vacationRequest.Occurrences.Count);
    }

    [Fact]
    public async Task MissedRun_RecoveredByProcessDue()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-missed");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        harness.Db.ChangeTracker.Clear();

        // No direct processing happened (worker was down): the next due scan
        // picks the subscription up and creates exactly one request.
        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));
        var created = await harness.VacationService.ProcessDueAsync(CancellationToken.None);
        Assert.Equal(1, created);
        Assert.Equal(1, await harness.Db.VacationRefundRequests.AsNoTracking().CountAsync());

        // A subsequent scan finds nothing due.
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(0, await harness.VacationService.ProcessDueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoWalletCredit_OnCreateOrApprove_OnlyOnComplete()
    {
        await using var harness = await VacationHarness.CreateAsync();
        var subscription = await harness.SeedSubscriptionAsync(
            TermStart, TermEnd, unitPrice: 100m, key: "vacation-lifecycle");
        await harness.RecordVacationAsync(subscription.PublicId, new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 8));
        await harness.DeliverAsync(subscription.PublicId, new DateOnly(2026, 8, 1));
        await harness.SeedPaymentAsync(subscription);
        harness.AdvanceTo(new DateTime(2026, 8, 10, 9, 30, 0, DateTimeKind.Unspecified));

        var outcome = await harness.VacationService.ProcessSubscriptionAsync(
            subscription.PublicId, CancellationToken.None);
        var requestId = outcome.RequestPublicId!.Value;
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());

        var approved = await harness.VacationService.ApproveAsync(
            harness.StaffActor, requestId, new DecideVacationRefundRequest("Looks right"), CancellationToken.None);
        Assert.Equal(VacationRefundStatus.Approved, approved.Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());

        // The customer history distinguishes created from approved: neither
        // means the refund was issued.
        var mine = await harness.VacationService.ListForCustomerAsync(
            harness.CustomerActor, CancellationToken.None);
        Assert.Equal(VacationRefundStatus.Approved, Assert.Single(mine).Status);

        var completed = await harness.VacationService.CompleteAsync(
            harness.StaffActor, requestId, new DecideVacationRefundRequest("Settled"), CancellationToken.None);
        Assert.Equal(VacationRefundStatus.Completed, completed.Status);
        Assert.Equal(700m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(7, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(700m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(
            700m,
            (await harness.Db.WalletTransactions.AsNoTracking().ToListAsync()).Sum(x => x.Amount));

        var events = await harness.Db.NotificationEvents
            .AsNoTracking()
            .Where(x => x.EventKey.StartsWith($"vacation-refund:{requestId:N}"))
            .OrderBy(x => x.Id)
            .Select(x => x.EventType)
            .ToListAsync();
        Assert.Equal(
            [
                NotificationEventTypes.VacationRefundRequested,
                NotificationEventTypes.VacationRefundApproved,
                NotificationEventTypes.VacationRefundCompleted
            ],
            events);
    }

    private static DeliveryService CreateDeliveryService(VacationHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        new TestPasswordHasher(),
        new NoopOtpDeliveryService(),
        new NullDeliveryRealtimePublisher(),
        Options.Create(new DeliveryOptions()),
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        harness.NumberSeriesService,
        new DeliveryOtpHandoffProtector(new EphemeralDataProtectionProvider()),
        refundReplacementConfigurationService: new RefundReplacementConfigurationService(
            harness.Db, harness.TimeProvider),
        refundReplacementMediaStorage: new MissedOccurrenceFailTests.ThrowingMediaStorage());

    private static RefundReplacementService CreateRefundService(VacationHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        new RefundReplacementConfigurationService(harness.Db, harness.TimeProvider),
        harness.NumberSeriesService,
        new MissedOccurrenceFailTests.ThrowingMediaStorage(),
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }));

    private sealed class NoopOtpDeliveryService : IOtpDeliveryService
    {
        public Task SendAsync(string destination, string code, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static bool IsTransientLock(Exception exception)
    {
        var current = exception;
        while (current is not null)
        {
            if (current.Message.Contains("locked", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("busy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    private sealed class VacationHarness : IAsyncDisposable
    {
        private readonly SqliteConnection keeper;

        private VacationHarness(
            SqliteConnection keeper,
            string connectionString,
            DoodhDirectDbContext db,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            User customer,
            User staff,
            Branch branch,
            Product product,
            CustomerAddress address,
            NumberSeriesService numberSeriesService,
            SubscriptionService subscriptionService,
            VacationRefundService vacationService)
        {
            this.keeper = keeper;
            ConnectionString = connectionString;
            Db = db;
            Clock = clock;
            TimeProvider = timeProvider;
            Customer = customer;
            Staff = staff;
            Branch = branch;
            Product = product;
            Address = address;
            NumberSeriesService = numberSeriesService;
            SubscriptionService = subscriptionService;
            VacationService = vacationService;
        }

        public string ConnectionString { get; }
        public DoodhDirectDbContext Db { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public User Customer { get; }
        public User Staff { get; }
        public Branch Branch { get; }
        public Product Product { get; }
        public CustomerAddress Address { get; }
        public NumberSeriesService NumberSeriesService { get; }
        public SubscriptionService SubscriptionService { get; }
        public VacationRefundService VacationService { get; }

        public RefundReplacementActor StaffActor =>
            new(Staff.Id, new HashSet<long>(), true);

        public RefundReplacementActor CustomerActor =>
            new(Customer.Id, new HashSet<long>(), false);

        public void AdvanceTo(DateTime indiaLocalNow) =>
            Clock.Advance(indiaLocalNow - Clock.Now);

        public DoodhDirectDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(ConnectionString, sqlite => sqlite.CommandTimeout(10))
                .Options;
            return new DoodhDirectDbContext(options);
        }

        public VacationRefundService CreateVacationService(DoodhDirectDbContext context) => new(
            context,
            TimeProvider,
            new NumberSeriesService(context, TimeProvider),
            new TestNotificationEventWriter(context, TimeProvider));

        public static async Task<VacationHarness> CreateAsync()
        {
            var clock = new TestClock(RecordVacationAt);
            var timeProvider = new TestIndiaTimeProvider(clock);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"vacation-refund-tests-{Guid.NewGuid():N}",
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

            db.NumberSeries.Add(new NumberSeries(
                VacationRefundService.SeriesCode,
                "Vacation-refund request numbers",
                "VR/{NUMBER:000000}",
                1,
                1,
                NumberSeriesResetPolicy.Never));

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var staff = new User(UserType.Employee);
            staff.SetProfile("Support Staff");
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            db.AddRange(customer, staff, branch);
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
            var subscriptionService = new SubscriptionService(
                db,
                new FixedBranchAllocation(branch),
                new ThrowingPaymentService(),
                timeProvider,
                eventWriter,
                numberSeriesService);
            var vacationService = new VacationRefundService(
                db, timeProvider, numberSeriesService, eventWriter);
            return new VacationHarness(
                keeper, connectionString, db, clock, timeProvider, customer, staff,
                branch, product, address, numberSeriesService, subscriptionService, vacationService);
        }

        public async Task<Subscription> SeedSubscriptionAsync(
            DateOnly startDate,
            DateOnly endDate,
            decimal unitPrice,
            string key)
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

            subscription.Activate(TimeProvider.Now);
            Db.Subscriptions.Add(subscription);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return await Db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == subscription.Id);
        }

        public async Task RecordVacationAsync(Guid subscriptionPublicId, DateOnly from, DateOnly to)
        {
            await SubscriptionService.CreateVacationAsync(
                Customer.Id,
                new CreateVacationRequest(from, to),
                CancellationToken.None);
            Db.ChangeTracker.Clear();
        }

        public async Task ManualSkipAsync(Guid subscriptionPublicId, DateOnly date)
        {
            var delivery = await Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.ScheduledDate == date);
            await SubscriptionService.SkipAsync(
                Customer.Id,
                subscriptionPublicId,
                new SkipSubscriptionDeliveryRequest(delivery.PublicId),
                CancellationToken.None);
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

        public async Task FailAsync(Guid subscriptionPublicId, DateOnly date)
        {
            var subscription = await Db.Subscriptions
                .Include(x => x.Deliveries)
                .SingleAsync(x => x.PublicId == subscriptionPublicId);
            subscription.MarkFailed(
                subscription.Deliveries.Single(x => x.ScheduledDate == date),
                TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task<Payment> SeedPaymentAsync(Subscription subscription)
        {
            var payment = Payment.CreateForSubscription(
                subscription.Id,
                Customer.Id,
                PaymentMethod.Wallet,
                subscription.PayableAmount,
                "INR",
                $"vacation-pay-{subscription.PublicId:N}",
                TimeProvider.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", TimeProvider.Now);
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return await Db.Payments.SingleAsync(x => x.Id == payment.Id);
        }

        /// <summary>
        /// Settles one vacation-skipped occurrence through the authoritative
        /// per-delivery records: a Completed ticket with its frozen deduction
        /// plus the succeeded refund row and wallet credit under the ticket's
        /// deterministic key.
        /// </summary>
        public async Task SettleOccurrenceAsync(
            Guid subscriptionPublicId,
            DateOnly date,
            decimal deductionPercentage)
        {
            var occurrence = await Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.ScheduledDate == date);
            var subscription = await Db.Subscriptions.AsNoTracking().SingleAsync();
            var now = TimeProvider.Now;
            Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                occurrence.Id,
                Customer.Id,
                Branch.Id,
                occurrence.ScheduledDate,
                "SUB-VACATION",
                "Customer",
                "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001",
                null,
                12.9716m,
                77.5946m));
            await Db.SaveChangesAsync();
            var deliveryId = await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == occurrence.Id)
                .Select(x => x.Id)
                .SingleAsync();

            var ticket = new RefundReplacementRequest(
                null,
                deliveryId,
                Customer.Id,
                Branch.Id,
                null,
                RefundReplacementType.Refund,
                RefundReplacementSource.DeliveryFailed,
                "Customer not available",
                null,
                now,
                now.AddHours(48),
                occurrence.Id,
                now,
                48,
                deductionPercentage,
                "CustomerNotAvailable");
            ticket.AssignRequestNumber($"RR/VAC/{date:ddMM}");
            ticket.Approve(Staff.Id, now, "Support settled");
            ticket.Complete(Staff.Id, now, "Support settled");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();

            var gross = decimal.Round(subscription.PayableAmount / subscription.TotalEntitlement, 2, MidpointRounding.AwayFromZero);
            var deduction = decimal.Round(gross * deductionPercentage / 100m, 2, MidpointRounding.AwayFromZero);
            var final = gross - deduction;
            // Same deterministic namespace as the delivery-fail settlement
            // (delivery-fail:{ticket}:refund): the authoritative evidence
            // the vacation processor inspects.
            var key = $"delivery-fail:{ticket.PublicId:N}:refund";
            var payment = await Db.Payments.Include(x => x.Refunds).SingleAsync();
            var refund = payment.StartRefund(final, "Customer-fault settlement", key, Staff.Id);
            refund.Succeed(now);
            payment.CompleteRefund(final);
            var wallet = await Db.Wallets.SingleOrDefaultAsync(x => x.CustomerId == Customer.Id);
            if (wallet is null)
            {
                wallet = new Wallet(Customer.Id, "INR");
                Db.Wallets.Add(wallet);
            }
            wallet.Credit(
                WalletTransactionType.RefundCredit, final, key,
                $"Customer-fault settlement", now, payment.Id, subscriptionId: subscription.Id);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>
        /// A Completed ticket with no refund row and no wallet credit:
        /// settlement cannot be established confidently.
        /// </summary>
        public async Task CompleteTicketWithoutMoneyAsync(Guid subscriptionPublicId, DateOnly date)
        {
            var occurrence = await Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.ScheduledDate == date);
            var now = TimeProvider.Now;
            Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                occurrence.Id, Customer.Id, Branch.Id, occurrence.ScheduledDate,
                "SUB-VACATION", "Customer", "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m));
            await Db.SaveChangesAsync();
            var deliveryId = await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == occurrence.Id)
                .Select(x => x.Id)
                .SingleAsync();
            var ticket = new RefundReplacementRequest(
                null, deliveryId, Customer.Id, Branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                "Customer not available", null, now, now.AddHours(48),
                occurrence.Id, now, 48, 10m, "CustomerNotAvailable");
            ticket.AssignRequestNumber($"RR/AMB/{date:ddMM}");
            ticket.Approve(Staff.Id, now, "Support settled");
            ticket.Complete(Staff.Id, now, "Support settled");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>A Failed original ticket with no linked remedy money.</summary>
        public async Task FailTicketWithoutRemedyAsync(Guid subscriptionPublicId, DateOnly date)
        {
            var occurrence = await Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.ScheduledDate == date);
            var now = TimeProvider.Now;
            Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                occurrence.Id, Customer.Id, Branch.Id, occurrence.ScheduledDate,
                "SUB-VACATION", "Customer", "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m));
            await Db.SaveChangesAsync();
            var deliveryId = await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == occurrence.Id)
                .Select(x => x.Id)
                .SingleAsync();
            var ticket = new RefundReplacementRequest(
                null, deliveryId, Customer.Id, Branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                "Customer not available", null, now, now.AddHours(48),
                occurrence.Id, now, 48, 10m, "CustomerNotAvailable");
            ticket.AssignRequestNumber($"RR/FAIL/{date:ddMM}");
            ticket.Approve(Staff.Id, now, "Approved re-delivery");
            ticket.MarkRemedyFailed(Staff.Id, now, "Replacement failed without remedy");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task PendingTicketAsync(Guid subscriptionPublicId, DateOnly date)
        {
            var occurrence = await Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.ScheduledDate == date);
            var now = TimeProvider.Now;
            Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                occurrence.Id, Customer.Id, Branch.Id, occurrence.ScheduledDate,
                "SUB-VACATION", "Customer", "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m));
            await Db.SaveChangesAsync();
            var deliveryId = await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == occurrence.Id)
                .Select(x => x.Id)
                .SingleAsync();
            var ticket = new RefundReplacementRequest(
                null, deliveryId, Customer.Id, Branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                "Customer not available", null, now, now.AddHours(48),
                occurrence.Id, now, 48, 10m, "CustomerNotAvailable");
            ticket.AssignRequestNumber($"RR/PEND/{date:ddMM}");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>
        /// Routes a pending re-delivery replacement from a failed occurrence.
        /// The replacement stays Scheduled beyond the term: it belongs to the
        /// remedy workflow, never to the vacation set.
        /// </summary>
        public async Task AddReplacementAsync(Guid subscriptionPublicId, DateOnly failedDate, DateOnly replacementDate)
        {
            var now = TimeProvider.Now;
            var failed = await Db.SubscriptionDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.ScheduledDate == failedDate);
            Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                failed.Id, Customer.Id, Branch.Id, failed.ScheduledDate,
                "SUB-VACATION", "Customer", "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m));
            await Db.SaveChangesAsync();
            var deliveryId = await Db.Deliveries
                .AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == failed.Id)
                .Select(x => x.Id)
                .SingleAsync();
            var ticket = new RefundReplacementRequest(
                null, deliveryId, Customer.Id, Branch.Id, null,
                RefundReplacementType.Replacement, RefundReplacementSource.DeliveryFailed,
                "Re-delivery", null, now, now.AddHours(48),
                failed.Id, now, 48, 0m, "CustomerNotAvailable");
            ticket.AssignRequestNumber("RR/REPL/01");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();

            var subscription = await Db.Subscriptions
                .Include(x => x.Deliveries)
                .SingleAsync(x => x.PublicId == subscriptionPublicId);
            subscription.AddReplacementDelivery(
                subscription.Deliveries.Single(x => x.ScheduledDate == failedDate),
                replacementDate,
                ticket.Id,
                "customer requested");
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await keeper.DisposeAsync();
        }
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
            throw new NotSupportedException("Vacation tests seed payments directly.");
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
}
