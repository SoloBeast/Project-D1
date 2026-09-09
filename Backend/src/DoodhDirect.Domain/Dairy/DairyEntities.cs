using DoodhDirect.Domain.Common;
using DoodhDirect.Domain.Deliveries;
using DoodhDirect.Domain.Orders;
using DoodhDirect.Domain.Subscriptions;

namespace DoodhDirect.Domain.Dairy;

public enum MilkBatchStatus
{
    Available = 1,
    Exhausted = 2
}

public enum MilkUsageSource
{
    Manual = 1,
    Order = 2,
    Subscription = 3
}

public sealed class MilkProduction : AuditableEntity
{
    private MilkProduction() { }

    public MilkProduction(
        long branchId,
        DateTime productionAt,
        int buffaloCount,
        decimal quantityProduced,
        string unit,
        long recordedByUserId,
        string? shift,
        string? remarks)
    {
        EnsureIndiaLocal(productionAt, nameof(productionAt));
        BranchId = branchId;
        ProductionAt = productionAt;
        BuffaloCount = buffaloCount;
        QuantityProduced = quantityProduced;
        Unit = unit.Trim().ToUpperInvariant();
        RecordedByUserId = recordedByUserId;
        Shift = string.IsNullOrWhiteSpace(shift) ? null : shift.Trim();
        Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
    }

    public long BranchId { get; private set; }
    public DateTime ProductionAt { get; private set; }
    public int BuffaloCount { get; private set; }
    public decimal QuantityProduced { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public long RecordedByUserId { get; private set; }
    public string? Shift { get; private set; }
    public string? Remarks { get; private set; }

    public ICollection<MilkBatch> Batches { get; private set; } = [];

    private static void EnsureIndiaLocal(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("The timestamp must be India-local.", parameterName);
        }
    }
}

public sealed class MilkBatch : AuditableEntity
{
    private MilkBatch() { }

    public MilkBatch(
        long branchId,
        long productionId,
        string batchNumber,
        DateTime productionAt,
        decimal quantityProduced,
        string unit)
    {
        if (productionAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("The timestamp must be India-local.", nameof(productionAt));
        }

        BranchId = branchId;
        ProductionId = productionId;
        BatchNumber = batchNumber.Trim().ToUpperInvariant();
        ProductionAt = productionAt;
        QuantityProduced = quantityProduced;
        Unit = unit.Trim().ToUpperInvariant();
        Status = MilkBatchStatus.Available;
    }

    public long BranchId { get; private set; }
    public long ProductionId { get; private set; }
    public string BatchNumber { get; private set; } = string.Empty;
    public DateTime ProductionAt { get; private set; }
    public decimal QuantityProduced { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public MilkBatchStatus Status { get; private set; }

    public MilkProduction Production { get; private set; } = null!;
    public ICollection<MilkUsage> Usages { get; private set; } = [];
    public ICollection<DeliveryBatchAllocation> Allocations { get; private set; } = [];

    public void MarkExhausted() => Status = MilkBatchStatus.Exhausted;
}

public sealed class MilkUsage : AuditableEntity
{
    private MilkUsage() { }

    /// <summary>
    /// Manual usage recorded by a Dairy Manager against a specific production batch.
    /// Always has a batch and is never tied to a delivery.
    /// </summary>
    public MilkUsage(
        long branchId,
        long batchId,
        string unit,
        DateTime usedAt,
        decimal quantityUsed,
        string purpose,
        long recordedByUserId,
        string? remarks)
    {
        if (usedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("The timestamp must be India-local.", nameof(usedAt));
        }

        BranchId = branchId;
        BatchId = batchId;
        Source = MilkUsageSource.Manual;
        Unit = NormalizeUnit(unit);
        UsedAt = usedAt;
        QuantityUsed = quantityUsed;
        Purpose = purpose.Trim();
        RecordedByUserId = recordedByUserId;
        Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
    }

