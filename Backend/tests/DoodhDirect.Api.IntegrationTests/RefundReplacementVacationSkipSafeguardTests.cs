using System.Security.Claims;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// D1-D slice 2.1 — vacation/skip conflict safeguard (Rule 7).
///
/// Discovered model truth (verified in tree, see also the
/// <c>CreateVacationRequest</c> contract): no vacation entity is persisted.
/// Vacation is a batch skip resolved to per-occurrence <c>Skipped</c> rows, so
/// the enforceable vacation/skip rules are: (a) a date already occupied by a
/// vacation/skipped occurrence is never proposed and is rejected at approval
/// inside the authoritative serializable transaction; (b) a scheduled
/// re-delivery replacement can never be consumed by an ordinary skip or
/// vacation — both are rejected/reported, never silently applied.
/// </summary>
public sealed class RefundReplacementVacationSkipSafeguardTests
{
    [Fact]
    public async Task Propose_SkipsOverVacationSkippedDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        // Seed a vacation/skipped occurrence on the otherwise-proposed date.
        await SkipDateAsync(harness, new DateOnly(2026, 8, 18));
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var proposal = await service.ProposeReplacementDateAsync(
            StaffActor(harness), requestId, CancellationToken.None);

        // 2026-08-17 fails the cutoff, 2026-08-18 is vacation-skipped.
        Assert.Equal(new DateOnly(2026, 8, 19), proposal.ProposedDate);
        // Advisory only: failed source + skipped date, no replacement row.
        Assert.Equal(2, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        Assert.DoesNotContain(
            await harness.Db.SubscriptionDeliveries.AsNoTracking().ToListAsync(),
            x => x.IsReplacement);
    }

