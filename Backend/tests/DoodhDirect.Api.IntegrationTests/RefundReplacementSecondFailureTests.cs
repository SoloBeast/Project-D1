using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 4 — second-failure refund-only enforcement (Rule 5).
///
/// A failed replacement delivery must never generate another re-delivery.
/// Both failure paths (customer fault and milk-test rejection) converge on
/// the same ticket shape linked to the replacement occurrence, so choice-time
/// rejection plus an approval-time backstop cover every route. First-failure
/// choices, the refund lifecycle, timeout refunds, and financials are
/// unchanged.
/// </summary>
public sealed class RefundReplacementSecondFailureTests
{
    [Fact]
    public async Task FirstFailure_ReDelivery_Permitted()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);

        var result = await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);

        Assert.Equal(CustomerChoice.ReDelivery, result.CustomerChoice);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, result.Status);
    }

    [Fact]
    public async Task ReplacementFailure_ReDelivery_Rejected_RefundRemains()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: false);
        var service = CreateRefundService(harness);

        // The second ticket is linked to the replacement occurrence itself.
        var second = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == secondId);
        var replacement = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.IsReplacement);
        Assert.Equal(replacement.Id, second.SubscriptionDeliveryId);
        Assert.Equal(RefundReplacementSource.DeliveryFailed, second.Source);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ChooseAsync(
            CustomerActor(harness),
            secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None));
        Assert.Contains("refund", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Rejection records nothing: the ticket still awaits choice and a
        // refund choice afterwards succeeds (choice lifecycle intact).
        harness.Db.ChangeTracker.Clear();
        var pending = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == secondId);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, pending.Status);
        Assert.Null(pending.CustomerChoice);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.CUSTOMER_CHOICE"
                && x.EntityId == secondId.ToString()));

        var refunded = await service.ChooseAsync(
            CustomerActor(harness),
            secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None);
        Assert.Equal(CustomerChoice.Refund, refunded.CustomerChoice);
    }

    [Fact]
    public async Task ReplacementFailure_Refund_ApprovedWithoutNewReplacement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: false);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness),
            secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None);

        var approved = await service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null), CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Null(approved.ReplacementDate);
        Assert.Null(approved.ReplacementDeliveryPublicId);

        // Exactly one replacement exists (from the first failure); both failed
        // histories are preserved and nothing was consumed or repriced.
        var rows = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, x => Assert.Equal(SubscriptionDeliveryStatus.Failed, x.Status));
        Assert.Single(rows, x => x.IsReplacement);
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(0, subscription.UsedEntitlement);
        Assert.Equal(new DateOnly(2026, 8, 23), subscription.EndDate);
        Assert.Equal(harness.Subscription.PayableAmount, subscription.PayableAmount);
        Assert.Equal(harness.Subscription.ChargesTotal, subscription.ChargesTotal);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task MilkTestRejection_OnReplacement_ObeysRefundOnly()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: true);
        var service = CreateRefundService(harness);

        var second = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == secondId);
        Assert.Equal(RefundReplacementSource.MilkTestRejected, second.Source);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.ChooseAsync(
            CustomerActor(harness),
            secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        var refunded = await service.ChooseAsync(
            CustomerActor(harness),
            secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None);
        Assert.Equal(CustomerChoice.Refund, refunded.CustomerChoice);
    }

    [Fact]
    public async Task DirectApproval_WithReDeliveryChoiceOnReplacement_IsRejected()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var secondId = await FailReplacementAsync(harness, failViaMilkTest: false);

        // Bypass ChooseAsync the way only direct entity access can: record the
        // choice on the entity itself, then attempt approval.
        var ticket = await harness.Db.RefundReplacementRequests
            .SingleAsync(x => x.PublicId == secondId);
        ticket.RecordCustomerChoice(CustomerChoice.ReDelivery, harness.TimeProvider.Now);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var service = CreateRefundService(harness);
        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null), CancellationToken.None));
        Assert.Contains("refund", exception.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Single(await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement).ToListAsync());
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);
    }

    /// <summary>
    /// Drives a first failure through approval (replacement on 2026-08-18),
    /// then fails the replacement itself — via staff failure or milk-test
    /// rejection — and returns the second ticket's public id.
    /// </summary>
    private static async Task<Guid> FailReplacementAsync(
        DeliveryServiceTests.DeliveryHarness harness, bool failViaMilkTest)
    {
        await FailAsync(harness);
        var firstId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var choiceService = CreateRefundService(harness);
        await choiceService.ChooseAsync(
            CustomerActor(harness),
            firstId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);
        await choiceService.ApproveAsync(
            StaffActor(harness), firstId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        harness.Db.ChangeTracker.Clear();

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

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
            harness.Db,
            harness.TimeProvider,
            harness.ConfigurationService,
            new NumberSeriesService(harness.Db, harness.TimeProvider),
            harness.ProofStorage,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

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
