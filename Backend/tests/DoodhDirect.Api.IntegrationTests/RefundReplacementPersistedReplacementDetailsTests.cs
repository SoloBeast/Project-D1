using DoodhDirect.Application.Common;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 6.1: persisted replacement details on ticket reads.
///
/// The approval response carries the scheduled replacement date, but later
/// detail/list GETs used to drop it. Reads now resolve the replacement from
/// the persisted <c>SubscriptionDelivery.RefundReplacementRequestId</c> link —
/// never from client input and never inferred from the original failed
/// occurrence. The operational delivery's public DELIVERY-series number is
/// exposed only once the replacement has actually materialized; nothing is
/// invented beforehand. Refund, awaiting-choice, and other non-replacement
/// shapes keep null replacement fields.
/// </summary>
public sealed class RefundReplacementPersistedReplacementDetailsTests
{
    private static readonly DateOnly ReplacementDay = new(2026, 8, 20);

    [Fact]
    public async Task Approve_ReDelivery_ReturnsSelectedReplacementDateWithoutDeliveryNumber()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var approved = await service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest("Re-drive on Thursday", ReplacementDay),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(ReplacementDay, approved.ReplacementDate);
        Assert.NotNull(approved.ReplacementDeliveryPublicId);
        // Approved but not yet materialized: no operational delivery exists,
        // so no delivery number may be exposed.
        Assert.Null(approved.ReplacementDeliveryNumber);
    }

    [Fact]
    public async Task StaffDetail_AfterApproval_ReturnsSamePersistedReplacementDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        var approved = await service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, ReplacementDay),
            CancellationToken.None);
        harness.Db.ChangeTracker.Clear();

        var detail = await service.GetForStaffAsync(
            StaffActor(harness), requestId, CancellationToken.None);

        Assert.Equal(approved.ReplacementDate, detail.ReplacementDate);
        Assert.Equal(ReplacementDay, detail.ReplacementDate);
        Assert.Equal(approved.ReplacementDeliveryPublicId, detail.ReplacementDeliveryPublicId);

        // The date comes from the persisted occurrence row, not the approval
        // response echo: it matches the stored replacement's scheduled date
        // and public id exactly.
        var occurrence = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement)
            .SingleAsync();
        Assert.Equal(occurrence.ScheduledDate, detail.ReplacementDate);
        Assert.Equal(occurrence.PublicId, detail.ReplacementDeliveryPublicId);

        // Still unmaterialized: no fabricated delivery reference.
        Assert.Null(detail.ReplacementDeliveryNumber);
        Assert.Equal(
            0,
            await harness.Db.Deliveries.AsNoTracking()
                .CountAsync(x => x.SubscriptionDeliveryId == occurrence.Id));
    }

    [Fact]
    public async Task StaffList_AfterApproval_ReturnsPersistedReplacementDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, ReplacementDay),
            CancellationToken.None);
        harness.Db.ChangeTracker.Clear();

        var page = await service.ListForStaffAsync(
            StaffActor(harness),
            new RefundReplacementListQuery(null, null, 1, 20),
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(requestId, item.RequestId);
        Assert.Equal(ReplacementDay, item.ReplacementDate);
    }

    [Fact]
    public async Task CustomerReads_AfterApproval_ReturnSamePersistedReplacementDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, ReplacementDay),
            CancellationToken.None);
        harness.Db.ChangeTracker.Clear();

        var detail = await service.GetForCustomerAsync(
            CustomerActor(harness), requestId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(ReplacementDay, detail.ReplacementDate);
        Assert.NotNull(detail.ReplacementDeliveryPublicId);
        Assert.Null(detail.ReplacementDeliveryNumber);

        var list = await service.ListForCustomerAsync(
            CustomerActor(harness), CancellationToken.None);
        Assert.Equal(ReplacementDay, Assert.Single(list).ReplacementDate);
    }

    [Fact]
    public async Task MaterializedReplacement_ExposesLegitimateDeliveryNumber()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, ReplacementDay),
            CancellationToken.None);

        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, ReplacementDay, CancellationToken.None);
        var operational = await harness.Db.Deliveries.AsNoTracking()
            .Include(x => x.SubscriptionDelivery)
            .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
            .SingleAsync();
        Assert.NotNull(operational.DeliveryNumber);
        harness.Db.ChangeTracker.Clear();

        var detail = await service.GetForStaffAsync(
            StaffActor(harness), requestId, CancellationToken.None);

        // The exposed number is exactly the materialized operational
        // delivery's public business reference — and the occurrence link
        // still points at the replacement occurrence, not the delivery.
        Assert.Equal(operational.DeliveryNumber, detail.ReplacementDeliveryNumber);
        Assert.Equal(operational.SubscriptionDelivery!.PublicId, detail.ReplacementDeliveryPublicId);
        Assert.Equal(ReplacementDay, detail.ReplacementDate);
    }

    [Fact]
    public async Task RefundAndAwaitingChoiceTickets_CarryNoReplacementDetails()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        // The completed refund below executes the shared wallet refund, so
        // the same SQLite decimals-as-text workaround as the other refund
        // suites applies (SQL Server enforces the CHECKs numerically).
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateService(harness);

        // Awaiting choice with no recorded choice: no replacement exists.
        var awaiting = await service.GetForStaffAsync(
            StaffActor(harness), requestId, CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, awaiting.Status);
        Assert.Null(awaiting.ReplacementDate);
        Assert.Null(awaiting.ReplacementDeliveryPublicId);
        Assert.Null(awaiting.ReplacementDeliveryNumber);

        // Ordinary refund path: approve then complete executes the shared
        // wallet refund. No replacement is ever created, so every
        // replacement read stays null.
        await SeedSubscriptionPaymentAsync(harness);
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund),
            CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        var completed = await service.CompleteAsync(
            StaffActor(harness), requestId, new DecideRefundReplacementRequest("Refund verified"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Null(completed.ReplacementDate);
        Assert.Null(completed.ReplacementDeliveryPublicId);
        Assert.Null(completed.ReplacementDeliveryNumber);
        harness.Db.ChangeTracker.Clear();

        var detail = await service.GetForStaffAsync(
            StaffActor(harness), requestId, CancellationToken.None);
        Assert.Null(detail.ReplacementDate);
        Assert.Null(detail.ReplacementDeliveryPublicId);
        Assert.Null(detail.ReplacementDeliveryNumber);

        var page = await service.ListForStaffAsync(
            StaffActor(harness),
            new RefundReplacementListQuery(null, null, 1, 20),
            CancellationToken.None);
        Assert.Null(Assert.Single(page.Items).ReplacementDate);
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        return deliveryId;
    }

    private static async Task FailAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);

    private static async Task<Guid> TicketPublicIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static async Task ChooseReDeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId) =>
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);

    private static async Task SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            harness.Subscription.PayableAmount,
            "INR",
            $"persisted-details-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static RefundReplacementService CreateService(DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));
}
