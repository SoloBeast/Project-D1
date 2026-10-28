using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 5.1 (A) — failed-replacement original-ticket resolution.
///
/// A failed re-delivery replacement routes into the refund-only remedy owned
/// by a second ticket. When that remedy is successfully resolved (the D1-C
/// timeout auto-refund: wallet credit + Refund row + second ticket Completed),
/// the original approved ReDelivery ticket — whose remedy can never be
/// fulfilled — is resolved in the same transaction with an explicit,
/// auditable non-delivery outcome. It is never marked delivered and moves no
/// second refund. A merely recorded choice, a pending refund, or a failed
/// refund never resolves the original.
/// </summary>
public sealed class RefundReplacementFailedReplacementResolutionTests
{
    [Fact]
    public async Task SecondFailureTimeout_ResolvesOriginalWithExplicitNonDeliveryOutcome()
    {
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: false);
        await SeedSubscriptionPaymentAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(1, outcome);

        // The second (refund-only) ticket resolved exactly like a D1-C refund.
        var second = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == secondId);
        Assert.Equal(RefundReplacementStatus.Completed, second.Status);
        Assert.Equal(CustomerChoice.Refund, second.CustomerChoice);
        Assert.Equal(RefundReplacementType.Refund, second.Type);

        // Frozen math: payable 160 / 2 = 80 gross, 10% snapshot = 8, final 72.
        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(72m, walletTxn.Amount);
        Assert.Equal(WalletTransactionType.RefundCredit, walletTxn.Type);
        Assert.Equal($"delivery-fail:{secondId:N}:refund", walletTxn.IdempotencyKey);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(72m, payment.RefundedAmount);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(72m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Equal($"delivery-fail:{secondId:N}:refund", refund.IdempotencyKey);

        // The original ticket resolved with an explicit NON-delivery outcome.
        // Rule 8: Completed keeps meaning successful fulfillment, so the
        // non-delivery terminal state must be Failed — never Completed.
        var original = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == firstId);
        Assert.Equal(RefundReplacementStatus.Failed, original.Status);
        Assert.NotEqual(RefundReplacementStatus.Completed, original.Status);
        Assert.Equal(CustomerChoice.ReDelivery, original.CustomerChoice);
        Assert.Equal(RefundReplacementType.Replacement, original.Type);
        Assert.Equal(harness.Customer.Id, original.CompletedByUserId);
        Assert.NotNull(original.CompletedAt);
        Assert.Contains("non-delivered", original.CompletionRemarks ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(second.RequestNumber, original.CompletionRemarks ?? string.Empty, StringComparison.Ordinal);

        var resolvedAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED").ToListAsync());
        Assert.Equal(firstId.ToString(), resolvedAudit.EntityId);
        Assert.Contains(RefundReplacementStatus.Approved.ToString(), resolvedAudit.OldValueJson ?? string.Empty);
        Assert.Contains("ReplacementFailedRefunded", resolvedAudit.NewValueJson ?? string.Empty);
        Assert.Contains(second.RequestNumber, resolvedAudit.NewValueJson ?? string.Empty);

        var resolvedEvent = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved).ToListAsync());
        Assert.Equal($"refund-replacement:{firstId:N}:redelivery-failed-resolved", resolvedEvent.EventKey);
        var payload = JsonSerializer.Deserialize<JsonElement>(resolvedEvent.PayloadJson);
        var variables = payload.GetProperty("Variables");
        Assert.Equal(firstId.ToString(), variables.GetProperty("requestId").GetString());
        Assert.Equal(second.RequestNumber, variables.GetProperty("refundRequestNumber").GetString());
        Assert.Equal("72.00", variables.GetProperty("walletCredit").GetString());
        Assert.DoesNotContain("SubscriptionDeliveryId", resolvedEvent.PayloadJson, StringComparison.Ordinal);

        // Both failed histories preserved; frozen subscription financials untouched.
        var occurrences = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(2, occurrences.Count);
        Assert.All(occurrences, x => Assert.Equal(SubscriptionDeliveryStatus.Failed, x.Status));
        Assert.Single(occurrences, x => x.IsReplacement);
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(harness.Subscription.PayableAmount, subscription.PayableAmount);
        Assert.Equal(harness.Subscription.ChargesTotal, subscription.ChargesTotal);
        Assert.Equal(0, subscription.UsedEntitlement);
    }

    [Fact]
    public async Task ExplicitRefundChoice_DoesNotResolveOriginal()
    {
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: false);
        await SeedSubscriptionPaymentAsync(harness);
        var service = CreateChoiceService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        // A recorded choice is not a successful refund: the timeout worker
        // ignores the ticket and the original stays Approved for staff.
        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(0, outcome);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        var second = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == secondId);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, second.Status);
        Assert.Equal(CustomerChoice.Refund, second.CustomerChoice);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == firstId)).Status);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED"));
    }

    [Fact]
    public async Task FailedRefund_DoesNotResolveOriginal_ThenSucceedsAfterPaymentRepair()
    {
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: false);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        // No payment to refund against: nothing resolves, nothing is half-written.
        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(0, outcome);
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == firstId)).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());

        // Once the payment state is repaired, the same refund resolves both tickets.
        harness.Db.ChangeTracker.Clear();
        await SeedSubscriptionPaymentAsync(harness);
        var repaired = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(1, repaired);
        Assert.Equal(
            RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);
        Assert.Equal(
            RefundReplacementStatus.Failed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == firstId)).Status);
        Assert.Equal(72m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Retry_AfterSuccessfulResolution_IsIdempotent()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: false);
        await SeedSubscriptionPaymentAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);
        Assert.Equal(1, outcome);

        // Retried resolution observes the resolved pair and performs nothing.
        harness.Db.ChangeTracker.Clear();
        var retry = await TimeoutProcessor(harness).ProcessTicketAsync(secondId, CancellationToken.None);
        Assert.Null(retry);
        Assert.Equal(0, await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None));

        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved));
    }

    [Fact]
    public async Task MilkTestRejectionSecondFailure_ResolvesOriginal()
    {
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: true);
        await SeedSubscriptionPaymentAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var secondSource = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == secondId);
        Assert.Equal(RefundReplacementSource.MilkTestRejected, secondSource.Source);

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(1, outcome);
        Assert.Equal(
            RefundReplacementStatus.Failed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == firstId)).Status);
        Assert.Equal(
            RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        var resolvedAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED").ToListAsync());
        Assert.Equal(firstId.ToString(), resolvedAudit.EntityId);
    }

    [Fact]
    public async Task OrdinaryFirstFailureTimeout_LeavesNoOriginalResolutionTrace()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(1, outcome);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED"));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved));
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

    private static async Task<Guid> FailAndApproveRedeliveryAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        await FailAsync(harness);
        var requestId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var service = CreateChoiceService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        return requestId;
    }

    /// <summary>
    /// Fails the approved replacement itself — via staff failure or milk-test
    /// rejection — and returns the second (refund-only) ticket's public id.
    /// </summary>
    private static async Task<Guid> FailReplacementAsync(
        DeliveryServiceTests.DeliveryHarness harness, bool failViaMilkTest)
    {
        var firstId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 18), CancellationToken.None);
        var replacementDeliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId)
            .SingleAsync();

        if (failViaMilkTest)
        {
            await harness.AdvanceToArrivedAsync(replacementDeliveryId, harness.Staff);
            var milkService = new MilkTestService(
                harness.Db,
                harness.TimeProvider,
                harness.ProofStorage,
                new PassThroughImageValidator(),
                new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
            var customerActor = new MilkTestActor(harness.Customer.Id, new HashSet<long>(), false);
            var staffActor = new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);
            var requested = await milkService.RequestAsync(customerActor, replacementDeliveryId, CancellationToken.None);
            await milkService.UploadImageAsync(
                staffActor, requested.MilkTestId, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
                "test.jpg", "image/jpeg", CancellationToken.None);
            await milkService.CompleteAsync(
                staffActor, requested.MilkTestId,
                new CompleteMilkTestRequest([new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")], null),
                CancellationToken.None);
            await milkService.RejectAsync(
                customerActor, requested.MilkTestId, new DecideMilkTestRequest("Milk was sour"), CancellationToken.None);
        }
        else
        {
            await harness.AssignAsync(replacementDeliveryId, harness.Staff);
            await harness.FailSubscriptionAsync(replacementDeliveryId, harness.Staff);
        }

        harness.Db.ChangeTracker.Clear();
        return await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Where(x => x.PublicId != firstId)
            .Select(x => x.PublicId)
            .SingleAsync();
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static RefundReplacementService CreateChoiceService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    private static DeliveryFailTimeoutProcessor TimeoutProcessor(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new DeliveryFailTimeoutOptions()),
        Options.Create(new PaymentOptions { Currency = "INR" }),
        NullLogger<DeliveryFailTimeoutProcessor>.Instance);

    private static async Task SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            harness.Subscription.PayableAmount,
            "INR",
            $"failed-resolution-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private sealed class PassThroughImageValidator : IMilkTestImageValidator
    {
        public long MaximumFileSize => 10L * 1024L * 1024L;

        public Task<ValidatedMilkTestImage> ValidateAsync(
            Stream content,
            string fileName,
            string? declaredContentType,
            CancellationToken cancellationToken)
        {
            var buffered = new MemoryStream();
            content.CopyTo(buffered);
            buffered.Position = 0;
            return Task.FromResult(new ValidatedMilkTestImage(
                Path.GetFileName(fileName),
                declaredContentType ?? "image/jpeg",
                buffered.Length,
                buffered));
        }
    }
}
