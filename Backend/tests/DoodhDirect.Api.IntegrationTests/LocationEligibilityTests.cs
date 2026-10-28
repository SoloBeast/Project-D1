using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.Customer;
using DoodhDirect.Infrastructure.Identity;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.RefundReplacement;
using DoodhDirect.Infrastructure.Setup;
using DoodhDirect.Infrastructure.Subscriptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Location-based product eligibility: inside at least one branch service
/// radius every otherwise-eligible product may be ordered; outside every
/// radius only products with <c>AllowOrderOutsideBranchRadius</c> may be
/// ordered. Allocation always selects the nearest capable branch under the
/// existing availability and quantity rules. NULL radii keep unlimited
/// semantics. Client-supplied geography can never satisfy the check.
/// </summary>
public sealed class LocationEligibilityTests
{
    private const decimal HomeLat = 12.9720m;
    private const decimal HomeLon = 77.5950m;
    private const decimal RemoteLat = 14.0000m;
    private const decimal RemoteLon = 78.5000m;

    // ---------- Inside a radius ----------

    [Fact]
    public async Task InsideRadius_AllOtherwiseEligibleProductsAllowed_IncludingNonOptedIn()
    {
        await using var harness = await LocationHarness.CreateAsync();
        var request = harness.CheckoutRequest(
            harness.HomeAddress.PublicId,
            (harness.NormalProduct.PublicId, 1m),
            (harness.OptInProduct.PublicId, 1m));

        var preview = await harness.Orders.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal("NEAR", preview.BranchCode);
    }

    [Fact]
    public async Task OverlappingRadii_NearestQualifyingBranchSelected()
    {
        await using var harness = await LocationHarness.CreateAsync();
        var request = harness.CheckoutRequest(
            harness.MidAddress.PublicId,
            (harness.NormalProduct.PublicId, 1m));

        var preview = await harness.Orders.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal("MID", preview.BranchCode);
    }

