using DoodhDirect.Application.Common;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class SubscriptionPaymentWalletIntegrationTests
{
    [Fact]
    public async Task WalletPayment_UsesSubscriptionAsOnlyTarget_ActivatesAndReplaysIdempotently()
    {
        await using var harness = await SubscriptionPaymentHarness.CreateAsync();
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(1_000m, "subscription-funding-1"),
            CancellationToken.None);

        var first = await harness.PaymentService.CreateForSubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.Id,
            PaymentMethod.Wallet,
            "subscription-payment-1",
            CancellationToken.None);
        var replay = await harness.PaymentService.CreateForSubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.Id,
            PaymentMethod.Wallet,
            "subscription-payment-1",
            CancellationToken.None);

        Assert.Equal(first.PublicId, replay.PublicId);
        Assert.Equal(PaymentStatus.Success, first.Status);
        Assert.Equal(PaymentMethod.Wallet, first.Method);
        Assert.Equal(harness.Subscription.PayableAmount, first.Amount);
        Assert.Equal(harness.Subscription.PublicId, first.SubscriptionId);
        Assert.Null(first.OrderId);
        Assert.Null(first.OrderNumber);

        harness.Db.ChangeTracker.Clear();
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        var wallet = await harness.Db.Wallets.AsNoTracking().SingleAsync();
        var debit = await harness.Db.WalletTransactions
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Type == WalletTransactionType.SubscriptionDebit);

        Assert.Equal(harness.Subscription.Id, payment.SubscriptionId);
        Assert.Null(payment.OrderId);
        Assert.Equal(harness.Subscription.PayableAmount, payment.Amount);
        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(harness.Clock.UtcNow, subscription.ActivatedAtUtc);
        Assert.Equal(1_000m - harness.Subscription.PayableAmount, wallet.Balance);
        Assert.Equal(-harness.Subscription.PayableAmount, debit.Amount);
        Assert.Equal(harness.Subscription.Id, debit.SubscriptionId);
        Assert.Equal(payment.Id, debit.PaymentId);
        Assert.Null(debit.OrderId);
        Assert.Equal(2, await harness.Db.WalletTransactions.CountAsync());
    }

    [Fact]
    public async Task WalletPayment_WithInsufficientBalance_DoesNotPersistPaymentOrDebitOrActivate()
    {
        await using var harness = await SubscriptionPaymentHarness.CreateAsync();
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(100m, "insufficient-funding-1"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            harness.PaymentService.CreateForSubscriptionAsync(
                harness.Customer.Id,
                harness.Subscription.Id,
                PaymentMethod.Wallet,
                "subscription-payment-insufficient-1",
                CancellationToken.None));

        Assert.Equal(100m, exception.AvailableBalance);
        Assert.Equal(harness.Subscription.PayableAmount, exception.RequiredAmount);

        harness.Db.ChangeTracker.Clear();
        Assert.Empty(await harness.Db.Payments.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(
            await harness.Db.WalletTransactions.AsNoTracking().ToListAsync(),
            transaction => transaction.Type == WalletTransactionType.SubscriptionDebit);
        Assert.Equal(100m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(
            SubscriptionStatus.PaymentPending,
            (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).Status);
    }

    private sealed class SubscriptionPaymentHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private SubscriptionPaymentHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            Subscription subscription,
            TestClock clock,
            PaymentService paymentService,
            WalletService walletService)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            Subscription = subscription;
            Clock = clock;
            PaymentService = paymentService;
            WalletService = walletService;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public Subscription Subscription { get; }
        public TestClock Clock { get; }
        public PaymentService PaymentService { get; }
        public WalletService WalletService { get; }

        public static async Task<SubscriptionPaymentHarness> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var customer = new User(UserType.Customer);
            customer.SetProfile("Subscription Customer");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();

            var product = new Product(
                category.Id,
                "MILK-001",
                "Fresh Milk",
                null,
                "litre",
                80m);
            var branch = new Branch(
                "MAIN",
                "Main Branch",
                "Bengaluru",
                "Karnataka",
                12.9716m,
                77.5946m);
            var address = new CustomerAddress(
                customer.Id,
                "Home",
                "1 Main Road",
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                "Subscription Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            db.AddRange(product, branch, address);
            await db.SaveChangesAsync();

            var subscription = new Subscription(
                customer.Id,
                product.Id,
                address.Id,
                branch.Id,
                "subscription-create-1",
                new DateOnly(2026, 8, 17),
                new DateOnly(2026, 8, 31),
                quantity: 1.5m,
                unitPrice: 80m,
                totalEntitlement: 4,
                product.Sku,
                product.Name,
                product.UnitOfMeasure,
                branch.Code,
                branch.Name,
                "Home, 1 Main Road, Central, Bengaluru, Karnataka 560001");
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();

            var clock = new TestClock(new DateTime(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc));
            var paymentOptions = Options.Create(new PaymentOptions
            {
                Provider = "Mock",
                Currency = "INR",
                PaymentExpiryMinutes = 15,
                MockSigningSecret = "test-signing-secret"
            });
            var walletService = new WalletService(db, clock, paymentOptions);
            var paymentService = new PaymentService(
                db,
                new MockPaymentGateway(paymentOptions),
                walletService,
                clock,
                paymentOptions);

            return new SubscriptionPaymentHarness(
                connection,
                db,
                customer,
                subscription,
                clock,
                paymentService,
                walletService);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
