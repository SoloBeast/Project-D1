using DoodhDirect.Domain.Common;

namespace DoodhDirect.Domain.Dairy;

public enum MilkBatchStatus
{
    Available = 1,
    Exhausted = 2
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

    public void MarkExhausted() => Status = MilkBatchStatus.Exhausted;
}

public sealed class MilkUsage : AuditableEntity
{
    private MilkUsage() { }

    public MilkUsage(
        long branchId,
        long batchId,
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
        UsedAt = usedAt;
        QuantityUsed = quantityUsed;
        Purpose = purpose.Trim();
        RecordedByUserId = recordedByUserId;
        Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
    }

    public long BranchId { get; private set; }
    public long BatchId { get; private set; }
    public DateTime UsedAt { get; private set; }
    public decimal QuantityUsed { get; private set; }
    public string Purpose { get; private set; } = string.Empty;
    public long RecordedByUserId { get; private set; }
    public string? Remarks { get; private set; }

    public MilkBatch Batch { get; private set; } = null!;
}
