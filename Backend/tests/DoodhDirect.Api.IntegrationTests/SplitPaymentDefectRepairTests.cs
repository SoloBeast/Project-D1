using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Notifications;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Regression coverage for the three wallet-first split-payment repair findings:
/// (A) wallet-only subscription completion must persist activation and outcome
/// events transaction-safely; (B) rejected initial payment plans must not leave
/// unreachable pending subscriptions; (C) plan retry must report accurate
/// per-outcome states instead of a misleading "completed payment" message.
/// Disposable in-memory databases with fake gateways only; no real orders,
/// captures, or financial operations.
/// </summary>
public sealed class SplitPaymentDefectRepairTests
{
    // ------------------------------------------------------------------
    // Defect A: wallet-only subscription activation is persisted.
    // ------------------------------------------------------------------

    [Fact]
    public async Task WalletOnlySubscriptionPlan_PersistsActivationReconciliationAndExactlyOnceEvents()
    {
        await using var harness = await DefectHarness.CreateAsync();
        var subscription = await harness.CreateSubscriptionAsync(quantity: 1.25m, entitlement: 1);
        await harness.SeedWalletAsync(100m, "defect-a-funding-1");

        var plan = await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-a-plan-1", CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, plan.Payment.Status);
        Assert.Equal(PaymentMethod.Wallet, plan.Payment.Method);
        Assert.Equal(100m, plan.PayableAmount);
        Assert.Equal(100m, plan.WalletApplied);
        Assert.Equal(0m, plan.RazorpayAmount);

        // A fresh context — what the subsequent API GET observes — must agree
        // with the creation response: Active, timestamped, reconciled.
        await using var fresh = harness.FreshDb();
        var persisted = await fresh.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, persisted.Status);
        Assert.Equal(harness.TimeProvider.Now, persisted.ActivatedAt);

        var payment = await fresh.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal(PaymentMethod.Wallet, payment.Method);
        Assert.Equal(100m, payment.Amount);
        Assert.Equal(subscription.Id, payment.SubscriptionId);

        var debit = await fresh.WalletTransactions
            .AsNoTracking()
            .SingleAsync(x => x.Type == WalletTransactionType.SubscriptionDebit);
        Assert.Equal(-100m, debit.Amount);
        Assert.Equal(payment.Id, debit.PaymentId);
        Assert.Equal(subscription.Id, debit.SubscriptionId);
        Assert.Equal(0m, (await fresh.Wallets.AsNoTracking().SingleAsync()).Balance);