    /// <summary>
    /// Automatic consumption created when a one-time Order delivery reaches the
    /// Delivered state. Quantity/branch come from the persisted Order/OrderItem
    /// snapshots; batch identity comes from the manager-selected
    /// DeliveryBatchAllocation; idempotent per (DeliveryBatchAllocationId).
    /// </summary>
    public MilkUsage(
        long branchId,
        long deliveryId,
        long orderId,
        long? orderItemId,
        long batchId,
        long deliveryBatchAllocationId,
        string orderNumber,
        string deliveryNumber,
        string productName,
        string unit,
        DateTime usedAt,
        decimal quantityUsed,
        long recordedByUserId)
    {
        if (usedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("The timestamp must be India-local.", nameof(usedAt));
        }

        BranchId = branchId;
        DeliveryId = deliveryId;
        OrderId = orderId;
        OrderItemId = orderItemId;
        BatchId = batchId;
        DeliveryBatchAllocationId = deliveryBatchAllocationId;
        Source = MilkUsageSource.Order;
        OrderNumber = orderNumber.Trim();
        DeliveryNumber = deliveryNumber.Trim();
        ProductName = productName.Trim();
        Unit = NormalizeUnit(unit);
        UsedAt = usedAt;
        QuantityUsed = quantityUsed;
        Purpose = "Automatic Order Consumption";
        RecordedByUserId = recordedByUserId;
    }

    /// <summary>
    /// Automatic consumption created when a Subscription delivery occurrence reaches
    /// the Delivered state. Quantity/branch come from the persisted occurrence
    /// snapshot; batch identity comes from the manager-selected
    /// DeliveryBatchAllocation; idempotent per (DeliveryBatchAllocationId).
    /// </summary>
    public MilkUsage(
        long branchId,
        long deliveryId,
        long subscriptionDeliveryId,
        long batchId,
        long deliveryBatchAllocationId,
        string deliveryNumber,
        string productName,
        string unit,
        DateTime usedAt,
        decimal quantityUsed,
        long recordedByUserId)
    {
        if (usedAt.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("The timestamp must be India-local.", nameof(usedAt));
        }

        BranchId = branchId;
        DeliveryId = deliveryId;
        SubscriptionDeliveryId = subscriptionDeliveryId;
        BatchId = batchId;
        DeliveryBatchAllocationId = deliveryBatchAllocationId;
        Source = MilkUsageSource.Subscription;
        DeliveryNumber = deliveryNumber.Trim();
        ProductName = productName.Trim();
        Unit = NormalizeUnit(unit);
        UsedAt = usedAt;
        QuantityUsed = quantityUsed;
        Purpose = "Automatic Subscription Consumption";
        RecordedByUserId = recordedByUserId;
    }

    public long BranchId { get; private set; }
    public long? BatchId { get; private set; }
    public MilkUsageSource Source { get; private set; }
    public DateTime UsedAt { get; private set; }
    public decimal QuantityUsed { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public string Purpose { get; private set; } = string.Empty;
    public long RecordedByUserId { get; private set; }
    public string? Remarks { get; private set; }
    public long? DeliveryId { get; private set; }
    public long? OrderId { get; private set; }
    public long? SubscriptionDeliveryId { get; private set; }
    public long? OrderItemId { get; private set; }
    public long? DeliveryBatchAllocationId { get; private set; }
    public string? OrderNumber { get; private set; }
    public string? DeliveryNumber { get; private set; }
    public string? ProductName { get; private set; }

    public MilkBatch? Batch { get; private set; }
    public Delivery? Delivery { get; private set; }
    public Order? Order { get; private set; }
    public SubscriptionDelivery? SubscriptionDelivery { get; private set; }
    public OrderItem? OrderItem { get; private set; }
    public DeliveryBatchAllocation? DeliveryBatchAllocation { get; private set; }

    private static string NormalizeUnit(string unit) => unit.Trim().ToUpperInvariant();
}
