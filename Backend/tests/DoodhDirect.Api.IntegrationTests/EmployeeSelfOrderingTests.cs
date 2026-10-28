using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 1 employee self-ordering: an active employee orders for themselves through
/// the existing customer-facing services, scoped strictly to their own identity.
/// </summary>
public sealed class EmployeeSelfOrderingTests
{
    [Fact]
    public async Task Employee_CanPreviewAndCreateOwnOrder()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        var request = harness.OrderRequest(harness.EmployeeAddress.PublicId, harness.Product.PublicId, 2m);

        var preview = await harness.Orders.PreviewAsync(harness.Employee.Id, request, CancellationToken.None);
        Assert.Equal(160m, preview.PayableAmount);

        var created = await harness.Orders.CreateAsync(
            harness.Employee.Id, request, "employee-order-key", CancellationToken.None);

        Assert.Equal(harness.Employee.Id, (await harness.Db.Orders.SingleAsync()).CustomerId);
        Assert.Equal("employee-order-key", (await harness.Db.Orders.SingleAsync()).IdempotencyKey);
        var history = await harness.Orders.GetForCustomerAsync(harness.Employee.Id, CancellationToken.None);
        Assert.Single(history);
    }

    [Fact]
    public async Task Employee_OrderHistoryIsScopedToOwnIdentity()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        await harness.Orders.CreateAsync(
            harness.Employee.Id,
            harness.OrderRequest(harness.EmployeeAddress.PublicId, harness.Product.PublicId, 1m),
            "employee-key-1",
            CancellationToken.None);
        await harness.Orders.CreateAsync(
            harness.Customer.Id,
            harness.OrderRequest(harness.CustomerAddress.PublicId, harness.Product.PublicId, 1m),
            "customer-key-1",
            CancellationToken.None);

        var employeeHistory = await harness.Orders.GetForCustomerAsync(harness.Employee.Id, CancellationToken.None);
        var customerHistory = await harness.Orders.GetForCustomerAsync(harness.Customer.Id, CancellationToken.None);

        var employeeOrder = Assert.Single(employeeHistory);
        Assert.DoesNotContain(customerHistory, x => x.PublicId == employeeOrder.PublicId);
        Assert.All(employeeHistory, x => Assert.DoesNotContain(customerHistory, y => y.PublicId == x.PublicId));
    }

    [Fact]
    public async Task Employee_CannotUseAnotherCustomersAddress()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        var request = harness.OrderRequest(harness.CustomerAddress.PublicId, harness.Product.PublicId, 1m);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Orders.PreviewAsync(harness.Employee.Id, request, CancellationToken.None));
        Assert.Contains("address", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Employee_CannotReadAnotherCustomersOrder()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        var created = await harness.Orders.CreateAsync(
            harness.Customer.Id,
            harness.OrderRequest(harness.CustomerAddress.PublicId, harness.Product.PublicId, 1m),
            "customer-key-2",
            CancellationToken.None);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Orders.GetAsync(harness.Employee.Id, created.PublicId, bypassOwnership: false, CancellationToken.None));
    }

    [Fact]
    public async Task Employee_CreateIsIdempotentOnRetry()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        var request = harness.OrderRequest(harness.EmployeeAddress.PublicId, harness.Product.PublicId, 1m);

        var first = await harness.Orders.CreateAsync(harness.Employee.Id, request, "retry-key", CancellationToken.None);
        var second = await harness.Orders.CreateAsync(harness.Employee.Id, request, "retry-key", CancellationToken.None);

        Assert.Equal(first.PublicId, second.PublicId);
        Assert.Equal(1, await harness.Db.Orders.CountAsync(x => x.CustomerId == harness.Employee.Id));
    }

    [Fact]
    public async Task InactiveEmployee_IsRejectedByWalletAndProfileGates()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        harness.Employee.Deactivate();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Wallets.GetAsync(harness.Employee.Id, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Customers.GetProfileAsync(harness.Employee.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DeactivatedEmployee_TokenScopeIsNoLongerCurrent()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        var validator = new AuthorizationScopeValidator(harness.Db);

        Assert.True(await validator.IsTokenScopeCurrentAsync(
            harness.Employee.Id, harness.Employee.AuthorizationVersion, CancellationToken.None));

        harness.Employee.Deactivate();
        harness.Employee.BumpAuthorizationVersion();
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        // A previously issued token carries the stale version and must fail
        // per-request revalidation even if presented before expiry.
        Assert.False(await validator.IsTokenScopeCurrentAsync(
            harness.Employee.Id, harness.Employee.AuthorizationVersion - 1, CancellationToken.None));
        Assert.False(await validator.IsTokenScopeCurrentAsync(
            harness.Employee.Id, harness.Employee.AuthorizationVersion, CancellationToken.None));
    }

    [Fact]
    public async Task DeactivatedEmployee_HistoryIsPreserved()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        await harness.Orders.CreateAsync(
            harness.Employee.Id,
            harness.OrderRequest(harness.EmployeeAddress.PublicId, harness.Product.PublicId, 1m),
            "history-key",
            CancellationToken.None);

        harness.Employee.Deactivate();
        await harness.Db.SaveChangesAsync();

        var history = await harness.Orders.GetForCustomerAsync(harness.Employee.Id, CancellationToken.None);
        Assert.Single(history);
        Assert.Equal(1, await harness.Db.Orders.CountAsync(x => x.CustomerId == harness.Employee.Id));
    }

    [Fact]
    public async Task Employee_CanTopUpOwnWallet_ThroughVerifyFlow()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();

        var created = await harness.Payments.CreateWalletTopUpAsync(
            harness.Employee.Id, 500m, "employee-topup-key", CancellationToken.None);
        Assert.Equal(PaymentStatus.Pending, created.Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());

        var verified = await harness.Payments.VerifyAsync(
            harness.Employee.Id,
            new VerifyPaymentRequest(
                created.PublicId, created.GatewayOrderId!, $"pay_test_{created.PublicId:N}", "test_verified"),
            CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, verified.Status);
        var wallet = await harness.Wallets.GetAsync(harness.Employee.Id, CancellationToken.None);
        Assert.Equal(500m, wallet.Balance);
        var replay = await harness.Payments.CreateWalletTopUpAsync(
            harness.Employee.Id, 999m, "employee-topup-key", CancellationToken.None);
        Assert.Equal(created.PublicId, replay.PublicId);
        Assert.Equal(500m, (await harness.Wallets.GetAsync(harness.Employee.Id, CancellationToken.None)).Balance);
    }

    [Fact]
    public async Task WalletGate_StillRejectsNonSelfOrderingUserTypes()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        var admin = new User(UserType.SystemAdministrator);
        admin.SetProfile("Admin");
        harness.Db.Users.Add(admin);
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Wallets.GetAsync(admin.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Employee_ProfileProvisioningIsRetrySafe()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();

        await harness.Customers.GetProfileAsync(harness.Employee.Id, CancellationToken.None);
        await harness.Customers.GetProfileAsync(harness.Employee.Id, CancellationToken.None);
        await harness.Wallets.GetAsync(harness.Employee.Id, CancellationToken.None);
        await harness.Wallets.GetAsync(harness.Employee.Id, CancellationToken.None);

        Assert.Equal(1, await harness.Db.CustomerProfiles.CountAsync(x => x.UserId == harness.Employee.Id));
        Assert.Equal(1, await harness.Db.Wallets.CountAsync(x => x.CustomerId == harness.Employee.Id));
        Assert.Empty(await harness.Db.CustomerProfiles.Where(x => x.UserId == harness.Customer.Id).ToListAsync());
    }

    [Fact]
    public async Task Employee_CanCreatePrepaidSubscriptionForSelf()
    {
        await using var harness = await SelfOrderingHarness.CreateAsync();
        var request = new CreateSubscriptionRequest(
            harness.Product.PublicId,
            harness.EmployeeAddress.PublicId,
            1m,
            new DateOnly(2026, 8, 24),
            [DayOfWeek.Monday, DayOfWeek.Wednesday],
            4,
            PaymentMethod.Razorpay);

        var created = await harness.Subscriptions.CreateAsync(
            harness.Employee.Id, request, "employee-sub-key", CancellationToken.None);

        Assert.Equal(harness.Employee.Id, (await harness.Db.Subscriptions.SingleAsync()).CustomerId);
        Assert.Contains(harness.CapturingPayments.Calls, x => x.CustomerId == harness.Employee.Id);
        var mine = await harness.Subscriptions.GetForCustomerAsync(harness.Employee.Id, CancellationToken.None);
        Assert.Single(mine);
        Assert.Equal(created.Subscription.PublicId, mine.Single().PublicId);
    }

    [Fact]
    public async Task Seed_GrantsSelfOrderingToEmployeeRoles_WithoutAdminAccess()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"self-order-seed-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new DoodhDirectDbContext(options);
        await new IdentitySeedService(db).SeedAsync(CancellationToken.None);

        var assignments = await db.RolePermissions
            .Select(x => new { RoleCode = x.Role.Code, PermissionCode = x.Permission.Code })
            .ToListAsync();

        foreach (var role in new[]
        {
            AuthorizationCodes.DeliveryStaff,
            AuthorizationCodes.DairyManager,
            AuthorizationCodes.DeliveryManager,
            AuthorizationCodes.CustomerSupport,
            AuthorizationCodes.Accountant,
        })
        {
            var codes = assignments.Where(x => x.RoleCode == role).Select(x => x.PermissionCode).ToHashSet();
            Assert.Contains(AuthorizationCodes.OrdersCreateOwn, codes);
            Assert.Contains(AuthorizationCodes.OrdersReadOwn, codes);
            Assert.Contains(AuthorizationCodes.OrdersCancelOwn, codes);
            Assert.Contains(AuthorizationCodes.SubscriptionsCreateOwn, codes);
            Assert.Contains(AuthorizationCodes.SubscriptionsReadOwn, codes);
            Assert.Contains(AuthorizationCodes.SubscriptionsManageOwn, codes);
            Assert.Contains(AuthorizationCodes.PaymentsCreateOwn, codes);
            Assert.Contains(AuthorizationCodes.PaymentsReadOwn, codes);
            Assert.Contains(AuthorizationCodes.WalletReadOwn, codes);
            Assert.Contains(AuthorizationCodes.WalletTopUpOwn, codes);
            // Stage 2 own-resource support access: own deliveries and tickets.
            Assert.Contains(AuthorizationCodes.DeliveriesReadOwn, codes);
            Assert.Contains(AuthorizationCodes.MilkTestsRequestOwn, codes);
            Assert.Contains(AuthorizationCodes.MilkTestsReadOwn, codes);
            Assert.Contains(AuthorizationCodes.MilkTestsDecideOwn, codes);
            Assert.Contains(AuthorizationCodes.RefundReplacementRequestOwn, codes);
            Assert.Contains(AuthorizationCodes.RefundReplacementReadOwn, codes);
            // No global access and no other-customer profile management: the new
            // grants are strictly own-identity. (Accountant keeps its pre-existing
            // WalletAdjust/PaymentsRefund duties; support keeps its branch ticket
            // powers — both out of scope and unchanged here.)
            Assert.DoesNotContain(AuthorizationCodes.GlobalAccess, codes);
            Assert.DoesNotContain(AuthorizationCodes.CustomerProfilesManage, codes);
        }
    }

    private sealed class SelfOrderingHarness : IAsyncDisposable
    {
        private SelfOrderingHarness(
            DoodhDirectDbContext db,
            User employee,
            User customer,
            CustomerAddress employeeAddress,
            CustomerAddress customerAddress,
            Product product,
            Branch branch,
            OrderService orders,
            CustomerService customers,
            WalletService wallets,
            PaymentService payments,
            SubscriptionService subscriptions,
            CapturingPaymentService capturingPayments)
        {
            Db = db;
            Employee = employee;
            Customer = customer;
            EmployeeAddress = employeeAddress;
            CustomerAddress = customerAddress;
            Product = product;
            Branch = branch;
            Orders = orders;
            Customers = customers;
            Wallets = wallets;
            Payments = payments;
            Subscriptions = subscriptions;
            CapturingPayments = capturingPayments;
        }

        public DoodhDirectDbContext Db { get; }
        public User Employee { get; }
        public User Customer { get; }
        public CustomerAddress EmployeeAddress { get; }
        public CustomerAddress CustomerAddress { get; }
        public Product Product { get; }
        public Branch Branch { get; }
        public OrderService Orders { get; }
        public CustomerService Customers { get; }
        public WalletService Wallets { get; }
        public PaymentService Payments { get; }
        public SubscriptionService Subscriptions { get; }
        public CapturingPaymentService CapturingPayments { get; }

        public CheckoutRequest OrderRequest(Guid addressId, Guid productId, decimal quantity) =>
            new(addressId, null, [new OrderItemRequest(productId, quantity)]);

        public async Task<long> PaymentIdAsync(Guid paymentPublicId) =>
            await Db.Payments.Where(x => x.PublicId == paymentPublicId).Select(x => x.Id).SingleAsync();

        public static async Task<SelfOrderingHarness> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"self-order-tests-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var clock = new TestClock(new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var db = new DoodhDirectDbContext(options, timeProvider);
            db.NumberSeries.Add(new NumberSeries(
                "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));
            db.NumberSeries.Add(new NumberSeries(
                "CUSTOMER", "Customer Number", "CUST/{NUMBER:0000}", 1, 1, NumberSeriesResetPolicy.Never));
            db.NumberSeries.Add(new NumberSeries(
                "SUB", "Subscription Number", "SUB/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "MAIN"));

            var employee = new User(UserType.Employee);
            employee.SetProfile("Delivery Staff");
            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            db.Users.AddRange(employee, customer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            var product = new Product(0, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            category.Products.Add(product);
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var employeeAddress = new CustomerAddress(
                employee.Id, "Home", "9 Staff Quarters", "Central", "Bengaluru", "Karnataka",
                "560001", "Staff", "9888888888", 12.9716m, 77.5946m);
            var customerAddress = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            db.ProductCategories.Add(category);
            db.Branches.Add(branch);
            db.CustomerAddresses.AddRange(employeeAddress, customerAddress);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            await db.SaveChangesAsync();

            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var numberSeries = new NumberSeriesService(db, timeProvider);
            var orders = new OrderService(db, new BranchAllocationService(db), notificationEventWriter, numberSeries, timeProvider, new DoodhDirect.Infrastructure.RefundReplacement.RefundReplacementConfigurationService(db, timeProvider));
            var customers = new CustomerService(
                db, numberSeries, timeProvider,
                new BankDetailProtector(new EphemeralDataProtectionProvider()));
            var paymentOptions = Options.Create(new PaymentOptions
            {
                Provider = "Razorpay",
                Currency = "INR",
                RazorpayKeyId = "rzp_test_public",
                RazorpayKeySecret = "api-secret",
                MockSigningSecret = "unused",
            });
            var wallets = new WalletService(db, timeProvider, paymentOptions, notificationEventWriter);
            var payments = new PaymentService(
                db,
                new StubRazorpayGateway(),
                wallets,
                timeProvider,
                paymentOptions,
                notificationEventWriter,
                mockGateway: null,
                oneTimeDeliveryCreator: null);
            var capturingPayments = new CapturingPaymentService(db, clock);
            var subscriptions = new SubscriptionService(
                db, new FixedBranchAllocation(branch), capturingPayments, timeProvider, notificationEventWriter,
                new NumberSeriesService(db, timeProvider));
            return new SelfOrderingHarness(
                db, employee, customer, employeeAddress, customerAddress, product, branch,
                orders, customers, wallets, payments, subscriptions, capturingPayments);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>
    /// Minimal Razorpay stub: creates test gateway orders, accepts the
    /// "test_verified" signature, and reports captured status — mirroring the
    /// shared TestRazorpayGateway behavior used by the payment test suite.
    /// </summary>
    private sealed class StubRazorpayGateway : IPaymentGateway
    {
        private readonly Dictionary<Guid, GatewayOrderRequest> orders = [];

        public string ProviderName => "Razorpay";

        public bool IsLive => true;

        public Task<GatewayOrderResult> CreateOrderAsync(
            GatewayOrderRequest request, CancellationToken cancellationToken)
        {
            orders[request.PaymentId] = request;
            return Task.FromResult(new GatewayOrderResult(
                $"order_test_{request.PaymentId:N}", "created", request.AmountMinor, request.Currency));
        }

        public Task<string?> GetPublicKeyIdAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("rzp_test_public");

        public Task<bool> VerifyPaymentSignatureAsync(
            string gatewayOrderId, string gatewayPaymentId, string signature, CancellationToken cancellationToken) =>
            Task.FromResult(signature == "test_verified");

        public Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(
            string gatewayPaymentId, CancellationToken cancellationToken)
        {
            var match = orders.SingleOrDefault(x => gatewayPaymentId == $"pay_test_{x.Key:N}");
            if (match.Value is null) throw new InvalidOperationException("The test payment was not created by this gateway.");
            return Task.FromResult(new GatewayPaymentStatusResult(
                gatewayPaymentId,
                $"order_test_{match.Key:N}",
                "captured",
                match.Value.AmountMinor,
                match.Value.Currency,
                IsSuccessful: true,
                IsTerminalFailure: false));
        }

        public Task<GatewayOrderPaymentsResult> GetPaymentsForOrderAsync(
            string gatewayOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayOrderPaymentsResult(gatewayOrderId, []));

        public Task<bool> VerifyWebhookSignatureAsync(ReadOnlySpan<byte> payload, string signature, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public GatewayWebhookEvent ParseWebhook(ReadOnlySpan<byte> payload) =>
            throw new NotSupportedException();

        public Task<GatewayRefundResult> RefundAsync(
            string gatewayPaymentId, long amountMinor, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedBranchAllocation(Branch branch) : IBranchAllocationService
    {
        public Task<BranchAllocationResult> AllocateAsync(
            decimal latitude,
            decimal longitude,
            IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BranchAllocationResult(branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
    }

    private sealed class CapturingPaymentService(DoodhDirectDbContext db, TestClock clock) : IPaymentService
    {
        public List<(long CustomerId, long SubscriptionId, PaymentMethod Method, string IdempotencyKey)> Calls { get; } = [];

        public async Task<PaymentResult> CreateForSubscriptionAsync(
            long customerId,
            long subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls.Add((customerId, subscriptionId, method, idempotencyKey));
            var subscription = await db.Subscriptions
                .AsNoTracking()
                .SingleAsync(x => x.Id == subscriptionId, cancellationToken);
            return new PaymentResult(
                Guid.NewGuid(),
                null,
                null,
                null,
                method,
                "Razorpay",
                PaymentStatus.Pending,
                subscription.PayableAmount,
                0m,
                "INR",
                $"order_{subscription.PublicId:N}",
                null,
                "rzp_test",
                null,
                null,
                clock.UtcNow.AddMinutes(15),
                null,
                clock.UtcNow,
                subscription.PublicId);
        }

        public Task<PaymentResult> CreateAsync(
            long customerId, CreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> RetrySubscriptionAsync(
            long customerId, Guid subscriptionId, PaymentMethod method, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentPlanResult> CreateOrderPlanAsync(
            long customerId, Guid orderId, bool useWalletBalance, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentPlanResult> CreateSubscriptionPlanAsync(
            long customerId, long subscriptionId, bool useWalletBalance, string idempotencyKey, CancellationToken cancellationToken, bool expirePriorAttempts = false) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CreateWalletTopUpAsync(
            long customerId, decimal amount, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> VerifyAsync(
            long customerId, VerifyPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CompleteDevelopmentAsync(
            long customerId, Guid paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> CancelAsync(
            long customerId, Guid paymentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentResult> GetAsync(
            long userId, Guid paymentId, bool bypassOwnership, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RefundResult> RefundAsync(
            long requestedByUserId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PaymentReconciliationResult> ReconcileAsync(
            long requestedByUserId, Guid paymentId, bool bypassOwnership, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ProcessWebhookAsync(byte[] payload, string signature, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
