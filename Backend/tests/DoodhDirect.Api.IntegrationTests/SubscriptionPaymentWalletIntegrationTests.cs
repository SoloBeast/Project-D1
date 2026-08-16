using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    public async Task MockGatewayPayment_VerifiesAndActivatesSubscription()
    {
        await using var harness = await SubscriptionPaymentHarness.CreateAsync();

        var created = await harness.PaymentService.CreateForSubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.Id,
            PaymentMethod.Razorpay,
            "subscription-gateway-payment-1",
            CancellationToken.None);
        var gatewayPaymentId = $"pay_mock_{created.PublicId:N}";

        var verified = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            new DoodhDirect.Application.Payments.VerifyPaymentRequest(
                created.PublicId,
                created.GatewayOrderId!,
                gatewayPaymentId,
                "mock_verified"),
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, verified.Status);
        Assert.Equal(gatewayPaymentId, verified.GatewayPaymentId);
        Assert.Equal(harness.Subscription.PublicId, verified.SubscriptionId);

        harness.Db.ChangeTracker.Clear();
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatus.Success, payment.Status);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(harness.Clock.UtcNow, subscription.ActivatedAtUtc);
    }

    [Fact]
    public async Task MockGatewayPayment_VerificationEndpoint_ReturnsSuccessAndActivatesSubscription()
    {
        await using var factory = new PaymentApiFactory();
        using var client = factory.CreateClient();

        Guid paymentId;
        Guid subscriptionId;
        string gatewayOrderId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
            var customer = await db.Users.SingleAsync(user => user.Email == DevelopmentCustomerSeedService.Email);
            var product = await db.Products.FirstAsync();
            var branch = await db.Branches.FirstAsync();
            var address = await db.CustomerAddresses.SingleAsync(item => item.UserId == customer.Id && item.IsActive);
            var subscription = new Subscription(
                customer.Id,
                product.Id,
                address.Id,
                branch.Id,
                $"http-subscription-{Guid.NewGuid():N}",
                DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(1)),
                DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(15)),
                quantity: 1m,
                unitPrice: product.Price,
                totalEntitlement: 4,
                product.Sku,
                product.Name,
                product.UnitOfMeasure,
                branch.Code,
                branch.Name,
                "Development test address");
            db.Subscriptions.Add(subscription);
            await db.SaveChangesAsync();

            var payment = await scope.ServiceProvider.GetRequiredService<IPaymentService>()
                .CreateForSubscriptionAsync(
                    customer.Id,
                    subscription.Id,
                    PaymentMethod.Razorpay,
                    $"http-payment-{Guid.NewGuid():N}",
                    CancellationToken.None);
            paymentId = payment.PublicId;
            subscriptionId = subscription.PublicId;
            gatewayOrderId = payment.GatewayOrderId!;
        }

        using var response = await client.PostAsJsonAsync(
            "/api/v1/payments/verify",
            new
            {
                paymentId,
                gatewayOrderId,
                gatewayPaymentId = $"pay_mock_{paymentId:N}",
                signature = "mock_verified"
            });
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected HTTP 200 but received {(int)response.StatusCode}: {responseBody}");
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var envelope = JsonSerializer.Deserialize<ApiResponse<PaymentResult>>(
            responseBody,
            jsonOptions);
        Assert.NotNull(envelope);
        Assert.True(envelope.Success);
        Assert.Equal(PaymentStatus.Success, envelope.Data!.Status);
        Assert.Equal(subscriptionId, envelope.Data.SubscriptionId);

        await using var verificationScope = factory.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<DoodhDirectDbContext>();
        Assert.Equal(
            SubscriptionStatus.Active,
            (await verificationDb.Subscriptions.SingleAsync(item => item.PublicId == subscriptionId)).Status);
    }

    [Fact]
    public async Task FailedGatewayPayment_WalletRetryCreatesFreshAttemptAndActivatesSameSubscriptionIdempotently()
    {
        await using var harness = await SubscriptionPaymentHarness.CreateAsync();
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(1_000m, "retry-funding-1"),
            CancellationToken.None);

        var original = await harness.PaymentService.CreateForSubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.Id,
            PaymentMethod.Razorpay,
            "subscription-original-attempt-1",
            CancellationToken.None);
        var originalPayment = await harness.Db.Payments.SingleAsync(payment => payment.PublicId == original.PublicId);
        originalPayment.Fail("PAYMENT_DECLINED", "The gateway declined the payment.", "failed", harness.Clock.UtcNow);
        harness.Subscription.FailPayment();
        await harness.Db.SaveChangesAsync();

        var retry = await harness.PaymentService.RetrySubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.PublicId,
            PaymentMethod.Wallet,
            "subscription-retry-attempt-1",
            CancellationToken.None);
        var replay = await harness.PaymentService.RetrySubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.PublicId,
            PaymentMethod.Wallet,
            "subscription-retry-attempt-1",
            CancellationToken.None);

        Assert.NotEqual(original.PublicId, retry.PublicId);
        Assert.Equal(retry.PublicId, replay.PublicId);
        Assert.Equal(PaymentStatus.Success, retry.Status);
        Assert.Equal(PaymentMethod.Wallet, retry.Method);
        Assert.Equal(harness.Subscription.PublicId, retry.SubscriptionId);

        harness.Db.ChangeTracker.Clear();
        var payments = await harness.Db.Payments
            .AsNoTracking()
            .OrderBy(payment => payment.Id)
            .ToListAsync();
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        var wallet = await harness.Db.Wallets.AsNoTracking().SingleAsync();
        var debits = await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(transaction => transaction.Type == WalletTransactionType.SubscriptionDebit)
            .ToListAsync();

        Assert.Equal(2, payments.Count);
        Assert.Equal(original.PublicId, payments[0].PublicId);
        Assert.Equal(PaymentStatus.Failed, payments[0].Status);
        Assert.Equal("subscription-original-attempt-1", payments[0].IdempotencyKey);
        Assert.Equal(retry.PublicId, payments[1].PublicId);
        Assert.Equal(PaymentStatus.Success, payments[1].Status);
        Assert.Equal("subscription-retry-attempt-1", payments[1].IdempotencyKey);
        Assert.All(payments, payment => Assert.Equal(harness.Subscription.Id, payment.SubscriptionId));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(harness.Clock.UtcNow, subscription.ActivatedAtUtc);
        Assert.Equal(1_000m - harness.Subscription.PayableAmount, wallet.Balance);

        var debit = Assert.Single(debits);
        Assert.Equal(1_000m, debit.BalanceBefore);
        Assert.Equal(-harness.Subscription.PayableAmount, debit.Amount);
        Assert.Equal(wallet.Balance, debit.BalanceAfter);
        Assert.Equal(payments[1].Id, debit.PaymentId);
        Assert.Equal(harness.Subscription.Id, debit.SubscriptionId);
        Assert.Null(debit.OrderId);
    }

    [Fact]
    public async Task PendingGatewayPayment_WalletRetryExpiresOriginalAndActivatesSameSubscriptionIdempotently()
    {
        await using var harness = await SubscriptionPaymentHarness.CreateAsync();
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(1_000m, "pending-retry-funding-1"),
            CancellationToken.None);

        var original = await harness.PaymentService.CreateForSubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.Id,
            PaymentMethod.Razorpay,
            "subscription-pending-original-1",
            CancellationToken.None);
        var retry = await harness.PaymentService.RetrySubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.PublicId,
            PaymentMethod.Wallet,
            "subscription-pending-retry-1",
            CancellationToken.None);
        var replay = await harness.PaymentService.RetrySubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.PublicId,
            PaymentMethod.Wallet,
            "subscription-pending-retry-1",
            CancellationToken.None);

        Assert.NotEqual(original.PublicId, retry.PublicId);
        Assert.Equal(retry.PublicId, replay.PublicId);
        Assert.Equal(PaymentStatus.Success, retry.Status);
        Assert.Equal(harness.Subscription.PublicId, retry.SubscriptionId);

        harness.Db.ChangeTracker.Clear();
        var payments = await harness.Db.Payments
            .AsNoTracking()
            .OrderBy(payment => payment.Id)
            .ToListAsync();
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        var debits = await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(transaction => transaction.Type == WalletTransactionType.SubscriptionDebit)
            .ToListAsync();

        Assert.Equal(2, payments.Count);
        Assert.Equal(PaymentStatus.Expired, payments[0].Status);
        Assert.Equal(PaymentStatus.Success, payments[1].Status);
        Assert.Equal(original.PublicId, payments[0].PublicId);
        Assert.Equal(retry.PublicId, payments[1].PublicId);
        Assert.All(payments, payment => Assert.Equal(harness.Subscription.Id, payment.SubscriptionId));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Single(debits);
        Assert.Equal(payments[1].Id, debits[0].PaymentId);
    }

    [Fact]
    public async Task PendingGatewayPayment_WalletRetryWithInsufficientBalanceLeavesOriginalAttemptRecoverable()
    {
        await using var harness = await SubscriptionPaymentHarness.CreateAsync();
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(100m, "pending-retry-insufficient-funding-1"),
            CancellationToken.None);

        var original = await harness.PaymentService.CreateForSubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.Id,
            PaymentMethod.Razorpay,
            "subscription-pending-insufficient-original-1",
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            harness.PaymentService.RetrySubscriptionAsync(
                harness.Customer.Id,
                harness.Subscription.PublicId,
                PaymentMethod.Wallet,
                "subscription-pending-insufficient-retry-1",
                CancellationToken.None));

        Assert.Equal(100m, exception.AvailableBalance);
        Assert.Equal(harness.Subscription.PayableAmount, exception.RequiredAmount);

        harness.Db.ChangeTracker.Clear();
        var payments = await harness.Db.Payments.AsNoTracking().ToListAsync();
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Single(payments);
        Assert.Equal(original.PublicId, payments[0].PublicId);
        Assert.Equal(PaymentStatus.Pending, payments[0].Status);
        Assert.Empty(await harness.Db.WalletTransactions
            .AsNoTracking()
            .Where(transaction => transaction.Type == WalletTransactionType.SubscriptionDebit)
            .ToListAsync());
        Assert.Equal(100m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(SubscriptionStatus.PaymentPending, subscription.Status);
    }

    [Fact]
    public async Task FailedGatewayPayment_WalletRetryWithInsufficientBalanceRollsBackAttemptAndSubscriptionTransition()
    {
        await using var harness = await SubscriptionPaymentHarness.CreateAsync();
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(100m, "retry-insufficient-funding-1"),
            CancellationToken.None);

        var original = await harness.PaymentService.CreateForSubscriptionAsync(
            harness.Customer.Id,
            harness.Subscription.Id,
            PaymentMethod.Razorpay,
            "subscription-original-insufficient-1",
            CancellationToken.None);
        var originalPayment = await harness.Db.Payments.SingleAsync(payment => payment.PublicId == original.PublicId);
        originalPayment.Fail("PAYMENT_DECLINED", "The gateway declined the payment.", "failed", harness.Clock.UtcNow);
        harness.Subscription.FailPayment();
        await harness.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            harness.PaymentService.RetrySubscriptionAsync(
                harness.Customer.Id,
                harness.Subscription.PublicId,
                PaymentMethod.Wallet,
                "subscription-retry-insufficient-1",
                CancellationToken.None));

        Assert.Equal(100m, exception.AvailableBalance);
        Assert.Equal(harness.Subscription.PayableAmount, exception.RequiredAmount);

        harness.Db.ChangeTracker.Clear();
        var payments = await harness.Db.Payments.AsNoTracking().ToListAsync();
        Assert.Single(payments);
        Assert.Equal(original.PublicId, payments[0].PublicId);
        Assert.Equal(PaymentStatus.Failed, payments[0].Status);
        Assert.DoesNotContain(
            await harness.Db.WalletTransactions.AsNoTracking().ToListAsync(),
            transaction => transaction.Type == WalletTransactionType.SubscriptionDebit);
        Assert.Equal(100m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(
            SubscriptionStatus.PaymentFailed,
            (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).Status);
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

    private sealed class PaymentApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection connection;

        public PaymentApiFactory()
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            using var db = new DoodhDirectDbContext(options);
            db.Database.EnsureCreated();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<DoodhDirectDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<DoodhDirectDbContext>>();
                services.RemoveAll<DoodhDirectDbContext>();
                services.AddDbContext<DoodhDirectDbContext>(options => options.UseSqlite(connection));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.AuthenticationScheme;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.AuthenticationScheme;
                        options.DefaultScheme = TestAuthenticationHandler.AuthenticationScheme;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.AuthenticationScheme,
                        _ => { });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string AuthenticationScheme = "SubscriptionPaymentTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim("user_id", "1"),
                new Claim(AuthorizationCodes.PermissionClaim, AuthorizationCodes.PaymentsCreateOwn)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationScheme));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, AuthenticationScheme)));
        }
    }
}
