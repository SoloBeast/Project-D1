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
        DateTime productionAtUtc,
        int buffaloCount,
        decimal quantityProduced,
        string unit,
        long recordedByUserId,
        string? shift,
        string? remarks)
    {
        BranchId = branchId;
        ProductionAtUtc = productionAtUtc;
        BuffaloCount = buffaloCount;
        QuantityProduced = quantityProduced;
        Unit = unit.Trim().ToUpperInvariant();
        RecordedByUserId = recordedByUserId;
        Shift = string.IsNullOrWhiteSpace(shift) ? null : shift.Trim();
        Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
    }

    public long BranchId { get; private set; }
    public DateTime ProductionAtUtc { get; private set; }
    public int BuffaloCount { get; private set; }
    public decimal QuantityProduced { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public long RecordedByUserId { get; private set; }
    public string? Shift { get; private set; }
    public string? Remarks { get; private set; }

    public ICollection<MilkBatch> Batches { get; private set; } = [];
}

public sealed class MilkBatch : AuditableEntity
{
    private MilkBatch() { }

    public MilkBatch(
        long branchId,
        long productionId,
        string batchNumber,
        DateTime productionAtUtc,
        decimal quantityProduced,
        string unit)
    {
        BranchId = branchId;
        ProductionId = productionId;
        BatchNumber = batchNumber.Trim().ToUpperInvariant();
        ProductionAtUtc = productionAtUtc;
        QuantityProduced = quantityProduced;
        Unit = unit.Trim().ToUpperInvariant();
        Status = MilkBatchStatus.Available;
    }

    public long BranchId { get; private set; }
    public long ProductionId { get; private set; }
    public string BatchNumber { get; private set; } = string.Empty;
    public DateTime ProductionAtUtc { get; private set; }
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
        DateTime usedAtUtc,
        decimal quantityUsed,
        string purpose,
        long recordedByUserId,
        string? remarks)
    {
        BranchId = branchId;
        BatchId = batchId;
        UsedAtUtc = usedAtUtc;
        QuantityUsed = quantityUsed;
        Purpose = purpose.Trim();
        RecordedByUserId = recordedByUserId;
        Remarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
    }

    public long BranchId { get; private set; }
    public long BatchId { get; private set; }
    public DateTime UsedAtUtc { get; private set; }
    public decimal QuantityUsed { get; private set; }
    public string Purpose { get; private set; } = string.Empty;
    public long RecordedByUserId { get; private set; }
    public string? Remarks { get; private set; }

    public MilkBatch Batch { get; private set; } = null!;
}
