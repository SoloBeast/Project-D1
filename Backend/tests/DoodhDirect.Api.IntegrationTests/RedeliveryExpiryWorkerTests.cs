using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Notifications;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Automatic expired re-delivery notifications. The dedicated sweeper finds
/// tickets past <c>CustomerChoiceAt + 1 hour</c> while still awaiting staff
/// action and ensures the single deterministic customer notice through the
/// existing NotificationEvent pipeline (inbox + push, no app open needed).
/// Notice-only: never converts, approves, or moves money.
/// </summary>
public sealed class RedeliveryExpiryWorkerTests
{
    [Fact]
    public async Task Sweep_BeforeExpiry_NotifiesNothing()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);

        var notified = await SweepAsync(harness);

        Assert.Equal(0, notified);
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired"));
        Assert.Equal(0, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
    }

    [Fact]
    public async Task Sweep_AfterExpiry_NotifiesOnceAndChangesNothing()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        var notified = await SweepAsync(harness);

        Assert.Equal(1, notified);
        harness.Db.ChangeTracker.Clear();
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(CustomerChoice.ReDelivery, ticket.CustomerChoice);
        Assert.Equal(RefundReplacementType.Replacement, ticket.Type);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        var notice = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired").ToListAsync());
        Assert.Equal(NotificationEventTypes.RedeliveryExpired, notice.EventType);
        Assert.Equal(harness.Customer.Id, notice.UserId);
        Assert.Contains("one-hour", notice.PayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Sweep_ClosedApps_PipelineDeliversInboxAndPush()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        // No app open: only the background sweep runs.
        Assert.Equal(1, await SweepAsync(harness));

        // Seed the generic template and a push device, then run the existing
        // NotificationWorker pipeline (no app open needed).
        await new NotificationTemplateSeedService(harness.Db).SeedAsync(default);
        var dataProtection = new EphemeralDataProtectionProvider();
        var sharedTokenProtector = new NotificationTokenProtector(dataProtection);
        var sharedOtpProtector = new DeliveryOtpHandoffProtector(dataProtection);
        var notifications = new NotificationService(
            harness.Db,
            new SecureTokenGenerator(),
            sharedTokenProtector,
            harness.Clock);
        await notifications.RegisterDeviceAsync(
            new NotificationActor(harness.Customer.Id),
            new RegisterDeviceRequest("sweep-device", "push-token-sweep", "android", "Sweep device"),
            default);
        var gateways = Enum.GetValues<NotificationChannel>()
            .ToDictionary(channel => channel, channel => (INotificationChannelGateway)new ScriptedGateway(channel));
        var processor = new NotificationProcessor(
            harness.Db,
            gateways.Values,
            sharedTokenProtector,
            sharedOtpProtector,
            Options.Create(new NotificationOptions
            {
                BatchSize = 25,
                MaxAttempts = 4,
                InitialRetryDelayMinutes = 1,
                PollIntervalSeconds = 10
            }),
            harness.TimeProvider);
        // The harness fail flow already queued delivery events, so the sweep
        // notice is one of several pending events; closed apps still receive
        // it because the worker pipeline needs no open app.
        Assert.True(await processor.ProcessPendingEventsAsync(default) >= 1);
        await processor.ProcessDueDeliveriesAsync(default);

        harness.Db.ChangeTracker.Clear();
        var notification = await harness.Db.Notifications.AsNoTracking()
            .Include(x => x.Deliveries)
            .Where(x => x.EventType == NotificationEventTypes.RedeliveryExpired)
            .SingleAsync();
        Assert.Equal($"/refund-replacements/{requestId}", notification.DeepLink);
        Assert.Contains("one-hour", notification.Body, StringComparison.OrdinalIgnoreCase);
        var push = Assert.Single(notification.Deliveries.Where(x => x.Channel == NotificationChannel.Push));
        Assert.Equal(NotificationDeliveryStatus.Delivered, push.Status);
        var pushGateway = (ScriptedGateway)gateways[NotificationChannel.Push];
        Assert.Contains(pushGateway.Messages, x => x.DeepLink == $"/refund-replacements/{requestId}");
    }

    [Fact]
    public async Task Sweep_RestartRecovery_PicksUpMissedExpirations()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var firstId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, firstId);

        // Outage: the window passes with no sweep running.
        harness.Clock.Advance(TimeSpan.FromHours(2));

        // Recovery: the next cycle picks the missed expiration up.
        Assert.Equal(1, await SweepAsync(harness));
        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{firstId:N}:redelivery-expired"));
    }

    [Fact]
    public async Task Sweep_RetryAndConcurrentLazyCalls_NotifyExactlyOnce()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(1, await SweepAsync(harness));
        // Retry and concurrent lazy customer/staff calls add nothing.
        Assert.Equal(0, await SweepAsync(harness));
        await service.NotifyRedeliveryExpiredAsync(
            CustomerActor(harness), requestId, CancellationToken.None);
        await service.NotifyRedeliveryExpiredAsync(
            StaffActor(harness), requestId, CancellationToken.None);
        Assert.Equal(0, await SweepAsync(harness));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
    }

    [Fact]
    public async Task Sweep_IneligibleTickets_AreIgnored()
    {
        // No-choice ticket: no Re-Delivery recorded, so no expiry notice.
        await using (var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync())
        {
            await FailAsync(harness);
            harness.Clock.Advance(TimeSpan.FromHours(2));
            Assert.Equal(0, await SweepAsync(harness));
            Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
                .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryExpired));
        }

        // Converted ticket: choice is now Refund, so the expiry sweep skips it.
        await using (var converted = await DeliveryServiceTests.DeliveryHarness.CreateAsync())
        {
            await FailAsync(converted);
            var requestId = await TicketPublicIdAsync(converted);
            var service = CreateRefundService(converted);
            await ChooseReDeliveryAsync(service, converted, requestId);
            converted.Clock.Advance(TimeSpan.FromHours(2));
            await service.ConvertExpiredRedeliveryToRefundAsync(
                StaffActor(converted), requestId,
                new ConvertExpiredRedeliveryRequest(true, null), CancellationToken.None);
            converted.Db.ChangeTracker.Clear();
            var before = await converted.Db.NotificationEvents.AsNoTracking()
                .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryExpired);
            Assert.Equal(0, await SweepAsync(converted));
            converted.Db.ChangeTracker.Clear();
            Assert.Equal(before, await converted.Db.NotificationEvents.AsNoTracking()
                .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryExpired));
        }
    }

    [Fact]
    public async Task Sweep_MovesNoMoneyAndLeavesTicketConvertible()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        await SeedSubscriptionPaymentAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(1, await SweepAsync(harness));

        harness.Db.ChangeTracker.Clear();
        var ticket = await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync();
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        Assert.Equal(CustomerChoice.ReDelivery, ticket.CustomerChoice);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0, (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount);

        // The ticket still converts and refunds through the existing workflow.
        await service.ConvertExpiredRedeliveryToRefundAsync(
            StaffActor(harness), requestId,
            new ConvertExpiredRedeliveryRequest(true, "Sweep noticed; customer confirmed"), CancellationToken.None);
        await service.ApproveAsync(
            StaffActor(harness), requestId,
            new ApproveRefundReplacementRequest(null, ApplyDeduction: true, SettlementMethod: RefundSettlementMethod.Wallet),
            CancellationToken.None);
        await service.CompleteAsync(
            StaffActor(harness), requestId, new DecideRefundReplacementRequest(null), CancellationToken.None);
        Assert.Equal(RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Sweep_DoesNotChangeNoChoiceTimeoutBehaviour()
    {
        // A no-choice ticket past its persisted Deadline still auto-refunds
        // via the existing timeout processor, and the redelivery sweep
        // ignores it (no recorded Re-Delivery choice).
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await SeedSubscriptionPaymentAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var timeout = new DeliveryFailTimeoutProcessor(
            harness.Db,
            harness.TimeProvider,
            new TestNotificationEventWriter(harness.Db, harness.TimeProvider),
            Options.Create(new DeliveryFailTimeoutOptions()),
            Options.Create(new PaymentOptions { Currency = "INR" }),
            NullLogger<DeliveryFailTimeoutProcessor>.Instance);
        Assert.NotNull(await timeout.ProcessTicketAsync(requestId, CancellationToken.None));
        Assert.Equal(RefundReplacementStatus.Completed,
            (await harness.Db.RefundReplacementRequests.AsNoTracking().SingleAsync()).Status);

        Assert.Equal(0, await SweepAsync(harness));
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RedeliveryExpired));
    }

    [Fact]
    public async Task Sweep_SmallBatches_ProgressAcrossSweeps()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var service = CreateRefundService(harness);
        await FailAsync(harness);
        var firstId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        await ChooseReDeliveryAsync(service, harness, firstId);
        var secondId = await FailAdditionalTicketAsync(harness, new DateOnly(2026, 8, 19));
        await ChooseReDeliveryAsync(service, harness, secondId);
        var thirdId = await FailAdditionalTicketAsync(harness, new DateOnly(2026, 8, 20));
        await ChooseReDeliveryAsync(service, harness, thirdId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        // Three expired tickets with BatchSize 2: the first sweep notifies
        // two, the next sweep must progress to the remaining one instead of
        // re-selecting the already-noticed first batch, and later sweeps
        // find nothing left to do.
        Assert.Equal(2, await SweepAsync(harness, batchSize: 2));
        Assert.Equal(1, await SweepAsync(harness, batchSize: 2));
        Assert.Equal(0, await SweepAsync(harness, batchSize: 2));

        harness.Db.ChangeTracker.Clear();
        var keys = await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RedeliveryExpired)
            .Select(x => x.EventKey)
            .ToListAsync();
        Assert.Equal(3, keys.Count);
        Assert.Contains($"refund-replacement:{firstId:N}:redelivery-expired", keys);
        Assert.Contains($"refund-replacement:{secondId:N}:redelivery-expired", keys);
        Assert.Contains($"refund-replacement:{thirdId:N}:redelivery-expired", keys);
        Assert.Equal(3, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
        var tickets = await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync();
        Assert.Equal(3, tickets.Count);
        Assert.All(tickets, ticket =>
        {
            Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
            Assert.Equal(CustomerChoice.ReDelivery, ticket.CustomerChoice);
        });
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Sweep_AlreadyNoticedTicket_IsExcludedFromSelection()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var service = CreateRefundService(harness);
        await FailAsync(harness);
        var firstId = await harness.Db.RefundReplacementRequests.AsNoTracking()
            .Select(x => x.PublicId).SingleAsync();
        await ChooseReDeliveryAsync(service, harness, firstId);
        var secondId = await FailAdditionalTicketAsync(harness, new DateOnly(2026, 8, 19));
        await ChooseReDeliveryAsync(service, harness, secondId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        // The first ticket is noticed through the lazy on-demand path.
        await service.NotifyRedeliveryExpiredAsync(CustomerActor(harness), firstId, CancellationToken.None);

        // With batch size 1 the sweep must skip the noticed ticket and select
        // the remaining one; without the exclusion it would re-select the
        // noticed first ticket and report nothing new.
        Assert.Equal(1, await SweepAsync(harness, batchSize: 1));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{firstId:N}:redelivery-expired"));
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{secondId:N}:redelivery-expired"));
    }

    [Fact]
    public async Task Sweep_RepeatedSweeps_DoNotReprocessNoticedTickets()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = CreateRefundService(harness);
        await ChooseReDeliveryAsync(service, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal(1, await SweepAsync(harness));
        Assert.Equal(0, await SweepAsync(harness));
        Assert.Equal(0, await SweepAsync(harness));
        Assert.Equal(0, await SweepAsync(harness));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
    }

    [Fact]
    public async Task Sweep_ConcurrentWorkerAndLazyNotice_NotifyExactlyOnce()
    {
        await using var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        var setupService = CreateRefundService(harness);
        await FailAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ChooseReDeliveryAsync(setupService, harness, requestId);
        harness.Clock.Advance(TimeSpan.FromHours(2));

        // Separate connections over the same shared in-memory database so the
        // worker sweep and the on-demand lazy notices race for real. SQLite
        // serializes the writers; the unique EventKey keeps the notice to
        // exactly one row and one audit.
        await using var workerDb = OpenSharedDb(harness);
        await using var customerDb = OpenSharedDb(harness);
        await using var staffDb = OpenSharedDb(harness);
        var workerService = CreateRefundServiceOn(workerDb, harness);
        var customerService = CreateRefundServiceOn(customerDb, harness);
        var staffService = CreateRefundServiceOn(staffDb, harness);
        var worker = new RedeliveryExpiryProcessor(
            workerDb,
            harness.TimeProvider,
            workerService,
            Options.Create(new RedeliveryExpiryOptions { BatchSize = 50 }),
            NullLogger<RedeliveryExpiryProcessor>.Instance);

        Exception? sweepError = null;
        Exception? customerError = null;
        Exception? staffError = null;
        await Task.WhenAll(
            Task.Run(async () =>
            {
                try { await worker.ProcessExpiredRedeliveriesAsync(CancellationToken.None); }
                catch (Exception exception) { sweepError = exception; }
            }),
            Task.Run(async () =>
            {
                try { await customerService.NotifyRedeliveryExpiredAsync(CustomerActor(harness), requestId, CancellationToken.None); }
                catch (Exception exception) { customerError = exception; }
            }),
            Task.Run(async () =>
            {
                try { await staffService.NotifyRedeliveryExpiredAsync(StaffActor(harness), requestId, CancellationToken.None); }
                catch (Exception exception) { staffError = exception; }
            }));

        // Losers either observe the winner's row (benign no-op) or lose the
        // unique-key commit race; anything else is unexpected.
        foreach (var error in new[] { sweepError, customerError, staffError })
        {
            Assert.True(
                error is null || error is ConflictException || error is DbUpdateException,
                $"Unexpected concurrent outcome: {error}");
        }

        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventKey == $"refund-replacement:{requestId:N}:redelivery-expired"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.REDELIVERY_EXPIRED"));
    }

    private static async Task<int> SweepAsync(DeliveryServiceTests.DeliveryHarness harness, int batchSize = 50)
    {
        var processor = new RedeliveryExpiryProcessor(
            harness.Db,
            harness.TimeProvider,
            CreateRefundService(harness),
            Options.Create(new RedeliveryExpiryOptions { BatchSize = batchSize }),
            NullLogger<RedeliveryExpiryProcessor>.Instance);
        return await processor.ProcessExpiredRedeliveriesAsync(CancellationToken.None);
    }

    private static async Task<Guid> FailAdditionalTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness, DateOnly scheduledDate)
    {
        var subscription = await harness.Db.Subscriptions.Include(x => x.Deliveries).SingleAsync();
        subscription.AddDelivery(scheduledDate);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        await harness.Service.MaterializeEligibleAsync(harness.ManagerActor, scheduledDate, CancellationToken.None);
        var deliveryId = await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.SubscriptionDelivery != null
                && x.SubscriptionDelivery.ScheduledDate == scheduledDate
                && !x.SubscriptionDelivery.IsReplacement)
            .Select(x => x.PublicId).SingleAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        await harness.AssignAsync(deliveryId, harness.Staff);
        await harness.FailSubscriptionAsync(deliveryId, harness.Staff);
        harness.Db.ChangeTracker.Clear();
        return await harness.Db.RefundReplacementRequests.AsNoTracking()
            .OrderBy(x => x.Id).Select(x => x.PublicId).LastAsync();
    }

    private static DoodhDirectDbContext OpenSharedDb(DeliveryServiceTests.DeliveryHarness harness) =>
        new(new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlite(harness.Db.Database.GetConnectionString())
            .Options);

    private static async Task FailAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);

    private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        var batchId = await harness.RecordMilkBatchAsync();
        await harness.SaveAllocationAsync(deliveryId, batchId, 1m);
        await harness.AssignAsync(deliveryId, harness.Staff);
        return deliveryId;
    }

    private static async Task<Guid> TicketPublicIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static RefundReplacementActor StaffActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true);

    private static Task ChooseReDeliveryAsync(
        RefundReplacementService service,
        DeliveryServiceTests.DeliveryHarness harness,
        Guid requestId) =>
        service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);

    private static async Task SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        // SQLite stores decimals as TEXT, so the CHECK compares
        // lexicographically: 72 vs 800 passes and preserves the numeric
        // invariant (72 <= 800). The refund math still uses the frozen
        // subscription payable (160/2=80 gross).
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            800m,
            "INR",
            $"sweep-sub-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
    }

    private static RefundReplacementService CreateRefundService(
        DeliveryServiceTests.DeliveryHarness harness) =>
        CreateRefundServiceOn(harness.Db, harness);

    private static RefundReplacementService CreateRefundServiceOn(
        DoodhDirectDbContext db, DeliveryServiceTests.DeliveryHarness harness) => new(
            db,
            harness.TimeProvider,
            harness.ConfigurationService,
            new NumberSeriesService(db, harness.TimeProvider),
            harness.ProofStorage,
            new TestNotificationEventWriter(db, harness.TimeProvider));

    private sealed class ScriptedGateway(NotificationChannel channel) : INotificationChannelGateway
    {
        public NotificationChannel Channel { get; } = channel;
        public string ProviderCode => $"TEST_{Channel.ToString().ToUpperInvariant()}";
        public List<NotificationProviderMessage> Messages { get; } = [];

        public Task<NotificationProviderResult> SendAsync(
            NotificationProviderMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            return Task.FromResult(new NotificationProviderResult(
                NotificationAttemptOutcome.Delivered, $"test-{message.DeliveryId:N}"));
        }
    }
}
