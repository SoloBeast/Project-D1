using System.Data;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Dairy;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Dairy;

public sealed class DairyService(DoodhDirectDbContext dbContext, IClock clock) : IDairyService
{
    private const string MilkUnit = "L";

    public async Task<DairyDashboardResult> GetDashboardAsync(
        DairyActor actor,
        long branchId,
        DateOnly? productionDate,
        CancellationToken cancellationToken)
    {
        await RequireBranchAsync(actor, branchId, cancellationToken);
        var date = productionDate ?? DateOnly.FromDateTime(clock.UtcNow);
        var (fromUtc, toUtc) = DateRange(date, date);

        var entries = await dbContext.MilkProductions
            .AsNoTracking()
            .Where(x => x.BranchId == branchId && x.ProductionAtUtc >= fromUtc && x.ProductionAtUtc < toUtc)
            .ToListAsync(cancellationToken);
        var availability = await CalculateAvailabilityAsync(branchId, cancellationToken);

        return new DairyDashboardResult(
            branchId,
            date,
            entries.Sum(x => x.QuantityProduced),
            availability.AvailableQuantity,
            MilkUnit,
            entries.Count,
            availability.AvailableBatchCount,
            clock.UtcNow);
    }

    public async Task<MilkProductionResult> RecordProductionAsync(
        DairyActor actor,
        long branchId,
        RecordMilkProductionRequest request,
        CancellationToken cancellationToken)
    {
        await RequireBranchAsync(actor, branchId, cancellationToken);
        ValidateProduction(request);

        MilkProductionResult? result = null;
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var production = new MilkProduction(
                branchId,
                request.ProductionAtUtc,
                request.BuffaloCount,
                request.QuantityProduced,
                MilkUnit,
                actor.UserId,
                request.Shift,
                request.Remarks);
            dbContext.MilkProductions.Add(production);
            await dbContext.SaveChangesAsync(cancellationToken);

            var batch = new MilkBatch(
                branchId,
                production.Id,
                CreateBatchNumber(request.ProductionAtUtc, production.PublicId),
                request.ProductionAtUtc,
                request.QuantityProduced,
                MilkUnit);
            dbContext.MilkBatches.Add(batch);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            result = MapProduction(production, new MilkBatchResult(
                batch.PublicId,
                batch.BatchNumber,
                batch.BranchId,
                production.PublicId,
                batch.ProductionAtUtc,
                batch.QuantityProduced,
                batch.QuantityProduced,
                batch.Unit,
                batch.Status,
                batch.CreatedAtUtc));
        });

        return result!;
    }

    public async Task<IReadOnlyList<MilkProductionResult>> GetProductionHistoryAsync(
        DairyActor actor,
        long branchId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        await RequireBranchAsync(actor, branchId, cancellationToken);
        ValidateDateRange(fromDate, toDate);

        var query = dbContext.MilkProductions
            .AsNoTracking()
            .Include(x => x.Batches)
                .ThenInclude(x => x.Usages)
            .Where(x => x.BranchId == branchId);
        query = ApplyDateRange(query, x => x.ProductionAtUtc, fromDate, toDate);

        var productions = await query
            .OrderByDescending(x => x.ProductionAtUtc)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return productions.Select(production =>
        {
            var batch = production.Batches.Single();
            return MapProduction(production, MapBatch(batch, batch.Usages.Sum(x => x.QuantityUsed)));
        }).ToArray();
    }

    public async Task<IReadOnlyList<MilkBatchResult>> GetBatchesAsync(
        DairyActor actor,
        long branchId,
        MilkBatchStatus? status,
        CancellationToken cancellationToken)
    {
        await RequireBranchAsync(actor, branchId, cancellationToken);
        var batches = await dbContext.MilkBatches
            .AsNoTracking()
            .Include(x => x.Production)
            .Include(x => x.Usages)
            .Where(x => x.BranchId == branchId && (!status.HasValue || x.Status == status.Value))
            .OrderByDescending(x => x.ProductionAtUtc)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);

        return batches.Select(x => MapBatch(x, x.Usages.Sum(usage => usage.QuantityUsed))).ToArray();
    }

    public async Task<MilkBatchResult> GetBatchAsync(
        DairyActor actor,
        Guid batchPublicId,
        CancellationToken cancellationToken)
    {
        var batch = await dbContext.MilkBatches
            .AsNoTracking()
            .Include(x => x.Production)
            .Include(x => x.Usages)
            .SingleOrDefaultAsync(x => x.PublicId == batchPublicId, cancellationToken)
            ?? throw new NotFoundException("Milk batch was not found.");
        RequireBranch(actor, batch.BranchId);
        return MapBatch(batch, batch.Usages.Sum(x => x.QuantityUsed));
    }

    public async Task<MilkAvailabilityResult> GetAvailabilityAsync(
        DairyActor actor,
        long branchId,
        CancellationToken cancellationToken)
    {
        await RequireBranchAsync(actor, branchId, cancellationToken);
        return await CalculateAvailabilityAsync(branchId, cancellationToken);
    }

    public async Task<MilkUsageResult> RecordUsageAsync(
        DairyActor actor,
        Guid batchPublicId,
        RecordMilkUsageRequest request,
        CancellationToken cancellationToken)
    {
        ValidateUsage(request);
        MilkUsageResult? result = null;
        var strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var batch = await dbContext.MilkBatches
                .Include(x => x.Usages)
                .SingleOrDefaultAsync(x => x.PublicId == batchPublicId, cancellationToken)
                ?? throw new NotFoundException("Milk batch was not found.");
            RequireBranch(actor, batch.BranchId);

            if (request.UsedAtUtc < batch.ProductionAtUtc)
                throw new ValidationAppException("Usage time cannot precede production time.", nameof(request.UsedAtUtc));

            var available = batch.QuantityProduced - batch.Usages.Sum(x => x.QuantityUsed);
            if (request.QuantityUsed > available)
                throw new BusinessRuleException($"Only {available:0.###} L is available in this batch.");

            var usage = new MilkUsage(
                batch.BranchId,
                batch.Id,
                request.UsedAtUtc,
                request.QuantityUsed,
                request.Purpose,
                actor.UserId,
                request.Remarks);
            dbContext.MilkUsages.Add(usage);
            if (request.QuantityUsed == available)
                batch.MarkExhausted();

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            result = MapUsage(usage, batch);
        });

        return result!;
    }

    public async Task<IReadOnlyList<MilkUsageResult>> GetUsageHistoryAsync(
        DairyActor actor,
        long branchId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        await RequireBranchAsync(actor, branchId, cancellationToken);
        ValidateDateRange(fromDate, toDate);

        var query = dbContext.MilkUsages
            .AsNoTracking()
            .Include(x => x.Batch)
            .Where(x => x.BranchId == branchId);
        query = ApplyDateRange(query, x => x.UsedAtUtc, fromDate, toDate);

        var usages = await query
            .OrderByDescending(x => x.UsedAtUtc)
            .ThenByDescending(x => x.Id)
            .ToListAsync(cancellationToken);
        return usages.Select(x => MapUsage(x, x.Batch)).ToArray();
    }

    private async Task<MilkAvailabilityResult> CalculateAvailabilityAsync(
        long branchId,
        CancellationToken cancellationToken)
    {
        var batches = await dbContext.MilkBatches
            .AsNoTracking()
            .Include(x => x.Usages)
            .Where(x => x.BranchId == branchId)
            .ToListAsync(cancellationToken);
        var produced = batches.Sum(x => x.QuantityProduced);
        var used = batches.Sum(x => x.Usages.Sum(usage => usage.QuantityUsed));
        var availableBatchCount = batches.Count(x => x.QuantityProduced > x.Usages.Sum(usage => usage.QuantityUsed));
        return new MilkAvailabilityResult(
            branchId,
            produced,
            used,
            produced - used,
            MilkUnit,
            availableBatchCount,
            clock.UtcNow);
    }

    private async Task RequireBranchAsync(DairyActor actor, long branchId, CancellationToken cancellationToken)
    {
        RequireBranch(actor, branchId);
        var exists = await dbContext.Branches
            .AsNoTracking()
            .AnyAsync(x => x.Id == branchId && x.IsActive, cancellationToken);
        if (!exists)
            throw new NotFoundException("Active branch was not found.");
    }

    private static void RequireBranch(DairyActor actor, long branchId)
    {
        if (!actor.HasGlobalAccess && !actor.BranchIds.Contains(branchId))
            throw new ForbiddenAppException("You are not authorized for this branch.");
    }

    private void ValidateProduction(RecordMilkProductionRequest request)
    {
        ValidateUtcTimestamp(request.ProductionAtUtc, nameof(request.ProductionAtUtc));
        ValidateQuantity(request.QuantityProduced, nameof(request.QuantityProduced));
        if (request.BuffaloCount <= 0)
            throw new ValidationAppException("Buffalo count must be positive.", nameof(request.BuffaloCount));
        if (!string.Equals(request.Unit?.Trim(), MilkUnit, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Unit?.Trim(), "LITRE", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Unit?.Trim(), "LITRES", StringComparison.OrdinalIgnoreCase))
            throw new ValidationAppException("Milk unit must be litres (L).", nameof(request.Unit));
        ValidateLength(request.Shift, nameof(request.Shift), 40);
        ValidateLength(request.Remarks, nameof(request.Remarks), 1000);
    }

    private void ValidateUsage(RecordMilkUsageRequest request)
    {
        ValidateUtcTimestamp(request.UsedAtUtc, nameof(request.UsedAtUtc));
        ValidateQuantity(request.QuantityUsed, nameof(request.QuantityUsed));
        if (string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Trim().Length > 120)
            throw new ValidationAppException("Purpose is required and cannot exceed 120 characters.", nameof(request.Purpose));
        ValidateLength(request.Remarks, nameof(request.Remarks), 1000);
    }

    private void ValidateUtcTimestamp(DateTime value, string field)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ValidationAppException("Timestamp must include the UTC timezone.", field);
        if (value > clock.UtcNow.AddMinutes(5))
            throw new ValidationAppException("Timestamp cannot be in the future.", field);
    }

    private static void ValidateQuantity(decimal value, string field)
    {
        if (value <= 0)
            throw new ValidationAppException("Quantity must be positive.", field);
        if (decimal.Round(value, 3) != value)
            throw new ValidationAppException("Quantity cannot exceed three decimal places.", field);
    }

    private static void ValidateLength(string? value, string field, int maxLength)
    {
        if (value?.Trim().Length > maxLength)
            throw new ValidationAppException($"{field} cannot exceed {maxLength} characters.", field);
    }

    private static void ValidateDateRange(DateOnly? fromDate, DateOnly? toDate)
    {
        if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
            throw new ValidationAppException("From date cannot be after to date.", nameof(fromDate));
    }

    private static IQueryable<T> ApplyDateRange<T>(
        IQueryable<T> query,
        System.Linq.Expressions.Expression<Func<T, DateTime>> selector,
        DateOnly? fromDate,
        DateOnly? toDate)
    {
        if (fromDate.HasValue)
        {
            var fromUtc = fromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(BuildComparison(selector, fromUtc, greaterThanOrEqual: true));
        }
        if (toDate.HasValue)
        {
            var toUtc = toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(BuildComparison(selector, toUtc, greaterThanOrEqual: false));
        }
        return query;
    }

    private static System.Linq.Expressions.Expression<Func<T, bool>> BuildComparison<T>(
        System.Linq.Expressions.Expression<Func<T, DateTime>> selector,
        DateTime value,
        bool greaterThanOrEqual)
    {
        var comparison = greaterThanOrEqual
            ? System.Linq.Expressions.Expression.GreaterThanOrEqual(selector.Body, System.Linq.Expressions.Expression.Constant(value))
            : System.Linq.Expressions.Expression.LessThan(selector.Body, System.Linq.Expressions.Expression.Constant(value));
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(comparison, selector.Parameters);
    }

    private static (DateTime FromUtc, DateTime ToUtc) DateRange(DateOnly fromDate, DateOnly toDate) =>
        (fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private static string CreateBatchNumber(DateTime productionAtUtc, Guid productionPublicId) =>
        $"MB-{productionAtUtc:yyyyMMdd}-{productionPublicId:N}"[..20].ToUpperInvariant();

    private static MilkProductionResult MapProduction(MilkProduction production, MilkBatchResult batch) => new(
        production.PublicId,
        production.BranchId,
        production.ProductionAtUtc,
        production.Shift,
        production.BuffaloCount,
        production.QuantityProduced,
        production.Unit,
        production.RecordedByUserId,
        production.Remarks,
        production.CreatedAtUtc,
        batch);

    private static MilkBatchResult MapBatch(MilkBatch batch, decimal quantityUsed)
    {
        var available = batch.QuantityProduced - quantityUsed;
        return new MilkBatchResult(
            batch.PublicId,
            batch.BatchNumber,
            batch.BranchId,
            batch.Production.PublicId,
            batch.ProductionAtUtc,
            batch.QuantityProduced,
            available,
            batch.Unit,
            available == 0 ? MilkBatchStatus.Exhausted : MilkBatchStatus.Available,
            batch.CreatedAtUtc);
    }

    private static MilkUsageResult MapUsage(MilkUsage usage, MilkBatch batch) => new(
        usage.PublicId,
        batch.PublicId,
        batch.BatchNumber,
        usage.BranchId,
        usage.UsedAtUtc,
        usage.QuantityUsed,
        batch.Unit,
        usage.Purpose,
        usage.RecordedByUserId,
        usage.Remarks,
        usage.CreatedAtUtc);
}
