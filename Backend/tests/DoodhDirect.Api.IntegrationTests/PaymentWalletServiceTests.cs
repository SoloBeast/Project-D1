using System.Net;
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
        var expectedOccurredAt = new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified);
        Assert.Equal(expectedBalance, wallet.Balance);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(expectedOccurredAt, entry.OccurredAt);
            Assert.Equal(DateTimeKind.Unspecified, entry.OccurredAt.Kind);
        });
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
    public async Task RazorpayPayment_BelowOneRupee_IsRejectedBeforeGatewayOrderCreation()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        await using var harness = await PaymentHarness.CreateAsync(
            payableAmount: 0.99m,
            paymentOptions: options,
            gateway: new TestRazorpayGateway(options));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.CreateAsync(
                harness.Customer.Id,
                new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
                "payment-below-gateway-minimum",
                CancellationToken.None));

        Assert.Equal(
            "The payment amount must be at least 100 paise and representable by the gateway.",
            exception.Message);
        Assert.Equal(OrderStatus.PendingPayment, (await harness.Db.Orders.SingleAsync()).Status);
        Assert.Equal(PaymentStatus.Initiated, (await harness.Db.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task RazorpayGateway_NonSuccessResponse_PreservesOnlySanitizedProviderDiagnostic()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"BAD_REQUEST_ERROR\",\"description\":\"Invalid   key_id\\nvalue\",\"metadata\":{\"secret\":\"do-not-return\"}}}")
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        var gateway = new RazorpayPaymentGateway(
            client,
            Options.Create(new PaymentOptions
            {
                Provider = "Razorpay",
                Currency = "INR",
                RazorpayKeyId = "rzp_test_public",
                RazorpayKeySecret = "api-secret"
            }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.CreateOrderAsync(
                new GatewayOrderRequest(
                    Guid.NewGuid(), "DD-TEST", 100, "INR", DateTime.UtcNow.AddMinutes(15)),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("HTTP 400", exception.Message);
        Assert.Contains("Provider code: BAD_REQUEST_ERROR", exception.Message);
        Assert.Contains("Provider description: Invalid key_id value", exception.Message);
        Assert.DoesNotContain("do-not-return", exception.Message);
        Assert.DoesNotContain("api-secret", exception.Message);
    }

    [Fact]
    public async Task RazorpayCreateFailure_IsPersistedAndMappedToGenericBusinessRule()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":\"AUTHENTICATION_ERROR\",\"description\":\"Invalid API key\"}}")
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: new RazorpayPaymentGateway(client, options));

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.PaymentService.CreateAsync(
                harness.Customer.Id,
                new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
                "razorpay-401-create",
                CancellationToken.None));

        Assert.Equal("The payment gateway could not create the payment order.", exception.Message);
        var payment = await harness.Db.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal("GATEWAY_ORDER_FAILED", payment.FailureCode);
        Assert.Contains("HTTP 401", payment.FailureMessage);
        Assert.Contains("AUTHENTICATION_ERROR", payment.FailureMessage);
        Assert.DoesNotContain("api-secret", payment.FailureMessage);
    }

    [Fact]
    public async Task RazorpayGateway_NonJsonFailure_UsesStatusOnlyDiagnostic()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream failure with sensitive detail")
        });
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        var gateway = new RazorpayPaymentGateway(
            client,
            Options.Create(new PaymentOptions
            {
                Provider = "Razorpay",
                Currency = "INR",
                RazorpayKeyId = "rzp_test_public",
                RazorpayKeySecret = "api-secret"
            }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gateway.CreateOrderAsync(
                new GatewayOrderRequest(
                    Guid.NewGuid(), "DD-TEST", 100, "INR", DateTime.UtcNow.AddMinutes(15)),
                CancellationToken.None));

        Assert.Equal("Razorpay request failed with HTTP 502.", exception.Message);
        Assert.DoesNotContain("sensitive detail", exception.Message);
    }

    [Fact]
    public void RazorpayWebhook_WithoutWebhookSecret_FailsClosed()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            RazorpayWebhookSecret = null,
            MockSigningSecret = "unused"
        });
        var gateway = new RazorpayPaymentGateway(new HttpClient(), options);

        Assert.False(gateway.VerifyWebhookSignature("{}"u8, "any-signature"));
    }

    [Fact]
    public async Task RazorpayVerification_RequiresSignatureThenIndependentGatewayConfirmation()
    {
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        var gateway = new TestRazorpayGateway(options);
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: gateway);
        var created = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Razorpay),
            "payment-1",
            CancellationToken.None);

        var invalid = new VerifyPaymentRequest(
            created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "invalid");
        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.PaymentService.VerifyAsync(harness.Customer.Id, invalid, CancellationToken.None));

        var result = await harness.PaymentService.VerifyAsync(
            harness.Customer.Id,
            invalid with { Signature = "test_verified" },
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, result.Status);
        Assert.Equal($"pay_test_{created.PublicId:N}", result.GatewayPaymentId);
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.SingleAsync()).Status);
    }

    [Fact]
    public async Task PaymentReadsAreOwnershipScoped()
    {
        await using var harness = await PaymentHarness.CreateAsync();
        var payment = await harness.PaymentService.CreateAsync(
            harness.Customer.Id,
            new CreatePaymentRequest(harness.Order.PublicId, PaymentMethod.Development),
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
        var options = Options.Create(new PaymentOptions
        {
            Provider = "Razorpay",
            Currency = "INR",
            RazorpayKeyId = "rzp_test_public",
            RazorpayKeySecret = "api-secret",
            MockSigningSecret = "unused"
        });
        using var client = new HttpClient(new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("The invalid signature must be rejected before network access.")))
        {
            BaseAddress = new Uri("https://api.razorpay.com/v1/")
        };
        await using var harness = await PaymentHarness.CreateAsync(
            paymentOptions: options,
            gateway: new RazorpayPaymentGateway(client, options));
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

        public static async Task<PaymentHarness> CreateAsync(
            decimal payableAmount = 100m,
            IOptions<PaymentOptions>? paymentOptions = null,
            IPaymentGateway? gateway = null)
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

            var clock = new TestClock(new DateTime(2026, 8, 16, 7, 30, 0, DateTimeKind.Unspecified));
            paymentOptions ??= Options.Create(new PaymentOptions
            {
                Provider = "Mock",
                Currency = "INR",
                PaymentExpiryMinutes = 15,
                MockSigningSecret = "test-signing-secret"
            });
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var indiaTime = new TestIndiaTimeProvider(clock);
            var walletService = new WalletService(
                db,
                indiaTime,
                paymentOptions,
                notificationEventWriter);
            var paymentService = new PaymentService(
                db,
                gateway ?? new MockPaymentGateway(paymentOptions),
                walletService,
                indiaTime,
                paymentOptions,
                notificationEventWriter);
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

    private sealed class TestRazorpayGateway(IOptions<PaymentOptions> options) : IPaymentGateway
    {
        private readonly Dictionary<Guid, GatewayOrderRequest> orders = [];

        public string ProviderName => "Razorpay";
        public string? PublicKeyId => options.Value.RazorpayKeyId;
        public bool IsLive => true;

        public Task<GatewayOrderResult> CreateOrderAsync(
            GatewayOrderRequest request,
            CancellationToken cancellationToken)
        {
            orders[request.PaymentId] = request;
            return Task.FromResult(new GatewayOrderResult(
                $"order_test_{request.PaymentId:N}",
                "created",
                request.AmountMinor,
                request.Currency));
        }

        public bool VerifyPaymentSignature(
            string gatewayOrderId,
            string gatewayPaymentId,
            string signature) =>
            signature == "test_verified" &&
            TryGetPaymentId(gatewayPaymentId, out var paymentId) &&
            gatewayOrderId == $"order_test_{paymentId:N}" &&
            orders.ContainsKey(paymentId);

        public Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(
            string gatewayPaymentId,
            CancellationToken cancellationToken)
        {
            if (!TryGetPaymentId(gatewayPaymentId, out var paymentId) ||
                !orders.TryGetValue(paymentId, out var request))
            {
                throw new InvalidOperationException("The test payment was not created by this gateway.");
            }

            return Task.FromResult(new GatewayPaymentStatusResult(
                gatewayPaymentId,
                $"order_test_{paymentId:N}",
                "captured",
                request.AmountMinor,
                request.Currency,
                IsSuccessful: true,
                IsTerminalFailure: false));
        }

        public bool VerifyWebhookSignature(ReadOnlySpan<byte> payload, string signature) => false;

        public GatewayWebhookEvent ParseWebhook(ReadOnlySpan<byte> payload) =>
            throw new NotSupportedException();

        public Task<GatewayRefundResult> RefundAsync(
            string gatewayPaymentId,
            long amountMinor,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayRefundResult(
                $"refund_test_{idempotencyKey}",
                "processed",
                IsSuccessful: true,
                IsPending: false,
                FailureCode: null,
                FailureMessage: null));

        private static bool TryGetPaymentId(string gatewayPaymentId, out Guid paymentId)
        {
            const string prefix = "pay_test_";
            paymentId = Guid.Empty;
            return gatewayPaymentId.StartsWith(prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(gatewayPaymentId[prefix.Length..], "N", out paymentId);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }
}
