using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.MilkTesting;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 5.1 (B) — milk-test safeguards on cancelled operational deliveries.
///
/// Cancelling a subscription with a pending re-delivery voids the pre-pickup
/// operational delivery but leaves any attached doorstep test in place. That
/// test is preserved evidence: no decision (confirm/reject/complete/cancel),
/// image write, or re-processing may move the cancelled delivery back into an
/// active delivery/failure flow or mint another failure ticket/refund.
/// Read-only access to the evidence stays available.
/// </summary>
public sealed class CancelledDeliveryMilkTestSafeguardTests
{
    [Fact]
    public async Task PendingTest_Confirm_IsRejectedWithNoSideEffects()
    {
        await using var harness = await CancelWithPendingTestAsync(withImage: false);
        var testId = await TestPublicIdAsync(harness);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).ConfirmAsync(
            CustomerMilkActor(harness), testId, new DecideMilkTestRequest("Looks fine"), CancellationToken.None));
        Assert.Contains("cancelled", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        var test = await harness.Db.MilkTests.AsNoTracking().SingleAsync(x => x.PublicId == testId);
        Assert.Equal(MilkTestStatus.Requested, test.Status);
        Assert.Equal(MilkTestCustomerDecision.Pending, test.CustomerDecision);
        Assert.Equal(DeliveryStatus.Cancelled, (await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == harness.CancelledDeliveryId)).Status);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "MILK_TEST.CONFIRM"));
    }

    [Fact]
    public async Task PendingTest_Reject_IsRejectedWithoutFailureTicketOrRefund()
    {
        await using var harness = await CancelWithPendingTestAsync(withImage: false);
        var testId = await TestPublicIdAsync(harness);

        // Must be a clean business-rule rejection, not a ConflictException
        // from a rolled-back in-memory delivery failure.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).RejectAsync(
            CustomerMilkActor(harness), testId, new DecideMilkTestRequest("Milk was sour"), CancellationToken.None));
        Assert.Contains("cancelled", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(MilkTestCustomerDecision.Pending,
            (await harness.Db.MilkTests.AsNoTracking().SingleAsync(x => x.PublicId == testId)).CustomerDecision);
        Assert.Equal(DeliveryStatus.Cancelled, (await harness.Db.Deliveries.AsNoTracking()
            .SingleAsync(x => x.PublicId == harness.CancelledDeliveryId)).Status);
        // No additional failure ticket, no additional money movement.
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
        Assert.Equal(72m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Empty(await harness.Db.WalletTransactions.AsNoTracking()
            .Where(x => x.IdempotencyKey.StartsWith("delivery-fail:")).ToListAsync());
        // Scoped to the voided delivery and its test: the first failure's own
        // DELIVERY.FAIL audit (setup) must not pollute this assertion.
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "MILK_TEST.REJECT" && x.EntityId == testId.ToString()));
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "DELIVERY.FAIL"
                && x.EntityId == harness.CancelledDeliveryId.ToString()));
    }

    [Fact]
    public async Task PendingTest_Complete_IsRejected()
    {
        await using var harness = await CancelWithPendingTestAsync(withImage: false);
        var testId = await TestPublicIdAsync(harness);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).CompleteAsync(
            StaffMilkActor(harness), testId,
            new CompleteMilkTestRequest([new MilkTestParameterRequest("FAT", "Fat", 4.2m, "%")], null),
            CancellationToken.None));
        Assert.Contains("cancelled", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(MilkTestStatus.Requested,
            (await harness.Db.MilkTests.AsNoTracking().SingleAsync(x => x.PublicId == testId)).Status);
    }

    [Fact]
    public async Task PendingTest_ImageWrites_AreRejectedAndEvidencePreserved()
    {
        await using var harness = await CancelWithPendingTestAsync(withImage: true);
        var testId = await TestPublicIdAsync(harness);
        var imageId = await harness.Db.MilkTestImages.AsNoTracking()
            .Where(x => x.MilkTest.PublicId == testId).Select(x => x.PublicId).SingleAsync();

        var uploadError = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).UploadImageAsync(
            StaffMilkActor(harness), testId, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
            "late.jpg", "image/jpeg", CancellationToken.None));
        Assert.Contains("terminal", uploadError.Message, StringComparison.OrdinalIgnoreCase);

        var deleteError = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).DeleteImageAsync(
            StaffMilkActor(harness), testId, imageId, CancellationToken.None));
        Assert.Contains("terminal", deleteError.Message, StringComparison.OrdinalIgnoreCase);

        var replaceError = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).ReplaceImageAsync(
            StaffMilkActor(harness), testId, imageId,
            new MemoryStream([0xFF, 0xD8, 0xFF], writable: false), "swap.jpg", "image/jpeg", CancellationToken.None));
        Assert.Contains("terminal", replaceError.Message, StringComparison.OrdinalIgnoreCase);

        // The pre-cancel evidence image survives untouched.
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.MilkTestImages.AsNoTracking()
            .CountAsync(x => x.MilkTest.PublicId == testId));
    }

    [Fact]
    public async Task PendingTest_CustomerCancel_IsRejected()
    {
        await using var harness = await CancelWithPendingTestAsync(withImage: false);
        var testId = await TestPublicIdAsync(harness);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).CancelAsync(
            CustomerMilkActor(harness), testId, new DecideMilkTestRequest("Changed my mind"), CancellationToken.None));
        Assert.Contains("active delivery", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(MilkTestCustomerDecision.Pending,
            (await harness.Db.MilkTests.AsNoTracking().SingleAsync(x => x.PublicId == testId)).CustomerDecision);
    }

    [Fact]
    public async Task DecidedTest_EvidenceStaysReadableButImmutable()
    {
        await using var harness = await CancelHarness.CreateAsync(withImage: false, withTest: false);
        var deliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.PublicId == harness.CancelledDeliveryId).Select(x => x.Id).SingleAsync();

        // A staff-completed, customer-confirmed test attached to the voided
        // delivery (the decided state the service flow cannot produce after
        // the void, since completion requires arrival).
        var now = harness.Inner.TimeProvider.Now;
        var decided = new MilkTest(
            deliveryId, harness.Inner.Customer.Id, harness.Inner.Branch.Id, harness.Inner.Customer.Id, now);
        harness.Db.MilkTests.Add(decided);
        await harness.Db.SaveChangesAsync();
        decided.AddImage(new MilkTestImage(
            decided.Id, "seed/evidence.jpg", "evidence.jpg", "image/jpeg", 100,
            harness.Inner.Staff.Id, now));
        decided.AddParameter("FAT", "Fat", 4.2m, "%");
        decided.Complete(harness.Inner.Staff.Id, now, null);
        decided.Confirm(now, "Looks fine");
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        var decidedId = decided.PublicId;

        // Evidence stays readable for customer and staff.
        Assert.Equal(MilkTestCustomerDecision.Confirmed,
            (await MilkService(harness).GetForCustomerAsync(
                CustomerMilkActor(harness), harness.CancelledDeliveryId, CancellationToken.None))!
            .CustomerDecision);
        Assert.NotNull(await MilkService(harness).GetForStaffAsync(
            StaffMilkActor(harness), harness.CancelledDeliveryId, CancellationToken.None));

        // But no further decision or image write is accepted.
        await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).ConfirmAsync(
            CustomerMilkActor(harness), decidedId, new DecideMilkTestRequest("Again"), CancellationToken.None));
        await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).RejectAsync(
            CustomerMilkActor(harness), decidedId, new DecideMilkTestRequest("Changed mind"), CancellationToken.None));
        // A completed test rejects images via the lifecycle guard first —
        // identical to completed tests on Delivered/Failed deliveries.
        await Assert.ThrowsAsync<ConflictException>(() => MilkService(harness).UploadImageAsync(
            StaffMilkActor(harness), decidedId, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
            "late.jpg", "image/jpeg", CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        var preserved = await harness.Db.MilkTests.AsNoTracking().SingleAsync(x => x.PublicId == decidedId);
        Assert.Equal(MilkTestStatus.Completed, preserved.Status);
        Assert.Equal(MilkTestCustomerDecision.Confirmed, preserved.CustomerDecision);
    }

    [Fact]
    public async Task CompletedUndecidedTest_CustomerReviewReplace_IsRejected()
    {
        await using var harness = await CancelHarness.CreateAsync(withImage: false, withTest: false);
        var deliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.PublicId == harness.CancelledDeliveryId).Select(x => x.Id).SingleAsync();

        // A staff-completed but customer-undecided test attached to the voided
        // delivery: the review window is open, so without a delivery guard the
        // customer replace path would mutate preserved evidence.
        var now = harness.Inner.TimeProvider.Now;
        var review = new MilkTest(
            deliveryId, harness.Inner.Customer.Id, harness.Inner.Branch.Id, harness.Inner.Customer.Id, now);
        harness.Db.MilkTests.Add(review);
        await harness.Db.SaveChangesAsync();
        review.AddImage(new MilkTestImage(
            review.Id, "seed/evidence.jpg", "evidence.jpg", "image/jpeg", 100,
            harness.Inner.Staff.Id, now));
        review.AddParameter("FAT", "Fat", 4.2m, "%");
        review.Complete(harness.Inner.Staff.Id, now, null);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        var reviewId = review.PublicId;
        var imageId = await harness.Db.MilkTestImages.AsNoTracking()
            .Where(x => x.MilkTest.PublicId == reviewId).Select(x => x.PublicId).SingleAsync();

        // D1-D slice 5.2: narrowly scoped to Cancelled — Delivered/Failed
        // review behavior is unchanged, but the voided delivery's evidence
        // cannot be swapped during review.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => MilkService(harness).ReplaceImageAsync(
            CustomerMilkActor(harness), reviewId, imageId,
            new MemoryStream([0xFF, 0xD8, 0xFF], writable: false), "review.jpg", "image/jpeg",
            CancellationToken.None));
        Assert.Contains("cancelled", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        var preserved = await harness.Db.MilkTests.AsNoTracking().SingleAsync(x => x.PublicId == reviewId);
        Assert.Equal(MilkTestStatus.Completed, preserved.Status);
        Assert.Equal(MilkTestCustomerDecision.Pending, preserved.CustomerDecision);
        Assert.Equal(1, await harness.Db.MilkTestImages.AsNoTracking()
            .CountAsync(x => x.MilkTest.PublicId == reviewId));
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "MILK_TEST.IMAGE_REPLACE" && x.EntityId == reviewId.ToString()));
    }

    /// <summary>
    /// Drives fail → approve ReDelivery → materialize + assign replacement →
    /// request doorstep test (optionally with one staff image) → subscription
    /// cancel. Returns the harness with the operational delivery Cancelled and
    /// its test still attached as pending evidence.
    /// </summary>
    private sealed class CancelHarness : IAsyncDisposable
    {
        private CancelHarness(DeliveryServiceTests.DeliveryHarness inner, Guid cancelledDeliveryId)
        {
            Inner = inner;
            CancelledDeliveryId = cancelledDeliveryId;
        }

        public DeliveryServiceTests.DeliveryHarness Inner { get; }

        public Guid CancelledDeliveryId { get; }

        public DoodhDirectDbContext Db => Inner.Db;

        public async ValueTask DisposeAsync() => await Inner.DisposeAsync();

        public static async Task<CancelHarness> CreateAsync(bool withImage, bool withTest = true)
        {
            var inner = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
            await inner.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");

            await inner.FailSubscriptionAsync(
                await FailTicketAsync(inner), inner.Staff);
            var requestId = await inner.Db.RefundReplacementRequests.AsNoTracking()
                .Select(x => x.PublicId).SingleAsync();
            var choiceService = new RefundReplacementService(
                inner.Db,
                inner.TimeProvider,
                inner.ConfigurationService,
                new NumberSeriesService(inner.Db, inner.TimeProvider),
                inner.ProofStorage,
                new TestNotificationEventWriter(inner.Db, inner.TimeProvider));
            await choiceService.ChooseAsync(
                new RefundReplacementActor(inner.Customer.Id, new HashSet<long>(), false),
                requestId,
                new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
                CancellationToken.None);
            await choiceService.ApproveAsync(
                new RefundReplacementActor(inner.Manager.Id, new HashSet<long> { inner.Branch.Id }, true),
                requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
            inner.Db.ChangeTracker.Clear();

            var payment = Payment.CreateForSubscription(
                inner.Subscription.Id, inner.Customer.Id, PaymentMethod.Wallet,
                inner.Subscription.PayableAmount, "INR",
                $"cancelled-test-pay-{Guid.NewGuid():N}", inner.TimeProvider.Now.AddMinutes(15));
            payment.Succeed(null, "WALLET", inner.TimeProvider.Now);
            inner.Db.Payments.Add(payment);
            await inner.Db.SaveChangesAsync();
            inner.Db.ChangeTracker.Clear();

            await inner.Service.MaterializeEligibleAsync(
                inner.ManagerActor, new DateOnly(2026, 8, 18), CancellationToken.None);
            var deliveryId = await inner.Db.Deliveries.AsNoTracking()
                .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
                .Select(x => x.PublicId)
                .SingleAsync();
            await inner.AssignAsync(deliveryId, inner.Staff);

            var milkService = new MilkTestService(
                inner.Db,
                inner.TimeProvider,
                inner.ProofStorage,
                new PassThroughImageValidator(),
                new TestNotificationEventWriter(inner.Db, inner.TimeProvider));
            Guid? requestedTestId = null;
            if (withTest)
            {
                var requested = await milkService.RequestAsync(
                    new MilkTestActor(inner.Customer.Id, new HashSet<long>(), false),
                    deliveryId, CancellationToken.None);
                requestedTestId = requested.MilkTestId;
            }
            if (withImage && requestedTestId is not null)
            {
                await milkService.UploadImageAsync(
                    new MilkTestActor(inner.Staff.Id, new HashSet<long> { inner.Branch.Id }, false),
                    requestedTestId.Value, new MemoryStream([0xFF, 0xD8, 0xFF], writable: false),
                    "test.jpg", "image/jpeg", CancellationToken.None);
            }
            inner.Db.ChangeTracker.Clear();

            var cancelService = new SubscriptionService(
                inner.Db,
                new FixedBranchAllocationService(inner.Branch),
                new NotSupportedPaymentService(),
                inner.TimeProvider,
                new TestNotificationEventWriter(inner.Db, inner.TimeProvider));
            await cancelService.CancelAsync(
                inner.Customer.Id, inner.Subscription.PublicId, CancellationToken.None);
            inner.Db.ChangeTracker.Clear();

            Assert.Equal(DeliveryStatus.Cancelled, (await inner.Db.Deliveries.AsNoTracking()
                .SingleAsync(x => x.PublicId == deliveryId)).Status);
            return new CancelHarness(inner, deliveryId);
        }

        private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness inner)
        {
            var deliveryId = await inner.MaterializeSubscriptionAsync();
            await inner.AssignAsync(deliveryId, inner.Staff);
            return deliveryId;
        }
    }

    private static Task<CancelHarness> CancelWithPendingTestAsync(bool withImage) =>
        CancelHarness.CreateAsync(withImage);

    private static async Task<Guid> TestPublicIdAsync(CancelHarness harness) =>
        await harness.Db.MilkTests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static MilkTestService MilkService(CancelHarness harness) => new(
        harness.Db,
        harness.Inner.TimeProvider,
        harness.Inner.ProofStorage,
        new PassThroughImageValidator(),
        new TestNotificationEventWriter(harness.Db, harness.Inner.TimeProvider));

    private static MilkTestActor CustomerMilkActor(CancelHarness harness) =>
        new(harness.Inner.Customer.Id, new HashSet<long>(), false);

    private static MilkTestActor StaffMilkActor(CancelHarness harness) =>
        new(harness.Inner.Staff.Id, new HashSet<long> { harness.Inner.Branch.Id }, false);

    private sealed class FixedBranchAllocationService(DoodhDirect.Domain.Catalogue.Branch branch)
        : DoodhDirect.Application.Orders.IBranchAllocationService
    {
        public Task<DoodhDirect.Application.Orders.BranchAllocationResult> AllocateAsync(
            decimal latitude,
            decimal longitude,
            IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DoodhDirect.Application.Orders.BranchAllocationResult(
                branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
    }

    private sealed class NotSupportedPaymentService : DoodhDirect.Application.Payments.IPaymentService
    {
        public Task<DoodhDirect.Application.Payments.PaymentResult> CreateAsync(long c, DoodhDirect.Application.Payments.CreatePaymentRequest r, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never create payments.");
        public Task<DoodhDirect.Application.Payments.PaymentResult> CreateForSubscriptionAsync(long c, long s, PaymentMethod m, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never retry payments.");
        public Task<DoodhDirect.Application.Payments.PaymentResult> RetrySubscriptionAsync(long c, Guid s, PaymentMethod m, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never retry payments.");
        public Task<DoodhDirect.Application.Payments.PaymentResult> CreateWalletTopUpAsync(long c, decimal a, string k, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never top up.");
        public Task<DoodhDirect.Application.Payments.PaymentResult> VerifyAsync(long c, DoodhDirect.Application.Payments.VerifyPaymentRequest r, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never verify payments.");
        public Task<DoodhDirect.Application.Payments.PaymentResult> CompleteDevelopmentAsync(long c, Guid p, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never complete payments.");
        public Task<DoodhDirect.Application.Payments.PaymentResult> CancelAsync(long c, Guid p, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never cancel payments.");
        public Task<IReadOnlyList<DoodhDirect.Application.Payments.PaymentCapability>> GetCapabilitiesAsync(CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never query capabilities.");
        public Task<DoodhDirect.Application.Payments.PaymentResult> GetAsync(long u, Guid p, bool b, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never read payments.");
        public Task<DoodhDirect.Application.Payments.RefundResult> RefundAsync(long r, Guid p, DoodhDirect.Application.Payments.RefundPaymentRequest q, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never gateway-refund.");
        public Task<DoodhDirect.Application.Payments.PaymentReconciliationResult> ReconcileAsync(long r, Guid p, bool b, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never reconcile.");
        public Task ProcessWebhookAsync(byte[] p, string s, CancellationToken t) =>
            throw new NotSupportedException("Cancellation tests never process webhooks.");
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
