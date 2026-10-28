using DoodhDirect.Application.Common;
using DoodhDirect.Application.Orders;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class OrderCancellationReasonTests
{
    [Fact]
    public async Task Cancel_WithReasonOnly_IsAcceptedAndPersisted()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, result.Status);
        Assert.NotNull(result.Cancellation);
        Assert.Equal("ChangedMind", result.Cancellation.ReasonCode);
        Assert.Null(result.Cancellation.Comment);

        var stored = await harness.Db.OrderCancellations.AsNoTracking().SingleAsync();
        Assert.Equal("ChangedMind", stored.ReasonCode);
        Assert.Null(stored.Comment);
        Assert.Equal(harness.Customer.Id, stored.CancelledByUserId);
    }

    [Fact]
    public async Task Cancel_WithCommentOnly_IsAcceptedAsOther()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest(null, "  driver was rude  "), CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, result.Status);
        Assert.NotNull(result.Cancellation);
        Assert.Equal("Other", result.Cancellation.ReasonCode);
        Assert.Equal("driver was rude", result.Cancellation.Comment);
    }

    [Fact]
    public async Task Cancel_WithReasonAndComment_IsPersistedWithTrimming()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        var result = await harness.Service.CancelAsync(
            harness.Customer.Id,
            order.PublicId,
            new CancelOrderRequest("DuplicateOrder", "  ordered twice by mistake  "),
            CancellationToken.None);

        Assert.Equal("DuplicateOrder", result.Cancellation!.ReasonCode);
        Assert.Equal("ordered twice by mistake", result.Cancellation.Comment);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", "   ")]
    [InlineData("NotAReason", null)]
    [InlineData("Other", null)]
    [InlineData("Other", "   ")]
    public async Task Cancel_WithMissingOrInvalidReason_IsRejected(string? reason, string? comment)
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest(reason, comment), CancellationToken.None));

        Assert.Equal(OrderStatus.Confirmed, (await harness.Db.Orders.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancel_WithOverlongComment_IsRejected()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CancelAsync(
            harness.Customer.Id,
            order.PublicId,
            new CancelOrderRequest("ChangedMind", new string('x', 501)),
            CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_ByOtherCustomer_IsRejected()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.CancelAsync(
            harness.OtherCustomer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_IneligibleStatus_IsRejected()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            "pending-key",
            CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", null), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_Repeat_DoesNotDuplicateRecords()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        var first = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("ChangedMind", "first"), CancellationToken.None);
        var second = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("DuplicateOrder", "second"), CancellationToken.None);

        Assert.Equal(OrderStatus.Cancelled, second.Status);
        Assert.Equal("ChangedMind", second.Cancellation!.ReasonCode);
        Assert.Equal("first", second.Cancellation.Comment);
        Assert.Single(await harness.Db.OrderCancellations.AsNoTracking().ToListAsync());
        Assert.Single(
            await harness.Db.AuditLogs.AsNoTracking().Where(x => x.Action == "ORDER.CANCEL").ToListAsync());
        Assert.Equal(first.Cancellation!.CancelledAt, second.Cancellation.CancelledAt);
    }

    [Fact]
    public async Task Cancel_DoesNotInitiateRefundOrTouchPayments()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();

        await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("OrderedByMistake", null), CancellationToken.None);

        Assert.Empty(await harness.Db.Refunds.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Payments.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Wallets.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancel_ReasonVisible_InOwnHistoryAndDetail()
    {
        await using var harness = await OrderReasonHarness.CreateAsync();
        var order = await harness.CreateConfirmedOrderAsync();
        await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, new CancelOrderRequest("DeliveryTimeNoLongerSuitable", "evening works better"), CancellationToken.None);

        var detail = await harness.Service.GetAsync(harness.Customer.Id, order.PublicId, false, CancellationToken.None);
        Assert.Equal("DeliveryTimeNoLongerSuitable", detail.Cancellation!.ReasonCode);
        Assert.Equal("evening works better", detail.Cancellation.Comment);

        var mine = await harness.Service.GetForCustomerAsync(harness.Customer.Id, CancellationToken.None);
        Assert.Equal("DeliveryTimeNoLongerSuitable", Assert.Single(mine).Cancellation!.ReasonCode);
    }

    private sealed class OrderReasonHarness : IAsyncDisposable
    {
        private OrderReasonHarness(
            DoodhDirectDbContext db,
            Domain.Identity.User customer,
            Domain.Identity.User otherCustomer,
            Domain.Customer.CustomerAddress address,
            Domain.Catalogue.Product product,
            OrderService service)
        {
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Address = address;
            Product = product;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public Domain.Identity.User Customer { get; }
        public Domain.Identity.User OtherCustomer { get; }
        public Domain.Customer.CustomerAddress Address { get; }
        public Domain.Catalogue.Product Product { get; }
        public OrderService Service { get; }

        public CheckoutRequest Request(Guid addressId, Guid productId, decimal quantity) =>
            new(addressId, null, [new OrderItemRequest(productId, quantity)]);

        public async Task<OrderResult> CreateConfirmedOrderAsync()
        {
            var created = await Service.CreateAsync(
                Customer.Id, Request(Address.PublicId, Product.PublicId, 1m), $"key-{Guid.NewGuid():N}", CancellationToken.None);
            var stored = await Db.Orders.SingleAsync(x => x.PublicId == created.PublicId);
            stored.ConfirmPayment();
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return created;
        }

        public static async Task<OrderReasonHarness> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"order-cancel-reason-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var clock = new TestClock(new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var db = new DoodhDirectDbContext(options, timeProvider);
            db.NumberSeries.Add(new Domain.Setup.NumberSeries(
                "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, Domain.Setup.NumberSeriesResetPolicy.Never, "NEAR"));
            db.NumberSeries.Add(new Domain.Setup.NumberSeries(
                "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, Domain.Setup.NumberSeriesResetPolicy.Never, "FAR"));

            var customer = new Domain.Identity.User(Domain.Identity.UserType.Customer);
            customer.SetProfile("Customer");
            var otherCustomer = new Domain.Identity.User(Domain.Identity.UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            db.Users.AddRange(customer, otherCustomer);
            await db.SaveChangesAsync();

            var category = new Domain.Catalogue.ProductCategory("MILK", "Milk");
            category.Activate();
            var product = new Domain.Catalogue.Product(0, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            category.Products.Add(product);
            var address = new Domain.Customer.CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            var nearBranch = new Domain.Catalogue.Branch("NEAR", "Near Branch", "Bengaluru", "Karnataka", 12.9717m, 77.5947m);
            var farBranch = new Domain.Catalogue.Branch("FAR", "Far Branch", "Bengaluru", "Karnataka", 13.1000m, 77.7000m);
            db.ProductCategories.Add(category);
            db.CustomerAddresses.Add(address);
            db.Branches.AddRange(nearBranch, farBranch);
            await db.SaveChangesAsync();

            var nearAvailability = new Domain.Catalogue.ProductBranch(product.Id, nearBranch.Id, true, null);
            var farAvailability = new Domain.Catalogue.ProductBranch(product.Id, farBranch.Id, true, null);
            db.ProductBranches.AddRange(nearAvailability, farAvailability);
            await db.SaveChangesAsync();

            var service = new OrderService(
                db,
                new BranchAllocationService(db),
                new TestNotificationEventWriter(db, clock),
                new NumberSeriesService(db, timeProvider),
                timeProvider,
                new DoodhDirect.Infrastructure.RefundReplacement.RefundReplacementConfigurationService(db, timeProvider));
            return new OrderReasonHarness(db, customer, otherCustomer, address, product, service);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
