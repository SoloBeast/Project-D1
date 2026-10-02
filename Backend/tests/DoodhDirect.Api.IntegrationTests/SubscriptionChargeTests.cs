using DoodhDirect.Application.Common;
using DoodhDirect.Application.Notifications;
using DoodhDirect.Application.Orders;
using DoodhDirect.Application.Payments;
using DoodhDirect.Application.Subscriptions;
using DoodhDirect.Application.Wallets;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Configuration;
using DoodhDirect.Domain.Customer;
using DoodhDirect.Domain.Identity;
using DoodhDirect.Domain.Payments;
using DoodhDirect.Domain.Setup;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Domain.Wallets;
using DoodhDirect.Infrastructure.Payments;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Subscriptions;
using DoodhDirect.Infrastructure.Wallets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Stage 3: prepaid subscription charges calculated once at creation against
/// the complete prepaid product value. The capturing payment fake proves the
/// frozen payable flows to payment; real payment paths are covered by
/// <see cref="SubscriptionChargePaymentTests"/>.
/// </summary>
public sealed class SubscriptionChargeTests
{
    [Fact]
    public async Task GlobalCharge_AppliesToSubscription_WithProductValueBase()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        // Product value 1 × 80 × 4 = 320; 5% → 16; payable 336.
        Assert.Equal(336m, created.Subscription.PayableAmount);
        Assert.Equal(created.Subscription.PayableAmount, created.Payment.Amount);

        var (charges, total, payable) = await SnapshotStateAsync(harness);
        var charge = Assert.Single(charges);
        Assert.Equal("CGST", charge.ChargeCode);
        Assert.Equal(320m, charge.BaseAmount);
        Assert.Equal(16m, charge.Amount);
        Assert.Equal(16m, total);
        Assert.Equal(336m, payable);

