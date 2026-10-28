using System.Text.Json;
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
/// Stage 1: one-time order customer-fault refund. Support records an explicit
/// apply/waive deduction decision at approval (frozen snapshot of the shared
/// Admin Centre percentage, zero when waived); completion settles the frozen
/// refund to the wallet inside the ticket transaction. Refund-only: order
/// Replacement requests cannot be raised, approved or completed.
/// </summary>
public sealed class RefundReplacementOneTimeOrderRefundTests
{
    [Fact]
    public async Task Submit_Replacement_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var deliveryId = await SeedDeliveredOrderDeliveryAsync(harness);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.SubmitAsync(
            CustomerActor(harness),
            new SubmitRefundReplacementRequest(
                deliveryId, RefundReplacementType.Replacement, "Wants a fresh delivery.", null),
            CancellationToken.None));

        Assert.Contains("Only refund requests", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approve_WithoutDecision_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));

        Assert.Contains("deduction decision", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Pending,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_Apply_SnapshotsSharedConfiguration()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);
        await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, null, 25m),
            harness.Manager.Id, null, null, CancellationToken.None);

        var approved = await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest("Customer fault confirmed", ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(25m, approved.AppliedDeductionPercentage);
        Assert.False(approved.DeductionWaived);
        Assert.Null(approved.DeductionWaiverReason);
        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(25m, stored.AppliedDeductionPercentage);
        // Approval alone moves no money.
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        var approveAudit = await harness.Db.AuditLogs.AsNoTracking()
            .SingleAsync(x => x.Action == "REFUND_REPLACEMENT.APPROVE");
        Assert.Contains("25", approveAudit.NewValueJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approve_Waive_RequiresReason()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);

        await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "  ", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));
        Assert.Equal(
            RefundReplacementStatus.Pending,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_Waive_SnapshotsZero()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);

        var approved = await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "First failure goodwill", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(0m, approved.AppliedDeductionPercentage);
        Assert.True(approved.DeductionWaived);
        Assert.Equal("First failure goodwill", approved.DeductionWaiverReason);
    }

    [Fact]
    public async Task Approve_LegacyReplacement_IsBlocked()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness, type: RefundReplacementType.Replacement);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));

        Assert.Contains("Only refunds are supported", error.Message, StringComparison.OrdinalIgnoreCase);
        // History preserved: still Pending with its requested type.
        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementStatus.Pending, stored.Status);
        Assert.Equal(RefundReplacementType.Replacement, stored.Type);
    }

    [Fact]
    public async Task Complete_LegacyReplacement_IsBlocked()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(
            harness, type: RefundReplacementType.Replacement, preApprove: true);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("attempt"),
            CancellationToken.None));

        Assert.Contains("Only refunds are supported", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Preview_MatchesRecords_WithoutMutation()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);

        var applied = await service.PreviewOrderRefundAsync(
            StaffActor(harness), ticketId, true, CancellationToken.None);
        var waived = await service.PreviewOrderRefundAsync(
            StaffActor(harness), ticketId, false, CancellationToken.None);

        // Order payable 200, shared default deduction 10%.
        Assert.Equal(200m, applied.GrossBasis);
        Assert.Equal(10m, applied.AppliedPercentage);
        Assert.Equal(20m, applied.DeductionAmount);
        Assert.Equal(200m, applied.EligiblePaid);
        Assert.Equal(180m, applied.ProposedRefund);
        Assert.Equal("INR", applied.Currency);
        Assert.Equal(0m, waived.AppliedPercentage);
        Assert.Equal(0m, waived.DeductionAmount);
        Assert.Equal(200m, waived.ProposedRefund);

        // Read-only: no money, no status change, no audit, no outbox.
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(
            RefundReplacementStatus.Pending,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Complete_Apply_CreditsFrozenRefundExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var paymentId = await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        var completed = await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        // 200 gross, 10% = 20 deduction, 180 wallet credit.
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(180m, walletTxn.Amount);
        Assert.Equal(WalletTransactionType.RefundCredit, walletTxn.Type);
        Assert.Equal($"one-time-refund:{ticketId:N}:wallet", walletTxn.IdempotencyKey);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(paymentId, payment.PublicId);
        Assert.Equal(180m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(180m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Equal($"one-time-refund:{ticketId:N}:payment:{payment.Id}", refund.IdempotencyKey);

        // Retry after completion is rejected; nothing duplicates.
        await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("retry"),
            CancellationToken.None));
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());

        var completeAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.COMPLETE").ToListAsync());
        Assert.Contains("OneTimeOrderRefund", completeAudit.NewValueJson ?? string.Empty);
        var completedEvent = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementCompleted).ToListAsync());
        Assert.Contains("180.00", JsonSerializer.Deserialize<JsonElement>(
            completedEvent.PayloadJson).GetProperty("Variables").GetProperty("message").GetString() ?? string.Empty);
    }

    [Fact]
    public async Task FrozenPercentage_SurvivesLaterConfigChange()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, null, 40m),
            harness.Manager.Id, null, null, CancellationToken.None);

        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("config moved afterwards"),
            CancellationToken.None);

        // Still the 10% snapshot: 180 credited, not 120.
        Assert.Equal(180m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(10m, (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).AppliedDeductionPercentage);
    }

    [Fact]
    public async Task Complete_Waive_CreditsFullAmount()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "Goodwill", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("waived"),
            CancellationToken.None);

        Assert.Equal(200m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(200m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
    }

    [Theory]
    [InlineData(PaymentMethod.Wallet)]
    [InlineData(PaymentMethod.Razorpay)]
    [InlineData(PaymentMethod.Development)]
    public async Task Complete_AnyPaymentMethod_CreditsWalletWithoutGateway(PaymentMethod method)
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, method);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("paid"),
            CancellationToken.None);

        // No gateway is called: the local refund row succeeds and the wallet
        // is credited even for Razorpay/Development source payments.
        Assert.Equal(180m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Null(refund.GatewayRefundId);
    }

    [Fact]
    public async Task Complete_MultiplePayments_AggregatesDeterministically()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var firstId = await SeedOrderPaymentAsync(harness, ticketId, 120m, PaymentMethod.Wallet, "a");
        var secondId = await SeedOrderPaymentAsync(harness, ticketId, 80m, PaymentMethod.Wallet, "b");
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("split across payments"),
            CancellationToken.None);

        // Net 180 allocated by ascending payment id: 120 then 60.
        Assert.Equal(180m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        var refunds = await harness.Db.Refunds.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, refunds.Count);
        var payments = await harness.Db.Payments.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(firstId, payments[0].PublicId);
        Assert.Equal(secondId, payments[1].PublicId);
        Assert.Equal(120m, refunds[0].Amount);
        Assert.Equal(payments[0].Id, refunds[0].PaymentId);
        Assert.Equal(60m, refunds[1].Amount);
        Assert.Equal(payments[1].Id, refunds[1].PaymentId);
        Assert.Equal(120m, payments[0].RefundedAmount);
        Assert.Equal(60m, payments[1].RefundedAmount);
    }

    [Fact]
    public async Task Complete_CappedByOutstandingBalances()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var paymentDbId = await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        await RecordExternalRefundAsync(harness, paymentDbId, 150m, "prior-settlement");
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("capped"),
            CancellationToken.None);

        // Net 180 but only 50 remains: the new credit tops up the remainder.
        Assert.Equal(50m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(200m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(2, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Complete_UnpaidOrder_BlockedStaysApproved()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("no payment"),
            CancellationToken.None));

        Assert.Contains("no eligible paid amount", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Empty(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.COMPLETE").ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementCompleted).ToListAsync());
    }

    [Fact]
    public async Task Complete_Rounding_UsesFrozenPayable()
    {
        await using var harness = await CreateHarnessAsync();
        // 99.99 gross with 10%: deduction 10.00 (AwayFromZero), net 89.99.
        var ticketId = await SeedOneTimeTicketAsync(harness, subtotal: 99.99m, suffix: "R");
        await SeedOrderPaymentAsync(harness, ticketId, 99.99m, PaymentMethod.Wallet, "r");
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("rounding"),
            CancellationToken.None);

        Assert.Equal(89.99m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task ConcurrentCompleteOnSqlServer_CreditsExactlyOnce()
    {
        await using var harness = await SqlOneTimeRefundHarness.CreateAsync();

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
                    CancellationToken.None),
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
        Assert.Equal(180m, (await verifyDb.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
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

    /// <summary>
    /// Seeds a delivered one-time-order delivery without a ticket, for
    /// customer submission tests. Returns the delivery public id.
    /// </summary>
    private static async Task<Guid> SeedDeliveredOrderDeliveryAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        decimal subtotal = 200m,
        string suffix = "1")
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var order = new Order(
            harness.Customer.Id,
            null,
            harness.Branch.Id,
            $"one-time-order-{suffix}-{Guid.NewGuid():N}",
            $"ORD-ONETIME-{suffix}",
            subtotal,
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
            $"REF-ONETIME-{suffix}",
            "Customer",
            "9999999999",
            "Home, 1 Main Road, Central, Bengaluru",
            null,
            12.9716m,
            77.5946m);
        delivery.AssignDeliveryNumber($"DLV-ONETIME-{suffix}");
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync();

        delivery.Assign(harness.Staff.Id, harness.Staff.Id, now, null);
        delivery.PickUp(harness.Staff.Id, now, null);
        delivery.Start(harness.Staff.Id, now);
        delivery.Arrive(harness.Staff.Id, now);
        delivery.RecordOtpVerified(harness.Staff.Id, now);
        delivery.Complete(harness.Staff.Id, now, null);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return delivery.PublicId;
    }

    /// <summary>
    /// Seeds a directly-constructed order-backed one-time ticket (delivered
    /// order delivery → customer-submitted Refund request, Pending), with an
    /// optional legacy Replacement type and direct pre-approval.
    /// </summary>
    private static async Task<Guid> SeedOneTimeTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        RefundReplacementType type = RefundReplacementType.Refund,
        decimal subtotal = 200m,
        string suffix = "1",
        bool preApprove = false)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var order = new Order(
            harness.Customer.Id,
            null,
            harness.Branch.Id,
            $"one-time-order-{suffix}-{Guid.NewGuid():N}",
            $"ORD-ONETIME-{suffix}",
            subtotal,
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
            $"REF-ONETIME-{suffix}",
            "Customer",
            "9999999999",
            "Home, 1 Main Road, Central, Bengaluru",
            null,
            12.9716m,
            77.5946m);
        delivery.AssignDeliveryNumber($"DLV-ONETIME-{suffix}");
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
            type,
            RefundReplacementSource.CustomerPostDelivery,
            "Item was not satisfactory",
            null,
            now,
            now.AddHours(48));
        ticket.AssignRequestNumber($"RR-ONETIME-{suffix}");
        if (preApprove)
        {
            ticket.Approve(harness.Manager.Id, now, "Legacy approval.");
        }
        db.RefundReplacementRequests.Add(ticket);
        await db.SaveChangesAsync();
        // Stage 2 conditional evidence: delivered-product claims carry the
        // customer's proof photo (uploaded while Pending in the real flow)
        // so staff approval can proceed.
        ticket.AddImage(new RefundReplacementImage(
            ticket.Id,
            $"test/one-time/{suffix}/proof.jpg",
            "proof.jpg",
            "image/jpeg",
            5,
            harness.Customer.Id,
            now));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return ticket.PublicId;
    }

    private static async Task<Guid> SeedOrderPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid ticketId,
        decimal amount,
        PaymentMethod method,
        string keySuffix = "1")
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var ticket = await db.RefundReplacementRequests.AsNoTracking().SingleAsync(x => x.PublicId == ticketId);
        var payment = new Payment(
            ticket.OrderId!.Value,
            harness.Customer.Id,
            method,
            amount,
            "INR",
            $"one-time-pay-{keySuffix}-{Guid.NewGuid():N}",
            now.AddMinutes(15));
        payment.Succeed(
            method == PaymentMethod.Wallet ? null : $"gw-seed-{keySuffix}",
            method == PaymentMethod.Wallet ? "WALLET" : "captured",
            now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return payment.PublicId;
    }

    private static async Task RecordExternalRefundAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid paymentPublicId,
        decimal amount,
        string keySuffix)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var payment = await db.Payments
            .Include(x => x.Refunds)
            .SingleAsync(x => x.PublicId == paymentPublicId);
        var refund = payment.StartRefund(
            amount, "External settlement", $"external-{keySuffix}-{Guid.NewGuid():N}", harness.Manager.Id);
        refund.Succeed(now);
        payment.CompleteRefund(amount);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Duplicate-refund guard: a customer must not be able to raise a new
    /// refund request for a one-time order that has already been refunded,
    /// whether the refund settled through a completed ticket or externally.
    /// </summary>
    [Fact]
    public async Task Submit_AfterCompletedFullRefund_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "Goodwill", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("waived"),
            CancellationToken.None);
        var deliveryId = await TicketDeliveryIdAsync(harness, ticketId);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            SubmitOneTimeRefundAsync(service, harness, deliveryId));

        Assert.Contains("already been completed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Submit_AfterCompletedApplyDeduction_IsRejectedDespiteDeductionRemainder()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("apply deduction"),
            CancellationToken.None);
        var deliveryId = await TicketDeliveryIdAsync(harness, ticketId);

        // 180 credited; the 20 customer-fault remainder is not refundable,
        // so a second request must be refused rather than pay it out.
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(180m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            SubmitOneTimeRefundAsync(service, harness, deliveryId));

        Assert.Contains("already been completed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
        Assert.Equal(180m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
    }

    [Fact]
    public async Task Submit_WithPendingTicket_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);
        var deliveryId = await TicketDeliveryIdAsync(harness, ticketId);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            SubmitOneTimeRefundAsync(service, harness, deliveryId));

        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Submit_WithApprovedTicket_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness, preApprove: true);
        var service = CreateRefundService(harness);
        var deliveryId = await TicketDeliveryIdAsync(harness, ticketId);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            SubmitOneTimeRefundAsync(service, harness, deliveryId));

        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Submit_EligibleOrder_SucceedsAsPending()
    {
        await using var harness = await CreateHarnessAsync();
        var deliveryId = await SeedDeliveredOrderDeliveryAsync(harness);
        var service = CreateRefundService(harness);

        var result = await SubmitOneTimeRefundAsync(service, harness, deliveryId);

        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Submit_AfterRejection_AllowsNewRequest()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);
        await service.RejectAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("Not a valid claim."),
            CancellationToken.None);
        var deliveryId = await TicketDeliveryIdAsync(harness, ticketId);

        // Rejection is not a refund: the customer may raise a fresh request.
        var result = await SubmitOneTimeRefundAsync(service, harness, deliveryId);

        Assert.Equal(RefundReplacementStatus.Pending, result.Status);
        Assert.Equal(2, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Submit_AfterPartialExternalRefund_AllowsCappedCompletionWithoutOverRefund()
    {
        await using var harness = await CreateHarnessAsync();
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var deliveryId = await SeedDeliveredOrderDeliveryAsync(harness);
        var delivery = await db.Deliveries.AsNoTracking().SingleAsync(x => x.PublicId == deliveryId);
        var payment = new Payment(
            delivery.OrderId!.Value,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            200m,
            "INR",
            $"one-time-partial-{Guid.NewGuid():N}",
            now.AddMinutes(15));
        payment.Succeed(null, "WALLET", now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        var external = payment.StartRefund(
            150m, "External settlement", $"external-partial-{Guid.NewGuid():N}", harness.Manager.Id);
        external.Succeed(now);
        payment.CompleteRefund(150m);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = CreateRefundService(harness);
        // 50 remains outstanding: submission stays allowed (existing policy).
        var submitted = await SubmitOneTimeRefundAsync(service, harness, deliveryId);
        Assert.Equal(RefundReplacementStatus.Pending, submitted.Status);
        await service.ApproveAsync(
            StaffActor(harness), submitted.RequestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), submitted.RequestId,
            new DecideRefundReplacementRequest("top up remainder"),
            CancellationToken.None);

        // Net 180 with only 50 outstanding: tops up the remainder without
        // ever exceeding the 200 paid for the order.
        Assert.Equal(200m, (await db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(
            200m,
            await db.Refunds.AsNoTracking()
                .Where(x => x.Status == RefundStatus.Succeeded)
                .SumAsync(x => x.Amount));
        Assert.Equal(50m, await db.WalletTransactions.AsNoTracking().SumAsync(x => x.Amount));
    }

    [Fact]
    public async Task Submit_ConcurrentDuplicates_OneWinnerNoDuplicate()
    {
        await using var harness = await CreateHarnessAsync();
        var deliveryId = await SeedDeliveredOrderDeliveryAsync(harness);
        var firstService = CreateRefundService(harness);
        await using var secondDb = CreateSecondContext(harness);
        var secondService = CreateRefundServiceFor(secondDb, harness);

        var attempts = await Task.WhenAll(
            AttemptSubmitAsync(firstService, harness, deliveryId, "First concurrent request."),
            AttemptSubmitAsync(secondService, harness, deliveryId, "Second concurrent request."));

        Assert.Single(attempts, x => x.Result is not null);
        var loser = Assert.Single(attempts, x => x.Result is null);
        Assert.IsType<ConflictException>(loser.Error);
        Assert.Equal(1, await harness.Db.RefundReplacementRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Submit_AnotherCustomersDelivery_IsNotFound()
    {
        await using var harness = await CreateHarnessAsync();
        var deliveryId = await SeedDeliveredOrderDeliveryAsync(harness);
        var service = CreateRefundService(harness);
        var stranger = new RefundReplacementActor(harness.Manager.Id, new HashSet<long>(), false);

        await Assert.ThrowsAsync<NotFoundException>(() => service.SubmitAsync(
            stranger,
            new SubmitRefundReplacementRequest(
                deliveryId, RefundReplacementType.Refund, "Not my order.", null),
            CancellationToken.None,
            ProofImage()));
    }

    [Fact]
    public async Task Eligibility_AfterCompletedRefund_IsIneligible()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("apply deduction"),
            CancellationToken.None);
        var deliveryId = await TicketDeliveryIdAsync(harness, ticketId);

        var eligibility = await service.GetEligibilityAsync(
            CustomerActor(harness), deliveryId, CancellationToken.None);

        Assert.False(eligibility.IsEligible);
        Assert.Contains(
            "already been completed", eligibility.IneligibleReason, StringComparison.OrdinalIgnoreCase);
    }

    private static SubmitRefundReplacementImage ProofImage() =>
        new(new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 0x01], writable: false),
            "proof.jpg", "image/jpeg", 5);

    private static Task<RefundReplacementRequestResult> SubmitOneTimeRefundAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid deliveryId,
        string reason = "Milk was sour.") =>
        service.SubmitAsync(
            CustomerActor(harness),
            new SubmitRefundReplacementRequest(
                deliveryId, RefundReplacementType.Refund, reason, null),
            CancellationToken.None,
            ProofImage());

    private static Task<Guid> TicketDeliveryIdAsync(
        DeliveryServiceTests.DeliveryHarness harness, Guid ticketId) =>
        harness.Db.RefundReplacementRequests.AsNoTracking()
            .Where(x => x.PublicId == ticketId)
            .Select(x => x.Delivery.PublicId)
            .SingleAsync();

    private static DoodhDirectDbContext CreateSecondContext(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        // A second context on the same shared-cache store, so two service
        // instances race like two concurrent API requests.
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(harness.Db.Database.GetConnectionString(), sqlite => sqlite.CommandTimeout(30))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    private static RefundReplacementService CreateRefundServiceFor(
        DoodhDirectDbContext dbContext,
        DeliveryServiceTests.DeliveryHarness harness) => new(
        dbContext,
        harness.TimeProvider,
        new RefundReplacementConfigurationService(dbContext, harness.TimeProvider),
        new NumberSeriesService(dbContext, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(dbContext, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }));

    private static async Task<(RefundReplacementRequestResult? Result, Exception? Error)> AttemptSubmitAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid deliveryId,
        string reason)
    {
        try
        {
            return (await SubmitOneTimeRefundAsync(service, harness, deliveryId, reason), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    /// <summary>
    /// Throwaway SQL Server database so concurrent one-time-order completions
    /// exercise real serializable locking and unique-key contention.
    /// </summary>
    private sealed class SqlOneTimeRefundHarness : IAsyncDisposable
    {
        private const string SqlServerInstance = @".\SQLEXPRESS";
        private readonly string connectionString;

        private SqlOneTimeRefundHarness(
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

        public static async Task<SqlOneTimeRefundHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_OneTimeRefund_{Guid.NewGuid():N}";
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

            var order = new Order(
                customer.Id, null, branch.Id, $"sql-one-time-{Guid.NewGuid():N}", "ORD-SQL-1",
                200m, 0m, branch.Code, branch.Name, "Home", "1 Main Road", null,
                "Central", "Bengaluru", "Karnataka", "560001", null, null,
                "Customer", "9999999999", 12.9716m, 77.5946m);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var delivery = Delivery.ForOrder(
                order.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now),
                "REF-SQL-1", "Customer", "9999999999",
                "Home, 1 Main Road, Central, Bengaluru", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber("DLV-SQL-1");
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();

            delivery.Assign(staff.Id, staff.Id, now, null);
            delivery.PickUp(staff.Id, now, null);
            delivery.Start(staff.Id, now);
            delivery.Arrive(staff.Id, now);
            delivery.RecordOtpVerified(staff.Id, now);
            delivery.Complete(staff.Id, now, null);
            await db.SaveChangesAsync();

            var payment = new Payment(
                order.Id, customer.Id, PaymentMethod.Wallet,
                order.PayableAmount, "INR", $"sql-one-time-pay-{Guid.NewGuid():N}", now.AddMinutes(15));
            payment.Succeed(null, "WALLET", now);
            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            // 200 gross with the default 10% snapshot: 180 wallet credit.
            var ticket = new RefundReplacementRequest(
                order.Id, delivery.Id, customer.Id, branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery,
                "Customer missed the delivery call.", null, now, now.AddHours(48));
            ticket.AssignRequestNumber("RR-SQL-1");
            ticket.RecordDeductionDecision(true, 10m, null);
            ticket.Approve(staff.Id, now, "Approved with deduction.");
            db.RefundReplacementRequests.Add(ticket);
            await db.SaveChangesAsync();

            var harness = new SqlOneTimeRefundHarness(
                builder.ConnectionString, ticket.PublicId, staff.Id, branch.Id);
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
                throw new NotSupportedException("One-time-order completion records no media.");

            public Task<StoredMediaContent> OpenReadAsync(
                string storageKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException("One-time-order completion records no media.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
