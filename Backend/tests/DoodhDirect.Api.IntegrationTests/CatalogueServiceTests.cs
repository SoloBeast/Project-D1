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
    public async Task CreateProduct_DefaultsInactiveAndHasNoBranchAssignment()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var product = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001"),
            CancellationToken.None);

        Assert.False(product.IsActive);
        Assert.Empty(product.BranchAvailability);
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
                80.25m),
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
            ValidProduct(harness.Category.PublicId, "MILK-001"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, " milk-001 "),
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
            ValidProduct(harness.Category.PublicId, "MILK-002"),
            CancellationToken.None));
    }

    [Fact]
    public async Task PublicProducts_OnlyIncludeActiveProductsAndAvailableActiveBranches()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var available = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-001"),
            CancellationToken.None);
        var unavailable = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-002"),
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
            ValidProduct(harness.Category.PublicId, "MILK-001"),
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
            ValidProduct(harness.Category.PublicId, "MILK-001"),
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
            ValidProduct(harness.Category.PublicId, "MILK-001"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ValidationAppException>(() => harness.Service.SetBranchAvailabilityAsync(
            product.PublicId,
            new SetProductBranchAvailabilityRequest(harness.Branch.PublicId, true, 1.1234m),
            CancellationToken.None));
    }

    [Fact]
    public async Task SeedAsync_IsIdempotentAndCreatesAvailableBuffaloMilk()
    {
        await using var db = CreateDb();
        var timeProvider = new TestClock(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified));
        db.NumberSeries.Add(new NumberSeries(
            "BRANCH", "Branch Number", "BR/{NUMBER:000}", 1, 1, NumberSeriesResetPolicy.Never));
        await db.SaveChangesAsync();
        var seed = new CatalogueSeedService(
            db,
            new NumberSeriesService(db, timeProvider),
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
        new(" ", "Milk", null, Guid.Empty, "litre", 80m),
        new("MILK-001", " ", null, Guid.Empty, "litre", 80m),
        new("MILK-001", "Milk", null, Guid.Empty, "litre", 0m),
        new("MILK-001", "Milk", null, Guid.Empty, "litre", 80.001m),
        new("MILK-001", "Milk", null, Guid.Empty, "bottle", 80m)
    };

    private static UpsertProductRequest ValidProduct(Guid categoryId, string sku) =>
        new(sku, "Fresh Buffalo Milk", "Fresh milk", categoryId, "litre", 80m);

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