    [Fact]
    public async Task InsideRadius_NearestCannotFulfil_NoFallbackToOutsideBranch()
    {
        await using var harness = await LocationHarness.CreateAsync();

        // Both the non-opted-in and the opted-in variants stocked only by the
        // distant REMOTE branch must fail: inside a radius, only in-radius
        // branches may fulfil the cart.
        var normalError = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.CheckoutRequest(harness.HomeAddress.PublicId, (harness.RemoteOnlyProduct.PublicId, 1m)),
                CancellationToken.None));
        Assert.Contains("No active branch can fulfil", normalError.Message, StringComparison.Ordinal);

        var optInError = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.CheckoutRequest(harness.HomeAddress.PublicId, (harness.RemoteOnlyOptInProduct.PublicId, 1m)),
                CancellationToken.None));
        Assert.Contains("No active branch can fulfil", optInError.Message, StringComparison.Ordinal);
    }

    // ---------- Outside every radius ----------

    [Fact]
    public async Task OutsideEveryRadius_OptedInProductCanBeOrdered()
    {
        await using var harness = await LocationHarness.CreateAsync();
        var request = harness.CheckoutRequest(
            harness.RemoteAddress.PublicId,
            (harness.OptInProduct.PublicId, 1m));

        var preview = await harness.Orders.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal("REMOTE", preview.BranchCode);
    }

    [Fact]
    public async Task OutsideEveryRadius_NonOptedInLineRejectsWholeCart()
    {
        await using var harness = await LocationHarness.CreateAsync();
        var request = harness.CheckoutRequest(
            harness.RemoteAddress.PublicId,
            (harness.NormalProduct.PublicId, 1m));

        var error = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Orders.PreviewAsync(harness.Customer.Id, request, CancellationToken.None));

        Assert.Equal("items", error.Field);
        Assert.Contains("MILK-001", error.Message, StringComparison.Ordinal);
        Assert.Contains("outside every branch service radius", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideEveryRadius_MixedCartRejectedWithoutSideEffects()
    {
        await using var harness = await LocationHarness.CreateAsync();
        var request = harness.CheckoutRequest(
            harness.RemoteAddress.PublicId,
            (harness.OptInProduct.PublicId, 1m),
            (harness.NormalProduct.PublicId, 1m));

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Orders.CreateAsync(harness.Customer.Id, request, "location-mixed-key", CancellationToken.None));

        Assert.Empty(await harness.Db.Orders.AsNoTracking().ToListAsync());
        Assert.Empty(await harness.Db.Payments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OutsideEveryRadius_NearestCapableBranchSelectedDeterministically()
    {
        await using var harness = await LocationHarness.CreateAsync();

        // REMOTE (~77 km) beats FAR (~132 km) beats NEAR/MID (~150 km) from
        // the remote address; ties would break on Branch.Id.
        var preview = await harness.Orders.PreviewAsync(
            harness.Customer.Id,
            harness.CheckoutRequest(harness.RemoteAddress.PublicId, (harness.OptInProduct.PublicId, 1m)),
            CancellationToken.None);

        Assert.Equal("REMOTE", preview.BranchCode);
    }

    [Fact]
    public async Task TwinBranchesAtIdenticalCoordinates_BreakTiesByBranchId()
    {
        await using var harness = await LocationHarness.CreateAsync();

        var allocation = new BranchAllocationService(harness.Db);
        var result = await allocation.AllocateAsync(
            12.0000m, 76.0000m,
            [(harness.NormalProduct.Id, 1m)],
            CancellationToken.None);

        var firstTwinId = Math.Min(harness.Twin1.Id, harness.Twin2.Id);
        Assert.Equal(firstTwinId, result.BranchId);
    }

    // ---------- Preserved safeguards ----------

    [Fact]
    public async Task NullRadiusBranch_PreservesUnlimitedRadiusSemantics()
    {
        await using var harness = await LocationHarness.CreateAsync(includeUnlimitedBranch: true);
        var request = harness.CheckoutRequest(
            harness.RemoteAddress.PublicId,
            (harness.NormalProduct.PublicId, 1m));

        // The remote address is outside every finite radius, but the NULL
        // radius branch makes the delivery geographically covered, so the
        // non-opted-in product remains orderable.
        var preview = await harness.Orders.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(preview.BranchCode));
    }

    [Fact]
    public async Task ExistingRejections_RemainUnchanged()
    {
        await using var harness = await LocationHarness.CreateAsync();

        // Inactive product.
        var inactiveError = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.CheckoutRequest(harness.HomeAddress.PublicId, (harness.InactiveProduct.PublicId, 1m)),
                CancellationToken.None));
        Assert.Contains("inactive or unavailable", inactiveError.Message, StringComparison.Ordinal);

        // Unstocked product: no branch can fulfil.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.CheckoutRequest(harness.HomeAddress.PublicId, (harness.UnstockedProduct.PublicId, 1m)),
                CancellationToken.None));

        // Quantity above the branch cap: the only stocking branch is excluded.
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.CheckoutRequest(harness.HomeAddress.PublicId, (harness.LimitedProduct.PublicId, 5m)),
                CancellationToken.None));

        // The inactive branch never wins, even though it sits on the address.
        var preview = await harness.Orders.PreviewAsync(
            harness.Customer.Id,
            harness.CheckoutRequest(harness.HomeAddress.PublicId, (harness.NormalProduct.PublicId, 1m)),
            CancellationToken.None);
        Assert.NotEqual("DEAD", preview.BranchCode);
    }

    [Fact]
    public async Task MissingInvalidCoordinates_AndForeignAddresses_Rejected()
    {
        await using var harness = await LocationHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.ManualRequest(91m, 77.5950m, harness.NormalProduct.PublicId, 1m),
                CancellationToken.None));

        var foreignError = await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.CheckoutRequest(harness.OtherAddress.PublicId, (harness.NormalProduct.PublicId, 1m)),
                CancellationToken.None));
        Assert.Contains("address", foreignError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckoutContracts_CarryNoSpoofableGeography()
    {
        var checkoutMembers = typeof(CheckoutRequest).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(checkoutMembers, x => x.Contains("branch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(checkoutMembers, x => x.Contains("distance", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(checkoutMembers, x => x.Contains("radius", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(checkoutMembers, x => x.Contains("eligib", StringComparison.OrdinalIgnoreCase));

        var manualMembers = typeof(CheckoutAddressRequest).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(manualMembers, x => x.Contains("branch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manualMembers, x => x.Contains("distance", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manualMembers, x => x.Contains("withinradius", StringComparison.OrdinalIgnoreCase));

        // Manual coordinates far outside every radius still hit the server
        // rule: the coordinates are an input to measurement, never a verdict.
        await using var harness = await LocationHarness.CreateAsync();
        var error = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Orders.PreviewAsync(
                harness.Customer.Id,
                harness.ManualRequest(RemoteLat, RemoteLon, harness.NormalProduct.PublicId, 1m),
                CancellationToken.None));
        Assert.Contains("MILK-001", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmployeeSelfOrdering_FollowsTheSameRules()
    {
        await using var harness = await LocationHarness.CreateAsync();

        var denied = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Orders.PreviewAsync(
                harness.Employee.Id,
                harness.CheckoutRequest(harness.EmployeeRemoteAddress.PublicId, (harness.NormalProduct.PublicId, 1m)),
                CancellationToken.None));
        Assert.Contains("MILK-001", denied.Message, StringComparison.Ordinal);

        var allowed = await harness.Orders.PreviewAsync(
            harness.Employee.Id,
            harness.CheckoutRequest(harness.EmployeeRemoteAddress.PublicId, (harness.OptInProduct.PublicId, 1m)),
            CancellationToken.None);
        Assert.Equal("REMOTE", allowed.BranchCode);
    }

    // ---------- Catalogue persistence ----------

    [Fact]
    public async Task ProductFlag_PersistsThroughAdminCrud_AndAppearsInCatalogueResponses()
    {
        await using var harness = await LocationHarness.CreateAsync();

        var created = await harness.Catalogue.CreateProductAsync(
            new UpsertProductRequest(
                "MILK-FLAG-001",
                "Flagged Milk",
                null,
                harness.Category.PublicId,
                "litre",
                90m,
                [harness.NearBranch.PublicId],
                ApplicableChargeIds: [],
                AllowOrderOutsideBranchRadius: true),
            CancellationToken.None);
        Assert.True(created.AllowOrderOutsideBranchRadius);
        await harness.Catalogue.SetProductActiveAsync(created.PublicId, true, CancellationToken.None);

        var fetched = await harness.Catalogue.GetProductForAdministrationAsync(created.PublicId, CancellationToken.None);
        Assert.True(fetched.AllowOrderOutsideBranchRadius);

        var updated = await harness.Catalogue.UpdateProductAsync(
            created.PublicId,
            new UpsertProductRequest(
                "MILK-FLAG-001",
                "Flagged Milk",
                null,
                harness.Category.PublicId,
                "litre",
                90m,
                [harness.NearBranch.PublicId],
                ApplicableChargeIds: [],
                AllowOrderOutsideBranchRadius: false),
            CancellationToken.None);
        Assert.False(updated.AllowOrderOutsideBranchRadius);

        // New products default to inside-radius-only.
        var defaulted = await harness.Catalogue.CreateProductAsync(
            new UpsertProductRequest(
                "MILK-FLAG-002",
                "Default Milk",
                null,
                harness.Category.PublicId,
                "litre",
                90m,
                [harness.NearBranch.PublicId],
                []),
            CancellationToken.None);
        Assert.False(defaulted.AllowOrderOutsideBranchRadius);

        Assert.Contains(
            await harness.Catalogue.GetActiveProductsAsync(null, CancellationToken.None),
            x => x.Sku == "MILK-FLAG-001" && !x.AllowOrderOutsideBranchRadius);
    }

    // ---------- Subscriptions ----------

    [Fact]
    public async Task SubscriptionCreation_OutsideRadius_AllowsOptedInProductOnly()
    {
        await using var harness = await LocationHarness.CreateAsync(withSubscriptionSeries: true);

        var allowed = await harness.Subscriptions.CreateAsync(
            harness.Customer.Id,
            harness.SubscriptionRequest(harness.OptInProduct.PublicId, harness.RemoteAddress.PublicId),
            "location-sub-allowed",
            CancellationToken.None);
        Assert.Equal("REMOTE", allowed.Subscription.BranchCode);

        var denied = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Subscriptions.CreateAsync(
                harness.Customer.Id,
                harness.SubscriptionRequest(harness.NormalProduct.PublicId, harness.RemoteAddress.PublicId),
                "location-sub-denied",
                CancellationToken.None));
        Assert.Contains("outside every branch service radius", denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await harness.Db.Subscriptions.AsNoTracking().ToListAsync(),
            x => x.IdempotencyKey == "location-sub-denied");
    }

    // ---------- Harness ----------

    private sealed class LocationHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private LocationHarness(
            SqliteConnection connection,
            DoodhDirectDbContext db,
            User customer,
            User otherCustomer,
            User employee,
            ProductCategory category,
            Product normalProduct,
            Product optInProduct,
            Product remoteOnlyProduct,
            Product remoteOnlyOptInProduct,
            Product inactiveProduct,
            Product unstockedProduct,
            Product limitedProduct,
            Branch nearBranch,
            Branch midBranch,
            Branch farBranch,
            Branch remoteBranch,
            Branch twin1,
            Branch twin2,
            CustomerAddress homeAddress,
            CustomerAddress midAddress,
            CustomerAddress remoteAddress,
            CustomerAddress otherAddress,
            CustomerAddress employeeRemoteAddress,
            OrderService orders,
            CatalogueService catalogue,
            SubscriptionService? subscriptions)
        {
            _connection = connection;
            Db = db;
            Customer = customer;
            Employee = employee;
            Category = category;
            NormalProduct = normalProduct;
            OptInProduct = optInProduct;
            RemoteOnlyProduct = remoteOnlyProduct;
            RemoteOnlyOptInProduct = remoteOnlyOptInProduct;
            InactiveProduct = inactiveProduct;
            UnstockedProduct = unstockedProduct;
            LimitedProduct = limitedProduct;
            NearBranch = nearBranch;
            HomeAddress = homeAddress;
            MidAddress = midAddress;
            RemoteAddress = remoteAddress;
            OtherAddress = otherAddress;
            EmployeeRemoteAddress = employeeRemoteAddress;
            Twin1 = twin1;
            Twin2 = twin2;
            Orders = orders;
            Catalogue = catalogue;
            Subscriptions = subscriptions;
            OtherCustomer = otherCustomer;
            MidBranch = midBranch;
            FarBranch = farBranch;
            RemoteBranch = remoteBranch;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public User OtherCustomer { get; }
        public User Employee { get; }
        public ProductCategory Category { get; }
        public Product NormalProduct { get; }
        public Product OptInProduct { get; }
        public Product RemoteOnlyProduct { get; }
        public Product RemoteOnlyOptInProduct { get; }
        public Product InactiveProduct { get; }
        public Product UnstockedProduct { get; }
        public Product LimitedProduct { get; }
        public Branch NearBranch { get; }
        public Branch MidBranch { get; }
        public Branch FarBranch { get; }
        public Branch RemoteBranch { get; }
        public Branch Twin1 { get; }
        public Branch Twin2 { get; }
        public CustomerAddress HomeAddress { get; }
        public CustomerAddress MidAddress { get; }
        public CustomerAddress RemoteAddress { get; }
        public CustomerAddress OtherAddress { get; }
        public CustomerAddress EmployeeRemoteAddress { get; }
        public OrderService Orders { get; }
        public CatalogueService Catalogue { get; }
        public SubscriptionService? Subscriptions { get; }

        public CheckoutRequest CheckoutRequest(Guid addressId, params (Guid ProductId, decimal Quantity)[] lines) =>
            new(addressId, null, lines.Select(line => new OrderItemRequest(line.ProductId, line.Quantity)).ToArray());

        public CheckoutRequest ManualRequest(decimal latitude, decimal longitude, Guid productId, decimal quantity) =>
            new(
                null,
                new CheckoutAddressRequest(
                    null,
                    "9 Farm Lane",
                    null,
                    "Central",
                    "Bengaluru",
                    "Karnataka",
                    "560001",
                    null,
                    null,
                    "Recipient",
                    "9999999999",
                    latitude,
                    longitude),
                [new OrderItemRequest(productId, quantity)]);

        public CreateSubscriptionRequest SubscriptionRequest(Guid productId, Guid addressId) => new(
            productId,
            addressId,
            1m,
            new DateOnly(2026, 8, 17),
            [DayOfWeek.Monday, DayOfWeek.Wednesday],
            4,
            PaymentMethod.Razorpay);

        public static async Task<LocationHarness> CreateAsync(
            bool includeUnlimitedBranch = false,
            bool withSubscriptionSeries = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var clock = new TestClock(new DateTime(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc));
            var timeProvider = new TestIndiaTimeProvider(clock);

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            var otherCustomer = new User(UserType.Customer);
            otherCustomer.SetProfile("Other Customer");
            var employee = new User(UserType.Employee);
            employee.SetProfile("Employee");
            db.Users.AddRange(customer, otherCustomer, employee);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();

            Product SeedProduct(string sku, bool active, bool allowOutside)
            {
                var product = new Product(category.Id, sku, sku, null, "litre", 80m);
                if (active)
                {
                    product.Activate();
                }

                if (allowOutside)
                {
                    product.AllowOutsideBranchRadius();
                }

                db.Products.Add(product);
                return product;
            }

            var normal = SeedProduct("MILK-001", active: true, allowOutside: false);
            var optIn = SeedProduct("MILK-OPT", active: true, allowOutside: true);
            var remoteOnly = SeedProduct("MILK-REM", active: true, allowOutside: false);
            var remoteOnlyOptIn = SeedProduct("MILK-REMOPT", active: true, allowOutside: true);
            var inactive = SeedProduct("MILK-XXX", active: false, allowOutside: false);
            var unstocked = SeedProduct("MILK-NONE", active: true, allowOutside: false);
            var limited = SeedProduct("MILK-LTD", active: true, allowOutside: false);
            await db.SaveChangesAsync();

            Branch SeedBranch(string code, decimal latitude, decimal longitude, decimal? radiusKm, bool active)
            {
                var branch = new Branch(code, $"{code} Branch", "Bengaluru", "Karnataka", latitude, longitude);
                branch.Update(
                    $"{code} Branch", null, null, null, "Bengaluru", "Karnataka", null,
                    latitude, longitude, radiusKm);
                if (!active)
                {
                    branch.Deactivate();
                }

                db.Branches.Add(branch);
                return branch;
            }

            var near = SeedBranch("NEAR", 12.9716m, 77.5946m, 5m, active: true);
            var mid = SeedBranch("MID", 12.9750m, 77.6000m, 5m, active: true);
            var far = SeedBranch("FAR", 13.1000m, 77.7000m, 5m, active: true);
            var remote = SeedBranch("REMOTE", 13.5000m, 78.0000m, 5m, active: true);
            var dead = SeedBranch("DEAD", HomeLat, HomeLon, 5m, active: false);
            var twin1 = SeedBranch("TWIN1", 12.0000m, 76.0000m, 0.05m, active: true);
            var twin2 = SeedBranch("TWIN2", 12.0000m, 76.0000m, 0.05m, active: true);
            Branch? global = null;
            if (includeUnlimitedBranch)
            {
                global = SeedBranch("GLOBAL", 11.0000m, 75.0000m, null, active: true);
            }

            await db.SaveChangesAsync();

            void Stock(Product product, Branch branch, decimal? maxDailyQuantity = null)
            {
                var link = new ProductBranch(product.Id, branch.Id, true, maxDailyQuantity);
                db.ProductBranches.Add(link);
            }

            foreach (var branch in new[] { near, mid, far })
            {
                Stock(normal, branch);
                Stock(optIn, branch);
            }

            Stock(optIn, remote);
            Stock(remoteOnly, remote);
            Stock(remoteOnlyOptIn, remote);
            Stock(normal, dead);
            Stock(normal, twin1);
            Stock(normal, twin2);
            Stock(limited, near, maxDailyQuantity: 1m);
            if (global is not null)
            {
                Stock(normal, global);
                Stock(optIn, global);
            }

            await db.SaveChangesAsync();

            foreach (var branch in new[] { near, mid, far, remote })
            {
                db.NumberSeries.Add(new NumberSeries(
                    "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, branch.Code));
                if (withSubscriptionSeries)
                {
                    db.NumberSeries.Add(new NumberSeries(
                        SubscriptionService.SubscriptionSeriesCode,
                        $"Subscription document numbers for branch {branch.Code}",
                        "SUB/{SCOPE}/{FY}/{NUMBER:000000}",
                        1,
                        1,
                        NumberSeriesResetPolicy.Never,
                        branch.Code));
                }
            }

            CustomerAddress SeedAddress(User user, string label, decimal latitude, decimal longitude) =>
                new(user.Id, label, "1 Main Road", "Central", "Bengaluru", "Karnataka",
                    "560001", "Recipient", "9999999999", latitude, longitude);

            var home = SeedAddress(customer, "Home", HomeLat, HomeLon);
            var midAddr = SeedAddress(customer, "Mid", 12.9749m, 77.5999m);
            var remoteAddr = SeedAddress(customer, "Remote", RemoteLat, RemoteLon);
            var otherAddr = SeedAddress(otherCustomer, "Other", HomeLat, HomeLon);
            var empRemote = SeedAddress(employee, "EmpRemote", RemoteLat, RemoteLon);
            db.CustomerAddresses.AddRange(home, midAddr, remoteAddr, otherAddr, empRemote);
            await db.SaveChangesAsync();

            var allocation = new BranchAllocationService(db);
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var orders = new OrderService(
                db,
                allocation,
                notificationEventWriter,
                new NumberSeriesService(db, timeProvider),
                timeProvider,
                new RefundReplacementConfigurationService(db, timeProvider));
            var catalogue = new CatalogueService(
                db,
                new ProductImageValidator(Options.Create(new MilkTestMediaOptions())),
                new ThrowingMediaStorage(),
                clock);
            SubscriptionService? subscriptions = withSubscriptionSeries
                ? new SubscriptionService(
                    db,
                    allocation,
                    new StubSubscriptionPaymentService(db, clock),
                    timeProvider,
                    notificationEventWriter,
                    new NumberSeriesService(db, timeProvider))
                : null;

            return new LocationHarness(
                connection, db, customer, otherCustomer, employee, category,
                normal, optIn, remoteOnly, remoteOnlyOptIn, inactive, unstocked, limited,
                near, mid, far, remote, twin1, twin2,
                home, midAddr, remoteAddr, otherAddr, empRemote,
                orders, catalogue, subscriptions);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ThrowingMediaStorage : IMediaStorage
    {
        public Task<StoredMediaResult> SaveAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredMediaContent> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubSubscriptionPaymentService(DoodhDirectDbContext db, TestClock clock) : IPaymentService
    {
        public Task<PaymentResult> CreateForSubscriptionAsync(long customerId, long subscriptionId, PaymentMethod method, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(new PaymentResult(
                Guid.NewGuid(), null, null, null, method, "Mock", PaymentStatus.Pending,
                100m, 0m, "INR", $"order_{subscriptionId:N}", null, "rzp_test", null, null,
                clock.UtcNow.AddMinutes(15), null, clock.UtcNow, null));

        public Task<PaymentPlanResult> CreateSubscriptionPlanAsync(long customerId, long subscriptionId, bool useWalletBalance, string idempotencyKey, CancellationToken cancellationToken, bool expirePriorAttempts = false) =>
            throw new NotSupportedException("Location tests do not exercise plan creation.");

        public Task<PaymentPlanResult> CreateOrderPlanAsync(long customerId, Guid orderId, bool useWalletBalance, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentResult> RetrySubscriptionAsync(long customerId, Guid subscriptionId, PaymentMethod method, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentResult> CreateWalletTopUpAsync(long customerId, decimal amount, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GatewayOrderResult> CreateOrderAsync(long customerId, decimal amount, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetPublicKeyIdAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> VerifyPaymentSignatureAsync(string orderId, string paymentId, string signature, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GatewayPaymentStatusResult> GetPaymentStatusAsync(string orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GatewayOrderPaymentsResult> GetPaymentsForOrderAsync(string orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> VerifyWebhookSignatureAsync(string payload, string signature, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GatewayRefundResult> RefundAsync(string paymentId, decimal? amount, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentResult> CreateAsync(long customerId, CreatePaymentRequest request, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentResult> VerifyAsync(long customerId, VerifyPaymentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentResult> CompleteDevelopmentAsync(long customerId, Guid paymentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentResult> CancelAsync(long customerId, Guid paymentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentResult> GetAsync(long userId, Guid paymentId, bool bypassOwnership, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RefundResult> RefundAsync(long requestedByUserId, Guid paymentId, RefundPaymentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentReconciliationResult> ReconcileAsync(long requestedByUserId, Guid paymentId, bool bypassOwnership, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> ProcessBatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ProcessWebhookAsync(byte[] payload, string signature, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
