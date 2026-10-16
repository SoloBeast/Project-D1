using System.Text.Json;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
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
/// D1-C: expired delivery-fail tickets are automatically resolved to Refund
/// with a frozen subscription-paid calculation, wallet credit, idempotency,
/// notification and audit. No Re-Delivery fulfillment, no repricing, no
/// gateway calls, no Flutter changes.
/// </summary>
public sealed class DeliveryFailTimeoutTests
{
    [Fact]
    public async Task ExpiredTicket_IsResolvedWithFrozenRefundAndWalletCredit()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(1, outcome);
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(CustomerChoice.Refund, ticket.CustomerChoice);
        Assert.NotNull(ticket.CustomerChoiceAt);
        Assert.Equal(RefundReplacementType.Refund, ticket.Type);
        Assert.Equal(RefundReplacementStatus.Completed, ticket.Status);

        // Frozen math: payable 160 / 2 = 80 gross, 10% snapshot deduction = 8,
        // final wallet credit = 72. Subscription financial state untouched.
        var result = Assert.Single(
            await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(requestId, result.PublicId);
        var walletTxn = Assert.Single(await harness.Db.WalletTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(72m, walletTxn.Amount);
        Assert.Equal(WalletTransactionType.RefundCredit, walletTxn.Type);
        Assert.Equal($"delivery-fail:{requestId:N}:refund", walletTxn.IdempotencyKey);
        var wallet = await harness.Db.Wallets.AsNoTracking().SingleAsync();
        Assert.Equal(72m, wallet.Balance);

        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(72m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
        var refund = Assert.Single(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Equal(72m, refund.Amount);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Equal($"delivery-fail:{requestId:N}:refund", refund.IdempotencyKey);

        var subscription = await harness.Db.Subscriptions.AsNoTracking()
            .SingleAsync(x => x.Id == harness.Subscription.Id);
        Assert.Equal(harness.Subscription.PayableAmount, subscription.PayableAmount);
        Assert.Equal(harness.Subscription.ChargesTotal, subscription.ChargesTotal);
        Assert.Equal(1, await harness.Db.SubscriptionDeliveries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ExpiredTicket_WritesAuditsAndCustomerNotificationOnce()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        var audits = await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityId == requestId.ToString())
            .OrderBy(x => x.Id)
            .ToListAsync();
        var timeoutAudit = Assert.Single(audits, x => x.Action == "REFUND_REPLACEMENT.AUTOMATIC_TIMEOUT");
        Assert.Contains((await TicketRequestNumberAsync(harness)).ToString(), timeoutAudit.NewValueJson ?? string.Empty);
        Assert.Contains("AutomaticTimeout", timeoutAudit.NewValueJson ?? string.Empty);
        var refundAudit = Assert.Single(audits, x => x.Action == "REFUND_REPLACEMENT.AUTOMATIC_REFUND");
        Assert.Contains((await TicketRequestNumberAsync(harness)).ToString(), refundAudit.NewValueJson ?? string.Empty);
        Assert.Contains("72", refundAudit.NewValueJson ?? string.Empty);

        var notificationEvent = Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.RefundReplacementAutoRefunded)
            .ToListAsync());
        Assert.Equal($"refund-replacement:{requestId:N}:auto-refund", notificationEvent.EventKey);
        var payload = JsonSerializer.Deserialize<JsonElement>(notificationEvent.PayloadJson);
        var variables = payload.GetProperty("Variables");
        Assert.Equal(requestId.ToString(), variables.GetProperty("requestId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(variables.GetProperty("requestNumber").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(variables.GetProperty("deliveryNumber").GetString()));
        Assert.Equal("72.00", variables.GetProperty("walletCredit").GetString());
        Assert.Equal("8.00", variables.GetProperty("deductionAmount").GetString());
        Assert.Equal($"/refund-replacements/{requestId}", payload.GetProperty("DeepLink").GetString());
        // No internal identifiers leak into the customer payload.
        Assert.DoesNotContain("SubscriptionDeliveryId", notificationEvent.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("storageKey", notificationEvent.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonExpiredTicket_IsIgnored()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(0, outcome);
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Null(ticket.CustomerChoice);
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AlreadyChosenTicket_IsIgnored()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(0, outcome);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RefundReplacementAutoRefunded));
    }

    [Fact]
    public async Task ResolvedTicket_IsIgnored()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        var service = ChoiceService(harness);
        await service.ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);
        await service.ApproveAsync(
            new RefundReplacementActor(harness.Manager.Id, new HashSet<long> { harness.Branch.Id }, true),
            requestId, new ApproveRefundReplacementRequest(null), CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(0, outcome);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task RestartSafe_FreshProcessorResolvesExpiredTicket()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        // Simulate an API restart: a brand-new context and processor with no
        // in-memory state still detect the persisted deadline.
        await using var restartedDb = harness.CreateContext();
        // Hold the connection open so the pragma survives across commands.
        await restartedDb.Database.OpenConnectionAsync();
        await restartedDb.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        var outcome = await TimeoutProcessor(restartedDb, harness).ProcessTicketAsync(
            await TicketPublicIdAsync(harness), CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(72m, outcome.WalletCredit);
        await using var verifyDb = harness.CreateContext();
        var ticket = Assert.Single(await verifyDb.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(RefundReplacementStatus.Completed, ticket.Status);
    }

    [Fact]
    public async Task DeductionSnapshot_SurvivesConfigurationChange()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        await harness.ConfigurationService.UpdateAsync(
            new UpdateRefundReplacementConfigurationRequest(null, null, 25m),
            harness.Manager.Id, null, null, CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessTicketAsync(
            await TicketPublicIdAsync(harness), CancellationToken.None);

        // The ticket snapshot (10%) wins over the current 25% configuration.
        Assert.NotNull(outcome);
        Assert.Equal(10m, outcome.DeductionPercentage);
        Assert.Equal(8m, outcome.DeductionAmount);
        Assert.Equal(72m, outcome.WalletCredit);
    }

    [Fact]
    public async Task BusinessExample_840Payable5Entitlements10Percent_Yields151Point20()
    {
        await using var harness = await CreateHarnessAsync();
        var seeded = await SeedCustomTicketAsync(harness, 1m, 168m, 5, 10m);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessTicketAsync(seeded.RequestPublicId, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(168m, outcome.GrossRefund);
        Assert.Equal(16.80m, outcome.DeductionAmount);
        Assert.Equal(151.20m, outcome.WalletCredit);
    }

    [Fact]
    public async Task GrossRefund_IncludesFrozenTaxAndCharges()
    {
        await using var harness = await CreateHarnessAsync();
        // Product value 160 + frozen charge 40 = payable 200 over 2
        // entitlements: gross must be 100, not the product-only 80.
        var seeded = await SeedCustomTicketAsync(
            harness, 1m, 80m, 2, 10m,
            [new SubscriptionCharge("GST", "GST-18", "GST 18%", 18m, 160m, 40m)]);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessTicketAsync(seeded.RequestPublicId, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(200m, seeded.Payable);
        Assert.Equal(100m, outcome.GrossRefund);
        Assert.Equal(10m, outcome.DeductionAmount);
        Assert.Equal(90m, outcome.WalletCredit);
    }

    [Fact]
    public async Task Rounding_UsesAwayFromZero()
    {
        await using var harness = await CreateHarnessAsync();
        // Payable 33.33 / 2 = 16.665 -> 16.67 AwayFromZero (16.66 under ToEven).
        var seeded = await SeedCustomTicketAsync(harness, 3.333m, 5m, 2, 10m);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessTicketAsync(seeded.RequestPublicId, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(33.33m, seeded.Payable);
        Assert.Equal(16.67m, outcome.GrossRefund);
    }

    [Fact]
    public async Task CumulativeRefunds_NeverExceedFrozenPayable()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var payment = await SeedSubscriptionPaymentAsync(harness);
        // A prior manual refund of 150 against the 160 payable.
        var prior = payment.StartRefund(150m, "setup prior refund", "setup-prior-refund", harness.Customer.Id);
        prior.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(150m);
        var wallet = new Wallet(harness.Customer.Id, "INR");
        harness.Db.Wallets.Add(wallet);
        await harness.Db.SaveChangesAsync();
        wallet.Credit(
            WalletTransactionType.RefundCredit, 150m, "setup-prior-wallet",
            "setup", harness.TimeProvider.Now, payment.Id,
            subscriptionId: harness.Subscription.Id);
        await harness.Db.SaveChangesAsync();
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessTicketAsync(
            await TicketPublicIdAsync(harness), CancellationToken.None);

        // The normal 72 share is capped to the remaining 10; the total never
        // exceeds the frozen 160 payable.
        Assert.NotNull(outcome);
        Assert.Equal(10m, outcome.WalletCredit);
        var refreshed = await harness.Db.Payments.AsNoTracking().SingleAsync(x => x.Id == payment.Id);
        Assert.Equal(160m, refreshed.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, refreshed.Status);
    }

    [Fact]
    public async Task FullyRefundedPayment_LeavesTicketForOperator()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        var payment = await SeedSubscriptionPaymentAsync(harness);
        var prior = payment.StartRefund(
            harness.Subscription.PayableAmount, "setup full refund", "setup-full-refund", harness.Customer.Id);
        prior.Succeed(harness.TimeProvider.Now);
        payment.CompleteRefund(harness.Subscription.PayableAmount);
        await harness.Db.SaveChangesAsync();
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessTicketAsync(
            await TicketPublicIdAsync(harness), CancellationToken.None);

        Assert.Null(outcome);
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(RefundReplacementStatus.AwaitingCustomerChoice, ticket.Status);
        Assert.Null(ticket.CustomerChoice);
    }

    [Fact]
    public async Task Retry_IsIdempotentWithoutDuplicates()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));
        var processor = TimeoutProcessor(harness);

        Assert.NotNull(await processor.ProcessTicketAsync(requestId, CancellationToken.None));
        Assert.Null(await processor.ProcessTicketAsync(requestId, CancellationToken.None));
        Assert.Equal(0, await processor.ProcessExpiredTicketsAsync(CancellationToken.None));

        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RefundReplacementAutoRefunded));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.AUTOMATIC_TIMEOUT"));
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.AUTOMATIC_REFUND"));
    }

