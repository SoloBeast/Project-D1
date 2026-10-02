using System.Text.Json;
using DoodhDirect.Application.Catalogue;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.MilkTesting;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Catalogue;
using DoodhDirect.Infrastructure.MilkTesting;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

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

    // ---------------------------------------------------------------- product charge mappings

    /// <summary>Seeds an active, product-assignable (item-level) charge.</summary>
    private static async Task<Charge> SeedChargeAsync(
        DoodhDirectDbContext db,
        string code,
        bool applicableOnAll = false,
        bool isActive = true)
    {
        var charge = new Charge("GST", code, null, 5m, applicableOnAll);
        if (!isActive)
        {
            charge.Deactivate();
        }

        db.Charges.Add(charge);
        await db.SaveChangesAsync();
        return charge;
    }

    [Fact]
    public async Task CreateProduct_AssignsSingleApplicableCharge()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");

        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None);

        var assignment = Assert.Single(product.ApplicableCharges);
        Assert.Equal(charge.PublicId, assignment.ChargeId);
        Assert.Equal("GST-P", assignment.ChargeCode);
        Assert.True(assignment.IsActive);
    }

    [Fact]
    public async Task CreateProduct_AssignsMultipleCharges_AndIgnoresDuplicates()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var first = await SeedChargeAsync(harness.Db, "GST-P");
        var second = await SeedChargeAsync(harness.Db, "CESS-P");

        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [first.PublicId, second.PublicId, first.PublicId]),
            CancellationToken.None);

        Assert.Equal(2, product.ApplicableCharges.Count);
        Assert.Contains(product.ApplicableCharges, item => item.ChargeCode == "GST-P");
        Assert.Contains(product.ApplicableCharges, item => item.ChargeCode == "CESS-P");
        Assert.Equal(2, await harness.Db.ProductCharges.CountAsync());
    }

    [Fact]
    public async Task CreateProduct_GlobalCharge_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var global = await SeedChargeAsync(harness.Db, "GST-ALL", applicableOnAll: true);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => harness.Service.CreateProductAsync(
                ProductWithCharges(
                    harness.Category.PublicId,
                    "MILK-001",
                    [harness.Branch.PublicId],
                    [global.PublicId]),
                CancellationToken.None));

        Assert.Contains(
            "This charge is configured as Applicable on All and cannot be assigned to individual products",
            exception.Message);
        Assert.Empty(await harness.Db.ProductCharges.ToListAsync());
    }

    [Fact]
    public async Task CreateProduct_InactiveCharge_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var inactive = await SeedChargeAsync(harness.Db, "GST-P", isActive: false);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => harness.Service.CreateProductAsync(
                ProductWithCharges(
                    harness.Category.PublicId,
                    "MILK-001",
                    [harness.Branch.PublicId],
                    [inactive.PublicId]),
                CancellationToken.None));

        Assert.Contains("is inactive and cannot be assigned", exception.Message);
        Assert.Empty(await harness.Db.ProductCharges.ToListAsync());
    }

    [Fact]
    public async Task CreateProduct_UnknownCharge_IsRejected()
    {
        await using var harness = await CatalogueHarness.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            () => harness.Service.CreateProductAsync(
                ProductWithCharges(
                    harness.Category.PublicId,
                    "MILK-001",
                    [harness.Branch.PublicId],
                    [Guid.NewGuid()]),
                CancellationToken.None));
    }

    [Fact]
    public async Task UpdateProduct_ReplacesMappingSet_AndPreservesKeptMappings()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var kept = await SeedChargeAsync(harness.Db, "GST-P");
        var removed = await SeedChargeAsync(harness.Db, "CESS-P");
        var added = await SeedChargeAsync(harness.Db, "TCS-P");
        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [kept.PublicId, removed.PublicId]),
            CancellationToken.None);

        var updated = await harness.Service.UpdateProductAsync(
            product.PublicId,
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [kept.PublicId, added.PublicId]),
            CancellationToken.None);

        Assert.Equal(
            new[] { "GST-P", "TCS-P" },
            updated.ApplicableCharges.Select(item => item.ChargeCode).ToArray());
        Assert.Equal(2, await harness.Db.ProductCharges.CountAsync());
    }

    [Fact]
    public async Task UpdateProduct_PreservesExistingMapping_WhenChargeBecameInactive()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");
        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None);

        // The charge is deactivated AFTER the product mapped it: the mapping is
        // configuration and survives; re-saving the product with the same set
        // (as the admin UI does — inactive mappings show as locked selections)
        // must not fail and must not drop the mapping.
        charge.Deactivate();
        await harness.Db.SaveChangesAsync();

        var updated = await harness.Service.UpdateProductAsync(
            product.PublicId,
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None);

        var assignment = Assert.Single(updated.ApplicableCharges);
        Assert.Equal("GST-P", assignment.ChargeCode);
        Assert.False(assignment.IsActive);
    }

    [Fact]
    public async Task MultipleProducts_CanShareTheSameCharge()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");

        var first = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None);
        var second = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-002",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None);

        Assert.Equal("GST-P", first.ApplicableCharges.Single().ChargeCode);
        Assert.Equal("GST-P", second.ApplicableCharges.Single().ChargeCode);
        Assert.Equal(2, await harness.Db.ProductCharges.CountAsync());
    }

    [Fact]
    public async Task CustomerProductPayload_DoesNotExposeApplicableCharges()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");
        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None);
        await harness.Service.SetProductActiveAsync(product.PublicId, true, CancellationToken.None);

        var active = await harness.Service.GetActiveProductAsync(product.PublicId, CancellationToken.None);

        Assert.Empty(active.ApplicableCharges);
    }

    // -----------------------------------------------------------------
    // ProductCharge assignment audit trail
    // -----------------------------------------------------------------

    private static readonly long AuditActorId = 4242;

    private async Task<List<AuditLog>> ChargeAuditsAsync(
        DoodhDirectDbContext db,
        Guid productId) =>
        await db.AuditLogs
            .Where(log => log.EntityType == "Product"
                && log.EntityId == productId.ToString()
                && (log.Action == "PRODUCT.CHARGE_ASSIGNED"
                    || log.Action == "PRODUCT.CHARGE_UNASSIGNED"))
            .OrderBy(log => log.Id)
            .ToListAsync();

    [Fact]
    public async Task ChargeAudit_AssignCreatesAssignedEvent_WithActorAndSnapshot()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");

        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        var audits = await ChargeAuditsAsync(harness.Db, product.PublicId);
        var row = Assert.Single(audits);
        Assert.Equal("PRODUCT.CHARGE_ASSIGNED", row.Action);
        Assert.Equal(AuditActorId, row.UserId);
        Assert.Contains(charge.PublicId.ToString(), row.NewValueJson, StringComparison.Ordinal);
        Assert.Contains("GST-P", row.NewValueJson, StringComparison.Ordinal);
        Assert.Contains("MILK-001", row.NewValueJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChargeAudit_UnassignCreatesUnassignedEvent()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");
        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        await harness.Service.UpdateProductAsync(
            product.PublicId,
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                []),
            CancellationToken.None,
            actorUserId: AuditActorId);

        var audits = await ChargeAuditsAsync(harness.Db, product.PublicId);
        Assert.Equal(2, audits.Count);
        Assert.Equal("PRODUCT.CHARGE_ASSIGNED", audits[0].Action);
        var removed = Assert.Single(audits, log => log.Action == "PRODUCT.CHARGE_UNASSIGNED");
        Assert.Equal(AuditActorId, removed.UserId);
        // The unassignment snapshot must survive the join row's deletion.
        Assert.Contains(charge.PublicId.ToString(), removed.OldValueJson, StringComparison.Ordinal);
        Assert.Contains("GST-P", removed.OldValueJson, StringComparison.Ordinal);
        Assert.Null(removed.NewValueJson);
    }

    [Fact]
    public async Task ChargeAudit_ReplacementAuditsOnlyTheChangedPairs()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var first = await SeedChargeAsync(harness.Db, "GST-P");
        var second = await SeedChargeAsync(harness.Db, "CESS-P");
        var third = await SeedChargeAsync(harness.Db, "TDS-P");
        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [first.PublicId, second.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        await harness.Service.UpdateProductAsync(
            product.PublicId,
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [second.PublicId, third.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        var audits = await ChargeAuditsAsync(harness.Db, product.PublicId);
        // Create time: A+B assigned. Update A,B -> B,C must produce exactly:
        // A unassigned and C assigned — B (kept) stays silent.
        Assert.Equal(4, audits.Count);
        var updateAudits = audits.Skip(2).ToList();
        var unassigned = Assert.Single(updateAudits, log => log.Action == "PRODUCT.CHARGE_UNASSIGNED");
        Assert.Contains(first.PublicId.ToString(), unassigned.OldValueJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(second.PublicId.ToString(), unassigned.OldValueJson!, StringComparison.Ordinal);
        var assigned = Assert.Single(updateAudits, log => log.Action == "PRODUCT.CHARGE_ASSIGNED");
        Assert.Contains(third.PublicId.ToString(), assigned.NewValueJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(second.PublicId.ToString(), assigned.NewValueJson!, StringComparison.Ordinal);
        Assert.Equal(2, await harness.Db.ProductCharges.CountAsync());
    }

    [Fact]
    public async Task ChargeAudit_UnchangedMappings_ProduceNoEvents()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");
        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        var auditsAfterCreate = await ChargeAuditsAsync(harness.Db, product.PublicId);
        Assert.Single(auditsAfterCreate);

        // Re-saving with the identical mapping set (the admin UI's no-change
        // save path) must not generate any further charge events.
        await harness.Service.UpdateProductAsync(
            product.PublicId,
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        var auditsAfterResave = await ChargeAuditsAsync(harness.Db, product.PublicId);
        Assert.Single(auditsAfterResave);
    }

    [Fact]
    public async Task ChargeAudit_CreateWithMappings_EmitsOneEventPerCreatedMapping()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var first = await SeedChargeAsync(harness.Db, "GST-P");
        var second = await SeedChargeAsync(harness.Db, "CESS-P");

        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [first.PublicId, second.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        var audits = await ChargeAuditsAsync(harness.Db, product.PublicId);
        Assert.Equal(2, audits.Count);
        Assert.All(audits, log => Assert.Equal("PRODUCT.CHARGE_ASSIGNED", log.Action));
        var auditedCharges = audits
            .Select(log => log.NewValueJson)
            .Select(json => json!.Contains(first.PublicId.ToString(), StringComparison.Ordinal) ? first.PublicId : second.PublicId)
            .ToHashSet();
        Assert.Equal(new HashSet<Guid> { first.PublicId, second.PublicId }, auditedCharges);
    }

    [Fact]
    public async Task ChargeAudit_FailedOperation_LeavesNoMisleadingAuditRows()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var global = await SeedChargeAsync(harness.Db, "GST-ALL", applicableOnAll: true);
        var activeCharge = await SeedChargeAsync(harness.Db, "GST-P");
        var existingProduct = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [activeCharge.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        // Create attempt with a global charge: rejected before any SaveChanges.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => harness.Service.CreateProductAsync(
                ProductWithCharges(
                    harness.Category.PublicId,
                    "MILK-BAD",
                    [harness.Branch.PublicId],
                    [global.PublicId]),
                CancellationToken.None,
                actorUserId: AuditActorId));

        // Update attempt that would both add (invalid global) and remove an
        // existing mapping: the validation failure must roll the whole change
        // back — no unassignment row may appear for the untouched product.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => harness.Service.UpdateProductAsync(
                existingProduct.PublicId,
                ProductWithCharges(
                    harness.Category.PublicId,
                    "MILK-001",
                    [harness.Branch.PublicId],
                    [global.PublicId]),
                CancellationToken.None,
                actorUserId: AuditActorId));

        var audits = await ChargeAuditsAsync(harness.Db, existingProduct.PublicId);
        Assert.Single(audits);
        Assert.Equal("PRODUCT.CHARGE_ASSIGNED", audits[0].Action);
        Assert.Equal(1, await harness.Db.ProductCharges.CountAsync(link => link.Product.PublicId == existingProduct.PublicId));
    }

    [Fact]
    public async Task ChargeAudit_SnapshotContainsProductAndChargeIdentification()
    {
        await using var harness = await CatalogueHarness.CreateAsync();
        var charge = await SeedChargeAsync(harness.Db, "GST-P");
        var product = await harness.Service.CreateProductAsync(
            ProductWithCharges(
                harness.Category.PublicId,
                "MILK-001",
                [harness.Branch.PublicId],
                [charge.PublicId]),
            CancellationToken.None,
            actorUserId: AuditActorId);

        var row = Assert.Single(await ChargeAuditsAsync(harness.Db, product.PublicId));
        Assert.Equal("Product", row.EntityType);
        Assert.Equal(product.PublicId.ToString(), row.EntityId);
        using var document = JsonDocument.Parse(row.NewValueJson!);
        var snapshot = document.RootElement;
        Assert.Equal(product.PublicId.ToString(), snapshot.GetProperty("ProductId").GetString());
        Assert.Equal("MILK-001", snapshot.GetProperty("ProductSku").GetString());
        Assert.Equal(charge.PublicId.ToString(), snapshot.GetProperty("ChargeId").GetString());
        Assert.Equal("GST-P", snapshot.GetProperty("ChargeCode").GetString());
        Assert.Equal("GST", snapshot.GetProperty("ChargeType").GetString());
        Assert.Equal(5m, snapshot.GetProperty("Percentage").GetDecimal());
        Assert.True(snapshot.GetProperty("IsActive").GetBoolean());
        Assert.False(snapshot.GetProperty("ApplicableOnAll").GetBoolean());
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

    /// <summary>Product upsert with an explicit item-level charge assignment set.</summary>
    private static UpsertProductRequest ProductWithCharges(
        Guid categoryId,
        string sku,
        Guid[] branchIds,
        IReadOnlyList<Guid> chargeIds) =>
        new(sku, "Fresh Buffalo Milk", "Fresh milk", categoryId, "litre", 80m, branchIds, chargeIds);

    private static DateTime IndiaLocal(int year, int month, int day) =>
        DateTime.SpecifyKind(new DateTime(year, month, day, 10, 0, 0), DateTimeKind.Unspecified);

    private static async Task<CatalogueHarness> CreateHarnessAsync()
    {
        var db = CreateDb();
        var category = new ProductCategory("MILK", "Milk", "Milk products.");
        category.Activate();
        var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
        var admin = new User(UserType.Employee);
        db.ProductCategories.Add(category);
        db.Branches.Add(branch);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
        var storage = new CapturingProductMediaStorage();
        return new CatalogueHarness(
            db,
            category,
            branch,
            admin,
            storage,
            new CatalogueService(
                db,
                new ProductImageValidator(Options.Create(new MilkTestMediaOptions())),
                storage,
                new TestClock(DateTime.SpecifyKind(new DateTime(2026, 9, 27, 10, 0, 0), DateTimeKind.Unspecified))));
    }

    private static DoodhDirectDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
            .UseInMemoryDatabase($"catalogue-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new DoodhDirectDbContext(options);
    }

    // -----------------------------------------------------------------
    // Product image management
    // -----------------------------------------------------------------

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];

    private static async Task<CatalogueHarness> CreateImageHarnessAsync() =>
        await CatalogueHarness.CreateAsync();

    private static async Task<ProductResult> CreateActiveProductAsync(CatalogueHarness harness)
    {
        var product = await harness.Service.CreateProductAsync(
            ValidProduct(harness.Category.PublicId, "MILK-IMG-001", harness.Branch.PublicId),
            CancellationToken.None);
        await harness.Service.SetProductActiveAsync(product.PublicId, true, CancellationToken.None);
        return product;
    }

    [Fact]
    public async Task ProductImage_AbsentByDefault_AndPublicUrlIsNull()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);

        Assert.Null(product.ImageUrl);
        var open = await harness.Service.OpenProductImageAsync(product.PublicId, CancellationToken.None);
        Assert.Null(open);
    }

    [Fact]
    public async Task Upload_SetsImageUrl_AndPersistsImageRow()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);

        var result = await harness.Service.UpsertProductImageAsync(
            harness.AdminActor,
            product.PublicId,
            new MemoryStream(PngBytes),
            "milk.png",
            "image/png",
            CancellationToken.None);

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(PngBytes.LongLength, result.FileSize);
        Assert.Equal("milk.png", result.FileName);

        var refreshed = await harness.Service.GetProductForAdministrationAsync(product.PublicId, CancellationToken.None);
        Assert.Equal($"/api/v1/products/{product.PublicId}/image", refreshed.ImageUrl);

        var row = await harness.Db.ProductImages.SingleAsync();
        Assert.StartsWith("product-images/", row.StorageKey);
        Assert.DoesNotContain("milk", row.StorageKey, StringComparison.Ordinal);
        Assert.Equal(harness.Admin.Id, row.UploadedByUserId);

        await using var open = await harness.Service.OpenProductImageAsync(product.PublicId, CancellationToken.None);
        Assert.NotNull(open);
        Assert.Equal("image/png", open!.ContentType);
        using var reader = new BinaryReader(open.Content);
        var bytes = reader.ReadBytes((int)open.FileSize);
        Assert.Equal(PngBytes, bytes);
    }

    [Fact]
    public async Task Replace_KeepsOldBlobUntilCommit_ThenDeletesIt()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);
        await harness.Service.UpsertProductImageAsync(
            harness.AdminActor, product.PublicId, new MemoryStream(PngBytes), "first.png", "image/png", CancellationToken.None);
        var firstKey = (await harness.Db.ProductImages.SingleAsync()).StorageKey;

        var storage = (CapturingProductMediaStorage)harness.ServiceStorage;
        var result = await harness.Service.UpsertProductImageAsync(
            harness.AdminActor, product.PublicId, new MemoryStream(JpegBytes), "second.jpg", "image/jpeg", CancellationToken.None);

        Assert.Equal("image/jpeg", result.ContentType);
        var row = await harness.Db.ProductImages.SingleAsync();
        Assert.NotEqual(firstKey, row.StorageKey);
        // The old blob was deleted only after the commit; the new blob remains.
        Assert.Contains(firstKey, storage.DeletedKeys);
        Assert.DoesNotContain(row.StorageKey, storage.DeletedKeys);
        Assert.Contains(row.StorageKey, storage.SavedKeys);
    }

    [Fact]
    public async Task InvalidMagicBytes_AreRejected_AndNoBlobIsStored()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);
        var storage = (CapturingProductMediaStorage)harness.ServiceStorage;

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UpsertProductImageAsync(
                harness.AdminActor,
                product.PublicId,
                new MemoryStream("an executable, not an image"u8.ToArray()),
                "evil.png",
                "image/png",
                CancellationToken.None));

        Assert.Empty(await harness.Db.ProductImages.ToListAsync());
        Assert.Empty(storage.SavedKeys);
    }

    [Fact]
    public async Task DeclaredMimeMismatch_IsRejected()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UpsertProductImageAsync(
                harness.AdminActor,
                product.PublicId,
                new MemoryStream(PngBytes),
                "photo.png",
                "image/jpeg",
                CancellationToken.None));

        Assert.Empty(await harness.Db.ProductImages.ToListAsync());
    }

    [Fact]
    public async Task OversizedImage_IsRejected()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);

        await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.UpsertProductImageAsync(
                harness.AdminActor,
                product.PublicId,
                new MemoryStream(new byte[11 * 1024 * 1024]),
                "huge.png",
                "image/png",
                CancellationToken.None));

        Assert.Empty(await harness.Db.ProductImages.ToListAsync());
    }

    [Fact]
    public async Task PathTraversalFileName_IsSanitized()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);

        var result = await harness.Service.UpsertProductImageAsync(
            harness.AdminActor,
            product.PublicId,
            new MemoryStream(PngBytes),
            "../untrusted/photo.png",
            "image/png",
            CancellationToken.None);

        Assert.Equal("photo.png", result.FileName);
        var row = await harness.Db.ProductImages.SingleAsync();
        Assert.DoesNotContain("..", row.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replace_PreCommitFailure_DeletesNewBlob_AndKeepsOldReference()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);
        await harness.Service.UpsertProductImageAsync(
            harness.AdminActor, product.PublicId, new MemoryStream(PngBytes), "first.png", "image/png", CancellationToken.None);
        var firstKey = (await harness.Db.ProductImages.SingleAsync()).StorageKey;

        var storage = (CapturingProductMediaStorage)harness.ServiceStorage;
        // The blob stores successfully but the post-save size verification
        // fails BEFORE the database commit — the exact compensating-cleanup
        // window the safety pattern must cover.
        storage.ReturnMismatchedSize = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.UpsertProductImageAsync(
                harness.AdminActor, product.PublicId, new MemoryStream(JpegBytes), "second.jpg", "image/jpeg", CancellationToken.None));

        // The database still points at the original image.
        var row = await harness.Db.ProductImages.AsNoTracking().SingleAsync();
        Assert.Equal(firstKey, row.StorageKey);
        // The newly stored blob was removed by the compensating cleanup, and
        // the previous blob was never deleted.
        Assert.Contains(storage.SavedKeys[^1], storage.DeletedKeys);
        Assert.DoesNotContain(firstKey, storage.DeletedKeys);
    }

    [Fact]
    public async Task StorageSaveFailure_LeavesNoBlob_NoRow_AndPreviousImageIntact()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);
        await harness.Service.UpsertProductImageAsync(
            harness.AdminActor, product.PublicId, new MemoryStream(PngBytes), "first.png", "image/png", CancellationToken.None);
        var firstKey = (await harness.Db.ProductImages.SingleAsync()).StorageKey;

        var storage = (CapturingProductMediaStorage)harness.ServiceStorage;
        storage.FailNextSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.UpsertProductImageAsync(
                harness.AdminActor, product.PublicId, new MemoryStream(JpegBytes), "second.jpg", "image/jpeg", CancellationToken.None));

        // Nothing was stored, nothing was compensated, and the previous
        // image row and blob are untouched.
        Assert.False(storage.SavedKeys.Contains(firstKey, StringComparer.Ordinal) && storage.DeletedKeys.Contains(firstKey));
        Assert.NotNull(storage.FailedSaveKey);
        var row = await harness.Db.ProductImages.AsNoTracking().SingleAsync();
        Assert.Equal(firstKey, row.StorageKey);
        Assert.DoesNotContain(firstKey, storage.DeletedKeys);
    }

    [Fact]
    public async Task Remove_DeletesDatabaseState_BeforeBlobCleanup_AndPublicUrlBecomesNull()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);
        await harness.Service.UpsertProductImageAsync(
            harness.AdminActor, product.PublicId, new MemoryStream(PngBytes), "gone.png", "image/png", CancellationToken.None);
        var key = (await harness.Db.ProductImages.SingleAsync()).StorageKey;
        var storage = (CapturingProductMediaStorage)harness.ServiceStorage;
        storage.DeletedKeys.Clear();

        var removed = await harness.Service.RemoveProductImageAsync(harness.AdminActor, product.PublicId, CancellationToken.None);

        Assert.Equal("gone.png", removed.FileName);
        Assert.Empty(await harness.Db.ProductImages.ToListAsync());
        var refreshed = await harness.Service.GetProductForAdministrationAsync(product.PublicId, CancellationToken.None);
        Assert.Null(refreshed.ImageUrl);
        Assert.Contains(key, storage.DeletedKeys);
        Assert.Null(await harness.Service.OpenProductImageAsync(product.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task Remove_WithoutImage_IsNotFound()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.RemoveProductImageAsync(harness.AdminActor, product.PublicId, CancellationToken.None));
    }

    [Fact]
    public async Task ImageOperations_AreAudited()
    {
        await using var harness = await CreateImageHarnessAsync();
        var product = await CreateActiveProductAsync(harness);

        await harness.Service.UpsertProductImageAsync(
            harness.AdminActor, product.PublicId, new MemoryStream(PngBytes), "audit.png", "image/png", CancellationToken.None);
        await harness.Service.UpsertProductImageAsync(
            harness.AdminActor, product.PublicId, new MemoryStream(JpegBytes), "audit2.jpg", "image/jpeg", CancellationToken.None);
        await harness.Service.RemoveProductImageAsync(harness.AdminActor, product.PublicId, CancellationToken.None);

        var actions = await harness.Db.AuditLogs
            .Where(log => log.EntityType == "Product")
            .Select(log => log.Action)
            .ToListAsync();
        Assert.Contains("PRODUCT.IMAGE_UPLOAD", actions);
        Assert.Contains("PRODUCT.IMAGE_REPLACE", actions);
        Assert.Contains("PRODUCT.IMAGE_REMOVE", actions);
    }

    [Fact]
    public async Task Upload_ForUnknownProduct_IsNotFound()
    {
        await using var harness = await CreateImageHarnessAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Service.UpsertProductImageAsync(
                harness.AdminActor,
                Guid.NewGuid(),
                new MemoryStream(PngBytes),
                "milk.png",
                "image/png",
                CancellationToken.None));
    }

    /// <summary>In-memory media storage that records saved/deleted keys so the
    /// replace/remove ordering can be observed without touching the filesystem.</summary>
    private sealed class CapturingProductMediaStorage : IMediaStorage
    {
        private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _blobs = new();

        public List<string> SavedKeys { get; } = [];
        public List<string> DeletedKeys { get; } = [];

        /// <summary>Simulates a storage write that succeeds but reports a size
        /// inconsistent with the written bytes (the pre-commit verification
        /// failure window).</summary>
        public bool ReturnMismatchedSize { get; set; }

        public bool FailNextSave { get; set; }
        public string? FailedSaveKey { get; private set; }

        public Task<StoredMediaResult> SaveAsync(
            string storageKey,
            Stream content,
            string contentType,
            CancellationToken cancellationToken)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                FailedSaveKey = storageKey;
                throw new InvalidOperationException("simulated storage outage");
            }

            using var buffer = new MemoryStream();
            content.CopyTo(buffer);
            var bytes = buffer.ToArray();
            _blobs[storageKey] = (bytes, contentType);
            SavedKeys.Add(storageKey);
            return Task.FromResult(new StoredMediaResult(
                storageKey,
                contentType,
                ReturnMismatchedSize ? bytes.LongLength + 1 : bytes.LongLength));
        }

        public Task<StoredMediaContent> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
        {
            if (!_blobs.TryGetValue(storageKey, out var blob))
            {
                throw new NotFoundException("The test image content was not found.");
            }

            Stream content = new MemoryStream(blob.Bytes, writable: false);
            return Task.FromResult(new StoredMediaContent(content, blob.ContentType, blob.Bytes.LongLength));
        }

        public Task DeleteIfExistsAsync(string storageKey, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(storageKey);
            _blobs.Remove(storageKey);
            return Task.CompletedTask;
        }
    }

    private sealed class CatalogueHarness(
        DoodhDirectDbContext db,
        ProductCategory category,
        Branch branch,
        User admin,
        CapturingProductMediaStorage storage,
        CatalogueService service) : IAsyncDisposable
    {
        public DoodhDirectDbContext Db { get; } = db;
        public ProductCategory Category { get; } = category;
        public Branch Branch { get; } = branch;
        public User Admin { get; } = admin;
        public CapturingProductMediaStorage ServiceStorage { get; } = storage;
        public CatalogueService Service { get; } = service;

        public ProductImageActor AdminActor => new(Admin.Id);

        public static Task<CatalogueHarness> CreateAsync() => CreateHarnessAsync();

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
