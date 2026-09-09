using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class CatalogueServiceTests
{
    [Fact]
    public void NewCatalogueEntities_AreInactiveByDefault()
    {
        var category = new ProductCategory("MILK", "Milk");
        var product = new Product(1, "MILK-001", "Fresh Milk", null, "litre", 80m);

        Assert.False(category.IsActive);
        Assert.False(product.IsActive);
    }

    [Fact]
    public async Task CreateProduct_DefaultsInactiveAndAssignsSelectedBranches()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var product = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);

        Assert.False(product.IsActive);
        var assignment = Assert.Single(product.BranchAvailability);
        Assert.Equal("MAIN", assignment.BranchCode);
        Assert.True(assignment.IsAvailable);
    }

    [Fact]
    public async Task CreateCategory_DefaultsInactiveAndIsExcludedFromPublicCatalogue()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var category = await harness.Service.CreateCategoryAsync(
            new UpsertProductCategoryRequest("YOGURT", "Yogurt", null),
            CancellationToken.None);

        Assert.False(category.IsActive);
        Assert.DoesNotContain(
            await harness.Service.GetActiveCategoriesAsync(CancellationToken.None),
            item => item.PublicId == category.PublicId);
    }

    [Fact]
    public async Task CreateProduct_NormalizesValuesAndSupportsDecimalPrice()
    {
        await using var harness = await CatalogueHarness.CreateAsync();

        var result = await harness.Service.CreateProductAsync(
            new UpsertProductRequest(
                " milk-001 ",
                " Fresh Buffalo Milk ",
                " Sold by litre ",
                harness.Category.PublicId,
                " LITRE ",
                80.25m,
                [harness.Branch.PublicId]),
            CancellationToken.None);

        Assert.Equal("MILK-001", result.Sku);
        Assert.Equal("Fresh Buffalo Milk", result.Name);
        Assert.Equal("Sold by litre", result.Description);
        Assert.Equal("litre", result.UnitOfMeasure);
        Assert.Equal(80.25m, result.Price);
    }

    [Fact]
    public async Task CreateProduct_WithDuplicateSku_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, " milk-001 ", harness.Branch.PublicId),
            CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(InvalidProducts))]
    public async Task CreateProduct_WithInvalidValues_IsRejected(UpsertProductRequest request)
    {
        await using var harness = await CatalogueHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.CreateProductAsync(request with { CategoryId = harness.Category.PublicId }, CancellationToken.None));
    }

    [Fact]
    public async Task CreateProduct_WithInactiveCategory_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        await harness.Service.SetCategoryActiveAsync(harness.Category.PublicId, false, CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-002", harness.Branch.PublicId),
            CancellationToken.None));
    }

    [Fact]
    public async Task PublicProducts_OnlyIncludeActiveProductsAndAvailableActiveBranches()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var available = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);
        var unavailable = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-002", harness.Branch.PublicId),
            CancellationToken.None);
        await harness.Service.SetBranchAvailabilityAsync(
            available.PublicId,
            new SetProductBranchAvailabilityRequest(harness.Branch.PublicId, true, 125.375m),
            CancellationToken.None);
        await harness.Service.SetProductActiveAsync(
            available.PublicId,
            true,
            CancellationToken.None);
        await harness.Service.SetBranchAvailabilityAsync(
            unavailable.PublicId,
            new SetProductBranchAvailabilityRequest(harness.Branch.PublicId, false, null),
            CancellationToken.None);

        var products = await harness.Service.GetActiveProductsAsync(null, CancellationToken.None);

        var result = Assert.Single(products);
        Assert.Equal(available.PublicId, result.PublicId);
        Assert.Equal(125.375m, Assert.Single(result.BranchAvailability).MaxDailyQuantity);
    }

    [Fact]
    public async Task ProductActivation_RequiresActiveCategory()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var product = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);
        await harness.Service.SetProductActiveAsync(product.PublicId, false, CancellationToken.None);
        await harness.Service.SetCategoryActiveAsync(harness.Category.PublicId, false, CancellationToken.None);

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.SetProductActiveAsync(
            product.PublicId, true, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task BranchAvailability_RequiresPositiveMaximumQuantity(decimal quantity)
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var product = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.SetBranchAvailabilityAsync(
            product.PublicId,
            new SetProductBranchAvailabilityRequest(harness.Branch.PublicId, true, quantity),
            CancellationToken.None));
    }

    [Fact]
    public async Task BranchAvailability_RejectsMoreThanThreeDecimalPlaces()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var product = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.SetBranchAvailabilityAsync(
            product.PublicId,
            new SetProductBranchAvailabilityRequest(harness.Branch.PublicId, true, 1.1234m),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateProduct_AssignsAllSelectedBranchesAsAvailable()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var second = new Branch("NIT3", "NIT3 Branch", "Bengaluru", "Karnataka", 13.0m, 77.6m);
        harness.Db.Branches.Add(second);
        await harness.Db.SaveChangesAsync();

        var result = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId, second.PublicId),
            CancellationToken.None);

        Assert.Equal(2, result.BranchAvailability.Count);
        Assert.All(result.BranchAvailability, item => Assert.True(item.IsAvailable));
        Assert.Contains(result.BranchAvailability, item => item.BranchCode == "MAIN");
        Assert.Contains(result.BranchAvailability, item => item.BranchCode == "NIT3");
    }

    [Fact]
    public async Task CreateProduct_WithNoBranches_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001"),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateProduct_WithDuplicateBranches_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId, harness.Branch.PublicId),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateProduct_WithUnknownBranch_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", Guid.NewGuid()),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateProduct_WithInactiveBranch_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        harness.Branch.Deactivate();
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateProduct_WithArchivedBranch_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        harness.Branch.Archive(IndiaLocal(2026, 9, 8));
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateProduct_AddsAndRemovesBranchAssignments()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var second = new Branch("NIT3", "NIT3 Branch", "Bengaluru", "Karnataka", 13.0m, 77.6m);
        harness.Db.Branches.Add(second);
        await harness.Db.SaveChangesAsync();

        var created = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);

        var withSecond = await harness.Service.UpdateProductAsync(
            created.PublicId,
            new UpsertProductRequest(
                "MILK-001", "Fresh Buffalo Milk", "Fresh milk", harness.Category.PublicId, "litre", 80m,
                [harness.Branch.PublicId, second.PublicId]),
            CancellationToken.None);

        Assert.Equal(2, withSecond.BranchAvailability.Count);
        Assert.Contains(withSecond.BranchAvailability, item => item.BranchCode == "MAIN");
        Assert.Contains(withSecond.BranchAvailability, item => item.BranchCode == "NIT3");

        var onlySecond = await harness.Service.UpdateProductAsync(
            created.PublicId,
            new UpsertProductRequest(
                "MILK-001", "Fresh Buffalo Milk", "Fresh milk", harness.Category.PublicId, "litre", 80m,
                [second.PublicId]),
            CancellationToken.None);

        var remaining = Assert.Single(onlySecond.BranchAvailability);
        Assert.Equal("NIT3", remaining.BranchCode);
    }

    [Fact]
    public async Task UpdateProduct_AfterBranchArchived_PreservesHistoricalLink()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var second = new Branch("NIT3", "NIT3 Branch", "Bengaluru", "Karnataka", 13.0m, 77.6m);
        harness.Db.Branches.Add(second);
        await harness.Db.SaveChangesAsync();

        var created = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001", harness.Branch.PublicId),
            CancellationToken.None);

        harness.Branch.Archive(IndiaLocal(2026, 9, 8));
        await harness.Db.SaveChangesAsync();

        var updated = await harness.Service.UpdateProductAsync(
            created.PublicId,
            new UpsertProductRequest(
                "MILK-001", "Fresh Buffalo Milk", "Fresh milk", harness.Category.PublicId, "litre", 80m,
                [second.PublicId]),
            CancellationToken.None);

        // The archived MAIN link must survive (historical record), and NIT3 is added.
        Assert.Equal(2, updated.BranchAvailability.Count);
        Assert.Contains(updated.BranchAvailability, item => item.BranchCode == "MAIN");
        Assert.Contains(updated.BranchAvailability, item => item.BranchCode == "NIT3");
    }

    [Fact]
    public async Task SeedAsync_IsIdempotentAndCreatesAvailableBuffaloMilk()
    {
        await using var db = CreateDb();
        await db.SaveChangesAsync();
        var seed = new CatalogueSeedService(
            db,
            new NumberSeriesSeedService(db));

        await seed.SeedAsync(CancellationToken.None);
        await seed.SeedAsync(CancellationToken.None);

        Assert.Equal(1, await db.ProductCategories.CountAsync());
        Assert.Equal(1, await db.Branches.CountAsync());
        Assert.Equal(1, await db.Products.CountAsync());
        var product = await db.Products.Include(item => item.ProductBranches).SingleAsync();
        Assert.Equal("FRESH-BUFFALO-MILK", product.Sku);
        Assert.Equal("litre", product.UnitOfMeasure);
        Assert.True(product.ProductBranches.Single().IsAvailable);
    }

    public static TheoryData<UpsertProductRequest> InvalidProducts => new()
    {
        // Format-validation cases only. Unknown-branch rejection (NotFoundException) is
        // covered separately by CreateProduct_WithUnknownBranch_IsRejected.
        new(" ", "Milk", null, Guid.Empty, "litre", 80m, []),
        new("MILK-001", " ", null, Guid.Empty, "litre", 80m, []),
        new("MILK-001", "Milk", null, Guid.Empty, "litre", 0m, []),
        new("MILK-001", "Milk", null, Guid.Empty, "litre", 80.001m, []),
        new("MILK-001", "Milk", null, Guid.Empty, "bottle", 80m, [])
    };

    private static UpsertProductRequest ValidProduct(Guid categoryId, string sku, params Guid[] branchIds) =>
        new(sku, "Fresh Buffalo Milk", "Fresh milk", categoryId, "litre", 80m, branchIds);

    private static DateTime IndiaLocal(int year, int month, int day) =>
        DateTime.SpecifyKind(new DateTime(year, month, day, 10, 0, 0), DateTimeKind.Unspecified);

    private static async Task<CatalogueHarness> CreateHarnessAsync()
    {
        var db = CreateDb();
        var category = new ProductCategory("MILK", "Milk", "Milk products.");
        category.Activate();
        var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
        db.ProductCategories.Add(category);
        db.Branches.Add(branch);
        await db.SaveChangesAsync();
        return new CatalogueHarness(db, category, branch, new CatalogueService(db));
    }

    private static DoodhDirectDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"catalogue-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    private sealed class CatalogueHarness(
        DoodhDirectDbContext db,
        ProductCategory category,
        Branch branch,
        CatalogueService service) : IAsyncDisposable
    {
        public DoodhDirectDbContext Db { get; } = db;
        public ProductCategory Category { get; } = category;
        public Branch Branch { get; } = branch;
        public CatalogueService Service { get; } = service;

        public static Task<CatalogueHarness> CreateAsync() => CreateHarnessAsync();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
