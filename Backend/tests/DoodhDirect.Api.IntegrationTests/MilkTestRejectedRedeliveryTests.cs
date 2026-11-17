using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// MilkTestRejected re-delivery workflow: customer rejects the doorstep milk
/// test, chooses Re-delivery, staff approval creates the replacement delivery
/// through the same failed-flow mechanism as customer-unavailable, linked to
/// the original failed delivery. Refund choice stays refund-only.
/// </summary>
public sealed class MilkTestRejectedRedeliveryTests
{
    [Fact]
    public async Task MilkRejected_RedeliveryChoice_ApprovalCreatesReplacement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);

        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(new DateOnly(2026, 8, 16), approved.ReplacementDate);
        Assert.NotNull(approved.ReplacementDeliveryPublicId);

        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementSource.MilkTestRejected, ticket.Source);
        Assert.Equal(CustomerChoice.ReDelivery, ticket.CustomerChoice);

        var rows = await harness.Db.SubscriptionDeliveries.AsNoTracking().OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(2, rows.Count);
        var replacement = Assert.Single(rows.Where(x => x.IsReplacement));
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.Equal(rows[0].Id, replacement.ReplacesSubscriptionDeliveryId);
        Assert.Equal(ticket.Id, replacement.RefundReplacementRequestId);
        Assert.Equal(ticket.SubscriptionDeliveryId, replacement.ReplacesSubscriptionDeliveryId);
        Assert.Equal(approved.ReplacementDeliveryPublicId, replacement.PublicId);
    }

    [Fact]
    public async Task MilkRejected_ReplacementLinkedToOriginalDeliveryAndOrder()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Include(x => x.Delivery).Include(x => x.SubscriptionDelivery).SingleAsync();
        var replacement = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.IsReplacement);

        // Replacement points at the original failed occurrence; the original
        // operational delivery row is preserved as Failed.
        Assert.Equal(ticket.SubscriptionDeliveryId, replacement.ReplacesSubscriptionDeliveryId);
        Assert.Equal(ticket.Id, replacement.RefundReplacementRequestId);
        var original = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == ticket.DeliveryId);
        Assert.Equal(DeliveryStatus.Failed, original.Status);
        Assert.Equal(DeliveryFailureReasons.CustomerRejectedMilkTest, original.FailureReason);
        Assert.NotNull(ticket.MilkTestId);
        Assert.Equal(ticket.Delivery.BranchId, replacement.BranchId);
        Assert.Equal(harness.Customer.Id, ticket.CustomerId);
    }

    [Fact]
    public async Task MilkRejected_ReplacementAppearsInDeliveryOperationsAndCanBeAssigned()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        // Same-day policy: operational row materializes on the original date.
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 16), CancellationToken.None);
        var deliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId).SingleAsync();

        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        var assigned = await harness.AssignAsync(deliveryId, harness.Staff);
        Assert.Equal(DeliveryStatus.Assigned, assigned.Status);
    }

    [Fact]
    public async Task MilkRejected_OriginalDeliveryRemainsFailed()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        var originalDelivery = await harness.Db.Deliveries.AsNoTracking().SingleAsync(x => x.Id == ticket.DeliveryId);
        Assert.Equal(DeliveryStatus.Failed, originalDelivery.Status);
        var originalOccurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.Id == ticket.SubscriptionDeliveryId);
        Assert.Equal(SubscriptionDeliveryStatus.Failed, originalOccurrence.Status);
        Assert.False(originalOccurrence.IsReplacement);
    }

    [Fact]
    public async Task MilkRejected_SameDaySchedulingAndOneHourDeadline()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);

        var proposal = await service.ProposeReplacementDateAsync(StaffActor(harness), requestId, CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 8, 16), proposal.ProposedDate);

        // A staff-edited date off the original day is rejected, never silently moved.
        await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 17), SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task MilkRejected_ApprovalPastOneHourWindowFailsClosed()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);

        // The one-hour same-day window passes before staff acts.
        harness.Clock.Advance(TimeSpan.FromHours(2));
        harness.Db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));
        Assert.Contains("one-hour", exception.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync(x => x.IsReplacement));
    }

    [Fact]
    public async Task MilkRejected_TicketDoesNotCompleteBeforeReplacementDelivered()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CompleteAsync(
            StaffActor(harness), requestId, new DecideRefundReplacementRequest(null), CancellationToken.None));
        Assert.Contains("before its replacement delivery is delivered", exception.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task MilkRejected_ReplacementCompletionCompletesTicket()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 16), CancellationToken.None);
        var deliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId).SingleAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff), deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-delivered").ToListAsync());
    }

    [Fact]
    public async Task MilkRejected_RetryApprovalDoesNotDuplicateReplacement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Single(await harness.Db.SubscriptionDeliveries.AsNoTracking().Where(x => x.IsReplacement).ToListAsync());
        Assert.Equal(RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task MilkRejected_RefundChoiceFollowsRefundWorkflowWithoutReplacement()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var requestId = await SeedMilkRejectedTicketAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "Goodwill", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Null(approved.ReplacementDate);
        Assert.Null(approved.ReplacementDeliveryPublicId);
        Assert.Equal(0, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync(x => x.IsReplacement));

        await service.CompleteAsync(
            StaffActor(harness), requestId, new DecideRefundReplacementRequest(null), CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        // Milk-test rejections settle at 0% with no deduction: full share credited.
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    private static async Task<Guid> SeedMilkRejectedTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        var seedBatchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, seedBatchId, 1m);
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var milkService = CreateMilkService(harness);
        var customerActor = new MilkTestActor(harness.Customer.Id, new HashSet<long>(), false);
        var staffActor = new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);

        var requested = await milkService.RequestAsync(customerActor, deliveryId, CancellationToken.None);
        await milkService.UploadImageAsync(
            staffActor, requested.MilkTestId,
            new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
            "test.jpg", "image/jpeg", CancellationToken.None);
        await milkService.CompleteAsync(
            staffActor, requested.MilkTestId,
            new CompleteMilkTestRequest([new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")], "Test completed"),
            CancellationToken.None);
        await milkService.RejectAsync(
            customerActor, requested.MilkTestId, new DecideMilkTestRequest("Milk was sour"), CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        return await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();
    }

    private static async Task SeedSubscriptionPaymentAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        // Established pattern (SubscriptionTestFailureRefundTests): relax SQLite CHECK enforcement for the
        // payment/refund settlement writes on this connection.
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            harness.Subscription.PayableAmount,
            "INR",
            $"milk-rejected-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static RefundReplacementService CreateRefundService(DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    private static MilkTestService CreateMilkService(DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ProofStorage,
        new PassThroughImageValidator(),
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    private sealed class PassThroughImageValidator : IMilkTestImageValidator
    {
        public long MaximumFileSize => 10L * 1024L * 1024L;

        public Task<ValidatedMilkTestImage> ValidateAsync(
            Stream content, string fileName, string? declaredContentType, CancellationToken cancellationToken)
        {
            var buffered = new MemoryStream();
            content.CopyTo(buffered);
            buffered.Position = 0;
            return Task.FromResult(new ValidatedMilkTestImage(
                Path.GetFileName(fileName), declaredContentType ?? "image/jpeg", buffered.Length, buffered));
        }
    }
}
