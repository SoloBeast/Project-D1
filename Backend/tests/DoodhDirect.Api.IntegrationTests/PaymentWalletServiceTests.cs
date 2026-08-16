using DoodhDirect.Application.Common;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class PaymentWalletServiceTests
{
    [Theory]
    [InlineData(500, 160, 340)]
    [InlineData(160, 160, 0)]
    public async Task WalletPayment_WithSufficientOrExactBalance_DebitsLedgerAndConfirmsOrder(
        decimal startingBalance,
        decimal payableAmount,
        decimal expectedBalance)
    {
        await using var harness = await PaymentHarness.CreateAsync(payableAmount);
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(startingBalance, "topup-1"),
            CancellationToken.None);

        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
            "payment-1",
            CancellationToken.None);
        var replay = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
            "payment-1",
            CancellationToken.None);

        Assert.Equal(created.PublicId, replay.PublicId);
        Assert.Equal(PaymentStatus.Success, created.Status);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);

        var wallet = await harness.Db.Wallets.SingleAsync();
        var entries = await harness.Db.WalletTransactions.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(expectedBalance, wallet.Balance);
        Assert.Equal(2, entries.Count);
        Assert.Equal(WalletTransactionType.OrderDebit, entries[1].Type);
        Assert.Equal(startingBalance, entries[1].BalanceBefore);
        Assert.Equal(-payableAmount, entries[1].Amount);
        Assert.Equal(expectedBalance, entries[1].BalanceAfter);
    }

    [Theory]
    [InlineData(340, 800, 460)]
    [InlineData(0, 100, 100)]
    public async Task WalletPayment_WithInsufficientBalance_ReturnsBusinessErrorWithoutFinancialSideEffects(
        decimal startingBalance,
        decimal payableAmount,
        decimal expectedShortfall)
    {
        await using var harness = await PaymentHarness.CreateAsync(payableAmount);
        if (startingBalance > 0)
        {
            await harness.WalletService.TopUpAsync(
                harness.Customer.Id,
                new WalletTopUpRequest(startingBalance, "topup-1"),
                CancellationToken.None);
        }

        var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
            harness.PaymentService.CreateAsync(
                harness.Customer.Id,
                new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
                "payment-insufficient-1",
                CancellationToken.None));

        Assert.Equal("INSUFFICIENT_WALLET_BALANCE", exception.Code);
        Assert.Equal(422, exception.StatusCode);
        Assert.Equal(startingBalance, exception.AvailableBalance);
        Assert.Equal(payableAmount, exception.RequiredAmount);
        Assert.Equal(expectedShortfall, exception.Shortfall);
        Assert.Contains($"₹{expectedShortfall:0.##}", exception.Message);

        await harness.AssertInsufficientAttemptDidNotPersistAsync(startingBalance);
    }

    [Fact]
    public async Task WalletPayment_RepeatedInsufficientAttempts_CreateNoFinancialRecords()
    {
        await using var harness = await PaymentHarness.CreateAsync(payableAmount: 800m);
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id,
            new WalletTopUpRequest(340m, "topup-1"),
            CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var exception = await Assert.ThrowsAsync<InsufficientWalletBalanceException>(() =>
                harness.PaymentService.CreateAsync(
                    harness.Customer.Id,
                    new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
                    $"payment-insufficient-{attempt}",
                    CancellationToken.None));

            Assert.Equal(460m, exception.Shortfall);
            await harness.AssertInsufficientAttemptDidNotPersistAsync(expectedBalance: 340m);
        }
    }

    [Fact]
    public async Task RazorpayVerification_RequiresSignatureThenIndependentGatewayConfirmation()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "payment-1",
            CancellationToken.None);

        var invalid = new VerifyPaymentRequest(
            created.PublicId, created.GatewayOrderId!, $"pay_mock_{created.PublicId:N}", "invalid");
        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.PaymentService.VerifyAsync(harness.Customer.Id, invalid, CancellationToken.None));

        var result = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            invalid with { Signature = "mock_verified" },
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, result.Status);
        Assert.Equal($"pay_mock_{created.PublicId:N}", result.GatewayPaymentId);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task PaymentReadsAreOwnershipScoped()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        var payment = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "payment-1",
            CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.PaymentService.GetAsync(
                harness.OtherCustomer.Id, payment.PublicId, bypassOwnership: false, CancellationToken.None));

        var administratorRead = await harness.PaymentService.GetAsync(
            harness.OtherCustomer.Id, payment.PublicId, bypassOwnership: true, CancellationToken.None);
        Assert.Equal(payment.PublicId, administratorRead.PublicId);
    }

    [Fact]
    public async Task WalletAdjustment_IsIdempotentAndWritesAuditMetadata()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        var request = new WalletAdjustmentRequest(
            45m, "Manual correction", "adjust-1", "203.0.113.10", "DoodhDirectTests/1.0");

        var first = await harness.WalletService.AdjustAsync(
            harness.Administrator.Id, harness.Customer.PublicId, request, CancellationToken.None);
        var replay = await harness.WalletService.AdjustAsync(
            harness.Administrator.Id, harness.Customer.PublicId, request, CancellationToken.None);

        Assert.Equal(first.PublicId, replay.PublicId);
        Assert.Equal(45m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Single(await harness.Db.WalletTransactions.ToListAsync());

        var audit = await harness.Db.AuditLogs.SingleAsync();
        Assert.Equal("WalletAdjusted", audit.Action);
        Assert.Equal("203.0.113.10", audit.IPAddress);
        Assert.Equal("DoodhDirectTests/1.0", audit.UserAgent);
        Assert.Equal("Manual correction", audit.Reason);
    }

    [Fact]
    public async Task WalletRefund_CreditsWalletMarksRefundedAndWritesAudit()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        await harness.WalletService.TopUpAsync(
            harness.Customer.Id, new WalletTopUpRequest(100m, "topup-1"), CancellationToken.None);
        var payment = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Wallet),
            "payment-1", CancellationToken.None);

        var refund = await harness.PaymentService.RefundAsync(
            harness.Administrator.Id,
            payment.PublicId,
            new RefundPaymentRequest(40m, "Customer cancellation", "refund-1", "203.0.113.11", "Tests/1.0"),
            CancellationToken.None);

        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        var storedPayment = await harness.Db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.PartiallyRefunded, storedPayment.Status);
        Assert.Equal(40m, storedPayment.RefundedAmount);
        Assert.Equal(40m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Contains(await harness.Db.AuditLogs.ToListAsync(), x => x.Action == "PaymentRefundRequested");
    }

    [Fact]
    public async Task InvalidWebhookSignature_DoesNotPersistReceipt()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        var payload = "{\"event\":\"payment.captured\",\"id\":\"evt_1\"}"u8.ToArray();

        await Assert.ThrowsAsync<UnauthorizedAppException>(() =>
            harness.PaymentService.ProcessWebhookAsync(payload, "not-a-signature", CancellationToken.None));

        Assert.Empty(await harness.Db.PaymentWebhooks.ToListAsync());
    }

    private sealed class PaymentHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private PaymentHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            User administrator,
            Order order,
            PaymentService paymentService,
            WalletService walletService)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Administrator = administrator;
            Order = order;
            PaymentService = paymentService;
            WalletService = walletService;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User Administrator { get; }
        public Order Order { get; }
        public PaymentService PaymentService { get; }
        public WalletService WalletService { get; }

        public static async Task<PaymentHarness> CreateAsync(decimal payableAmount = 100m)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            var administrator = new User(UserType.SystemAdministrator);
            administrator.SetProfile("Administrator");
            db.Users.AddRange(customer, otherCustomer, administrator);
            await db.SaveChangesAsync();

            var order = new Order(
                customer.Id, 1, 1, "checkout-1", "DD-20260816020000-PAYMENT",
                subtotal: payableAmount, discountAmount: 0m,
                branchCode: "MAIN", branchName: "Main Branch", addressLabel: "Home",
                addressLine1: "1 Main Road", addressLine2: null, locality: "Central",
                city: "Bengaluru", state: "Karnataka", pinCode: "560001", landmark: null,
                deliveryInstructions: null, contactName: "Customer", contactMobile: "9999999999",
                latitude: 12.9716m, longitude: 77.5946m);
            db.Orders.Add(order);
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
                db, new MockPaymentGateway(paymentOptions), walletService, clock, paymentOptions);
            return new PaymentHarness(
                connection, db, customer, otherCustomer, administrator, order, paymentService, walletService);
        }

        public async Task AssertInsufficientAttemptDidNotPersistAsync(decimal expectedBalance)
        {
            Db.ChangeTracker.Clear();

            Assert.Empty(await Db.Payments.AsNoTracking().ToListAsync());
            Assert.Equal(
                OrderStatus.PendingPayment,
                (await Db.Orders.AsNoTracking().SingleAsync()).Status);

            var wallet = await Db.Wallets.AsNoTracking().SingleOrDefaultAsync();
            if (expectedBalance == 0)
            {
                Assert.Null(wallet);
                Assert.Empty(await Db.WalletTransactions.AsNoTracking().ToListAsync());
                return;
            }

            Assert.NotNull(wallet);
            Assert.Equal(expectedBalance, wallet.Balance);
            var entries = await Db.WalletTransactions.AsNoTracking().ToListAsync();
            var topUp = Assert.Single(entries);
            Assert.Equal(WalletTransactionType.TopUp, topUp.Type);
            Assert.Equal(expectedBalance, topUp.Amount);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
