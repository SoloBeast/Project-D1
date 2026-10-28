using System.Text.Json;
using System.Text.Json.Serialization;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 5.2 — correct non-delivery terminal state (Rule 8) and complete
/// refund-resolution wiring.
///
/// <c>Completed</c> keeps meaning successful fulfillment. When a re-delivery
/// replacement fails and its refund-only remedy succeeds, the original
/// approved ReDelivery ticket closes as <c>Failed</c> — never
/// <c>Completed</c> — with the same remarks, audit
/// (<c>Resolution = "ReplacementFailedRefunded"</c>), and customer event on
/// every path. Both money-moving paths share one frozen implementation and
/// one <c>delivery-fail:</c> idempotency key: the D1-C automatic timeout
/// refund, and staff completion of an explicitly Refund-chosen second-failure
/// ticket (Choose → Approve → Complete executes the frozen refund in the same
/// serializable transaction). Choice-only, approval-pending, infeasible, and
/// failed refunds never resolve the original.
/// </summary>
public sealed class RefundReplacementStaffRefundResolutionTests
{
    [Fact]
    public async Task StaffRefundChoice_Approve_Complete_ResolvesOriginalAsFailed()
    {
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        var service = CreateRefundService(harness);

        await service.ChooseAsync(
            CustomerActor(harness), secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        var completed = await service.CompleteAsync(
            StaffActor(harness), secondId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        // The second (refund-only) ticket completed exactly like a D1-C refund.
        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        var second = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == secondId);
        Assert.Equal(RefundReplacementStatus.Completed, second.Status);
        Assert.Equal(CustomerChoice.Refund, second.CustomerChoice);
        Assert.Equal(RefundReplacementType.Refund, second.Type);
        Assert.Equal(harness.Manager.Id, second.CompletedByUserId);
        Assert.NotNull(second.CompletedAt);

        // Frozen math, identical to the timeout path: payable 160 / 2 = 80
        // gross, 10% snapshot = 8, final 72 — under the same idempotency key.
        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(72m, walletTxn.Amount);
        Assert.Equal(WalletTransactionType.RefundCredit, walletTxn.Type);
        Assert.Equal($"delivery-fail:{secondId:N}:refund", walletTxn.IdempotencyKey);
        Assert.Equal(72m, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(72m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Equal($"delivery-fail:{secondId:N}:refund", refund.IdempotencyKey);

        // Rule 8: the original remedy (re-delivery) never succeeded, so the
        // original closes as Failed — Completed is not used for non-delivery.
        var original = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == firstId);
        Assert.Equal(RefundReplacementStatus.Failed, original.Status);
        Assert.NotEqual(RefundReplacementStatus.Completed, original.Status);
        Assert.Equal(CustomerChoice.ReDelivery, original.CustomerChoice);
        Assert.Equal(RefundReplacementType.Replacement, original.Type);
        Assert.Equal(harness.Manager.Id, original.CompletedByUserId);
        Assert.NotNull(original.CompletedAt);
        Assert.Contains("non-delivered", original.CompletionRemarks ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(second.RequestNumber, original.CompletionRemarks ?? string.Empty, StringComparison.Ordinal);

        var resolvedAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED").ToListAsync());
        Assert.Equal(firstId.ToString(), resolvedAudit.EntityId);
        Assert.Contains(RefundReplacementStatus.Approved.ToString(), resolvedAudit.OldValueJson ?? string.Empty);
        Assert.Contains(RefundReplacementStatus.Failed.ToString(), resolvedAudit.NewValueJson ?? string.Empty);
        Assert.Contains("ReplacementFailedRefunded", resolvedAudit.NewValueJson ?? string.Empty);
        Assert.Contains(second.RequestNumber, resolvedAudit.NewValueJson ?? string.Empty);

        var resolvedEvent = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved).ToListAsync());
        Assert.Equal($"refund-replacement:{firstId:N}:redelivery-failed-resolved", resolvedEvent.EventKey);

        var completeAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.COMPLETE" && x.EntityId == secondId.ToString())
            .ToListAsync());
        Assert.Contains("SecondFailureStaffRefund", completeAudit.NewValueJson ?? string.Empty);

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
    public async Task StaffCompletion_FirstFailureRefund_ExecutesSharedRefund()
    {
        // Audit remediation A: an explicitly Refund-chosen FIRST-failure
        // ticket executes the shared delivery-fail refund at completion —
        // frozen share credited, refund row written. No original-ticket
        // resolution applies here (there is no replacement chain yet), so no
        // REDELIVERY_FAILED_RESOLVED event is emitted.
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var completed = await service.CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(72m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(72m, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED"));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved));
    }

    [Fact]
    public async Task Approve_SubscriptionFailRefund_WithoutDecision_IsRejected()
    {
        // Stage 3: approving a subscription failed-delivery Refund without
        // support's explicit deduction decision is rejected — the deduction
        // must never default silently, mirroring the order-backed flow.
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        var error = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));

        Assert.Contains("deduction decision", error.Message, StringComparison.OrdinalIgnoreCase);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Approve_SubscriptionFailRefund_Apply_RecordsFrozenSnapshot()
    {
        // Applying reuses the percentage frozen when the failure was
        // recorded (10% in this harness), identical to the automatic timeout
        // path — never the live configuration value.
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest("Customer fault confirmed", ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(10m, approved.AppliedDeductionPercentage);
        Assert.False(approved.DeductionWaived);
        Assert.Null(approved.DeductionWaiverReason);
        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(10m, stored.AppliedDeductionPercentage);
        Assert.False(stored.DeductionWaived);
        // Approval alone moves no money.
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Approve_SubscriptionFailRefund_Waive_RequiresReason()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        var requestId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));
        await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "  ", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None));
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(
            RefundReplacementStatus.AwaitingCustomerChoice,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task StaffCompletion_WaivedSubscriptionRefund_CreditsFullShare()
    {
        // A waived deduction settles the full frozen share: payable 160 / 2
        // = 80 gross with zero deduction, unlike the 72 on the applied path.
        await using var harness = await CreateHarnessAsync();
        await FailAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        var approved = await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: false, WaiverReason: "Dairy fault confirmed", SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);

        Assert.Equal(0m, approved.AppliedDeductionPercentage);
        Assert.True(approved.DeductionWaived);
        Assert.Equal("Dairy fault confirmed", approved.DeductionWaiverReason);

        var completed = await service.CompleteAsync(
            StaffActor(harness), requestId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Completed, completed.Status);
        Assert.Equal(80m, (await harness.Db.WalletTransactions.AsNoTracking().SingleAsync()).Amount);
        Assert.Equal(80m, (await harness.Db.Refunds.AsNoTracking().SingleAsync()).Amount);
        var stored = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.True(stored.DeductionWaived);
    }

