using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class OrderPlacedManagerNotificationTests
{
    [Fact]
    public async Task CreateAsync_NotifiesActiveDairyAndDeliveryManagers_ForAllocatedBranch()
    {
        await using var harness = await ManagerOrderHarness.CreateAsync();
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            "manager-notify-key-1",
            CancellationToken.None);

        var events = await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.OrderCreated)
            .ToListAsync();
        // 1 customer + dairy manager + delivery manager (distinct users).
        Assert.Equal(3, events.Count);
        Assert.Contains(events, x => x.UserId == harness.Customer.Id && x.EventKey == $"order:{order.PublicId:N}:created");
        var dairyEvent = Assert.Single(events, x => x.UserId == harness.DairyManager.Id);
        var deliveryEvent = Assert.Single(events, x => x.UserId == harness.DeliveryManager.Id);
        Assert.Equal($"order:{order.PublicId:N}:placed:staff:{harness.DairyManager.Id}", dairyEvent.EventKey);
        Assert.Equal($"order:{order.PublicId:N}:placed:staff:{harness.DeliveryManager.Id}", deliveryEvent.EventKey);
        Assert.Contains($"/staff/orders/{order.PublicId}", dairyEvent.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("device-token", dairyEvent.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_DoesNotNotifyManagersFromUnrelatedBranches_InactiveOrMissing()
    {
        await using var harness = await ManagerOrderHarness.CreateAsync(includeOtherBranchManager: true, includeInactiveManager: true);
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            "manager-notify-key-2",
            CancellationToken.None);

        var events = await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.OrderCreated)
            .ToListAsync();
        Assert.DoesNotContain(events, x => x.UserId == harness.OtherBranchManager!.Id);
        Assert.DoesNotContain(events, x => x.UserId == harness.InactiveManager!.Id);
        // Only customer + 2 active allocated managers.
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public async Task CreateAsync_SingleEmployeeHoldingBothRoles_ReceivesOnlyOneCopy()
    {
        await using var harness = await ManagerOrderHarness.CreateAsync(singleDualRoleManager: true);
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            "manager-notify-key-3",
            CancellationToken.None);

        var events = await harness.Db.NotificationEvents.AsNoTracking()
            .Where(x => x.EventType == NotificationEventTypes.OrderCreated)
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Single(events, x => x.UserId == harness.Customer.Id);
        Assert.Single(events, x => x.UserId == harness.DualRoleManager!.Id);
    }

    [Fact]
    public async Task CreateAsync_RetriedWithSameIdempotencyKey_DoesNotDuplicateManagerNotifications()
    {
        await using var harness = await ManagerOrderHarness.CreateAsync();
        var first = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            "manager-notify-retry-key",
            CancellationToken.None);
        var second = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            "manager-notify-retry-key",
            CancellationToken.None);

        Assert.Equal(first.PublicId, second.PublicId);
        var count = await harness.Db.NotificationEvents.CountAsync(x => x.EventType == NotificationEventTypes.OrderCreated);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task PreviewAsync_DoesNotEmitOrderPlacedNotifications()
    {
        await using var harness = await ManagerOrderHarness.CreateAsync();
        await harness.Service.PreviewAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            CancellationToken.None);

        Assert.Equal(0, await harness.Db.NotificationEvents.CountAsync());
    }

    private sealed class ManagerOrderHarness : IAsyncDisposable
    {
        private ManagerOrderHarness(
            DoodhDirectDbContext db,
            User customer,
            User dairyManager,
            User deliveryManager,
            User? otherBranchManager,
            User? inactiveManager,
            User? dualRoleManager,
            CustomerAddress address,
            Product product,
            Branch nearBranch,
            OrderService service)
        {
            Db = db;
            Customer = customer;
            DairyManager = dairyManager;
            DeliveryManager = deliveryManager;
            OtherBranchManager = otherBranchManager;
            InactiveManager = inactiveManager;
            DualRoleManager = dualRoleManager;
            Address = address;
            Product = product;
            NearBranch = nearBranch;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User DairyManager { get; }
        public User DeliveryManager { get; }
        public User? OtherBranchManager { get; }
        public User? InactiveManager { get; }
        public User? DualRoleManager { get; }
        public CustomerAddress Address { get; }
        public Product Product { get; }
        public Branch NearBranch { get; }
        public OrderService Service { get; }

        public CheckoutRequest Request(Guid addressId, Guid productId, decimal quantity) =>
            new(addressId, null, [new OrderItemRequest(productId, quantity)]);

        public static async Task<ManagerOrderHarness> CreateAsync(
            bool includeOtherBranchManager = false,
            bool includeInactiveManager = false,
            bool singleDualRoleManager = false)
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"manager-order-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var clock = new TestClock(new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified));
            var db = new DoodhDirectDbContext(options, new TestIndiaTimeProvider(clock));
            await new IdentitySeedService(db).SeedAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            db.NumberSeries.Add(new NumberSeries("ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NEAR"));
            db.NumberSeries.Add(new NumberSeries("ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "FAR"));

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            db.Users.Add(customer);

            var dairyManager = new User(UserType.Employee);
            dairyManager.SetProfile("Dairy Manager");
            var deliveryManager = new User(UserType.Employee);
            deliveryManager.SetProfile("Delivery Manager");
            User? dual = null;
            if (singleDualRoleManager)
            {
                dual = new User(UserType.Employee);
                dual.SetProfile("Dual Manager");
                db.Users.Add(dual);
            }
            else
            {
                db.Users.AddRange(dairyManager, deliveryManager);
            }

            User? other = null;
            if (includeOtherBranchManager)
            {
                other = new User(UserType.Employee);
                other.SetProfile("Other Branch Manager");
                db.Users.Add(other);
            }

            User? inactive = null;
            if (includeInactiveManager)
            {
                inactive = new User(UserType.Employee);
                inactive.SetProfile("Inactive Manager");
                db.Users.Add(inactive);
            }

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
            var farBranch = new Branch("FAR", "Far Branch", "Bengaluru", "Karnataka", 13.1m, 77.7m);
            db.ProductCategories.Add(category);
            db.CustomerAddresses.Add(address);
            db.Branches.AddRange(nearBranch, farBranch);
            await db.SaveChangesAsync();
            var availability = new ProductBranch(product.Id, nearBranch.Id, true, null);
            product.ProductBranches.Add(availability);
            nearBranch.ProductBranches.Add(availability);
            db.ProductBranches.Add(availability);
            await db.SaveChangesAsync();

            var roles = await db.Roles.ToDictionaryAsync(x => x.Code);
            void Assign(User user, string roleCode, long? branchId)
            {
                db.UserRoles.Add(new UserRole(user.Id, roles[roleCode].Id, branchId));
            }

            if (singleDualRoleManager)
            {
                Assign(dual!, AuthorizationCodes.DairyManager, nearBranch.Id);
                Assign(dual!, AuthorizationCodes.DeliveryManager, nearBranch.Id);
                // Keep non-null placeholders for assertions that don't apply in this mode.
                dairyManager = dual!;
                deliveryManager = dual!;
            }
            else
            {
                Assign(dairyManager, AuthorizationCodes.DairyManager, nearBranch.Id);
                Assign(deliveryManager, AuthorizationCodes.DeliveryManager, nearBranch.Id);
            }

            if (other is not null)
            {
                Assign(other, AuthorizationCodes.DairyManager, farBranch.Id);
            }

            if (inactive is not null)
            {
                Assign(inactive, AuthorizationCodes.DairyManager, nearBranch.Id);
                inactive.Deactivate();
            }

            await db.SaveChangesAsync();

            var allocation = new BranchAllocationService(db);
            var timeProvider = new TestIndiaTimeProvider(clock);
            var service = new OrderService(
                db,
                allocation,
                new TestNotificationEventWriter(db, clock),
                new NumberSeriesService(db, timeProvider),
                timeProvider,
                new DoodhDirect.Infrastructure.RefundReplacement.RefundReplacementConfigurationService(db, timeProvider));
            return new ManagerOrderHarness(db, customer, dairyManager, deliveryManager, other, inactive, dual, address, product, nearBranch, service);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
