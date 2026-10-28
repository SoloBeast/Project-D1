using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 2: explicit manual-bank refund settlement. Support designates the
/// settlement method once at approval; ManualBankTransfer freezes a
/// beneficiary snapshot plus payment-level reservations (Pending refund
/// rows) instead of moving money, and later settles exactly once on UTR
/// confirmation or releases explicitly before any transfer reaches the bank.
/// </summary>
public sealed class RefundReplacementManualBankSettlementTests
{
    private const string HolderName = "Asha Customer";
    private const string BankName = "Example National Bank";
    private const string Ifsc = "EXMP0001234";
    private const string FullAccountNumber = "4111111111111111";
    private const string AccountLast4 = "1111";

    [Fact]
    public async Task ApproveRefund_WithoutSettlementMethod_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "Goodwill"),
            CancellationToken.None));

        Assert.Contains("settlement method", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Pending,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ApproveRefund_WithInvalidSettlementMethod_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: (RefundSettlementMethod)99),
            CancellationToken.None));

        Assert.Contains("settlement method", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Pending,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ApproveReplacement_WithSettlementMethod_IgnoresMethod()
    {
        // Non-refund shapes retain their behavior: one-time replacements
        // cannot be approved regardless of the settlement method supplied.
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness, type: RefundReplacementType.Replacement);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None));

        Assert.Contains("Only refunds are supported", error.Message, StringComparison.OrdinalIgnoreCase);
        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementStatus.Pending, stored.Status);
        Assert.Equal(RefundSettlementMethod.Unspecified, stored.SettlementMethod);
    }

    [Fact]
    public async Task ApproveManualBank_WithoutBankDetails_IsRejectedWithoutSideEffects()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None));

        Assert.Contains("Profile", error.Message, StringComparison.OrdinalIgnoreCase);
        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementStatus.Pending, stored.Status);
        Assert.Equal(RefundSettlementMethod.Unspecified, stored.SettlementMethod);
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(PaymentStatus.Success, await harness.Db.Payments.AsNoTracking().Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task ApproveManualBank_CapturesImmutableSnapshotAndReserves()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        await SeedBankDetailAsync(harness);
        var service = CreateRefundService(harness);

        var approved = await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(RefundSettlementMethod.ManualBankTransfer, approved.SettlementMethod);
        Assert.Equal(200m, approved.ReservedAmount);
        Assert.Equal("INR", approved.ReservedCurrency);

        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundSettlementMethod.ManualBankTransfer, stored.SettlementMethod);
        Assert.Equal(200m, stored.ManualBankReservedAmount);
        Assert.Equal("INR", stored.ManualBankReservedCurrency);
        Assert.Equal(HolderName, stored.ManualBankHolderNameSnapshot);
        Assert.Equal(BankName, stored.ManualBankNameSnapshot);
        Assert.Equal(Ifsc, stored.ManualBankIfscSnapshot);
        Assert.Equal(AccountLast4, stored.ManualBankAccountLast4Snapshot);
        Assert.False(string.IsNullOrWhiteSpace(stored.ManualBankProtectedAccountNumber));
        Assert.DoesNotContain(FullAccountNumber, stored.ManualBankProtectedAccountNumber);

        // Exactly one Pending reservation; the payment is held, not paid.
        var reservation = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(RefundStatus.Pending, reservation.Status);
        Assert.Equal(200m, reservation.Amount);
        Assert.StartsWith("manual-bank:", reservation.IdempotencyKey, StringComparison.Ordinal);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(0m, payment.RefundedAmount);

        // No money moved: no wallet credit, no succeeded refund.
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync(x => x.Status == RefundStatus.Succeeded));

        // APIs never expose the full account number.
        var payload = JsonSerializer.Serialize(approved);
        Assert.DoesNotContain(FullAccountNumber, payload, StringComparison.Ordinal);
        var staffDetail = await service.GetForStaffAsync(StaffActor(harness), ticketId, CancellationToken.None);
        Assert.DoesNotContain(
            FullAccountNumber, JsonSerializer.Serialize(staffDetail), StringComparison.Ordinal);
        Assert.Equal(AccountLast4, staffDetail.BeneficiaryAccountLast4);
    }

    [Fact]
    public async Task ApproveManualBank_ReservesAcrossMultiplePaymentsDeterministically()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 150m, PaymentMethod.Wallet, "m1");
        await SeedOrderPaymentAsync(harness, ticketId, 150m, PaymentMethod.Wallet, "m2");
        await SeedBankDetailAsync(harness);
        var service = CreateRefundService(harness);

        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);

        // Net 200 split across the two payments by ascending payment id.
        var reservations = await harness.Db.Refunds.AsNoTracking()
            .OrderBy(x => x.PaymentId).ToListAsync();
        Assert.Equal(2, reservations.Count);
        Assert.Equal(150m, reservations[0].Amount);
        Assert.Equal(50m, reservations[1].Amount);
        Assert.All(reservations, x => Assert.Equal(RefundStatus.Pending, x.Status));
        Assert.Equal(
            200m,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).ManualBankReservedAmount);
    }

    [Fact]
    public async Task PendingReservation_RemovesAmountFromCompetingRefundPaths()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        await SeedBankDetailAsync(harness);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var orderId = (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).OrderId!.Value;
        var eligible = await OneTimeOrderRefund.FindEligiblePaymentsAsync(
            harness.Db, orderId, harness.Customer.Id, CancellationToken.None);
        Assert.Empty(eligible);
        Assert.False(await OneTimeOrderRefund.HasVerifiedSettlementAsync(
            harness.Db, orderId, harness.Customer.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CompleteManualBankTicket_IsBlockedReservationStaysPending()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        await SeedBankDetailAsync(harness);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("attempt"),
            CancellationToken.None));

        Assert.Contains("manual bank transfer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(
            RefundStatus.Pending, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ConfirmManualBank_InvalidUtr_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m);
        var service = CreateRefundService(harness);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() => service.ConfirmManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ConfirmManualBankTransferRequest("ABC", null),
            CancellationToken.None));

        Assert.Contains("6", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(
            RefundStatus.Pending, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
    }

    [Fact]
    public async Task ConfirmManualBank_SettlesExactlyOnceWithNoWalletOrGatewayMovement()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m);
        await ClaimAndGenerateAsync(harness, ticketId);
        var service = CreateRefundService(harness);

        var confirmed = await service.ConfirmManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ConfirmManualBankTransferRequest("UTR20260001", "Paid via NEFT"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, confirmed.Status);
        var reservation = await harness.Db.Refunds.AsNoTracking().SingleAsync();
        Assert.Equal(RefundStatus.Succeeded, reservation.Status);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(200m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());

        // Replaying the confirmation returns the confirmed result without
        // duplicate settlement.
        var replayed = await service.ConfirmManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ConfirmManualBankTransferRequest("UTR20260001", "Paid via NEFT"),
            CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed, replayed.Status);
        Assert.Equal(200m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ConfirmManualBank_OnWalletTicket_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ConfirmManualBankTransferRequest("UTR20260001", null),
            CancellationToken.None));
    }

    [Fact]
    public async Task ReleaseManualBank_RestoresPaymentAndVoidsTicket()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m);
        var service = CreateRefundService(harness);

        var released = await service.ReleaseManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ReleaseManualBankTransferRequest("Wrong beneficiary account", null),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Rejected, released.Status);
        Assert.Equal(
            RefundStatus.Failed, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal(0m, payment.RefundedAmount);
        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal("Wrong beneficiary account", stored.ManualBankVoidReason);
        Assert.NotNull(stored.ManualBankVoidedAt);

        // The freed amount is available to refund paths again.
        harness.Db.ChangeTracker.Clear();
        var orderId = stored.OrderId!.Value;
        var eligible = await OneTimeOrderRefund.FindEligiblePaymentsAsync(
            harness.Db, orderId, harness.Customer.Id, CancellationToken.None);
        Assert.Equal(200m, eligible.Sum(x => x.Amount - x.RefundedAmount));
    }

    [Fact]
    public async Task ReleaseManualBank_WithoutReason_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m);
        var service = CreateRefundService(harness);

        await Assert.ThrowsAsync<ValidationAppException>(() => service.ReleaseManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ReleaseManualBankTransferRequest("  ", null),
            CancellationToken.None));

        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(
            RefundStatus.Pending, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ReleaseManualBank_AfterConfirm_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m);
        await ClaimAndGenerateAsync(harness, ticketId);
        var service = CreateRefundService(harness);
        await service.ConfirmManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ConfirmManualBankTransferRequest("UTR20260001", null),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => service.ReleaseManualBankTransferAsync(
            StaffActor(harness), ticketId,
            new ReleaseManualBankTransferRequest("Too late", null),
            CancellationToken.None));

        // Settlement stands: no release, no duplicate movement.
        Assert.Equal(200m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(
            RefundStatus.Succeeded, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ProfileEdit_AfterApproval_LeavesSnapshotUnchanged()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedManualBankTicketAsync(harness, 200m);

        var protector = new BankDetailProtector(SharedDataProtection);
        var detail = await harness.Db.CustomerBankDetails.SingleAsync(x => x.UserId == harness.Customer.Id);
        detail.Update(
            "Changed Name", "Changed Bank", protector.Protect("5555555555555555"), "5555", "CHGD0009999");
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(HolderName, stored.ManualBankHolderNameSnapshot);
        Assert.Equal(BankName, stored.ManualBankNameSnapshot);
        Assert.Equal(Ifsc, stored.ManualBankIfscSnapshot);
        Assert.Equal(AccountLast4, stored.ManualBankAccountLast4Snapshot);
        Assert.Equal(200m, stored.ManualBankReservedAmount);
    }

    [Fact]
    public async Task LegacyTicket_CompletesThroughWalletPathUnchanged()
    {
        // A ticket approved through the legacy domain path (no settlement
        // method recorded) keeps established wallet behavior end to end.
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness, preApprove: true);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        var service = CreateRefundService(harness);

        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundSettlementMethod.Unspecified, stored.SettlementMethod);

        var completed = await service.CompleteAsync(
            StaffActor(harness), ticketId,
            new DecideRefundReplacementRequest("legacy"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(200m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(
            RefundStatus.Succeeded, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ConcurrentManualBankApprovals_ReserveExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, 200m, PaymentMethod.Wallet);
        await SeedBankDetailAsync(harness);

        var firstDb = harness.CreateContext();
        var secondDb = harness.CreateContext();
        await using var _ = firstDb;
        await using var __ = secondDb;
        var firstService = CreateRefundServiceFor(firstDb, harness);
        var secondService = CreateRefundServiceFor(secondDb, harness);

        var outcomes = await Task.WhenAll(
            new[]
            {
                firstService.ApproveAsync(
                    StaffActor(harness), ticketId,
                    new ApproveRefundReplacementRequest(
                        null, ApplyDeduction: false, WaiverReason: "Goodwill",
                        SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
                    CancellationToken.None),
                secondService.ApproveAsync(
                    StaffActor(harness), ticketId,
                    new ApproveRefundReplacementRequest(
                        null, ApplyDeduction: false, WaiverReason: "Goodwill",
                        SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
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
        Assert.Single(outcomes, x => x.Result is null);

        await using var verifyDb = harness.CreateContext();
        await using var ___ = verifyDb;
        Assert.Equal(
            RefundReplacementStatus.Approved,
            await verifyDb.RefundReplacementRequests.AsNoTracking().Select(x => x.Status).SingleAsync());
        Assert.Equal(1, await verifyDb.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(200m, (await verifyDb.Refunds.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(0, await verifyDb.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task SubscriptionFailRefund_ManualBank_ReservesShareAndSettlesOnConfirm()
    {
        // First-failure subscription refund: frozen share (80 − 10% = 72)
        // reserved against the subscription payment, settled on confirm.
        await using var harness = await CreateHarnessAsync();
        await FailSubscriptionAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        await SeedBankDetailAsync(harness);
        var requestId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);

        Assert.Equal(RefundSettlementMethod.ManualBankTransfer, approved.SettlementMethod);
        Assert.Equal(72m, approved.ReservedAmount);
        var reservation = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(RefundStatus.Pending, reservation.Status);
        Assert.Equal(72m, reservation.Amount);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());

        await ClaimAndGenerateAsync(harness, requestId);
        var confirmed = await service.ConfirmManualBankTransferAsync(
            StaffActor(harness), requestId,
            new ConfirmManualBankTransferRequest("UTR20260072", null),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, confirmed.Status);
        Assert.Equal(72m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        Assert.Equal(
            RefundStatus.Succeeded, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
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
        DeliveryServiceTests.DeliveryHarness harness) =>
        CreateRefundServiceFor(harness.Db, harness);

    private static RefundReplacementService CreateRefundServiceFor(
        Infrastructure.Persistence.DoodhDirectDbContext db,
        DeliveryServiceTests.DeliveryHarness harness) => new(
        db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(db, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }),
        new BankDetailProtector(SharedDataProtection),
        new ManualBankBeneficiaryProtector(SharedDataProtection));

    // One shared ephemeral provider per test run: the seeder and every
    // service instance must agree on keys, exactly like production agrees
    // on the durable key ring. Tests in this class run sequentially.
    private static readonly EphemeralDataProtectionProvider SharedDataProtection = new();

    private static async Task SeedBankDetailAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var protector = new BankDetailProtector(SharedDataProtection);
        harness.Db.CustomerBankDetails.Add(new CustomerBankDetail(
            harness.Customer.Id,
            HolderName,
            BankName,
            protector.Protect(FullAccountNumber),
            AccountLast4,
            Ifsc));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

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
            // Stage-1-era row: deduction decision recorded through the
            // domain, settlement method never designated (Unspecified).
            ticket.RecordDeductionDecision(false, 0m, "Legacy waiver recorded before Stage 2.");
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

    /// <summary>
    /// Stage 3 gate: direct settlement requires a generated export item, so
    /// tests that confirm through <see cref="RefundReplacementService"/>
    /// claim the ticket into a batch and generate it first.
    /// </summary>
    private static RefundBankExportService CreateExportService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        CreateRefundService(harness),
        new CompanyBankService(
            harness.Db, harness.TimeProvider, new CompanyBankDetailProtector(SharedDataProtection)),
        new ManualBankBeneficiaryProtector(SharedDataProtection),
        new CompanyBankDetailProtector(SharedDataProtection));

    private static async Task SeedExportPrerequisitesAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        if (!await harness.Db.CompanyBankDetails.AnyAsync())
        {
            var protector = new CompanyBankDetailProtector(SharedDataProtection);
            var detail = new CompanyBankDetail(
                "DoodhDirect Foods Pvt Ltd",
                "State Bank of India",
                protector.Protect("123456789012"),
                "9012",
                "SBIN0001234");
            detail.SetCreated(harness.TimeProvider.Now);
            harness.Db.CompanyBankDetails.Add(detail);
        }

        if (!await harness.Db.NumberSeries.AnyAsync(x => x.Code == RefundBankExportService.NumberSeriesCode))
        {
            harness.Db.NumberSeries.Add(new NumberSeries(
                RefundBankExportService.NumberSeriesCode,
                "Manual-bank refund export batch numbers",
                "RBE/{NUMBER:000000}",
                1,
                1,
                NumberSeriesResetPolicy.Never));
        }

        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static async Task ClaimAndGenerateAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        Guid ticketId)
    {
        await SeedExportPrerequisitesAsync(harness);
        var exports = CreateExportService(harness);
        var batch = await exports.CreateBatchAsync(
            StaffActor(harness),
            new CreateRefundBankExportBatchRequest([ticketId]),
            CancellationToken.None);
        await exports.GenerateBatchAsync(StaffActor(harness), batch.BatchId, CancellationToken.None);
    }

    private static async Task<Guid> SeedManualBankTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        decimal paymentAmount)
    {
        var ticketId = await SeedOneTimeTicketAsync(harness);
        await SeedOrderPaymentAsync(harness, ticketId, paymentAmount, PaymentMethod.Wallet);
        await SeedBankDetailAsync(harness);
        var service = CreateRefundService(harness);
        await service.ApproveAsync(
            StaffActor(harness), ticketId,
            new ApproveRefundReplacementRequest(
                null, ApplyDeduction: false, WaiverReason: "Goodwill",
                SettlementMethod: RefundSettlementMethod.ManualBankTransfer),
            CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        return ticketId;
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

    private static async Task FailSubscriptionAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);
        harness.Db.ChangeTracker.Clear();
    }

    private static async Task SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            harness.Subscription.PayableAmount,
            "INR",
            $"manual-bank-sub-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }
}
