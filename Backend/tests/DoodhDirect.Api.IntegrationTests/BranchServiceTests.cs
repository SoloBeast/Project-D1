using System.Reflection;
using System.Text.Json;
using DoodhDirect.Api.Controllers;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Branches;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Identity;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Branches;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class BranchServiceTests
{
    private static readonly UpsertBranchRequest BengaluruBranch = new(
        "BLR-01", "Bengaluru Central", "1 MG Road", "Shivajinagar", "Central",
        "Bengaluru", "Karnataka", "560001", 12.9716m, 77.5946m, 8m);

    private static readonly UpsertBranchRequest MumbaiBranch = new(
        "BOM-02", "Mumbai West", "Link Road", "Andheri", "West",
        "Mumbai", "Maharashtra", "400053", 19.1136m, 72.8697m, 6m);

    // ---------------------------------------------------------------- list / get

    [Fact]
    public async Task ListAsync_ReturnsBranchesOrderedByNameThenCode()
    {
        await using var harness = await BranchHarness.CreateAsync();
        await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);
        await harness.Service.CreateAsync(1, MumbaiBranch, CancellationToken.None);

        var result = await harness.Service.ListAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Bengaluru Central", result[0].Name);
        Assert.Equal("Mumbai West", result[1].Name);
        Assert.All(result, branch => Assert.False(string.IsNullOrWhiteSpace(branch.BranchNumber)));
    }

    [Fact]
    public async Task GetAsync_ReturnsBranchByPublicId()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);

        var result = await harness.Service.GetAsync(created.PublicId, CancellationToken.None);

        Assert.Equal(created.PublicId, result.PublicId);
        Assert.Equal("BLR-01", result.Code);
    }

    [Fact]
    public async Task GetAsync_ThrowsNotFoundForUnknownBranch()
    {
        await using var harness = await BranchHarness.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // ---------------------------------------------------------------- create

    [Fact]
    public async Task CreateAsync_AllocatesBranchNumberAndScopedOrderSeries()
    {
        await using var harness = await BranchHarness.CreateAsync();

        var result = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.PublicId);
        Assert.Equal("BLR-01", result.Code);
        Assert.Equal("Bengaluru Central", result.Name);
        Assert.True(result.IsActive);
        // Branch number is allocated server-side from the BRANCH series.
        Assert.Equal("BRN-000001", result.BranchNumber);
        // The branch-scoped ORDER series must exist the moment the branch does.
        var orderSeries = await harness.Db.NumberSeries
            .SingleAsync(item => item.Code == "ORDER" && item.ScopeKey == "BLR-01");
        Assert.Equal("ORD/BLR-01/{FY}/{NUMBER:000000}", orderSeries.Template);
        Assert.Equal(NumberSeriesResetPolicy.FinancialYear, orderSeries.ResetPolicy);
    }

    [Fact]
    public async Task CreateAsync_WritesAuditLogWithCreatedAction()
    {
        await using var harness = await BranchHarness.CreateAsync();

        var result = await harness.Service.CreateAsync(42, BengaluruBranch, CancellationToken.None);

        var audit = await harness.Db.AuditLogs
            .SingleAsync(item => item.EntityId == result.PublicId.ToString());
        Assert.Equal(BranchService.ActionCreated, audit.Action);
        Assert.Equal("Branch", audit.EntityType);
        Assert.Equal(42, audit.UserId);
        Assert.Null(audit.OldValueJson);
        Assert.NotNull(audit.NewValueJson);
        Assert.Contains("\"BLR-01\"", audit.NewValueJson);
        Assert.Contains("\"BRN-000001\"", audit.NewValueJson);
    }

    [Fact]
    public async Task CreateAsync_NormalizesCodeToUpperCaseAndTrims()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var request = BengaluruBranch with { Code = "  blr-01  " };

        var result = await harness.Service.CreateAsync(1, request, CancellationToken.None);

        Assert.Equal("BLR-01", result.Code);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateCodeCaseInsensitively()
    {
        await using var harness = await BranchHarness.CreateAsync();
        await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);

        var duplicate = BengaluruBranch with { Code = "blr-01" };

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.CreateAsync(1, duplicate, CancellationToken.None));
        Assert.Contains("already in use", exception.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await harness.Db.Branches.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingRequiredFields()
    {
        await using var harness = await BranchHarness.CreateAsync();

        var invalid = BengaluruBranch with { Code = "  " };

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.CreateAsync(1, invalid, CancellationToken.None));
        Assert.Equal(0, await harness.Db.Branches.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_RejectsOutOfRangeLatitude()
    {
        await using var harness = await BranchHarness.CreateAsync();

        var invalid = BengaluruBranch with { Latitude = 91m };

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.CreateAsync(1, invalid, CancellationToken.None));
        Assert.Equal("latitude", exception.Field);
    }

    // ---------------------------------------------------------------- update

    [Fact]
    public async Task UpdateAsync_ChangesNameAndAddressDetails()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);
        var updatedRequest = BengaluruBranch with
        {
            Name = "Bengaluru Central Prime",
            AddressLine1 = "2 MG Road",
            PinCode = "560002",
            ServiceRadiusKm = 12m
        };

        var result = await harness.Service.UpdateAsync(
            7, created.PublicId, updatedRequest, CancellationToken.None);

        Assert.Equal("Bengaluru Central Prime", result.Name);
        Assert.Equal("2 MG Road", result.AddressLine1);
        Assert.Equal("560002", result.PinCode);
        Assert.Equal(12m, result.ServiceRadiusKm);
        // Branch number and code are stable across edits.
        Assert.Equal("BLR-01", result.Code);
        Assert.Equal("BRN-000001", result.BranchNumber);
    }

    [Fact]
    public async Task UpdateAsync_RecordsBeforeAndAfterAuditSnapshot()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);
        var updatedRequest = BengaluruBranch with { Name = "Renamed Branch" };

        await harness.Service.UpdateAsync(7, created.PublicId, updatedRequest, CancellationToken.None);

        var audit = await harness.Db.AuditLogs
            .SingleAsync(item => item.Action == BranchService.ActionUpdated);
        Assert.Equal(7, audit.UserId);
        Assert.NotNull(audit.OldValueJson);
        Assert.NotNull(audit.NewValueJson);
        Assert.Contains("Bengaluru Central", audit.OldValueJson);
        Assert.Contains("Renamed Branch", audit.NewValueJson);
    }

    [Fact]
    public async Task UpdateAsync_ThrowsConflictWhenRenamingToExistingCode()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var first = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);
        await harness.Service.CreateAsync(1, MumbaiBranch, CancellationToken.None);

        var renameToMumbai = MumbaiBranch with { Code = "BOM-02", Name = "Trying To Steal" };

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            harness.Service.UpdateAsync(1, first.PublicId, renameToMumbai, CancellationToken.None));
        Assert.Contains("already in use", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_BlocksCodeChangeWhenOrdersReferenceBranch()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);
        await harness.AddOrderAsync(created);

        var requestWithNewCode = BengaluruBranch with { Code = "BLR-99" };

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.UpdateAsync(1, created.PublicId, requestWithNewCode, CancellationToken.None));
        Assert.Contains("orders already reference", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("BLR-01", (await harness.Db.Branches.SingleAsync()).Code);
    }

    [Fact]
    public async Task UpdateAsync_BlocksCodeChangeWhenProductAvailabilityReferencesBranch()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);
        await harness.AddProductAvailabilityAsync(created);

        var requestWithNewCode = BengaluruBranch with { Code = "BLR-99" };

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.UpdateAsync(1, created.PublicId, requestWithNewCode, CancellationToken.None));
        Assert.Contains("product availability already references", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("BLR-01", (await harness.Db.Branches.SingleAsync()).Code);
    }

    [Fact]
    public async Task UpdateAsync_BlocksCodeChangeWhenScopedOrderSeriesExists()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);
        // Every branch gets a scoped ORDER series at creation; a rename must be
        // blocked because the series has already consumed numbers under the code.
        var requestWithNewCode = BengaluruBranch with { Code = "BLR-99" };

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.Service.UpdateAsync(1, created.PublicId, requestWithNewCode, CancellationToken.None));
        Assert.Contains("order numbering series already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_AllowsCodeChangeForLegacyBranchWithoutScopedOrderSeries()
    {
        await using var harness = await BranchHarness.CreateAsync();

        // Seed a legacy branch directly (pre-dates the scoped ORDER series) so no
        // order series exists under its code; the code-change guard must permit it.
        var legacy = new Branch("LEG-01", "Legacy Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
        legacy.AssignBranchNumber("BRN-009999");
        harness.Db.Branches.Add(legacy);
        await harness.Db.SaveChangesAsync();

        var requestWithNewCode = BengaluruBranch with { Code = "LEG-02" };

        var result = await harness.Service.UpdateAsync(
            1, legacy.PublicId, requestWithNewCode, CancellationToken.None);

        Assert.Equal("LEG-02", result.Code);
        Assert.Equal("BRN-009999", result.BranchNumber);
        Assert.Equal("Bengaluru Central", result.Name);
        // No ORDER series was created under either the old or the new code.
        Assert.False(await harness.Db.NumberSeries.AnyAsync(
            item => item.Code == "ORDER" && (item.ScopeKey == "LEG-01" || item.ScopeKey == "LEG-02")));
    }

    // ---------------------------------------------------------------- activate / deactivate

    [Fact]
    public async Task SetActiveAsync_DeactivateThenActivateIsIdempotent()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var created = await harness.Service.CreateAsync(1, BengaluruBranch, CancellationToken.None);

        var deactivated = await harness.Service.SetActiveAsync(9, created.PublicId, false, CancellationToken.None);
        Assert.False(deactivated.IsActive);

        // Deactivating an already-inactive branch is a no-op.
        var deactivatedAgain = await harness.Service.SetActiveAsync(9, created.PublicId, false, CancellationToken.None);
        Assert.False(deactivatedAgain.IsActive);

        var reactivated = await harness.Service.SetActiveAsync(9, created.PublicId, true, CancellationToken.None);
        Assert.True(reactivated.IsActive);

        var activatedAgain = await harness.Service.SetActiveAsync(9, created.PublicId, true, CancellationToken.None);
        Assert.True(activatedAgain.IsActive);

        // Only real state transitions write audit entries (create + deactivate + activate).
        var actions = await harness.Db.AuditLogs
            .Where(item => item.EntityId == created.PublicId.ToString())
            .Select(item => item.Action)
            .ToListAsync();
        Assert.Equal(3, actions.Count);
        Assert.Equal(1, actions.Count(action => action == BranchService.ActionCreated));
        Assert.Equal(1, actions.Count(action => action == BranchService.ActionDeactivated));
        Assert.Equal(1, actions.Count(action => action == BranchService.ActionActivated));
    }

    [Fact]
    public async Task SetActiveAsync_ThrowsNotFoundForUnknownBranch()
    {
        await using var harness = await BranchHarness.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.SetActiveAsync(1, Guid.NewGuid(), false, CancellationToken.None));
    }

    // ---------------------------------------------------------------- concurrency (shared-cache SQLite)

    [Fact]
    public async Task ConcurrentCreates_AllocateDistinctSequentialBranchNumbers()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var requests = Enumerable.Range(1, 5)
            .Select(i => BengaluruBranch with { Code = $"BR-{i:D2}" })
            .ToArray();

        var results = await Task.WhenAll(requests.Select(async request =>
        {
            await using var context = harness.CreateContext();
            return await harness.CreateService(context)
                .CreateAsync(1, request, CancellationToken.None);
        }));

        var numbers = results.Select(branch => branch.BranchNumber).Order().ToArray();
        Assert.Equal(5, numbers.Distinct().Count());
        Assert.Equal("BRN-000001", numbers[0]);
        Assert.Equal("BRN-000005", numbers[^1]);
    }

    [Fact]
    public async Task ConcurrentCreates_WithSameCodeOnlyOneSucceeds()
    {
        await using var harness = await BranchHarness.CreateAsync();
        var requests = Enumerable.Range(1, 3).Select(_ => BengaluruBranch).ToArray();

        var outcomes = await Task.WhenAll(requests.Select(async request =>
        {
            await using var context = harness.CreateContext();
            try
            {
                await harness.CreateService(context).CreateAsync(1, request, CancellationToken.None);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }));

        Assert.Equal(1, outcomes.Count(success => success));
        Assert.Equal(1, await harness.Db.Branches.CountAsync());
    }

    // ---------------------------------------------------------------- RBAC reflection

    public static TheoryData<string, string> ManageActions => new()
    {
        { nameof(BranchController.Create), "permission:" + AuthorizationCodes.BranchesManage },
        { nameof(BranchController.Update), "permission:" + AuthorizationCodes.BranchesManage },
        { nameof(BranchController.Activate), "permission:" + AuthorizationCodes.BranchesManage },
        { nameof(BranchController.Deactivate), "permission:" + AuthorizationCodes.BranchesManage }
    };

    public static TheoryData<string, string> ReadActions => new()
    {
        { nameof(BranchController.List), "permission:" + AuthorizationCodes.BranchesRead },
        { nameof(BranchController.Get), "permission:" + AuthorizationCodes.BranchesRead }
    };

    [Fact]
    public void Controller_UsesAdministrationRouteAndEveryActionRequiresPermission()
    {
        var route = Assert.Single(typeof(BranchController).GetCustomAttributes<RouteAttribute>());
        Assert.Equal("api/v1/admin/branches", route.Template);

        // Authentication is enforced per-action via a "permission:" policy rather than a
        // class-level [Authorize]; every HTTP action must carry one and none may be anonymous.
        var actionMethods = typeof(BranchController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(inherit: false).Any())
            .ToList();

        Assert.NotEmpty(actionMethods);
        foreach (var method in actionMethods)
        {
            var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));
            Assert.NotNull(authorize.Policy);
            Assert.StartsWith("permission:", authorize.Policy);
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        }
    }

    [Theory]
    [MemberData(nameof(ReadActions))]
    public void ReadAction_UsesExpectedRouteAndReadPermission(string methodName, string permission)
    {
        var method = RequireMethod(methodName);
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal(permission, authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.NotNull(Assert.Single(method.GetCustomAttributes<HttpGetAttribute>(inherit: false)));
    }

    [Theory]
    [MemberData(nameof(ManageActions))]
    public void ManageAction_UsesExpectedRouteAndManagePermission(string methodName, string permission)
    {
        var method = RequireMethod(methodName);
        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>(inherit: false));

        Assert.Equal(permission, authorize.Policy);
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
    }

    [Theory]
    [InlineData(nameof(BranchController.Create), typeof(HttpPostAttribute), null)]
    [InlineData(nameof(BranchController.Update), typeof(HttpPutAttribute), "{branchId:guid}")]
    [InlineData(nameof(BranchController.Activate), typeof(HttpPostAttribute), "{branchId:guid}/activate")]
    [InlineData(nameof(BranchController.Deactivate), typeof(HttpPostAttribute), "{branchId:guid}/deactivate")]
    public void Action_UsesExpectedHttpVerbAndRoute(string methodName, Type verbType, string? template)
    {
        var method = RequireMethod(methodName);
        var attribute = Assert.Single(method.GetCustomAttributes(verbType, inherit: false));

        Assert.Equal(template, ((HttpMethodAttribute)attribute).Template);
    }

    private static MethodInfo RequireMethod(string methodName) =>
        typeof(BranchController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Method {methodName} was not found.");

    private sealed class BranchHarness : IAsyncDisposable
    {
        private const string BranchSeriesCode = "BRANCH";

        private readonly string _connectionString;
        private readonly SqliteConnection _connection;
        private readonly TestClock _clock;
        private readonly TestIndiaTimeProvider _timeProvider;

        private BranchHarness(
            string connectionString,
            SqliteConnection connection,
            DoodhDirectDbContext db,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            BranchService service)
        {
            _connectionString = connectionString;
            _connection = connection;
            Db = db;
            _clock = clock;
            _timeProvider = timeProvider;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public BranchService Service { get; }

        public static async Task<BranchHarness> CreateAsync()
        {
            var clock = new TestClock(new DateTime(2026, 8, 20, 2, 41, 0, DateTimeKind.Unspecified));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"branch-tests-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 10
            }.ToString();
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(connection, sqlite => sqlite.CommandTimeout(10))
                .Options;
            var db = new DoodhDirectDbContext(options);
            await db.Database.EnsureCreatedAsync();

            // Seed the global BRANCH numbering series that allocation consumes.
            db.NumberSeries.Add(new NumberSeries(
                BranchSeriesCode, "Branch Number", "BRN-{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var numberSeriesService = new NumberSeriesService(db, timeProvider);
            var seedService = new NumberSeriesSeedService(db);
            var service = new BranchService(db, numberSeriesService, seedService, timeProvider);
            return new BranchHarness(connectionString, connection, db, clock, timeProvider, service);
        }

        /// <summary>Creates a fresh context over a new connection to the same shared in-memory database.</summary>
        public DoodhDirectDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseSqlite(_connectionString, sqlite => sqlite.CommandTimeout(10))
                .Options;
            return new DoodhDirectDbContext(options, _timeProvider);
        }

        public BranchService CreateService(DoodhDirectDbContext db) => new(
            db,
            new NumberSeriesService(db, _timeProvider),
            new NumberSeriesSeedService(db),
            _timeProvider);

        public async Task AddOrderAsync(BranchResult branch)
        {
            var customer = new User(UserType.Customer);
            customer.SetProfile("Ordering Customer");
            Db.Users.Add(customer);
            await Db.SaveChangesAsync();

            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            Db.CustomerAddresses.Add(address);
            await Db.SaveChangesAsync();

            var branchEntity = await Db.Branches.SingleAsync(item => item.PublicId == branch.PublicId);
            var order = new Order(
                customer.Id,
                address.Id,
                branchEntity.Id,
                $"idem-{Guid.NewGuid():N}",
                "ORD-000001",
                100m,
                0m,
                branch.Code,
                branch.Name,
                "Home",
                "1 Main Road",
                null,
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                null,
                null,
                "Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            Db.Orders.Add(order);
            await Db.SaveChangesAsync();
        }

        public async Task AddProductAvailabilityAsync(BranchResult branch)
        {
            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            var product = new Product(0, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            category.Products.Add(product);
            Db.ProductCategories.Add(category);
            await Db.SaveChangesAsync();

            var branchEntity = await Db.Branches.SingleAsync(item => item.PublicId == branch.PublicId);
            var availability = new ProductBranch(product.Id, branchEntity.Id, true, null);
            Db.ProductBranches.Add(availability);
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
