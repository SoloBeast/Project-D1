using System.Data;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Dairy;
using DoodhDirect.Domain.Dairy;
using DoodhDirect.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Dairy;

public sealed class DairyService(
    DoodhDirectDbContext dbContext,
    IIndiaTimeProvider timeProvider) : IDairyService
{
    private const string MilkUnit = "L";

    public async Task<DairyDashboardResult> GetDashboardAsync(
        DairyActor actor,
        long branchId,
        DateOnly? productionDate,
        CancellationToken cancellationToken)
    {
        await RequireBranchAsync(actor, branchId, cancellationToken);
        var date = productionDate ?? timeProvider.Today;
        var (from, to) = DateRange(date, date);

        var entries = await dbContext.MilkProductions
            .AsNoTracking()
            .Where(x => x.BranchId == branchId && x.ProductionAt >= from && x.ProductionAt < to)
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
            timeProvider.Now);
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
                request.ProductionAt,
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
                CreateBatchNumber(request.ProductionAt, production.PublicId),
                request.ProductionAt,
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
                batch.ProductionAt,
                batch.QuantityProduced,
                batch.QuantityProduced,
                batch.Unit,
                batch.Status,
                batch.CreatedAt));
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
        query = ApplyDateRange(query, x => x.ProductionAt, fromDate, toDate);

        var productions = await query
            .OrderByDescending(x => x.ProductionAt)
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
            .OrderByDescending(x => x.ProductionAt)
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

            if (request.UsedAt < batch.ProductionAt)
                throw new ValidationAppException("Usage time cannot precede production time.", nameof(request.UsedAt));

            var available = batch.QuantityProduced - batch.Usages.Sum(x => x.QuantityUsed);
            if (request.QuantityUsed > available)
                throw new BusinessRuleException($"Only {available:0.###} L is available in this batch.");

            var usage = new MilkUsage(
                batch.BranchId,
                batch.Id,
                request.UsedAt,
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
        query = ApplyDateRange(query, x => x.UsedAt, fromDate, toDate);

        var usages = await query
            .OrderByDescending(x => x.UsedAt)
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
            timeProvider.Now);
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
        ValidateIndiaLocalTimestamp(request.ProductionAt, nameof(request.ProductionAt));
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
        ValidateIndiaLocalTimestamp(request.UsedAt, nameof(request.UsedAt));
        ValidateQuantity(request.QuantityUsed, nameof(request.QuantityUsed));
        if (string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Trim().Length > 120)
            throw new ValidationAppException("Purpose is required and cannot exceed 120 characters.", nameof(request.Purpose));
        ValidateLength(request.Remarks, nameof(request.Remarks), 1000);
    }

    private void ValidateIndiaLocalTimestamp(DateTime value, string field)
    {
        if (value.Kind != DateTimeKind.Unspecified)
            throw new ValidationAppException("Timestamp must be India-local without a timezone offset.", field);
        if (value > timeProvider.Now.AddMinutes(5))
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

    private IQueryable<T> ApplyDateRange<T>(
        IQueryable<T> query,
        System.Linq.Expressions.Expression<Func<T, DateTime>> selector,
        DateOnly? fromDate,
        DateOnly? toDate)
    {
        if (fromDate.HasValue)
        {
            var from = fromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            query = query.Where(BuildComparison(selector, from, greaterThanOrEqual: true));
        }
        if (toDate.HasValue)
        {
            var to = toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            query = query.Where(BuildComparison(selector, to, greaterThanOrEqual: false));
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

    private static (DateTime From, DateTime To) DateRange(DateOnly fromDate, DateOnly toDate) =>
        (fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
            toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified));

    private static string CreateBatchNumber(DateTime productionAt, Guid productionPublicId) =>
        $"MB-{productionAt:yyyyMMdd}-{productionPublicId:N}"[..20].ToUpperInvariant();

    private static MilkProductionResult MapProduction(MilkProduction production, MilkBatchResult batch) => new(
        production.PublicId,
        production.BranchId,
        production.ProductionAt,
        production.Shift,
        production.BuffaloCount,
        production.QuantityProduced,
        production.Unit,
        production.RecordedByUserId,
        production.Remarks,
        production.CreatedAt,
        batch);

    private static MilkBatchResult MapBatch(MilkBatch batch, decimal quantityUsed)
    {
        var available = batch.QuantityProduced - quantityUsed;
        return new MilkBatchResult(
            batch.PublicId,
            batch.BatchNumber,
            batch.BranchId,
            batch.Production.PublicId,
            batch.ProductionAt,
            batch.QuantityProduced,
            available,
            batch.Unit,
            available == 0 ? MilkBatchStatus.Exhausted : MilkBatchStatus.Available,
            batch.CreatedAt);
    }

    private static MilkUsageResult MapUsage(MilkUsage usage, MilkBatch batch) => new(
        usage.PublicId,
        batch.PublicId,
        batch.BatchNumber,
        usage.BranchId,
        usage.UsedAt,
        usage.QuantityUsed,
        batch.Unit,
        usage.Purpose,
        usage.RecordedByUserId,
        usage.Remarks,
        usage.CreatedAt);
}