        Assert.True((await harness.Db.Charges.AsNoTracking().SingleAsync()).IsUsed);
    }

    [Fact]
    public async Task InactiveGlobalCharge_IsIgnored()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.DeactivateChargeAsync(charge);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        Assert.Equal(320m, created.Subscription.PayableAmount);
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Empty(charges);
        Assert.Equal(0m, total);
        Assert.Equal(320m, payable);
        Assert.False((await harness.Db.Charges.AsNoTracking().SingleAsync()).IsUsed);
    }

    [Fact]
    public async Task MappedCharge_AppliesToMatchingProduct_WithProductValueBase()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "GST-MAP", "Mapped GST", 10m, applicableOnAll: false);
        await harness.SeedProductChargeAsync(charge, harness.Product);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        // 320 base; 10% → 32; payable 352.
        Assert.Equal(352m, created.Subscription.PayableAmount);
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        var snapshot = Assert.Single(charges);
        Assert.Equal("GST-MAP", snapshot.ChargeCode);
        Assert.Equal(320m, snapshot.BaseAmount);
        Assert.Equal(32m, snapshot.Amount);
        Assert.Equal(32m, total);
        Assert.Equal(352m, payable);
    }

    [Fact]
    public async Task MappedInactiveCharge_IsIgnored_AndMappingPreserved()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "GST-MAP", "Mapped GST", 10m, applicableOnAll: false);
        await harness.SeedProductChargeAsync(charge, harness.Product);
        await harness.DeactivateChargeAsync(charge);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        Assert.Equal(320m, created.Subscription.PayableAmount);
        Assert.Empty(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
        Assert.Equal(1, await harness.Db.ProductCharges.CountAsync());
        Assert.False((await harness.Db.Charges.AsNoTracking().SingleAsync()).IsUsed);
    }

    [Fact]
    public async Task MappedCharge_IgnoresUnrelatedProduct()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "GST-MAP", "Mapped GST", 10m, applicableOnAll: false);
        await harness.SeedProductChargeAsync(charge, harness.Product);
        var other = await harness.SecondProductAsync("GHEE-001", "Ghee", 700m);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(product: other), "subscription-charge-1", CancellationToken.None);

        Assert.Equal(2800m, created.Subscription.PayableAmount);
        Assert.Empty(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task GlobalAndMappedCharges_Coexist_WithIndependentRounding()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var mapped = await harness.SeedChargeAsync("GST", "GST-MAP", "Mapped GST", 10m, applicableOnAll: false);
        await harness.SeedProductChargeAsync(mapped, harness.Product);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        // 320 base each: 16 + 32 = 48; payable 368; ordered by code.
        Assert.Equal(368m, created.Subscription.PayableAmount);
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(2, charges.Count);
        Assert.Equal("CGST", charges[0].ChargeCode);
        Assert.Equal(16m, charges[0].Amount);
        Assert.Equal("GST-MAP", charges[1].ChargeCode);
        Assert.Equal(32m, charges[1].Amount);
        Assert.Equal(48m, total);
        Assert.Equal(368m, payable);
    }

    [Fact]
    public async Task GlobalCharge_IncludesQuantityAndMultipleEntitlementsInBase()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);

        // 2 × 80 × 5 entitlements = 800 base; 5% → 40; payable 840.
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(quantity: 2m, entitlement: 5),
            "subscription-charge-1",
            CancellationToken.None);

        Assert.Equal(840m, created.Subscription.PayableAmount);
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(800m, Assert.Single(charges).BaseAmount);
        Assert.Equal(40m, total);
        Assert.Equal(840m, payable);
    }

    [Fact]
    public async Task MappedCharge_IncludesQuantityAndMultipleEntitlementsInBase()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "GST-MAP", "Mapped GST", 10m, applicableOnAll: false);
        await harness.SeedProductChargeAsync(charge, harness.Product);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(quantity: 2m, entitlement: 5),
            "subscription-charge-1",
            CancellationToken.None);

        Assert.Equal(880m, created.Subscription.PayableAmount);
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(800m, Assert.Single(charges).BaseAmount);
        Assert.Equal(80m, total);
        Assert.Equal(880m, payable);
    }

    [Fact]
    public async Task FractionalQuantity_PreservesAwayFromZeroRounding()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var product = await harness.SecondProductAsync("GHEE-001", "Ghee", 100.25m);

        // 2.5 × 100.25 × 1 = 250.625 → product value 250.63;
        // 5% of 250.63 = 12.5315 → 12.53; payable 263.16.
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(product: product, quantity: 2.5m, days: [DayOfWeek.Monday], entitlement: 1),
            "subscription-charge-1",
            CancellationToken.None);

        Assert.Equal(250.63m, created.Subscription.PayableAmount - 12.53m);
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(250.63m, Assert.Single(charges).BaseAmount);
        Assert.Equal(12.53m, total);
        Assert.Equal(263.16m, payable);
        Assert.Equal(263.16m, created.Subscription.PayableAmount);
    }

    [Fact]
    public async Task MultipleCharges_AreIndependentlyRounded()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.SeedChargeAsync("GST", "SGST", "State GST", 7.5m, applicableOnAll: true);
        var product = await harness.SecondProductAsync("GHEE-001", "Ghee", 100.25m);

        // Base 250.63: 5% → 12.53; 7.5% → 18.80; total 31.33; payable 281.96.
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(product: product, quantity: 2.5m, days: [DayOfWeek.Monday], entitlement: 1),
            "subscription-charge-1",
            CancellationToken.None);

        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(2, charges.Count);
        Assert.Equal(12.53m, charges[0].Amount);
        Assert.Equal(18.80m, charges[1].Amount);
        Assert.Equal(31.33m, total);
        Assert.Equal(281.96m, payable);
        Assert.Equal(281.96m, created.Subscription.PayableAmount);
    }

    [Fact]
    public async Task PayableAmount_EqualsProductValuePlusChargeAmounts()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(quantity: 2m, entitlement: 5),
            "subscription-charge-1",
            CancellationToken.None);

        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(800m + 40m, subscription.PayableAmount);
        Assert.Equal(40m, subscription.ChargesTotal);
        Assert.Equal(subscription.PayableAmount, created.Subscription.PayableAmount);
    }

    [Fact]
    public async Task SubscriptionCharge_PersistsAllSnapshotFields()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        var snapshot = Assert.Single(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
        Assert.Equal(subscription.Id, snapshot.SubscriptionId);
        Assert.Equal("GST", snapshot.ChargeType);
        Assert.Equal("CGST", snapshot.ChargeCode);
        Assert.Equal("Central GST", snapshot.Description);
        Assert.Equal(5m, snapshot.Percentage);
        Assert.Equal(320m, snapshot.BaseAmount);
        Assert.Equal(16m, snapshot.Amount);
        Assert.Equal(created.Subscription.PublicId, subscription.PublicId);
    }

    [Fact]
    public async Task MasterPercentageChange_DoesNotAlterSnapshot()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        await harness.UpdateChargeAsync(charge, "GST", "Updated description", 7.5m);

        var (charges, total, payable) = await SnapshotStateAsync(harness);
        var snapshot = Assert.Single(charges);
        Assert.Equal(5m, snapshot.Percentage);
        Assert.Equal("Central GST", snapshot.Description);
        Assert.Equal(16m, snapshot.Amount);
        Assert.Equal(16m, total);
        Assert.Equal(336m, payable);
    }

    [Fact]
    public async Task MasterDescriptionChange_DoesNotAlterSnapshot()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        await harness.UpdateChargeAsync(charge, "GST", "Renamed GST", 5m);

        var snapshot = Assert.Single((await SnapshotStateAsync(harness)).Charges);
        Assert.Equal("Central GST", snapshot.Description);
        Assert.Equal(5m, snapshot.Percentage);
    }

    [Fact]
    public async Task MasterDeactivation_DoesNotAlterSnapshot()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        await harness.DeactivateChargeAsync(charge);

        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(16m, Assert.Single(charges).Amount);
        Assert.Equal(16m, total);
        Assert.Equal(336m, payable);
    }

    [Fact]
    public async Task MappingRemoval_DoesNotAlterSnapshot()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "GST-MAP", "Mapped GST", 10m, applicableOnAll: false);
        await harness.SeedProductChargeAsync(charge, harness.Product);
        await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        await harness.RemoveMappingAsync(charge, harness.Product);

        var (charges, total, payable) = await SnapshotStateAsync(harness);
        var snapshot = Assert.Single(charges);
        Assert.Equal("GST-MAP", snapshot.ChargeCode);
        Assert.Equal(32m, snapshot.Amount);
        Assert.Equal(32m, total);
        Assert.Equal(352m, payable);
        Assert.Equal(0, await harness.Db.ProductCharges.CountAsync());
    }

    [Fact]
    public async Task ProductPriceChange_DoesNotAlterSubscription()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        await harness.UpdateProductPriceAsync(harness.Product, 90m);

        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(320m, Assert.Single(charges).BaseAmount);
        Assert.Equal(16m, total);
        Assert.Equal(336m, payable);
        Assert.Equal(80m, (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).UnitPrice);
    }

    [Fact]
    public async Task IdempotentReplay_PreservesRowsAndPayable()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var request = harness.Request();

        var first = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-charge-1", CancellationToken.None);
        var replay = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-charge-1", CancellationToken.None);

        Assert.Equal(first.Subscription.PublicId, replay.Subscription.PublicId);
        Assert.Equal(first.Subscription.PayableAmount, replay.Subscription.PayableAmount);
        Assert.Single(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
        Assert.Single(await harness.Db.Subscriptions.AsNoTracking().ToListAsync());
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(16m, Assert.Single(charges).Amount);
        Assert.Equal(16m, total);
        Assert.Equal(336m, payable);
    }

    [Fact]
    public async Task PaymentRetry_UsesFrozenPayableWithoutRecalculation()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var request = harness.Request();

        var first = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-charge-1", CancellationToken.None);
        var replay = await harness.Service.CreateAsync(
            harness.Customer.Id, request, "subscription-charge-1", CancellationToken.None);

        // The capturing payment fake reads the persisted payable per call.
        Assert.Equal(2, harness.PaymentService.Calls.Count);
        Assert.Equal(336m, first.Payment.Amount);
        Assert.Equal(336m, replay.Payment.Amount);
        Assert.Single(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PauseResume_PreservesCharges()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync(
            utcNow: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), cutoffHours: 12);
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(startDate: new DateOnly(2026, 8, 17), entitlement: 2),
            "subscription-charge-1",
            CancellationToken.None);
        await harness.ActivateAsync();

        await harness.Service.PauseAsync(harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);
        await harness.Service.ResumeAsync(harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        // Entitlement 2: base 1 × 80 × 2 = 160; 5% → 8; payable 168.
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(8m, Assert.Single(charges).Amount);
        Assert.Equal(8m, total);
        Assert.Equal(168m, payable);
    }

    [Fact]
    public async Task SkipAndVacation_PreserveCharges()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync(
            utcNow: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), cutoffHours: 12);
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(startDate: new DateOnly(2026, 8, 17), entitlement: 2),
            "subscription-charge-1",
            CancellationToken.None);
        await harness.ActivateAsync();

        var firstDelivery = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .FirstAsync();
        await harness.Service.SkipAsync(
            harness.Customer.Id,
            created.Subscription.PublicId,
            new SkipSubscriptionDeliveryRequest(firstDelivery.PublicId),
            CancellationToken.None);
        await harness.Service.CreateVacationAsync(
            harness.Customer.Id,
            new CreateVacationRequest(new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 19)),
            CancellationToken.None);

        // Entitlement 2: base 160; 5% → 8; payable 168.
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(8m, Assert.Single(charges).Amount);
        Assert.Equal(8m, total);
        Assert.Equal(168m, payable);
    }

    [Fact]
    public async Task Cancel_PreservesCharges()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync(
            utcNow: new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), cutoffHours: 12);
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(startDate: new DateOnly(2026, 8, 17), entitlement: 2),
            "subscription-charge-1",
            CancellationToken.None);
        await harness.ActivateAsync();

        await harness.Service.CancelAsync(harness.Customer.Id, created.Subscription.PublicId, CancellationToken.None);

        // Entitlement 2: base 160; 5% → 8; payable 168.
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(8m, Assert.Single(charges).Amount);
        Assert.Equal(8m, total);
        Assert.Equal(168m, payable);
        Assert.Equal(SubscriptionStatus.Cancelled, (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Completion_PreservesCharges()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(startDate: new DateOnly(2026, 8, 17), entitlement: 2),
            "subscription-charge-1",
            CancellationToken.None);
        await harness.ActivateAsync();

        var deliveryIds = await harness.Db.SubscriptionDeliveries
            .AsNoTracking()
            .OrderBy(x => x.ScheduledDate)
            .Select(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, deliveryIds.Count);
        foreach (var deliveryId in deliveryIds)
        {
            await harness.Service.MarkDeliveryDeliveredAsync(deliveryId, CancellationToken.None);
        }

        // Entitlement 2: base 160; 5% → 8; payable 168.
        var (charges, total, payable) = await SnapshotStateAsync(harness);
        Assert.Equal(8m, Assert.Single(charges).Amount);
        Assert.Equal(8m, total);
        Assert.Equal(168m, payable);
        Assert.Equal(SubscriptionStatus.Completed, (await harness.Db.Subscriptions.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task InvalidGlobalWithMapping_ResolvesOnce_AndMasterUntouched()
    {
        await using var harness = await SubscriptionChargeHarness.CreateAsync();
        var charge = await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.SeedProductChargeAsync(charge, harness.Product);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id, harness.Request(), "subscription-charge-1", CancellationToken.None);

        Assert.Equal(336m, created.Subscription.PayableAmount);
        var snapshot = Assert.Single(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
        Assert.Equal(320m, snapshot.BaseAmount);
        Assert.Equal(16m, snapshot.Amount);

        var master = await harness.Db.Charges.AsNoTracking().SingleAsync();
        Assert.True(master.ApplicableOnAll);
        Assert.Equal(1, await harness.Db.ProductCharges.CountAsync());
    }

    private static async Task<(List<SubscriptionCharge> Charges, decimal Total, decimal Payable)> SnapshotStateAsync(
        SubscriptionChargeHarness harness)
    {
        var charges = await harness.Db.SubscriptionCharges
            .AsNoTracking()
            .OrderBy(x => x.ChargeType)
            .ThenBy(x => x.ChargeCode)
            .ToListAsync();
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        return (charges, subscription.ChargesTotal, subscription.PayableAmount);
    }

    private sealed class SubscriptionChargeHarness : IAsyncDisposable
    {
        private SubscriptionChargeHarness(
            DoodhDirectDbContext db,
            User customer,
            Product product,
            Branch branch,
            CustomerAddress address,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            CapturingSubscriptionPaymentService paymentService,
            SubscriptionService service)
        {
            Db = db;
            Customer = customer;
            Product = product;
            Branch = branch;
            Address = address;
            Clock = clock;
            TimeProvider = timeProvider;
            PaymentService = paymentService;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public Product Product { get; }
        public Branch Branch { get; }
        public CustomerAddress Address { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public CapturingSubscriptionPaymentService PaymentService { get; }
        public SubscriptionService Service { get; }

        public CreateSubscriptionRequest Request(
            Product? product = null,
            decimal quantity = 1m,
            DateOnly? startDate = null,
            IReadOnlyCollection<DayOfWeek>? days = null,
            int entitlement = 4,
            PaymentMethod method = PaymentMethod.Razorpay) =>
            new(
                (product ?? Product).PublicId,
                Address.PublicId,
                quantity,
                startDate ?? new DateOnly(2026, 8, 17),
                days ?? [DayOfWeek.Monday, DayOfWeek.Wednesday],
                entitlement,
                method);

        public async Task ActivateAsync()
        {
            var subscription = await Db.Subscriptions.SingleAsync();
            subscription.Activate(TimeProvider.Now);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task<Charge> SeedChargeAsync(
            string type, string code, string? description, decimal percentage, bool applicableOnAll = true)
        {
            var charge = new Charge(type, code, description, percentage, applicableOnAll);
            Db.Charges.Add(charge);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return charge;
        }

        public async Task SeedProductChargeAsync(Charge charge, Product product)
        {
            Db.ProductCharges.Add(new ProductCharge(product.Id, charge.Id));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task<Product> SecondProductAsync(string sku, string name, decimal price)
        {
            var categoryId = Product.CategoryId;
            var product = new Product(categoryId, sku, name, null, "litre", price);
            product.Activate();
            Db.Products.Add(product);
            await Db.SaveChangesAsync();
            Db.ProductBranches.Add(new ProductBranch(product.Id, Branch.Id, true, 100m));
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return product;
        }

        public async Task DeactivateChargeAsync(Charge charge)
        {
            var tracked = await Db.Charges.SingleAsync(x => x.Id == charge.Id);
            tracked.Deactivate();
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task UpdateChargeAsync(Charge charge, string type, string? description, decimal percentage)
        {
            var tracked = await Db.Charges.SingleAsync(x => x.Id == charge.Id);
            tracked.Update(type, description, percentage);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task RemoveMappingAsync(Charge charge, Product product)
        {
            var link = await Db.ProductCharges.SingleAsync(x => x.ChargeId == charge.Id && x.ProductId == product.Id);
            Db.ProductCharges.Remove(link);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task UpdateProductPriceAsync(Product product, decimal price)
        {
            var tracked = await Db.Products.SingleAsync(x => x.Id == product.Id);
            tracked.Update(tracked.CategoryId, tracked.Sku, tracked.Name, null, tracked.UnitOfMeasure, price);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public static async Task<SubscriptionChargeHarness> CreateAsync(
            DateTime? utcNow = null,
            int? cutoffHours = null)
        {
            // InMemory (Stage 2 precedent): SQLite enforces the Charge master
            // CHECK constraints as TEXT comparisons, which rejects valid
            // percentages; transactions are intentionally ignored here — the
            // serializable-tx shape is verified read-only against SQL Server.
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"subscription-charge-tests-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new DoodhDirectDbContext(options);

            var customer = new User(UserType.Customer);
            customer.SetProfile("Customer");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var address = new CustomerAddress(
                customer.Id,
                "Home",
                "1 Main Road",
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                "Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            db.AddRange(product, branch, address);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            if (cutoffHours.HasValue)
            {
                db.SystemConfigurations.Add(new SystemConfiguration(
                    "Subscription.SkipPauseCutoffHours",
                    cutoffHours.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "integer"));
            }
            await db.SaveChangesAsync();

            var clock = new TestClock(utcNow ?? new DateTime(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var paymentService = new CapturingSubscriptionPaymentService(db, clock);
            var allocation = new FixedBranchAllocationService(branch);
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var service = new SubscriptionService(
                db,
                allocation,
                paymentService,
                timeProvider,
                notificationEventWriter);
            return new SubscriptionChargeHarness(
                db, customer, product, branch, address, clock, timeProvider, paymentService, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    private sealed class FixedBranchAllocationService(Branch branch) : IBranchAllocationService
    {
        public Task<BranchAllocationResult> AllocateAsync(
            decimal latitude,
            decimal longitude,
            IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BranchAllocationResult(
                branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
    }

    private sealed class CapturingSubscriptionPaymentService(
        DoodhDirectDbContext db,
        TestClock clock) : IPaymentService
    {
        public List<(long CustomerId, long SubscriptionId, PaymentMethod Method, string IdempotencyKey)> Calls { get; } = [];

        public async Task<PaymentResult> CreateForSubscriptionAsync(
            long customerId,
            long subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls.Add((customerId, subscriptionId, method, idempotencyKey));
            var subscription = await db.Subscriptions
                .AsNoTracking()
                .SingleAsync(x => x.Id == subscriptionId, cancellationToken);
            return new PaymentResult(
                Guid.NewGuid(),
                null,
                null,
                method,
                method switch
                {
                    PaymentMethod.Wallet => "Wallet",
                    PaymentMethod.Development => "Mock",
                    _ => "Razorpay"
                },
                PaymentStatus.Pending,
                subscription.PayableAmount,
                0m,
                "INR",
                $"order_{subscription.PublicId:N}",
                null,
                "rzp_test",
                null,
                null,
                clock.UtcNow.AddMinutes(15),
                null,
                clock.UtcNow,
                subscription.PublicId);
        }

        public Task<PaymentResult> CreateWalletTopUpAsync(
            long customerId,
            decimal amount,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> RetrySubscriptionAsync(
            long customerId,
            Guid subscriptionId,
            PaymentMethod method,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CompleteDevelopmentAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CancelAsync(
            long customerId,
            Guid paymentId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PaymentCapability>> GetCapabilitiesAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> CreateAsync(
            long customerId,
            CreatePaymentRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> VerifyAsync(
            long customerId,
            VerifyPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentResult> GetAsync(
            long userId,
            Guid paymentId,
            bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PaymentReconciliationResult> ReconcileAsync(
            long requestedByUserId,
            Guid paymentId,
            bool bypassOwnership,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RefundResult> RefundAsync(
            long requestedByUserId,
            Guid paymentId,
            RefundPaymentRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ProcessWebhookAsync(
            byte[] payload,
            string signature,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>
/// Stage 3 payment boundary: real <see cref="PaymentService"/> (mock gateway)
/// and <see cref="WalletService"/> prove the charge-inclusive frozen payable is
/// consumed without any payment-code change.
/// </summary>
public sealed class SubscriptionChargePaymentTests
{
    [Fact]
    public async Task WalletPayment_DebitsChargeInclusiveFrozenPayable()
    {
        await using var harness = await ChargedSubscriptionPaymentHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);
        await harness.SeedWalletAsync(1_000m, "wallet-funding-1");

        // Wallet payment completes synchronously inside creation.
        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(method: PaymentMethod.Wallet),
            "subscription-charge-wallet-1",
            CancellationToken.None);

        Assert.Equal(SubscriptionStatus.Active, created.Subscription.Status);
        Assert.Equal(336m, created.Subscription.PayableAmount);
        Assert.Equal(336m, created.Payment.Amount);

        harness.Db.ChangeTracker.Clear();
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        var wallet = await harness.Db.Wallets.AsNoTracking().SingleAsync();
        var debit = await harness.Db.WalletTransactions
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Type == WalletTransactionType.SubscriptionDebit);
        Assert.Equal(336m, payment.Amount);
        Assert.Equal(664m, wallet.Balance);
        Assert.Equal(-336m, debit.Amount);
        Assert.Single(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task GatewayPayment_AmountEqualsChargeInclusivePayable()
    {
        await using var harness = await ChargedSubscriptionPaymentHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(method: PaymentMethod.Development),
            "subscription-charge-gateway-1",
            CancellationToken.None);
        var verified = await harness.PaymentService.CompleteDevelopmentAsync(
            harness.Customer.Id, created.Payment.PublicId, CancellationToken.None);

        Assert.Equal(PaymentStatus.Success, verified.Status);
        harness.Db.ChangeTracker.Clear();
        var payment = await harness.Db.Payments.AsNoTracking().SingleAsync();
        var subscription = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(336m, payment.Amount);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(336m, subscription.PayableAmount);
        Assert.Equal(16m, Assert.Single(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync()).Amount);
    }

    [Fact]
    public async Task FailedPayment_PreservesChargeRowsAndPayable()
    {
        await using var harness = await ChargedSubscriptionPaymentHarness.CreateAsync();
        await harness.SeedChargeAsync("GST", "CGST", "Central GST", 5m, applicableOnAll: true);

        var created = await harness.Service.CreateAsync(
            harness.Customer.Id,
            harness.Request(method: PaymentMethod.Development),
            "subscription-charge-failed-1",
            CancellationToken.None);

        var payment = await harness.Db.Payments.SingleAsync(x => x.PublicId == created.Payment.PublicId);
        var subscription = await harness.Db.Subscriptions.SingleAsync();
        payment.Fail("PAYMENT_DECLINED", "The gateway declined the payment.", "failed", harness.TimeProvider.Now);
        subscription.FailPayment();
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var snapshot = Assert.Single(await harness.Db.SubscriptionCharges.AsNoTracking().ToListAsync());
        Assert.Equal(16m, snapshot.Amount);
        var persisted = await harness.Db.Subscriptions.AsNoTracking().SingleAsync();
        Assert.Equal(336m, persisted.PayableAmount);
        Assert.Equal(16m, persisted.ChargesTotal);
        Assert.Equal(SubscriptionStatus.PaymentFailed, persisted.Status);
    }

    private sealed class ChargedSubscriptionPaymentHarness : IAsyncDisposable
    {
        private ChargedSubscriptionPaymentHarness(
            DoodhDirectDbContext db,
            User customer,
            Product product,
            Branch branch,
            CustomerAddress address,
            TestClock clock,
            TestIndiaTimeProvider timeProvider,
            PaymentService paymentService,
            SubscriptionService service)
        {
            Db = db;
            Customer = customer;
            Product = product;
            Branch = branch;
            Address = address;
            Clock = clock;
            TimeProvider = timeProvider;
            PaymentService = paymentService;
            Service = service;
        }

        public DoodhDirectDbContext Db { get; }
        public User Customer { get; }
        public Product Product { get; }
        public Branch Branch { get; }
        public CustomerAddress Address { get; }
        public TestClock Clock { get; }
        public TestIndiaTimeProvider TimeProvider { get; }
        public PaymentService PaymentService { get; }
        public SubscriptionService Service { get; }

        public CreateSubscriptionRequest Request(PaymentMethod method = PaymentMethod.Development) =>
            new(
                Product.PublicId,
                Address.PublicId,
                1m,
                new DateOnly(2026, 8, 17),
                [DayOfWeek.Monday, DayOfWeek.Wednesday],
                4,
                method);

        public async Task<Charge> SeedChargeAsync(
            string type, string code, string? description, decimal percentage, bool applicableOnAll = true)
        {
            var charge = new Charge(type, code, description, percentage, applicableOnAll);
            Db.Charges.Add(charge);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return charge;
        }

        public async Task SeedWalletAsync(decimal amount, string idempotencyKey)
        {
            var wallet = await Db.Wallets.SingleOrDefaultAsync(x => x.CustomerId == Customer.Id);
            if (wallet is null)
            {
                wallet = new Wallet(Customer.Id, "INR");
                Db.Wallets.Add(wallet);
            }

            wallet.Credit(WalletTransactionType.TopUp, amount, idempotencyKey, "Wallet top-up", TimeProvider.Now);
            await Db.SaveChangesAsync();
        }

        public static async Task<ChargedSubscriptionPaymentHarness> CreateAsync()
        {
            // InMemory (Stage 2 precedent): see SubscriptionChargeHarness.
            var options = new DbContextOptionsBuilder<DoodhDirectDbContext>()
                .UseInMemoryDatabase($"subscription-charge-payment-tests-{Guid.NewGuid():N}")
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            var db = new DoodhDirectDbContext(options);

            var customer = new User(UserType.Customer);
            customer.SetProfile("Subscription Customer");
            db.Users.Add(customer);
            await db.SaveChangesAsync();

            var category = new ProductCategory("MILK", "Milk");
            category.Activate();
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            var product = new Product(category.Id, "MILK-001", "Fresh Milk", null, "litre", 80m);
            product.Activate();
            var branch = new Branch("MAIN", "Main Branch", "Bengaluru", "Karnataka", 12.9716m, 77.5946m);
            var address = new CustomerAddress(
                customer.Id,
                "Home",
                "1 Main Road",
                "Central",
                "Bengaluru",
                "Karnataka",
                "560001",
                "Subscription Customer",
                "9999999999",
                12.9716m,
                77.5946m);
            db.AddRange(product, branch, address);
            await db.SaveChangesAsync();
            db.ProductBranches.Add(new ProductBranch(product.Id, branch.Id, true, 100m));
            await db.SaveChangesAsync();

            var clock = new TestClock(new DateTime(2026, 8, 16, 2, 0, 0, DateTimeKind.Utc));
            var timeProvider = new TestIndiaTimeProvider(clock);
            var paymentOptions = Options.Create(new PaymentOptions
            {
                Provider = "Mock",
                Currency = "INR",
                PaymentExpiryMinutes = 15,
                MockSigningSecret = "test-signing-secret"
            });
            var notificationEventWriter = new TestNotificationEventWriter(db, clock);
            var walletService = new WalletService(db, timeProvider, paymentOptions, notificationEventWriter);
            var paymentService = new PaymentService(
                db, new MockPaymentGateway(paymentOptions), walletService, timeProvider, paymentOptions, notificationEventWriter);
            var service = new SubscriptionService(
                db, new FixedBranchAllocation(branch), paymentService, timeProvider, notificationEventWriter);
            return new ChargedSubscriptionPaymentHarness(
                db, customer, product, branch, address, clock, timeProvider, paymentService, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }

        private sealed class FixedBranchAllocation(Branch branch) : IBranchAllocationService
        {
            public Task<BranchAllocationResult> AllocateAsync(
                decimal latitude,
                decimal longitude,
                IReadOnlyCollection<(long ProductId, decimal Quantity)> items,
                CancellationToken cancellationToken) =>
                Task.FromResult(new BranchAllocationResult(
                    branch.Id, branch.PublicId, branch.Code, branch.Name, 0m));
        }
    }
}
