using System.Security.Claims;
using DoodhDirect.Api.Authorization;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.Orders;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class StaffOrderAccessRegressionTests
{
    [Fact]
    public void StaffOrderPolicy_AllowsBranchOrAdminRead_AndDeniesOwnOnly()
    {
        Assert.StartsWith("AnyPermission:", AuthorizationPolicyNames.AnyStaffOrderRead, StringComparison.Ordinal);
        Assert.Contains(AuthorizationCodes.OrdersReadBranch, AuthorizationPolicyNames.AnyStaffOrderRead, StringComparison.Ordinal);
        Assert.Contains(AuthorizationCodes.OrdersRead, AuthorizationPolicyNames.AnyStaffOrderRead, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthorizationCodes.OrdersReadOwn, AuthorizationPolicyNames.AnyStaffOrderRead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnyStaffOrderRead_AllowsEitherBranchOrAdminClaim()
    {
        var provider = new DoodhDirectAuthorizationPolicyProvider(
            Microsoft.Extensions.Options.Options.Create(new AuthorizationOptions()));
        var policy = await provider.GetPolicyAsync(AuthorizationPolicyNames.AnyStaffOrderRead);
        Assert.NotNull(policy);
        var requirement = Assert.IsType<AnyPermissionRequirement>(
            Assert.Single(policy.Requirements.OfType<AnyPermissionRequirement>()));

        var branchContext = CreateAuthContext(requirement, AuthorizationCodes.OrdersReadBranch);
        await new AnyPermissionAuthorizationHandler().HandleAsync(branchContext);
        Assert.True(branchContext.HasSucceeded);

        var adminContext = CreateAuthContext(requirement, AuthorizationCodes.OrdersRead);
        await new AnyPermissionAuthorizationHandler().HandleAsync(adminContext);
        Assert.True(adminContext.HasSucceeded);

        var ownOnlyContext = CreateAuthContext(requirement, AuthorizationCodes.OrdersReadOwn);
        await new AnyPermissionAuthorizationHandler().HandleAsync(ownOnlyContext);
        Assert.False(ownOnlyContext.HasSucceeded);
    }

    [Fact]
    public async Task GetForBranchAsync_GlobalAdminCanOpenBranchOrder_WhileOutOfBranchStaffAndUnrelatedCustomerCannot()
    {
        await using var harness = await StaffOrderHarness.CreateAsync();
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m),
            "staff-order-access-key",
            CancellationToken.None);

        // Authorized global administrator (System Administrator with global access,
        // as persisted in IdentitySeedService) can open the branch order.
        var global = await harness.Service.GetForBranchAsync(
            new OrderActor(9001, new HashSet<long>(), HasGlobalAccess: true),
            order.PublicId,
            CancellationToken.None);
        Assert.Equal(order.OrderNumber, global.OrderNumber);

        // Branch-scoped staff from the allocated branch can open it.
        var inBranch = await harness.Service.GetForBranchAsync(
            new OrderActor(9002, new HashSet<long> { harness.NearBranch.Id }),
            order.PublicId,
            CancellationToken.None);
        Assert.Equal(order.OrderNumber, inBranch.OrderNumber);

        // Branch-scoped staff from an unrelated branch cannot open it.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetForBranchAsync(
                new OrderActor(9003, new HashSet<long> { harness.NearBranch.Id + 1000 }),
                order.PublicId,
                CancellationToken.None));

        // An unrelated customer cannot open it through the ownership-checked path.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetAsync(harness.OtherCustomer.Id, order.PublicId, bypassOwnership: false, CancellationToken.None));
    }

    [Fact]
    public async Task IdentitySeed_GrantsSystemAdminBranchOrderRead()
    {
        await using var harness = await StaffOrderHarness.CreateAsync();
        var role = await harness.Db.Roles.SingleAsync(x => x.Code == AuthorizationCodes.SystemAdmin);
        var permission = await harness.Db.Permissions.SingleAsync(x => x.Code == AuthorizationCodes.OrdersReadBranch);
        var seeded = await harness.Db.RolePermissions.AnyAsync(x => x.RoleId == role.Id && x.PermissionId == permission.Id);
        Assert.True(seeded);
    }

    private static AuthorizationHandlerContext CreateAuthContext(Microsoft.AspNetCore.Authorization.IAuthorizationRequirement requirement, string permission)
    {
        var identity = new ClaimsIdentity(
            [new Claim(AuthorizationCodes.PermissionClaim, permission)], authenticationType: "Test");
        var principal = new ClaimsPrincipal(identity);
        return new AuthorizationHandlerContext([requirement], principal, resource: null);
    }

    private sealed class StaffOrderHarness : IAsyncDisposable
    {
        private StaffOrderHarness(
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            CustomerAddress address,
            Product product,
            Branch nearBranch,
            OrderService service)
        {
            Db = db;
            Customer = customer;
            OtherCustomer = otherCustomer;
            Address = address;
            Product = product;
            NearBranch = nearBranch;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public CustomerAddress Address { get; }
        public Product Product { get; }
        public Branch NearBranch { get; }
        public OrderService Service { get; }

        public CheckoutRequest Request(Guid addressId, Guid productId, decimal quantity) =>
            new(addressId, null, [new OrderItemRequest(productId, quantity)]);

        public static async Task<StaffOrderHarness> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"staff-order-access-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var clock = new TestClock(new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified));
            var db = new DoodhDirectDbContext(options, new TestIndiaTimeProvider(clock));
            var seed = new DoodhDirect.Infrastructure.Identity.IdentitySeedService(db);
            await seed.SeedAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            db.NumberSeries.Add(new NumberSeries(
                "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NEAR"));
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
            db.ProductCategories.Add(category);
            db.CustomerAddresses.Add(address);
            db.Branches.Add(nearBranch);
            await db.SaveChangesAsync();
            var availability = new ProductBranch(product.Id, nearBranch.Id, true, null);
            product.ProductBranches.Add(availability);
            nearBranch.ProductBranches.Add(availability);
            db.ProductBranches.Add(availability);
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
            return new StaffOrderHarness(db, customer, otherCustomer, address, product, nearBranch, service);
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
