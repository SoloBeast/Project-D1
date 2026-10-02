using System.Data;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Infrastructure.Orders;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DoodhDirect.Api.IntegrationTests;

public sealed class ChargeServiceTests
{
    private static readonly TestClock Frozen = new(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Unspecified));

    // ---------------------------------------------------------------- master CRUD

    [Fact]
    public async Task Create_PersistsTrimmedActiveCharge_AndWritesAudit()
    {
        await using var db = ChargeDb.Create();
        var time = new TestClock(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Unspecified));
        var service = new ChargeService(db, new TestIndiaTimeProvider(time));

        var result = await service.CreateAsync(
            new CreateChargeRequest("  GST  ", " GST-5 ", "Goods and services tax", 5m),
            actorUserId: 11,
            CancellationToken.None);

        Assert.Equal("GST", result.ChargeType);
        Assert.Equal("GST-5", result.ChargeCode);
        Assert.Equal("Goods and services tax", result.Description);
        Assert.Equal(5m, result.Percentage);
        Assert.True(result.IsActive);
        Assert.False(result.IsUsed);

        var stored = await db.Charges.SingleAsync();
        Assert.True(stored.IsActive);
        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal(ChargeService.ActionCreated, audit.Action);
        Assert.Equal("Charge", audit.EntityType);
        Assert.Equal(11, audit.UserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.01)]
    public async Task Create_RejectsPercentageOutsideBounds(decimal percentage)
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));

        var exception = await Assert.ThrowsAsync<ValidationAppException>(
            () => service.CreateAsync(
                new CreateChargeRequest("GST", "GST-5", null, percentage),
                11,
                CancellationToken.None));

        Assert.Contains("Percentage must be greater than 0", exception.Message);
        Assert.Empty(await db.Charges.ToListAsync());
    }

    [Fact]
    public async Task Create_RejectsPercentageWithMoreThanTwoDecimals()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));

        await Assert.ThrowsAsync<ValidationAppException>(
            () => service.CreateAsync(
                new CreateChargeRequest("GST", "GST-5", null, 2.555m),
                11,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsMissingChargeCode(string? chargeCode)
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));

        await Assert.ThrowsAsync<ValidationAppException>(
            () => service.CreateAsync(
                new CreateChargeRequest("GST", chargeCode!, null, 5m),
                11,
                CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsDuplicateCode_IgnoringCase()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m), 11, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.CreateAsync(
                new CreateChargeRequest("GST", "gst-5", null, 6m),
                11,
                CancellationToken.None));

        Assert.Contains("already exists", exception.Message);
        Assert.Equal(1, await db.Charges.CountAsync());
    }

    [Fact]
    public async Task Update_ChangesEditableFields_ButFreezesTypeOnceUsed()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var created = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m), 11, CancellationToken.None);

        var updated = await service.UpdateAsync(
            created.PublicId,
            new UpdateChargeRequest("GST", "Updated description", 6.5m),
            11,
            CancellationToken.None);

        Assert.Equal(6.5m, updated.Percentage);
        Assert.Equal("Updated description", updated.Description);
        Assert.Contains(ChargeService.ActionUpdated, (await db.AuditLogs.ToListAsync()).Select(a => a.Action));
    }

    [Fact]
    public async Task Update_RejectsTypeChangeOnceReferenced()
    {
        await using var db = ChargeDb.Create();
        var time = new TestIndiaTimeProvider(Frozen);
        var service = new ChargeService(db, time);
        var created = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m), 11, CancellationToken.None);

        db.OrderCharges.Add(new OrderCharge(
            "GST", "GST-5", null, 5m, 100m, 5m));
        await db.SaveChangesAsync();
        var master = await db.Charges.SingleAsync();
        master.MarkUsed();
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.UpdateAsync(
                created.PublicId,
                new UpdateChargeRequest("Tax", null, 6m),
                11,
                CancellationToken.None));

        Assert.Contains("no longer change", exception.Message);
    }

    [Fact]
    public async Task SetActive_TogglesApplicability_AndWritesDistinctAuditActions()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var created = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m), 11, CancellationToken.None);

        var deactivated = await service.SetActiveAsync(created.PublicId, false, 11, CancellationToken.None);
        Assert.False(deactivated.IsActive);
        var activated = await service.SetActiveAsync(created.PublicId, true, 11, CancellationToken.None);
        Assert.True(activated.IsActive);

        var actions = (await db.AuditLogs.ToListAsync()).Select(audit => audit.Action).ToArray();
        Assert.Contains(ChargeService.ActionDeactivated, actions);
        Assert.Contains(ChargeService.ActionActivated, actions);
    }

    [Fact]
    public async Task Delete_RemovesUnusedCharge_ButRefusesReferencedCharge()
    {
        await using var db = ChargeDb.Create();
        var time = new TestIndiaTimeProvider(Frozen);
        var service = new ChargeService(db, time);
        var unused = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-UNUSED", null, 5m), 11, CancellationToken.None);
        var referenced = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-USED", null, 5m), 11, CancellationToken.None);

        db.OrderCharges.Add(new OrderCharge("GST", "GST-USED", null, 5m, 100m, 5m));
        await db.SaveChangesAsync();

        await service.DeleteAsync(unused.PublicId, 11, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.DeleteAsync(referenced.PublicId, 11, CancellationToken.None));

        Assert.Contains("only be deactivated", exception.Message);
        Assert.Single(await db.Charges.ToListAsync());
        Assert.Contains(ChargeService.ActionDeleted, (await db.AuditLogs.ToListAsync()).Select(a => a.Action));
    }

    [Fact]
    public async Task List_OrdersByTypeThenCode()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        await service.CreateAsync(new CreateChargeRequest("Service", "SC", null, 5m), 11, CancellationToken.None);
        await service.CreateAsync(new CreateChargeRequest("GST", "SGST", null, 2.5m), 11, CancellationToken.None);
        await service.CreateAsync(new CreateChargeRequest("GST", "CGST", null, 2.5m), 11, CancellationToken.None);

        var list = await service.ListAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "CGST", "SGST", "SC" },
            list.Select(charge => charge.ChargeCode).ToArray());
    }

    [Fact]
    public async Task List_ReportsAssignedProductCounts()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var mapped = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-P", null, 5m, ApplicableOnAll: false),
            11,
            CancellationToken.None);
        await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m),
            11,
            CancellationToken.None);
        await SeedMappedProductAsync(db, "GST-P");
        await SeedMappedProductAsync(db, "GST-P");

        var list = await service.ListAsync(CancellationToken.None);

        Assert.Equal(2, list.Single(charge => charge.PublicId == mapped.PublicId).ProductCount);
        Assert.Equal(0, list.Single(charge => charge.ChargeCode == "GST-5").ProductCount);
        // Single-record reads carry the same count so the toggle guard never
        // works from a stale zero.
        Assert.Equal(2, (await service.GetAsync(mapped.PublicId, CancellationToken.None)).ProductCount);
    }

    // ---------------------------------------------------------------- applicability mode

    [Fact]
    public async Task Create_DefaultsToGloballyApplicable()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));

        var result = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m),
            11,
            CancellationToken.None);

        Assert.True(result.ApplicableOnAll);
    }

    [Fact]
    public async Task Create_ItemLevelCharge_WhenApplicableOnAllFalse()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));

        var result = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-P", null, 5m, ApplicableOnAll: false),
            11,
            CancellationToken.None);

        Assert.False(result.ApplicableOnAll);
        var stored = await db.Charges.SingleAsync();
        Assert.False(stored.ApplicableOnAll);
    }

    [Fact]
    public async Task SetApplicableOnAll_GlobalToItem_SucceedsAndAudits()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var charge = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m),
            11,
            CancellationToken.None);

        var updated = await service.SetApplicableOnAllAsync(charge.PublicId, false, 11, CancellationToken.None);

        Assert.False(updated.ApplicableOnAll);
        // CHARGE.CREATED (create) + CHARGE.UPDATED (the applicability toggle).
        Assert.Equal(2, await db.AuditLogs.CountAsync());
        var audit = await db.AuditLogs.SingleAsync(item => item.Action == ChargeService.ActionUpdated);
        Assert.Equal("Charge", audit.EntityType);
    }

    [Fact]
    public async Task SetApplicableOnAll_ItemToGlobal_WithZeroMappings_Succeeds()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var charge = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-P", null, 5m, ApplicableOnAll: false),
            11,
            CancellationToken.None);

        var updated = await service.SetApplicableOnAllAsync(charge.PublicId, true, 11, CancellationToken.None);

        Assert.True(updated.ApplicableOnAll);
    }

    [Fact]
    public async Task SetApplicableOnAll_ItemToGlobal_WithMapping_IsRefused_AndModeUnchanged()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var charge = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-P", null, 5m, ApplicableOnAll: false),
            11,
            CancellationToken.None);
        await SeedMappedProductAsync(db, "GST-P");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.SetApplicableOnAllAsync(charge.PublicId, true, 11, CancellationToken.None));

        Assert.Contains(
            "Cannot enable Applicable on All because this charge is assigned to one or more products",
            exception.Message);
        Assert.False((await db.Charges.SingleAsync()).ApplicableOnAll);
        Assert.Single(db.ProductCharges);
    }

    [Fact]
    public async Task SetApplicableOnAll_ItemToGlobal_WithMultipleMappings_IsRefused()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var charge = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-P", null, 5m, ApplicableOnAll: false),
            11,
            CancellationToken.None);
        await SeedMappedProductAsync(db, "GST-P");
        await SeedMappedProductAsync(db, "GST-P");

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.SetApplicableOnAllAsync(charge.PublicId, true, 11, CancellationToken.None));

        Assert.Equal(2, await db.ProductCharges.CountAsync());
    }

    [Fact]
    public async Task SetApplicableOnAll_WritesAuditSnapshotWithApplicability()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var charge = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", null, 5m),
            11,
            CancellationToken.None);

        await service.SetApplicableOnAllAsync(charge.PublicId, false, 11, CancellationToken.None);

        var audit = await db.AuditLogs.SingleAsync(item => item.Action == ChargeService.ActionUpdated);
        Assert.Contains("\"ApplicableOnAll\":true", audit.OldValueJson);
        Assert.Contains("\"ApplicableOnAll\":false", audit.NewValueJson);
    }

    [Fact]
    public async Task Delete_ChargeWithProductMappings_IsRefused()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var charge = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-P", null, 5m, ApplicableOnAll: false),
            11,
            CancellationToken.None);
        await SeedMappedProductAsync(db, "GST-P");

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(
            () => service.DeleteAsync(charge.PublicId, 11, CancellationToken.None));

        Assert.Contains(
            "This charge is assigned to one or more products",
            exception.Message);
        Assert.Single(await db.Charges.ToListAsync());
    }

    [Fact]
    public async Task Delete_UnmappedUnusedCharge_Succeeds()
    {
        await using var db = ChargeDb.Create();
        var service = new ChargeService(db, new TestIndiaTimeProvider(Frozen));
        var charge = await service.CreateAsync(
            new CreateChargeRequest("GST", "GST-P", null, 5m, ApplicableOnAll: false),
            11,
            CancellationToken.None);

        await service.DeleteAsync(charge.PublicId, 11, CancellationToken.None);

        Assert.Empty(await db.Charges.ToListAsync());
    }

    /// <summary>Adds one product with a single charge mapping row (distinct SKU per call).</summary>
    private static async Task SeedMappedProductAsync(DoodhDirectDbContext db, string chargeCode)
    {
        var charge = await db.Charges.SingleAsync(item => item.ChargeCode == chargeCode);
        var product = new Product(
            categoryId: 1,
            sku: $"MILK-{Guid.NewGuid():N}"[..8].ToUpperInvariant(),
            name: "Fresh Milk",
            description: null,
            unitOfMeasure: "litre",
            price: 80m);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ProductCharges.Add(new ProductCharge(product.Id, charge.Id));
        await db.SaveChangesAsync();
    }

    private sealed class ChargeDb
    {
        public static DoodhDirectDbContext Create()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"charge-tests-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new DoodhDirectDbContext(options, new TestIndiaTimeProvider(Frozen));
        }
    }
}

