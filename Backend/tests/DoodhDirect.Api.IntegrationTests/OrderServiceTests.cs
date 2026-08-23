using System.Text.Json;
using DoodhDirect.Api.Serialization;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Orders;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class OrderServiceTests
{
    [Fact]
    public async Task PreviewAsync_UsesBackendPriceAndNearestEligibleBranch()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 2.5m);

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal(harness.NearBranch.PublicId, result.BranchId);
        Assert.Equal("NEAR", result.BranchCode);
        Assert.Equal(200m, result.Subtotal);
        Assert.Equal(0m, result.DiscountAmount);
        Assert.Equal(200m, result.PayableAmount);
        var line = Assert.Single(result.Items);
        Assert.Equal(80m, line.UnitPrice);
        Assert.Equal(200m, line.LineTotal);
    }

    [Fact]
    public async Task PreviewAsync_RejectsAddressOwnedByAnotherCustomer()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var otherCustomer = new User(UserType.Customer);
        otherCustomer.SetProfile("Other Customer");
        harness.Db.Users.Add(otherCustomer);
        await harness.Db.SaveChangesAsync();

        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.PreviewAsync(otherCustomer.Id, request, CancellationToken.None));

        Assert.Contains("address", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewAsync_RejectsQuantityAboveBranchCapacity()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.NearAvailability.Update(isAvailable: true, maxDailyQuantity: 1.5m);
        harness.FarAvailability.Update(isAvailable: true, maxDailyQuantity: 1.5m);
        await harness.Db.SaveChangesAsync();

        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 2m);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None));

        Assert.Contains("branch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreviewAsync_RejectsInactiveProduct()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Product.Deactivate();
        await harness.Db.SaveChangesAsync();

        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_PersistsPendingPaymentOrderAndHistoricalSnapshots()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1.25m);

        var result = await harness.Service.CreateAsync(
            harness.Customer.Id, request, " checkout-key ", CancellationToken.None);

        Assert.Equal(harness.NearBranch.PublicId, result.BranchId);
        Assert.Equal(OrderStatus.PendingPayment, result.Status);
        Assert.StartsWith("DD-202608200241", result.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(100m, result.Subtotal);
        Assert.Equal("Home", result.AddressLabel);
        Assert.Equal("Fresh Milk", Assert.Single(result.Items).ProductName);

        var stored = await harness.Db.Orders
            .Include(order => order.Items)
            .SingleAsync();
        Assert.Equal("checkout-key", stored.IdempotencyKey);
        Assert.Equal("Home", stored.AddressLabelSnapshot);
        Assert.Equal(80m, stored.Items.Single().UnitPrice);
        Assert.Equal(100m, stored.Items.Single().LineTotal);
        var expectedCreatedAt = new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified);
        Assert.Equal(expectedCreatedAt, stored.CreatedAt);
        Assert.Equal(expectedCreatedAt, result.CreatedAt);
    }

    [Fact]
    public async Task CustomerReadsAndCancellationAreOwnershipScoped()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "ownership-key", CancellationToken.None);

        var storedOrder = await harness.Db.Orders.SingleAsync(x => x.PublicId == order.PublicId);
        storedOrder.ConfirmPayment();
        await harness.Db.SaveChangesAsync();

        Assert.Single(await harness.Service.GetForCustomerAsync(harness.Customer.Id, CancellationToken.None));
        Assert.Empty(await harness.Service.GetForCustomerAsync(harness.OtherCustomer.Id, CancellationToken.None));

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetAsync(harness.OtherCustomer.Id, order.PublicId, false, CancellationToken.None));

        var cancelled = await harness.Service.CancelAsync(
            harness.Customer.Id, order.PublicId, CancellationToken.None);
        var expectedCancelledAt = new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Equal(expectedCancelledAt, cancelled.CancelledAt);
        Assert.Equal(DateTimeKind.Unspecified, cancelled.CancelledAt!.Value.Kind);

        var cancelledOrder = await harness.Db.Orders.SingleAsync(x => x.PublicId == order.PublicId);
        Assert.Equal(expectedCancelledAt, cancelledOrder.CancelledAt);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.CancelAsync(harness.Customer.Id, order.PublicId, CancellationToken.None));
    }

    [Fact]
    public void IndiaLocalDateTimeJsonConverter_EmitsSuffixFreeApplicationTime()
    {
        var value = new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified);
        var options = new JsonSerializerOptions();
        options.Converters.Add(
            new IndiaLocalDateTimeJsonConverter(
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")));

        var json = JsonSerializer.Serialize(value, options);
        var roundTrip = JsonSerializer.Deserialize<DateTime>(json, options);

        Assert.Equal("\"2026-08-20T02:41:00.000\"", json);
        Assert.Equal(DateTimeKind.Unspecified, roundTrip.Kind);
        Assert.Equal(value, roundTrip);
    }

    [Fact]
    public void IndiaLocalDateTimeJsonConverter_ConvertsUtcInputToIndiaLocal()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(
            new IndiaLocalDateTimeJsonConverter(
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")));

        var value = JsonSerializer.Deserialize<DateTime>(
            "\"2026-08-17T03:00:00.000Z\"", options);

        Assert.Equal(
            new DateTime(2026, 8, 17, 8, 30, 0, DateTimeKind.Unspecified),
            value);
        Assert.Equal(DateTimeKind.Unspecified, value.Kind);
    }

    [Fact]
    public void ApiJson_UsesOneIndiaLocalPolicyForEveryTimestampProperty()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new IndiaLocalDateTimeJsonConverter(
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")));
        var value = new TimestampContract(
            new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 20, 6, 21, 55, DateTimeKind.Unspecified),
            new DateTime(2026, 8, 20, 7, 0, 0, DateTimeKind.Unspecified));

        var json = JsonSerializer.Serialize(value, options);
        var roundTrip = JsonSerializer.Deserialize<TimestampContract>(json, options);

        Assert.NotNull(roundTrip);
        Assert.DoesNotContain("Z", json, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", json, StringComparison.Ordinal);
        Assert.Equal(value, roundTrip);
        Assert.Equal(DateTimeKind.Unspecified, roundTrip.ExpiresAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, roundTrip.ProcessedAt!.Value.Kind);
    }

    private sealed record TimestampContract(
        DateTime BusinessAt,
        DateTime ExpiresAt,
        DateTime? ProcessedAt);

    private sealed class OrderHarness : IAsyncDisposable
    {
        private OrderHarness(
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            CustomerAddress address,
            Product product,
            Branch nearBranch,
            ProductBranch nearAvailability,
            ProductBranch farAvailability,
            OrderService service)
        {
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Address = address;
            Product = product;
            NearBranch = nearBranch;
            NearAvailability = nearAvailability;
            FarAvailability = farAvailability;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public CustomerAddress Address { get; }
        public Product Product { get; }
        public Branch NearBranch { get; }
        public ProductBranch NearAvailability { get; }
        public ProductBranch FarAvailability { get; }
        public OrderService Service { get; }

        public CheckoutRequest Request(Guid addressId, Guid productId, decimal quantity) =>
            new(addressId, [new OrderItemRequest(productId, quantity)]);

        public static async Task<OrderHarness> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"order-tests-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var clock = new TestClock(new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified));
            var db = new DoodhDirectDbContext(options, new TestIndiaTimeProvider(clock));

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            db.Users.AddRange(customer, otherCustomer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            var product = new Product(0, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            category.Products.Add(product);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            var nearBranch = new Branch("NEAR", "Near Branch", "Bengaluru", "Karnataka", 12.9717m, 77.5947m);
            var farBranch = new Branch("FAR", "Far Branch", "Bengaluru", "Karnataka", 13.1000m, 77.7000m);

            db.ProductCategories.Add(category);
            db.CustomerAddresses.Add(address);
            db.Branches.AddRange(nearBranch, farBranch);
            await db.SaveChangesAsync();

            var nearAvailability = new ProductBranch(product.Id, nearBranch.Id, true, null);
            var farAvailability = new ProductBranch(product.Id, farBranch.Id, true, null);
            product.ProductBranches.Add(nearAvailability);
            nearBranch.ProductBranches.Add(nearAvailability);
            product.ProductBranches.Add(farAvailability);
            farBranch.ProductBranches.Add(farAvailability);
            db.ProductBranches.AddRange(nearAvailability, farAvailability);
            await db.SaveChangesAsync();

            var allocation = new BranchAllocationService(db);
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var timeProvider = new TestIndiaTimeProvider(clock);
            return new OrderHarness(
                db, customer, otherCustomer, address, product, nearBranch, nearAvailability,
                farAvailability, new OrderService(db, allocation, notificationEventWriter, timeProvider));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
