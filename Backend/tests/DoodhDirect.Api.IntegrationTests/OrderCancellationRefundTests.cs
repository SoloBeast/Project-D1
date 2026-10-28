using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.RefundReplacement;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.RefundReplacement;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class OrderCancellationRefundTests
{
    [Fact]
    public async Task PrePickup_FullRefundAtZeroDeduction()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-1");

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, result.Status);
        Assert.NotNull(result.Cancellation);
        Assert.NotNull(result.Cancellation.Refund);
        Assert.Equal("Completed", result.Cancellation.Refund.Status);
        Assert.Equal(order.PayableAmount, result.Cancellation.Refund.GrossBasis);
        Assert.Equal(0m, result.Cancellation.Refund.DeductionPercentage);
        Assert.Equal(0m, result.Cancellation.Refund.DeductionAmount);
        Assert.Equal(order.PayableAmount, result.Cancellation.Refund.RefundedAmount);

        Assert.Equal(order.PayableAmount, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(order.PayableAmount, payment.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        var stored = await harness.Db.OrderCancellations.AsNoTracking().SingleAsync();
        Assert.Equal(0m, stored.AppliedDeductionPercentage);
        Assert.Equal(order.PayableAmount, stored.RefundAmount);
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "ORDER.CANCEL_REFUND").ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.OrderCancelRefunded).ToListAsync());
    }

    [Fact]
    public async Task PostPickup_WithoutConfirmation_IsRejectedWithoutChanges()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o3-1");
        await harness.AssignAndPickupAsync(order.PublicId);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));
        Assert.Contains("confirmation", error.Message, StringComparison.OrdinalIgnoreCase);

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.Assigned, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(DeliveryStatus.PickedUp, (await harness.Db.Deliveries.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PostPickup_WithConfirmation_AppliesConfiguredDeduction()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        await harness.SetDeductionPercentageAsync("25");
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o3-2");
        await harness.AssignAndPickupAsync(order.PublicId);

        var preview = await harness.Service.PreviewCancelAsync(harness.Customer.Id, order.PublicId, CancellationToken.None);
        Assert.True(preview.CanCancel);
        Assert.True(preview.PickupOccurred);
        Assert.True(preview.RequiresConfirmation);
        Assert.Equal(25m, preview.DeductionPercentage);
        var expectedDeduction = decimal.Round(order.PayableAmount * 25m / 100m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedDeduction, preview.DeductionAmount);
        Assert.Equal(order.PayableAmount - expectedDeduction, preview.EstimatedRefund);

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId,
            new CancelOrderRequest("ChangedMind", null, true, preview.DeductionPercentage, preview.EstimatedRefund),
            CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, result.Status);
        Assert.Equal(25m, result.Cancellation!.Refund!.DeductionPercentage);
        Assert.Equal(expectedDeduction, result.Cancellation.Refund.DeductionAmount);
        Assert.Equal(order.PayableAmount - expectedDeduction, result.Cancellation.Refund.RefundedAmount);
        Assert.Equal(order.PayableAmount - expectedDeduction, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
        Assert.Equal(DeliveryStatus.Cancelled, (await harness.Db.Deliveries.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task FrozenPercentage_SurvivesLaterConfigChange()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        await harness.SetDeductionPercentageAsync("25");
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o3-3");
        await harness.AssignAndPickupAsync(order.PublicId);
        var preview = await harness.Service.PreviewCancelAsync(harness.Customer.Id, order.PublicId, CancellationToken.None);
        await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId,
            new CancelOrderRequest("ChangedMind", null, true, preview.DeductionPercentage, preview.EstimatedRefund),
            CancellationToken.None);

        await harness.SetDeductionPercentageAsync("40");
        harness.Db.ChangeTracker.Clear();
        var reread = await harness.Service.GetAsync(harness.Customer.Id, order.PublicId, false, CancellationToken.None);

        Assert.Equal(25m, reread.Cancellation!.Refund!.DeductionPercentage);
        Assert.Equal(preview.DeductionAmount, reread.Cancellation.Refund.DeductionAmount);
        Assert.Equal(preview.EstimatedRefund, reread.Cancellation.Refund.RefundedAmount);
    }

    [Fact]
    public async Task Preview_DoesNotMutateOrMoveMoney()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-2");

        var preview = await harness.Service.PreviewCancelAsync(harness.Customer.Id, order.PublicId, CancellationToken.None);

        Assert.True(preview.CanCancel);
        Assert.False(preview.PickupOccurred);
        Assert.False(preview.RequiresConfirmation);
        Assert.Equal(0m, preview.DeductionPercentage);
        Assert.Equal(order.PayableAmount, preview.EstimatedRefund);
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task DeliveredOrder_CannotCancel()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-3");
        await harness.CompleteDeliveryAsync(order.PublicId);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));
        Assert.Contains("delivered", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PendingPayment_BlocksCancellation()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedPendingPaymentAsync(order.PublicId, order.PayableAmount, "pay-pending-1");

        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));

        harness.Db.ChangeTracker.Clear();
        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task FailedDeliveryState_BlocksCancellation()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-4");
        await harness.FailDeliveryAsync(order.PublicId);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SplitPayments_AllocatedWithinCaps()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        // Payable is 80: wallet covers 30, Razorpay covers 60 (10 overpaid buffer).
        await harness.SeedSuccessPaymentAsync(order.PublicId, 30m, PaymentMethod.Wallet, "pay-split-w");
        await harness.SeedSuccessPaymentAsync(order.PublicId, 60m, PaymentMethod.Razorpay, "pay-split-r");

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("DuplicateOrder", null), CancellationToken.None);

        Assert.Equal(80m, result.Cancellation!.Refund!.RefundedAmount);
        var walletPayment = await harness.Db.Payments.AsNoTracking().SingleAsync(x => x.Method == PaymentMethod.Wallet);
        var gatewayPayment = await harness.Db.Payments.AsNoTracking().SingleAsync(x => x.Method == PaymentMethod.Razorpay);
        Assert.Equal(30m, walletPayment.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, walletPayment.Status);
        Assert.Equal(50m, gatewayPayment.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, gatewayPayment.Status);
        Assert.Equal(80m, (await harness.Db.Wallets.AsNoTracking().SingleAsync()).Balance);
    }

    [Fact]
    public async Task PendingTicket_BlocksCancellation()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-5");
        await harness.MaterializeDeliveryAsync(order.PublicId);
        await harness.SeedPendingTicketAsync(order.PublicId);

        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CompletedSettlement_BlocksDuplicateRefund()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-6");
        await harness.MaterializeDeliveryAsync(order.PublicId);
        await harness.SeedCompletedTicketSettlementAsync(order.PublicId);

        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRequests_ProduceSingleRecordAndCredit()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-7");

        var attempts = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await using var db = harness.CreateContext();
            var service = harness.CreateService(db);
            return await service.CancelAsync(
                harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None);
        })).ToArray();
        var results = await Task.WhenAll(attempts);

        Assert.All(results, r => Assert.Equal(OrderStatus.Cancelled, r.Status));
        Assert.Equal(1, await harness.Db.OrderCancellations.AsNoTracking().CountAsync());
        Assert.Equal(order.PayableAmount, await harness.Db.Wallets.AsNoTracking().SumAsync(x => x.Balance));
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Retry_DoesNotDuplicateMoneyOrRecords()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-8");

        var first = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None);
        var second = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None);

        Assert.Equal(first.Cancellation!.Refund!.RefundedAmount, second.Cancellation!.Refund!.RefundedAmount);
        Assert.Equal(1, await harness.Db.OrderCancellations.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.WalletTransactions.AsNoTracking().CountAsync());
        Assert.Equal(1, await harness.Db.Refunds.AsNoTracking().CountAsync());
        Assert.Equal(order.PayableAmount, await harness.Db.Wallets.AsNoTracking().SumAsync(x => x.Balance));
        Assert.Single(await harness.Db.AuditLogs.AsNoTracking()
            .Where(x => x.Action == "ORDER.CANCEL_REFUND").ToListAsync());
        Assert.Single(await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.OrderCancelRefunded).ToListAsync());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", "   ")]
    [InlineData("NotAReason", null)]
    [InlineData("Other", null)]
    public async Task InvalidReason_RejectedWithoutChanges(string? reason, string? comment)
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-9");

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest(reason, comment), CancellationToken.None));
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PostCancel_DeliveryCannotComplete()
    {
        await using var harness = await CancelRefundHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.SeedSuccessPaymentAsync(order.PublicId, order.PayableAmount, PaymentMethod.Wallet, "pay-o2-10");
        await harness.MaterializeDeliveryAsync(order.PublicId);

        await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var delivery = await harness.Db.Deliveries.SingleAsync();
        var storedOrder = await harness.Db.Orders.SingleAsync();
        Assert.Throws<InvalidOperationException>(() => delivery.Complete(harness.Employee.Id, harness.TimeProvider.Now, null));
        Assert.Throws<InvalidOperationException>(() => storedOrder.MarkDelivered());
    }

    private sealed class CancelRefundHarness : IAsyncDisposable
    {
        private readonly SqliteConnection keeper;
        private readonly string connectionString;

        private CancelRefundHarness(SqliteConnection keeper, string connectionString, DoodhDirectDbContext db,
            User customer, User staff, User employee, Branch branch, Product product, CustomerAddress address,
            TestClock clock, TestIndiaTimeProvider timeProvider, OrderService service)
        {
            this.keeper = keeper;
            this.connectionString = connectionString;
            Db = db;
            Customer = customer;
            Staff = staff;
            Employee = employee;
            Branch = branch;
            Product = product;
            Address = address;
            Clock = clock;
            TimeProvider = timeProvider;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User Staff { get; }
        public User Employee { get; }
        public Branch Branch { get; }
        public Product Product { get; }
        public CustomerAddress Address { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public OrderService Service { get; }

        public static async Task<CancelRefundHarness> CreateAsync()
        {
            var clock = new TestClock(new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"order-cancel-refund-tests-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 10
            }.ToString();
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(10))
                .Options;
            var db = new DoodhDirectDbContext(options, timeProvider);
            await db.Database.EnsureCreatedAsync();

            db.NumberSeries.Add(new NumberSeries(
                "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NEAR"));
            db.NumberSeries.Add(new NumberSeries(
                "DELIVERY", "Delivery Number", "DLV/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NEAR"));

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var staff = new User(UserType.Employee);
            staff.SetProfile("Staff");
            var employee = new User(UserType.Employee);
            employee.SetProfile("Rider");
            db.Users.AddRange(customer, staff, employee);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            var product = new Product(0, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            category.Products.Add(product);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            var branch = new Branch("NEAR", "Near Branch", "Bengaluru", "Karnataka", 12.9717m, 77.5947m);
            db.ProductCategories.Add(category);
            db.CustomerAddresses.Add(address);
            db.Branches.Add(branch);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, null));
            await db.SaveChangesAsync();

            var service = CreateService(db, timeProvider, clock);
            return new CancelRefundHarness(keeper, connectionString, db, customer, staff, employee, branch, product, address, clock, timeProvider, service);
        }

        public DoodhDirectDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(10))
                .Options;
            return new DoodhDirectDbContext(options, TimeProvider);
        }

        public OrderService CreateService(DoodhDirectDbContext context) =>
            CreateService(context, TimeProvider, Clock);

        private static OrderService CreateService(DoodhDirectDbContext context, TestIndiaTimeProvider timeProvider, TestClock clock) =>
            new(context,
                new BranchAllocationService(context),
                new TestNotificationEventWriter(context, clock),
                new NumberSeriesService(context, timeProvider),
                timeProvider,
                new RefundReplacementConfigurationService(context, timeProvider));

        public async Task<OrderResult> CreateConfirmedOrderAsync()
        {
            var created = await Service.CreateAsync(
                Customer.Id,
                new CheckoutRequest(Address.PublicId, null, [new OrderItemRequest(Product.PublicId, 1m)]),
                $"key-{Guid.NewGuid():N}", CancellationToken.None);
            var stored = await Db.Orders.SingleAsync(x => x.PublicId == created.PublicId);
            stored.ConfirmPayment();
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return await Service.GetAsync(Customer.Id, created.PublicId, false, CancellationToken.None);
        }

        public async Task<Payment> SeedSuccessPaymentAsync(Guid orderPublicId, decimal amount, PaymentMethod method, string key)
        {
            var order = await Db.Orders.AsNoTracking().SingleAsync(x => x.PublicId == orderPublicId);
            var payment = new Payment(order.Id, Customer.Id, method, amount, "INR", key, TimeProvider.Now.AddDays(1));
            payment.Succeed(method == PaymentMethod.Wallet ? null : "pay_test_123", "captured", TimeProvider.Now);
            Db.Payments.Add(payment);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return payment;
        }

        public async Task SeedPendingPaymentAsync(Guid orderPublicId, decimal amount, string key)
        {
            var order = await Db.Orders.AsNoTracking().SingleAsync(x => x.PublicId == orderPublicId);
            Db.Payments.Add(new Payment(order.Id, Customer.Id, PaymentMethod.Razorpay, amount, "INR", key, TimeProvider.Now.AddDays(1)));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task<Delivery> MaterializeDeliveryAsync(Guid orderPublicId)
        {
            var order = await Db.Orders.AsNoTracking().SingleAsync(x => x.PublicId == orderPublicId);
            var delivery = Delivery.ForOrder(
                order.Id, Customer.Id, Branch.Id, DateOnly.FromDateTime(TimeProvider.Now),
                order.OrderNumber, "Customer", "9999999999",
                "1 Main Road, Central, Bengaluru, Karnataka 560001", null, 12.9716m, 77.5946m);
            delivery.AssignDeliveryNumber("DLV/NEAR/000001");
            Db.Deliveries.Add(delivery);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return delivery;
        }

        public async Task AssignAndPickupAsync(Guid orderPublicId)
        {
            var delivery = await MaterializeDeliveryAsync(orderPublicId);
            var tracked = await Db.Deliveries.SingleAsync(x => x.Id == delivery.Id);
            var order = await Db.Orders.SingleAsync(x => x.PublicId == orderPublicId);
            tracked.Assign(Employee.Id, Staff.Id, TimeProvider.Now, "assign");
            order.AssignForDelivery();
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();

            tracked = await Db.Deliveries.SingleAsync(x => x.Id == delivery.Id);
            tracked.PickUp(Employee.Id, TimeProvider.Now, null);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task CompleteDeliveryAsync(Guid orderPublicId)
        {
            var delivery = await MaterializeDeliveryAsync(orderPublicId);
            var tracked = await Db.Deliveries.SingleAsync(x => x.Id == delivery.Id);
            var order = await Db.Orders.SingleAsync(x => x.PublicId == orderPublicId);
            tracked.Assign(Employee.Id, Staff.Id, TimeProvider.Now, "assign");
            order.AssignForDelivery();
            tracked.PickUp(Employee.Id, TimeProvider.Now, null);
            tracked.Start(Employee.Id, TimeProvider.Now);
            order.StartDelivery();
            tracked.Arrive(Employee.Id, TimeProvider.Now);
            tracked.RecordOtpVerified(Employee.Id, TimeProvider.Now);
            tracked.Complete(Employee.Id, TimeProvider.Now, null);
            order.MarkDelivered();
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task FailDeliveryAsync(Guid orderPublicId)
        {
            var delivery = await MaterializeDeliveryAsync(orderPublicId);
            var tracked = await Db.Deliveries.SingleAsync(x => x.Id == delivery.Id);
            var order = await Db.Orders.SingleAsync(x => x.PublicId == orderPublicId);
            tracked.Assign(Employee.Id, Staff.Id, TimeProvider.Now, "assign");
            order.AssignForDelivery();
            tracked.PickUp(Employee.Id, TimeProvider.Now, null);
            tracked.Fail(Employee.Id, TimeProvider.Now, DeliveryFailureReasons.CustomerNotAvailable, null, null, null);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SetDeductionPercentageAsync(string value)
        {
            var existing = await Db.SystemConfigurations.SingleOrDefaultAsync(
                x => x.Key == RefundReplacementConfigurationKeys.DeliveryFailDeductionPercentage);
            if (existing is null)
            {
                Db.SystemConfigurations.Add(new SystemConfiguration(
                    RefundReplacementConfigurationKeys.DeliveryFailDeductionPercentage, value, "decimal"));
            }
            else
            {
                existing.UpdateValue(value, TimeProvider.Now);
            }
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SeedPendingTicketAsync(Guid orderPublicId)
        {
            var order = await Db.Orders.AsNoTracking().SingleAsync(x => x.PublicId == orderPublicId);
            var delivery = await Db.Deliveries.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
            var ticket = new RefundReplacementRequest(
                order.Id, delivery.Id, Customer.Id, Branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery,
                "Item was not satisfactory", null, TimeProvider.Now, TimeProvider.Now.AddHours(48));
            ticket.AssignRequestNumber($"RR-O2/{Guid.NewGuid():N}");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SeedCompletedTicketSettlementAsync(Guid orderPublicId)
        {
            var order = await Db.Orders.AsNoTracking().SingleAsync(x => x.PublicId == orderPublicId);
            var delivery = await Db.Deliveries.AsNoTracking().SingleAsync(x => x.OrderId == order.Id);
            var now = TimeProvider.Now;
            var ticket = new RefundReplacementRequest(
                order.Id, delivery.Id, Customer.Id, Branch.Id, null,
                RefundReplacementType.Refund, RefundReplacementSource.CustomerPostDelivery,
                "Item was not satisfactory", null, now, now.AddHours(48));
            ticket.AssignRequestNumber($"RR-O2/{Guid.NewGuid():N}");
            ticket.RecordDeductionDecision(true, 10m, null);
            ticket.Approve(Staff.Id, now, "Approved with deduction.");
            ticket.Complete(Staff.Id, now, "Settled.");
            Db.RefundReplacementRequests.Add(ticket);
            await Db.SaveChangesAsync();

            var gross = order.PayableAmount;
            var deduction = decimal.Round(gross * 10m / 100m, 2, MidpointRounding.AwayFromZero);
            var final = gross - deduction;
            var key = $"one-time-refund:{ticket.PublicId:N}:wallet";
            var payment = await Db.Payments.Include(x => x.Refunds).SingleAsync();
            var refund = payment.StartRefund(final, "Ticket settlement", key, Customer.Id);
            refund.Succeed(now);
            payment.CompleteRefund(final);
            var wallet = new Wallet(Customer.Id, "INR");
            wallet.Credit(WalletTransactionType.RefundCredit, final, key, "Ticket settlement", now, payment.Id, order.Id);
            Db.Wallets.Add(wallet);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await keeper.DisposeAsync();
        }
    }
}
