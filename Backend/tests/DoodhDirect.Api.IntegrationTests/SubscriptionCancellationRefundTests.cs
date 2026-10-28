using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class SubscriptionCancellationRefundTests
{
    [Fact]
    public async Task Cancel_RefundsOnlyEligibleUnusedPortion()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.DeliverFirstAsync();
        await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Cancelled, result.Status);
        Assert.NotNull(result.CancellationRefund);
        Assert.Equal("Completed", result.CancellationRefund.Status);
        Assert.Equal(3, result.CancellationRefund.EligibleOccurrences);
        Assert.Equal(240m, result.CancellationRefund.RefundedAmount);

        Assert.Equal(240m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(3, await harness.Db.SubscriptionCancelRefundOccurrences.AsNoTracking().CountAsync());
        Assert.Equal(240m, await harness.Db.SubscriptionCancelRefundOccurrences.AsNoTracking().SumAsync(x => x.AllocationAmount));
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(240m, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
    }

    [Fact]
    public async Task Cancel_SkippedAndFailedOccurrences_RemainOnExistingWorkflows()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.SkipFirstAsync();
        await harness.FailSecondAsync();
        await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        // Skipped/failed rows never enter the Scheduled snapshot set, so
        // only the 2 remaining Scheduled occurrences are eligible.
        Assert.Equal(2, result.CancellationRefund!.EligibleOccurrences);
        Assert.Equal(0, result.CancellationRefund.ExcludedOccurrences);
        Assert.Equal(160m, result.CancellationRefund.RefundedAmount);
        Assert.Equal(SubscriptionDeliveryStatus.Skipped, await harness.OccurrenceStatusAsync(0));
        Assert.Equal(SubscriptionDeliveryStatus.Failed, await harness.OccurrenceStatusAsync(1));
    }

    [Fact]
    public async Task Cancel_VacationIncludedOccurrence_IsExcluded()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        var payment = await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");
        await harness.SeedPendingVacationRequestAsync(created.Subscription.PublicId, payment.Id, occurrenceIndex: 0);

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(3, result.CancellationRefund!.EligibleOccurrences);
        Assert.Equal(1, result.CancellationRefund.ExcludedOccurrences);
        Assert.Equal(240m, result.CancellationRefund.RefundedAmount);
    }

    [Fact]
    public async Task Cancel_CompletedCustomerFaultTicket_IsExcludedEntirely()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        var payment = await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");
        await harness.FailSecondAsync();
        // NOTE: Sqlite stores decimals as TEXT, so its CHECK comparison is
        // lexicographic. A 60% deduction yields a 32.00 settlement whose
        // leading digit keeps every TEXT comparison aligned with SQL
        // Server's numeric semantics used in production.
        var retained = await harness.SeedCompletedCustomerFaultSettlementAsync(1, deductionPercentage: 60m);

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(3, result.CancellationRefund!.EligibleOccurrences);
        Assert.Equal(240m, result.CancellationRefund.RefundedAmount);
        Assert.Equal(240m + retained, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Cancel_PendingTicket_BlocksCancellation()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");
        await harness.SeedPendingTicketAsync(occurrenceIndex: 0);

        var error = await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None));
        Assert.Contains("unresolved", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Active, (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancel_UsesFrozenPricing_NotCurrentCatalogue()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");

        var product = await harness.Db.Products.SingleAsync();
        product.Update(product.CategoryId, product.Sku, product.Name, product.Description, product.UnitOfMeasure, 999m);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(320m, result.CancellationRefund!.RefundedAmount);
    }

    [Fact]
    public async Task Cancel_SplitPayments_AllocatedAcrossRowsInIdOrder()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.DeliverFirstAsync();
        var walletPayment = await harness.SeedSuccessPaymentAsync(100m, PaymentMethod.Wallet, "pay-wallet-1");
        var gatewayPayment = await harness.SeedSuccessPaymentAsync(220m, PaymentMethod.Razorpay, "pay-rzp-1");

        await harness.Service.CancelAsync(harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        var wallet = await harness.Db.Payments.AsNoTracking().SingleAsync(x => x.Id == walletPayment.Id);
        var gateway = await harness.Db.Payments.AsNoTracking().SingleAsync(x => x.Id == gatewayPayment.Id);
        Assert.Equal(100m, wallet.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, wallet.Status);
        Assert.Equal(140m, gateway.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, gateway.Status);
        Assert.Equal(240m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task Cancel_WithoutSuccessfulPayment_CancelsWithNoRefund()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Cancelled, result.Status);
        Assert.NotNull(result.CancellationRefund);
        Assert.Equal("None", result.CancellationRefund.Status);
        Assert.Equal(0m, result.CancellationRefund.RefundedAmount);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.SubscriptionCancelRefundOccurrences.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancel_PendingGatewayAttempt_BlocksUnsafeRefund()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.SeedPendingPaymentAsync(created.Subscription.PayableAmount, "pay-pending-1");

        var error = await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None));
        Assert.Contains("pending gateway", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Active, (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancel_Repeat_ProducesAtMostOneCreditPerOccurrence()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");

        var first = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        var second = await harness.Service.CancelAsync(
            harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        Assert.Equal("Completed", first.CancellationRefund!.Status);
        Assert.Equal("Completed", second.CancellationRefund!.Status);
        Assert.Equal(first.CancellationRefund.RefundedAmount, second.CancellationRefund.RefundedAmount);
        Assert.Equal(320m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(4, await harness.Db.SubscriptionCancelRefundOccurrences.AsNoTracking().CountAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.SubscriptionCancelRefunded).ToListAsync());
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "SUBSCRIPTION.CANCEL_REFUND").ToListAsync());
    }

    [Fact]
    public async Task Cancel_ConcurrentRequests_ProduceAtMostOneCreditPerOccurrence()
    {
        // Real SQL Server Express: SERIALIZABLE key-range locking is
        // enforced by the engine, so racing cancellations genuinely
        // collide here (a shared SQLite connection serializes writers
        // and cannot prove this). Requires SQLEXPRESS to be running.
        var databaseName = $"DoodhDirect_Test_CancelRace_{Guid.NewGuid():N}";
        var connectionBuilder = new SqlConnectionStringBuilder
        {
            DataSource = @".\SQLEXPRESS",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            MultipleActiveResultSets = true,
        };
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseSqlServer(connectionBuilder.ConnectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 5);
                sql.CommandTimeout(60);
            })
            .Options;
        await using var seedDb = new DoodhDirectDbContext(options);
        try
        {
            await seedDb.Database.EnsureCreatedAsync();
        }
        catch (SqlException ex) when (ex.Number is -2 or 2 or 40 or 53)
        {
            await seedDb.DisposeAsync();
            throw new InvalidOperationException(
                @"This concurrency test requires the local SQL Server Express instance ('.\SQLEXPRESS'). " +
                "Start the SQLEXPRESS service and re-run dotnet test.", ex);
        }

        try
        {
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            seedDb.Users.Add(customer);
            await seedDb.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            seedDb.ProductCategories.Add(category);
            await seedDb.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            var branch = new Domain.Catalogue.Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            seedDb.AddRange(product, branch, address);
            await seedDb.SaveChangesAsync();
            seedDb.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            await seedDb.SaveChangesAsync();
            await new NumberSeriesSeedService(seedDb)
                .EnsureScopedSubscriptionSeriesAsync(branch.Code, CancellationToken.None);

            var clock = new TestClock(new DateTime(2026, 8, 15, 2, 0, 0, DateTimeKind.Utc));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var customerId = customer.Id;
            var subscriptionPublicId = Guid.Empty;
            await using (var setupDb = new DoodhDirectDbContext(options))
            {
                var setup = new SubscriptionService(
                    setupDb,
                    new FixedAllocation(branch),
                    new NoOpPaymentService(setupDb, clock),
                    timeProvider,
                    new TestNotificationEventWriter(setupDb, clock),
                    new NumberSeriesService(setupDb, timeProvider));
                var created = await setup.CreateAsync(
                    customerId,
                    new CreateSubscriptionRequest(
                        product.PublicId,
                        address.PublicId,
                        1m,
                        new DateOnly(2026, 8, 17),
                        [DayOfWeek.Monday, DayOfWeek.Wednesday],
                        4,
                        PaymentMethod.Razorpay),
                    $"sub-{Guid.NewGuid():N}",
                    CancellationToken.None);
                var subscription = await setupDb.Subscriptions.SingleAsync();
                subscription.Activate(timeProvider.Now);
                await setupDb.SaveChangesAsync();
                var payment = Payment.CreateForSubscription(
                    subscription.Id, customerId, PaymentMethod.Wallet,
                    subscription.PayableAmount, "INR", "pay-race-1", timeProvider.Now.AddDays(1));
                payment.Succeed(null, "wallet-debit", timeProvider.Now);
                setupDb.Payments.Add(payment);
                await setupDb.SaveChangesAsync();
                subscriptionPublicId = created.Subscription.PublicId;
            }

            var attempts = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                await using var db = new DoodhDirectDbContext(options);
                var service = new SubscriptionService(
                    db,
                    new FixedAllocation(branch),
                    new NoOpPaymentService(db, clock),
                    timeProvider,
                    new TestNotificationEventWriter(db, clock),
                    new NumberSeriesService(db, timeProvider));
                try
                {
                    return await service.CancelAsync(customerId, subscriptionPublicId, CancellationToken.None);
                }
                catch (ConflictException)
                {
                    // Lost the race before the winner's commit became
                    // visible: the operation is safe to retry by contract.
                    return null;
                }
            })).ToArray();
            var firstPass = await Task.WhenAll(attempts);

            // At least the winner reports its committed outcome.
            Assert.Contains(firstPass, r => r is not null && r.CancellationRefund?.Status == "Completed");

            // Every loser retries into the winner's committed outcome.
            var results = firstPass.ToList();
            await using (var retryDb = new DoodhDirectDbContext(options))
            {
                var retryService = new SubscriptionService(
                    retryDb,
                    new FixedAllocation(branch),
                    new NoOpPaymentService(retryDb, clock),
                    timeProvider,
                    new TestNotificationEventWriter(retryDb, clock),
                    new NumberSeriesService(retryDb, timeProvider));
                for (var i = 0; i < results.Count; i++)
                {
                    results[i] ??= await retryService.CancelAsync(customerId, subscriptionPublicId, CancellationToken.None);
                }
            }

            // Every attempt reports the winner's committed outcome; the
            // wallet holds exactly one full-prepaid credit.
            Assert.All(results, r =>
            {
                Assert.NotNull(r);
                Assert.Equal(SubscriptionStatus.Cancelled, r.Status);
                Assert.NotNull(r.CancellationRefund);
                Assert.Equal("Completed", r.CancellationRefund.Status);
                Assert.Equal(320m, r.CancellationRefund.RefundedAmount);
            });
            Assert.Equal(320m, await seedDb.Wallets.AsNoTracking().SumAsync(x => x.Balance));
            Assert.Equal(4, await seedDb.SubscriptionCancelRefundOccurrences.AsNoTracking().CountAsync());
            Assert.Single(await seedDb.NotificationEvents.AsNoTracking()
                .Where(x => x.EventType == NotificationEventTypes.SubscriptionCancelRefunded).ToListAsync());
            Assert.Single(await seedDb.AuditLogs.AsNoTracking()
                .Where(x => x.Action == "SUBSCRIPTION.CANCEL_REFUND").ToListAsync());
        }
        finally
        {
            try { await seedDb.Database.EnsureDeletedAsync(); } catch { }
        }
    }

    [Fact]
    public async Task Cancel_LedgerPaymentAndAuditRecords_Reconcile()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var created = await harness.CreateActiveSubscriptionAsync();
        await harness.DeliverFirstAsync();
        await harness.SeedSuccessPaymentAsync(created.Subscription.PayableAmount, PaymentMethod.Wallet, "pay-seed-1");

        await harness.Service.CancelAsync(harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        var walletTotal = await harness.Db.WalletTransactions.AsNoTracking()
            .Where(x => x.Type == WalletTransactionType.RefundCredit).SumAsync(x => x.Amount);
        var occurrenceTotal = await harness.Db.SubscriptionCancelRefundOccurrences.AsNoTracking().SumAsync(x => x.AllocationAmount);
        var refundTotal = await harness.Db.Refunds.AsNoTracking()
            .Where(x => x.Status == RefundStatus.Succeeded && x.IdempotencyKey.StartsWith("subscription-cancel:"))
            .SumAsync(x => x.Amount);
        var paymentRefunded = (await harness.Db.Payments.AsNoTracking().SingleAsync()).RefundedAmount;
        Assert.Equal(240m, walletTotal);
        Assert.Equal(walletTotal, occurrenceTotal);
        Assert.Equal(walletTotal, refundTotal);
        Assert.Equal(walletTotal, paymentRefunded);
        Assert.Contains(
            await harness.Db.AuditLogs.AsNoTracking().Select(x => x.Action).ToListAsync(),
            x => x == "SUBSCRIPTION.CANCEL_REFUND");
    }

    private sealed class FixedAllocation(Domain.Catalogue.Branch branch) : Application.Orders.IBranchAllocationService
    {
        public Task<BranchAllocationResult> AllocateAsync(
            decimal latitude, decimal longitude,
            IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BranchAllocationResult(branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
    }

    private sealed class NoOpPaymentService(DoodhDirectDbContext db, TestClock clock) : IPaymentService
    {
        public async Task<PaymentResult> CreateForSubscriptionAsync(
            long customerId, long subscriptionId, PaymentMethod method, string idempotencyKey, CancellationToken cancellationToken)
        {
            var subscription = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == subscriptionId, cancellationToken);
            return new PaymentResult(
                Guid.NewGuid(), null, null, null, method, "Razorpay", PaymentStatus.Pending,
                subscription.PayableAmount, 0m, "INR", $"order_{subscription.PublicId:N}", null,
                "rzp_test", null, null, clock.UtcNow.AddMinutes(15), null, clock.UtcNow, subscription.PublicId);
        }

        public Task<PaymentResult> CreateWalletTopUpAsync(long a, decimal b, string c, CancellationToken d) => throw new NotSupportedException();
        public Task<PaymentResult> RetrySubscriptionAsync(long a, Guid b, PaymentMethod c, string d, CancellationToken e) => throw new NotSupportedException();
        public Task<PaymentPlanResult> CreateOrderPlanAsync(long a, Guid b, bool c, string d, CancellationToken e) => throw new NotSupportedException();
        public Task<PaymentPlanResult> CreateSubscriptionPlanAsync(long a, long b, bool c, string d, CancellationToken e, bool f = false) => throw new NotSupportedException();
        public Task<PaymentResult> CompleteDevelopmentAsync(long a, Guid b, CancellationToken c) => throw new NotSupportedException();
        public Task<PaymentResult> CancelAsync(long a, Guid b, CancellationToken c) => throw new NotSupportedException();
        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(CancellationToken c) => throw new NotSupportedException();
        public Task<PaymentResult> CreateAsync(long a, CreatePaymentRequest b, string c, CancellationToken d) => throw new NotSupportedException();
        public Task<PaymentResult> VerifyAsync(long a, VerifyPaymentRequest b, CancellationToken c) => throw new NotSupportedException();
        public Task<PaymentResult> GetAsync(long a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
        public Task<PaymentReconciliationResult> ReconcileAsync(long a, Guid b, bool c, CancellationToken d) => throw new NotSupportedException();
        public Task<RefundResult> RefundAsync(long a, Guid b, RefundPaymentRequest c, CancellationToken d) => throw new NotSupportedException();
        public Task ProcessWebhookAsync(byte[] a, string b, CancellationToken c) => throw new NotSupportedException();
    }

    private sealed class CancelRefundHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private CancelRefundHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            Product product,
            Domain.Catalogue.Branch branch,
            CustomerAddress address,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            SubscriptionService service)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            Product = product;
            Branch = branch;
            Address = address;
            Clock = clock;
            TimeProvider = timeProvider;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public Product Product { get; }
        public Domain.Catalogue.Branch Branch { get; }
        public CustomerAddress Address { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public SubscriptionService Service { get; }

        public static async Task<CancelRefundHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            var branch = new Domain.Catalogue.Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            db.AddRange(product, branch, address);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            await db.SaveChangesAsync();
            await new NumberSeriesSeedService(db)
                .EnsureScopedSubscriptionSeriesAsync(branch.Code, CancellationToken.None);

            // 07:30 IST on the 15th: inside the 24h skip cutoff for the
            // first (17th) occurrence, so manual-skip seeding succeeds.
            var clock = new TestClock(new DateTime(2026, 8, 15, 2, 0, 0, DateTimeKind.Utc));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var service = new SubscriptionService(
                db,
                new FixedAllocation(branch),
                new NoOpPaymentService(db, clock),
                timeProvider,
                new TestNotificationEventWriter(db, clock),
                new NumberSeriesService(db, timeProvider));
            return new CancelRefundHarness(connection, db, customer, product, branch, address, clock, timeProvider, service);
        }

        public async Task<CreatedSubscriptionResult> CreateActiveSubscriptionAsync()
        {
            var created = await Service.CreateAsync(
                Customer.Id,
                new CreateSubscriptionRequest(
                    Product.PublicId,
                    Address.PublicId,
                    1m,
                    new DateOnly(2026, 8, 17),
                    [DayOfWeek.Monday, DayOfWeek.Wednesday],
                    4,
                    PaymentMethod.Razorpay),
                $"sub-{Guid.NewGuid():N}",
                CancellationToken.None);
            var subscription = await Db.Subscriptions.SingleAsync();
            subscription.Activate(TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return created;
        }

        public async Task DeliverFirstAsync()
        {
            var subscription = await Db.Subscriptions.Include(x => x.Deliveries).SingleAsync();
            var first = subscription.Deliveries.OrderBy(x => x.ScheduledDate).First();
            subscription.MarkDelivered(first, TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SkipFirstAsync()
        {
            var subscription = await Db.Subscriptions.Include(x => x.Deliveries).SingleAsync();
            var first = subscription.Deliveries.OrderBy(x => x.ScheduledDate).First();
            subscription.Skip(first, TimeProvider.Now, TimeSpan.FromHours(24));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task FailSecondAsync()
        {
            var subscription = await Db.Subscriptions.Include(x => x.Deliveries).SingleAsync();
            var second = subscription.Deliveries.OrderBy(x => x.ScheduledDate).Skip(1).First();
            subscription.MarkFailed(second, TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task<SubscriptionDeliveryStatus> OccurrenceStatusAsync(int index)
        {
            var statuses = await Db.SubscriptionDeliveries.AsNoTracking()
                .OrderBy(x => x.ScheduledDate).Select(x => x.Status).ToListAsync();
            return statuses[index];
        }

        public async Task<Payment> SeedSuccessPaymentAsync(decimal amount, PaymentMethod method, string key)
        {
            var subscription = await Db.Subscriptions.AsNoTracking().SingleAsync();
            var payment = Payment.CreateForSubscription(
                subscription.Id, Customer.Id, method, amount, "INR", key, TimeProvider.Now.AddDays(1));
            if (method == PaymentMethod.Wallet)
            {
                payment.Succeed(null, "wallet-debit", TimeProvider.Now);
            }
            else
            {
                payment.Succeed("pay_test_123", "captured", TimeProvider.Now);
            }

            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return payment;
        }

        public async Task SeedPendingPaymentAsync(decimal amount, string key)
        {
            var subscription = await Db.Subscriptions.AsNoTracking().SingleAsync();
            Db.Payments.Add(Payment.CreateForSubscription(
                subscription.Id, Customer.Id, PaymentMethod.Razorpay, amount, "INR", key, TimeProvider.Now.AddDays(1)));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SeedPendingVacationRequestAsync(Guid subscriptionPublicId, long paymentId, int occurrenceIndex)
        {
            var subscription = await Db.Subscriptions.AsNoTracking().SingleAsync(x => x.PublicId == subscriptionPublicId);
            var occurrence = await Db.SubscriptionDeliveries.AsNoTracking()
                .OrderBy(x => x.ScheduledDate).ToListAsync();
            var target = occurrence[occurrenceIndex];
            var gross = decimal.Round(subscription.PayableAmount / subscription.TotalEntitlement, 2, MidpointRounding.AwayFromZero);
            var request = new VacationRefundRequest(
                subscription.Id, Customer.Id, Branch.Id, gross, gross, 1,
                subscription.PayableAmount, subscription.TotalEntitlement, "INR", paymentId,
                "{\"basis\":\"test\"}", TimeProvider.Now);
            request.AssignRequestNumber($"VAC/{Guid.NewGuid():N}");
            Db.VacationRefundRequests.Add(request);
            await Db.SaveChangesAsync();
            Db.VacationRefundOccurrences.Add(new VacationRefundOccurrence(
                request.Id, target.Id, target.ScheduledDate, target.Quantity, gross));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task<decimal> SeedCompletedCustomerFaultSettlementAsync(int occurrenceIndex, decimal deductionPercentage)
        {
            var subscription = await Db.Subscriptions.AsNoTracking().SingleAsync();
            var occurrence = await Db.SubscriptionDeliveries.AsNoTracking()
                .OrderBy(x => x.ScheduledDate).ToListAsync();
            var target = occurrence[occurrenceIndex];
            var now = TimeProvider.Now;
            Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                target.Id, Customer.Id, Branch.Id, target.ScheduledDate,
                "SUB-CANCEL-TEST", "Customer", "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m));
            await Db.SaveChangesAsync();
            var deliveryId = await Db.Deliveries.AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == target.Id).Select(x => x.Id).SingleAsync();
            var ticket = new RefundReplacementRequest(
                null, deliveryId, Customer.Id, Branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                "Customer not available", null, now, now.AddHours(48),
                target.Id, now, 48, deductionPercentage, "CustomerNotAvailable");
            ticket.AssignRequestNumber($"RR/CANCEL/{Guid.NewGuid():N}");
            ticket.Approve(Customer.Id, now, "Support settled");
            ticket.Complete(Customer.Id, now, "Support settled");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();

            var gross = decimal.Round(subscription.PayableAmount / subscription.TotalEntitlement, 2, MidpointRounding.AwayFromZero);
            var deduction = decimal.Round(gross * deductionPercentage / 100m, 2, MidpointRounding.AwayFromZero);
            var final = gross - deduction;
            var key = $"delivery-fail:{ticket.PublicId:N}:refund";
            var payment = await Db.Payments.Include(x => x.Refunds).SingleAsync();
            var refund = payment.StartRefund(final, "Customer-fault settlement", key, Customer.Id);
            refund.Succeed(now);
            payment.CompleteRefund(final);
            var wallet = new Wallet(Customer.Id, "INR");
            wallet.Credit(WalletTransactionType.RefundCredit, final, key, "Customer-fault settlement", now, payment.Id, subscriptionId: subscription.Id);
            Db.Wallets.Add(wallet);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return final;
        }

        public async Task SeedPendingTicketAsync(int occurrenceIndex)
        {
            var occurrence = await Db.SubscriptionDeliveries.AsNoTracking()
                .OrderBy(x => x.ScheduledDate).ToListAsync();
            var target = occurrence[occurrenceIndex];
            var now = TimeProvider.Now;
            Db.Deliveries.Add(Delivery.ForSubscriptionOccurrence(
                target.Id, Customer.Id, Branch.Id, target.ScheduledDate,
                "SUB-CANCEL-PENDING", "Customer", "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m));
            await Db.SaveChangesAsync();
            var deliveryId = await Db.Deliveries.AsNoTracking()
                .Where(x => x.SubscriptionDeliveryId == target.Id).Select(x => x.Id).SingleAsync();
            var ticket = new RefundReplacementRequest(
                null, deliveryId, Customer.Id, Branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.DeliveryFailed,
                "Customer not available", null, now, now.AddHours(48),
                target.Id, now, 48, 10m, "CustomerNotAvailable");
            ticket.AssignRequestNumber($"RR/PENDING/{Guid.NewGuid():N}");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