public sealed class CheckoutChargeTests
{
    private static readonly TestClock Frozen = new(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Unspecified));

    [Fact]
    public async Task Preview_NoActiveCharges_TotalEqualsSubtotal()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal(80m, result.Subtotal);
        Assert.Empty(result.Charges);
        Assert.Equal(0m, result.ChargesTotal);
        Assert.Equal(80m, result.PayableAmount);
    }

    [Fact]
    public async Task Preview_OneActiveCharge_RoundsIndependentlyPerApprovedRule()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "GST-5", "GST at five percent", 5m));
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 2.5m);

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal(200m, result.Subtotal);
        var charge = Assert.Single(result.Charges);
        Assert.Equal("GST-5", charge.ChargeCode);
        Assert.Equal(200m, charge.BaseAmount);
        Assert.Equal(10m, charge.Amount);
        Assert.Equal(10m, result.ChargesTotal);
        Assert.Equal(210m, result.PayableAmount);
    }

    [Fact]
    public async Task Preview_MultipleActiveCharges_AllApplyToSameBase_AndSumRoundedAmounts()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "CGST", null, 2.5m));
        harness.Db.Charges.Add(new Charge("GST", "SGST", null, 2.5m));
        harness.Db.Charges.Add(new Charge("Service", "SC", null, 5m));
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal(80m, result.Subtotal);
        Assert.Equal(3, result.Charges.Count);
        Assert.All(result.Charges, charge => Assert.Equal(80m, charge.BaseAmount));
        // 80 × 2.5% = 2.00 each; 80 × 5% = 4.00. Sum of rounded amounts = 8.00.
        Assert.Equal(8m, result.ChargesTotal);
        Assert.Equal(88m, result.PayableAmount);
    }

    [Fact]
    public async Task Preview_InactiveCharge_IsExcluded()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var inactive = new Charge("GST", "GST-OLD", null, 18m);
        inactive.Deactivate();
        harness.Db.Charges.Add(inactive);
        harness.Db.Charges.Add(new Charge("GST", "GST-5", null, 5m));
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        var charge = Assert.Single(result.Charges);
        Assert.Equal("GST-5", charge.ChargeCode);
        Assert.Equal(84m, result.PayableAmount);
    }

    [Fact]
    public async Task Preview_ChargeOnDiscountedSubtotal_UsesPostDiscountBase()
    {
        // Discount is currently hard-coded to zero in the calculation; the base
        // contract (subtotal − discount) is pinned here so a future discount rule
        // cannot silently move the taxable base.
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "GST-5", null, 5m));
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var preview = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal(0m, preview.DiscountAmount);
        Assert.Equal(80m, Assert.Single(preview.Charges).BaseAmount);
    }

    [Fact]
    public async Task Create_FreezesSnapshot_MarksMastersUsed_AndPayableBecomesChargeInclusive()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "CGST", null, 2.5m));
        harness.Db.Charges.Add(new Charge("GST", "SGST", null, 2.5m));
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "charge-checkout-key", CancellationToken.None);

        Assert.Equal(80m, order.Subtotal);
        Assert.Equal(2, order.Charges.Count);
        Assert.All(order.Charges, charge => Assert.Equal(80m, charge.BaseAmount));
        Assert.Equal(2m, order.Charges.Single(c => c.ChargeCode == "CGST").Amount);
        Assert.Equal(2m, order.Charges.Single(c => c.ChargeCode == "SGST").Amount);
        Assert.Equal(4m, order.ChargesTotal);
        Assert.Equal(84m, order.PayableAmount);

        var storedOrder = await harness.Db.Orders.Include(o => o.Charges).SingleAsync();
        Assert.Equal(2, storedOrder.Charges.Count);
        Assert.Equal(84m, storedOrder.PayableAmount);
        var masters = await harness.Db.Charges.ToListAsync();
        Assert.All(masters, charge => Assert.True(charge.IsUsed));
    }

    [Fact]
    public async Task Create_IdempotentRetry_DoesNotDuplicateOrderChargeRows()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "GST-5", null, 5m));
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.Product);
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var first = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "same-idempotency-key", CancellationToken.None);
        var replay = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "same-idempotency-key", CancellationToken.None);

        Assert.Equal(first.PublicId, replay.PublicId);
        Assert.Equal(first.PayableAmount, replay.PayableAmount);
        var chargeRows = await harness.Db.Set<OrderCharge>().ToListAsync();
        // Global + mapped: two frozen rows, neither duplicated by the replay.
        Assert.Equal(2, chargeRows.Count);
        Assert.Equal(8m, replay.ChargesTotal);
        Assert.Single(await harness.Db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Preview_And_Create_ProduceIdenticalChargeCalculations()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "CGST", null, 2.5m));
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.SecondProduct);
        var request = harness.RequestLines(
            harness.Address.PublicId,
            (harness.Product.PublicId, 2.5m),
            (harness.SecondProduct.PublicId, 1m));

        var preview = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "preview-parity-key", CancellationToken.None);

        // Global base 300.25 → 7.51; mapped base 100.25 → 5.01.
        Assert.Equal(7.51m, preview.Charges.Single(c => c.ChargeCode == "CGST").Amount);
        Assert.Equal(5.01m, preview.Charges.Single(c => c.ChargeCode == "GST-MAP").Amount);
        Assert.Equal(preview.ChargesTotal, order.ChargesTotal);
        Assert.Equal(preview.PayableAmount, order.PayableAmount);
        Assert.Equal(
            preview.Charges.Select(c => (c.ChargeCode, c.BaseAmount, c.Amount)).ToArray(),
            order.Charges.Select(c => (c.ChargeCode, c.BaseAmount, c.Amount)).ToArray());
    }

    [Fact]
    public async Task HistoricalOrder_IsUnaffectedByMasterEditsOrDeactivation()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var chargeService = new ChargeService(
            harness.Db, new TestIndiaTimeProvider(Frozen));
        var master = await chargeService.CreateAsync(
            new CreateChargeRequest("GST", "GST-5", "GST 5", 5m), 11, CancellationToken.None);
        var mapped = await chargeService.CreateAsync(
            new CreateChargeRequest("GST", "GST-ITEM", "Item GST", 10m, ApplicableOnAll: false),
            11,
            CancellationToken.None);
        await harness.SeedProductChargeAsync(
            (await harness.Db.Charges.SingleAsync(charge => charge.ChargeCode == "GST-ITEM")),
            harness.Product);
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "history-key", CancellationToken.None);
        // Global 4.00 + mapped 8.00 on the same 80 base.
        Assert.Equal(92m, order.PayableAmount);

        // Tomorrow: admin raises both percentages, deactivates both records,
        // and removes the ProductCharge mapping entirely.
        await chargeService.UpdateAsync(
            master.PublicId,
            new UpdateChargeRequest("GST", "GST 5", 6m),
            11,
            CancellationToken.None);
        await chargeService.SetActiveAsync(master.PublicId, false, 11, CancellationToken.None);
        await chargeService.UpdateAsync(
            mapped.PublicId,
            new UpdateChargeRequest("GST", "Item GST", 15m),
            11,
            CancellationToken.None);
        await chargeService.SetActiveAsync(mapped.PublicId, false, 11, CancellationToken.None);
        var mapping = await harness.Db.ProductCharges.SingleAsync();
        harness.Db.ProductCharges.Remove(mapping);
        await harness.Db.SaveChangesAsync();

        var historical = await harness.Service.GetAsync(
            harness.Customer.Id, order.PublicId, bypassOwnership: false, CancellationToken.None);
        Assert.Equal(2, historical.Charges.Count);
        var snapshotGlobal = historical.Charges.Single(charge => charge.ChargeCode == "GST-5");
        Assert.Equal(5m, snapshotGlobal.Percentage);
        Assert.Equal(4m, snapshotGlobal.Amount);
        Assert.Equal(80m, snapshotGlobal.BaseAmount);
        var snapshotMapped = historical.Charges.Single(charge => charge.ChargeCode == "GST-ITEM");
        Assert.Equal(10m, snapshotMapped.Percentage);
        Assert.Equal(8m, snapshotMapped.Amount);
        Assert.Equal(80m, snapshotMapped.BaseAmount);
        Assert.Equal(12m, historical.ChargesTotal);
        Assert.Equal(92m, historical.PayableAmount);
        // The master-side mapping removal is real — the snapshot is what froze it.
        Assert.Empty(await harness.Db.ProductCharges.ToListAsync());
    }

    [Fact]
    public async Task HistoricalOrder_RowsSumExactlyToChargesTotal()
    {
        await using var harness = await OrderHarness.CreateAsync();
        // Chosen to exercise rounding: 80 × 2.555% is rejected at 2dp, so use
        // percentages whose raw products have sub-paise remainders.
        harness.Db.Charges.Add(new Charge("GST", "CGST", null, 2.5m));
        harness.Db.Charges.Add(new Charge("GST", "SGST", null, 2.51m));
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "sum-key", CancellationToken.None);

        var stored = await harness.Db.Orders.Include(o => o.Charges).SingleAsync();
        Assert.Equal(
            stored.Charges.Sum(charge => charge.Amount),
            stored.ChargesTotal);
        Assert.Equal(
            stored.Subtotal - stored.DiscountAmount + stored.ChargesTotal,
            stored.PayableAmount);
    }

    // -----------------------------------------------------------------
    // Stage 2: GLOBAL + PRODUCT-MAPPED one-time order charge calculation
    // -----------------------------------------------------------------

    [Fact]
    public async Task MappedCharge_AppliesOnlyToMatchingProduct()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.SecondProduct);
        var request = harness.RequestLines(
            harness.Address.PublicId,
            (harness.Product.PublicId, 1m),
            (harness.SecondProduct.PublicId, 1m));

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Equal(180.25m, result.Subtotal);
        var charge = Assert.Single(result.Charges);
        Assert.Equal("GST-MAP", charge.ChargeCode);
        // Only the mapped product's line total feeds the base — the unrelated
        // ₹80 line contributes nothing.
        Assert.Equal(100.25m, charge.BaseAmount);
        Assert.Equal(5.01m, charge.Amount);
        Assert.Equal(5.01m, result.ChargesTotal);
        Assert.Equal(185.26m, result.PayableAmount);
    }

    [Fact]
    public async Task MappedCharge_IgnoresUnrelatedProduct()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.SecondProduct);
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        Assert.Empty(result.Charges);
        Assert.Equal(0m, result.ChargesTotal);
        Assert.Equal(80m, result.PayableAmount);
    }

    [Fact]
    public async Task MappedInactiveCharge_IsExcluded_ButMappingRemains()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.Product);
        mapped.Deactivate();
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);

        var result = await harness.Service.PreviewAsync(harness.Customer.Id, request, CancellationToken.None);

        // Inactive charges never apply…
        Assert.Empty(result.Charges);
        Assert.Equal(80m, result.PayableAmount);
        // …but the mapping is configuration and survives deactivation.
        Assert.Equal(1, await harness.Db.ProductCharges.CountAsync());
        Assert.False((await harness.Db.Charges.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task MappedCharge_OnBothProducts_AggregatesToOneOrderChargeRow()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.Product, harness.SecondProduct);
        var request = harness.RequestLines(
            harness.Address.PublicId,
            (harness.Product.PublicId, 1m),
            (harness.SecondProduct.PublicId, 2m));

        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "aggregate-key", CancellationToken.None);

        // One charge across two mapped lines: exactly ONE OrderCharge row whose
        // base is the sum of the established rounded line totals (80 + 200.50).
        Assert.Equal(280.50m, order.Subtotal);
        var charge = Assert.Single(order.Charges);
        Assert.Equal("GST-MAP", charge.ChargeCode);
        Assert.Equal(280.50m, charge.BaseAmount);
        // 280.50 × 5% = 14.025 → 14.03 (AwayFromZero, per-charge rounding).
        Assert.Equal(14.03m, charge.Amount);
        Assert.Equal(14.03m, order.ChargesTotal);
        Assert.Equal(294.53m, order.PayableAmount);

        var stored = await harness.Db.Orders.Include(o => o.Charges).SingleAsync();
        Assert.Single(stored.Charges);
        Assert.Equal(280.50m, stored.Charges.Single().BaseAmount);
    }

    [Fact]
    public async Task Global_And_MappedCharges_Coexist_WithDistinctBases()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "CGST", null, 2.5m));
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.SecondProduct);
        var request = harness.RequestLines(
            harness.Address.PublicId,
            (harness.Product.PublicId, 1m),
            (harness.SecondProduct.PublicId, 2m));

        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "coexist-key", CancellationToken.None);

        // Global uses the order-level base; mapped uses the matching-line base.
        Assert.Equal(2, order.Charges.Count);
        var global = order.Charges.Single(charge => charge.ChargeCode == "CGST");
        var mappedLine = order.Charges.Single(charge => charge.ChargeCode == "GST-MAP");
        Assert.Equal(280.50m, global.BaseAmount);
        Assert.Equal(7.01m, global.Amount);
        Assert.Equal(200.50m, mappedLine.BaseAmount);
        Assert.Equal(10.03m, mappedLine.Amount);
        // Deterministic ChargeType/ChargeCode ordering across the combined set.
        Assert.Equal(
            new[] { "CGST", "GST-MAP" },
            order.Charges.Select(charge => charge.ChargeCode).ToArray());
        Assert.Equal(17.04m, order.ChargesTotal);
        Assert.Equal(297.54m, order.PayableAmount);
    }

    [Fact]
    public async Task InvalidMasterData_GlobalWithMapping_ProducesExactlyOneRow_AndIsNotMutated()
    {
        await using var harness = await OrderHarness.CreateAsync();
        // Deliberate invariant violation seeded directly: global flag AND
        // product mappings. Checkout must apply the charge exactly once and
        // must NOT silently repair master data.
        var invalid = new Charge("GST", "GST-BAD", null, 5m, applicableOnAll: true);
        harness.Db.Charges.Add(invalid);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(invalid, harness.Product, harness.SecondProduct);
        var request = harness.RequestLines(
            harness.Address.PublicId,
            (harness.Product.PublicId, 1m),
            (harness.SecondProduct.PublicId, 2m));

        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "defense-key", CancellationToken.None);

        var stored = await harness.Db.Orders.Include(o => o.Charges).SingleAsync();
        var chargeRow = Assert.Single(stored.Charges);
        Assert.Equal("GST-BAD", chargeRow.ChargeCode);
        // The duplicate resolves as global — the charge's current master flag.
        Assert.Equal(280.50m, chargeRow.BaseAmount);
        Assert.Equal(14.03m, chargeRow.Amount);

        // Master data untouched: flag still true, both mappings still present.
        var master = await harness.Db.Charges.SingleAsync();
        Assert.True(master.ApplicableOnAll);
        Assert.Equal(2, await harness.Db.ProductCharges.CountAsync());
    }

    [Fact]
    public async Task MappedCharge_BaseUsesEstablishedRoundedLineTotals()
    {
        await using var harness = await OrderHarness.CreateAsync();
        var mapped = new Charge("GST", "GST-MAP", null, 5m, applicableOnAll: false);
        harness.Db.Charges.Add(mapped);
        await harness.Db.SaveChangesAsync();
        await harness.SeedProductChargeAsync(mapped, harness.SecondProduct);
        var request = harness.Request(harness.Address.PublicId, harness.SecondProduct.PublicId, 2.5m);

        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "paise-key", CancellationToken.None);

        // 2.5 × 100.25 = 250.625 → LineTotal 250.63 (2dp, AwayFromZero). The
        // mapped base is that ROUNDED line total, not the raw 250.625.
        var stored = await harness.Db.Orders.Include(o => o.Items).Include(o => o.Charges).SingleAsync();
        Assert.Equal(250.63m, stored.Items.Single().LineTotal);
        var charge = Assert.Single(stored.Charges);
        Assert.Equal(250.63m, charge.BaseAmount);
        Assert.Equal(12.53m, charge.Amount);
        Assert.Equal(12.53m, stored.ChargesTotal);
        Assert.Equal(263.16m, stored.PayableAmount);
    }

    [Fact]
    public async Task GetAsync_CustomerOrderResponse_CarriesChargeSnapshot()
    {
        await using var harness = await OrderHarness.CreateAsync();
        harness.Db.Charges.Add(new Charge("GST", "GST-5", "GST five", 5m));
        await harness.Db.SaveChangesAsync();
        var request = harness.Request(harness.Address.PublicId, harness.Product.PublicId, 1m);
        var order = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "detail-key", CancellationToken.None);

        var detail = await harness.Service.GetAsync(
            harness.Customer.Id, order.PublicId, bypassOwnership: false, CancellationToken.None);

        var charge = Assert.Single(detail.Charges);
        Assert.Equal("GST-5", charge.ChargeCode);
        Assert.Equal("GST five", charge.Description);
        Assert.Equal(5m, charge.Percentage);
        Assert.Equal(4m, charge.Amount);
        Assert.Equal(84m, detail.PayableAmount);
    }

    private sealed class OrderHarness : IAsyncDisposable
    {
        private OrderHarness(
            DoodhDirectDbContext db,
            User customer,
            CustomerAddress address,
            Product product,
            Product secondProduct,
            OrderService service)
        {
            Db = db;
            Customer = customer;
            Address = address;
            Product = product;
            SecondProduct = secondProduct;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public CustomerAddress Address { get; }
        public Product Product { get; }

        /// <summary>Second catalogue product (₹100.25/litre) so multi-line
        /// Stage 2 scenarios can exercise mapped-charge aggregation and the
        /// paise rounding of line totals.</summary>
        public Product SecondProduct { get; }

        public OrderService Service { get; }

        public CheckoutRequest Request(Guid addressId, Guid productId, decimal quantity) =>
            new(addressId, null, [new OrderItemRequest(productId, quantity)]);

        /// <summary>Multi-line checkout request (Stage 2 mapped-charge tests).
        /// Product ids must be distinct — OrderValidation rejects duplicates.</summary>
        public CheckoutRequest RequestLines(
            Guid addressId,
            params (Guid ProductId, decimal Quantity)[] items) =>
            new(addressId, null, [.. items.Select(item => new OrderItemRequest(item.ProductId, item.Quantity))]);

        /// <summary>Seeds ProductCharge mappings for a charge master (Stage 1
        /// configuration, bypassing the catalogue service — the checkout tests
        /// exercise the ORDER side of the Stage 2 design).</summary>
        public async Task SeedProductChargeAsync(Charge charge, params Product[] products)
        {
            foreach (var product in products)
            {
                Db.ProductCharges.Add(new ProductCharge(product.Id, charge.Id));
            }

            await Db.SaveChangesAsync();
        }

        public static async Task<OrderHarness> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"checkout-charge-tests-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var clock = new TestClock(new DateTime(2026, 9, 28, 10, 0, 0, DateTimeKind.Unspecified));
            var db = new DoodhDirectDbContext(options, new TestIndiaTimeProvider(clock));
            db.NumberSeries.Add(new NumberSeries(
                "ORDER", "Order Number", "ORD/{SCOPE}/{NUMBER:000000}", 1, 1, NumberSeriesResetPolicy.Never, "NEAR"));

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            var product = new Product(0, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            category.Products.Add(product);
            var secondProduct = new Product(0, "MILK-002", "Toned Milk", null, "litre", 100.25m);
            secondProduct.Activate();
            category.Products.Add(secondProduct);
            var address = new CustomerAddress(
                customer.Id, "Home", "1 Main Road", "Central", "Bengaluru", "Karnataka",
                "560001", "Customer", "9999999999", 12.9716m, 77.5946m);
            var branch = new Branch("NEAR", "Near Branch", "Bengaluru", "Karnataka", 12.9717m, 77.5947m);
            db.ProductCategories.Add(category);
            db.CustomerAddresses.Add(address);
            db.Branches.Add(branch);
            await db.SaveChangesAsync();

            var availability = new ProductBranch(product.Id, branch.Id, true, null);
            product.ProductBranches.Add(availability);
            branch.ProductBranches.Add(availability);
            db.ProductBranches.Add(availability);
            var secondAvailability = new ProductBranch(secondProduct.Id, branch.Id, true, null);
            secondProduct.ProductBranches.Add(secondAvailability);
            branch.ProductBranches.Add(secondAvailability);
            db.ProductBranches.Add(secondAvailability);
            await db.SaveChangesAsync();

            var timeProvider = new TestIndiaTimeProvider(clock);
            return new OrderHarness(
                db,
                customer,
                address,
                product,
                secondProduct,
                new OrderService(
                    db,
                    new BranchAllocationService(db),
                    new TestNotificationEventWriter(db, clock),
                    new NumberSeriesService(db, timeProvider),
                    timeProvider));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
