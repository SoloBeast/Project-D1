using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Deliveries;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;
using DoodhDirect.Infrastructure.Deliveries;
using Microsoft.EntityFrameworkCore;
using static DoodhDirect.Api.IntegrationTests.DeliveryServiceTests;

namespace DoodhDirect.Api.IntegrationTests;

/// <summary>
/// Batch-allocation timing gate: allocation is enforced at assignment for
/// milk items only, never at OTP completion. Non-milk deliveries assign and
/// complete without any allocation; mixed deliveries require cover for the
/// milk portion only. Milk identity comes from the product category code.
/// </summary>
public sealed class DeliveryBatchAllocationMilkGateTests
{
    [Fact]
    public async Task MilkAssignment_WithoutAllocation_IsRejected()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.AssignAsync(deliveryId, harness.Staff));
        Assert.Equal("Batch allocations must be saved before assigning this milk delivery.", exception.Message);
    }

    [Fact]
    public async Task MilkAssignment_WithAllocation_AssignAndOtpSucceed()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await harness.MaterializeOrderAsync();
        var batchId = await harness.RecordMilkBatchAsync(2m);
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);

        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        Assert.True(verified.RequiresBatchAllocation);
        Assert.Equal(2m, Assert.Single(await harness.Db.MilkUsages.AsNoTracking().ToListAsync()).QuantityUsed);
    }

    [Fact]
    public async Task NonMilkAssignment_WithoutAllocation_AssignAndOtpSucceed()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await CreateNonMilkOrderDeliveryAsync(harness, 1m);

        // No allocation saved: assignment must succeed for non-milk.
        var assigned = await harness.AssignAsync(deliveryId, harness.Staff);
        Assert.Equal(DeliveryStatus.Assigned, assigned.Status);
        Assert.False(assigned.RequiresBatchAllocation);

        var actor = harness.StaffActor(harness.Staff);
        await harness.Service.PickUpAsync(actor, deliveryId, new DeliveryNotesRequest(null), CancellationToken.None);
        await harness.Service.StartAsync(actor, deliveryId, CancellationToken.None);
        await harness.Service.ArriveAsync(actor, deliveryId, CancellationToken.None);

        var code = await harness.GetOtpCodeAsync(deliveryId);
        var verified = await harness.Service.VerifyOtpAsync(
            actor, deliveryId, new VerifyDeliveryOtpRequest(code), CancellationToken.None);

        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        Assert.False(verified.RequiresBatchAllocation);
        Assert.Equal(0, await harness.Db.MilkUsages.AsNoTracking().CountAsync());
        Assert.Equal(0, await harness.Db.DeliveryBatchAllocations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task NonMilk_SaveBatchAllocations_IsRejected()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var deliveryId = await CreateNonMilkOrderDeliveryAsync(harness, 1m);
        var batchId = await harness.RecordMilkBatchAsync(2m);

        var exception = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                deliveryId,
                new SaveDeliveryBatchAllocationsRequest(
                    [new DeliveryBatchAllocationRequest(batchId, 1m)]),
                CancellationToken.None));
        Assert.Equal("Batch allocations are only required for milk items.", exception.Message);
    }

    [Fact]
    public async Task MixedOrder_RequiresMilkQuantityOnly()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var milk = await harness.Db.Products.AsNoTracking().SingleAsync(x => x.Sku == "MILK-001");
        var paneer = await CreateNonMilkProductAsync(harness, "PANEER-001", "Paneer", 200m);
        var deliveryId = await CreateOrderDeliveryAsync(
            harness, [(milk.Id, 2m, milk.Sku, milk.Name, milk.UnitOfMeasure), (paneer.Id, 3m, paneer.Sku, paneer.Name, paneer.UnitOfMeasure)],
            "order-mixed-1", "ORD-MIX-001");

        var info = await harness.Service.GetBatchAllocationsAsync(
            harness.ManagerActor, deliveryId, CancellationToken.None);
        Assert.Equal(2m, info.TotalRequiredQuantity);

        // Assignment without any allocation is rejected (milk portion uncovered).
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            harness.AssignAsync(deliveryId, harness.Staff));

        // Covering only the milk portion (2L) satisfies the gate.
        var batchId = await harness.RecordMilkBatchAsync(2m);
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        var assigned = await harness.AssignAsync(deliveryId, harness.Staff);
        Assert.True(assigned.RequiresBatchAllocation);

        // Saving the combined total (2L milk + 3 paneer) is rejected: only the
        // milk portion requires cover.
        var secondDeliveryId = await CreateOrderDeliveryAsync(
            harness, [(milk.Id, 2m, milk.Sku, milk.Name, milk.UnitOfMeasure), (paneer.Id, 1m, paneer.Sku, paneer.Name, paneer.UnitOfMeasure)],
            "order-mixed-2", "ORD-MIX-002");
        var secondBatchId = await harness.RecordMilkBatchAsync(5m);
        var mismatch = await Assert.ThrowsAsync<ValidationAppException>(() =>
            harness.Service.SaveBatchAllocationsAsync(
                harness.ManagerActor,
                secondDeliveryId,
                new SaveDeliveryBatchAllocationsRequest(
                    [new DeliveryBatchAllocationRequest(secondBatchId, 3m)]),
                CancellationToken.None));
        Assert.Contains("must equal the delivery requirement (2 L)", mismatch.Message);
    }

    [Fact]
    public async Task MixedOrder_WithMilkCover_OtpCompletesWithoutNonMilkUsage()
    {
        await using var harness = await DeliveryHarness.CreateAsync();
        var milk = await harness.Db.Products.AsNoTracking().SingleAsync(x => x.Sku == "MILK-001");
        var paneer = await CreateNonMilkProductAsync(harness, "PANEER-002", "Paneer", 200m);
        var deliveryId = await CreateOrderDeliveryAsync(
            harness, [(milk.Id, 2m, milk.Sku, milk.Name, milk.UnitOfMeasure), (paneer.Id, 1m, paneer.Sku, paneer.Name, paneer.UnitOfMeasure)],
            "order-mixed-3", "ORD-MIX-003");

        var batchId = await harness.RecordMilkBatchAsync(2m);
        await harness.SaveAllocationAsync(deliveryId, batchId, 2m);
        await harness.AdvanceToArrivedAsync(deliveryId, harness.Staff);
        var code = await harness.GetOtpCodeAsync(deliveryId);
        var verified = await harness.Service.VerifyOtpAsync(
            harness.StaffActor(harness.Staff),
            deliveryId,
            new VerifyDeliveryOtpRequest(code),
            CancellationToken.None);

        Assert.Equal(DeliveryStatus.Delivered, verified.Status);
        var usages = await harness.Db.MilkUsages.AsNoTracking().ToListAsync();
        Assert.Equal(2m, Assert.Single(usages).QuantityUsed);
    }

    private static async Task<Guid> CreateNonMilkOrderDeliveryAsync(DeliveryHarness harness, decimal quantity)
    {
        var paneer = await CreateNonMilkProductAsync(harness, "PANEER-001", "Paneer", 200m);
        return await CreateOrderDeliveryAsync(
            harness, [(paneer.Id, quantity, paneer.Sku, paneer.Name, paneer.UnitOfMeasure)],
            $"order-paneer-{Guid.NewGuid():N}",
            $"ORD-PAN-{Guid.NewGuid():N}".Substring(0, 20));
    }

    private static async Task<Product> CreateNonMilkProductAsync(
        DeliveryHarness harness, string sku, string name, decimal price)
    {
        var category = new ProductCategory("PANEER", "Paneer");
        harness.Db.ProductCategories.Add(category);
        await harness.Db.SaveChangesAsync();
        var product = new Product(category.Id, sku, name, null, "piece", price);
        harness.Db.Products.Add(product);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        return await harness.Db.Products.AsNoTracking().SingleAsync(x => x.Sku == sku);
    }

    private static async Task<Guid> CreateOrderDeliveryAsync(
        DeliveryHarness harness,
        IReadOnlyCollection<(long ProductId, decimal Quantity, string Sku, string Name, string Unit)> lines,
        string idempotencyKey,
        string orderNumber)
    {
        var address = await harness.Db.CustomerAddresses.AsNoTracking()
            .SingleAsync(x => x.UserId == harness.Customer.Id);
        var order = new Order(
            harness.Customer.Id,
            address.Id,
            harness.Branch.Id,
            idempotencyKey,
            orderNumber,
            100m,
            0m,
            harness.Branch.Code,
            harness.Branch.Name,
            address.Label,
            address.AddressLine1,
            address.AddressLine2,
            address.Locality,
            address.City,
            address.State,
            address.PinCode,
            address.Landmark,
            address.DeliveryInstructions,
            address.ContactName,
            address.ContactMobile,
            address.Latitude,
            address.Longitude);
        order.ConfirmPayment();
        foreach (var line in lines)
        {
            order.AddItem(new OrderItem(line.ProductId, line.Quantity, 50m, line.Sku, line.Name, line.Unit));
        }
        harness.Db.Orders.Add(order);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        await harness.Service.MaterializeEligibleAsync(
            harness.ManagerActor, harness.Today, CancellationToken.None);
        return await harness.Db.Deliveries.AsNoTracking()
            .Where(x => x.OrderId == order.Id)
            .Select(x => x.PublicId)
            .SingleAsync();
    }

    // Fail-closed product classification: a missing product or category
    // navigation must throw instead of silently classifying the item as
    // non-milk. Detached graphs are used because EF relationship fixup
    // would re-populate navigations on tracked entities.
    [Fact]
    public void MissingOrderItemProduct_ClassificationFailsClosed()
    {
        var delivery = OrderDeliveryWithItems(
            new OrderItem(1, 1m, 50m, "MILK-001", "Fresh Milk", "litre"));

        var requires = Assert.Throws<BusinessRuleException>(() => ClassificationRequires(delivery));
        Assert.Contains("could not be determined", requires.Message);
        var quantity = Assert.Throws<BusinessRuleException>(() => ClassificationQuantity(delivery));
        Assert.Contains("could not be determined", quantity.Message);
    }

    [Fact]
    public void MissingProductCategory_ClassificationFailsClosed()
    {
        var product = new Product(7, "MILK-001", "Fresh Milk", null, "litre", 80m);
        var delivery = OrderDeliveryWithItems(ItemWithProduct(product));

        var requires = Assert.Throws<BusinessRuleException>(() => ClassificationRequires(delivery));
        Assert.Contains("category is missing", requires.Message);
        Assert.Throws<BusinessRuleException>(() => ClassificationQuantity(delivery));
    }

    [Fact]
    public void DeliveryWithoutOrderOrSubscription_ClassificationFailsClosed()
    {
        var delivery = Delivery.ForOrder(
            1, 1, 1, new DateOnly(2026, 8, 16), "ORD-1", "Name", "9999999999", "Addr", null, 12.9716m, 77.5946m);

        var requires = Assert.Throws<BusinessRuleException>(() => ClassificationRequires(delivery));
        Assert.Contains("no order or subscription occurrence", requires.Message);
        Assert.Throws<BusinessRuleException>(() => ClassificationQuantity(delivery));
    }

    [Fact]
    public void MissingSubscriptionProduct_ClassificationFailsClosed()
    {
        var delivery = Delivery.ForSubscriptionOccurrence(
            1, 1, 1, new DateOnly(2026, 8, 16), "SUB-1", "Name", "9999999999", "Addr", null, 12.9716m, 77.5946m);
        SetBackingField(
            delivery,
            nameof(Delivery.SubscriptionDelivery),
            RuntimeHelpers.GetUninitializedObject(typeof(SubscriptionDelivery)));

        var requires = Assert.Throws<BusinessRuleException>(() => ClassificationRequires(delivery));
        Assert.Contains("subscription product is missing", requires.Message);
        Assert.Throws<BusinessRuleException>(() => ClassificationQuantity(delivery));
    }

    [Fact]
    public void ValidMilkAndNonMilkItems_StillClassified()
    {
        var milkCategory = new ProductCategory("MILK", "Milk");
        var milkProduct = new Product(7, "MILK-001", "Fresh Milk", null, "litre", 80m);
        SetBackingField(milkProduct, nameof(Product.Category), milkCategory);
        var milkDelivery = OrderDeliveryWithItems(ItemWithProduct(milkProduct, 2m));
        Assert.True(ClassificationRequires(milkDelivery));
        Assert.Equal(2m, ClassificationQuantity(milkDelivery));

        var paneerCategory = new ProductCategory("PANEER", "Paneer");
        var paneerProduct = new Product(8, "PANEER-001", "Paneer", null, "piece", 200m);
        SetBackingField(paneerProduct, nameof(Product.Category), paneerCategory);
        var paneerDelivery = OrderDeliveryWithItems(ItemWithProduct(paneerProduct));
        Assert.False(ClassificationRequires(paneerDelivery));
        Assert.Equal(0m, ClassificationQuantity(paneerDelivery));
    }

    private static Delivery OrderDeliveryWithItems(params OrderItem[] items)
    {
        var delivery = Delivery.ForOrder(
            1, 1, 1, new DateOnly(2026, 8, 16), "ORD-1", "Name", "9999999999", "Addr", null, 12.9716m, 77.5946m);
        var order = (Order)RuntimeHelpers.GetUninitializedObject(typeof(Order));
        SetBackingField(order, nameof(Order.Items), items.ToList());
        SetBackingField(delivery, nameof(Delivery.Order), order);
        return delivery;
    }

    private static OrderItem ItemWithProduct(Product product, decimal quantity = 1m)
    {
        var item = new OrderItem(product.CategoryId, quantity, 50m, product.Sku, product.Name, product.UnitOfMeasure);
        SetBackingField(item, nameof(OrderItem.Product), product);
        return item;
    }

    private static bool ClassificationRequires(Delivery delivery) =>
        InvokeClassifier<bool>("RequiresBatchAllocationFor", delivery);

    private static decimal ClassificationQuantity(Delivery delivery) =>
        InvokeClassifier<decimal>("RequiredQuantityFor", delivery);

    private static T InvokeClassifier<T>(string methodName, Delivery delivery)
    {
        var method = typeof(DeliveryService).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Classifier '{methodName}' was not found.");
        try
        {
            return (T)method.Invoke(null, [delivery])!;
        }
        catch (TargetInvocationException exception)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
    }

    private static void SetBackingField(object target, string propertyName, object? value)
    {
        var field = target.GetType().GetField(
                $"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Backing field for '{propertyName}' was not found.");
        field.SetValue(target, value);
    }
}
