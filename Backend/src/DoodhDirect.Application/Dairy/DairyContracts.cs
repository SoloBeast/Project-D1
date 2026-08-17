using DoodhDirect.Domain.Dairy;

namespace DoodhDirect.Application.Dairy;

public sealed record DairyActor(
    long UserId,
    IReadOnlyCollection<long> BranchIds,
    bool HasGlobalAccess = false);

public sealed record RecordMilkProductionRequest(
    DateTime ProductionAtUtc,
    string? Shift,
    int BuffaloCount,
    decimal QuantityProduced,
    string Unit,
    string? Remarks);

public sealed record RecordMilkUsageRequest(
    DateTime UsedAtUtc,
    decimal QuantityUsed,
    string Purpose,
    string? Remarks);

public sealed record MilkProductionResult(
    Guid PublicId,
    long BranchId,
    DateTime ProductionAtUtc,
    string? Shift,
    int BuffaloCount,
    decimal QuantityProduced,
    string Unit,
    long RecordedByUserId,
    string? Remarks,
    DateTime CreatedAtUtc,
    MilkBatchResult Batch);

public sealed record MilkBatchResult(
    Guid PublicId,
    string BatchNumber,
    long BranchId,
    Guid ProductionPublicId,
    DateTime ProductionAtUtc,
    decimal QuantityProduced,
    decimal AvailableQuantity,
    string Unit,
    MilkBatchStatus Status,
    DateTime CreatedAtUtc);

public sealed record MilkUsageResult(
    Guid PublicId,
    Guid BatchPublicId,
    string BatchNumber,
    long BranchId,
    DateTime UsedAtUtc,
    decimal QuantityUsed,
    string Unit,
    string Purpose,
    long RecordedByUserId,
    string? Remarks,
    DateTime CreatedAtUtc);

public sealed record MilkAvailabilityResult(
    long BranchId,
    decimal QuantityProduced,
    decimal QuantityUsed,
    decimal AvailableQuantity,
    string Unit,
    int AvailableBatchCount,
    DateTime CalculatedAtUtc);

public sealed record DairyDashboardResult(
    long BranchId,
    DateOnly ProductionDate,
    decimal QuantityProduced,
    decimal AvailableQuantity,
    string Unit,
    int ProductionEntryCount,
    int AvailableBatchCount,
    DateTime CalculatedAtUtc);

public interface IDairyService
{
    Task<DairyDashboardResult> GetDashboardAsync(
        DairyActor actor,
        long branchId,
        DateOnly? productionDate,
        CancellationToken cancellationToken);

    Task<MilkProductionResult> RecordProductionAsync(
        DairyActor actor,
        long branchId,
        RecordMilkProductionRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MilkProductionResult>> GetProductionHistoryAsync(
        DairyActor actor,
        long branchId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MilkBatchResult>> GetBatchesAsync(
        DairyActor actor,
        long branchId,
        MilkBatchStatus? status,
        CancellationToken cancellationToken);

    Task<MilkBatchResult> GetBatchAsync(
        DairyActor actor,
        Guid batchPublicId,
        CancellationToken cancellationToken);

    Task<MilkAvailabilityResult> GetAvailabilityAsync(
        DairyActor actor,
        long branchId,
        CancellationToken cancellationToken);

    Task<MilkUsageResult> RecordUsageAsync(
        DairyActor actor,
        Guid batchPublicId,
        RecordMilkUsageRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MilkUsageResult>> GetUsageHistoryAsync(
        DairyActor actor,
        long branchId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken);
}