    [Fact]
    public async Task TimeoutThenSameChoice_ReturnsResolvedStateWithoutSideEffects()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));
        await TimeoutProcessor(harness).ProcessTicketAsync(requestId, CancellationToken.None);

        var result = await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.Refund), CancellationToken.None);

        Assert.Equal(CustomerChoice.Refund, result.CustomerChoice);
        Assert.Equal(RefundReplacementStatus.Completed, result.Status);
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.AuditLogs.AsNoTracking()
            .CountAsync(x => x.Action == "REFUND_REPLACEMENT.AUTOMATIC_REFUND"));
    }

    [Fact]
    public async Task TimeoutThenDifferentChoice_IsRejected()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        harness.Clock.Advance(TimeSpan.FromHours(25));
        await TimeoutProcessor(harness).ProcessTicketAsync(requestId, CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(() => ChoiceService(harness).ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None));

        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CustomerReDeliveryChoice_WinsOverLaterTimeout()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.FailSubscriptionAsync(await FailTicketAsync(harness), harness.Staff);
        await SeedSubscriptionPaymentAsync(harness);
        var requestId = await TicketPublicIdAsync(harness);
        await ChoiceService(harness).ChooseAsync(
            CustomerActor(harness), requestId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None);
        harness.Clock.Advance(TimeSpan.FromHours(25));

        var outcome = await TimeoutProcessor(harness).ProcessExpiredTicketsAsync(CancellationToken.None);

        Assert.Equal(0, outcome);
        var ticket = Assert.Single(await harness.Db.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(CustomerChoice.ReDelivery, ticket.CustomerChoice);
        Assert.Equal(0, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.Refunds.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task TimeoutRaceOnSqlServer_ExactlyOneWorkerWins()
    {
        await using var harness = await SqlTimeoutHarness.CreateAsync();
        await using var firstDb = harness.CreateContext();
        await using var secondDb = harness.CreateContext();

        var outcomes = await Task.WhenAll(
            AttemptAsync(() => harness.CreateProcessor(firstDb).ProcessTicketAsync(
                harness.RequestPublicId, CancellationToken.None)),
            AttemptAsync(() => harness.CreateProcessor(secondDb).ProcessTicketAsync(
                harness.RequestPublicId, CancellationToken.None)));

        Assert.Single(outcomes, x => x.Result is not null);
        Assert.Single(outcomes, x => x.Error is null && x.Result is null || x.Error is not null);

        await using var verifyDb = harness.CreateContext();
        Assert.Equal(1, await verifyDb.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await verifyDb.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(1, await verifyDb.NotificationEvents.AsNoTracking()
            .CountAsync(x => x.EventType == NotificationEventTypes.RefundReplacementAutoRefunded));
        var ticket = Assert.Single(await verifyDb.RefundReplacementRequests.AsNoTracking().ToListAsync());
        Assert.Equal(CustomerChoice.Refund, ticket.CustomerChoice);
        Assert.Equal(RefundReplacementStatus.Completed, ticket.Status);
    }

    [Fact]
    public async Task CustomerChoiceVsTimeoutRaceOnSqlServer_ExactlyOneWinsWithoutContradiction()
    {
        await using var harness = await SqlTimeoutHarness.CreateAsync();
        await using var choiceDb = harness.CreateContext();
        await using var timeoutDb = harness.CreateContext();
        var actor = new RefundReplacementActor(harness.CustomerId, new HashSet<long>(), false);

        var choiceAttempt = AttemptAsync(() => harness.CreateChoiceService(choiceDb).ChooseAsync(
            actor, harness.RequestPublicId,
            new ChooseRefundReplacementRequest(CustomerChoice.ReDelivery), CancellationToken.None));
        var timeoutAttempt = AttemptAsync(() => harness.CreateProcessor(timeoutDb).ProcessTicketAsync(
            harness.RequestPublicId, CancellationToken.None));
        await Task.WhenAll(choiceAttempt, timeoutAttempt);

        await using var verifyDb = harness.CreateContext();
        var ticket = Assert.Single(await verifyDb.RefundReplacementRequests.AsNoTracking().ToListAsync());
        var walletCount = await verifyDb.WalletTransactions.AsNoTracking().CountAsync();
        if (ticket.CustomerChoice == CustomerChoice.ReDelivery)
        {
            Assert.Equal(0, walletCount);
            Assert.Equal(0, await verifyDb.Refunds.AsNoTracking().CountAsync());
        }
        else
        {
            Assert.Equal(CustomerChoice.Refund, ticket.CustomerChoice);
            Assert.Equal(1, walletCount);
            Assert.Equal(1, await verifyDb.Refunds.AsNoTracking().CountAsync());
        }
    }

    /// <summary>
    /// SQLite stores decimals as TEXT, so range CHECKs (for example
    /// <c>RefundedAmount &lt;= Amount</c>) misbehave there: "72" sorts after
    /// "160". The pragma matches the existing PaymentHarness precedent; the
    /// SQL Server tests below enforce the real constraints numerically.
    /// </summary>
    private static async Task<DeliveryServiceTests.DeliveryHarness> CreateHarnessAsync()
    {
        var harness = await DeliveryServiceTests.DeliveryHarness.CreateAsync();
        await harness.Db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        return harness;
    }

    private static async Task<(TResult? Result, Exception? Error)> AttemptAsync<TResult>(
        Func<Task<TResult?>> operation)
    {
        try
        {
            return (await operation(), null);
        }
        catch (Exception exception)
        {
            return (default, exception);
        }
    }

    private static RefundReplacementActor CustomerActor(DeliveryServiceTests.DeliveryHarness harness) =>
        new(harness.Customer.Id, new HashSet<long>(), false);

    private static async Task<Guid> FailTicketAsync(DeliveryServiceTests.DeliveryHarness harness)
    {
        var deliveryId = await harness.MaterializeSubscriptionAsync();
        await harness.AssignAsync(deliveryId, harness.Staff);
        return deliveryId;
    }

    private static async Task<Guid> TicketPublicIdAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.PublicId).SingleAsync();

    private static async Task<string> TicketRequestNumberAsync(DeliveryServiceTests.DeliveryHarness harness) =>
        await harness.Db.RefundReplacementRequests.AsNoTracking().Select(x => x.RequestNumber).SingleAsync();

    private static DeliveryFailTimeoutProcessor TimeoutProcessor(
        DeliveryServiceTests.DeliveryHarness harness) =>
        TimeoutProcessor(harness.Db, harness);

    private static DeliveryFailTimeoutProcessor TimeoutProcessor(
        DoodhDirectDbContext db,
        DeliveryServiceTests.DeliveryHarness harness) => new(
        db,
        harness.TimeProvider,
        new TestNotificationEventWriter(db, harness.TimeProvider),
        Options.Create(new DeliveryFailTimeoutOptions()),
        Options.Create(new PaymentOptions { Currency = "INR" }),
        NullLogger<DeliveryFailTimeoutProcessor>.Instance);

    private static RefundReplacementService ChoiceService(DeliveryServiceTests.DeliveryHarness harness) => new(
        harness.Db,
        harness.TimeProvider,
        harness.ConfigurationService,
        new NumberSeriesService(harness.Db, harness.TimeProvider),
        harness.ProofStorage,
        new TestNotificationEventWriter(harness.Db, harness.TimeProvider));

    /// <summary>
    /// Seeds a successful wallet payment for the harness subscription so the
    /// timeout processor has a Payment/Refund aggregate to refund against.
    /// </summary>
    private static async Task<Payment> SeedSubscriptionPaymentAsync(
        DeliveryServiceTests.DeliveryHarness harness)
    {
        var payment = Payment.CreateForSubscription(
            harness.Subscription.Id,
            harness.Customer.Id,
            PaymentMethod.Wallet,
            harness.Subscription.PayableAmount,
            "INR",
            $"timeout-sub-pay-{Guid.NewGuid():N}",
            harness.TimeProvider.Now.AddMinutes(15));
        payment.Succeed(null, "WALLET", harness.TimeProvider.Now);
        harness.Db.Payments.Add(payment);
        await harness.Db.SaveChangesAsync();
        return payment;
    }

    private sealed record CustomTicketSeed(Guid RequestPublicId, decimal Payable);

    /// <summary>
    /// Seeds a minimal subscription stack (subscription, occurrence, delivery,
    /// failure ticket, successful payment) with exact financial values to prove
    /// the frozen refund math independently of the shared harness fixtures.
    /// </summary>
    private static async Task<CustomTicketSeed> SeedCustomTicketAsync(
        DeliveryServiceTests.DeliveryHarness harness,
        decimal quantity,
        decimal unitPrice,
        int totalEntitlement,
        decimal deductionPercentage,
        IReadOnlyCollection<SubscriptionCharge>? charges = null)
    {
        var db = harness.Db;
        var now = harness.TimeProvider.Now;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var branch = harness.Branch;
        var customer = harness.Customer;
        var staff = harness.Staff;

        var category = await db.ProductCategories.SingleAsync();
        var product = new Product(
            category.Id, $"TMO-{suffix}", "Timeout Milk", null, "litre", unitPrice);
        var address = new CustomerAddress(
            customer.Id, $"Tmo {suffix}", "9 Timeout Road", "Central", "Bengaluru", "Karnataka",
            "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
        db.AddRange(product, address);
        await db.SaveChangesAsync();

        var today = harness.TimeProvider.Today;
        var subscription = new Subscription(
            customer.Id, product.Id, address.Id, branch.Id, $"timeout-sub-{suffix}",
            today, today.AddDays(7), quantity, unitPrice, totalEntitlement,
            product.Sku, product.Name, product.UnitOfMeasure,
            branch.Code, branch.Name, "9 Timeout Road, Bengaluru");
        if (charges is not null)
        {
            subscription.AddCharges(charges);
        }

        subscription.AddDelivery(today);
        subscription.Activate(now);
        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync();
        var occurrence = subscription.Deliveries.Single();

        var delivery = Delivery.ForSubscriptionOccurrence(
            occurrence.Id, customer.Id, branch.Id, today,
            $"SUB-TMO-{suffix}-{today:yyyyMMdd}", "Customer", "9999999999",
            "9 Timeout Road, Bengaluru", null, 12.9716m, 77.5946m);
        delivery.AssignDeliveryNumber($"DLV/TMO/{suffix}");
        delivery.Assign(staff.Id, staff.Id, now, null);
        db.Deliveries.Add(delivery);
        await db.SaveChangesAsync();

        delivery.Fail(
            staff.Id, now, DeliveryFailureReasons.CustomerNotAvailable, "No response", null, null);
        var ticket = new RefundReplacementRequest(
            null, delivery.Id, customer.Id, branch.Id, null,
            RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
            DeliveryFailureReasons.CustomerNotAvailable, null, now, now.AddHours(24),
            occurrence.Id, now, 24, deductionPercentage,
            DeliveryFailureReasons.CustomerNotAvailable,
            RefundReplacementStatus.AwaitingCustomerChoice);
        ticket.AssignRequestNumber($"RR/TMO/{suffix}");
        db.RefundReplacementRequests.Add(ticket);

        var payment = Payment.CreateForSubscription(
            subscription.Id, customer.Id, PaymentMethod.Wallet,
            subscription.PayableAmount, "INR", $"timeout-pay-{suffix}", now.AddMinutes(15));
        payment.Succeed(null, "WALLET", now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new CustomTicketSeed(ticket.PublicId, subscription.PayableAmount);
    }

    /// <summary>
    /// Throwaway SQL Server database so worker races exercise real row-version
    /// locking and unique-key contention instead of pretending SQLite proves
    /// multi-instance behavior.
    /// </summary>
    private sealed class SqlTimeoutHarness : IAsyncDisposable
    {
        private const string SqlServerInstance = @".\SQLEXPRESS";
        private readonly string connectionString;

        private SqlTimeoutHarness(string connectionString, Guid requestPublicId, long customerId)
        {
            this.connectionString = connectionString;
            RequestPublicId = requestPublicId;
            CustomerId = customerId;
        }

        public Guid RequestPublicId { get; }
        public long CustomerId { get; }

        public DoodhDirectDbContext CreateContext()
        {
            // Mirrors production resiliency (DependencyInjection: retry 5x) so a
            // deadlock victim retries into the resolved-state path.
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

        public DeliveryFailTimeoutProcessor CreateProcessor(DoodhDirectDbContext db)
        {
            var clock = new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified));
            clock.Advance(TimeSpan.FromHours(25));
            var time = new TestIndiaTimeProvider(clock);
            return new DeliveryFailTimeoutProcessor(
                db,
                time,
                new TestNotificationEventWriter(db, time),
                Options.Create(new DeliveryFailTimeoutOptions()),
                Options.Create(new PaymentOptions { Currency = "INR" }),
                NullLogger<DeliveryFailTimeoutProcessor>.Instance);
        }

        public RefundReplacementService CreateChoiceService(DoodhDirectDbContext db)
        {
            // The customer chooses just before the deadline (valid choice) while
            // the timeout worker observes an expired deadline: the genuine race.
            var clock = new TestClock(new DateTime(2026, 8, 17, 9, 30, 0, DateTimeKind.Unspecified));
            var time = new TestIndiaTimeProvider(clock);
            return new RefundReplacementService(
                db,
                time,
                new Infrastructure.RefundReplacement.RefundReplacementConfigurationService(db, time),
                new NumberSeriesService(db, time),
                new ThrowawayMediaStorage(),
                new TestNotificationEventWriter(db, time));
        }

        public static async Task<SqlTimeoutHarness> CreateAsync()
        {
            var databaseName = $"DoodhDirect_Test_Timeout_{Guid.NewGuid():N}";
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
            staff.SetProfile("Delivery Staff");
            staff.SetContact("9000000001", null);
            db.AddRange(branch, customer, staff);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            db.AddRange(product, address);
            await db.SaveChangesAsync();

            var subscription = new Subscription(
                customer.Id, product.Id, address.Id, branch.Id, "timeout-sql-1",
                DateOnly.FromDateTime(now), DateOnly.FromDateTime(now).AddDays(7),
                1m, 80m, 2, product.Sku, product.Name, product.UnitOfMeasure,
                branch.Code, branch.Name, "1 Main Road, Bengaluru");
            subscription.AddDelivery(DateOnly.FromDateTime(now));
            subscription.Activate(now);
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();
            var occurrence = subscription.Deliveries.Single();

            var payment = Payment.CreateForSubscription(
                subscription.Id, customer.Id, PaymentMethod.Wallet,
                subscription.PayableAmount, "INR", "timeout-sql-pay-1", now.AddMinutes(15));
            payment.Succeed(null, "WALLET", now);
            db.Payments.Add(payment);
            await db.SaveChangesAsync();

            var delivery = Delivery.ForSubscriptionOccurrence(
                occurrence.Id, customer.Id, branch.Id, DateOnly.FromDateTime(now),
                $"SUB-{subscription.PublicId:N}-{DateOnly.FromDateTime(now):yyyyMMdd}",
                "Customer", "9999999999", "1 Main Road, Bengaluru", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber("DLV/MAIN/000001");
            delivery.Assign(staff.Id, staff.Id, now, null);
            db.Deliveries.Add(delivery);
            await db.SaveChangesAsync();

            delivery.Fail(
                staff.Id, now, DeliveryFailureReasons.CustomerNotAvailable, "No response", null, null);
            var ticket = new RefundReplacementRequest(
                null, delivery.Id, customer.Id, branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                DeliveryFailureReasons.CustomerNotAvailable, null, now, now.AddHours(24),
                occurrence.Id, now, 24, 10m, DeliveryFailureReasons.CustomerNotAvailable,
                RefundReplacementStatus.AwaitingCustomerChoice);
            ticket.AssignRequestNumber("RR/TEST/000001");
            db.RefundReplacementRequests.Add(ticket);
            await db.SaveChangesAsync();

            var harness = new SqlTimeoutHarness(builder.ConnectionString, ticket.PublicId, customer.Id);
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
                throw new NotSupportedException("Timeout processing records no media.");

            public Task<StoredMediaContent> OpenReadAsync(
                string storageKey, CancellationToken cancellationToken) =>
                throw new NotSupportedException("Timeout processing records no media.");

            public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
                Task.CompletedTask;
        }
    }
}