    [Fact]
    public async Task StaffCompletion_WithoutCompatiblePayment_ThrowsAndLeavesBothApproved()
    {
        // No successful subscription payment: the completion itself rolls
        // back, so the second ticket stays Approved, the original stays
        // Approved, and nothing is half-written — mirroring the timeout path.
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness), secondId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None));
        Assert.Contains("no successful payment", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == secondId)).Status);
        Assert.Equal(
            RefundReplacementStatus.Approved,
            (await harness.Db.RefundReplacementRequests.AsNoTracking()
                .SingleAsync(x => x.PublicId == firstId)).Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED"));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved));
    }

    [Fact]
    public async Task StaffCompletion_Twice_SecondIsConflictWithoutDuplication()
    {
        await using var harness = await CreateHarnessAsync();
        await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), secondId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<ConflictException>(() => service.CompleteAsync(
            StaffActor(harness), secondId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None));
        Assert.Contains("approved", error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved));
    }

    [Fact]
    public async Task TimeoutAfterStaffCompletion_SkipsWithoutDuplication()
    {
        // Cross-path idempotency: once staff resolves the pair, the timeout
        // worker observes the resolved state and performs nothing — the same
        // delivery-fail: key is never credited twice.
        await using var harness = await CreateHarnessAsync();
        await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), secondId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);
        Assert.Equal(0, outcome);
        Assert.Null(await TimeoutProcessor(harness).ProcessTicketAsync(secondId, CancellationToken.None));

        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryFailedResolved));
    }

    [Fact]
    public async Task FailedOutcome_SerializesAndPersistsAsFailed()
    {
        // The new terminal outcome survives every boundary as "Failed": the
        // API contract enum, the JSON wire format (JsonStringEnumConverter, as
        // configured in Program.cs), the string-mapped database column, and
        // the audit trail — while Completed keeps its success meaning.
        await using var harness = await CreateHarnessAsync();
        var firstId = await FailAndApproveRedeliveryAsync(harness);
        var secondId = await FailReplacementAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), secondId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), secondId, new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), secondId,
            new DecideRefundReplacementRequest("Refund verified and paid"),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var original = await service.GetForStaffAsync(StaffActor(harness), firstId, CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Failed, original.Status);
        Assert.NotEqual(RefundReplacementStatus.Completed, original.Status);

        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonStringEnumConverter());
        Assert.Equal("\"Failed\"", JsonSerializer.Serialize(original.Status, options));
        Assert.Equal(
            RefundReplacementStatus.Failed,
            JsonSerializer.Deserialize<RefundReplacementStatus>("\"Failed\"", options));
        // Unknown values still fail safe, and Completed still means success.
        Assert.Equal("\"Completed\"", JsonSerializer.Serialize(RefundReplacementStatus.Completed, options));

        var persisted = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .SingleAsync(x => x.PublicId == firstId);
        Assert.Equal(RefundReplacementStatus.Failed, persisted.Status);

        var resolvedAudit = Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_FAILED_RESOLVED").ToListAsync());
        Assert.Contains(RefundReplacementStatus.Failed.ToString(), resolvedAudit.NewValueJson ?? string.Empty);
        Assert.DoesNotContain(
            RefundReplacementStatus.Completed.ToString(),
            resolvedAudit.NewValueJson ?? string.Empty);
    }

    [Fact]
    public void MarkRemedyFailed_ClosesApprovedAsFailed_NeverCompleted()
    {
        // Domain-level Rule 8: the non-delivery closure is available from
        // exactly the same state as Complete (Approved), the two outcomes are
        // mutually exclusive, and Failed reuses the CompletedBy* columns for
        // who closed the ticket and when.
        var now = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Unspecified);
        var ticket = new RefundReplacementRequest(
            null, 1, 1, 1, null,
            RefundReplacementType.Replacement,
            RefundReplacementSource.DeliveryFailed,
            "Customer not available",
            null,
            new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Unspecified),
            subscriptionDeliveryId: 7,
            failedAt: new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Unspecified),
            deliveryFailWindowHoursSnapshot: 24,
            deliveryFailDeductionPercentageSnapshot: 10m);
        ticket.Approve(9, now, null);

        ticket.MarkRemedyFailed(11, now.AddHours(1), "Replacement failed; refunded.");

        Assert.Equal(RefundReplacementStatus.Failed, ticket.Status);
        Assert.NotEqual(RefundReplacementStatus.Completed, ticket.Status);
        Assert.Equal(11, ticket.CompletedByUserId);
        Assert.NotNull(ticket.CompletedAt);
        Assert.Equal("Replacement failed; refunded.", ticket.CompletionRemarks);

        var pending = new RefundReplacementRequest(
            null, 1, 1, 1, null,
            RefundReplacementType.Replacement,
            RefundReplacementSource.DeliveryFailed,
            "Customer not available",
            null,
            new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Unspecified),
            subscriptionDeliveryId: 7,
            failedAt: new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Unspecified),
            deliveryFailWindowHoursSnapshot: 24,
            deliveryFailDeductionPercentageSnapshot: 10m);
        Assert.Throws<InvalidOperationException>(() => pending.MarkRemedyFailed(11, now, null));

        var approved = new RefundReplacementRequest(
            null, 1, 1, 1, null,
            RefundReplacementType.Refund,
            RefundReplacementSource.DeliveryFailed,
            "Customer not available",
            null,
            new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Unspecified),
            subscriptionDeliveryId: 7,
            failedAt: new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Unspecified),
            deliveryFailWindowHoursSnapshot: 24,
            deliveryFailDeductionPercentageSnapshot: 10m);
        approved.Approve(9, now, null);
        approved.Complete(9, now.AddHours(1), null);
        Assert.Equal(RefundReplacementStatus.Completed, approved.Status);
        Assert.NotEqual(RefundReplacementStatus.Failed, approved.Status);
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
        var service = CreateRefundService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null, SettlementMethod: RefundSettlementMethod.Wallet), CancellationToken.None);
        harness.Db.ChangeTracker.Clear();
        return requestId;
    }

    /// <summary>
    /// Fails the approved replacement itself via the staff failure path and
    /// returns the second (refund-only) ticket's public id.
    /// </summary>
    private static async Task<Guid> FailReplacementAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var firstId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, new DateOnly(2026, 8, 18), CancellationToken.None);
        var replacementDeliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null && x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId)
            .SingleAsync();

        await harness.AssignAsync(replacementDeliveryId, harness.Staff);
        await harness.FailSubscriptionAsync(replacementDeliveryId, harness.Staff);

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

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
        Options.Create(new PaymentOptions { Currency = "INR" }));

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
            $"staff-resolution-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }
}
