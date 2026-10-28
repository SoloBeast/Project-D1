using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class SubscriptionTestFailureRefundTests
{
    [Fact]
    public void EffectiveDeduction_TestFailureSubscription_ReturnsZero_EvenWhenSnapshotNonzero()
    {
        var effective = DeliveryFailRefund.EffectiveDeductionPercentage(
            RefundReplacementSource.MilkTestRejected, subscriptionDeliveryId: 5, appliedDeductionPercentage: 10m, deductionSnapshot: 10m);
        Assert.Equal(0m, effective);
    }

    [Fact]
    public void EffectiveDeduction_TestFailureSubscription_IgnoresStaffAppliedDecision()
    {
        var effective = DeliveryFailRefund.EffectiveDeductionPercentage(
            RefundReplacementSource.MilkTestRejected, subscriptionDeliveryId: 7, appliedDeductionPercentage: 25m, deductionSnapshot: 10m);
        Assert.Equal(0m, effective);
    }

    [Fact]
    public void EffectiveDeduction_OrdinaryDeliveryFailure_KeepsConfiguredDeduction()
    {
        Assert.Equal(10m, DeliveryFailRefund.EffectiveDeductionPercentage(
            RefundReplacementSource.DeliveryFailed, 5, appliedDeductionPercentage: null, deductionSnapshot: 10m));
        Assert.Equal(25m, DeliveryFailRefund.EffectiveDeductionPercentage(
            RefundReplacementSource.DeliveryFailed, 5, appliedDeductionPercentage: 25m, deductionSnapshot: 10m));
    }

    [Fact]
    public void EffectiveDeduction_OneTimeMilkTestRejected_KeepsRecordedDecision()
    {
        // One-time order tickets have no subscription delivery; the narrow
        // subscription test-failure rule must not apply to them.
        Assert.Equal(10m, DeliveryFailRefund.EffectiveDeductionPercentage(
            RefundReplacementSource.MilkTestRejected, subscriptionDeliveryId: null, appliedDeductionPercentage: 10m, deductionSnapshot: 10m));
    }

    [Fact]
    public void Calculate_FullRefund_IncludesFrozenCharges_InPerDeliveryShare()
    {
        // Frozen payable 200 (160 product + 40 charges) over 2 entitlements:
        // per-delivery gross 100, zero deduction => full 100.
        var amounts = DeliveryFailRefund.Calculate(200m, 2, 0m, 200m, 0m);
        Assert.Equal(100m, amounts.Gross);
        Assert.Equal(0m, amounts.Deduction);
        Assert.Equal(100m, amounts.Final);
    }

    [Fact]
    public async Task TestFailureRefund_ReturnsFullPerDeliveryShare_WhenNormalDeductionNonzero()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var ticketId = await FailViaMilkTestAsync(harness);
        await SeedSubscriptionPaymentAsync(harness, harness.Subscription.PayableAmount);
        var service = CreateRefundService(harness);

        await service.ChooseAsync(CustomerActor(harness), ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        // Staff records "apply deduction" — the test-failure rule must still settle at 0%.
        await service.ApproveAsync(StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Refund verified and paid"), CancellationToken.None);

        // Payable 160 / 2 = 80 gross, 0 deduction => full 80 (not 72).
        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(80m, walletTxn.Amount);
        Assert.Equal(80m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
    }

    [Fact]
    public async Task TestFailureRefund_LeavesOtherDeliveriesAndEntitlementUnchanged()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var ticketId = await FailViaMilkTestAsync(harness);
        await SeedSubscriptionPaymentAsync(harness, harness.Subscription.PayableAmount);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(CustomerActor(harness), ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Refund verified and paid"), CancellationToken.None);

        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(harness.Subscription.PayableAmount, subscription.PayableAmount);
        Assert.Equal(harness.Subscription.ChargesTotal, subscription.ChargesTotal);
        Assert.Equal(harness.Subscription.TotalEntitlement, subscription.TotalEntitlement);
        // Only the failed delivery refunded; no whole-subscription credit.
        Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task TestFailureRefund_RespectsRemainingRefundable_WhenPaymentPartlyRefunded()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var ticketId = await FailViaMilkTestAsync(harness);
        // Payment covers only one share: gross 80 capped to 50.
        await SeedSubscriptionPaymentAsync(harness, 50m);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(CustomerActor(harness), ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Refund verified and paid"), CancellationToken.None);

        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(50m, walletTxn.Amount);
    }

    [Fact]
    public async Task TestFailureRefund_RepeatedCompletion_DoesNotIssueSecondRefund()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var ticketId = await FailViaMilkTestAsync(harness);
        await SeedSubscriptionPaymentAsync(harness, harness.Subscription.PayableAmount);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(CustomerActor(harness), ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Refund verified and paid"), CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Retry"), CancellationToken.None));
        Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OrdinaryDeliveryFailureRefund_StillAppliesConfiguredDeduction()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);
        var ticketId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        await SeedSubscriptionPaymentAsync(harness, harness.Subscription.PayableAmount);
        var service = CreateRefundService(harness);

        await service.ChooseAsync(CustomerActor(harness), ticketId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Refund verified and paid"), CancellationToken.None);

        // Ordinary failure keeps 10% deduction: 80 - 8 = 72.
        Assert.Equal(72m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
    }

    private static async Task<Guid> FailViaMilkTestAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var milkService = new MilkTestService(
            harness.Db,
            harness.TimeProvider,
            harness.ProofStorage,
            new PassThroughImageValidator(),
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
        var customerActor = new MilkTestActor(harness.Customer.Id, new HashSet<long>(), false);
        var staffActor = new MilkTestActor(harness.Staff.Id, new HashSet<long> { harness.Branch.Id }, false);
        var requested = await milkService.RequestAsync(customerActor, deliveryId, CancellationToken.None);
        await milkService.UploadImageAsync(
            staffActor, requested.MilkTestId, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
            "test.jpg", "image/jpeg", CancellationToken.None);
        await milkService.CompleteAsync(
            staffActor, requested.MilkTestId,
            new CompleteMilkTestRequest([new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")], null),
            CancellationToken.None);
        await milkService.RejectAsync(
            customerActor, requested.MilkTestId, new DecideMilkTestRequest("Milk was sour"), CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementSource.MilkTestRejected, ticket.Source);
        return ticket.PublicId;
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

    private static async Task SeedSubscriptionPaymentAsync(DeliveryServiceTests.DeliveryHarness harness, decimal amount)
    {
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            amount,
            "INR",
            $"test-failure-pay-{Guid.NewGuid():N}",
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
