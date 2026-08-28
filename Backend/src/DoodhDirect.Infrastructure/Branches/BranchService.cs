using System.Data;
using System.Text.Json;
using DoodhDirect.Application.Abstractions;
using DoodhDirect.Application.Branches;
using DoodhDirect.Application.Common;
using DoodhDirect.Application.Setup;
using DoodhDirect.Domain.Auditing;
using DoodhDirect.Domain.Catalogue;
using DoodhDirect.Infrastructure.Persistence;
using DoodhDirect.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace DoodhDirect.Infrastructure.Branches;

/// <summary>
/// Administrative branch management. Creation allocates the branch number from the
/// centralized <c>BRANCH</c> numbering series inside a serializable transaction and
/// guarantees the branch-scoped ORDER series exists the moment the branch does.
/// Codes are immutable once a branch is referenced by orders, product availability,
/// or a scoped ORDER numbering series.
/// </summary>
public sealed class BranchService(
    DoodhDirectDbContext dbContext,
    INumberSeriesService numberSeriesService,
    NumberSeriesSeedService numberSeriesSeedService,
    IIndiaTimeProvider timeProvider) : IBranchService
{
    public const string ActionCreated = "BRANCH.CREATED";
    public const string ActionUpdated = "BRANCH.UPDATED";
    public const string ActionActivated = "BRANCH.ACTIVATED";
    public const string ActionDeactivated = "BRANCH.DEACTIVATED";

    public async Task<IReadOnlyList<BranchResult>> ListAsync(CancellationToken cancellationToken) =>
        (await dbContext.Branches.AsNoTracking()
            .OrderBy(branch => branch.Name)
            .ThenBy(branch => branch.Code)
            .ToListAsync(cancellationToken))
        .Select(branch => branch.ToBranchResult())
        .ToArray();

    public async Task<BranchResult> GetAsync(Guid branchId, CancellationToken cancellationToken) =>
        (await FindBranchAsync(branchId, cancellationToken)).ToBranchResult();

    public async Task<BranchResult> CreateAsync(
        long actorUserId,
        UpsertBranchRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var normalizedCode = NormalizeCode(request.Code);

        BranchResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            if (await dbContext.Branches.AnyAsync(branch => branch.Code == normalizedCode, cancellationToken))
            {
                throw new ConflictException("The branch code is already in use.");
            }

            var branch = Mutate(() => new Branch(
                normalizedCode,
                request.Name,
                request.City,
                request.State,
                request.Latitude,
                request.Longitude));
            branch.Update(
                request.Name,
                request.AddressLine1,
                request.AddressLine2,
                request.Locality,
                request.City,
                request.State,
                request.PinCode,
                request.Latitude,
                request.Longitude,
                request.ServiceRadiusKm);
            branch.AssignBranchNumber(
                await numberSeriesService.GetNextNumberAsync("BRANCH", actorUserId, cancellationToken));

            dbContext.Branches.Add(branch);
            await dbContext.SaveChangesAsync(cancellationToken);

            // Guarantee the branch-scoped ORDER series exists the moment the branch
            // does, so order creation for this branch always has a series to consume.
            await numberSeriesSeedService.EnsureScopedOrderSeriesAsync(branch.Code, cancellationToken);

            AddAudit(actorUserId, ActionCreated, branch, null, BranchSnapshot(branch), null);
            await dbContext.SaveChangesAsync(cancellationToken);

            result = branch.ToBranchResult();
        }, cancellationToken);

        return result!;
    }

    public async Task<BranchResult> UpdateAsync(
        long actorUserId,
        Guid branchId,
        UpsertBranchRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        var normalizedCode = NormalizeCode(request.Code);

        BranchResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var branch = await FindBranchAsync(branchId, cancellationToken);

            if (await dbContext.Branches.AnyAsync(
                    item => item.Code == normalizedCode && item.Id != branch.Id,
                    cancellationToken))
            {
                throw new ConflictException("The branch code is already in use.");
            }

            if (branch.Code != normalizedCode)
            {
                await EnsureCodeChangeAllowedAsync(branch, cancellationToken);
            }

            var snapshot = BranchSnapshot(branch);
            if (branch.Code != normalizedCode)
            {
                branch.ChangeCode(normalizedCode);
            }
            branch.Update(
                request.Name,
                request.AddressLine1,
                request.AddressLine2,
                request.Locality,
                request.City,
                request.State,
                request.PinCode,
                request.Latitude,
                request.Longitude,
                request.ServiceRadiusKm);

            await dbContext.SaveChangesAsync(cancellationToken);
            AddAudit(actorUserId, ActionUpdated, branch, snapshot, BranchSnapshot(branch), null);
            await dbContext.SaveChangesAsync(cancellationToken);

            result = branch.ToBranchResult();
        }, cancellationToken);

        return result!;
    }

    public async Task<BranchResult> SetActiveAsync(
        long actorUserId,
        Guid branchId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        BranchResult? result = null;
        await ExecuteSerializableAsync(async () =>
        {
            var branch = await FindBranchAsync(branchId, cancellationToken);
            if (isActive && !branch.IsActive)
            {
                branch.Activate();
                AddAudit(actorUserId, ActionActivated, branch, null, BranchSnapshot(branch), null);
            }
            else if (!isActive && branch.IsActive)
            {
                branch.Deactivate();
                AddAudit(actorUserId, ActionDeactivated, branch, null, BranchSnapshot(branch), null);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            result = branch.ToBranchResult();
        }, cancellationToken);

        return result!;
    }

    private async Task<Branch> FindBranchAsync(Guid branchId, CancellationToken cancellationToken) =>
        await dbContext.Branches.SingleOrDefaultAsync(
            branch => branch.PublicId == branchId,
            cancellationToken) ?? throw new NotFoundException("The branch was not found.");

    private async Task EnsureCodeChangeAllowedAsync(Branch branch, CancellationToken cancellationToken)
    {
        if (await dbContext.Orders.AnyAsync(order => order.BranchId == branch.Id, cancellationToken))
        {
            throw new BusinessRuleException(
                $"The branch code cannot be changed because orders already reference branch '{branch.Code}'.");
        }

        if (await dbContext.ProductBranches.AnyAsync(
                item => item.BranchId == branch.Id,
                cancellationToken))
        {
            throw new BusinessRuleException(
                $"The branch code cannot be changed because product availability already references branch '{branch.Code}'.");
        }

        if (await dbContext.NumberSeries.AnyAsync(
                item => item.Code == "ORDER" && item.ScopeKey == branch.Code,
                cancellationToken))
        {
            throw new BusinessRuleException(
                $"The branch code cannot be changed because an order numbering series already exists for '{branch.Code}'.");
        }
    }

    private static void Validate(UpsertBranchRequest request)
    {
        ValidateRequired(request.Code, "Code", 20);
        ValidateRequired(request.Name, "Name", 160);
        ValidateRequired(request.City, "City", 80);
        ValidateRequired(request.State, "State", 80);

        ValidateOptional(request.AddressLine1, "Address line 1", 200);
        ValidateOptional(request.AddressLine2, "Address line 2", 200);
        ValidateOptional(request.Locality, "Locality", 120);
        ValidateOptional(request.PinCode, "PIN code", 12);

        if (request.Latitude is < -90m or > 90m)
        {
            throw new ValidationAppException("Latitude must be between -90 and 90.", "latitude");
        }

        if (request.Longitude is < -180m or > 180m)
        {
            throw new ValidationAppException("Longitude must be between -180 and 180.", "longitude");
        }

        if (request.ServiceRadiusKm is <= 0m)
        {
            throw new ValidationAppException("Service radius must be greater than zero.", "serviceRadiusKm");
        }
    }

    private static void ValidateRequired(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationAppException($"{field} is required.", field);
        }
        if (value.Trim().Length > maximumLength)
        {
            throw new ValidationAppException($"{field} cannot exceed {maximumLength} characters.", field);
        }
    }

    private static void ValidateOptional(string? value, string field, int maximumLength)
    {
        if (value is not null && value.Trim().Length > maximumLength)
        {
            throw new ValidationAppException($"{field} cannot exceed {maximumLength} characters.", field);
        }
    }

    private static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Trim().ToUpperInvariant();

    private void AddAudit(long userId, string action, Branch branch, object? oldValue, object newValue, string? reason) =>
        dbContext.AddAuditLog(new AuditLog(
            userId,
            action,
            "Branch",
            branch.PublicId.ToString(),
            oldValue is null ? null : JsonSerializer.Serialize(oldValue),
            JsonSerializer.Serialize(newValue),
            null,
            null,
            reason,
            timeProvider.Now));

    private static object BranchSnapshot(Branch branch) => new
    {
        branch.Code,
        branch.Name,
        branch.AddressLine1,
        branch.AddressLine2,
        branch.Locality,
        branch.City,
        branch.State,
        branch.PinCode,
        branch.Latitude,
        branch.Longitude,
        branch.ServiceRadiusKm,
        branch.BranchNumber,
        branch.IsActive
    };

    private async Task ExecuteSerializableAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation();
            return;
        }

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await operation();
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private static T Mutate<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (ArgumentException exception)
        {
            throw new ValidationAppException(exception.Message, exception.ParamName);
        }
    }
}