    [Fact]
    public async Task Approve_RejectsVacationSkippedDate_ThenSucceedsOnAlternativeDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await SkipDateAsync(harness, new DateOnly(2026, 8, 18));
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() => service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 18)),
            CancellationToken.None));
        Assert.Equal("replacementDate", exception.Field);
        Assert.Contains("vacation or skipped", exception.Message, StringComparison.OrdinalIgnoreCase);

        // Rejection writes nothing: choice immutability and the approval
        // lifecycle survive, so staff can retry with an eligible date.
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(2, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
        var pending = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, pending.Status);
        Assert.Equal(CustomerChoice.ReDelivery, pending.CustomerChoice);

        var approved = await service.ApproveAsync(
            StaffActor(harness),
            requestId,
            new ApproveRefundReplacementRequest(null, new DateOnly(2026, 8, 19)),
            CancellationToken.None);

        Assert.Equal(RefundReplacementStatus.Approved, approved.Status);
        Assert.Equal(new DateOnly(2026, 8, 19), approved.ReplacementDate);
        Assert.NotNull(approved.ReplacementDeliveryPublicId);
        var replacement = Assert.Single(await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .Where(x => x.IsReplacement).ToListAsync());
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.Equal(new DateOnly(2026, 8, 19), replacement.ScheduledDate);
    }

    [Fact]
    public async Task Skip_ReplacementDelivery_IsRejectedAndStaysScheduled()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var refundService = CreateRefundService(harness);
        await ChooseReDeliveryAsync(refundService, harness, requestId);
        var approved = await refundService.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 8, 18), approved.ReplacementDate);

        var subscriptionService = CreateSubscriptionService(harness);
        var subscriptionPublicId = await harness.Db.Subscriptions.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            subscriptionService.SkipAsync(
                harness.Customer.Id,
                subscriptionPublicId,
                new SkipSubscriptionDeliveryRequest(approved.ReplacementDeliveryPublicId!.Value),
                CancellationToken.None));
        Assert.Contains("explicit operational decision", exception.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        var replacement = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .SingleAsync(x => x.IsReplacement);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
    }

    [Fact]
    public async Task Vacation_ReplacementDelivery_IsIneligibleNeverSkipped()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var refundService = CreateRefundService(harness);
        await ChooseReDeliveryAsync(refundService, harness, requestId);
        var approved = await refundService.ApproveAsync(
            StaffActor(harness), requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        Assert.Equal(new DateOnly(2026, 8, 18), approved.ReplacementDate);

        // An ordinary scheduled occurrence beside the replacement proves the
        // vacation still skips what it legitimately can.
        await AddDeliveryAsync(harness, new DateOnly(2026, 8, 19));

        var subscriptionService = CreateSubscriptionService(harness);
        var result = await subscriptionService.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 19)),
            CancellationToken.None);

        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(new DateOnly(2026, 8, 19), Assert.Single(result.SkippedDates).Date);
        var blocked = Assert.Single(result.Ineligible);
        Assert.Equal(new DateOnly(2026, 8, 18), blocked.Date);
        Assert.Equal("replacementDelivery", blocked.Reason);

        var rows = await harness.Db.SubscriptionDeliveries.AsNoTracking()
            .OrderBy(x => x.ScheduledDate).ToListAsync();
        Assert.Equal(SubscriptionDeliveryStatus.Failed, rows[0].Status);
        var replacement = Assert.Single(rows, x => x.IsReplacement);
        Assert.Equal(SubscriptionDeliveryStatus.Scheduled, replacement.Status);
        Assert.Equal(
            SubscriptionDeliveryStatus.Skipped,
            rows.Single(x => x.ScheduledDate == new DateOnly(2026, 8, 19)).Status);
    }

    [Fact]
    public async Task ProposalController_ReturnsNextEligibleDate()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var controller = new RefundReplacementController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim("user_id", harness.Manager.Id.ToString()),
                            new Claim(AuthorizationCodes.BranchClaim, harness.Branch.Id.ToString()),
                            new Claim(AuthorizationCodes.PermissionClaim, AuthorizationCodes.GlobalAccess),
                        ],
                        authenticationType: "Test"))
                }
            }
        };

        var response = await controller.ProposeReplacementDate(requestId, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);
        var envelope = Assert.IsType<ApiResponse<ReplacementDateProposalResult>>(objectResult.Value);
        Assert.True(envelope.Success);
        Assert.NotNull(envelope.Data);
        Assert.Equal(requestId, envelope.Data.RequestId);
        Assert.Equal(new DateOnly(2026, 8, 18), envelope.Data.ProposedDate);
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

    private static async Task ChooseReDeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId) =>
        await service.ChooseAsync(
            CustomerActor(harness),
            requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery),
            CancellationToken.None);

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) => new(
            harness.Db,
            harness.TimeProvider,
            harness.ConfigurationService,
            new NumberSeriesService(harness.Db, harness.TimeProvider),
            harness.ProofStorage,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    private static SubscriptionService CreateSubscriptionService(
        DeliveryServiceTests.DeliveryHarness harness) =>
        // Skip/vacation paths never touch branch allocation or payments.
        new(
            harness.Db,
            null!,
            null!,
            harness.TimeProvider,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    /// <summary>
    /// Seeds a vacation/skipped occurrence on <paramref name="date"/> the same
    /// way the product does — an ordinary <c>Scheduled</c> occurrence skipped
    /// through the domain operation (the vacation path delegates to it).
    /// </summary>
    private static async Task SkipDateAsync(
        DeliveryServiceTests.DeliveryHarness harness, DateOnly date)
    {
        await AddDeliveryAsync(harness, date);
        var subscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries).SingleAsync();
        subscription.Skip(
            subscription.Deliveries.Single(x => x.ScheduledDate == date),
            harness.TimeProvider.Now,
            TimeSpan.FromHours(24));
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static async Task AddDeliveryAsync(
        DeliveryServiceTests.DeliveryHarness harness, DateOnly date)
    {
        var subscription = await harness.Db.Subscriptions
            .Include(x => x.Deliveries).SingleAsync();
        subscription.AddDelivery(date);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }
}