        var events = await fresh.NotificationEvents.AsNoTracking().ToListAsync();
        Assert.Single(events, x => x.EventType == NotificationEventTypes.PaymentSucceeded);
        Assert.Single(events, x => x.EventType == NotificationEventTypes.SubscriptionActivated);
    }

    [Fact]
    public async Task WalletOnlySubscriptionPlan_ReplayDoesNotDuplicateDebitActivationOrEvents()
    {
        await using var harness = await DefectHarness.CreateAsync();
        var subscription = await harness.CreateSubscriptionAsync(quantity: 1.25m, entitlement: 1);
        await harness.SeedWalletAsync(100m, "defect-a-funding-2");

        var first = await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-a-replay-1", CancellationToken.None);
        var replay = await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-a-replay-1", CancellationToken.None);

        Assert.Equal(first.PaymentGroupId, replay.PaymentGroupId);

        await using var fresh = harness.FreshDb();
        Assert.Equal(1, await fresh.Payments.CountAsync());
        Assert.Single(await fresh.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.SubscriptionDebit).ToListAsync());
        Assert.Equal(SubscriptionStatus.Active, (await fresh.Subscriptions.AsNoTracking().SingleAsync()).Status);
        var events = await fresh.NotificationEvents.AsNoTracking().ToListAsync();
        Assert.Single(events, x => x.EventType == NotificationEventTypes.PaymentSucceeded);
        Assert.Single(events, x => x.EventType == NotificationEventTypes.SubscriptionActivated);
        Assert.Equal(0m, (await fresh.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    // ------------------------------------------------------------------
    // Defect B: rejected plans leave no unreachable subscriptions.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Create_WithSubOneRemainder_RejectsBeforePersistingAnything()
    {
        await using var harness = await DefectHarness.CreateAsync();
        var services = harness.SubscriptionServices(rejectPlans: false);
        // Base payable is 1.25 x 80 x 2 = 200 (no charge masters seeded),
        // so 199.99 leaves a 0.01 remainder below the gateway minimum.
        await harness.SeedWalletAsync(199.99m, "defect-b-funding-1");

        var request = new CreateSubscriptionRequest(
            harness.Product.PublicId,
            harness.Address.PublicId,
            1.25m,
            new DateOnly(2026, 8, 17),
            [DayOfWeek.Monday],
            2,
            PaymentMethod.Razorpay,
            UseWalletBalance: true);

        var exception = await Assert.ThrowsAsync<WalletRemainderBelowGatewayMinimumException>(() =>
            services.Service.CreateAsync(harness.Customer.Id, request, "defect-b-1", CancellationToken.None));
        Assert.Equal("WALLET_SPLIT_REMAINDER_BELOW_MINIMUM", exception.Code);
        Assert.Equal(0.01m, exception.RazorpayRemainder);

        // Nothing persisted: no orphan subscription, no payment, wallet intact,
        // no creation/pending notifications, payment service never invoked.
        Assert.Empty(await harness.Db.Subscriptions.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Payments.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.NotificationEvents.AsNoTracking().ToListAsync());
        Assert.Equal(199.99m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Empty(services.Payments.PlanCalls);
        Assert.Empty(services.Payments.CreateCalls);
    }

    [Fact]
    public async Task Create_ConcurrentRemainderFailure_ReturnsRecoverableIdWithoutFalseActivation()
    {
        await using var harness = await DefectHarness.CreateAsync();
        // Full wallet cover passes the deterministic pre-check; the stub then
        // simulates the authoritative plan revalidation failing on a
        // concurrent balance change.
        var services = harness.SubscriptionServices(rejectPlans: true);
        await harness.SeedWalletAsync(200m, "defect-b-funding-2");

        var request = new CreateSubscriptionRequest(
            harness.Product.PublicId,
            harness.Address.PublicId,
            1.25m,
            new DateOnly(2026, 8, 17),
            [DayOfWeek.Monday],
            2,
            PaymentMethod.Razorpay,
            UseWalletBalance: true);

        var exception = await Assert.ThrowsAsync<SubscriptionInitialPaymentRejectedException>(() =>
            services.Service.CreateAsync(harness.Customer.Id, request, "defect-b-2", CancellationToken.None));
        Assert.Equal("WALLET_SPLIT_REMAINDER_BELOW_MINIMUM", exception.Code);
        Assert.NotEqual(Guid.Empty, exception.SubscriptionId);

        await using var fresh = harness.FreshDb();
        var persisted = await fresh.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(exception.SubscriptionId, persisted.PublicId);
        Assert.Equal(SubscriptionStatus.PaymentPending, persisted.Status);
        Assert.Empty(await fresh.Payments.AsNoTracking().ToListAsync());
        Assert.Equal(200m, (await fresh.Wallets.AsNoTracking().SingleAsync()).Balance);

        // Creation/pending notifications exist; nothing implies activation or
        // payment success.
        var eventTypes = (await fresh.NotificationEvents.AsNoTracking().ToListAsync())
            .Select(x => x.EventType).ToList();
        Assert.Equal(2, eventTypes.Count);
        Assert.Contains(NotificationEventTypes.SubscriptionCreated, eventTypes);
        Assert.Contains(NotificationEventTypes.SubscriptionPaymentPending, eventTypes);
        Assert.DoesNotContain(NotificationEventTypes.SubscriptionActivated, eventTypes);
        Assert.DoesNotContain(NotificationEventTypes.PaymentSucceeded, eventTypes);
    }

    [Fact]
    public async Task Retry_RecoveredSubscription_DoesNotDuplicateSubscriptionEntitlementOrNotifications()
    {
        await using var harness = await DefectHarness.CreateAsync();
        var services = harness.SubscriptionServices(rejectPlans: true);
        await harness.SeedWalletAsync(200m, "defect-b-funding-3");

        var request = new CreateSubscriptionRequest(
            harness.Product.PublicId,
            harness.Address.PublicId,
            1.25m,
            new DateOnly(2026, 8, 17),
            [DayOfWeek.Monday],
            2,
            PaymentMethod.Razorpay,
            UseWalletBalance: true);

        var rejected = await Assert.ThrowsAsync<SubscriptionInitialPaymentRejectedException>(() =>
            services.Service.CreateAsync(harness.Customer.Id, request, "defect-b-3", CancellationToken.None));
        var deliveriesBefore = await harness.Db.SubscriptionDeliveries.CountAsync();

        // The recovered id retries on the SAME subscription: no duplicate
        // subscription, entitlement, delivery, or creation notification.
        var retried = await services.Service.RetryPaymentAsync(
            harness.Customer.Id,
            rejected.SubscriptionId,
            new RetrySubscriptionPaymentRequest(PaymentMethod.Razorpay),
            "defect-b-3-retry-1",
            CancellationToken.None);

        Assert.Equal(rejected.SubscriptionId, retried.Subscription.PublicId);
        Assert.Equal(SubscriptionStatus.PaymentPending, retried.Subscription.Status);
        Assert.Single(await harness.Db.Subscriptions.AsNoTracking().ToListAsync());
        Assert.Equal(deliveriesBefore, await harness.Db.SubscriptionDeliveries.CountAsync());
        Assert.Equal(2, await harness.Db.NotificationEvents.CountAsync());
        Assert.Single(services.Payments.RetryCalls);
    }

    // ------------------------------------------------------------------
    // Defect C: accurate retry states, no duplicate money movement.
    // ------------------------------------------------------------------

    [Fact]
    public async Task SecondAttempt_WhileGatewayLegLive_ReportsAmbiguousNotCompleted()
    {
        await using var harness = await DefectHarness.CreateAsync();
        var subscription = await harness.CreateSubscriptionAsync(quantity: 3.125m, entitlement: 1);
        await harness.SeedWalletAsync(100m, "defect-c-funding-1");

        var plan = await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-c-live-1", CancellationToken.None);
        Assert.Equal(2, plan.Components.Count);

        var exception = await Assert.ThrowsAsync<SplitPlanOutcomeAmbiguousException>(() =>
            harness.PaymentService.CreateSubscriptionPlanAsync(
                harness.Customer.Id, subscription.Id, useWalletBalance: true,
                "defect-c-live-2", CancellationToken.None));
        Assert.Equal("SPLIT_PLAN_OUTCOME_AMBIGUOUS", exception.Code);
        Assert.DoesNotContain("completed payment", exception.Message, StringComparison.OrdinalIgnoreCase);

        // No second gateway order, no second debit, held funds untouched.
        Assert.Equal(1, harness.Gateway.CreateOrderCalls);
        Assert.Equal(2, await harness.Db.Payments.CountAsync());
        Assert.Single(await harness.Db.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.SubscriptionDebit).ToListAsync());
        Assert.Equal(0m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task SecondAttempt_AfterDefinitiveGatewayFailure_ReportsInProgressWhileFundsHeld()
    {
        await using var harness = await DefectHarness.CreateAsync();
        harness.Gateway.OrderMode = DefectTestGateway.Mode.FailedTerminal;
        var subscription = await harness.CreateSubscriptionAsync(quantity: 3.125m, entitlement: 1);
        await harness.SeedWalletAsync(100m, "defect-c-funding-2");

        await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-c-held-1", CancellationToken.None);

        // The retry endpoint (not a fresh plan) reports the held-funds state
        // accurately: the purchase is in progress, not completed.
        var exception = await Assert.ThrowsAsync<SplitPlanAttemptInProgressException>(() =>
            harness.PaymentService.RetrySubscriptionAsync(
                harness.Customer.Id, subscription.PublicId, PaymentMethod.Wallet,
                "defect-c-held-2", CancellationToken.None));
        Assert.Equal("SPLIT_PLAN_ATTEMPT_IN_PROGRESS", exception.Code);
        Assert.DoesNotContain("completed payment", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, harness.Gateway.CreateOrderCalls);
        Assert.Equal(2, await harness.Db.Payments.CountAsync());
        Assert.Single(await harness.Db.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.SubscriptionDebit).ToListAsync());
        Assert.Equal(0m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Retry_CompletedPurchase_ReportsAccurateStateWithoutDuplicates()
    {
        await using var harness = await DefectHarness.CreateAsync();
        var subscription = await harness.CreateSubscriptionAsync(quantity: 1.25m, entitlement: 1);
        await harness.SeedWalletAsync(100m, "defect-c-funding-3");

        await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-c-done-1", CancellationToken.None);
        var eventsBefore = await harness.Db.NotificationEvents.CountAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.RetrySubscriptionAsync(
                harness.Customer.Id, subscription.PublicId, PaymentMethod.Wallet,
                "defect-c-done-retry-1", CancellationToken.None));
        Assert.Contains("cannot retry payment", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await harness.Db.Payments.CountAsync());
        Assert.Single(await harness.Db.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.SubscriptionDebit).ToListAsync());
        Assert.Equal(eventsBefore, await harness.Db.NotificationEvents.CountAsync());
        Assert.Equal(0m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task DefinitiveGatewayFailure_ReleasesWalletOnce_FreshPlanProceeds()
    {
        await using var harness = await DefectHarness.CreateAsync();
        harness.Gateway.OrderMode = DefectTestGateway.Mode.FailedTerminal;
        var subscription = await harness.CreateSubscriptionAsync(quantity: 3.125m, entitlement: 1);
        await harness.SeedWalletAsync(100m, "defect-c-funding-4");

        var doomed = await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-c-doomed-1", CancellationToken.None);
        var razorpayLeg = doomed.Components.Single(x => x.Method == PaymentMethod.Razorpay);

        var first = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, razorpayLeg.PublicId, CancellationToken.None);
        var second = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, razorpayLeg.PublicId, CancellationToken.None);
        Assert.Equal(PaymentStatus.Cancelled, first.Status);
        Assert.Equal(PaymentStatus.Cancelled, second.Status);

        // Wallet restored exactly once; the abandoned group no longer blocks.
        Assert.Equal(100m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.SubscriptionDebit).ToListAsync());

        var fresh = await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: false,
            "defect-c-fresh-1", CancellationToken.None);
        Assert.Equal(250m, fresh.RazorpayAmount);
        Assert.NotEqual(doomed.PaymentGroupId, fresh.PaymentGroupId);

        // Still exactly one debit: the fresh full-Razorpay plan debited nothing.
        Assert.Single(await harness.Db.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.SubscriptionDebit).ToListAsync());
    }

    [Fact]
    public async Task GatewayTransportFailure_BlocksRetryAndHoldsFunds()
    {
        await using var harness = await DefectHarness.CreateAsync();
        var subscription = await harness.CreateSubscriptionAsync(quantity: 3.125m, entitlement: 1);
        await harness.SeedWalletAsync(100m, "defect-c-funding-5");

        var plan = await harness.PaymentService.CreateSubscriptionPlanAsync(
            harness.Customer.Id, subscription.Id, useWalletBalance: true,
            "defect-c-transport-1", CancellationToken.None);
        Assert.Equal(2, plan.Components.Count);

        // The gateway becomes unreachable during reconciliation: the live leg
        // cannot be proven uncaptured, so retry is blocked and funds are held.
        harness.Gateway.OrderMode = DefectTestGateway.Mode.ThrowTransport;

        var exception = await Assert.ThrowsAsync<SplitPlanOutcomeAmbiguousException>(() =>
            harness.PaymentService.CreateSubscriptionPlanAsync(
                harness.Customer.Id, subscription.Id, useWalletBalance: true,
                "defect-c-transport-2", CancellationToken.None));
        Assert.Equal("SPLIT_PLAN_OUTCOME_AMBIGUOUS", exception.Code);

        Assert.Equal(2, await harness.Db.Payments.CountAsync());
        Assert.Equal(1, harness.Gateway.CreateOrderCalls);
        Assert.Equal(0m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    // ------------------------------------------------------------------
    // Harness: disposable SQLite + fake Razorpay gateway + real services.
    // ------------------------------------------------------------------

    private sealed class DefectHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly DbContextOptions<DoodhDirectDbContext> options;

        private DefectHarness(
            SqliteConnection connection,
            DbContextOptions<DoodhDirectDbContext> options,
            DoodhDirectDbContext db,
            User customer,
            Product product,
            CustomerAddress address,
            Branch branch,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            PaymentService paymentService,
            WalletService walletService,
            DefectTestGateway gateway)
        {
            this.connection = connection;
            this.options = options;
            Db = db;
            Customer = customer;
            Product = product;
            Address = address;
            Branch = branch;
            Clock = clock;
            TimeProvider = timeProvider;
            PaymentService = paymentService;
            WalletService = walletService;
            Gateway = gateway;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public Product Product { get; }
        public CustomerAddress Address { get; }
        public Branch Branch { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public PaymentService PaymentService { get; }
        public WalletService WalletService { get; }
        public DefectTestGateway Gateway { get; }

        public static async Task<DefectHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = new User(UserType.Customer);
            customer.SetProfile("Defect Customer");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();

            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            var branch = new Branch(
                "MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Defect Customer", "9999999999", 12.9716m, 77.5946m);
            db.AddRange(product, branch, address);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            await db.SaveChangesAsync();
            await new NumberSeriesSeedService(db)
                .EnsureScopedSubscriptionSeriesAsync(branch.Code, CancellationToken.None);

            var clock = new TestClock(new DateTime(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc));
            var paymentOptions = Options.Create(new PaymentOptions
            {
                Provider = "Razorpay",
                Currency = "INR",
                PaymentExpiryMinutes = 15,
                RazorpayKeyId = "rzp_test_public",
                RazorpayKeySecret = "api-secret",
                MockSigningSecret = "unused"
            });
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var indiaTime = new TestIndiaTimeProvider(clock);
            var gateway = new DefectTestGateway(paymentOptions);
            var walletService = new WalletService(db, indiaTime, paymentOptions, notificationEventWriter);
            var paymentService = new PaymentService(
                db, gateway, walletService, indiaTime, paymentOptions, notificationEventWriter);
            return new DefectHarness(
                connection, options, db, customer, product, address, branch,
                clock, indiaTime, paymentService, walletService, gateway);
        }

        public DoodhDirectDbContext FreshDb() => new(options);

        public async Task<Subscription> CreateSubscriptionAsync(decimal quantity, int entitlement)
        {
            var subscription = new Subscription(
                Customer.Id,
                Product.Id,
                Address.Id,
                Branch.Id,
                $"defect-sub-{Guid.NewGuid():N}",
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 31),
                quantity: quantity,
                unitPrice: 80m,
                totalEntitlement: entitlement,
                Product.Sku,
                Product.Name,
                Product.UnitOfMeasure,
                Branch.Code,
                Branch.Name,
                "Home, 1 Main Road, Central, Bengaluru, Karnataka 560001");
            Db.Subscriptions.Add(subscription);
            await Db.SaveChangesAsync();
            return subscription;
        }

        public async Task SeedWalletAsync(decimal amount, string idempotencyKey)
        {
            var wallet = await Db.Wallets.SingleOrDefaultAsync(x => x.CustomerId == Customer.Id);
            if (wallet is null)
            {
                wallet = new Wallet(Customer.Id, "INR");
                Db.Wallets.Add(wallet);
            }

            wallet.Credit(WalletTransactionType.TopUp, amount, idempotencyKey, "Wallet top-up", TimeProvider.Now);
            await Db.SaveChangesAsync();
        }

        public (SubscriptionService Service, StubSubscriptionPaymentService Payments) SubscriptionServices(
            bool rejectPlans)
        {
            var payments = new StubSubscriptionPaymentService(Db, Clock, rejectPlans);
            var service = new SubscriptionService(
                Db,
                new FixedBranchAllocationService(Branch),
                payments,
                TimeProvider,
                new TestNotificationEventWriter(Db, Clock),
                new NumberSeriesService(Db, TimeProvider));
            return (service, payments);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedBranchAllocationService(Branch branch) : IBranchAllocationService
    {
        public Task<BranchAllocationResult> AllocateAsync(
            decimal latitude,
            decimal longitude,
            IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
            CancellationToken cancellationToken) => Task.FromResult(
            new BranchAllocationResult(branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
    }

    /// <summary>
    /// Stub payment service for subscription-creation tests. With
    /// <c>rejectPlans</c> it throws the sub-₹1 remainder rejection from the
    /// plan call, simulating a concurrent balance change after the
    /// deterministic pre-creation guard passed.
    /// </summary>
    private sealed class StubSubscriptionPaymentService(
        DoodhDirectDbContext db,
        TestClock clock,
        bool rejectPlans) : IPaymentService
    {
        public List<(long CustomerId, long SubscriptionId, string IdempotencyKey)> PlanCalls { get; } = [];
        public List<(long CustomerId, long SubscriptionId, string IdempotencyKey)> CreateCalls { get; } = [];
        public List<(long CustomerId, Guid SubscriptionId, string IdempotencyKey)> RetryCalls { get; } = [];

        public Task<PaymentResult> CreateForSubscriptionAsync(
            long customerId, long subscriptionId, PaymentMethod method,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            CreateCalls.Add((customerId, subscriptionId, idempotencyKey));
            throw new NotSupportedException("Creation tests use Razorpay wallet-split plans.");
        }

        public Task<PaymentResult> RetrySubscriptionAsync(
            long customerId, Guid subscriptionId, PaymentMethod method,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            RetryCalls.Add((customerId, subscriptionId, idempotencyKey));
            return Task.FromResult(SuccessResult(subscriptionId, method));
        }

        public Task<PaymentPlanResult> CreateOrderPlanAsync(
            long customerId, Guid orderId, bool useWalletBalance,
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentPlanResult> CreateSubscriptionPlanAsync(
            long customerId, long subscriptionId, bool useWalletBalance,
            string idempotencyKey, CancellationToken cancellationToken,
            bool expirePriorAttempts = false)
        {
            PlanCalls.Add((customerId, subscriptionId, idempotencyKey));
            if (rejectPlans)
            {
                throw new WalletRemainderBelowGatewayMinimumException(199.99m, 0.01m, 200m, 1.00m, "INR");
            }

            throw new NotSupportedException("Pre-creation guard tests never reach the plan call.");
        }

        public Task<PaymentResult> CreateWalletTopUpAsync(
            long customerId, decimal amount, string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CompleteDevelopmentAsync(
            long customerId, Guid paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CancelAsync(
            long customerId, Guid paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CreateAsync(
            long customerId, CreatePaymentRequest request, string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> VerifyAsync(
            long customerId, VerifyPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> GetAsync(
            long userId, Guid paymentId, bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentReconciliationResult> ReconcileAsync(
            long requestedByUserId, Guid paymentId, bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RefundResult> RefundAsync(
            long requestedByUserId, Guid paymentId, RefundPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ProcessWebhookAsync(
            byte[] payload, string signature, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private PaymentResult SuccessResult(Guid subscriptionId, PaymentMethod method)
        {
            var subscription = db.Subscriptions.AsQueryable()
                .Single(x => x.PublicId == subscriptionId);
            return new PaymentResult(
                Guid.NewGuid(), null, null, null, method, "Razorpay", PaymentStatus.Pending,
                subscription.PayableAmount, 0m, "INR", $"order_{subscription.PublicId:N}",
                null, "rzp_test", null, null, clock.UtcNow.AddMinutes(15), null,
                clock.UtcNow, subscription.PublicId);
        }
    }

    private sealed class DefectTestGateway(IOptions<PaymentOptions> options) : IPaymentGateway
    {
        public enum Mode { Captured, FailedTerminal, ThrowTransport }

        private readonly Dictionary<Guid, GatewayOrderRequest> orders = [];

        public Mode OrderMode { get; set; } = Mode.Captured;
        public int CreateOrderCalls { get; private set; }
        public string ProviderName => "Razorpay";
        public bool IsLive => true;

        public Task<string?> GetPublicKeyIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(options.Value.RazorpayKeyId);

        public Task<GatewayOrderResult> CreateOrderAsync(
            GatewayOrderRequest request, CancellationToken cancellationToken)
        {
            CreateOrderCalls++;
            orders[request.PaymentId] = request;
            return Task.FromResult(new GatewayOrderResult(
                $"order_test_{request.PaymentId:N}", "created",
                request.AmountMinor, request.Currency));
        }

        public Task<bool> VerifyPaymentSignatureAsync(
            string gatewayOrderId, string gatewayPaymentId,
            string signature, CancellationToken cancellationToken) =>
            Task.FromResult(
                signature == "test_verified" &&
                TryGetPaymentId(gatewayPaymentId, out var paymentId) &&
                gatewayOrderId == $"order_test_{paymentId:N}" &&
                orders.ContainsKey(paymentId));

        public Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(
            string gatewayPaymentId, CancellationToken cancellationToken)
        {
            if (!TryGetPaymentId(gatewayPaymentId, out var paymentId) ||
                !orders.TryGetValue(paymentId, out var request))
            {
                throw new InvalidOperationException("The test payment was not created by this gateway.");
            }

            return Task.FromResult(OrderMode == Mode.FailedTerminal
                ? new GatewayPaymentStatusResult(
                    gatewayPaymentId, $"order_test_{paymentId:N}", "failed",
                    request.AmountMinor, request.Currency, false, true)
                : new GatewayPaymentStatusResult(
                    gatewayPaymentId, $"order_test_{paymentId:N}", "captured",
                    request.AmountMinor, request.Currency, true, false));
        }

        public Task<GatewayOrderPaymentsResult> GetPaymentsForOrderAsync(
            string gatewayOrderId, CancellationToken cancellationToken)
        {
            if (OrderMode == Mode.ThrowTransport)
            {
                return Task.FromException<GatewayOrderPaymentsResult>(
                    new HttpRequestException("Simulated gateway transport failure."));
            }

            var match = orders.SingleOrDefault(x => gatewayOrderId == $"order_test_{x.Key:N}");
            if (match.Value is null)
            {
                return Task.FromResult(new GatewayOrderPaymentsResult(gatewayOrderId, []));
            }

            GatewayPaymentStatusResult status = OrderMode == Mode.FailedTerminal
                ? new(
                    $"pay_failed_{match.Key:N}", gatewayOrderId, "failed",
                    match.Value.AmountMinor, match.Value.Currency, false, true)
                : new(
                    $"pay_test_{match.Key:N}", gatewayOrderId, "captured",
                    match.Value.AmountMinor, match.Value.Currency, true, false);
            return Task.FromResult(new GatewayOrderPaymentsResult(gatewayOrderId, [status]));
        }

        public Task<bool> VerifyWebhookSignatureAsync(
            ReadOnlySpan<byte> payload, string signature, CancellationToken cancellationToken) =>
            Task.FromResult(payload.Length > 0 && signature == "test_webhook_verified");

        public GatewayWebhookEvent ParseWebhook(ReadOnlySpan<byte> payload) =>
            throw new InvalidOperationException("No test webhook event was configured.");

        public Task<GatewayRefundResult> RefundAsync(
            string gatewayPaymentId, long amountMinor,
            string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayRefundResult(
                $"refund_test_{idempotencyKey}", "processed", true, false, null, null));

        private static bool TryGetPaymentId(string gatewayPaymentId, out Guid paymentId)
        {
            const string prefix = "pay_test_";
            paymentId = Guid.Empty;
            return gatewayPaymentId.StartsWith(prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(gatewayPaymentId[prefix.Length..], "N", out paymentId);
        }
    }
}
