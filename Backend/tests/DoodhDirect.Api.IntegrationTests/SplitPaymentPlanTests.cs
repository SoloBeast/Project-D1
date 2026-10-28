using DoodhDirect.Application.Common;
using DoodhDirect.Application.Payments;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Wallet-first split-payment plans for one-time orders:
/// opt-out/opt-in math, sub-₹1 guard, plan-aware confirmation,
/// exactly-once wallet release, and idempotent replays.
/// </summary>
public sealed class SplitPaymentPlanTests
{
    private static IOptions<PaymentOptions> RazorpayOptions() => Options.Create(new PaymentOptions
    {
        Provider = "Razorpay",
        Currency = "INR",
        PaymentExpiryMinutes = 15,
        RazorpayKeyId = "rzp_test_public",
        RazorpayKeySecret = "api-secret",
        MockSigningSecret = "unused"
    });

    [Fact]
    public async Task OptOut_ChargesFullAmountThroughRazorpay_WalletUntouched()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(1_000m, "topup-optout");

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: false,
            "plan-optout-1", CancellationToken.None);

        Assert.Equal(250m, plan.PayableAmount);
        Assert.Equal(0m, plan.WalletApplied);
        Assert.Equal(250m, plan.RazorpayAmount);
        Assert.False(plan.UsesWallet);
        Assert.Single(plan.Components);
        Assert.Equal(PaymentMethod.Razorpay, plan.Payment.Method);
        Assert.Equal(1_000m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.SingleAsync()).Status);
        Assert.Empty(await harness.Db.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.OrderDebit).ToListAsync());
    }

    [Fact]
    public async Task OptIn_PartialWallet_SplitsRemainderToRazorpay()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(100m, "topup-partial");

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-partial-1", CancellationToken.None);

        Assert.Equal(250m, plan.PayableAmount);
        Assert.Equal(100m, plan.WalletApplied);
        Assert.Equal(150m, plan.RazorpayAmount);
        Assert.True(plan.UsesWallet);
        Assert.Equal(2, plan.Components.Count);
        Assert.NotNull(plan.PaymentGroupId);
        Assert.All(plan.Components, c => Assert.Equal(plan.PaymentGroupId, c.PaymentGroupId));
        var rows = await harness.Db.Payments.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            250m,
            rows.Sum(x => x.Amount));
        Assert.Equal(0m, (await harness.Db.Wallets.SingleAsync()).Balance);
        // Wallet leg alone must not confirm the order.
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task ExactWalletCoverage_CreatesNoRazorpayRow_ConfirmsOrder()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 200m);
        await harness.SeedWalletAsync(200m, "topup-exact");

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-exact-1", CancellationToken.None);

        Assert.Equal(200m, plan.PayableAmount);
        Assert.Equal(200m, plan.WalletApplied);
        Assert.Equal(0m, plan.RazorpayAmount);
        Assert.Single(plan.Components);
        Assert.Equal(PaymentMethod.Wallet, plan.Payment.Method);
        Assert.Equal(PaymentStatus.Success, plan.Payment.Status);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);
        Assert.Equal(0m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Equal(0, harness.Gateway.CreateOrderCalls);
    }

    [Fact]
    public async Task WalletGreaterThanPayable_DebitsOnlyPayableAmount()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 200m);
        await harness.SeedWalletAsync(1_000m, "topup-rich");

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-rich-1", CancellationToken.None);

        Assert.Equal(200m, plan.WalletApplied);
        Assert.Equal(0m, plan.RazorpayAmount);
        Assert.Equal(800m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task ZeroWallet_OptIn_UsesRazorpayForFullAmount()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 120m);

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-zero-1", CancellationToken.None);

        Assert.Equal(120m, plan.PayableAmount);
        Assert.Equal(0m, plan.WalletApplied);
        Assert.Equal(120m, plan.RazorpayAmount);
        Assert.False(plan.UsesWallet);
        Assert.Single(plan.Components);
        Assert.Null(await harness.Db.Wallets.AsNoTracking().SingleOrDefaultAsync());
    }

    [Theory]
    [InlineData(100.00, 100.00, 0.00, false)]   // exact cover: wallet-only
    [InlineData(99.99, 100.00, 0.01, true)]     // remainder below minimum
    [InlineData(99.01, 100.00, 0.99, true)]     // remainder below minimum
    [InlineData(99.00, 100.00, 1.00, false)]    // remainder at minimum: allowed
    public async Task SubOneRupeeBoundaries(
        decimal walletBalance, decimal payable, decimal expectedRemainder, bool shouldReject)
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: payable);
        if (walletBalance > 0)
        {
            await harness.SeedWalletAsync(walletBalance, $"topup-bound-{walletBalance:0.00}");
        }

        if (shouldReject)
        {
            var exception = await Assert.ThrowsAsync<WalletRemainderBelowGatewayMinimumException>(() =>
                harness.PaymentService.CreateOrderPlanAsync(
                    harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
                    $"plan-bound-{walletBalance:0.00}", CancellationToken.None));
            Assert.Equal("WALLET_SPLIT_REMAINDER_BELOW_MINIMUM", exception.Code);
            Assert.Equal(expectedRemainder, exception.RazorpayRemainder);
            // Rejected before any debit: no payments, wallet intact, order payable.
            Assert.Empty(await harness.Db.Payments.AsNoTracking().ToListAsync());
            Assert.Equal(walletBalance, (await harness.Db.Wallets.SingleAsync()).Balance);
            Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.SingleAsync()).Status);
        }
        else
        {
            var plan = await harness.PaymentService.CreateOrderPlanAsync(
                harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
                $"plan-bound-ok-{walletBalance:0.00}", CancellationToken.None);
            Assert.Equal(expectedRemainder, plan.RazorpayAmount);
        }
    }

    [Fact]
    public async Task RazorpayLegSuccess_ConfirmsOrderOnlyWhenBothLegsSucceeded()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(100m, "topup-confirm");

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-confirm-1", CancellationToken.None);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.SingleAsync()).Status);

        var razorpayLeg = plan.Components.Single(x => x.Method == PaymentMethod.Razorpay);
        var verified = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            new VerifyPaymentRequest(
                razorpayLeg.PublicId, razorpayLeg.GatewayOrderId!,
                $"pay_test_{razorpayLeg.PublicId:N}", "test_verified"),
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, verified.Status);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);
        var rows = await harness.Db.Payments.AsNoTracking().ToListAsync();
        Assert.Equal(250m, rows.Sum(x => x.Amount));
    }

    [Fact]
    public async Task DefinitiveGatewayFailure_ReleasesWalletExactlyOnce_OrderNotPaid()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(100m, "topup-release");
        harness.Gateway.OrderMode = SplitTestGateway.Mode.FailedTerminal;

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-release-1", CancellationToken.None);
        var razorpayLeg = plan.Components.Single(x => x.Method == PaymentMethod.Razorpay);

        var first = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, razorpayLeg.PublicId, CancellationToken.None);
        var second = await harness.PaymentService.CancelAsync(
            harness.Customer.Id, razorpayLeg.PublicId, CancellationToken.None);

        Assert.Equal(PaymentStatus.Cancelled, first.Status);
        Assert.Equal(PaymentStatus.Cancelled, second.Status);
        // Wallet restored exactly once.
        Assert.Equal(100m, (await harness.Db.Wallets.SingleAsync()).Balance);
        var releases = await harness.Db.WalletTransactions
            .Where(x => x.IdempotencyKey == $"split-release:{plan.PaymentGroupId:N}:wallet")
            .ToListAsync();
        Assert.Single(releases);
        var walletRow = await harness.Db.Payments
            .Include(x => x.Refunds)
            .SingleAsync(x => x.Method == PaymentMethod.Wallet);
        Assert.Equal(PaymentStatus.Refunded, walletRow.Status);
        Assert.Single(walletRow.Refunds);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task AmbiguousGatewayOutcome_DoesNotReleaseWallet()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(100m, "topup-ambiguous");
        harness.Gateway.OrderMode = SplitTestGateway.Mode.ThrowTransport;

        var plan = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-ambiguous-1", CancellationToken.None);
        var razorpayLeg = plan.Components.Single(x => x.Method == PaymentMethod.Razorpay);

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.PaymentService.CancelAsync(
                harness.Customer.Id, razorpayLeg.PublicId, CancellationToken.None));

        // Held funds retained for reconciliation: wallet still debited, leg untouched.
        Assert.Equal(0m, (await harness.Db.Wallets.SingleAsync()).Balance);
        Assert.Equal(
            PaymentStatus.Success,
            (await harness.Db.Payments.SingleAsync(x => x.Method == PaymentMethod.Wallet)).Status);
        Assert.Equal(
            PaymentStatus.Pending,
            (await harness.Db.Payments.SingleAsync(x => x.Method == PaymentMethod.Razorpay)).Status);
    }

    [Fact]
    public async Task DuplicateCreate_SameKey_ReplaysPlanWithoutDuplicateDebits()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(100m, "topup-idem");

        var first = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-idem-1", CancellationToken.None);
        var replay = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-idem-1", CancellationToken.None);

        Assert.Equal(first.PaymentGroupId, replay.PaymentGroupId);
        Assert.Equal(2, await harness.Db.Payments.CountAsync());
        Assert.Single(await harness.Db.WalletTransactions
            .Where(x => x.Type == WalletTransactionType.OrderDebit).ToListAsync());
        Assert.Equal(0m, (await harness.Db.Wallets.SingleAsync()).Balance);
    }

    [Fact]
    public async Task TamperedExpectedPayable_IsRejected()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(100m, "topup-tamper");

        await Assert.ThrowsAsync<ConflictException>(() =>
            harness.PaymentService.CreateAsync(
                harness.Customer.Id,
                new CreatePaymentRequest(
                    harness.Order.PublicId, PaymentMethod.Razorpay,
                    UseWalletBalance: true, ExpectedPayableAmount: 1m),
                "plan-tamper-1",
                CancellationToken.None));
    }

    [Fact]
    public async Task FreshAttempt_AfterAbandonedPlan_CanProceed()
    {
        await using var harness = await SplitHarness.CreateAsync(payableAmount: 250m);
        await harness.SeedWalletAsync(100m, "topup-fresh");
        harness.Gateway.OrderMode = SplitTestGateway.Mode.FailedTerminal;

        var doomed = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: true,
            "plan-doomed-1", CancellationToken.None);
        await harness.PaymentService.CancelAsync(
            harness.Customer.Id,
            doomed.Components.Single(x => x.Method == PaymentMethod.Razorpay).PublicId,
            CancellationToken.None);

        // The gateway consistently reports no capture for the abandoned leg,
        // so reconciliation proves it safe to attempt again.
        var fresh = await harness.PaymentService.CreateOrderPlanAsync(
            harness.Customer.Id, harness.Order.PublicId, useWalletBalance: false,
            "plan-fresh-2", CancellationToken.None);

        Assert.Equal(250m, fresh.RazorpayAmount);
        Assert.NotEqual(doomed.PaymentGroupId, fresh.PaymentGroupId);
    }

    private sealed class SplitHarness : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private SplitHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            Order order,
            TestIndiaTimeProvider timeProvider,
            PaymentService paymentService,
            SplitTestGateway gateway)
        {
            this.connection = connection;
            Db = db;
            Customer = customer;
            Order = order;
            TimeProvider = timeProvider;
            PaymentService = paymentService;
            Gateway = gateway;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public Order Order { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public PaymentService PaymentService { get; }
        public SplitTestGateway Gateway { get; }

        public static async Task<SplitHarness> CreateAsync(decimal payableAmount = 100m)
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
            customer.SetProfile("Split Customer");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var order = new Order(
                customer.Id, 1, 1, "checkout-split-1", $"DD-SPLIT-{Guid.NewGuid():N}",
                subtotal: payableAmount, discountAmount: 0m,
                branchCode: "MAIN", branchName: "Main Branch", addressLabel: "Home",
                addressLine1: "1 Main Road", addressLine2: null, locality: "Central",
                city: "Bengaluru", state: "Karnataka", pinCode: "560001", landmark: null,
                deliveryInstructions: null, contactName: "Customer", contactMobile: "9999999999",
                latitude: 12.9716m, longitude: 77.5946m);
            db.Orders.Add(order);
            await db.SaveChangesAsync();

            var clock = new TestClock(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified));
            var paymentOptions = RazorpayOptions();
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var indiaTime = new TestIndiaTimeProvider(clock);
            var gateway = new SplitTestGateway(paymentOptions);
            var walletService = new WalletService(db, indiaTime, paymentOptions, notificationEventWriter);
            var paymentService = new PaymentService(
                db, gateway, walletService, indiaTime, paymentOptions, notificationEventWriter);
            return new SplitHarness(connection, db, customer, order, indiaTime, paymentService, gateway);
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

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class SplitTestGateway(IOptions<PaymentOptions> options) : IPaymentGateway
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

            return Task.FromResult(new GatewayPaymentStatusResult(
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
